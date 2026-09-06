// ==================== Vai trò chính:                Tìm giao điểm của 2 đường thẳng, tính góc giữa chúng
// ==================== Thành phần / Class tiêu biểu: LineIntersectionTool
// ==================== Phụ thuộc vào:                OpenCvSharp + Core.Models (LineResult, Point2d)
// ==================== Pattern / Kỹ thuật nổi bật:   Line-Line Intersection bằng phương pháp Cramer's Rule (giải hệ 2 phương trình tuyến tính)

using System;
using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Models;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;
using P2 = VisionFlow.Core.Models.Point2d;

namespace VisionFlow.Tools.Geometry;

/// <summary>
/// Tìm giao điểm của 2 đường thẳng (thường lấy từ 2 FindLine) - nền tảng cho đo góc nghiêng,
/// xác định vị trí đỉnh (corner) khi không có blob rõ rệt, kiểm tra song song/vuông góc.
/// </summary>
[ToolMetadata("LineIntersection", DisplayName = "Line Intersection", Category = "Geometry",
    Description = "Find the intersection point of 2 lines and the angle between them")]
public sealed class LineIntersectionTool : VisionTool
{
    #region 1. Khai báo Port (In/Out)
    private readonly InputPort<IVisionImage> _input;
    private readonly InputPort<LineResult> _line1;
    private readonly InputPort<LineResult> _line2;

    private readonly OutputPort<IVisionImage> _outImage;
    private readonly OutputPort<P2?> _outIntersectionPoint;
    private readonly OutputPort<bool> _outHasIntersection;
    private readonly OutputPort<double> _outDistance;
    private readonly OutputPort<double> _outAngle;
    #endregion

    #region 2. Khai báo Parameter
    // ----- Tab Intersection -----
    private readonly ToolParameter<bool> _extendLines;
    private readonly ToolParameter<double> _maxDistance;
    private readonly ToolParameter<double> _toleranceAngle;

    // ----- Tab Display -----
    private readonly ToolParameter<bool> _drawLines;
    private readonly ToolParameter<bool> _drawIntersection;
    private readonly ToolParameter<bool> _drawExtendedLines;
    private readonly ToolParameter<string> _lineColor;
    private readonly ToolParameter<string> _intersectionColor;
    private readonly ToolParameter<int> _lineThickness;
    private readonly ToolParameter<int> _intersectionSize;
    #endregion

    public LineIntersectionTool()
    {
        _input = AddInput<IVisionImage>("Image", optional: true);
        _line1 = AddInput<LineResult>("Line1", optional: true);
        _line2 = AddInput<LineResult>("Line2", optional: true);

        _outImage = AddOutput<IVisionImage>("Image");
        _outIntersectionPoint = AddOutput<P2?>("IntersectionPoint");
        _outHasIntersection = AddOutput<bool>("HasIntersection");
        _outDistance = AddOutput<double>("Distance");
        _outAngle = AddOutput<double>("Angle");

        _extendLines = AddParameter("ExtendLines", true, "Extend Lines", category: "Intersection", order: 1);
        _maxDistance = AddParameter("MaxDistance", 1000.0, "Max Distance", 0.0, 1_000_000.0, category: "Intersection", order: 2);
        _toleranceAngle = AddParameter("ToleranceAngle", 0.5, "Tolerance Angle", 0.0, 45.0, category: "Intersection", order: 3);

        _drawLines = AddParameter("DrawLines", true, "Draw Lines", category: "Display", order: 1);
        _drawIntersection = AddParameter("DrawIntersection", true, "Draw Intersection", category: "Display", order: 2);
        _drawExtendedLines = AddParameter("DrawExtendedLines", false, "Draw Extended Lines", category: "Display", order: 3);
        _lineColor = AddChoiceParameter("LineColor", "Green", GeometryColors.Names, "Line Color", category: "Display", order: 4);
        _intersectionColor = AddChoiceParameter("IntersectionColor", "Red", GeometryColors.Names, "Intersection Color", category: "Display", order: 5);
        _lineThickness = AddParameter("LineThickness", 2, "Line Thickness", 1, 20, category: "Display", order: 6);
        _intersectionSize = AddParameter("IntersectionSize", 6, "Intersection Size", 1, 50, category: "Display", order: 7);
    }

