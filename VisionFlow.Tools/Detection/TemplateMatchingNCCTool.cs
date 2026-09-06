// ==================== Vai trò chính:                Tìm kiếm 1 hoặc nhiều vật thể giống ảnh mẫu (Template) trong ảnh nguồn bằng NCC (Normalized Cross-Correlation)
// ==================== Thành phần / Class tiêu biểu: TemplateMatchingNCCTool
// ==================== Phụ thuộc vào:                OpenCvSharp (Cv2.MatchTemplate) + Core.Models (TemplateMatchingNCCResult) + Core.Ports + Core.Tools + Tools.Imaging
// ==================== Pattern / Kỹ thuật nổi bật:   Multi-angle template rotation + Greedy NMS theo IoU (đúng thuật toán buổi 92) + Sub-pixel Parabolic Interpolation (buổi 100)

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Models;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging; // Chứa extension method .AsMat()
using P2 = VisionFlow.Core.Models.Point2d;

namespace VisionFlow.Tools.Detection;

/// <summary>
/// Công cụ khớp mẫu cổ điển theo phương pháp NCC (Normalized Cross-Correlation):
/// "Trượt" ảnh mẫu (Template) qua từng vị trí trên ảnh nguồn, tính hệ số tương quan chuẩn hoá tại mỗi vị trí,
/// những nơi có hệ số càng gần 1.0 thì càng giống mẫu. Nhanh và chính xác cao khi vật thể có hình dạng/độ sáng cố định.
/// Quy trình xử lý:
/// 1. Chuẩn hoá ảnh nguồn + Template về hệ xám (áp dụng Invert nếu cấu hình yêu cầu tìm bản âm).
/// 2. Với mỗi góc xoay trong khoảng [-Angle, +Angle]: xoay Template (mở rộng canvas để không cắt góc),
///    chạy Cv2.MatchTemplate(CCoeffNormed), quét toàn bộ ma trận score thu thập các ứng viên vượt ngưỡng Score.
/// 3. Gom toàn bộ ứng viên từ mọi góc xoay lại, sắp xếp theo Score giảm dần.
/// 4. Áp dụng Non-Maximum Suppression (NMS): duyệt tuần tự, giữ ứng viên nếu độ chồng lấn (IoU khung bao)
///    với mọi kết quả đã chọn trước đó không vượt quá MaxOverlap. Lặp lại tới khi hết ứng viên hoặc đủ MaxPos.
/// 5. (Tuỳ chọn) Tinh chỉnh sub-pixel vị trí X, Y bằng nội suy Parabol quanh đỉnh tương quan.
/// 6. Vẽ overlay (khung 4 cạnh + tâm + score, mỗi kết quả 1 màu khác nhau) và xuất kết quả.
/// </summary>
[ToolMetadata("TemplateMatchingNCC", DisplayName = "Template Matching (NCC)", Category = "Detection",
    Description = "Classic multi-angle, multi-instance Normalized Cross-Correlation template matching.")]
public sealed class TemplateMatchingNCCTool : VisionTool
{
    #region 1. Khai báo các Cổng truyền nhận dữ liệu (Ports)
    private readonly InputPort<IVisionImage> _input; // Cổng vào bắt buộc: ảnh nguồn (ảnh lớn) cần tìm kiếm bên trong
    private readonly InputPort<IVisionImage> _templateInput; // Cổng vào bắt buộc: ảnh mẫu (Template) đã cắt sẵn

    private readonly OutputPort<IVisionImage> _outImage; // Cổng ra: ảnh nguồn đã vẽ đè khung/tâm/score tại mỗi vị trí khớp
    private readonly OutputPort<TemplateMatchingNCCResult> _outResult; // Cổng ra: kết quả tổng hợp (Matches, Success, ExecutionTimeMs)
    private readonly OutputPort<int> _outMatchesCount; // Cổng ra: tổng số kết quả tìm được (MatchesCount)
    #endregion

    #region 2. Khai báo các Tham số cấu hình (Parameters)
    // --- Tab Detection ---
    private readonly ToolParameter<int> _maxPos; // Số lượng vị trí khớp mẫu tối đa được phép xuất ra
    private readonly ToolParameter<double> _maxOverlap; // Tỷ lệ chồng lấn (IoU) tối đa cho phép giữa 2 kết quả (cơ chế NMS)
    private readonly ToolParameter<double> _angle; // Phạm vi góc xoay tìm kiếm: quét từ -Angle đến +Angle (độ)

