using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;

namespace VisionFlow.Tools.Histogram;

/// <summary>
/// Gamma Correction: biến đổi sáng theo đường cong phi tuyến output=(input/255)^Gamma*255.
/// QUY ƯỚC QUAN TRỌNG (khớp tài liệu, NGƯỢC với "gamma encoding" sách giáo khoa thông thường):
/// Gamma NHỎ (0.4-0.6) -> ảnh SÁNG hơn (kéo mạnh vùng tối lên). Gamma LỚN (1.2-3.0) -> ảnh TỐI hơn.
/// "Quy tắc nhớ: Gamma càng nhỏ, ảnh càng sáng."
/// </summary>
[ToolMetadata("GammaCorrection", DisplayName = "Gamma Correction", Category = "Histogram",
    Description = "Non-linear brightness adjustment via gamma curve - preserves highlight/shadow detail selectively")]
public sealed class GammaCorrectionTool : VisionTool
{
    private readonly InputPort<IVisionImage> _input;
    private readonly OutputPort<IVisionImage> _output;
    private readonly ToolParameter<double> _gamma; // Tab Enhancement: 0.1..3.0, mặc định 1.0 = giữ nguyên

    public GammaCorrectionTool()
    {
        _input = AddInput<IVisionImage>("Image");
        _output = AddOutput<IVisionImage>("Image");
        _gamma = AddParameter("Gamma", 1.0, "Gamma", 0.1, 3.0, category: "Enhancement", order: 1);
    }

    protected override void OnExecute(IToolContext context)
    {
        Mat src = _input.Value!.AsMat();

        // ----- Bước 1: Dựng bảng tra cứu (LUT) 256 phần tử -----
        var lutData = new byte[256];
        double gamma = _gamma.Value;
        for (int i = 0; i < 256; i++)
        {
            double normalized = i / 255.0; // Chuẩn hóa pixel về khoảng [0,1] trước khi lũy thừa
            double corrected = System.Math.Pow(normalized, gamma) * 255.0; // Công thức cốt lõi
            lutData[i] = (byte)System.Math.Clamp(corrected, 0, 255);
        }

        // SỬA LỖI: Dùng Mat.FromPixelData thay vì gọi constructor trực tiếp gây lỗi truy cập (CS0122)
        using Mat lut = Mat.FromPixelData(1, 256, MatType.CV_8UC1, lutData);

        // ----- Bước 2: Áp dụng LUT lên toàn ảnh -----
        Mat dst = new Mat();
        Cv2.LUT(src, lut, dst);

        _output.Value = new MatVisionImage(dst);
        context.Log($"GammaCorrection: Gamma={gamma:F2}");
    }
}