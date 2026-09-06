// ==================== Vai trò chính:                Shape-Based Matching: so khớp hướng gradient (bất biến xoay, ánh sáng) - dùng Contour để khoanh vùng ứng viên tăng tốc
// ==================== Thành phần / Class tiêu biểu: ContourTemplateMatchingTool
// ==================== Phụ thuộc vào:                OpenCvSharp (Sobel, FindContours) + Core.Models (TemplateMatchingNCCResult, dùng chung với TemplateMatchingNCCTool) + Core.Ports + Core.Tools
// ==================== Pattern / Kỹ thuật nổi bật:   Edge-gradient Shape Model (buổi 100) + Contour Candidate Pruning + Local NMS per-contour + Global NMS theo IoU + Sub-pixel Parabolic

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Models;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;
using P2 = VisionFlow.Core.Models.Point2d;

namespace VisionFlow.Tools.Detection;

/// <summary>
/// Shape-Based Matching (buổi 100): so khớp theo HƯỚNG GRADIENT của biên dạng thay vì giá trị pixel như NCC.
/// Bất biến với thay đổi ánh sáng, chịu được che khuất một phần. Khác TemplateMatchingNCCTool ở chỗ:
/// thay vì quét brute-force toàn ảnh, dùng Contour (tách biên) để khoanh vùng ứng viên trước -> tăng tốc rất nhiều.
/// Quy trình:
/// 1. Xây Shape Model từ Template: Sobel -> lọc điểm có magnitude > MinContrast -> lưu (dx,dy,direction) quanh tâm.
/// 2. Lấy Contour ứng viên: dùng AllContours từ Tool trước (FindContoursTool) nếu có nối, hoặc tự tách bằng Canny+FindContours.
/// 3. Lọc Contour theo diện tích (MinContourArea/MaxContourArea) để loại nhiễu.
/// 4. Với mỗi Contour: lấy tâm làm điểm xuất phát, quét cục bộ trong bán kính ROIPadding x mọi góc trong [-Angle,+Angle],
///    tính score = trung bình cos(lệch hướng gradient model vs ảnh) - đúng công thức buổi 100.
///    Giữ tối đa MaxMatchesPerContour đỉnh cục bộ tốt nhất mỗi Contour.
/// 5. Gộp toàn bộ ứng viên từ mọi Contour, áp Non-Maximum Suppression toàn cục theo MaxOverlap (IoU khung bao xoay).
/// 6. (Tuỳ chọn) Tinh chỉnh Sub-pixel vị trí bằng nội suy Parabol.
/// 7. Vẽ overlay + xuất kết quả (dùng chung TemplateMatchingNCCResult với TemplateMatchingNCCTool).
/// </summary>
[ToolMetadata("ContourTemplateMatching", DisplayName = "Contour Template Matching (Shape-Based)", Category = "Detection",
    Description = "Rotation-invariant, lighting-robust shape matching using edge gradient direction, accelerated by contour candidate pruning.")]
public sealed class ContourTemplateMatchingTool : VisionTool
{
    #region 1. Ports
    private readonly InputPort<IVisionImage> _input;               // Ảnh nguồn cần tìm kiếm
    private readonly InputPort<IVisionImage> _templateInput;       // Ảnh mẫu (Template) dùng để xây Shape Model
    private readonly InputPort<P2[][]> _allContoursInput;          // (Tuỳ chọn) Nối từ FindContoursTool.AllContours để tái sử dụng, tránh tách contour 2 lần

    private readonly OutputPort<IVisionImage> _outImage;                    // Ảnh overlay kết quả
    private readonly OutputPort<IVisionImage> _outVisualization;            // Ảnh trực quan hoá riêng (bản sao độc lập của overlay) - InteractiveVisualization
    private readonly OutputPort<TemplateMatchingNCCResult> _outResult;      // Kết quả tổng hợp (dùng chung model với TemplateMatchingNCCTool)
    private readonly OutputPort<int> _outMatchCount;                        // Tổng số kết quả tìm được
    private readonly OutputPort<TemplateMatchInstance?> _outBestMatch;      // Kết quả có score cao nhất (null nếu không tìm thấy)
    private readonly OutputPort<TemplateMatchInstance[]> _outAllMatches;    // Toàn bộ kết quả dạng mảng, tiện nối thẳng vào tool khác
    #endregion

