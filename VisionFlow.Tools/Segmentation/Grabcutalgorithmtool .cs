// ==================== Vai trò chính:                Tách vật thể (foreground) khỏi nền phức tạp với độ chính xác cao - kỹ thuật mạnh nhất nhóm Segmentation cổ điển
// ==================== Thành phần / Class tiêu biểu: GrabCutAlgorithmTool
// ==================== Phụ thuộc vào:                OpenCvSharp (Cv2.GrabCut - Gaussian Mixture Model + Graph Cut)
// ==================== Pattern / Kỹ thuật nổi bật:   Init bằng Rect (nhanh, khung thẳng) hoặc bằng Mask
//                       (chậm hơn nhưng hỗ trợ khung xoay nghiêng) - GrabCut tự lặp học phân phối màu
//                       nền/vật thể qua nhiều vòng (Iterations) rồi tối ưu biên giới bằng Graph Cut

using System;
using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Models; // Point2d
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;
using Point2d = VisionFlow.Core.Models.Point2d;

namespace VisionFlow.Tools.Segmentation; // Cùng thư mục với RegionGrowingTool / WatershedAlgorithmTool

/// <summary>Định dạng ảnh kết quả xuất ra ở cổng ImageMatrix.</summary>
public enum GrabCutOutputMode
{
    MaskedForeground, // Chỉ giữ pixel vật thể, nền đen - phù hợp xử lý tiếp (đo, OCR trên vật)
    Composite,        // Ảnh gốc + tô xanh lá nhạt lên vật thể - phù hợp demo trực quan
    MaskOnly          // Chỉ mặt nạ đen-trắng - phù hợp nối tiếp Threshold/Morphology
}

/// <summary>
/// GrabCutAlgorithm: thuật toán tách nền/vật thể mạnh nhất trong nhóm Segmentation cổ điển - chỉ cần vẽ
/// 1 khung hình chữ nhật (có thể xoay) bao quanh vật thể, GrabCut tự học phân phối màu nền (Gaussian
/// Mixture Model) và tối ưu biên giới bằng Graph Cut qua nhiều vòng lặp, cho kết quả biên mềm mại gần
/// như chất lượng AI khi vật thể có viền tương đối rõ. Đánh đổi: chậm hơn nhiều so với Threshold/Watershed
/// (50ms-3s tùy Iterations), không phù hợp realtime FPS cao hoặc tách nhiều vật cùng lúc.
/// </summary>
[ToolMetadata(
    "GrabCutAlgorithm",
    DisplayName = "Grab Cut Algorithm",
    Category = "Segmentation",
    Description = "Separate foreground object from complex background using GrabCut (GMM + Graph Cut)")]
public sealed class GrabCutAlgorithmTool : VisionTool
{
    // Giá trị nhãn thô của OpenCV GrabCut mask (giữ nguyên hằng số gốc thay vì đoán tên enum OpenCvSharp,
    // để tránh sai lệch giữa các phiên bản binding): 0=chắc chắn nền, 1=chắc chắn vật thể,
    // 2=có thể là nền, 3=có thể là vật thể. Foreground cuối cùng = pixel có nhãn LẺ (1 hoặc 3).
    private const byte GcBackground = 0;
    private const byte GcForeground = 1;
    private const byte GcProbableBackground = 2;
    private const byte GcProbableForeground = 3;

    #region 1. Khai báo Port (In/Out)
    private readonly InputPort<IVisionImage> _imageMatrix;

    private readonly OutputPort<IVisionImage> _outImageMatrix;
    private readonly OutputPort<IVisionImage> _outForegroundMask;
    private readonly OutputPort<double> _outForegroundArea;
    private readonly OutputPort<Rect> _outBoundingBox;
    private readonly OutputPort<IReadOnlyList<Point2d>> _outForegroundContour;
    private readonly OutputPort<IVisionImage> _outInteractiveVisualization;
    #endregion

    #region 2. Khai báo Parameter
    // --- Tab Region ---
    private readonly ToolParameter<double> _regionCenterX;
    private readonly ToolParameter<double> _regionCenterY;
    private readonly ToolParameter<double> _regionWidth;
    private readonly ToolParameter<double> _regionHeight;
    private readonly ToolParameter<double> _regionAngle;
    private readonly ToolParameter<bool> _autoRegionIfZero;

