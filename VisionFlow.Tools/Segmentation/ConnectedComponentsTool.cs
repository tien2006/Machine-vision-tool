// ==================== Vai trò chính:                Gán nhãn từng vùng liên thông (connected region) trong ảnh nhị phân, kèm thống kê Area/BBox/Centroid
// ==================== Thành phần / Class tiêu biểu: ConnectedComponentsTool
// ==================== Phụ thuộc vào:                OpenCvSharp + Core.Models (Point2d, RectRegion)
// ==================== Pattern / Kỹ thuật nổi bật:   Cv2.ConnectedComponentsWithStats (thuật toán Union-Find/Two-Pass Labeling)

using System;
using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Models;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;
using P2 = VisionFlow.Core.Models.Point2d;

namespace VisionFlow.Tools.Segmentation;

/// <summary>
/// Tool gán nhãn vùng liên thông: mỗi cụm pixel trắng liền kề nhau trong ảnh nhị phân sẽ được gán
/// 1 con số label duy nhất (1, 2, 3...). Nhanh hơn FindContours đáng kể khi chỉ cần Area/BoundingBox/Centroid
/// (không cần hình dạng đường viền chi tiết) vì không phải duyệt từng điểm biên.
/// </summary>
[ToolMetadata("ConnectedComponents", DisplayName = "Connected Components", Category = "Segmentation",
    Description = "Label connected regions in a binary image with Area/BoundingBox/Centroid statistics")]
public sealed class ConnectedComponentsTool : VisionTool
{
    #region 1. Khai báo Port (In/Out)
    private readonly InputPort<IVisionImage> _input;             // Cổng vào: ảnh xám hoặc màu (tool tự nhị phân hóa nội bộ)
    private readonly OutputPort<IVisionImage> _outImage;         // Cổng ra: ảnh overlay hiển thị kết quả
    private readonly OutputPort<IVisionImage> _outLabelMatrix;   // Cổng ra: ma trận nhãn thô (CV_32S), pixel = label ID
    private readonly OutputPort<int> _outComponentCount;         // Tổng số vùng liên thông hợp lệ (sau lọc)
    private readonly OutputPort<double[]> _outAreas;             // Mảng diện tích từng vùng
    private readonly OutputPort<RectRegion[]> _outBoundingBoxes; // Mảng bounding box từng vùng
    private readonly OutputPort<P2[]> _outCentroids;             // Mảng tâm khối từng vùng
    #endregion

    #region 2. Khai báo Parameter theo từng Tab
    // ----- Tab Binarization -----
    private readonly ToolParameter<bool> _autoThreshold;   // Mở rộng thêm so với tài liệu gốc: cho phép Otsu tự động thay vì luôn nhập tay
    private readonly ToolParameter<double> _thresholdValue;
    private readonly ToolParameter<bool> _invertBinary;
    private readonly ToolParameter<string> _connectivity; // "4" hoặc "8"

    // ----- Tab Region -----
    private readonly ToolParameter<bool> _enableAreaFilter;
    private readonly ToolParameter<double> _minArea;
    private readonly ToolParameter<double> _maxArea;

    // ----- Tab Sorting -----
    private readonly ToolParameter<string> _sortMode;
    private readonly ToolParameter<int> _maxResults;

    // ----- Tab Output -----
    private readonly ToolParameter<bool> _drawContours;
    private readonly ToolParameter<int> _contoursThickness;
    private readonly ToolParameter<bool> _drawBoundingBoxes;
    private readonly ToolParameter<bool> _drawCentroids;
    private readonly ToolParameter<bool> _drawLabels;
    private readonly ToolParameter<bool> _outputAsColorImage;
    private readonly ToolParameter<bool> _colorizeComponents;
    #endregion

