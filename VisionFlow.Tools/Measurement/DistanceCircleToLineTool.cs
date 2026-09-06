// ==================== Vai trò chính:                Đo khoảng cách (clearance) giữa 1 hình tròn và 1 đường thẳng
// ==================== Thành phần / Class tiêu biểu: DistanceCircleToLineTool
// ==================== Phụ thuộc vào:                OpenCvSharp + Core.Models (CircleResult, LineResult, Point2d)
// ==================== Pattern / Kỹ thuật nổi bật:   Chiếu điểm (projection) lên đoạn thẳng có kẹp biên (clamped projection)

using System;
using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Models;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Geometry; // Dùng lại GeometryColors đã viết ở nhóm Geometry
using VisionFlow.Tools.Imaging;
using P2 = VisionFlow.Core.Models.Point2d;

namespace VisionFlow.Tools.Measurement;

/// <summary>
/// Đo khoảng cách giữa lỗ tròn (circle) và cạnh chuẩn (line) - kiểm tra vị trí tương đối phục vụ
/// đo kích thước và kiểm tra tolerance (VD: lỗ bắt vít có đủ khoảng cách an toàn đến mép sản phẩm không).
/// LƯU Ý THIẾT KẾ: DistancePx được tính là khoảng cách "clearance" (mép ngoài đường tròn -> line),
/// tức là (khoảng cách tâm-đến-line) - Radius, vì đây là con số có ý nghĩa vật lý thực tế hơn khi kiểm tra tolerance.
/// </summary>
[ToolMetadata("DistanceCircleToLine", DisplayName = "Distance Circle To Line", Category = "Measurement",
    Description = "Measure the clearance distance between a circle and a line")]
public sealed class DistanceCircleToLineTool : VisionTool
{
    #region 1. Khai báo Port (In/Out)
    private readonly InputPort<IVisionImage> _input;
    private readonly InputPort<CircleResult> _circle;
    private readonly InputPort<LineResult> _line;

    private readonly OutputPort<IVisionImage> _outImage;
    private readonly OutputPort<double> _outDistancePx;
    private readonly OutputPort<double> _outDistanceMm;
    private readonly OutputPort<P2> _outCircleCenter;
    private readonly OutputPort<P2> _outClosestPointOnLine;
    private readonly OutputPort<bool> _outMeasurementValid;
    #endregion

    #region 2. Khai báo Parameter
    private readonly ToolParameter<bool> _drawCircle;
    private readonly ToolParameter<bool> _drawLine;
    private readonly ToolParameter<bool> _drawMeasurement;
    private readonly ToolParameter<int> _measurementLineThickness;
    private readonly ToolParameter<double> _pixelsToMM;
    private readonly ToolParameter<bool> _showDimensions;
    #endregion

    public DistanceCircleToLineTool()
    {
        _input = AddInput<IVisionImage>("Image", optional: true);
        _circle = AddInput<CircleResult>("Circle", optional: true);
        _line = AddInput<LineResult>("Line", optional: true);

        _outImage = AddOutput<IVisionImage>("Image");
        _outDistancePx = AddOutput<double>("DistancePx");
        _outDistanceMm = AddOutput<double>("DistanceMm");
        _outCircleCenter = AddOutput<P2>("CircleCenter");
        _outClosestPointOnLine = AddOutput<P2>("ClosestPointOnLine");
        _outMeasurementValid = AddOutput<bool>("MeasurementValid");

        _drawCircle = AddParameter("DrawCircle", true, "Draw Circle", category: "Display", order: 1);
        _drawLine = AddParameter("DrawLine", true, "Draw Line", category: "Display", order: 2);
        _drawMeasurement = AddParameter("DrawMeasurement", true, "Draw Measurement", category: "Display", order: 3);
        _measurementLineThickness = AddParameter("MeasurementLineThickness", 2, "Measurement Line Thickness", 1, 20, category: "Display", order: 4);
        _pixelsToMM = AddParameter("PixelsToMM", 1.0, "Pixels To MM", 0.0001, 1000.0, category: "Display", order: 5);
        _showDimensions = AddParameter("ShowDimensions", false, "Show Dimensions", category: "Display", order: 6);
    }