    // --- Tab Iterations ---
    private readonly ToolParameter<int> _iterations;

    // --- Tab Output ---
    private readonly ToolParameter<GrabCutOutputMode> _outputMode;
    private readonly ToolParameter<bool> _drawRegion;
    private readonly ToolParameter<bool> _drawForegroundContour;
    private readonly ToolParameter<int> _contourThickness;
    #endregion

    public GrabCutAlgorithmTool()
    {
        _imageMatrix = AddInput<IVisionImage>("ImageMatrix", "Image Matrix");

        _outImageMatrix = AddOutput<IVisionImage>("ImageMatrix", "Image Matrix");
        _outForegroundMask = AddOutput<IVisionImage>("ForegroundMask", "Foreground Mask");
        _outForegroundArea = AddOutput<double>("ForegroundArea", "Foreground Area");
        _outBoundingBox = AddOutput<Rect>("BoundingBox", "Bounding Box");
        _outForegroundContour = AddOutput<IReadOnlyList<Point2d>>("ForegroundContour", "Foreground Contour");
        _outInteractiveVisualization = AddOutput<IVisionImage>("InteractiveVisualization", "Interactive Visualization");

        _regionCenterX = AddParameter("RegionCenterX", 0.0, "Region Center X", min: 0.0, max: 100000.0, category: "Region", order: 1);
        _regionCenterY = AddParameter("RegionCenterY", 0.0, "Region Center Y", min: 0.0, max: 100000.0, category: "Region", order: 2);
        _regionWidth = AddParameter("RegionWidth", 0.0, "Region Width", min: 0.0, max: 100000.0, category: "Region", order: 3);
        _regionHeight = AddParameter("RegionHeight", 0.0, "Region Height", min: 0.0, max: 100000.0, category: "Region", order: 4);
        _regionAngle = AddParameter("RegionAngle", 0.0, "Region Angle", min: -180.0, max: 180.0, category: "Region", order: 5);
        _autoRegionIfZero = AddParameter("AutoRegionIfZero", true, "Auto Region If Zero", category: "Region", order: 6);

        _iterations = AddParameter("Iterations", 5, "Iterations", min: 1, max: 15, category: "Iterations", order: 1);

        _outputMode = AddParameter("OutputMode", GrabCutOutputMode.MaskedForeground, "Output Mode", category: "Output", order: 1);
        _drawRegion = AddParameter("DrawRegion", true, "Draw Region", category: "Output", order: 2);
        _drawForegroundContour = AddParameter("DrawForegroundContour", true, "Draw Foreground Contour", category: "Output", order: 3);
        _contourThickness = AddParameter("ContourThickness", 2, "Contour Thickness", min: 1, max: 20, category: "Output", order: 4);
    }