    public ConnectedComponentsTool()
    {
        // ----- Port -----
        _input = AddInput<IVisionImage>("Image");
        _outImage = AddOutput<IVisionImage>("Image");
        _outLabelMatrix = AddOutput<IVisionImage>("LabelMatrix");
        _outComponentCount = AddOutput<int>("ComponentCount");
        _outAreas = AddOutput<double[]>("Areas");
        _outBoundingBoxes = AddOutput<RectRegion[]>("BoundingBoxes");
        _outCentroids = AddOutput<P2[]>("Centroids");

        // ----- Tab Binarization -----
        _autoThreshold = AddParameter("AutoThreshold", true, "Auto Threshold (Otsu)", category: "Binarization", order: 1);
        _thresholdValue = AddParameter("ThresholdValue", 128.0, "Threshold Value", 0.0, 255.0, category: "Binarization", order: 2);
        _invertBinary = AddParameter("InvertBinary", false, "Invert Binary", category: "Binarization", order: 3);
        _connectivity = AddChoiceParameter("Connectivity", "8", new[] { "4", "8" }, "Connectivity", category: "Binarization", order: 4);

        // ----- Tab Region -----
        _enableAreaFilter = AddParameter("EnableAreaFilter", true, "Enable Area Filter", category: "Region", order: 1);
        _minArea = AddParameter("MinArea", 50.0, "Min Area", 0.0, 10_000_000.0, category: "Region", order: 2);
        _maxArea = AddParameter("MaxArea", 1_000_000.0, "Max Area", 0.0, 10_000_000.0, category: "Region", order: 3);

        // ----- Tab Sorting -----
        _sortMode = AddChoiceParameter("SortMode", "AreaDescending",
            new[] { "None", "AreaDescending", "AreaAscending", "PositionXY" }, "Sort Mode", category: "Sorting", order: 1);
        _maxResults = AddParameter("MaxResults", 1000, "Max Results", 1, 100_000, category: "Sorting", order: 2);

        // ----- Tab Output -----
        _drawContours = AddParameter("DrawContours", true, "Draw Contours", category: "Output", order: 1);
        _contoursThickness = AddParameter("ContoursThickness", 2, "Contours Thickness", 1, 20, category: "Output", order: 2);
        _drawBoundingBoxes = AddParameter("DrawBoundingBoxes", false, "Draw Bounding Boxes", category: "Output", order: 3);
        _drawCentroids = AddParameter("DrawCentroids", true, "Draw Centroids", category: "Output", order: 4);
        _drawLabels = AddParameter("DrawLabels", true, "Draw Labels", category: "Output", order: 5);
        _outputAsColorImage = AddParameter("OutputAsColorImage", true, "Output As Color Image", category: "Output", order: 6);
        _colorizeComponents = AddParameter("ColorizeComponents", false, "Colorize Components", category: "Output", order: 7);
    }

    protected override void OnExecute(IToolContext context)
    {
        Mat src = _input.Value!.AsMat();

        // ----- Bước 1: Chuyển ảnh sang xám nếu cần -----
        Mat gray; bool grayIsTemp = false;
        if (src.Channels() == 1) { gray = src; }
        else { gray = new Mat(); Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY); grayIsTemp = true; }

        // ----- Bước 2: Nhị phân hóa (Tab Binarization) -----
        Mat binary = new Mat();
        ThresholdTypes baseType = _invertBinary.Value ? ThresholdTypes.BinaryInv : ThresholdTypes.Binary;
        if (_autoThreshold.Value)
            Cv2.Threshold(gray, binary, 0, 255, baseType | ThresholdTypes.Otsu);
        else
            Cv2.Threshold(gray, binary, _thresholdValue.Value, 255, baseType);

        // ----- Bước 3: Gán nhãn vùng liên thông kèm thống kê nhanh -----
        PixelConnectivity connectivity = _connectivity.Value == "4" ? PixelConnectivity.Connectivity4 : PixelConnectivity.Connectivity8;
        Mat labels = new Mat(), stats = new Mat(), centroids = new Mat();
        int numLabels = Cv2.ConnectedComponentsWithStats(binary, labels, stats, centroids, connectivity, MatType.CV_32S);

        // ----- Bước 4: Duyệt từng label (bỏ label 0 = nền), đọc thẳng từ bảng Stats/Centroids (KHÔNG cần dò contour -> rất nhanh) -----
        var raw = new List<(int Label, double Area, RectRegion BBox, P2 Centroid)>();
        for (int label = 1; label < numLabels; label++)
        {
            double area = stats.At<int>(label, (int)ConnectedComponentsTypes.Area);
            int left = stats.At<int>(label, (int)ConnectedComponentsTypes.Left);
            int top = stats.At<int>(label, (int)ConnectedComponentsTypes.Top);
            int w = stats.At<int>(label, (int)ConnectedComponentsTypes.Width);
            int h = stats.At<int>(label, (int)ConnectedComponentsTypes.Height);
            double cx = centroids.At<double>(label, 0);
            double cy = centroids.At<double>(label, 1);
            raw.Add((label, area, new RectRegion(left, top, w, h), new P2(cx, cy)));
        }

