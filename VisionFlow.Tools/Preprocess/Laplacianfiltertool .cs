// ==================== Vai trò chính:                Phát hiện cạnh qua đạo hàm bậc 2 - nhạy với mọi hướng cùng lúc, dùng cho kiểm tra lấy nét và phát hiện vết nứt li ti
// ==================== Thành phần / Class tiêu biểu: LaplacianFilterTool
// ==================== Phụ thuộc vào:                OpenCvSharp (Cv2.Laplacian, ConvertScaleAbs)
// ==================== Pattern / Kỹ thuật nổi bật:   Đạo hàm bậc 2 (∇²I = ∂²I/∂x² + ∂²I/∂y²) - phản ứng mạnh
//                       tại điểm ảnh có tốc độ thay đổi độ sáng đột ngột; vùng phẳng (độ sáng không đổi) sẽ thành màu đen hoàn toàn

using System;
using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;

namespace VisionFlow.Tools.Preprocess; // Cùng thư mục với SobelFilterTool / CannyEdgeDetectionTool

/// <summary>
/// Laplacian Filter: tính tổng đạo hàm bậc 2 theo cả 2 trục, phản ứng với cạnh theo MỌI HƯỚNG cùng lúc
/// trong 1 lần lọc (không cần tính riêng X/Y rồi kết hợp như Sobel). Trong hệ thống kiểm tra ngoại quan,
/// dùng cho 3 mục đích chính: (1) Kiểm tra lấy nét (Auto-focus) - lấy giá trị trung bình sau Laplacian,
/// càng cao ảnh càng sắc nét; (2) Phát hiện vết nứt li ti - cực nhạy nên bắt được cả khuyết điểm siêu nhỏ
/// mà bộ lọc thường bỏ qua; (3) Làm sắc nét ảnh (ảnh gốc trừ đi Laplacian).
/// CẢNH BÁO: chính vì rất nhạy, Laplacian cũng rất nhạy NHIỄU - gần như luôn cần lọc nhiễu (Smooth/Bilateral)
/// ở node TRƯỚC nếu không muốn kết quả lẫn đầy hạt nhiễu li ti khó phân biệt với cạnh thật.
/// </summary>
[ToolMetadata(
    "LaplacianFilter",
    DisplayName = "Laplacian Filter",
    Category = "Preprocessing",
    Description = "Second-derivative edge detector; used for auto-focus check, micro-crack detection and sharpening")]
public sealed class LaplacianFilterTool : VisionTool
{
    #region 1. Khai báo Port (In/Out)
    private readonly InputPort<IVisionImage> _imageMatrix;   // Nhận ảnh gốc
    private readonly OutputPort<IVisionImage> _outImageMatrix; // Ma trận ảnh chứa đường biên/chi tiết tần số cao - vùng phẳng thành đen hoàn toàn
    #endregion

    #region 2. Khai báo Parameter
    // --- Tab Processing ---
    private readonly ToolParameter<bool> _convertToGray; // Bật: chuyển ảnh màu sang xám trước khi lọc - nhanh hơn, ổn định hơn, giảm nhiễu màu. Tắt: giữ màu, Laplacian xử lý trực tiếp trên từng kênh

    // --- Tab Filter ---
    private readonly ToolParameter<double> _delta;      // Cộng thêm vào kết quả sau lọc - Delta=0 giữ nguyên output, tăng lên khi ảnh kết quả quá tối
    private readonly ToolParameter<int> _kernelSize;    // Kích thước kernel (VD 3 = kernel 3x3) - lớn hơn tác động vùng rộng hơn, cạnh mạnh hơn nhưng cũng nhạy nhiễu hơn
    private readonly ToolParameter<double> _scale;      // Hệ số nhân kết quả trước khi xuất - tăng làm cạnh rõ hơn nhưng nhiễu cũng dễ nổi bật hơn, giảm cho kết quả mềm hơn
    #endregion

    public LaplacianFilterTool()
    {
        _imageMatrix = AddInput<IVisionImage>("ImageMatrix", "Image Matrix");
        _outImageMatrix = AddOutput<IVisionImage>("ImageMatrix", "Image Matrix");

        _convertToGray = AddParameter("ConvertToGray", true, "Convert To Gray", category: "Processing", order: 1);

        // KernelSize=3: kernel Laplacian chuẩn phổ biến nhất
        _kernelSize = AddParameter("KernelSize", 3, "Kernel Size", min: 1, max: 7, category: "Filter", order: 1);
        _scale = AddParameter("Scale", 1.0, "Scale", min: 0.1, max: 10.0, category: "Filter", order: 2);
        _delta = AddParameter("Delta", 0.0, "Delta", min: 0.0, max: 255.0, category: "Filter", order: 3);
    }

    protected override void OnExecute(IToolContext context)
    {
        Mat original = _imageMatrix.Value!.AsMat();

        // ----- Bước 1: ConvertToGray theo Tab Processing -----
        Mat src = original;
        bool srcIsTemp = false;
        if (_convertToGray.Value && original.Channels() > 1)
        {
            src = new Mat();
            Cv2.CvtColor(original, src, original.Channels() == 4 ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY);
            srcIsTemp = true;
        }
        // Nếu ConvertToGray=false: giữ nguyên ảnh màu, Cv2.Laplacian tự áp dụng lên TỪNG kênh độc lập bên dưới

        // ----- Bước 2: ép KernelSize về 1, 3, 5 hoặc 7 (giá trị hợp lệ mà OpenCV Laplacian chấp nhận) -----
        int k = _kernelSize.Value switch
        {
            <= 1 => 1,
            <= 3 => 3,
            <= 5 => 5,
            _ => 7
        };

        // Dùng độ sâu trung gian CV_16S để giữ được cả giá trị âm (đạo hàm bậc 2 đổi dấu quanh cạnh) -
        // tránh mất một nửa thông tin cạnh nếu tính thẳng ra 8U, tương tự lý do đã dùng ở SobelFilterTool.
        using Mat laplacian = new Mat();
        Cv2.Laplacian(src, laplacian, MatType.CV_16S, k, _scale.Value, _delta.Value, BorderTypes.Reflect101);

        Mat dst = new Mat();
        Cv2.ConvertScaleAbs(laplacian, dst); // Lấy trị tuyệt đối + ép về 8-bit để xem/threshold được ở node sau

        _outImageMatrix.Value = new MatVisionImage(dst);

        if (srcIsTemp) src.Dispose();

        context.Log($"LaplacianFilter: convertToGray={_convertToGray.Value}, kernel={k}x{k}, scale={_scale.Value}, delta={_delta.Value}");
    }
}