// ==================== Vai trò chính:                Cân bằng histogram tự động, trải đều pixel từ 0-255 để tăng tương phản
// ==================== Thành phần / Class tiêu biểu: HistogramEqualizationTool
// ==================== Phụ thuộc vào:                OpenCvSharp
// ==================== Pattern / Kỹ thuật nổi bật:   Cv2.EqualizeHist trên kênh Y của không gian YCrCb để giữ nguyên màu sắc gốc

using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;

namespace VisionFlow.Tools.Histogram;

/// <summary>
/// Cân bằng histogram toàn cục (global) - trải đều phân phối pixel ra hết dải 0-255, không tham số.
/// Chỉ nên dùng khi ảnh low-contrast ĐỀU trên toàn khung hình; nếu ánh sáng lệch cục bộ, dùng CLAHE.
/// </summary>
[ToolMetadata("HistogramEqualization", DisplayName = "Histogram Equalization", Category = "Histogram",
    Description = "Global histogram equalization to improve overall image contrast")]
public sealed class HistogramEqualizationTool : VisionTool
{
    private readonly InputPort<IVisionImage> _input;
    private readonly OutputPort<IVisionImage> _output;

    public HistogramEqualizationTool()
    {
        _input = AddInput<IVisionImage>("Image");
        _output = AddOutput<IVisionImage>("Image");
        // Không có Parameter nào - đúng như tài liệu mô tả "hoàn toàn tự động"
    }

    protected override void OnExecute(IToolContext context)
    {
        Mat src = _input.Value!.AsMat();
        Mat dst = new Mat();

        if (src.Channels() == 1)
        {
            Cv2.EqualizeHist(src, dst); // Ảnh xám: áp dụng trực tiếp lên kênh duy nhất
        }
        else
        {
            // Ảnh màu: chuyển sang YCrCb, chỉ equalize kênh Y (độ sáng) - giữ nguyên Cr/Cb (thông tin màu sắc)
            Mat ycrcb = new Mat();
            Cv2.CvtColor(src, ycrcb, ColorConversionCodes.BGR2YCrCb);
            Mat[] channels = Cv2.Split(ycrcb);
            Cv2.EqualizeHist(channels[0], channels[0]); // channels[0] = kênh Y trong thứ tự YCrCb
            Cv2.Merge(channels, ycrcb);
            Cv2.CvtColor(ycrcb, dst, ColorConversionCodes.YCrCb2BGR);
            ycrcb.Dispose();
            foreach (var ch in channels) ch.Dispose();
        }

        _output.Value = new MatVisionImage(dst);
        context.Log("HistogramEqualization: applied global equalization.");
    }
}