    #region 2. Parameters
    // --- Tab Contour Filtering ---
    private readonly ToolParameter<bool> _enableAreaFilter;    // Bật lọc contour theo diện tích
    private readonly ToolParameter<double> _minContourArea;    // Diện tích contour tối thiểu được xem là ứng viên hợp lệ
    private readonly ToolParameter<double> _maxContourArea;    // Diện tích contour tối đa
    private readonly ToolParameter<int> _roiPadding;           // Bán kính (px) vùng tìm kiếm cục bộ quanh tâm mỗi contour

    // --- Tab TemplateMatching ---
    private readonly ToolParameter<double> _score;                  // Ngưỡng score tối thiểu (0-1) để công nhận 1 kết quả khớp
    private readonly ToolParameter<double> _maxOverlap;             // Ngưỡng IoU tối đa cho phép giữa 2 kết quả (NMS toàn cục)
    private readonly ToolParameter<double> _angle;                  // Phạm vi góc xoay tìm kiếm: quét từ -Angle đến +Angle (độ)
    private readonly ToolParameter<bool> _subpixel;                 // Bật tinh chỉnh sub-pixel vị trí
    private readonly ToolParameter<int> _maxMatchesPerContour;      // Số đỉnh cục bộ tối đa giữ lại cho mỗi contour
    private readonly ToolParameter<double> _minContrast;            // Ngưỡng magnitude gradient tối thiểu khi xây Shape Model từ Template

    // --- Tab Display ---
    private readonly ToolParameter<bool> _drawMatches;           // Vẽ khung 4 cạnh + tâm + score cho từng kết quả
    private readonly ToolParameter<bool> _drawTemplateContours;  // Vẽ lại các Contour ứng viên đã dùng để dò (debug trực quan)
    private readonly ToolParameter<bool> _drawSearchRegion;      // Vẽ vùng tìm kiếm cục bộ (hình vuông ROIPadding) quanh mỗi contour
    #endregion

    public ContourTemplateMatchingTool()
    {
        _input = AddInput<IVisionImage>("Image", "Image (Source)");
        _templateInput = AddInput<IVisionImage>("Template", "Template Image");
        _allContoursInput = AddInput<P2[][]>("AllContours", "All Contours (optional)", optional: true);

        _outImage = AddOutput<IVisionImage>("Image", "Image (Result)");
        _outVisualization = AddOutput<IVisionImage>("InteractiveVisualization", "Interactive Visualization");
        _outResult = AddOutput<TemplateMatchingNCCResult>("Result", "ContourTemplateMatchingOutputModel");
        _outMatchCount = AddOutput<int>("MatchCount", "Match Count");
        _outBestMatch = AddOutput<TemplateMatchInstance?>("BestMatch", "Best Match");
        _outAllMatches = AddOutput<TemplateMatchInstance[]>("AllMatches", "All Matches");

        _enableAreaFilter = AddParameter<bool>("EnableAreaFilter", true, "Enable Area Filter", category: "ContourFiltering", order: 1);
        _minContourArea = AddParameter<double>("MinContourArea", 50.0, "Min Contour Area", min: 0.0, max: 1_000_000.0, category: "ContourFiltering", order: 2);
        _maxContourArea = AddParameter<double>("MaxContourArea", 100_000.0, "Max Contour Area", min: 0.0, max: 10_000_000.0, category: "ContourFiltering", order: 3);
        _roiPadding = AddParameter<int>("ROIPadding", 15, "ROI Padding / Search Radius (px)", min: 0, max: 200, category: "ContourFiltering", order: 4);

        _score = AddParameter<double>("Score", 0.7, "Min Score", min: 0.0, max: 1.0, category: "TemplateMatching", order: 1);
        _maxOverlap = AddParameter<double>("MaxOverlap", 0.2, "Max Overlap", min: 0.0, max: 1.0, category: "TemplateMatching", order: 2);
        _angle = AddParameter<double>("Angle", 30.0, "Angle Range (deg)", min: 0.0, max: 180.0, category: "TemplateMatching", order: 3);
        _subpixel = AddParameter<bool>("Subpixel", false, "Subpixel Accuracy", category: "TemplateMatching", order: 4);
        _maxMatchesPerContour = AddParameter<int>("MaxMatchesPerContour", 1, "Max Matches Per Contour", min: 1, max: 20, category: "TemplateMatching", order: 5);
        _minContrast = AddParameter<double>("MinContrast", 30.0, "Min Contrast (Template)", min: 1.0, max: 255.0, category: "TemplateMatching", order: 6);

        _drawMatches = AddParameter<bool>("DrawMatches", true, "Draw Matches", category: "Display", order: 1);
        _drawTemplateContours = AddParameter<bool>("DrawTemplateContours", false, "Draw Candidate Contours", category: "Display", order: 2);
        _drawSearchRegion = AddParameter<bool>("DrawSearchRegion", false, "Draw Search Region", category: "Display", order: 3);
    }

