// ==================== Vai trò chính:                Tính đường trung tuyến (middle line) nằm giữa 2 đường thẳng đầu vào
// ==================== Thành phần / Class tiêu biểu: LineCenterLineTool
// ==================== Phụ thuộc vào:                OpenCvSharp + Core.Models (LineResult, LineSegment, Point2d)
// ==================== Pattern / Kỹ thuật nổi bật:   Trung bình vector hướng (căn chỉnh dấu trước khi cộng), khoảng cách điểm-đến-đường-vô-hạn

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
/// Tính đường thẳng nằm chính giữa 2 đường input - dùng tìm trục đối xứng của rãnh/khe/ren ốc,
/// tính tim đường dẫn giữa 2 mép băng tải, hoặc làm đường tham chiếu đo độ lệch tâm.
/// </summary>
[ToolMetadata("LineCenterLine", DisplayName = "Line Center Line", Category = "Geometry",
    Description = "Compute the middle line between 2 input lines (parallel-aware)")]
public sealed class LineCenterLineTool : VisionTool
{
    #region 1. Khai báo Port (In/Out)
    private readonly InputPort<IVisionImage> _input;
    private readonly InputPort<LineResult> _line1;
    private readonly InputPort<LineResult> _line2;

    private readonly OutputPort<IVisionImage> _outImage;
    private readonly OutputPort<LineSegment> _outMiddleLine;
    private readonly OutputPort<double> _outDistanceToLine1;
    private readonly OutputPort<double> _outDistanceToLine2;
    private readonly OutputPort<double> _outLineAngle;
    private readonly OutputPort<double> _outLineLength;
    private readonly OutputPort<bool> _outIsParallel;
    #endregion

    #region 2. Khai báo Parameter
    // ----- Tab Line Extension -----
    private readonly ToolParameter<bool> _extendCenterLine;
    private readonly ToolParameter<double> _extensionLength;
    private readonly ToolParameter<string> _extensionMode; // Chỉ "Both" được implement, các mode khác là placeholder theo đúng tài liệu

    // ----- Tab Display -----
    private readonly ToolParameter<bool> _drawInputLines;
    private readonly ToolParameter<bool> _drawCenterPoints;
    private readonly ToolParameter<bool> _drawCenterLine;
    private readonly ToolParameter<string> _inputLineColor;
    private readonly ToolParameter<string> _centerLineColor;
    private readonly ToolParameter<string> _centerPointColor;
    private readonly ToolParameter<int> _lineThickness;
    private readonly ToolParameter<int> _pointSize;
    #endregion

    public LineCenterLineTool()
    {
        _input = AddInput<IVisionImage>("Image", optional: true);
        _line1 = AddInput<LineResult>("Line1", optional: true);
        _line2 = AddInput<LineResult>("Line2", optional: true);

        _outImage = AddOutput<IVisionImage>("Image");
        _outMiddleLine = AddOutput<LineSegment>("MiddleLine");
        _outDistanceToLine1 = AddOutput<double>("DistanceToLine1");
        _outDistanceToLine2 = AddOutput<double>("DistanceToLine2");
        _outLineAngle = AddOutput<double>("LineAngle");
        _outLineLength = AddOutput<double>("LineLength");
        _outIsParallel = AddOutput<bool>("IsParallel");

        _extendCenterLine = AddParameter("ExtendCenterLine", false, "Extend Center Line", category: "Line Extension", order: 1);
        _extensionLength = AddParameter("ExtensionLength", 100.0, "Extension Length", 1.0, 100_000.0, category: "Line Extension", order: 2);
        _extensionMode = AddChoiceParameter("ExtensionMode", "Both", new[] { "Both", "Forward", "Backward", "ToImageBounds" }, "Extension Mode", category: "Line Extension", order: 3);

        _drawInputLines = AddParameter("DrawInputLines", true, "Draw Input Lines", category: "Display", order: 1);
        _drawCenterPoints = AddParameter("DrawCenterPoints", true, "Draw Center Points", category: "Display", order: 2);
        _drawCenterLine = AddParameter("DrawCenterLine", true, "Draw Center Line", category: "Display", order: 3);
        _inputLineColor = AddChoiceParameter("InputLineColor", "Green", GeometryColors.Names, "Input Line Color", category: "Display", order: 4);
        _centerLineColor = AddChoiceParameter("CenterLineColor", "Red", GeometryColors.Names, "Center Line Color", category: "Display", order: 5);
        _centerPointColor = AddChoiceParameter("CenterPointColor", "Yellow", GeometryColors.Names, "Center Point Color", category: "Display", order: 6);
        _lineThickness = AddParameter("LineThickness", 2, "Line Thickness", 1, 20, category: "Display", order: 7);
        _pointSize = AddParameter("PointSize", 6, "Point Size", 1, 50, category: "Display", order: 8);
    }

