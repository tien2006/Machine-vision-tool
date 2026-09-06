// ==================== Vai trò chính:                Đo khoảng cách/gap giữa 2 đường thẳng, kiểm tra song song và giao cắt
// ==================== Thành phần / Class tiêu biểu: DistanceLineToLineTool
// ==================== Phụ thuộc vào:                OpenCvSharp + Core.Models (LineResult, Point2d)
// ==================== Pattern / Kỹ thuật nổi bật:   Segment-Segment distance (4-way point-to-segment), infinite-line perpendicular distance

using System;
using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Models;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;
using P2 = VisionFlow.Core.Models.Point2d;

namespace VisionFlow.Tools.Measurement;

/// <summary>
/// Đo khoảng cách giữa 2 đường thẳng - kiểm tra độ song song, khe hở (gap) hoặc chiều rộng vật thể
/// có 2 cạnh đối diện (VD: bề rộng rãnh, khe hở giữa 2 mép băng chuyền).
/// </summary>
[ToolMetadata("DistanceLineToLine", DisplayName = "Distance Line To Line", Category = "Measurement",
    Description = "Measure distance, parallelism, and intersection between 2 lines")]
public sealed class DistanceLineToLineTool : VisionTool
{
    #region 1. Khai báo Port (In/Out)
    private readonly InputPort<IVisionImage> _input;
    private readonly InputPort<LineResult> _line1;
    private readonly InputPort<LineResult> _line2;

    private readonly OutputPort<IVisionImage> _outImage;
    private readonly OutputPort<double> _outDistancePx;
    private readonly OutputPort<double> _outDistanceMm;
    private readonly OutputPort<P2> _outClosestPointLine1;
    private readonly OutputPort<P2> _outClosestPointLine2;
    private readonly OutputPort<bool> _outMeasurementValid;
    private readonly OutputPort<bool> _outIsParallel;
    private readonly OutputPort<bool> _outIsIntersecting;
    #endregion

    #region 2. Khai báo Parameter
    // ----- Tab Measurement -----
    private readonly ToolParameter<double> _pixelsToMM;
    private readonly ToolParameter<bool> _useInfiniteLines;

    // ----- Tab Display -----
    private readonly ToolParameter<bool> _drawLine1;
    private readonly ToolParameter<bool> _drawLine2;
    private readonly ToolParameter<bool> _drawMeasurement;
    private readonly ToolParameter<int> _measurementLineThickness;
    private readonly ToolParameter<bool> _showDimensions;

    // ----- Tab Output -----
    private readonly ToolParameter<bool> _useDirectDrawing;
    #endregion

    public DistanceLineToLineTool()
    {
        _input = AddInput<IVisionImage>("Image", optional: true);
        _line1 = AddInput<LineResult>("Line1", optional: true);
        _line2 = AddInput<LineResult>("Line2", optional: true);

        _outImage = AddOutput<IVisionImage>("Image");
        _outDistancePx = AddOutput<double>("DistancePx");
        _outDistanceMm = AddOutput<double>("DistanceMm");
        _outClosestPointLine1 = AddOutput<P2>("ClosestPointLine1");
        _outClosestPointLine2 = AddOutput<P2>("ClosestPointLine2");
        _outMeasurementValid = AddOutput<bool>("MeasurementValid");
        _outIsParallel = AddOutput<bool>("IsParallel");
        _outIsIntersecting = AddOutput<bool>("IsIntersecting");

        _pixelsToMM = AddParameter("PixelsToMM", 1.0, "Pixels To MM", 0.0001, 1000.0, category: "Measurement", order: 1);
        _useInfiniteLines = AddParameter("UseInfiniteLines", false, "Use Infinite Lines", category: "Measurement", order: 2);

        _drawLine1 = AddParameter("DrawLine1", true, "Draw Line1", category: "Display", order: 1);
        _drawLine2 = AddParameter("DrawLine2", true, "Draw Line2", category: "Display", order: 2);
        _drawMeasurement = AddParameter("DrawMeasurement", true, "Draw Measurement", category: "Display", order: 3);
        _measurementLineThickness = AddParameter("MeasurementLineThickness", 2, "Measurement Line Thickness", 1, 20, category: "Display", order: 4);
        _showDimensions = AddParameter("ShowDimensions", false, "Show Dimensions", category: "Display", order: 5);

        _useDirectDrawing = AddParameter("UseDirectDrawing", true, "Use Direct Drawing", category: "Output", order: 1);
    }

