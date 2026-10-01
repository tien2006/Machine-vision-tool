// ==================== Vai trò chính:                Cô lập vùng kiểm tra dạng DẢI giữa 2 đường thẳng song song - đường ray, băng tải, chân IC, vạch kẻ
// ==================== Thành phần / Class tiêu biểu: ParallelLineMaskTool
// ==================== Phụ thuộc vào:                OpenCvSharp, Core.Models (LineResult, LineSegment, Point2d)
// ==================== Pattern / Kỹ thuật nổi bật:   Composite Mask bằng tứ giác nối 2 đoạn thẳng (FillPoly) -
//                       validate tính song song (góc lệch) + khoảng cách trước khi tạo mask, giống pattern đã dùng ở AnnularMask

using System;
using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Models; // LineResult, LineSegment, Point2d
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;
using Point2d = VisionFlow.Core.Models.Point2d;

namespace VisionFlow.Tools.Utility; // Cùng thư mục với AnnularMaskTool

/// <summary>Chế độ tạo mặt nạ dựa trên 2 đường thẳng Line1/Line2 (hiện tại tài liệu chỉ định nghĩa Between).</summary>
public enum ParallelLineMaskMode
{
    Between // Giữ lại vùng KHÔNG GIAN nằm giữa 2 đường thẳng song song
}

/// <summary>
/// ParallelLineMask: cô lập vùng kiểm tra dạng dải nằm giữa 2 đường thẳng gần như song song (Line1/Line2
/// là 2 CỔNG VÀO kiểu <see cref="LineResult"/>, thường nối từ 2 node FindLineTool chạy trên 2 mép của
/// đối tượng dạng dải: đường ray băng tải, cặp chân IC, vạch kẻ đường). Trước khi tạo mask, Tool kiểm tra
/// nghiêm ngặt 2 đường có thực sự song song (MaxAngleDifference) và đủ cách xa nhau (MinLineDistance) hay
/// không - đúng pattern validate hình học đã dùng ở <see cref="AnnularMaskTool"/>.
/// </summary>
[ToolMetadata(
    "ParallelLineMask",
    DisplayName = "Parallel Line Mask",
    Category = "Utility",
    Description = "Create a band-shaped mask between two parallel lines to isolate rail/track/pin-row inspection areas")]
public sealed class ParallelLineMaskTool : VisionTool
{
    #region 1. Khai báo Port (In/Out)
    private readonly InputPort<IVisionImage> _imageMatrix; // Ảnh gốc cần xử lý
    private readonly InputPort<LineResult> _line1;
    private readonly InputPort<LineResult> _line2;

    private readonly OutputPort<IVisionImage> _outImageMatrix; // Xuất lại NGUYÊN VẸN ảnh gốc (không cắt, không đổi) - theo đúng tài liệu
    private readonly OutputPort<IVisionImage> _outMaskImage;   // Ảnh mặt nạ đen trắng
    private readonly OutputPort<IVisionImage> _outMaskedImage; // Ảnh gốc với vùng ngoài dải bị đổ đen/nền
    #endregion

    #region 2. Khai báo Parameter
    private readonly ToolParameter<ParallelLineMaskMode> _maskMode;
    private readonly ToolParameter<int> _fillValue;
    private readonly ToolParameter<int> _backgroundValue;
    private readonly ToolParameter<double> _maxAngleDifference; // Độ - sai số góc tối đa để 2 đường còn được coi là song song
    private readonly ToolParameter<double> _minLineDistance;    // Khoảng cách vuông góc tối thiểu giữa 2 đường để coi là 1 "dải" hợp lệ
    private readonly ToolParameter<bool> _validateParallel;     // Bật/tắt kiểm tra MaxAngleDifference
    private readonly ToolParameter<bool> _extendLines;          // Kéo dài 2 đường ra xa để dải mask phủ hết chiều dài cần thiết
    private readonly ToolParameter<bool> _useLineLength;        // Chỉ có tác dụng khi ExtendLines=true - xem giải thích trong OnExecute
    #endregion