    protected override void OnExecute(IToolContext context)
    {
        Mat original = _imageMatrix.Value!.AsMat();
        Mat bgr = ToColor(original); // GrabCut bắt buộc ảnh CV_8UC3

        int width = bgr.Width, height = bgr.Height;

        // ----- Bước 1: xác định vùng init (Rect thẳng hoặc polygon xoay) -----
        double cx = _regionCenterX.Value, cy = _regionCenterY.Value;
        double w = _regionWidth.Value, h = _regionHeight.Value;

        if (w <= 0 && h <= 0 && _autoRegionIfZero.Value)
        {
            // Tự động dùng vùng giữa 50% ảnh - tiện cho demo nhanh
            w = width * 0.5;
            h = height * 0.5;
            cx = width * 0.5;
            cy = height * 0.5;
        }

        if (w <= 0 || h <= 0)
            throw new ToolExecutionException("GrabCutAlgorithm: RegionWidth/RegionHeight must be > 0 (or enable AutoRegionIfZero).");

        bool isRotated = Math.Abs(_regionAngle.Value) > 0.01;
        Point[] regionCorners = BuildRotatedRectCorners(cx, cy, w, h, _regionAngle.Value);

        using Mat mask = new Mat(height, width, MatType.CV_8UC1, new Scalar(GcBackground));
        using Mat bgdModel = new Mat();
        using Mat fgdModel = new Mat();

        GrabCutModes mode;
        Rect initRect;

        if (isRotated)
        {
            // ----- Khung xoay: tự dựng mask (GC_PR_FGD bên trong polygon) rồi dùng InitWithMask -----
            Cv2.FillPoly(mask, new[] { regionCorners }, new Scalar(GcProbableForeground));
            mode = GrabCutModes.InitWithMask;
            initRect = new Rect(0, 0, width, height); // Không được thuật toán dùng tới ở chế độ InitWithMask, nhưng API vẫn yêu cầu 1 Rect hợp lệ
        }
        else
        {
            // ----- Khung thẳng (axis-aligned): dùng InitWithRect - nhanh hơn, đủ dùng cho đa số trường hợp -----
            var rawRect = new Rect((int)Math.Round(cx - w / 2), (int)Math.Round(cy - h / 2), (int)Math.Round(w), (int)Math.Round(h));
            initRect = ClampRect(rawRect, width, height);
            if (initRect.Width <= 0 || initRect.Height <= 0)
                throw new ToolExecutionException("GrabCutAlgorithm: Region falls entirely outside the image.");
            mode = GrabCutModes.InitWithRect;
        }

        // ----- Bước 2: chạy GrabCut - GMM + Graph Cut lặp qua Iterations vòng -----
        Cv2.GrabCut(bgr, mask, initRect, bgdModel, fgdModel, _iterations.Value, mode);

        // ----- Bước 3: xây dựng mặt nạ nhị phân cuối cùng - foreground = nhãn LẺ (1 hoặc 3) -----
        using Mat fgExact = new Mat();
        using Mat fgProbable = new Mat();
        Cv2.InRange(mask, new Scalar(GcForeground), new Scalar(GcForeground), fgExact);
        Cv2.InRange(mask, new Scalar(GcProbableForeground), new Scalar(GcProbableForeground), fgProbable);
        Mat foregroundMask = new Mat();
        Cv2.BitwiseOr(fgExact, fgProbable, foregroundMask);

        double area = Cv2.CountNonZero(foregroundMask);
        Rect boundingBox = area > 0 ? Cv2.BoundingRect(foregroundMask) : new Rect(0, 0, 0, 0);

        // Lấy contour LỚN NHẤT của foreground (GrabCut đôi khi để lại vài đốm nhỏ lẻ tẻ quanh biên)
        Cv2.FindContours(foregroundMask, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        Point2d[] foregroundContour = contours.Length > 0
            ? contours.OrderByDescending(c => Cv2.ContourArea(c)).First().Select(p => new Point2d(p.X, p.Y)).ToArray()
            : Array.Empty<Point2d>();

        // ----- Bước 4: dựng ImageMatrix theo OutputMode -----
        Mat resultImage = BuildOutputImage(bgr, foregroundMask);

        // ----- Bước 5: InteractiveVisualization trên nền đen + vẽ đè lên ImageMatrix -----
        Mat overlayOnBlack = new Mat(bgr.Size(), MatType.CV_8UC3, Scalar.Black);
        DrawAnnotations(resultImage, regionCorners, boundingBox, contours);
        DrawAnnotations(overlayOnBlack, regionCorners, boundingBox, contours);

        _outImageMatrix.Value = new MatVisionImage(resultImage);
        _outForegroundMask.Value = new MatVisionImage(foregroundMask);
        _outForegroundArea.Value = area;
        _outBoundingBox.Value = boundingBox;
        _outForegroundContour.Value = foregroundContour;
        _outInteractiveVisualization.Value = new MatVisionImage(overlayOnBlack);

        bgr.Dispose();

        context.Log($"GrabCutAlgorithm: mode={mode}, iterations={_iterations.Value}, foregroundArea={area:F0}px, outputMode={_outputMode.Value}");
    }

    /// <summary>Dựng ảnh output theo đúng OutputMode đã chọn ở Tab Output.</summary>
    private Mat BuildOutputImage(Mat bgr, Mat foregroundMask)
    {
        switch (_outputMode.Value)
        {
            case GrabCutOutputMode.MaskOnly:
                {
                    // Trả về đúng mặt nạ đen-trắng, chuyển sang 3 kênh để đồng bộ kiểu dữ liệu ImageMatrix với các mode khác
                    var maskBgr = new Mat();
                    Cv2.CvtColor(foregroundMask, maskBgr, ColorConversionCodes.GRAY2BGR);
                    return maskBgr;
                }

            case GrabCutOutputMode.Composite:
                {
                    using Mat greenLayer = new Mat(bgr.Size(), bgr.Type(), new Scalar(0, 255, 0));
                    using Mat blended = new Mat();
                    Cv2.AddWeighted(bgr, 0.7, greenLayer, 0.3, 0, blended); // Trộn xanh lá NHẠT trên toàn ảnh trước
                    Mat composite = bgr.Clone();
                    blended.CopyTo(composite, foregroundMask); // Chỉ ghi đè phần đã trộn tại đúng vùng foreground
                    return composite;
                }

            default: // MaskedForeground
                {
                    Mat masked = new Mat(bgr.Size(), bgr.Type(), Scalar.Black);
                    bgr.CopyTo(masked, foregroundMask);
                    return masked;
                }
        }
    }

    /// <summary>
    /// Vẽ vùng init (xanh dương) theo DrawRegion; bbox + contour vật thể (xanh lá + vàng) theo DrawForegroundContour.
    /// LƯU Ý: tài liệu Tab Output chỉ có 2 cờ (DrawRegion, DrawForegroundContour) nhưng InteractiveVisualization
    /// mô tả 3 thành phần (init/bbox/contour) - tôi gộp việc vẽ bbox chung điều kiện với DrawForegroundContour
    /// vì cả 2 đều là thông tin mô tả KẾT QUẢ foreground, khác với DrawRegion chỉ mô tả vùng init đầu vào.
    /// </summary>
    private void DrawAnnotations(Mat target, Point[] regionCorners, Rect boundingBox, Point[][] contours)
    {
        if (_drawRegion.Value)
            Cv2.Polylines(target, new[] { regionCorners }, isClosed: true, new Scalar(255, 0, 0), thickness: 2); // Xanh dương

        if (_drawForegroundContour.Value)
        {
            if (boundingBox.Width > 0 && boundingBox.Height > 0)
                Cv2.Rectangle(target, boundingBox, new Scalar(0, 255, 0), thickness: 2); // Xanh lá

            Cv2.DrawContours(target, contours, -1, new Scalar(0, 255, 255), _contourThickness.Value); // Vàng
        }
    }

    /// <summary>Tính 4 góc của 1 hình chữ nhật xoay quanh tâm (cx,cy) - tái sử dụng ý tưởng đã dùng ở ActiveContourTool cho ellipse.</summary>
    private static Point[] BuildRotatedRectCorners(double cx, double cy, double w, double h, double angleDeg)
    {
        double angleRad = angleDeg * Math.PI / 180.0;
        double cosA = Math.Cos(angleRad), sinA = Math.Sin(angleRad);
        double hw = w / 2.0, hh = h / 2.0;

        (double x, double y)[] local = { (-hw, -hh), (hw, -hh), (hw, hh), (-hw, hh) };
        return local.Select(p =>
        {
            double rx = p.x * cosA - p.y * sinA;
            double ry = p.x * sinA + p.y * cosA;
            return new Point((int)Math.Round(cx + rx), (int)Math.Round(cy + ry));
        }).ToArray();
    }

    private static Rect ClampRect(Rect r, int imgWidth, int imgHeight)
    {
        int x = Math.Max(0, r.X), y = Math.Max(0, r.Y);
        int right = Math.Min(imgWidth, r.X + r.Width);
        int bottom = Math.Min(imgHeight, r.Y + r.Height);
        return new Rect(x, y, Math.Max(0, right - x), Math.Max(0, bottom - y));
    }

    private static Mat ToColor(Mat src)
    {
        if (src.Channels() == 3) return src.Clone();
        var color = new Mat();
        Cv2.CvtColor(src, color, src.Channels() == 1 ? ColorConversionCodes.GRAY2BGR : ColorConversionCodes.BGRA2BGR);
        return color;
    }
}