    protected override void OnExecute(IToolContext context)
    {
        Mat canvas = _input.Value != null ? _input.Value.AsMat().Clone() : new Mat(480, 640, MatType.CV_8UC3, Scalar.All(0));
        if (canvas.Channels() == 1) { Mat c3 = new Mat(); Cv2.CvtColor(canvas, c3, ColorConversionCodes.GRAY2BGR); canvas.Dispose(); canvas = c3; }

        if (_circle.Value is not CircleResult c || _line.Value is not LineResult l)
        {
            context.Log("DistanceCircleToLineTool: Circle hoặc Line bị thiếu -> MeasurementValid=false.");
            _outImage.Value = new MatVisionImage(canvas);
            _outDistancePx.Value = 0; _outDistanceMm.Value = 0; _outCircleCenter.Value = default;
            _outClosestPointOnLine.Value = default; _outMeasurementValid.Value = false;
            return;
        }

        P2 center = c.Circle.Center;
        P2 a = l.Segment.P1, b = l.Segment.P2;

        // ----- Bước 1: Chiếu tâm đường tròn lên ĐOẠN THẲNG (kẹp t trong [0,1], không kéo dài vô hạn) -----
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double lenSq = dx * dx + dy * dy;
        double t = lenSq > 1e-9 ? ((center.X - a.X) * dx + (center.Y - a.Y) * dy) / lenSq : 0;
        t = Math.Clamp(t, 0.0, 1.0); // Kẹp về đoạn thẳng thực tế - nếu chiếu ra ngoài đoạn thì lấy điểm mút gần nhất
        var closest = new P2(a.X + t * dx, a.Y + t * dy);

        // ----- Bước 2: Tính khoảng cách clearance = (khoảng cách tâm-đến-điểm gần nhất) - bán kính -----
        double centerDistance = Math.Sqrt(Math.Pow(center.X - closest.X, 2) + Math.Pow(center.Y - closest.Y, 2));
        double clearance = Math.Max(0, centerDistance - c.Circle.Radius); // Không cho ra số âm (trường hợp đường tròn cắt qua line)

        // ----- Bước 3: Vẽ overlay -----
        if (_drawCircle.Value) Cv2.Circle(canvas, (Point)ToCv(center), (int)c.Circle.Radius, Scalar.Cyan, 2);
        if (_drawLine.Value) Cv2.Line(canvas, (Point)ToCv(a), (Point)ToCv(b), Scalar.LimeGreen, 2);
        if (_drawMeasurement.Value)
        {
            Cv2.Line(canvas, (Point)ToCv(center), (Point)ToCv(closest), Scalar.Red, _measurementLineThickness.Value);
            if (_showDimensions.Value)
                Cv2.PutText(canvas, $"D={clearance * _pixelsToMM.Value:F2}mm", (Point)ToCv(new P2((center.X + closest.X) / 2, (center.Y + closest.Y) / 2 - 10)),
                    HersheyFonts.HersheySimplex, 0.5, Scalar.White, 1);
        }

        // ----- Bước 4: Đẩy kết quả ra cổng output -----
        _outImage.Value = new MatVisionImage(canvas);
        _outDistancePx.Value = clearance;
        _outDistanceMm.Value = clearance * _pixelsToMM.Value;
        _outCircleCenter.Value = center;
        _outClosestPointOnLine.Value = closest;
        _outMeasurementValid.Value = true;

        context.Log($"DistanceCircleToLine: {clearance:F2}px ({clearance * _pixelsToMM.Value:F2}mm)");
    }

    private static Point2f ToCv(P2 p) => new Point2f((float)p.X, (float)p.Y);
}