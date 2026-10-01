// ==================== Vai trò chính:                Phân đoạn (segmentation) đối tượng bằng đường cong "Snake" tự co giãn bám sát biên thật trong ảnh
// ==================== Thành phần / Class tiêu biểu: ActiveContourTool
// ==================== Phụ thuộc vào:                OpenCvSharp (Sobel để tính gradient, Canny/FindContours cho khởi tạo)
// ==================== Pattern / Kỹ thuật nổi bật:   Greedy Snake (Williams & Shah) - biến thể đơn giản hoá của
//                       Active Contour cổ điển Kass-Witkin-Terzopoulos: mỗi điểm contour tự tìm vị trí tốt nhất
//                       trong 1 cửa sổ lân cận (SearchRadius) sao cho tối thiểu hoá tổng năng lượng liên tục
//                       (Econt) + độ cong (Ecurv) + hút về biên ảnh thật (gradient mạnh), lặp lại nhiều vòng.

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

namespace VisionFlow.Tools.Segmentation;

/// <summary>Cách tạo đường bao khởi tạo cho Snake trước khi bắt đầu co giãn.</summary>
public enum InitialContourMethod
{
    Region,    // Hình ellipse nội tiếp trong vùng ROI (RegionCenterX/Y/Width/Height/Angle)
    CannyEdge  // Dò biên Canny trong vùng ROI, lấy contour lớn nhất tìm được làm điểm khởi tạo
}

/// <summary>
/// ActiveContour (Snake): khởi tạo 1 đường cong ban đầu (ellipse hoặc biên Canny) quanh vùng ROI, sau đó
/// LẶP LẠI nhiều vòng để từng điểm trên đường cong tự dịch chuyển tới vị trí "tốt hơn" trong vùng lân cận -
/// nơi cân bằng giữa việc GIỮ HÌNH DẠNG mượt (Alpha/Beta) và việc BÁM VÀO biên thật của vật thể (gradient ảnh).
/// Phù hợp khi biên vật thể không đều/không khép kín rõ ràng - trường hợp Threshold/FindContours đơn thuần
/// dễ bị đứt đoạn hoặc lem ra ngoài do nhiễu/ánh sáng không đều.
/// </summary>
[ToolMetadata(
    "ActiveContour",
    DisplayName = "Active Contour",
    Category = "Segmentation",
    Description = "Snake-based segmentation: an initial curve iteratively shrinks/deforms to hug the real object boundary")]
public sealed class ActiveContourTool : VisionTool
{
    #region 1. Khai báo Port (In/Out)
    private readonly InputPort<IVisionImage> _imageMatrix;

    private readonly OutputPort<IVisionImage> _outImageMatrix;
    private readonly OutputPort<IVisionImage> _outRegionMask;
    private readonly OutputPort<IReadOnlyList<Point2d>> _outContourPoints;
    private readonly OutputPort<double> _outContourArea;
    private readonly OutputPort<double> _outContourPerimeter;
    private readonly OutputPort<Rect> _outBoundingBox;
    private readonly OutputPort<IVisionImage> _outInteractiveVisualization; // Overlay trên nền ĐEN, dùng cho OverlayRendererTool
    #endregion

    #region 2. Khai báo Parameter
    // --- Tab Region ---
    private readonly ToolParameter<double> _regionCenterX;
    private readonly ToolParameter<double> _regionCenterY;
    private readonly ToolParameter<double> _regionWidth;
    private readonly ToolParameter<double> _regionHeight;
    private readonly ToolParameter<double> _regionAngle; // Độ

    // --- Tab Initialization ---
    private readonly ToolParameter<InitialContourMethod> _initialContour;
    private readonly ToolParameter<double> _cannyLowThreshold;
    private readonly ToolParameter<double> _cannyHighThreshold;