    protected override void OnExecute(IToolContext context)
    {
        var stopwatch = Stopwatch.StartNew();

        Mat src = _input.Value!.AsMat();
        Mat template = _templateInput.Value!.AsMat();

        if (template.Width <= 0 || template.Height <= 0)
            throw new ToolExecutionException("ContourTemplateMatching: Template không hợp lệ.");

        Mat srcGray = ToGray(src, out bool srcOwned);
        Mat templateGray = ToGray(template, out bool templateOwned);

        // ----- Bước 1: Xây Shape Model từ Template (giống hệt thuật toán Train() buổi 100) -----
        List<ShapeModelPoint> shapeModel = BuildShapeModel(templateGray, _minContrast.Value);
        double templateHalfW = templateGray.Width / 2.0;
        double templateHalfH = templateGray.Height / 2.0;

        // ----- Bước 2: Tính bản đồ hướng + độ lớn gradient CHO TOÀN ẢNH NGUỒN (dùng lại cho mọi contour/góc) -----
        ComputeGradients(srcGray, out Mat gradDir, out Mat gradMag);

        // ----- Bước 3: Lấy danh sách Contour ứng viên -----
        P2[][] contours = _allContoursInput.Value is { Length: > 0 } connected
            ? connected // Ưu tiên dùng Contour đã được FindContoursTool phía trước tách sẵn, tránh xử lý trùng lặp
            : ExtractContoursFallback(srcGray);

        // ----- Bước 4: Lọc Contour theo diện tích -----
        var candidateCenters = new List<(P2 Center, Rect BBox)>();
        foreach (var contour in contours)
        {
            if (contour.Length < 3) continue;
            var cvPoints = contour.Select(p => new Point((int)p.X, (int)p.Y)).ToArray();
            double area = Cv2.ContourArea(cvPoints);

            if (_enableAreaFilter.Value && (area < _minContourArea.Value || area > _maxContourArea.Value))
                continue;

            Rect bbox = Cv2.BoundingRect(cvPoints);
            Moments m = Cv2.Moments(cvPoints);
            if (Math.Abs(m.M00) < 1e-6) continue; // Contour suy biến (diện tích ~0), bỏ qua tránh chia cho 0
            var center = new P2(m.M10 / m.M00, m.M01 / m.M00);
            candidateCenters.Add((center, bbox));
        }

        // ----- Bước 5: Với mỗi contour ứng viên, quét cục bộ (góc xoay x dịch chuyển nhỏ) tìm đỉnh score -----
        double angleRange = Math.Max(0.0, _angle.Value);
        double angleStep = angleRange < 1e-6 ? 1.0 : Math.Max(1.0, angleRange / 15.0); // Bước góc thích ứng, giống nguyên tắc thô->tinh của Image Pyramid buổi 100
        int radius = Math.Max(0, _roiPadding.Value);
        int step = radius > 20 ? 3 : (radius > 8 ? 2 : 1); // Bước dịch chuyển thích ứng theo bán kính tìm kiếm, giữ tốc độ hợp lý

        var rawCandidates = new List<RawCandidate>();

        foreach (var (center, bbox) in candidateCenters)
        {
            var localPeaks = new List<(double Score, double Angle, double Cx, double Cy)>();

            for (double a = -angleRange; a <= angleRange + 1e-6; a += angleStep)
            {
                double angleDeg = angleRange < 1e-6 ? 0.0 : a;
                for (int ddx = -radius; ddx <= radius; ddx += Math.Max(1, step))
                    for (int ddy = -radius; ddy <= radius; ddy += Math.Max(1, step))
                    {
                        double cx = center.X + ddx, cy = center.Y + ddy;
                        double score = ComputeShapeScore(gradDir, gradMag, shapeModel, cx, cy, angleDeg);
                        if (score >= _score.Value)
                            localPeaks.Add((score, angleDeg, cx, cy));
                    }

                if (angleRange < 1e-6) break; // Không có phạm vi xoay -> chỉ 1 vòng góc 0 độ
            }

            if (localPeaks.Count == 0) continue;

            // Giữ tối đa MaxMatchesPerContour đỉnh cục bộ tốt nhất, loại các đỉnh quá gần nhau (NMS cục bộ đơn giản theo khoảng cách)
            localPeaks.Sort((x, y) => y.Score.CompareTo(x.Score));
            var acceptedLocal = new List<(double Score, double Angle, double Cx, double Cy)>();
            double minSeparation = Math.Max(3.0, Math.Min(templateHalfW, templateHalfH) * 0.5);

            foreach (var peak in localPeaks)
            {
                if (acceptedLocal.Count >= _maxMatchesPerContour.Value) break;
                bool tooClose = acceptedLocal.Any(p => Distance(p.Cx, p.Cy, peak.Cx, peak.Cy) < minSeparation);
                if (!tooClose) acceptedLocal.Add(peak);
            }

            foreach (var peak in acceptedLocal)
            {
                double fx = peak.Cx, fy = peak.Cy;
                if (_subpixel.Value)
                {
                    (fx, fy) = RefineCenterSubPixel(gradDir, gradMag, shapeModel, peak.Cx, peak.Cy, peak.Angle);
                }

                var corners = ComputeRotatedCorners(fx, fy, peak.Angle, templateHalfW, templateHalfH);
                var candBBox = BoundingRectOf(corners);
                rawCandidates.Add(new RawCandidate(peak.Score, peak.Angle, corners.LT, corners.RT, corners.RB, corners.LB, candBBox));
            }
        }

        // ----- Bước 6: Non-Maximum Suppression TOÀN CỤC theo IoU (đúng thuật toán buổi 92 áp dụng lại ở đây) -----
        rawCandidates.Sort((x, y) => y.Score.CompareTo(x.Score));
        var accepted = new List<RawCandidate>();
        foreach (var cand in rawCandidates)
        {
            bool overlapsExisting = accepted.Any(acc => ComputeIoU(cand.BBox, acc.BBox) > _maxOverlap.Value);
            if (!overlapsExisting) accepted.Add(cand);
        }

        // ----- Bước 7: Đóng gói kết quả -----
        var matches = new List<TemplateMatchInstance>();
        for (int i = 0; i < accepted.Count; i++)
        {
            var c = accepted[i];
            P2 matchCenter = new P2(
                (c.LeftTop.X + c.RightTop.X + c.RightBottom.X + c.LeftBottom.X) / 4.0,
                (c.LeftTop.Y + c.RightTop.Y + c.RightBottom.Y + c.LeftBottom.Y) / 4.0);
            matches.Add(new TemplateMatchInstance(c.LeftTop, c.RightTop, c.RightBottom, c.LeftBottom,
                matchCenter, c.Score, c.AngleDeg, i));
        }

        // ----- Bước 8: Vẽ overlay -----
        Mat overlay = new Mat();
        Cv2.CvtColor(srcGray, overlay, ColorConversionCodes.GRAY2BGR);

        if (_drawSearchRegion.Value)
        {
            foreach (var (center, _) in candidateCenters)
                Cv2.Rectangle(overlay,
                    new Point((int)(center.X - radius), (int)(center.Y - radius)),
                    new Point((int)(center.X + radius), (int)(center.Y + radius)),
                    new Scalar(80, 80, 80), 1, LineTypes.AntiAlias);
        }

        if (_drawTemplateContours.Value)
        {
            var cvContours = contours.Select(ct => ct.Select(p => new Point((int)p.X, (int)p.Y)).ToArray()).ToArray();
            Cv2.DrawContours(overlay, cvContours, -1, new Scalar(128, 128, 0), 1, LineTypes.AntiAlias);
        }

        if (_drawMatches.Value)
        {
            for (int i = 0; i < matches.Count; i++)
            {
                var m = matches[i];
                Scalar color = PickDistinctColor(i);
                var pts = new[]
                {
                    new Point((int)m.LeftTop.X, (int)m.LeftTop.Y),
                    new Point((int)m.RightTop.X, (int)m.RightTop.Y),
                    new Point((int)m.RightBottom.X, (int)m.RightBottom.Y),
                    new Point((int)m.LeftBottom.X, (int)m.LeftBottom.Y),
                };
                Cv2.Polylines(overlay, new[] { pts }, true, color, 2, LineTypes.AntiAlias);
                Cv2.Circle(overlay, (int)m.Center.X, (int)m.Center.Y, 3, color, -1, LineTypes.AntiAlias);
                Cv2.PutText(overlay, $"#{i} {m.MatchScore:F2} {m.MatchedAngle:F0}deg",
                    new Point((int)m.LeftTop.X, (int)m.LeftTop.Y - 6),
                    HersheyFonts.HersheySimplex, 0.45, color, 1, LineTypes.AntiAlias);
            }
        }

        stopwatch.Stop();

        var result = new TemplateMatchingNCCResult
        {
            Matches = matches,
            Success = matches.Count > 0,
            ExecutionTimeMs = stopwatch.Elapsed.TotalMilliseconds,
            Judge = matches.Count > 0 ? Judge.OK : Judge.NG,
        };

        // ----- Bước 9: Xuất kết quả ra các cổng Output -----
        // finalImage "cho đi" Mat overlay vào Output -> KHÔNG được Dispose(overlay) sau đó (bài học từ HoughCircleDetectionTool)
        _outImage.Value = new MatVisionImage(overlay);
        _outVisualization.Value = new MatVisionImage(overlay.Clone()); // Bản sao ĐỘC LẬP cho cổng riêng, tránh 2 port cùng trỏ chung 1 Mat
        _outResult.Value = result;
        _outMatchCount.Value = matches.Count;
        _outBestMatch.Value = matches.Count > 0 ? matches[0] : (TemplateMatchInstance?)null; // matches đã sort giảm dần theo Score ở Bước 6
        _outAllMatches.Value = matches.ToArray();

        // ----- Bước 10: Dọn dẹp tài nguyên Mat trung gian (KHÔNG đụng tới overlay - đã "cho đi" ở Bước 9) -----
        gradDir.Dispose();
        gradMag.Dispose();
        if (templateOwned) templateGray.Dispose();
        if (srcOwned) srcGray.Dispose();

        context.Log($"ContourTemplateMatching: {matches.Count} match(es) từ {candidateCenters.Count} contour ứng viên, {stopwatch.Elapsed.TotalMilliseconds:F1}ms.");
    }

