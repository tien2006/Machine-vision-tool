// ==================== Vai trò chính:                Phát hiện cạnh bằng đạo hàm Sobel - xuất riêng biệt GradientX, GradientY và Magnitude tổng hợp
// ==================== Thành phần / Class tiêu biểu: SobelFilterTool
// ==================== Phụ thuộc vào:                OpenCvSharp (Cv2.Sobel, ConvertScaleAbs, AddWeighted)
// ==================== Pattern / Kỹ thuật nổi bật:   Luôn tính CẢ 2 hướng Gx/Gy rồi xuất riêng từng cổng,
//                       tham số Direction chỉ quyết định cổng ImageMatrix (pass-through) phản ánh kết quả nào,
//                       KHÔNG giới hạn việc tính toán - downstream vẫn luôn có đủ GradientX/GradientY/Magnitude để dùng

using System;
using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;

namespace VisionFlow.Tools.Preprocess; // Cùng thư mục với LaplacianFilterTool / CannyEdgeDetectionTool

/// <summary>Hướng gradient chính - quyết định cổng ImageMatrix (pass-through) phản ánh kết quả nào; GradientX/GradientY/Magnitude luôn được tính đủ cả 3 bất kể lựa chọn này.</summary>
public enum SobelDirection
{
    X,    // Cạnh dọc (biến thiên độ sáng theo chiều ngang) - ImageMatrix sẽ = GradientX
    Y,    // Cạnh ngang (biến thiên độ sáng theo chiều dọc) - ImageMatrix sẽ = GradientY
    Both  // Mọi hướng - ImageMatrix sẽ = Magnitude (kết hợp Gx và Gy)
}

/// <summary>
/// SobelFilter: tính đạo hàm ảnh theo 2 trục X/Y bằng kernel Sobel, dùng để trích xuất biên dạng vật thể -
/// ứng dụng phổ biến: dẫn đường tọa độ mép sản phẩm cho PLC (cắt/dán/gắp), loại bỏ ảnh hưởng màu sắc nhẹ
/// để chỉ tập trung vào hình dáng vật lý. Luôn xuất đủ 4 cổng: ImageMatrix (pass-through theo Direction),
/// GradientX, GradientY và Magnitude (khung xương biên dạng đầy đủ - kết quả quan trọng nhất).
/// </summary>
[ToolMetadata(
    "SobelFilter",
    DisplayName = "Sobel Filter",
    Category = "Preprocessing",
    Description = "Sobel edge detection - outputs GradientX, GradientY and combined Magnitude separately")]
public sealed class SobelFilterTool : VisionTool
{
    #region 1. Khai báo Port (In/Out)
    private readonly InputPort<IVisionImage> _imageMatrix;

    private readonly OutputPort<IVisionImage> _outImageMatrix; // Pass-through theo Direction (xem enum SobelDirection)
    private readonly OutputPort<IVisionImage> _outGradientX;
    private readonly OutputPort<IVisionImage> _outGradientY;
    private readonly OutputPort<IVisionImage> _outMagnitude;   // Kết quả quan trọng nhất - khung xương biên dạng đầy đủ
    #endregion

    #region 2. Khai báo Parameter
    private readonly ToolParameter<bool> _convertToGray;   // True: Sobel chạy trên ảnh xám (nhanh hơn, cạnh ổn định hơn) - chỉ tắt khi màu sắc là yếu tố quan trọng
    private readonly ToolParameter<double> _delta;          // Cộng thêm vào kết quả - tăng lên khi ảnh output quá tối
    private readonly ToolParameter<SobelDirection> _direction;
    private readonly ToolParameter<int> _kernelSize;         // 3, 5 hoặc 7 - nhỏ giữ chi tiết sắc nét, lớn giảm nhiễu nhưng cạnh dày hơn
    private readonly ToolParameter<double> _scale;           // Hệ số nhân cường độ - tăng khi muốn cạnh sáng/rõ hơn để quan sát/đo kiểm
    #endregion