    // --- Tab Threshold ---
    private readonly ToolParameter<double> _score; // Điểm số NCC tối thiểu để công nhận một vị trí là kết quả khớp

    // --- Tab Advanced ---
    private readonly ToolParameter<bool> _subpixel; // Bật độ chính xác sub-pixel cho toạ độ kết quả
    private readonly ToolParameter<bool> _invert; // Đảo ngược cường độ sáng/tối của Template trước khi so khớp

    // --- Display (không có trong mục lục docx nhưng cần để bật/tắt vẽ, đồng bộ style các Tool khác) ---
    private readonly ToolParameter<bool> _drawMatches; // Vẽ khung/tâm/score của các kết quả tìm được lên ảnh output
    #endregion

    public TemplateMatchingNCCTool()
    {
        _input = AddInput<IVisionImage>("Image", "Image (Source)");
        _templateInput = AddInput<IVisionImage>("Template", "Template Image");

        _outImage = AddOutput<IVisionImage>("Image", "Image (Result)");
        _outResult = AddOutput<TemplateMatchingNCCResult>("Result", "TemplateMatchingNCCOutputModel");
        _outMatchesCount = AddOutput<int>("MatchesCount", "Matches Count");

        _maxPos = AddParameter<int>("MaxPos", 10, "Max Positions", min: 1, max: 500, category: "Detection", order: 1); // Mặc định giữ tối đa 10 kết quả tốt nhất
        _maxOverlap = AddParameter<double>("MaxOverlap", 0.1, "Max Overlap", min: 0.0, max: 1.0, category: "Detection", order: 2); // Mặc định 10% chồng lấn, đúng khuyến nghị trong tài liệu
        _angle = AddParameter<double>("Angle", 10.0, "Angle Range (deg)", min: 0.0, max: 180.0, category: "Detection", order: 3); // Mặc định quét ±10 độ

        _score = AddParameter<double>("Score", 0.8, "Min Score", min: -1.0, max: 1.0, category: "Threshold", order: 1); // Mặc định yêu cầu giống mẫu tối thiểu 80%

        _subpixel = AddParameter<bool>("Subpixel", false, "Subpixel Accuracy", category: "Advanced", order: 1);
        _invert = AddParameter<bool>("Invert", false, "Invert Polarity", category: "Advanced", order: 2);

        _drawMatches = AddParameter<bool>("DrawMatches", true, "Draw Matches", category: "Display", order: 1);
    }