    #region 3. Helpers

    private readonly struct ShapeModelPoint
    {
        public readonly double Dx, Dy, Direction;
        public ShapeModelPoint(double dx, double dy, double direction) { Dx = dx; Dy = dy; Direction = direction; }
    }

    private readonly struct RawCandidate
    {
        public readonly double Score, AngleDeg;
        public readonly P2 LeftTop, RightTop, RightBottom, LeftBottom;
        public readonly Rect BBox;
        public RawCandidate(double score, double angleDeg, P2 lt, P2 rt, P2 rb, P2 lb, Rect bbox)
        { Score = score; AngleDeg = angleDeg; LeftTop = lt; RightTop = rt; RightBottom = rb; LeftBottom = lb; BBox = bbox; }
    }

    /// <summary>Xây Shape Model từ Template: Sobel gradient -> lọc theo MinContrast -> lưu (dx,dy,direction) quanh tâm. Y hệt Train() buổi 100.</summary>
    private static List<ShapeModelPoint> BuildShapeModel(Mat templateGray, double minContrast)
    {
        ComputeGradients(templateGray, out Mat dir, out Mat mag);
        double cx = templateGray.Width / 2.0, cy = templateGray.Height / 2.0;

        var points = new List<ShapeModelPoint>();
        for (int y = 0; y < templateGray.Height; y++)
            for (int x = 0; x < templateGray.Width; x++)
            {
                double m = mag.At<float>(y, x);
                if (m < minContrast) continue;
                double d = dir.At<float>(y, x);
                points.Add(new ShapeModelPoint(x - cx, y - cy, d));
            }

        dir.Dispose();
        mag.Dispose();
        return points;
    }

