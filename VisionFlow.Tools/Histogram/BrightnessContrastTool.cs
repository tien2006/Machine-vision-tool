// ==================== Vai trò chính:                Điều chỉnh độ sáng (cộng) và độ tương phản (nhân) tuyến tính trên toàn ảnh
// ==================== Thành phần / Class tiêu biểu: BrightnessContrastTool
// ==================== Phụ thuộc vào:                OpenCvSharp
// ==================== Pattern / Kỹ thuật nổi bật:   Phép biến đổi tuyến tính pixel_new = pixel*Contrast + Brightness (Mat.ConvertTo)

using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;

namespace VisionFlow.Tools.Histogram;

/// <summary>
/// Tool tiền xử lý cơ bản nhất: chỉnh sáng/tương phản tuyến tính đều cho MỌI pixel cùng 1 công thức.
/// Khác với GammaCorrection (phi tuyến, chỉ "cứu" riêng vùng tối HOẶC sáng), tool này tác động đồng đều.
/// </summary>
[ToolMetadata("BrightnessContrast", DisplayName = "Brightness Contrast", Category = "Histogram",
    Description = "Linear brightness/contrast adjustment applied uniformly to all pixels")]
public sealed class BrightnessContrastTool : VisionTool
{
    private readonly InputPort<IVisionImage> _input;
    private readonly OutputPort<IVisionImage> _output;

    // ----- Tab Enhancement -----
    private readonly ToolParameter<double> _brightness; // Cộng vào mọi pixel: -100..+100
    private readonly ToolParameter<double> _contrast;    // Nhân với mọi pixel: 0.1..3.0

    public BrightnessContrastTool()
    {
        _input = AddInput<IVisionImage>("Image");
        _output = AddOutput<IVisionImage>("Image");

        _brightness = AddParameter("Brightness", 0.0, "Brightness", -100.0, 100.0, category: "Enhancement", order: 1);
        _contrast = AddParameter("Contrast", 1.0, "Contrast", 0.1, 3.0, category: "Enhancement", order: 2);
    }

    protected override void OnExecute(IToolContext context)
    {
        Mat src = _input.Value!.AsMat();
        Mat dst = new Mat();

        // ConvertTo(dst, rtype=-1 nghĩa là giữ nguyên kiểu dữ liệu gốc, alpha=hệ số nhân Contrast, beta=giá trị cộng Brightness)
        // OpenCV tự động CLIP kết quả về khoảng hợp lệ [0,255] cho kiểu 8-bit -> đúng yêu cầu "giá trị bị giới hạn 0-255" của tài liệu
        src.ConvertTo(dst, -1, _contrast.Value, _brightness.Value);

        _output.Value = new MatVisionImage(dst);
        context.Log($"BrightnessContrast: Brightness={_brightness.Value}, Contrast={_contrast.Value}");
    }
}