    public ParallelLineMaskTool()
    {
        _imageMatrix = AddInput<IVisionImage>("ImageMatrix", "Image Matrix");
        _line1 = AddInput<LineResult>("Line1", "Line 1");
        _line2 = AddInput<LineResult>("Line2", "Line 2");

        _outImageMatrix = AddOutput<IVisionImage>("ImageMatrix", "Image Matrix");
        _outMaskImage = AddOutput<IVisionImage>("MaskImage", "Mask Image");
        _outMaskedImage = AddOutput<IVisionImage>("MaskedImage", "Masked Image");

        _maskMode = AddParameter("MaskMode", ParallelLineMaskMode.Between, "Mask Mode", category: "Mask", order: 1);
        _fillValue = AddParameter("FillValue", 255, "Fill Value", min: 0, max: 255, category: "Mask", order: 2);
        _backgroundValue = AddParameter("BackgroundValue", 0, "Background Value", min: 0, max: 255, category: "Mask", order: 3);
        _maxAngleDifference = AddParameter("MaxAngleDifference", 5.0, "Max Angle Difference", min: 0.0, max: 90.0, category: "Mask", order: 4);
        _minLineDistance = AddParameter("MinLineDistance", 5.0, "Min Line Distance", min: 0.0, max: 10000.0, category: "Mask", order: 5);
        _validateParallel = AddParameter("ValidateParallel", true, "Validate Parallel", category: "Mask", order: 6);
        _extendLines = AddParameter("ExtendLines", true, "Extend Lines", category: "Mask", order: 7);
        _useLineLength = AddParameter("UseLineLength", false, "Use Line Length", category: "Mask", order: 8);
    }

    protected override void OnExecute(IToolContext context)
    {
        Mat src = _imageMatrix.Value!.AsMat();

        var line1 = _line1.Value ?? throw new ToolExecutionException("ParallelLineMask: Line1 input is not connected.");
        var line2 = _line2.Value ?? throw new ToolExecutionException("ParallelLineMask: Line2 input is not connected.");

        // ----- Bước 1: kiểm tra tính song song (theo AngleDeg mỗi đường đã tự tính sẵn) -----
        if (_validateParallel.Value)
        {
            double diff = AngleDifference(line1.AngleDeg, line2.AngleDeg);
            if (diff > _maxAngleDifference.Value)
                throw new ToolExecutionException(
                    $"ParallelLineMask: Line1/Line2 angle difference ({diff:F1}°) exceeds MaxAngleDifference ({_maxAngleDifference.Value}°).");
        }

        // ----- Bước 2: kiểm tra khoảng cách vuông góc giữa 2 đường (dùng phương trình đường thẳng chuẩn hóa Ax+By+C=0) -----
        var eq1 = LineEquation(line1.Segment.P1, line1.Segment.P2);
        Point2d line2Mid = new((line2.Segment.P1.X + line2.Segment.P2.X) / 2.0, (line2.Segment.P1.Y + line2.Segment.P2.Y) / 2.0);
        double lineDistance = PointToLineDistance(line2Mid, eq1);

        if (lineDistance < _minLineDistance.Value)
            throw new ToolExecutionException(
                $"ParallelLineMask: distance between Line1/Line2 ({lineDistance:F1}px) is smaller than MinLineDistance ({_minLineDistance.Value}px).");

        // ----- Bước 3: chuẩn hóa hướng nối 2 đường để tứ giác không bị "vặn xoắn" -----
        // Line1/Line2 có thể được 2 node FindLineTool khác nhau quét theo chiều ngược nhau (P1<->P2 đảo thứ tự).
        // Nối sai chiều sẽ tạo tứ giác tự cắt (self-intersecting) khi FillPoly, méo hoàn toàn hình dải mong muốn.
        // Cách xử lý: thử cả 2 cách ghép cặp điểm, chọn cách cho tổng khoảng cách NGẮN HƠN (tức là 2 đầu tương ứng gần nhau).
        Point2d l2P1 = line2.Segment.P1, l2P2 = line2.Segment.P2;
        double straightPairing = Distance(line1.Segment.P1, l2P1) + Distance(line1.Segment.P2, l2P2);
        double swappedPairing = Distance(line1.Segment.P1, l2P2) + Distance(line1.Segment.P2, l2P1);
        if (swappedPairing < straightPairing)
            (l2P1, l2P2) = (l2P2, l2P1); // Đảo lại để đầu P1-P1 và P2-P2 tương ứng gần nhau nhất

        // ----- Bước 4: quyết định độ dài kéo dài mỗi đường (ExtendLines/UseLineLength) -----
        double extendLength;
        if (!_extendLines.Value)
        {
            extendLength = 0; // Dùng đúng độ dài đoạn thẳng gốc, không kéo dài thêm
        }
        else if (_useLineLength.Value)
        {
            // Kéo dài theo bội số độ dài đoạn gốc (x3 mỗi đầu) - cho dải mask "vừa đủ" quanh đoạn phát hiện được,
            // tránh phủ tràn lan không cần thiết khi ảnh rất lớn nhưng đoạn thẳng dò được chỉ ngắn
            double len1 = Distance(line1.Segment.P1, line1.Segment.P2);
            extendLength = len1 * 3.0;
        }
        else
        {
            // Mặc định: kéo dài bằng đường chéo ảnh để chắc chắn phủ hết toàn bộ chiều ảnh theo mọi hướng đường thẳng
            extendLength = Math.Sqrt(src.Width * (double)src.Width + src.Height * (double)src.Height);
        }

        var (l1Start, l1End) = ExtendSegment(line1.Segment.P1, line1.Segment.P2, extendLength);
        var (l2Start, l2End) = ExtendSegment(l2P1, l2P2, extendLength);

        // ----- Bước 5: dựng tứ giác dải giữa 2 đường và vẽ mask -----
        Point[] quad =
        {
            ToCvPoint(l1Start), ToCvPoint(l1End), ToCvPoint(l2End), ToCvPoint(l2Start)
        };

        using Mat keepMask = new Mat(src.Size(), MatType.CV_8UC1, Scalar.Black);
        Cv2.FillPoly(keepMask, new[] { quad }, Scalar.White); // OpenCV tự động kẹp các điểm nằm ngoài ảnh, không cần tự clip tay

        int fill = _fillValue.Value;
        int bg = _backgroundValue.Value;

        Mat maskOutput = new Mat(src.Size(), MatType.CV_8UC1, new Scalar(bg));
        maskOutput.SetTo(new Scalar(fill), keepMask);

        using Mat excludeMask = new Mat();
        Cv2.BitwiseNot(keepMask, excludeMask);

        Mat maskedImage = src.Clone();
        Scalar bgColor = src.Channels() == 1 ? new Scalar(bg) : new Scalar(bg, bg, bg);
        maskedImage.SetTo(bgColor, excludeMask);

        _outImageMatrix.Value = new MatVisionImage(src.Clone()); // Xuất lại nguyên vẹn ảnh gốc theo đúng tài liệu
        _outMaskImage.Value = new MatVisionImage(maskOutput);
        _outMaskedImage.Value = new MatVisionImage(maskedImage);

        context.Log($"ParallelLineMask: angleDiff={AngleDifference(line1.AngleDeg, line2.AngleDeg):F1}°, distance={lineDistance:F1}px, extend={extendLength:F0}px");
    }