    /// <summary>Tính bản đồ hướng gradient (radian, -pi..pi) và độ lớn gradient bằng Sobel X/Y cho toàn ảnh.</summary>
    private static void ComputeGradients(Mat gray, out Mat direction, out Mat magnitude)
    {
        using Mat gx = new Mat();
        using Mat gy = new Mat();
        Cv2.Sobel(gray, gx, MatType.CV_32F, 1, 0, ksize: 3);
        Cv2.Sobel(gray, gy, MatType.CV_32F, 0, 1, ksize: 3);

        magnitude = new Mat();
        Cv2.Magnitude(gx, gy, magnitude);

        direction = new Mat();
        Cv2.Phase(gx, gy, direction, angleInDegrees: false); // Cv2.Phase trả về góc trong khoảng 0 tới 2*pi - dùng trực tiếp với cos() vẫn đúng vì cos tuần hoàn 2*pi
    }

    /// <summary>
    /// Score = trung bình cos(hiệu hướng gradient giữa Model đã xoay và ảnh) tại vị trí (cx,cy), góc angleDeg.
    /// Đúng công thức Search() buổi 100: Score = (1/N) * Sigma cos(theta_model(i) - theta_image(x+dx, y+dy)).
    /// </summary>
    private static double ComputeShapeScore(Mat gradDir, Mat gradMag, List<ShapeModelPoint> model, double cx, double cy, double angleDeg)
    {
        if (model.Count == 0) return 0.0;

        double rad = angleDeg * Math.PI / 180.0;
        double cosA = Math.Cos(rad), sinA = Math.Sin(rad);
        int w = gradDir.Width, h = gradDir.Height;

        double scoreSum = 0.0;
        int validCount = 0;

        foreach (var pt in model)
        {
            double rx = pt.Dx * cosA - pt.Dy * sinA;
            double ry = pt.Dx * sinA + pt.Dy * cosA;
            int px = (int)(cx + rx), py = (int)(cy + ry);
            if (px < 0 || px >= w || py < 0 || py >= h) continue;
            if (gradMag.At<float>(py, px) < 5) continue; // Bỏ qua điểm ảnh vùng phẳng (không có cạnh thật) để tránh nhiễu score

            double imgDir = gradDir.At<float>(py, px);
            double diff = (pt.Direction + rad) - imgDir;
            scoreSum += Math.Cos(diff);
            validCount++;
        }

        if (validCount < model.Count * 0.5) return 0.0; // Ít hơn 50% điểm model có dữ liệu hợp lệ -> coi như không khớp (che khuất quá nhiều)
        return scoreSum / validCount;
    }

