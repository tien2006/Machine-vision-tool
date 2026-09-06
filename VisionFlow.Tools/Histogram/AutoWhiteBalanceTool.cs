// ==================== Vai trò chính:                Tự động cân bằng trắng bằng giả định Gray World, loại bỏ ám màu do ánh sáng môi trường
// ==================== Thành phần / Class tiêu biểu: AutoWhiteBalanceTool
// ==================== Phụ thuộc vào:                OpenCvSharp
// ==================== Pattern / Kỹ thuật nổi bật:   Gray World Assumption - trung bình R/G/B toàn ảnh phải xám trung tính, kênh nào lệch thì scale lại

using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;

namespace VisionFlow.Tools.Histogram;

/// <summary>
/// Cân bằng trắng tự động theo giả định Gray World: trung bình toàn ảnh (đủ nhiều pixel, nhiều màu)
/// PHẢI là màu xám trung tính (R=G=B). Nếu 1 kênh cao bất thường (VD đèn vàng làm kênh R,G cao hơn B)
/// -> tool tự scale giảm kênh đó để đưa trung bình về cân bằng. Ảnh grayscale -> pass-through không sửa.
/// LƯU Ý: giả định này SAI khi ảnh có 1 màu chủ đạo chiếm gần hết khung hình (VD quả táo đỏ chiếm 90%).
/// </summary>
[ToolMetadata("AutoWhiteBalance", DisplayName = "Auto White Balance", Category = "Histogram",
    Description = "Automatic white balance correction using the Gray World assumption")]
public sealed class AutoWhiteBalanceTool : VisionTool
{
    private readonly InputPort<IVisionImage> _input;
    private readonly OutputPort<IVisionImage> _output;
    // Không có Parameter nào - đúng tài liệu "hoàn toàn tự động dựa vào dữ liệu ảnh"

    public AutoWhiteBalanceTool()
    {
        _input = AddInput<IVisionImage>("Image");
        _output = AddOutput<IVisionImage>("Image");
    }

    protected override void OnExecute(IToolContext context)
    {
        Mat src = _input.Value!.AsMat();

        // ----- Ảnh grayscale: không có khái niệm cân bằng trắng -> pass-through nguyên vẹn -----
        if (src.Channels() == 1)
        {
            _output.Value = new MatVisionImage(src.Clone());
            context.Log("AutoWhiteBalance: grayscale image -> pass-through (no white balance concept).");
            return;
        }

        // ----- Bước 1: Tính giá trị trung bình từng kênh B, G, R -----
        Mat[] channels = Cv2.Split(src);
        double meanB = Cv2.Mean(channels[0]).Val0;
        double meanG = Cv2.Mean(channels[1]).Val0;
        double meanR = Cv2.Mean(channels[2]).Val0;

        // ----- Bước 2: Tính mức xám trung tính mục tiêu = trung bình của cả 3 kênh -----
        double avgGray = (meanB + meanG + meanR) / 3.0;

        // ----- Bước 3: Tính hệ số scale cho từng kênh để đưa mean của nó về đúng avgGray -----
        // Ví dụ: nếu meanB=100 nhưng avgGray=120 -> kB=1.2 -> nhân kênh Blue lên 1.2 lần để "kéo" mean Blue lên bằng avgGray
        double kB = meanB > 1e-6 ? avgGray / meanB : 1.0;
        double kG = meanG > 1e-6 ? avgGray / meanG : 1.0;
        double kR = meanR > 1e-6 ? avgGray / meanR : 1.0;

        // ----- Bước 4: Áp dụng hệ số scale lên từng kênh, OpenCV tự clip về [0,255] -----
        channels[0].ConvertTo(channels[0], -1, kB, 0);
        channels[1].ConvertTo(channels[1], -1, kG, 0);
        channels[2].ConvertTo(channels[2], -1, kR, 0);

        Mat dst = new Mat();
        Cv2.Merge(channels, dst);
        foreach (var ch in channels) ch.Dispose();

        _output.Value = new MatVisionImage(dst);
        context.Log($"AutoWhiteBalance: kB={kB:F2}, kG={kG:F2}, kR={kR:F2} (meanB={meanB:F1}, meanG={meanG:F1}, meanR={meanR:F1})");
    }
}