    // --- Tab Snake ---
    private readonly ToolParameter<double> _alpha;         // Độ co giãn (elasticity) - phạt khoảng cách không đều giữa các điểm liền kề
    private readonly ToolParameter<double> _beta;          // Độ mượt (smoothness) - phạt độ cong gấp khúc
    private readonly ToolParameter<double> _gamma;         // Hệ số bước di chuyển mỗi vòng lặp (0-1): điểm dịch bao nhiêu % quãng đường tới vị trí tốt nhất tìm được
    private readonly ToolParameter<int> _iterations;
    private readonly ToolParameter<int> _searchRadius;     // Bán kính cửa sổ tìm kiếm quanh mỗi điểm (pixel)
    private readonly ToolParameter<int> _contourPointCount;

    // --- Tab Output ---
    private readonly ToolParameter<bool> _outputAsColorImage;
    private readonly ToolParameter<bool> _drawContour;
    private readonly ToolParameter<bool> _drawFilled;
    private readonly ToolParameter<int> _contourThickness;
    #endregion

    public ActiveContourTool()
    {
        _imageMatrix = AddInput<IVisionImage>("ImageMatrix", "Image Matrix");

        _outImageMatrix = AddOutput<IVisionImage>("ImageMatrix", "Image Matrix");
        _outRegionMask = AddOutput<IVisionImage>("RegionMask", "Region Mask");
        _outContourPoints = AddOutput<IReadOnlyList<Point2d>>("ContourPoints", "Contour Points");
        _outContourArea = AddOutput<double>("ContourArea", "Contour Area");
        _outContourPerimeter = AddOutput<double>("ContourPerimeter", "Contour Perimeter");
        _outBoundingBox = AddOutput<Rect>("BoundingBox", "Bounding Box");
        _outInteractiveVisualization = AddOutput<IVisionImage>("InteractiveVisualization", "Interactive Visualization");

        _regionCenterX = AddParameter("RegionCenterX", 0.0, "Region Center X", min: 0.0, max: 100000.0, category: "Region", order: 1);
        _regionCenterY = AddParameter("RegionCenterY", 0.0, "Region Center Y", min: 0.0, max: 100000.0, category: "Region", order: 2);
        _regionWidth = AddParameter("RegionWidth", 200.0, "Region Width", min: 5.0, max: 100000.0, category: "Region", order: 3);
        _regionHeight = AddParameter("RegionHeight", 200.0, "Region Height", min: 5.0, max: 100000.0, category: "Region", order: 4);
        _regionAngle = AddParameter("RegionAngle", 0.0, "Region Angle", min: -180.0, max: 180.0, category: "Region", order: 5);

        _initialContour = AddParameter("InitialContour", InitialContourMethod.Region, "Initial Contour", category: "Initialization", order: 1);
        _cannyLowThreshold = AddParameter("CannyLowThreshold", 50.0, "Canny Low Threshold", min: 0.0, max: 255.0, category: "Initialization", order: 2);
        _cannyHighThreshold = AddParameter("CannyHighThreshold", 150.0, "Canny High Threshold", min: 0.0, max: 255.0, category: "Initialization", order: 3);

        // Alpha/Beta mặc định nhỏ: contour linh hoạt bám chi tiết, người dùng tăng dần khi cần ổn định hơn
        _alpha = AddParameter("Alpha", 0.5, "Alpha", min: 0.0, max: 10.0, category: "Snake", order: 1);
        _beta = AddParameter("Beta", 0.5, "Beta", min: 0.0, max: 10.0, category: "Snake", order: 2);
        _gamma = AddParameter("Gamma", 0.3, "Gamma", min: 0.01, max: 1.0, category: "Snake", order: 3);
        _iterations = AddParameter("Iterations", 150, "Iterations", min: 1, max: 2000, category: "Snake", order: 4);
        _searchRadius = AddParameter("SearchRadius", 3, "Search Radius", min: 1, max: 30, category: "Snake", order: 5);
        _contourPointCount = AddParameter("ContourPointCount", 60, "Contour Point Count", min: 8, max: 500, category: "Snake", order: 6);

        _outputAsColorImage = AddParameter("OutputAsColorImage", true, "Output As Color Image", category: "Output", order: 1);
        _drawContour = AddParameter("DrawContour", true, "Draw Contour", category: "Output", order: 2);
        _drawFilled = AddParameter("DrawFilled", false, "Draw Filled", category: "Output", order: 3);
        _contourThickness = AddParameter("ContourThickness", 2, "Contour Thickness", min: 1, max: 20, category: "Output", order: 4);
    }