    protected override void OnExecute(IToolContext context)
    {
        Mat canvas = _input.Value != null ? _input.Value.AsMat().Clone() : new Mat(480, 640, MatType.CV_8UC3, Scalar.All(0));
        if (canvas.Channels() == 1) { Mat c3 = new Mat(); Cv2.CvtColor(canvas, c3, ColorConversionCodes.GRAY2BGR); canvas.Dispose(); canvas = c3; }

        if (_line1.Value is not LineResult l1 || _line2.Value is not LineResult l2)
        {
            context.Log("LineCenterLineTool: Line1 hoặc Line2 bị thiếu -> trả về giá trị mặc định.");
            _outImage.Value = new MatVisionImage(canvas);
            _outMiddleLine.Value = default; _outDistanceToLine1.Value = 0; _outDistanceToLine2.Value = 0;
            _outLineAngle.Value = 0; _outLineLength.Value = 0; _outIsParallel.Value = false;
            return;
        }

        P2 mid1 = new P2((l1.Segment.P1.X + l1.Segment.P2.X) / 2.0, (l1.Segment.P1.Y + l1.Segment.P2.Y) / 2.0); // Trung điểm Line1
        P2 mid2 = new P2((l2.Segment.P1.X + l2.Segment.P2.X) / 2.0, (l2.Segment.P1.Y + l2.Segment.P2.Y) / 2.0); // Trung điểm Line2

        // ----- Bước 1: Chuẩn hóa 2 vector hướng về đơn vị (length=1) -----
        double dx1 = l1.Segment.P2.X - l1.Segment.P1.X, dy1 = l1.Segment.P2.Y - l1.Segment.P1.Y;
        double dx2 = l2.Segment.P2.X - l2.Segment.P1.X, dy2 = l2.Segment.P2.Y - l2.Segment.P1.Y;
        double len1 = Math.Sqrt(dx1 * dx1 + dy1 * dy1), len2 = Math.Sqrt(dx2 * dx2 + dy2 * dy2);
        double ux1 = dx1 / Math.Max(len1, 1e-9), uy1 = dy1 / Math.Max(len1, 1e-9);
        double ux2 = dx2 / Math.Max(len2, 1e-9), uy2 = dy2 / Math.Max(len2, 1e-9);

        // ----- Bước 2: Xét song song (|cos| > 0.98 ~ lệch < 11.5 độ). Nếu vector 2 ngược hướng vector 1 -> đảo dấu trước khi cộng -----
        double cosAngle = ux1 * ux2 + uy1 * uy2;
        bool isParallel = Math.Abs(cosAngle) > 0.98;
        if (cosAngle < 0) { ux2 = -ux2; uy2 = -uy2; } // Căn chỉnh 2 vector về cùng "bán cầu hướng" trước khi lấy trung bình

        double avgUx = ux1 + ux2, avgUy = uy1 + uy2; // Vector hướng trung bình (chưa chuẩn hóa)
        double avgLen = Math.Sqrt(avgUx * avgUx + avgUy * avgUy);
        if (avgLen > 1e-9) { avgUx /= avgLen; avgUy /= avgLen; } // Chuẩn hóa về vector đơn vị

        P2 centerPoint = new P2((mid1.X + mid2.X) / 2.0, (mid1.Y + mid2.Y) / 2.0); // Điểm giữa của 2 trung điểm - tâm middle line

        // ----- Bước 3: Xác định độ dài mở rộng mỗi phía (Tab Line Extension) -----
        double extendLen = _extendCenterLine.Value
            ? _extensionLength.Value
            : Math.Max(canvas.Width, canvas.Height); // Mặc định: đủ dài để xuyên suốt toàn bộ ảnh

        P2 endA = new P2(centerPoint.X - avgUx * extendLen, centerPoint.Y - avgUy * extendLen);
        P2 endB = new P2(centerPoint.X + avgUx * extendLen, centerPoint.Y + avgUy * extendLen);
        var middleLine = new LineSegment(endA, endB);

        // ----- Bước 4: Khoảng cách vuông góc từ mid1/mid2 đến middle line (dùng công thức tích chéo) -----
        double distToLine1 = Math.Abs(avgUx * (mid1.Y - centerPoint.Y) - avgUy * (mid1.X - centerPoint.X));
        double distToLine2 = Math.Abs(avgUx * (mid2.Y - centerPoint.Y) - avgUy * (mid2.X - centerPoint.X));
        double lineAngle = Math.Atan2(avgUy, avgUx) * 180.0 / Math.PI;

        // ----- Bước 5: Vẽ overlay -----
        if (_drawInputLines.Value)
        {
            Cv2.Line(canvas, (Point)ToCv(l1.Segment.P1), (Point)ToCv(l1.Segment.P2), GeometryColors.FromName(_inputLineColor.Value), _lineThickness.Value);
            Cv2.Line(canvas, (Point)ToCv(l2.Segment.P1), (Point)ToCv(l2.Segment.P2), GeometryColors.FromName(_inputLineColor.Value), _lineThickness.Value);
        }
        if (_drawCenterPoints.Value)
        {
            Cv2.Circle(canvas, (Point)ToCv(mid1), _pointSize.Value, GeometryColors.FromName(_centerPointColor.Value), -1);
            Cv2.Circle(canvas, (Point)ToCv(mid2), _pointSize.Value, GeometryColors.FromName(_centerPointColor.Value), -1);
        }
        if (_drawCenterLine.Value)
        {
            Cv2.Line(canvas, (Point)ToCv(endA), (Point)ToCv(endB), GeometryColors.FromName(_centerLineColor.Value), _lineThickness.Value);
            if (isParallel) // Khi song song, vẽ thêm marker vuông góc ngắn tại tâm để nhấn mạnh "trục đối xứng"
            {
                double perpUx = -avgUy, perpUy = avgUx; // Vector vuông góc với hướng middle line
                Cv2.Line(canvas, (Point)ToCv(new P2(centerPoint.X - perpUx * _pointSize.Value, centerPoint.Y - perpUy * _pointSize.Value)),
                                  (Point)ToCv(new P2(centerPoint.X + perpUx * _pointSize.Value, centerPoint.Y + perpUy * _pointSize.Value)),
                                  GeometryColors.FromName(_centerLineColor.Value), 1);
            }
            string label = isParallel ? "Middle Line (Parallel)" : "Middle Line (Average)";
            Cv2.PutText(canvas, label, (Point)ToCv(new P2(centerPoint.X + 8, centerPoint.Y - 8)), HersheyFonts.HersheySimplex, 0.4, Scalar.White, 1);
        }

        // ----- Bước 6: Đẩy kết quả ra cổng output -----
        _outImage.Value = new MatVisionImage(canvas);
        _outMiddleLine.Value = middleLine;
        _outDistanceToLine1.Value = distToLine1;
        _outDistanceToLine2.Value = distToLine2;
        _outLineAngle.Value = lineAngle;
        _outLineLength.Value = extendLen * 2.0; // Tổng chiều dài = 2 lần độ mở rộng mỗi phía
        _outIsParallel.Value = isParallel;

        context.Log($"LineCenterLine: IsParallel={isParallel}, Angle={lineAngle:F1}deg, Dist1={distToLine1:F1}, Dist2={distToLine2:F1}");
    }

    /// <summary>Chuyển Point2d (hệ thống) sang Point2f (OpenCV) để vẽ - tránh lặp code ép kiểu nhiều lần.</summary>
    private static Point2f ToCv(P2 p) => new Point2f((float)p.X, (float)p.Y);
}