    public SobelFilterTool()
    {
        _imageMatrix = AddInput<IVisionImage>("ImageMatrix", "Image Matrix");

        _outImageMatrix = AddOutput<IVisionImage>("ImageMatrix", "Image Matrix");
        _outGradientX = AddOutput<IVisionImage>("GradientX", "Gradient X");
        _outGradientY = AddOutput<IVisionImage>("GradientY", "Gradient Y");
        _outMagnitude = AddOutput<IVisionImage>("Magnitude", "Magnitude");

        _convertToGray = AddParameter("ConvertToGray", true, "Convert To Gray", category: "Filter", order: 1);
        _delta = AddParameter("Delta", 0.0, "Delta", min: 0.0, max: 255.0, category: "Filter", order: 2);
        _direction = AddParameter("Direction", SobelDirection.Both, "Direction", category: "Filter", order: 3);
        _kernelSize = AddParameter("KernelSize", 3, "Kernel Size", min: 1, max: 7, category: "Filter", order: 4);
        _scale = AddParameter("Scale", 1.0, "Scale", min: 0.1, max: 10.0, category: "Filter", order: 5);
    }

    protected override void OnExecute(IToolContext context)
    {
        Mat original = _imageMatrix.Value!.AsMat();

        // ----- Bước 1: ConvertToGray - Sobel chủ yếu quan tâm biến thiên độ sáng, không cần đủ 3 kênh màu -----
        Mat src = original;
        bool srcIsTemp = false;
        if (_convertToGray.Value && original.Channels() > 1)
        {
            src = new Mat();
            Cv2.CvtColor(original, src, original.Channels() == 4 ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY);
            srcIsTemp = true;
        }

        // ----- Bước 2: ép KernelSize về 1, 3, 5 hoặc 7 (giá trị hợp lệ duy nhất OpenCV Sobel chấp nhận) -----
        int k = _kernelSize.Value switch
        {
            <= 1 => 1,
            <= 3 => 3,
            <= 5 => 5,
            _ => 7
        };

        // ----- Bước 3: luôn tính CẢ 2 hướng bằng độ sâu trung gian CV_16S để giữ giá trị âm (gradient đổi chiều) -----
        using Mat gx16 = new Mat();
        using Mat gy16 = new Mat();
        Cv2.Sobel(src, gx16, MatType.CV_16S, 1, 0, k, _scale.Value, _delta.Value, BorderTypes.Reflect101);
        Cv2.Sobel(src, gy16, MatType.CV_16S, 0, 1, k, _scale.Value, _delta.Value, BorderTypes.Reflect101);

        Mat gradientX = new Mat();
        Mat gradientY = new Mat();
        Cv2.ConvertScaleAbs(gx16, gradientX); // Lấy trị tuyệt đối + ép về 8-bit để xem/threshold được ở node sau
        Cv2.ConvertScaleAbs(gy16, gradientY);

        Mat magnitude = new Mat();
        // Xấp xỉ độ lớn gradient bằng trung bình trọng số |Gx|+|Gy| - nhanh hơn công thức chính xác sqrt(Gx^2+Gy^2)
        // mà vẫn đủ tốt cho mục đích inspection, giữ đúng thang giá trị 0-255 như GradientX/GradientY
        Cv2.AddWeighted(gradientX, 0.5, gradientY, 0.5, 0, magnitude);

        // ----- Bước 4: ImageMatrix pass-through theo Direction (xem giải thích trong doc-comment class) -----
        Mat passThrough = _direction.Value switch
        {
            SobelDirection.X => gradientX.Clone(),
            SobelDirection.Y => gradientY.Clone(),
            _ => magnitude.Clone()
        };

        _outImageMatrix.Value = new MatVisionImage(passThrough);
        _outGradientX.Value = new MatVisionImage(gradientX);
        _outGradientY.Value = new MatVisionImage(gradientY);
        _outMagnitude.Value = new MatVisionImage(magnitude);

        if (srcIsTemp) src.Dispose();

        context.Log($"SobelFilter: direction={_direction.Value}, kernel={k}x{k}, scale={_scale.Value}, delta={_delta.Value}, convertToGray={_convertToGray.Value}");
    }
}