    protected override void OnExecute(IToolContext context)
    {
        Mat src = _imageMatrix.Value!.AsMat();

        // Ảnh xám dùng để tính gradient (năng lượng ngoài) - Active Contour hoạt động trên độ sáng, không cần màu
        using Mat gray = src.Channels() == 1 ? src.Clone() : ToGray(src);

        // ----- Bước 1: tính bản đồ gradient magnitude, chuẩn hoá về [0,1] để dùng làm năng lượng ngoài -----
        using Mat gradMag = ComputeNormalizedGradientMagnitude(gray);

        // ----- Bước 2: khởi tạo đường contour ban đầu -----
        List<Point2d> contour = _initialContour.Value == InitialContourMethod.CannyEdge
            ? BuildCannyInitialContour(gray, context)
            : BuildEllipseInitialContour();

        // ----- Bước 3: chạy Greedy Snake qua nhiều vòng lặp -----
        int n = contour.Count;
        double alpha = _alpha.Value, beta = _beta.Value, gamma = Math.Clamp(_gamma.Value, 0.0, 1.0);
        int radius = _searchRadius.Value;

        for (int iter = 0; iter < _iterations.Value; iter++)
        {
            // Khoảng cách trung bình giữa các điểm liền kề của vòng lặp HIỆN TẠI - dùng làm mốc cho Econt (độ đều đặn)
            double avgDist = AverageNeighborDistance(contour);

            var updated = new List<Point2d>(n);
            for (int i = 0; i < n; i++)
            {
                Point2d prev = contour[(i - 1 + n) % n];
                Point2d cur = contour[i];
                Point2d next = contour[(i + 1) % n];

                Point2d best = cur;
                double bestEnergy = double.MaxValue;

                for (int dy = -radius; dy <= radius; dy++)
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        double cx = cur.X + dx, cy = cur.Y + dy;
                        if (cx < 0 || cy < 0 || cx >= gray.Width || cy >= gray.Height) continue;

                        double dPrev = Distance(new Point2d(cx, cy), prev);
                        double econt = (dPrev - avgDist) * (dPrev - avgDist); // Phạt nếu khoảng cách tới điểm trước lệch xa mức trung bình

                        double curvX = prev.X - 2 * cx + next.X;
                        double curvY = prev.Y - 2 * cy + next.Y;
                        double ecurv = curvX * curvX + curvY * curvY; // Đạo hàm bậc 2 rời rạc - phạt góc gấp khúc

                        double eimage = -SampleBilinear(gradMag, cx, cy); // Gradient càng mạnh, năng lượng càng âm (càng "hấp dẫn")

                        double energy = alpha * econt + beta * ecurv + eimage;
                        if (energy < bestEnergy)
                        {
                            bestEnergy = energy;
                            best = new Point2d(cx, cy);
                        }
                    }

                // Di chuyển GAMMA phần trăm quãng đường tới vị trí tốt nhất tìm được - tránh nhảy giật cục, hội tụ êm hơn
                updated.Add(new Point2d(cur.X + gamma * (best.X - cur.X), cur.Y + gamma * (best.Y - cur.Y)));
            }
            contour = updated;
        }

        // ----- Bước 4: tính các đại lượng hình học từ contour cuối cùng -----
        Point[] cvContour = contour.Select(p => new Point((int)Math.Round(p.X), (int)Math.Round(p.Y))).ToArray();
        double area = Cv2.ContourArea(cvContour);
        double perimeter = Cv2.ArcLength(cvContour, closed: true);
        Rect boundingBox = Cv2.BoundingRect(cvContour);

        using Mat regionMask = new Mat(src.Size(), MatType.CV_8UC1, Scalar.Black);
        Cv2.FillPoly(regionMask, new[] { cvContour }, Scalar.White);

        // ----- Bước 5: vẽ 2 lớp output theo đúng pattern InteractiveVisualization (nền đen) + ImageMatrix (nền ảnh gốc) -----
        Mat baseImage = _outputAsColorImage.Value ? ToColor(src) : src.Clone();
        Mat overlayOnBlack = new Mat(src.Size(), MatType.CV_8UC3, Scalar.Black);