    protected override void OnExecute(IToolContext context)
    {
        Mat canvas = _input.Value != null ? _input.Value.AsMat().Clone() : new Mat(480, 640, MatType.CV_8UC3, Scalar.All(0));
        if (canvas.Channels() == 1) { Mat c3 = new Mat(); Cv2.CvtColor(canvas, c3, ColorConversionCodes.GRAY2BGR); canvas.Dispose(); canvas = c3; }

        if (_line1.Value is not LineResult l1 || _line2.Value is not LineResult l2)
        {
            context.Log("DistanceLineToLineTool: Line1 hoặc Line2 bị thiếu -> MeasurementValid=false.");
            _outImage.Value = new MatVisionImage(canvas);
            _outDistancePx.Value = 0; _outDistanceMm.Value = 0; _outClosestPointLine1.Value = default; _outClosestPointLine2.Value = default;
            _outMeasurementValid.Value = false; _outIsParallel.Value = false; _outIsIntersecting.Value = false;
            return;
        }

        P2 a1 = l1.Segment.P1, a2 = l1.Segment.P2, b1 = l2.Segment.P1, b2 = l2.Segment.P2;
        double dxA = a2.X - a1.X, dyA = a2.Y - a1.Y, dxB = b2.X - b1.X, dyB = b2.Y - b1.Y;
        double lenA = Math.Sqrt(dxA * dxA + dyA * dyA), lenB = Math.Sqrt(dxB * dxB + dyB * dyB);

        // ----- Bước 1: Kiểm tra song song bằng |cos(góc)| > 0.98, dùng chung tiêu chí với LineCenterLineTool -----
        double cosAngle = lenA > 1e-9 && lenB > 1e-9 ? Math.Abs((dxA * dxB + dyA * dyB) / (lenA * lenB)) : 0;
        bool isParallel = cosAngle > 0.98;

        // ----- Bước 2: Kiểm tra 2 ĐOẠN THẲNG có cắt nhau không (luôn kiểm tra theo đoạn thực tế, không phụ thuộc UseInfiniteLines) -----
        bool isIntersecting = SegmentsIntersect(a1, a2, b1, b2);

        // ----- Bước 3: Tính khoảng cách theo chế độ UseInfiniteLines -----
        double distance; P2 closest1, closest2;
        if (_useInfiniteLines.Value)
        {
            if (isIntersecting || !isParallel)
            {
                // 2 đường vô hạn không song song LUÔN cắt nhau tại đâu đó trong mặt phẳng 2D -> khoảng cách = 0
                distance = 0;
                closest1 = closest2 = LineLineIntersectInfinite(a1, dxA, dyA, b1, dxB, dyB) ?? a1;
            }
            else
            {
                // Song song vô hạn: khoảng cách không đổi dọc theo đường -> đo từ 1 điểm bất kỳ của Line1 (a1) tới đường vô hạn Line2
                closest1 = a1;
                closest2 = ProjectOntoInfiniteLine(a1, b1, dxB, dyB);
                distance = Math.Sqrt(Math.Pow(a1.X - closest2.X, 2) + Math.Pow(a1.Y - closest2.Y, 2));
            }
        }
        else
        {
            // Đo khoảng cách nhỏ nhất giữa 2 ĐOẠN THẲNG thực tế: xét cả 4 trường hợp điểm-đến-đoạn rồi lấy min
            var (d1, c1a, c1b) = PointToSegmentDistance(a1, b1, b2);
            var (d2, c2a, c2b) = PointToSegmentDistance(a2, b1, b2);
            var (d3, c3a, c3b) = PointToSegmentDistance(b1, a1, a2);
            var (d4, c4a, c4b) = PointToSegmentDistance(b2, a1, a2);

            var candidates = new[] { (d1, c1a, c1b), (d2, c2a, c2b), (d3, c3b, c3a), (d4, c4b, c4a) };
            var best = candidates[0];
            foreach (var cand in candidates) if (cand.Item1 < best.Item1) best = cand;
            distance = isIntersecting ? 0 : best.Item1; // Nếu 2 đoạn cắt nhau, khoảng cách chắc chắn = 0
            closest1 = best.Item2; closest2 = best.Item3;
        }

        // ----- Bước 4: Vẽ overlay (chỉ vẽ khi UseDirectDrawing = true) -----
        if (_useDirectDrawing.Value)
        {
            if (_drawLine1.Value) Cv2.Line(canvas, (Point)ToCv(a1), (Point)ToCv(a2), Scalar.LimeGreen, 2);
            if (_drawLine2.Value) Cv2.Line(canvas, (Point)ToCv(b1), (Point)ToCv(b2), Scalar.Cyan, 2);
            if (_drawMeasurement.Value)
            {
                Cv2.Line(canvas, (Point)ToCv(closest1), (Point)ToCv(closest2), Scalar.Red, _measurementLineThickness.Value);
                if (_showDimensions.Value)
                    Cv2.PutText(canvas, $"D={distance * _pixelsToMM.Value:F2}mm", (Point)ToCv(new P2((closest1.X + closest2.X) / 2, (closest1.Y + closest2.Y) / 2 - 10)),
                        HersheyFonts.HersheySimplex, 0.5, Scalar.White, 1);
            }
        }

        // ----- Bước 5: Đẩy kết quả ra cổng output -----
        _outImage.Value = new MatVisionImage(canvas);
        _outDistancePx.Value = distance;
        _outDistanceMm.Value = distance * _pixelsToMM.Value;
        _outClosestPointLine1.Value = closest1;
        _outClosestPointLine2.Value = closest2;
        _outMeasurementValid.Value = true;
        _outIsParallel.Value = isParallel;
        _outIsIntersecting.Value = isIntersecting;

        context.Log($"DistanceLineToLine: {distance:F2}px, IsParallel={isParallel}, IsIntersecting={isIntersecting}");
    }

