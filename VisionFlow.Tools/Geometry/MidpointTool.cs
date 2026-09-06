// ==================== Vai trò chính:                Tính điểm giữa và khoảng cách Euclidean giữa 2 điểm tọa độ
// ==================== Thành phần / Class tiêu biểu: MidpointTool
// ==================== Phụ thuộc vào:                OpenCvSharp + Core.Models (Point2d)
// ==================== Pattern / Kỹ thuật nổi bật:   Tool "kết nối" thuần toán học, không xử lý ma trận ảnh, chỉ vẽ overlay minh họa

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
/// Tool "kết nối": lấy 2 tọa độ từ output của các tool tìm đối tượng khác (tâm Blob, đỉnh FindLine...)
/// để tính điểm giữa và khoảng cách giữa chúng - hữu ích đo khoảng cách giữa 2 lỗ, 2 mép, 2 marker...
/// </summary>
[ToolMetadata("Midpoint", DisplayName = "Midpoint", Category = "Geometry",
    Description = "Compute the midpoint and Euclidean distance between 2 points")]
public sealed class MidpointTool : VisionTool
{
    #region 1. Khai báo Port (In/Out)
    private readonly InputPort<IVisionImage> _input;   // Ảnh nền để vẽ overlay - TÙY CHỌN (optional)
    private readonly InputPort<P2?> _point1;            // Điểm thứ nhất - TÙY CHỌN
    private readonly InputPort<P2?> _point2;            // Điểm thứ hai - TÙY CHỌN

    private readonly OutputPort<IVisionImage> _outImage;
    private readonly OutputPort<double> _outMidpointX;
    private readonly OutputPort<double> _outMidpointY;
    private readonly OutputPort<P2> _outMidpoint;
    private readonly OutputPort<double> _outDistance;
    #endregion

    #region 2. Khai báo Parameter (Tab Display)
    private readonly ToolParameter<bool> _drawPoints;
    private readonly ToolParameter<bool> _drawMidpoint;
    private readonly ToolParameter<bool> _drawLine;
    private readonly ToolParameter<string> _pointColor;
    private readonly ToolParameter<string> _midpointColor;
    private readonly ToolParameter<string> _lineColor;
    private readonly ToolParameter<int> _pointSize;
    private readonly ToolParameter<int> _lineThickness;
    #endregion

    public MidpointTool()
    {
        _input = AddInput<IVisionImage>("Image", optional: true);
        _point1 = AddInput<P2?>("Point1", optional: true);
        _point2 = AddInput<P2?>("Point2", optional: true);

        _outImage = AddOutput<IVisionImage>("Image");
        _outMidpointX = AddOutput<double>("MidpointX");
        _outMidpointY = AddOutput<double>("MidpointY");
        _outMidpoint = AddOutput<P2>("Midpoint");
        _outDistance = AddOutput<double>("Distance");

        _drawPoints = AddParameter("DrawPoints", true, "Draw Points", category: "Display", order: 1);
        _drawMidpoint = AddParameter("DrawMidpoint", true, "Draw Midpoint", category: "Display", order: 2);
        _drawLine = AddParameter("DrawLine", true, "Draw Line", category: "Display", order: 3);
        _pointColor = AddChoiceParameter("PointColor", "Cyan", GeometryColors.Names, "Point Color", category: "Display", order: 4);
        _midpointColor = AddChoiceParameter("MidpointColor", "Red", GeometryColors.Names, "Midpoint Color", category: "Display", order: 5);
        _lineColor = AddChoiceParameter("LineColor", "Yellow", GeometryColors.Names, "Line Color", category: "Display", order: 6);
        _pointSize = AddParameter("PointSize", 4, "Point Size", 1, 50, category: "Display", order: 7);
        _lineThickness = AddParameter("LineThickness", 2, "Line Thickness", 1, 20, category: "Display", order: 8);
    }

    protected override void OnExecute(IToolContext context)
    {
        // ----- Bước 1: Lấy ảnh nền hoặc tự tạo canvas đen 640x480 nếu không có ảnh đầu vào -----
        Mat canvas = _input.Value != null ? _input.Value.AsMat().Clone() : new Mat(480, 640, MatType.CV_8UC3, Scalar.All(0));
        if (canvas.Channels() == 1) { Mat c3 = new Mat(); Cv2.CvtColor(canvas, c3, ColorConversionCodes.GRAY2BGR); canvas.Dispose(); canvas = c3; }

        // ----- Bước 2: Kiểm tra dữ liệu đầu vào - nếu thiếu 1 trong 2 điểm thì báo lỗi mềm, trả về giá trị mặc định -----
        if (_point1.Value is not P2 p1 || _point2.Value is not P2 p2)
        {
            context.Log("MidpointTool: Point1 hoặc Point2 bị thiếu -> trả về giá trị mặc định (0,0), Distance=0.");
            _outImage.Value = new MatVisionImage(canvas);
            _outMidpointX.Value = 0; _outMidpointY.Value = 0; _outMidpoint.Value = new P2(0, 0); _outDistance.Value = 0;
            return; // Dừng sớm, không tính toán tiếp vì thiếu dữ liệu
        }

        // ----- Bước 3: Tính điểm giữa và khoảng cách Euclidean -----
        var midpoint = new P2((p1.X + p2.X) / 2.0, (p1.Y + p2.Y) / 2.0); // Công thức trung điểm cổ điển
        double distance = Math.Sqrt(Math.Pow(p2.X - p1.X, 2) + Math.Pow(p2.Y - p1.Y, 2)); // Định lý Pythagoras

        // ----- Bước 4: Vẽ overlay minh họa theo Tab Display -----
        if (_drawLine.Value)
            Cv2.Line(canvas, new Point((int)p1.X, (int)p1.Y), new Point((int)p2.X, (int)p2.Y), GeometryColors.FromName(_lineColor.Value), _lineThickness.Value);
        if (_drawPoints.Value)
        {
            Cv2.Circle(canvas, new Point((int)p1.X, (int)p1.Y), _pointSize.Value, GeometryColors.FromName(_pointColor.Value), -1);
            Cv2.Circle(canvas, new Point((int)p2.X, (int)p2.Y), _pointSize.Value, GeometryColors.FromName(_pointColor.Value), -1);
        }
        if (_drawMidpoint.Value)
            Cv2.Circle(canvas, new Point((int)midpoint.X, (int)midpoint.Y), _pointSize.Value + 3, GeometryColors.FromName(_midpointColor.Value), -1);

        // ----- Bước 5: Đẩy kết quả ra cổng output -----
        _outImage.Value = new MatVisionImage(canvas);
        _outMidpointX.Value = midpoint.X;
        _outMidpointY.Value = midpoint.Y;
        _outMidpoint.Value = midpoint;
        _outDistance.Value = distance;

        context.Log($"Midpoint: ({midpoint.X:F1},{midpoint.Y:F1}), Distance={distance:F2}px");
    }
}