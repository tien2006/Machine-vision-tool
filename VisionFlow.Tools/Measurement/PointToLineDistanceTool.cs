// ==================== Vai trò chính:                Đo khoảng cách từ 1 điểm tới 1 đường thẳng (kiểm tra alignment/độ lệch so với cạnh chuẩn)
// ==================== Thành phần / Class tiêu biểu: PointToLineDistanceTool
// ==================== Phụ thuộc vào:                OpenCvSharp + Core.Models (LineResult, LineSegment, Point2d)
// ==================== Pattern / Kỹ thuật nổi bật:   Point-to-segment projection (chân đường vuông góc, có kẹp biên)

using System;
using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Models;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Geometry; // Dùng lại GeometryColors
using VisionFlow.Tools.Imaging;
using P2 = VisionFlow.Core.Models.Point2d;

namespace VisionFlow.Tools.Measurement;

/// <summary>
/// Đo khoảng cách ngắn nhất từ 1 điểm tới 1 đường thẳng tham chiếu - kiểm tra vị trí/độ lệch/alignment
/// của đối tượng so với 1 cạnh chuẩn (VD: tâm lỗ có lệch quá xa so với mép sản phẩm không).
/// </summary>
[ToolMetadata("PointToLineDistance", DisplayName = "Point To Line Distance", Category = "Measurement",
    Description = "Measure the perpendicular distance from a point to a reference line")]
public sealed class PointToLineDistanceTool : VisionTool
{
    #region 1. Khai báo Port (In/Out)
    private readonly InputPort<IVisionImage> _input;
    private readonly InputPort<P2?> _point;
    private readonly InputPort<LineResult> _line;

    private readonly OutputPort<IVisionImage> _outImage;
    private readonly OutputPort<double> _outDistance;
    private readonly OutputPort<P2> _outClosestPointOnLine;
    private readonly OutputPort<LineSegment> _outPerpendicularLine;
    private readonly OutputPort<bool> _outIsPointOnLine;
    #endregion

    #region 2. Khai báo Parameter
    // ----- Tab Calculation -----
    private readonly ToolParameter<string> _distanceUnit; // "Pixels" hoặc "MM"
    private readonly ToolParameter<double> _scale;
    private readonly ToolParameter<int> _precision;
    private readonly ToolParameter<double> _pointOnLineTolerance; // Mở rộng: ngưỡng để xác định IsPointOnLine, tài liệu chưa đặt tên tham số cụ thể

    // ----- Tab Display -----
    private readonly ToolParameter<bool> _drawPoint;
    private readonly ToolParameter<bool> _drawLine;
    private readonly ToolParameter<bool> _drawPerpendicularLine;
    private readonly ToolParameter<bool> _drawClosestPoint;
    private readonly ToolParameter<bool> _drawDistanceText;
    private readonly ToolParameter<string> _pointColor;
    private readonly ToolParameter<string> _lineColor;
    private readonly ToolParameter<string> _perpendicularColor;
    private readonly ToolParameter<string> _closestPointColor;
    private readonly ToolParameter<int> _lineThickness;
    private readonly ToolParameter<int> _pointSize;
    #endregion

    public PointToLineDistanceTool()
    {
        _input = AddInput<IVisionImage>("Image", optional: true);
        _point = AddInput<P2?>("Point", optional: true);
        _line = AddInput<LineResult>("Line", optional: true);

        _outImage = AddOutput<IVisionImage>("Image");
        _outDistance = AddOutput<double>("Distance");
        _outClosestPointOnLine = AddOutput<P2>("ClosestPointOnLine");
        _outPerpendicularLine = AddOutput<LineSegment>("PerpendicularLine");
        _outIsPointOnLine = AddOutput<bool>("IsPointOnLine");

        _distanceUnit = AddChoiceParameter("DistanceUnit", "Pixels", new[] { "Pixels", "MM" }, "Distance Unit", category: "Calculation", order: 1);
        _scale = AddParameter("Scale", 1.0, "Scale", 0.0001, 1000.0, category: "Calculation", order: 2);
        _precision = AddParameter("Precision", 2, "Precision", 0, 6, category: "Calculation", order: 3);
        _pointOnLineTolerance = AddParameter("PointOnLineTolerance", 2.0, "Point-On-Line Tolerance", 0.0, 1000.0, category: "Calculation", order: 4);

        _drawPoint = AddParameter("DrawPoint", true, "Draw Point", category: "Display", order: 1);
        _drawLine = AddParameter("DrawLine", true, "Draw Line", category: "Display", order: 2);
        _drawPerpendicularLine = AddParameter("DrawPerpendicularLine", true, "Draw Perpendicular Line", category: "Display", order: 3);
        _drawClosestPoint = AddParameter("DrawClosestPoint", true, "Draw Closest Point", category: "Display", order: 4);
        _drawDistanceText = AddParameter("DrawDistanceText", true, "Draw Distance Text", category: "Display", order: 5);
        _pointColor = AddChoiceParameter("PointColor", "Yellow", GeometryColors.Names, "Point Color", category: "Display", order: 6);
        _lineColor = AddChoiceParameter("LineColor", "Green", GeometryColors.Names, "Line Color", category: "Display", order: 7);
        _perpendicularColor = AddChoiceParameter("PerpendicularColor", "Red", GeometryColors.Names, "Perpendicular Color", category: "Display", order: 8);
        _closestPointColor = AddChoiceParameter("ClosestPointColor", "Cyan", GeometryColors.Names, "Closest Point Color", category: "Display", order: 9);
        _lineThickness = AddParameter("LineThickness", 2, "Line Thickness", 1, 20, category: "Display", order: 10);
        _pointSize = AddParameter("PointSize", 5, "Point Size", 1, 50, category: "Display", order: 11);
    }