    protected override void OnExecute(IToolContext context)
    {
        Mat canvas = _input.Value != null ? _input.Value.AsMat().Clone() : new Mat(480, 640, MatType.CV_8UC3, Scalar.All(0));
        if (canvas.Channels() == 1) { Mat c3 = new Mat(); Cv2.CvtColor(canvas, c3, ColorConversionCodes.GRAY2BGR); canvas.Dispose(); canvas = c3; }

        if (_line1.Value is not LineResult l1 || _line2.Value is not LineResult l2)
        {
            context.Log("LineIntersectionTool: Line1 hoặc Line2 bị thiếu -> HasIntersection=false.");
            _outImage.Value = new MatVisionImage(canvas);
            _outIntersectionPoint.Value = null; _outHasIntersection.Value = false; _outDistance.Value = 0; _outAngle.Value = 0;
            return;
        }

        P2 p1 = l1.Segment.P1, p2 = l1.Segment.P2; // Điểm đầu/cuối đường thẳng 1
        P2 q1 = l2.Segment.P1, q2 = l2.Segment.P2; // Điểm đầu/cuối đường thẳng 2
        double dx1 = p2.X - p1.X, dy1 = p2.Y - p1.Y; // Vector hướng đường 1
        double dx2 = q2.X - q1.X, dy2 = q2.Y - q1.Y; // Vector hướng đường 2

        // ----- Bước 1: Tính góc giữa 2 đường bằng tích vô hướng của 2 vector đơn vị -----
        double len1 = Math.Sqrt(dx1 * dx1 + dy1 * dy1), len2 = Math.Sqrt(dx2 * dx2 + dy2 * dy2);
        double angleDeg = 0;
        bool isParallel = true;
        if (len1 > 1e-9 && len2 > 1e-9)
        {
            double dot = (dx1 / len1) * (dx2 / len2) + (dy1 / len1) * (dy2 / len2); // Cos góc giữa 2 vector hướng
            angleDeg = Math.Acos(Math.Clamp(Math.Abs(dot), -1.0, 1.0)) * 180.0 / Math.PI; // acos(|dot|) -> luôn ra khoảng 0-90 độ
            isParallel = angleDeg < _toleranceAngle.Value; // Góc quá nhỏ -> coi như song song, không có giao điểm
        }

        P2? intersection = null;
        bool hasIntersection = false;

        if (!isParallel)
        {
            // ----- Bước 2: Giải hệ phương trình tìm giao điểm bằng Cramer's Rule -----
            double denom = dx1 * dy2 - dy1 * dx2; // Định thức hệ số (mẫu số chung)
            double t1 = ((q1.X - p1.X) * dy2 - (q1.Y - p1.Y) * dx2) / denom; // Tham số vị trí giao điểm trên đường 1
            double t2 = ((q1.X - p1.X) * dy1 - (q1.Y - p1.Y) * dx1) / denom; // Tham số vị trí giao điểm trên đường 2

            bool withinSegments = t1 >= 0 && t1 <= 1 && t2 >= 0 && t2 <= 1; // Giao điểm có nằm trong 2 đoạn thẳng gốc không
            if (_extendLines.Value || withinSegments) // ExtendLines=true -> chấp nhận mọi vị trí; false -> chỉ chấp nhận khi nằm trong đoạn
            {
                var pt = new P2(p1.X + t1 * dx1, p1.Y + t1 * dy1); // Tọa độ giao điểm thực tế
                double distanceToOrigin = Math.Sqrt(pt.X * pt.X + pt.Y * pt.Y); // Khoảng cách từ giao điểm đến gốc (0,0) theo đúng tài liệu
                if (distanceToOrigin <= _maxDistance.Value) // Loại bỏ giao điểm "ảo" ở quá xa khi 2 đường gần như song song
                {
                    intersection = pt;
                    hasIntersection = true;
                }
            }
        }

        // ----- Bước 3: Vẽ overlay -----
        if (_drawLines.Value)
        {
            Cv2.Line(canvas, new Point((int)p1.X, (int)p1.Y), new Point((int)p2.X, (int)p2.Y), GeometryColors.FromName(_lineColor.Value), _lineThickness.Value);
            Cv2.Line(canvas, new Point((int)q1.X, (int)q1.Y), new Point((int)q2.X, (int)q2.Y), GeometryColors.FromName(_lineColor.Value), _lineThickness.Value);
        }
        if (_drawExtendedLines.Value && len1 > 1e-9 && len2 > 1e-9)
        {
            // Kéo dài mỗi đường thêm 2000px ra 2 phía để trực quan hóa "đường ảo" khi ExtendLines=true
            double ux1 = dx1 / len1, uy1 = dy1 / len1, ux2 = dx2 / len2, uy2 = dy2 / len2;
            Cv2.Line(canvas, new Point((int)(p1.X - ux1 * 2000), (int)(p1.Y - uy1 * 2000)), new Point((int)(p2.X + ux1 * 2000), (int)(p2.Y + uy1 * 2000)), Scalar.Gray, 1);
            Cv2.Line(canvas, new Point((int)(q1.X - ux2 * 2000), (int)(q1.Y - uy2 * 2000)), new Point((int)(q2.X + ux2 * 2000), (int)(q2.Y + uy2 * 2000)), Scalar.Gray, 1);
        }
        if (_drawIntersection.Value && intersection is P2 ip)
            Cv2.Circle(canvas, new Point((int)ip.X, (int)ip.Y), _intersectionSize.Value, GeometryColors.FromName(_intersectionColor.Value), -1);

        // ----- Bước 4: Đẩy kết quả ra cổng output -----
        _outImage.Value = new MatVisionImage(canvas);
        _outIntersectionPoint.Value = intersection;
        _outHasIntersection.Value = hasIntersection;
        _outDistance.Value = intersection is P2 d ? Math.Sqrt(d.X * d.X + d.Y * d.Y) : 0;
        _outAngle.Value = angleDeg;

        context.Log($"LineIntersection: HasIntersection={hasIntersection}, Angle={angleDeg:F1}deg");
    }
}