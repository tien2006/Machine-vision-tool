// ==================== Vai trò chính:                Lọc vùng màu theo dải HSV Min-Max - robust hơn ColorRangeDetection (RGB) khi ánh sáng thay đổi
// ==================== Thành phần / Class tiêu biểu: HSVColorFilterTool
// ==================== Phụ thuộc vào:                OpenCvSharp
// ==================== Pattern / Kỹ thuật nổi bật:   Cv2.InRange trên HSV, output nhẹ (không kèm contour) để ghép nhanh với FindContours/BlobAnalysis downstream
// ==================== FIX: "Image" trước đây LUÔN là bản sao ảnh gốc không đổi (chỉ MaskImage mới phản ánh kết quả),
//                          trong khi Editor auto-preview LUÔN lấy cổng ảnh ĐẦU TIÊN (Image) -> chỉnh HSV không thấy gì đổi.
//                          Giờ thêm ShowOverlayPreview: Image sẽ tô màu nổi bật (highlight) đúng vùng khớp HSV -> live preview hoạt động.

using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;

namespace VisionFlow.Tools.Color;

/// <summary>
/// Phiên bản HSV đơn giản/nhẹ/nhanh hơn ColorRangeDetection (RGB) - ưu tiên hàng đầu khi làm việc với
/// ảnh từ camera thực tế vì HSV tách Hue (màu) khỏi Value (độ sáng), ít bị ảnh hưởng bởi ánh sáng thay đổi.
/// </summary>
[ToolMetadata("HSVColorFilter", DisplayName = "HSV Color Filter", Category = "Color",
    Description = "Filter pixels by HSV Min-Max range - robust to lighting changes")]
public sealed class HSVColorFilterTool : VisionTool
{
    private readonly InputPort<IVisionImage> _input;
    private readonly OutputPort<IVisionImage> _outImage;
    private readonly OutputPort<IVisionImage> _outMaskImage;
    private readonly OutputPort<int> _outPixelCount;
    private readonly OutputPort<double> _outCoverage;

    // ----- Tab Color - mặc định đỏ (0-10) theo tài liệu -----
    private readonly ToolParameter<double> _hueMin, _hueMax;
    private readonly ToolParameter<double> _saturationMin, _saturationMax;
    private readonly ToolParameter<double> _valueMin, _valueMax;

    // ★ FIX: tham số mới - quyết định "Image" hiển thị overlay highlight (mặc định, để thấy kết quả NGAY khi chỉnh
    // slider) hay giữ đúng hành vi pass-through nguyên bản theo tài liệu (khi pipeline downstream cần ảnh gốc y nguyên).
    private readonly ToolParameter<bool> _showOverlayPreview;
    private readonly ToolParameter<string> _overlayColor; // Màu highlight vùng khớp - dùng chung style GeometryColors đã có ở nhóm Geometry

    public HSVColorFilterTool()
    {
        _input = AddInput<IVisionImage>("Image");
        _outImage = AddOutput<IVisionImage>("Image");
        _outMaskImage = AddOutput<IVisionImage>("MaskImage");
        _outPixelCount = AddOutput<int>("PixelCount");
        _outCoverage = AddOutput<double>("Coverage");

        _hueMin = AddParameter("HueMin", 0.0, "Hue Min", 0.0, 180.0, category: "Color", order: 1);
        _hueMax = AddParameter("HueMax", 10.0, "Hue Max", 0.0, 180.0, category: "Color", order: 2);
        _saturationMin = AddParameter("SaturationMin", 100.0, "Saturation Min", 0.0, 255.0, category: "Color", order: 3); // MinSat=100+ loại vùng nhạt/xám
        _saturationMax = AddParameter("SaturationMax", 255.0, "Saturation Max", 0.0, 255.0, category: "Color", order: 4);
        _valueMin = AddParameter("ValueMin", 50.0, "Value Min", 0.0, 255.0, category: "Color", order: 5); // MinValue=50+ loại vùng quá tối
        _valueMax = AddParameter("ValueMax", 255.0, "Value Max", 0.0, 255.0, category: "Color", order: 6);

        // ★ FIX: đặt ở Tab Output cho đúng nhóm với các tuỳ chọn hiển thị khác trong toàn bộ dự án
        _showOverlayPreview = AddParameter("ShowOverlayPreview", true, "Show Overlay Preview", category: "Output", order: 1);
        _overlayColor = AddChoiceParameter("OverlayColor", "Green", new[] { "Red", "Green", "Blue", "Yellow", "Cyan", "Magenta" }, "Overlay Color", category: "Output", order: 2);
    }

