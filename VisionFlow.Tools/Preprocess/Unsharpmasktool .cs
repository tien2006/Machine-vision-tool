// ==================== Vai trò chính:                Làm NÉT ảnh (ngược với Smooth/Bilateral) - tăng tương phản cục bộ tại vùng cạnh
// ==================== Thành phần / Class tiêu biểu: UnsharpMaskTool
// ==================== Phụ thuộc vào:                OpenCvSharp (GaussianBlur, AddWeighted, Absdiff, Threshold)
// ==================== Pattern / Kỹ thuật nổi bật:   Unsharp Masking cổ điển: Sharpened = Original + Amount*(Original - Blurred)
//                       Radius quyết định độ rộng vùng ảnh hưởng (= sigma của Gaussian dùng để tạo bản mờ so sánh),
//                       kernel size của Gaussian được TỰ ĐỘNG suy ra từ Radius (công thức chuẩn k = ceil(3*Radius)*2+1)

using System;
using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;

namespace VisionFlow.Tools.Preprocess; // Cùng thư mục với SmoothTool / BilateralFilteringTool

/// <summary>
/// Unsharp Mask: lấy ảnh gốc trừ đi phiên bản đã làm mờ Gaussian ra "lớp chi tiết cao tần" (mask), rồi
/// cộng ngược lớp đó vào ảnh gốc theo hệ số Amount -> vùng cạnh/chi tiết được khuếch đại tương phản, vùng
/// phẳng gần như không đổi. <c>Radius</c> quyết định "chi tiết nhỏ hay chi tiết lớn" được làm nét: Radius
/// nhỏ chỉ bắt fine detail (VD chữ khắc nhỏ), Radius lớn làm nét cả các mảng cạnh lớn nhưng dễ tạo viền
/// quầng (halo) nếu quá cao.
/// CẢNH BÁO: khác Smooth (giảm nhiễu), Unsharp Mask có thể KHUẾCH ĐẠI nhiễu hạt còn sót nếu Amount quá cao
/// hoặc Radius quá lớn - luôn kiểm tra ảnh kết quả trực quan trước khi chốt tham số.
/// </summary>
[ToolMetadata(
    "UnsharpMask",
    DisplayName = "Unsharp Mask",
    Category = "Preprocessing",
    Description = "Classic unsharp masking to sharpen edges/fine details; can amplify noise if overused")]
public sealed class UnsharpMaskTool : VisionTool
{
    #region 1. Khai báo Port (In/Out)
    private readonly InputPort<IVisionImage> _input;    // ImageMatrix - ảnh nguồn (xám hoặc màu, thô hoặc đã tiền xử lý)
    private readonly OutputPort<IVisionImage> _output;  // ImageMatrix - ảnh đã làm nét, cùng kích thước/số kênh với đầu vào
    #endregion

    #region 2. Khai báo Parameter (2 tham số chính theo đúng tài liệu: Amount, Radius)
    private readonly ToolParameter<double> _amount;     // Độ mạnh hiệu ứng sharpen: thấp = hiệu ứng nhẹ tự nhiên, cao = sắc nét nhưng dễ nhiễu/viền giả. Thường dùng 1.0-2.0
    private readonly ToolParameter<double> _radius;     // Độ rộng vùng ảnh hưởng: nhỏ = bắt fine detail, lớn = làm nét cả cạnh lớn nhưng dễ halo nếu quá cao

    // --- Tham số BỔ SUNG (không có trong tài liệu gốc, giữ lại vì hữu ích) ---
    private readonly ToolParameter<double> _threshold;  // Ngưỡng chênh lệch tối thiểu để áp dụng làm nét; 0 = áp dụng toàn ảnh (mặc định)
    #endregion

    public UnsharpMaskTool()
    {
        _input = AddInput<IVisionImage>("ImageMatrix", "Image Matrix");
        _output = AddOutput<IVisionImage>("ImageMatrix", "Image Matrix");

        // Amount=1.5: nằm giữa khoảng khuyến nghị 1.0-2.0 của tài liệu
        _amount = AddParameter("Amount", 1.5, "Amount", min: 0.0, max: 5.0, category: "Filter", order: 1);

        // Radius=1.0: tập trung fine detail theo mặc định, tăng dần khi cần làm nét vùng cạnh lớn hơn
        _radius = AddParameter("Radius", 1.0, "Radius", min: 0.1, max: 20.0, category: "Filter", order: 2);

        // Threshold=0: mặc định làm nét toàn ảnh, tăng lên (VD 5-15) khi ảnh nhiễu hạt nhiều
        _threshold = AddParameter("Threshold", 0.0, "Threshold", min: 0.0, max: 100.0, category: "Filter", order: 3);
    }

    protected override void OnExecute(IToolContext context)
    {
        Mat src = _input.Value!.AsMat();

        // ----- Bước 1: suy ra kernel size Gaussian TỰ ĐỘNG từ Radius (không bắt người dùng nhập riêng KernelSize) -----
        // Công thức chuẩn: kernel phải đủ rộng để chứa trọn vùng ảnh hưởng ~3*sigma mỗi bên
        int k = (int)Math.Ceiling(_radius.Value * 3) * 2 + 1;
        if (k < 1) k = 1;

        // ----- Bước 2: tạo phiên bản mờ làm nền so sánh - Radius chính là sigma của Gaussian -----
        using Mat blurred = new Mat();
        Cv2.GaussianBlur(src, blurred, new Size(k, k), _radius.Value, _radius.Value, BorderTypes.Reflect101);

        // ----- Bước 3: công thức Unsharp Mask cổ điển -----
        // Sharpened = Original*(1+Amount) - Blurred*Amount
        // AddWeighted tự động saturate_cast về đúng kiểu 8-bit (giá trị âm/vượt 255 tự kẹp về 0/255)
        Mat sharpened = new Mat();
        Cv2.AddWeighted(src, 1.0 + _amount.Value, blurred, -_amount.Value, 0, sharpened);

        // ----- Bước 4 (tùy chọn, ngoài tài liệu): áp dụng Threshold để tránh khuếch đại nhiễu ở vùng phẳng -----
        if (_threshold.Value > 0)
        {
            using Mat diff = new Mat();
            Cv2.Absdiff(src, blurred, diff);

            Mat diffGray = diff;
            bool diffGrayIsSeparate = false;
            if (diff.Channels() > 1)
            {
                diffGray = new Mat();
                Cv2.CvtColor(diff, diffGray, ColorConversionCodes.BGR2GRAY);
                diffGrayIsSeparate = true;
            }

            using Mat mask = new Mat();
            Cv2.Threshold(diffGray, mask, _threshold.Value, 255, ThresholdTypes.BinaryInv); // Vùng chênh lệch NHỎ (coi là nhiễu) -> mask=255
            src.CopyTo(sharpened, mask); // Ghi đè lại bằng ảnh gốc tại các vùng đó

            if (diffGrayIsSeparate) diffGray.Dispose();
        }

        _output.Value = new MatVisionImage(sharpened);

        context.Log($"UnsharpMask: amount={_amount.Value}, radius={_radius.Value} (kernel auto={k}x{k}), threshold={_threshold.Value}");
    }
}