    /// <summary>Nội suy Parabol riêng theo trục X và Y quanh vị trí đỉnh hiện tại, dùng chính ComputeShapeScore làm hàm mục tiêu.</summary>
    private static (double X, double Y) RefineCenterSubPixel(Mat gradDir, Mat gradMag, List<ShapeModelPoint> model, double cx, double cy, double angleDeg)
    {
        double sxm = ComputeShapeScore(gradDir, gradMag, model, cx - 1, cy, angleDeg);
        double sxc = ComputeShapeScore(gradDir, gradMag, model, cx, cy, angleDeg);
        double sxp = ComputeShapeScore(gradDir, gradMag, model, cx + 1, cy, angleDeg);
        double sym = ComputeShapeScore(gradDir, gradMag, model, cx, cy - 1, angleDeg);
        double syp = ComputeShapeScore(gradDir, gradMag, model, cx, cy + 1, angleDeg);

        double dx = ParabolicOffset(sxm, sxc, sxp);
        double dy = ParabolicOffset(sym, sxc, syp);
        return (cx + dx, cy + dy);
    }

    private static double ParabolicOffset(double sMinus1, double sCenter, double sPlus1)
    {
        double denom = sMinus1 - 2 * sCenter + sPlus1;
        if (Math.Abs(denom) < 1e-9) return 0.0;
        double offset = 0.5 * (sMinus1 - sPlus1) / denom;
        return Math.Clamp(offset, -1.0, 1.0);
    }