        DrawResult(baseImage, cvContour);
        DrawResult(overlayOnBlack, cvContour);

        _outImageMatrix.Value = new MatVisionImage(baseImage);
        _outInteractiveVisualization.Value = new MatVisionImage(overlayOnBlack);
        _outRegionMask.Value = new MatVisionImage(regionMask.Clone());
        _outContourPoints.Value = contour;
        _outContourArea.Value = area;
        _outContourPerimeter.Value = perimeter;
        _outBoundingBox.Value = boundingBox;

        context.Log($"ActiveContour: {n} points, {_iterations.Value} iterations, area={area:F0}px², perimeter={perimeter:F0}px");
    }

    #region Khởi tạo contour
    private List<Point2d> BuildEllipseInitialContour()
    {
        double cx = _regionCenterX.Value, cy = _regionCenterY.Value;
        double a = _regionWidth.Value / 2.0, b = _regionHeight.Value / 2.0;
        double angleRad = _regionAngle.Value * Math.PI / 180.0;
        double cosA = Math.Cos(angleRad), sinA = Math.Sin(angleRad);

        int n = _contourPointCount.Value;
        var points = new List<Point2d>(n);
        for (int i = 0; i < n; i++)
        {
            double t = 2 * Math.PI * i / n;
            double ex = a * Math.Cos(t), ey = b * Math.Sin(t); // Điểm trên ellipse chưa xoay, tâm tại gốc toạ độ
            double rx = ex * cosA - ey * sinA;                  // Xoay quanh gốc theo RegionAngle
            double ry = ex * sinA + ey * cosA;
            points.Add(new Point2d(cx + rx, cy + ry));
        }
        return points;
    }

    private List<Point2d> BuildCannyInitialContour(Mat gray, IToolContext context)
    {
        // Cắt vùng ROI hình chữ nhật bao quanh Region (không xoay, chỉ dùng làm cửa sổ tìm biên cho gọn/nhanh)
        int x = (int)Math.Round(_regionCenterX.Value - _regionWidth.Value / 2);
        int y = (int)Math.Round(_regionCenterY.Value - _regionHeight.Value / 2);
        int w = (int)Math.Round(_regionWidth.Value);
        int h = (int)Math.Round(_regionHeight.Value);
        Rect roi = ClampRect(new Rect(x, y, w, h), gray.Width, gray.Height);

        using Mat roiMat = new Mat(gray, roi);
        using Mat edges = new Mat();
        Cv2.Canny(roiMat, edges, _cannyLowThreshold.Value, _cannyHighThreshold.Value);

        Cv2.FindContours(edges, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        if (contours.Length == 0)
        {
            context.Log("ActiveContour: no Canny edge found in Region, falling back to ellipse initialization.");
            return BuildEllipseInitialContour();
        }

        // Lấy contour có diện tích lớn nhất - giả định đó là biên vật thể chính cần bám theo
        var largest = contours.OrderByDescending(c => Cv2.ContourArea(c)).First();
        var worldPoints = largest.Select(p => new Point2d(p.X + roi.X, p.Y + roi.Y)).ToList();

        return ResampleClosedPolygon(worldPoints, _contourPointCount.Value);
    }

    /// <summary>Lấy lại đúng N điểm cách đều nhau theo chu vi từ 1 đa giác khép kín bất kỳ (nội suy tuyến tính theo chiều dài cung).</summary>
    private static List<Point2d> ResampleClosedPolygon(List<Point2d> polygon, int n)
    {
        if (polygon.Count < 3) return polygon; // Đa giác suy biến, không đủ để resample có ý nghĩa

        double totalLength = 0;
        var segLengths = new List<double>();
        for (int i = 0; i < polygon.Count; i++)
        {
            double d = Distance(polygon[i], polygon[(i + 1) % polygon.Count]);
            segLengths.Add(d);
            totalLength += d;
        }

        var result = new List<Point2d>(n);
        double step = totalLength / n;
        double target = 0, accumulated = 0;
        int segIndex = 0;

        for (int i = 0; i < n; i++)
        {
            while (accumulated + segLengths[segIndex] < target && segIndex < segLengths.Count - 1)
            {
                accumulated += segLengths[segIndex];
                segIndex++;
            }
            double segT = segLengths[segIndex] < 1e-6 ? 0 : (target - accumulated) / segLengths[segIndex];
            Point2d p1 = polygon[segIndex], p2 = polygon[(segIndex + 1) % polygon.Count];
            result.Add(new Point2d(p1.X + (p2.X - p1.X) * segT, p1.Y + (p2.Y - p1.Y) * segT));
            target += step;
        }
        return result;
    }
    #endregion

    #region Tính toán năng lượng / hình học
    private static Mat ComputeNormalizedGradientMagnitude(Mat gray)
    {
        using Mat gx = new Mat(), gy = new Mat();
        Cv2.Sobel(gray, gx, MatType.CV_32F, 1, 0, 3);
        Cv2.Sobel(gray, gy, MatType.CV_32F, 0, 1, 3);

        Mat mag = new Mat();
        Cv2.Magnitude(gx, gy, mag);
        Cv2.Normalize(mag, mag, 0, 1, NormTypes.MinMax); // Đưa về [0,1] để hệ số Alpha/Beta/Eimage có cùng thang đo, dễ cân chỉnh
        return mag;
    }

    /// <summary>Nội suy song tuyến để lấy giá trị gradient tại toạ độ thực (không nguyên) - cho contour di chuyển mượt hơn thay vì nhảy theo lưới pixel.</summary>
    private static double SampleBilinear(Mat mag, double x, double y)
    {
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        int x1 = Math.Min(x0 + 1, mag.Width - 1), y1 = Math.Min(y0 + 1, mag.Height - 1);
        x0 = Math.Clamp(x0, 0, mag.Width - 1);
        y0 = Math.Clamp(y0, 0, mag.Height - 1);

        double fx = x - x0, fy = y - y0;
        double v00 = mag.At<float>(y0, x0), v10 = mag.At<float>(y0, x1);
        double v01 = mag.At<float>(y1, x0), v11 = mag.At<float>(y1, x1);

        return v00 * (1 - fx) * (1 - fy) + v10 * fx * (1 - fy) + v01 * (1 - fx) * fy + v11 * fx * fy;
    }

    private static double AverageNeighborDistance(List<Point2d> contour)
    {
        double sum = 0;
        for (int i = 0; i < contour.Count; i++)
            sum += Distance(contour[i], contour[(i + 1) % contour.Count]);
        return sum / contour.Count;
    }

    private static double Distance(Point2d a, Point2d b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    #endregion

    #region Vẽ kết quả & tiện ích ảnh
    private void DrawResult(Mat target, Point[] contour)
    {
        if (_drawFilled.Value)
            Cv2.FillPoly(target, new[] { contour }, new Scalar(0, 200, 0)); // Xanh lá - vùng object đã phân đoạn

        if (_drawContour.Value)
            Cv2.Polylines(target, new[] { contour }, isClosed: true, new Scalar(0, 0, 255), thickness: _contourThickness.Value); // Đỏ - đường viền Snake
    }

    private static Mat ToGray(Mat src)
    {
        var gray = new Mat();
        Cv2.CvtColor(src, gray, src.Channels() == 4 ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY);
        return gray;
    }

    private static Mat ToColor(Mat src)
    {
        if (src.Channels() == 3) return src.Clone();
        var color = new Mat();
        Cv2.CvtColor(src, color, src.Channels() == 1 ? ColorConversionCodes.GRAY2BGR : ColorConversionCodes.BGRA2BGR);
        return color;
    }

    private static Rect ClampRect(Rect r, int imgWidth, int imgHeight)
    {
        int x = Math.Max(0, r.X), y = Math.Max(0, r.Y);
        int right = Math.Min(imgWidth, r.X + r.Width);
        int bottom = Math.Min(imgHeight, r.Y + r.Height);
        return new Rect(x, y, Math.Max(1, right - x), Math.Max(1, bottom - y));
    }
    #endregion
}