    /// <summary>Khoảng cách ngắn nhất từ 1 điểm tới 1 ĐOẠN THẲNG (có kẹp biên), trả về kèm 2 điểm gần nhau nhất.</summary>
    private static (double Dist, P2 OnPoint, P2 OnSegment) PointToSegmentDistance(P2 pt, P2 segA, P2 segB)
    {
        double dx = segB.X - segA.X, dy = segB.Y - segA.Y;
        double lenSq = dx * dx + dy * dy;
        double t = lenSq > 1e-9 ? Math.Clamp(((pt.X - segA.X) * dx + (pt.Y - segA.Y) * dy) / lenSq, 0.0, 1.0) : 0;
        var closest = new P2(segA.X + t * dx, segA.Y + t * dy);
        return (Math.Sqrt(Math.Pow(pt.X - closest.X, 2) + Math.Pow(pt.Y - closest.Y, 2)), pt, closest);
    }

    /// <summary>Kiểm tra 2 đoạn thẳng có cắt nhau không, dùng phương pháp định hướng (orientation test) chuẩn.</summary>
    private static bool SegmentsIntersect(P2 p1, P2 p2, P2 p3, P2 p4)
    {
        double d1 = Cross(p3, p4, p1), d2 = Cross(p3, p4, p2), d3 = Cross(p1, p2, p3), d4 = Cross(p1, p2, p4);
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }
    private static double Cross(P2 a, P2 b, P2 c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    /// <summary>Chiếu 1 điểm lên đường thẳng VÔ HẠN (không kẹp biên) đi qua "linePoint" với hướng (dx,dy).</summary>
    private static P2 ProjectOntoInfiniteLine(P2 pt, P2 linePoint, double dx, double dy)
    {
        double lenSq = dx * dx + dy * dy;
        double t = lenSq > 1e-9 ? ((pt.X - linePoint.X) * dx + (pt.Y - linePoint.Y) * dy) / lenSq : 0;
        return new P2(linePoint.X + t * dx, linePoint.Y + t * dy);
    }

    /// <summary>Giao điểm của 2 đường thẳng VÔ HẠN (Cramer's Rule), trả về null nếu song song hoàn toàn.</summary>
    private static P2? LineLineIntersectInfinite(P2 a1, double dxA, double dyA, P2 b1, double dxB, double dyB)
    {
        double denom = dxA * dyB - dyA * dxB;
        if (Math.Abs(denom) < 1e-9) return null;
        double t = ((b1.X - a1.X) * dyB - (b1.Y - a1.Y) * dxB) / denom;
        return new P2(a1.X + t * dxA, a1.Y + t * dyA);
    }

    private static Point2f ToCv(P2 p) => new Point2f((float)p.X, (float)p.Y);
}