    protected override void OnExecute(IToolContext context)
    {
        Mat srcRaw = _input.Value!.AsMat();
        Mat src = ColorUtil.EnsureBgr(srcRaw, context, "HSVColorFilter");

        Mat hsv = new Mat();
        Cv2.CvtColor(src, hsv, ColorConversionCodes.BGR2HSV);

        Mat mask = new Mat();
        Cv2.InRange(hsv, new Scalar(_hueMin.Value, _saturationMin.Value, _valueMin.Value),
            new Scalar(_hueMax.Value, _saturationMax.Value, _valueMax.Value), mask);
        hsv.Dispose();

        int pixelCount = Cv2.CountNonZero(mask);
        double coverage = (double)pixelCount / (mask.Rows * mask.Cols) * 100.0;

        // ★ FIX: dựng ảnh preview cho cổng "Image" - đây là cổng Editor LUÔN lấy làm ảnh hiển thị lớn,
        // nên phải phản ánh ĐÚNG kết quả lọc mới thấy được thay đổi ngay khi kéo slider HSV.
        Mat previewImage;
        if (_showOverlayPreview.Value)
        {
            // Tô màu nổi bật (highlight) 50% alpha đúng vùng đang khớp dải HSV, giữ nguyên phần còn lại của ảnh gốc
            // -> mỗi lần đổi Hue/Saturation/Value, vùng highlight di chuyển/co giãn NGAY LẬP TỨC, thấy trực quan rõ ràng.
            Scalar highlight = _overlayColor.Value switch
            {
                "Red" => Scalar.Red,
                "Blue" => Scalar.Blue,
                "Yellow" => Scalar.Yellow,
                "Cyan" => Scalar.Cyan,
                "Magenta" => Scalar.Magenta,
                _ => Scalar.LimeGreen, // "Green" mặc định
            };
            using Mat colorLayer = new Mat(src.Size(), src.Type(), highlight); // Lớp phủ 1 màu đồng nhất, cùng kích thước ảnh gốc
            using Mat blended = new Mat();
            Cv2.AddWeighted(src, 0.5, colorLayer, 0.5, 0, blended); // Trộn 50-50 giữa ảnh gốc và lớp màu highlight

            previewImage = src.Clone();       // Bắt đầu từ ảnh gốc nguyên vẹn
            blended.CopyTo(previewImage, mask); // CHỈ ghi đè phần đã trộn màu vào đúng những pixel có mask=255 (đang khớp HSV)
        }
        else
        {
            // Giữ đúng hành vi pass-through nguyên bản theo tài liệu (dùng khi downstream cần ảnh gốc y nguyên)
            previewImage = src.Clone();
        }

        _outImage.Value = new MatVisionImage(previewImage); // ★ FIX: giờ "Image" phản ánh đúng kết quả lọc, không còn là bản sao tĩnh
        _outMaskImage.Value = new MatVisionImage(mask);
        _outPixelCount.Value = pixelCount;
        _outCoverage.Value = coverage;

        if (!ReferenceEquals(src, srcRaw)) src.Dispose();

        context.Log($"HSVColorFilter: {pixelCount}px matched ({coverage:F1}% coverage), Hue=[{_hueMin.Value},{_hueMax.Value}]");
    }
}