    protected override void OnExecute(IToolContext context)
    {
        Mat canvas = _input.Value != null ? _input.Value.AsMat().Clone() : new Mat(480, 640, MatType.CV_8UC3, Scalar.All(0));
        if (canvas.Channels() == 1) { Mat c3 = new Mat(); Cv2.CvtColor(canvas, c3, ColorConversionCodes.GRAY2BGR); canvas.Dispose(); canvas = c3; }

        if (_point.Value is not P2 pt || _line.Value is not LineResult l)
        {
            context.Log("PointToLineDistanceTool: Point hoặc Line bị thiếu -> trả về giá trị mặc định.");
            _outImage.Value = new MatVisionImage(canvas);
            _outDistance.Value = 0; _outClosestPointOnLine.Value = default; _outPerpendicularLine.Value = default; _outIsPointOnLine.Value = false;
            return;
        }

        P2 a = l.Segment.P1, b = l.Segment.P2;

        // ----- Bước 1: Chiếu điểm lên ĐOẠN THẲNG (kẹp biên, coi Line là cạnh thực tế hữu hạn) -----
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double lenSq = dx * dx + dy * dy;
        double t = lenSq > 1e-9 ? Math.Clamp(((pt.X - a.X) * dx + (pt.Y - a.Y) * dy) / lenSq, 0.0, 1.0) : 0;
        var closest = new P2(a.X + t * dx, a.Y + t * dy);

        double distancePx = Math.Sqrt(Math.Pow(pt.X - closest.X, 2) + Math.Pow(pt.Y - closest.Y, 2));
        bool isOnLine = distancePx <= _pointOnLineTolerance.Value;

        double distanceOutput = _distanceUnit.Value == "MM" ? distancePx * _scale.Value : distancePx; // Quy đổi đơn vị theo Tab Calculation
        var perpendicularLine = new LineSegment(pt, closest);

        // ----- Bước 2: Vẽ overlay -----
        if (_drawLine.Value) Cv2.Line(canvas, (Point)ToCv(a), (Point)ToCv(b), GeometryColors.FromName(_lineColor.Value), _lineThickness.Value);
        if (_drawPoint.Value) Cv2.Circle(canvas, (Point)ToCv(pt), _pointSize.Value, GeometryColors.FromName(_pointColor.Value), -1);
        if (_drawClosestPoint.Value) Cv2.Circle(canvas, (Point)ToCv(closest), _pointSize.Value, GeometryColors.FromName(_closestPointColor.Value), -1);
        if (_drawPerpendicularLine.Value) Cv2.Line(canvas, (Point)ToCv(pt), (Point)ToCv(closest), GeometryColors.FromName(_perpendicularColor.Value), _lineThickness.Value);
        if (_drawDistanceText.Value)
        {
            string unit = _distanceUnit.Value == "MM" ? "mm" : "px";
            string text = $"{Math.Round(distanceOutput, _precision.Value)}{unit}";
            Cv2.PutText(canvas, text, (Point)ToCv(new P2((pt.X + closest.X) / 2 + 8, (pt.Y + closest.Y) / 2 - 8)), HersheyFonts.HersheySimplex, 0.5, Scalar.White, 1);
        }

        // ----- Bước 3: Đẩy kết quả ra cổng output -----
        _outImage.Value = new MatVisionImage(canvas);
        _outDistance.Value = distanceOutput;
        _outClosestPointOnLine.Value = closest;
        _outPerpendicularLine.Value = perpendicularLine;
        _outIsPointOnLine.Value = isOnLine;

        context.Log($"PointToLineDistance: {distanceOutput:F2}{(_distanceUnit.Value == "MM" ? "mm" : "px")}, IsPointOnLine={isOnLine}");
    }

    private static Point2f ToCv(P2 p) => new Point2f((float)p.X, (float)p.Y);
}