    /// <summary>Chênh lệch góc giữa 2 đường thẳng, chuẩn hóa về [0,90] vì đường thẳng không phân biệt chiều (lệch 180° = cùng hướng).</summary>
    private static double AngleDifference(double angle1Deg, double angle2Deg)
    {
        double raw = Math.Abs(angle1Deg - angle2Deg) % 180.0;
        return raw > 90.0 ? 180.0 - raw : raw;
    }

    private static double Distance(Point2d a, Point2d b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>Phương trình đường thẳng chuẩn hóa Ax+By+C=0 với (A,B) là vector pháp tuyến đơn vị - theo đúng công thức Buổi 102.</summary>
    private static (double A, double B, double C) LineEquation(Point2d p1, Point2d p2)
    {
        double dx = p2.X - p1.X, dy = p2.Y - p1.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-6) return (0, 0, 0); // Đường suy biến (2 điểm trùng nhau) - tránh chia cho 0

        double a = dy / len, b = -dx / len;
        double c = -(a * p1.X + b * p1.Y);
        return (a, b, c);
    }

    private static double PointToLineDistance(Point2d p, (double A, double B, double C) line) =>
        Math.Abs(line.A * p.X + line.B * p.Y + line.C);

    /// <summary>Kéo dài đoạn thẳng P1->P2 thêm extendLength ở MỖI đầu, dọc theo đúng hướng của đoạn gốc.</summary>
    private static (Point2d start, Point2d end) ExtendSegment(Point2d p1, Point2d p2, double extendLength)
    {
        double dx = p2.X - p1.X, dy = p2.Y - p1.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-6) return (p1, p2);

        double ux = dx / len, uy = dy / len;
        return (
            new Point2d(p1.X - ux * extendLength, p1.Y - uy * extendLength),
            new Point2d(p2.X + ux * extendLength, p2.Y + uy * extendLength));
    }

    private static Point ToCvPoint(Point2d p) => new((int)Math.Round(p.X), (int)Math.Round(p.Y));
}