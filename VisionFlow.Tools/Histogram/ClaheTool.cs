// ==================== Vai trò chính:                Cân bằng tương phản cục bộ (Contrast Limited Adaptive Histogram Equalization) theo từng tile nhỏ
// ==================== Thành phần / Class tiêu biểu: ClaheTool
// ==================== Phụ thuộc vào:                OpenCvSharp
// ==================== Pattern / Kỹ thuật nổi bật:   Cv2.CreateCLAHE - chia ảnh thành lưới tile, equalize riêng từng tile, giới hạn khuếch đại bằng ClipLimit

using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;

namespace VisionFlow.Tools.Histogram;

/// <summary>
/// CLAHE: phiên bản nâng cao của HistogramEqualization - chia ảnh thành lưới tile nhỏ, cân bằng
/// riêng từng tile nên xử lý tốt ảnh ánh sáng không đều (1 góc tối, 1 góc sáng) mà Equalization
/// toàn cục không làm được. Chậm hơn HistogramEqualization vì phải xử lý từng tile riêng biệt.
/// </summary>
[ToolMetadata("CLAHE", DisplayName = "CLAHE", Category = "Histogram",
    Description = "Contrast Limited Adaptive Histogram Equalization for uneven lighting")]
public sealed class ClaheTool : VisionTool
{
    private readonly InputPort<IVisionImage> _input;
    private readonly OutputPort<IVisionImage> _output;

    // ----- Tab Enhancement -----
    private readonly ToolParameter<double> _clipLimit;
    private readonly ToolParameter<int> _tileGridSizeX;
    private readonly ToolParameter<int> _tileGridSizeY;

    public ClaheTool()
    {
        _input = AddInput<IVisionImage>("Image");
        _output = AddOutput<IVisionImage>("Image");

        // Mặc định khuyến nghị theo tài liệu: ClipLimit=3, TileGridSize=8x8
        _clipLimit = AddParameter("ClipLimit", 3.0, "Clip Limit", 0.1, 40.0, category: "Enhancement", order: 1);
        _tileGridSizeX = AddParameter("TileGridSizeX", 8, "Tile Grid Size X", 1, 64, category: "Enhancement", order: 2);
        _tileGridSizeY = AddParameter("TileGridSizeY", 8, "Tile Grid Size Y", 1, 64, category: "Enhancement", order: 3);
    }

    protected override void OnExecute(IToolContext context)
    {
        Mat src = _input.Value!.AsMat();
        using CLAHE clahe = Cv2.CreateCLAHE(_clipLimit.Value, new Size(_tileGridSizeX.Value, _tileGridSizeY.Value));

        Mat dst = new Mat();
        if (src.Channels() == 1)
        {
            clahe.Apply(src, dst); // Ảnh xám: áp dụng trực tiếp
        }
        else
        {
            // Ảnh màu: cùng chiến lược YCrCb như HistogramEqualizationTool - chỉ tăng cường kênh Y
            Mat ycrcb = new Mat();
            Cv2.CvtColor(src, ycrcb, ColorConversionCodes.BGR2YCrCb);
            Mat[] channels = Cv2.Split(ycrcb);
            clahe.Apply(channels[0], channels[0]);
            Cv2.Merge(channels, ycrcb);
            Cv2.CvtColor(ycrcb, dst, ColorConversionCodes.YCrCb2BGR);
            ycrcb.Dispose();
            foreach (var ch in channels) ch.Dispose();
        }

        _output.Value = new MatVisionImage(dst);
        context.Log($"CLAHE: ClipLimit={_clipLimit.Value}, TileGrid={_tileGridSizeX.Value}x{_tileGridSizeY.Value}");
    }
}