    protected override void OnExecute(IToolContext context)
    {
        var stopwatch = Stopwatch.StartNew(); // Đo ExecutionTimeMs ngay từ đầu, đúng yêu cầu Output

        Mat src = _input.Value!.AsMat();
        Mat template = _templateInput.Value!.AsMat();

        if (template.Width <= 0 || template.Height <= 0 || template.Width > src.Width || template.Height > src.Height)
            throw new ToolExecutionException("TemplateMatchingNCC: kích thước Template không hợp lệ hoặc lớn hơn ảnh nguồn.");

        // Bước 1: Chuẩn hoá cả 2 ảnh về hệ xám (NCC hoạt động tốt và ổn định nhất trên ảnh 1 kênh)
        Mat srcGray = ToGray(src, out bool srcOwned);
        Mat templateGray = ToGray(template, out bool templateOwned);

        // Áp dụng Invert nếu cấu hình yêu cầu tìm bản âm bản của Template (đảo trắng<->đen)
        Mat templateForMatch = templateGray;
        bool invertedOwned = false;
        if (_invert.Value)
        {
            templateForMatch = new Mat();
            Cv2.BitwiseNot(templateGray, templateForMatch);
            invertedOwned = true;
        }

        // Bước 2: Quét NCC ở nhiều góc xoay, thu thập toàn bộ ứng viên vượt ngưỡng Score
        double angleRange = Math.Max(0.0, _angle.Value);
        // Bước góc quét thích ứng: phạm vi càng rộng thì bước càng thô để giữ tốc độ hợp lý (giống nguyên tắc "Image Pyramid" đã học ở buổi 100 - tinh chỉnh thô trước, chính xác sau)
        double angleStep = angleRange < 1e-6 ? 1.0 : Math.Max(1.0, angleRange / 15.0);

        var rawCandidates = new List<RawCandidate>();

        for (double a = -angleRange; a <= angleRange + 1e-6; a += angleStep)
        {
            double angleDeg = angleRange < 1e-6 ? 0.0 : a; // Nếu Angle=0 chỉ chạy đúng 1 lần ở góc 0 độ

            (Mat rotatedTemplate, Point2f[] cornersInCanvas) = RotateTemplateWithCanvas(templateForMatch, angleDeg);

            if (rotatedTemplate.Width > srcGray.Width || rotatedTemplate.Height > srcGray.Height)
            {
                rotatedTemplate.Dispose();
                if (angleRange < 1e-6) break;
                continue; // Template xoay bị phình to hơn cả ảnh nguồn ở góc này -> bỏ qua góc này
            }

            using Mat scoreMap = new Mat();
            Cv2.MatchTemplate(srcGray, rotatedTemplate, scoreMap, TemplateMatchModes.CCoeffNormed);

            CollectCandidates(scoreMap, _score.Value, angleDeg, rotatedTemplate.Size(), cornersInCanvas,
                _subpixel.Value, rawCandidates);

            rotatedTemplate.Dispose();

            if (angleRange < 1e-6) break; // Không có phạm vi xoay -> chỉ chạy 1 vòng lặp duy nhất
        }

        // Bước 3: Sắp xếp toàn bộ ứng viên (từ mọi góc xoay) theo Score giảm dần
        rawCandidates.Sort((x, y) => y.Score.CompareTo(x.Score));

        // Bước 4: Non-Maximum Suppression - giữ lại kết quả tốt nhất, loại bỏ các ứng viên chồng lấn quá nhiều (đúng thuật toán buổi 92)
        var accepted = new List<RawCandidate>();
        foreach (var cand in rawCandidates)
        {
            if (accepted.Count >= _maxPos.Value) break; // Đã đủ số lượng kết quả tối đa yêu cầu

            bool overlapsExisting = accepted.Any(acc => ComputeIoU(cand.BBox, acc.BBox) > _maxOverlap.Value);
            if (!overlapsExisting)
                accepted.Add(cand);
        }

        // Bước 5: Đóng gói kết quả thành TemplateMatchInstance
        var matches = new List<TemplateMatchInstance>();
        for (int i = 0; i < accepted.Count; i++)
        {
            var c = accepted[i];
            P2 center = new P2(
                (c.LeftTop.X + c.RightTop.X + c.RightBottom.X + c.LeftBottom.X) / 4.0,
                (c.LeftTop.Y + c.RightTop.Y + c.RightBottom.Y + c.LeftBottom.Y) / 4.0);

            matches.Add(new TemplateMatchInstance(
                LeftTop: c.LeftTop, RightTop: c.RightTop, RightBottom: c.RightBottom, LeftBottom: c.LeftBottom,
                Center: center, MatchScore: c.Score, MatchedAngle: c.AngleDeg, Index: i));
        }

        // Bước 6: Vẽ overlay kết quả
        Mat overlay = new Mat();
        Cv2.CvtColor(srcGray, overlay, ColorConversionCodes.GRAY2BGR);

        if (_drawMatches.Value)
        {
            for (int i = 0; i < matches.Count; i++)
            {
                var m = matches[i];
                Scalar color = PickDistinctColor(i); // Mỗi kết quả 1 màu khác nhau để dễ phân biệt, đúng yêu cầu tài liệu

                var pts = new[]
                {
                    new Point((int)m.LeftTop.X, (int)m.LeftTop.Y),
                    new Point((int)m.RightTop.X, (int)m.RightTop.Y),
                    new Point((int)m.RightBottom.X, (int)m.RightBottom.Y),
                    new Point((int)m.LeftBottom.X, (int)m.LeftBottom.Y),
                };
                Cv2.Polylines(overlay, new[] { pts }, true, color, 2, LineTypes.AntiAlias);
                Cv2.Circle(overlay, (int)m.Center.X, (int)m.Center.Y, 3, color, -1, LineTypes.AntiAlias);
                Cv2.PutText(overlay, $"#{i} {m.MatchScore:F2}", new Point((int)m.LeftTop.X, (int)m.LeftTop.Y - 6),
                    HersheyFonts.HersheySimplex, 0.5, color, 1, LineTypes.AntiAlias);
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

        // Bước 7: Đẩy kết quả ra các cổng Output
        _outImage.Value = new MatVisionImage(overlay);
        _outResult.Value = result;
        _outMatchesCount.Value = matches.Count;

        // Bước 8: Dọn dẹp tài nguyên Mat trung gian
        if (invertedOwned) templateForMatch.Dispose();
        if (templateOwned) templateGray.Dispose();
        if (srcOwned) srcGray.Dispose();

        context.Log($"TemplateMatchingNCC: {matches.Count} match(es) found in {stopwatch.Elapsed.TotalMilliseconds:F1}ms.");
    }

    #region 3. Các hàm hỗ trợ thuật toán (Helpers)

    /// <summary>Cấu trúc tạm lưu 1 ứng viên khớp mẫu trước khi qua bước NMS.</summary>
    private readonly struct RawCandidate
    {
        public readonly double Score;
        public readonly double AngleDeg;
        public readonly P2 LeftTop, RightTop, RightBottom, LeftBottom;
        public readonly Rect BBox; // Khung bao (axis-aligned) dùng để tính IoU nhanh trong bước NMS

        public RawCandidate(double score, double angleDeg, P2 lt, P2 rt, P2 rb, P2 lb, Rect bbox)
        {
            Score = score; AngleDeg = angleDeg;
            LeftTop = lt; RightTop = rt; RightBottom = rb; LeftBottom = lb;
            BBox = bbox;
        }
    }

    /// <summary>
    /// Tự áp ma trận Affine 2x3 (dạng OpenCV: hàng 0 = [m00,m01,m02], hàng 1 = [m10,m11,m12]) vào từng điểm.
    /// Dùng thay cho Cv2.Transform vì overload của hàm đó không nhận thẳng mảng Point2f[] làm tham số nguồn/đích.
    /// Công thức: x' = m00*x + m01*y + m02 ; y' = m10*x + m11*y + m12
    /// </summary>
    private static Point2f[] TransformPoints(Point2f[] points, Mat affine2x3)
    {
        double m00 = affine2x3.At<double>(0, 0), m01 = affine2x3.At<double>(0, 1), m02 = affine2x3.At<double>(0, 2);
        double m10 = affine2x3.At<double>(1, 0), m11 = affine2x3.At<double>(1, 1), m12 = affine2x3.At<double>(1, 2);

        var result = new Point2f[points.Length];
        for (int i = 0; i < points.Length; i++)
        {
            double x = points[i].X, y = points[i].Y;
            result[i] = new Point2f((float)(m00 * x + m01 * y + m02), (float)(m10 * x + m11 * y + m12));
        }
        return result;
    }

    /// <summary>Convert ảnh bất kỳ về hệ xám; trả về owned=true nếu hàm này tự tạo ra Mat mới (cần Dispose sau khi dùng xong).</summary>
    private static Mat ToGray(Mat src, out bool owned)
    {
        if (src.Channels() == 1) { owned = false; return src; }
        Mat gray = new Mat();
        Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
        owned = true;
        return gray;
    }

    /// <summary>
    /// Xoay Template quanh tâm của chính nó với 1 canvas được MỞ RỘNG đủ lớn để không bị cắt góc,
    /// đồng thời trả về toạ độ 4 góc gốc của Template SAU khi xoay, tính trong hệ toạ độ của canvas mới.
    /// </summary>
    private static (Mat RotatedCanvas, Point2f[] CornersInCanvas) RotateTemplateWithCanvas(Mat templateGray, double angleDeg)
    {
        int w = templateGray.Width, h = templateGray.Height;

        if (Math.Abs(angleDeg) < 1e-6)
        {
            // Góc 0 độ: không cần xoay, canvas chính là Template gốc, 4 góc giữ nguyên toạ độ (0,0)-(w,0)-(w,h)-(0,h)
            var corners0 = new[]
            {
                new Point2f(0, 0), new Point2f(w, 0), new Point2f(w, h), new Point2f(0, h)
            };
            return (templateGray.Clone(), corners0);
        }

        double rad = angleDeg * Math.PI / 180.0;
        double cos = Math.Abs(Math.Cos(rad)), sin = Math.Abs(Math.Sin(rad));
        int newW = (int)Math.Ceiling(w * cos + h * sin); // Kích thước canvas mới đủ rộng để chứa toàn bộ Template sau khi xoay
        int newH = (int)Math.Ceiling(w * sin + h * cos);

        Point2f center = new Point2f(w / 2f, h / 2f);
        using Mat rotMat = Cv2.GetRotationMatrix2D(center, angleDeg, 1.0);
        // Cv2.GetRotationMatrix2D chỉ xoay tại chỗ quanh tâm gốc -> cộng thêm độ dịch để đưa tâm về giữa canvas MỚI (rộng hơn)
        rotMat.Set(0, 2, rotMat.At<double>(0, 2) + (newW / 2.0 - center.X));
        rotMat.Set(1, 2, rotMat.At<double>(1, 2) + (newH / 2.0 - center.Y));

        Mat canvas = new Mat();
        Cv2.WarpAffine(templateGray, canvas, rotMat, new Size(newW, newH),
            InterpolationFlags.Linear, BorderTypes.Replicate); // Replicate biên để tránh viền đen giả gây nhiễu điểm số NCC ở rìa

        // Biến đổi 4 góc gốc của Template qua đúng ma trận Affine vừa dùng để warp canvas
        Point2f[] originalCorners = { new Point2f(0, 0), new Point2f(w, 0), new Point2f(w, h), new Point2f(0, h) };
        Point2f[] transformedCorners = TransformPoints(originalCorners, rotMat); // Tự nhân ma trận thay vì Cv2.Transform (API này không nhận thẳng mảng Point2f[])

        return (canvas, transformedCorners);
    }

    /// <summary>
    /// Quét toàn bộ ma trận điểm số NCC (scoreMap), lặp lại: tìm điểm cực đại hiện tại -> nếu vượt ngưỡng thì
    /// ghi nhận ứng viên rồi "xoá" (gán -1) 1 vùng lân cận quanh điểm đó để không đếm trùng cùng 1 vật nhiều lần
    /// ngay trong nội bộ 1 góc xoay -> lặp lại tới khi không còn điểm nào vượt ngưỡng.
    /// </summary>
    private static void CollectCandidates(Mat scoreMap, double minScore, double angleDeg, Size templateSize,
        Point2f[] cornersInCanvas, bool subpixel, List<RawCandidate> output)
    {
        Mat working = scoreMap.Clone(); // Làm việc trên bản sao để có thể "dập" (suppress) các đỉnh đã xử lý mà không ảnh hưởng scoreMap gốc
        int suppressRadiusX = Math.Max(2, templateSize.Width / 4);
        int suppressRadiusY = Math.Max(2, templateSize.Height / 4);

        while (true)
        {
            Cv2.MinMaxLoc(working, out _, out double maxVal, out _, out OpenCvSharp.Point maxLoc);
            if (maxVal < minScore) break; // Không còn ứng viên nào đủ tốt trong ảnh này -> dừng

            double px = maxLoc.X, py = maxLoc.Y;

            // Tinh chỉnh sub-pixel (tuỳ chọn): nội suy Parabol riêng theo trục X và trục Y quanh đỉnh tương quan
            if (subpixel)
            {
                (px, py) = RefinePeakSubPixel(working, maxLoc, px, py);
            }

            // Toạ độ 4 góc thực tế trong ảnh nguồn = vị trí match (top-left của canvas Template) + toạ độ góc trong canvas
            P2 lt = new P2(px + cornersInCanvas[0].X, py + cornersInCanvas[0].Y);
            P2 rt = new P2(px + cornersInCanvas[1].X, py + cornersInCanvas[1].Y);
            P2 rb = new P2(px + cornersInCanvas[2].X, py + cornersInCanvas[2].Y);
            P2 lb = new P2(px + cornersInCanvas[3].X, py + cornersInCanvas[3].Y);

            double minX = new[] { lt.X, rt.X, rb.X, lb.X }.Min();
            double maxX = new[] { lt.X, rt.X, rb.X, lb.X }.Max();
            double minY = new[] { lt.Y, rt.Y, rb.Y, lb.Y }.Min();
            double maxY = new[] { lt.Y, rt.Y, rb.Y, lb.Y }.Max();
            var bbox = new Rect((int)minX, (int)minY, Math.Max(1, (int)(maxX - minX)), Math.Max(1, (int)(maxY - minY)));

            output.Add(new RawCandidate(maxVal, angleDeg, lt, rt, rb, lb, bbox));

            // "Dập" vùng lân cận quanh đỉnh vừa lấy để vòng lặp tiếp theo tìm đỉnh khác, tránh lấy trùng cùng 1 vị trí
            int x0 = Math.Max(0, maxLoc.X - suppressRadiusX);
            int y0 = Math.Max(0, maxLoc.Y - suppressRadiusY);
            int x1 = Math.Min(working.Width - 1, maxLoc.X + suppressRadiusX);
            int y1 = Math.Min(working.Height - 1, maxLoc.Y + suppressRadiusY);
            using Mat roi = new Mat(working, new Rect(x0, y0, x1 - x0 + 1, y1 - y0 + 1));
            roi.SetTo(-1.0); // NCC score luôn <= 1.0 -> gán -1 đảm bảo vùng này không bao giờ được chọn lại
        }

        working.Dispose();
    }

    /// <summary>
    /// Nội suy Parabol 1D áp dụng riêng cho trục X và trục Y quanh đỉnh tương quan nguyên (maxLoc),
    /// cho ra toạ độ lẻ như (120.34, 88.12) thay vì chỉ dừng ở số nguyên pixel - cùng công thức đã học ở buổi 100.
    /// </summary>
    private static (double X, double Y) RefinePeakSubPixel(Mat scoreMap, OpenCvSharp.Point maxLoc, double fallbackX, double fallbackY)
    {
        int x = maxLoc.X, y = maxLoc.Y;
        if (x <= 0 || y <= 0 || x >= scoreMap.Width - 1 || y >= scoreMap.Height - 1)
            return (fallbackX, fallbackY); // Đỉnh nằm sát biên ma trận, không đủ 2 lân cận để nội suy -> giữ nguyên toạ độ pixel

        double left = scoreMap.At<float>(y, x - 1), center = scoreMap.At<float>(y, x), right = scoreMap.At<float>(y, x + 1);
        double up = scoreMap.At<float>(y - 1, x), down = scoreMap.At<float>(y + 1, x);

        double dx = ParabolicOffset(left, center, right);
        double dy = ParabolicOffset(up, center, down);

        return (x + dx, y + dy);
    }

    /// <summary>Công thức nội suy Parabol chuẩn: offset = 0.5*(sMinus1 - sPlus1) / (sMinus1 - 2*sCenter + sPlus1).</summary>
    private static double ParabolicOffset(double sMinus1, double sCenter, double sPlus1)
    {
        double denom = sMinus1 - 2 * sCenter + sPlus1;
        if (Math.Abs(denom) < 1e-9) return 0.0;
        double offset = 0.5 * (sMinus1 - sPlus1) / denom;
        return Math.Clamp(offset, -1.0, 1.0); // Chặn trong khoảng hợp lý, tránh nhảy lố do nhiễu
    }

    /// <summary>Tính IoU (Intersection over Union) giữa 2 khung bao chữ nhật - dùng cho bước Non-Maximum Suppression.</summary>
    private static double ComputeIoU(Rect a, Rect b)
    {
        Rect inter = a.Intersect(b);
        double interArea = inter.Width > 0 && inter.Height > 0 ? (double)inter.Width * inter.Height : 0.0;
        if (interArea <= 0) return 0.0;

        double unionArea = (double)a.Width * a.Height + (double)b.Width * b.Height - interArea;
        return unionArea > 0 ? interArea / unionArea : 0.0;
    }

    /// <summary>Bảng màu xoay vòng để mỗi kết quả tìm được có 1 màu riêng biệt, dễ phân biệt trên overlay.</summary>
    private static Scalar PickDistinctColor(int index)
    {
        Scalar[] palette =
        {
            new Scalar(0, 255, 255),   // Vàng
            new Scalar(0, 255, 0),     // Xanh lá
            new Scalar(255, 0, 0),     // Xanh dương
            new Scalar(0, 165, 255),   // Cam
            new Scalar(255, 0, 255),   // Tím/Magenta
            new Scalar(255, 255, 0),   // Cyan
        };
        return palette[index % palette.Length];
    }

    #endregion
}