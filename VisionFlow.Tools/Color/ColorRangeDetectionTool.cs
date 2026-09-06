// ==================== Vai trò chính:                Lọc vùng màu theo dải RGB thuần, xuất mask + contour + thống kê sẵn cho pipeline downstream
// ==================== Thành phần / Class tiêu biểu: ColorRangeDetectionTool
// ==================== Phụ thuộc vào:                OpenCvSharp + Core.Models (Point2d)
// ==================== Pattern / Kỹ thuật nổi bật:   Cv2.InRange trên BGR trực tiếp, Contours xuất kiểu P2[][] tương thích FindContoursTool

using System;
using System.Linq;
using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Models;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;
using P2 = VisionFlow.Core.Models.Point2d;

namespace VisionFlow.Tools.Color;

/// <summary>
/// Lọc màu theo dải RGB Min-Max thuần túy - đơn giản hơn ColorAnalyzer nhưng cho dữ liệu cụ thể để
/// xử lý downstream: BinaryMask (đưa vào FindContours), Contours (đưa THẲNG vào ExtractObjectsFromContours
/// vì dùng chung kiểu P2[][]). Nhạy với ánh sáng đổi - cần môi trường ánh sáng ổn định.
/// </summary>
[ToolMetadata("ColorRangeDetection", DisplayName = "Color Range Detection", Category = "Color",
    Description = "Filter pixels by RGB Min-Max range, output mask/contours/statistics")]
public sealed class ColorRangeDetectionTool : VisionTool
{
    private readonly InputPort<IVisionImage> _input;
    private readonly OutputPort<IVisionImage> _outImage;
    private readonly OutputPort<IVisionImage> _outBinaryMask;
    private readonly OutputPort<P2[][]> _outContours;
    private readonly OutputPort<int> _outBlobCount;
    private readonly OutputPort<double> _outTotalArea;
    private readonly OutputPort<P2> _outCenterOfMass;

    // ----- Tab Color (dải RGB) - mặc định đặt cho vật ĐỎ theo đúng tài liệu -----
    private readonly ToolParameter<double> _redMin, _redMax;
    private readonly ToolParameter<double> _greenMin, _greenMax;
    private readonly ToolParameter<double> _blueMin, _blueMax;

    // ----- Tab Range -----
    private readonly ToolParameter<double> _minBlobArea;

    public ColorRangeDetectionTool()
    {
        _input = AddInput<IVisionImage>("Image");
        _outImage = AddOutput<IVisionImage>("Image");
        _outBinaryMask = AddOutput<IVisionImage>("BinaryMask");
        _outContours = AddOutput<P2[][]>("Contours");
        _outBlobCount = AddOutput<int>("BlobCount");
        _outTotalArea = AddOutput<double>("TotalArea");
        _outCenterOfMass = AddOutput<P2>("CenterOfMass");

        _redMin = AddParameter("RedMin", 100.0, "Red Min", 0.0, 255.0, category: "Color", order: 1);
        _redMax = AddParameter("RedMax", 255.0, "Red Max", 0.0, 255.0, category: "Color", order: 2);
        _greenMin = AddParameter("GreenMin", 0.0, "Green Min", 0.0, 255.0, category: "Color", order: 3);
        _greenMax = AddParameter("GreenMax", 100.0, "Green Max", 0.0, 255.0, category: "Color", order: 4);
        _blueMin = AddParameter("BlueMin", 0.0, "Blue Min", 0.0, 255.0, category: "Color", order: 5);
        _blueMax = AddParameter("BlueMax", 100.0, "Blue Max", 0.0, 255.0, category: "Color", order: 6);

        _minBlobArea = AddParameter("MinBlobArea", 100.0, "Min Blob Area", 0.0, 10_000_000.0, category: "Range", order: 1);
    }

    protected override void OnExecute(IToolContext context)
    {
        Mat srcRaw = _input.Value!.AsMat();
        Mat src = ColorUtil.EnsureBgr(srcRaw, context, "ColorRangeDetection");

        // ----- Bước 1: InRange trực tiếp trên BGR (thứ tự Scalar trong OpenCV luôn là B,G,R) -----
        var lower = new Scalar(_blueMin.Value, _greenMin.Value, _redMin.Value);
        var upper = new Scalar(_blueMax.Value, _greenMax.Value, _redMax.Value);
        Mat mask = new Mat();
        Cv2.InRange(src, lower, upper, mask);

        // ----- Bước 2: Tìm contour, lọc theo MinBlobArea -----
        Cv2.FindContours(mask, out Point[][] rawContours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        var kept = rawContours.Where(c => Cv2.ContourArea(c) >= _minBlobArea.Value).ToArray();

        double totalArea = kept.Sum(c => Cv2.ContourArea(c));
        double sumCx = 0, sumCy = 0;
        foreach (var c in kept)
        {
            Moments m = Cv2.Moments(c);
            if (m.M00 > 1e-6) { sumCx += m.M10 / m.M00 * (m.M00); sumCy += m.M01 / m.M00 * (m.M00); } // Trọng số theo diện tích từng vùng
        }
        P2 centerOfMass = totalArea > 1e-6 ? new P2(sumCx / totalArea, sumCy / totalArea) : new P2(0, 0);

        // ----- Bước 3: Vẽ overlay -----
        Mat overlay = src.Clone();
        Cv2.DrawContours(overlay, kept, -1, Scalar.LimeGreen, 2);
        if (kept.Length > 0)
            Cv2.Circle(overlay, new Point((int)centerOfMass.X, (int)centerOfMass.Y), 5, Scalar.Red, -1);

        _outImage.Value = new MatVisionImage(overlay);
        _outBinaryMask.Value = new MatVisionImage(mask);
        _outContours.Value = kept.Select(c => c.Select(p => new P2(p.X, p.Y)).ToArray()).ToArray();
        _outBlobCount.Value = kept.Length;
        _outTotalArea.Value = totalArea;
        _outCenterOfMass.Value = centerOfMass;

        if (!ReferenceEquals(src, srcRaw)) src.Dispose();

        context.Log($"ColorRangeDetection: {kept.Length} blobs, TotalArea={totalArea:F0}, Center=({centerOfMass.X:F0},{centerOfMass.Y:F0})");
    }
}