        // ----- Bước 5: Lọc theo diện tích (Tab Region) -----
        IEnumerable<(int Label, double Area, RectRegion BBox, P2 Centroid)> filtered = raw;
        if (_enableAreaFilter.Value)
            filtered = filtered.Where(x => x.Area >= _minArea.Value && x.Area <= _maxArea.Value);

        // ----- Bước 6: Sắp xếp + giới hạn MaxResults (Tab Sorting) -----
        var sorted = _sortMode.Value switch
        {
            "AreaAscending" => filtered.OrderBy(x => x.Area),
            "PositionXY" => filtered.OrderBy(x => x.Centroid.Y).ThenBy(x => x.Centroid.X),
            "None" => filtered,
            _ => filtered.OrderByDescending(x => x.Area),
        };
        var finalList = sorted.Take(Math.Max(1, _maxResults.Value)).ToList();

        // ----- Bước 7: Vẽ overlay (Tab Output) -----
        Mat overlay = _outputAsColorImage.Value ? new Mat() : gray.Clone();
        if (_outputAsColorImage.Value) Cv2.CvtColor(gray, overlay, ColorConversionCodes.GRAY2BGR);

        int idx = 0;
        foreach (var c in finalList)
        {
            Scalar color = _colorizeComponents.Value ? ComponentColor(idx, finalList.Count) : Scalar.Yellow;

            if (_drawContours.Value)
            {
                // Cần contour thật để vẽ đường viền -> chỉ dò contour CHO RIÊNG label này (mask so sánh == label), tránh tốn CPU cho toàn ảnh
                using Mat labelMask = new Mat();
                Cv2.Compare(labels, c.Label, labelMask, CmpTypes.EQ);
                Cv2.FindContours(labelMask, out Point[][] cts, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
                if (cts.Length > 0)
                    Cv2.DrawContours(overlay, cts, -1, color, _contoursThickness.Value);
            }
            if (_drawBoundingBoxes.Value)
            {
                var bb = c.BBox;
                Cv2.Rectangle(overlay, new Rect((int)bb.X, (int)bb.Y, (int)bb.Width, (int)bb.Height), Scalar.Cyan, 1);
            }
            if (_drawCentroids.Value)
                Cv2.Circle(overlay, new Point((int)c.Centroid.X, (int)c.Centroid.Y), 3, Scalar.Red, -1);
            if (_drawLabels.Value)
                Cv2.PutText(overlay, $"#{c.Label}", new Point((int)c.Centroid.X + 5, (int)c.Centroid.Y - 5),
                    HersheyFonts.HersheySimplex, 0.4, Scalar.LimeGreen, 1);
            idx++;
        }

        // ----- Bước 8: Đẩy kết quả ra cổng output -----
        _outImage.Value = new MatVisionImage(overlay);
        _outLabelMatrix.Value = new MatVisionImage(labels.Clone()); // Clone vì "labels" sẽ bị Dispose ngay dưới đây
        _outComponentCount.Value = finalList.Count;
        _outAreas.Value = finalList.Select(x => x.Area).ToArray();
        _outBoundingBoxes.Value = finalList.Select(x => x.BBox).ToArray();
        _outCentroids.Value = finalList.Select(x => x.Centroid).ToArray();

        binary.Dispose(); labels.Dispose(); stats.Dispose(); centroids.Dispose();
        if (grayIsTemp) gray.Dispose();

        context.Log($"ConnectedComponents: {numLabels - 1} raw -> {finalList.Count} after filter (connectivity={_connectivity.Value}).");
    }

    /// <summary>Sinh màu cầu vồng (HSV wheel) khác nhau cho từng component theo chỉ số, dùng khi ColorizeComponents=true.</summary>
    private static Scalar ComponentColor(int index, int total)
    {
        double hueDeg = total > 0 ? (index * 180.0 / Math.Max(1, total)) : 0; // OpenCV Hue chạy 0-179
        using Mat hsv = new Mat(1, 1, MatType.CV_8UC3, new Scalar(hueDeg, 200, 255));
        using Mat bgr = new Mat();
        Cv2.CvtColor(hsv, bgr, ColorConversionCodes.HSV2BGR);
        Vec3b px = bgr.At<Vec3b>(0, 0);
        return new Scalar(px.Item0, px.Item1, px.Item2);
    }
}