    /// <summary>Xoay 4 góc của Template (quanh tâm chính nó) theo angleDeg rồi dịch tới vị trí (fx,fy) tìm được trong ảnh nguồn.</summary>
    private static (P2 LT, P2 RT, P2 RB, P2 LB) ComputeRotatedCorners(double fx, double fy, double angleDeg, double halfW, double halfH)
    {
        double rad = angleDeg * Math.PI / 180.0;
        double cosA = Math.Cos(rad), sinA = Math.Sin(rad);

        P2 Rot(double dx, double dy) => new P2(fx + dx * cosA - dy * sinA, fy + dx * sinA + dy * cosA);

        return (Rot(-halfW, -halfH), Rot(halfW, -halfH), Rot(halfW, halfH), Rot(-halfW, halfH));
    }

    private static Rect BoundingRectOf((P2 LT, P2 RT, P2 RB, P2 LB) c)
    {
        double minX = new[] { c.LT.X, c.RT.X, c.RB.X, c.LB.X }.Min();
        double maxX = new[] { c.LT.X, c.RT.X, c.RB.X, c.LB.X }.Max();
        double minY = new[] { c.LT.Y, c.RT.Y, c.RB.Y, c.LB.Y }.Min();
        double maxY = new[] { c.LT.Y, c.RT.Y, c.RB.Y, c.LB.Y }.Max();
        return new Rect((int)minX, (int)minY, Math.Max(1, (int)(maxX - minX)), Math.Max(1, (int)(maxY - minY)));
    }

    private static double ComputeIoU(Rect a, Rect b)
    {
        Rect inter = a.Intersect(b);
        double interArea = inter.Width > 0 && inter.Height > 0 ? (double)inter.Width * inter.Height : 0.0;
        if (interArea <= 0) return 0.0;
        double unionArea = (double)a.Width * a.Height + (double)b.Width * b.Height - interArea;
        return unionArea > 0 ? interArea / unionArea : 0.0;
    }

    private static double Distance(double x1, double y1, double x2, double y2) => Math.Sqrt((x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2));

    /// <summary>Khi không có AllContours nối sẵn từ Tool trước, tự tách contour bằng Canny + FindContours để dùng làm ứng viên.</summary>
    private static P2[][] ExtractContoursFallback(Mat gray)
    {
        using Mat edges = new Mat();
        Cv2.Canny(gray, edges, 50, 150);
        Cv2.FindContours(edges, out Point[][] cvContours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        return cvContours.Select(ct => ct.Select(p => new P2(p.X, p.Y)).ToArray()).ToArray();
    }

    private static Mat ToGray(Mat src, out bool owned)
    {
        if (src.Channels() == 1) { owned = false; return src; }
        Mat gray = new Mat();
        Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
        owned = true;
        return gray;
    }

    private static Scalar PickDistinctColor(int index)
    {
        Scalar[] palette =
        {
            new Scalar(0, 255, 255), new Scalar(0, 255, 0), new Scalar(255, 0, 0),
            new Scalar(0, 165, 255), new Scalar(255, 0, 255), new Scalar(255, 255, 0),
        };
        return palette[index % palette.Length];
    }

    #endregion
}