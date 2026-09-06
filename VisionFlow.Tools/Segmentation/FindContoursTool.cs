// ==================== Vai trò chính:                Tìm toàn bộ đường bao (contour) trong ảnh nhị phân, lọc theo hình học, vẽ overlay trực quan
// ==================== Thành phần / Class tiêu biểu: FindContoursTool
// ==================== Phụ thuộc vào:                OpenCvSharp + Core.Models (Point2d, RectRegion)
// ==================== Pattern / Kỹ thuật nổi bật:   FindContours + multi-stage filtering (Area/Perimeter/PointCount/Convexity/Solidity/Circularity), Sort + TopN

using System;
using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Models;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;
using P2 = VisionFlow.Core.Models.Point2d; // Alias ngắn gọn giống các file khác trong dự án

namespace VisionFlow.Tools.Segmentation; // Namespace/thư mục mới dành cho nhóm Segmentation

/// <summary>
/// Tool tìm đường bao (contour) - nền tảng của mọi thao tác đo lường/đếm/nhận dạng hình dạng sau này.
/// Nhận vào ảnh NHỊ PHÂN (đã qua Threshold/Morphology ở bước trước), quét toàn bộ vùng trắng và
/// trả về danh sách đường bao đã được lọc theo diện tích/chu vi/độ lồi/độ tròn, kèm ảnh overlay trực quan.
/// </summary>
[ToolMetadata("FindContours", DisplayName = "Find Contours", Category = "Segmentation",
    Description = "Detect and filter contours from a binary image")]
public sealed class FindContoursTool : VisionTool
{
    #region 1. Khai báo Port (In/Out)
    private readonly InputPort<IVisionImage> _input;                 // Cổng vào: ảnh nhị phân (0/255)
    private readonly OutputPort<IVisionImage> _outImage;             // Cổng ra: ảnh overlay đã vẽ contour/bbox/tâm
    private readonly OutputPort<int> _outContourCount;               // Tổng số contour tìm được (trước lọc)
    private readonly OutputPort<int> _outFilteredContourCount;       // Số contour còn lại sau lọc
    private readonly OutputPort<double[]> _outContourAreas;          // Mảng diện tích từng contour (sau lọc)
    private readonly OutputPort<double[]> _outContourPerimeters;     // Mảng chu vi từng contour (sau lọc)
    private readonly OutputPort<P2[]> _outContourCentroids;          // Mảng tâm từng contour (sau lọc)
    private readonly OutputPort<RectRegion[]> _outBoundingBoxes;     // Mảng bounding box từng contour (sau lọc)
    private readonly OutputPort<P2[]> _outLargestContour;            // Contour có diện tích lớn nhất (rỗng nếu không có)
    private readonly OutputPort<P2[][]> _outAllContours;             // TOÀN BỘ contour sau lọc -> nối vào ExtractObjectsFromContours
    #endregion

    #region 2. Khai báo Parameter theo từng Tab
    // ----- Tab Segmentation -----
    private readonly ToolParameter<RetrievalModes> _retrievalMode;
    private readonly ToolParameter<ContourApproximationModes> _approximationMode;

    // ----- Tab Region (bộ lọc hình học) -----
    private readonly ToolParameter<bool> _enableAreaFilter;
    private readonly ToolParameter<double> _minArea;
    private readonly ToolParameter<double> _maxArea;
    private readonly ToolParameter<bool> _enablePerimeterFilter;
    private readonly ToolParameter<double> _minPerimeter;
    private readonly ToolParameter<double> _maxPerimeter;
    private readonly ToolParameter<bool> _enablePointCountFilter;
    private readonly ToolParameter<int> _minPointCount;
    private readonly ToolParameter<int> _maxPointCount;
    private readonly ToolParameter<bool> _enableConvexityFilter;
    private readonly ToolParameter<double> _minConvexity;
    private readonly ToolParameter<bool> _enableSolidityFilter;
    private readonly ToolParameter<double> _minSolidity;
    private readonly ToolParameter<bool> _enableCircularityFilter;
    private readonly ToolParameter<double> _minCircularity;
    private readonly ToolParameter<double> _maxCircularity;

    // ----- Tab Output -----
    private readonly ToolParameter<string> _sortMode;
    private readonly ToolParameter<int> _maxResults;
    private readonly ToolParameter<bool> _drawContours;
    private readonly ToolParameter<bool> _drawFilledContours;
    private readonly ToolParameter<int> _contoursThickness;
    private readonly ToolParameter<bool> _drawBoundingBoxes;
    private readonly ToolParameter<bool> _drawMinAreaRects;
    private readonly ToolParameter<bool> _drawConvexHull;
    private readonly ToolParameter<bool> _drawCentroid;
    private readonly ToolParameter<bool> _outputAsColorImage;

    // ----- Tab Advanced -----
    private readonly ToolParameter<bool> _enableApproximation;
    private readonly ToolParameter<double> _approximationEpsilon;
    private readonly ToolParameter<bool> _enableConvexHullReplace;
    #endregion

    public FindContoursTool()
    {
        // ----- Port -----
        _input = AddInput<IVisionImage>("Image");
        _outImage = AddOutput<IVisionImage>("Image");
        _outContourCount = AddOutput<int>("ContourCount");
        _outFilteredContourCount = AddOutput<int>("FilteredContourCount");
        _outContourAreas = AddOutput<double[]>("ContourAreas");
        _outContourPerimeters = AddOutput<double[]>("ContourPerimeters");
        _outContourCentroids = AddOutput<P2[]>("ContourCentroids");
        _outBoundingBoxes = AddOutput<RectRegion[]>("BoundingBoxes");
        _outLargestContour = AddOutput<P2[]>("LargestContour");
        _outAllContours = AddOutput<P2[][]>("AllContours");

        // ----- Tab Segmentation -----
        _retrievalMode = AddParameter("RetrievalMode", RetrievalModes.External, "Retrieval Mode", category: "Segmentation", order: 1);
        _approximationMode = AddParameter("ApproximationMode", ContourApproximationModes.ApproxSimple, "Approximation Mode", category: "Segmentation", order: 2);

        // ----- Tab Region -----
        _enableAreaFilter = AddParameter("EnableAreaFilter", true, "Enable Area Filter", category: "Region", order: 1);
        _minArea = AddParameter("MinArea", 50.0, "Min Area", 0.0, 10_000_000.0, category: "Region", order: 2);
        _maxArea = AddParameter("MaxArea", 1_000_000.0, "Max Area", 0.0, 10_000_000.0, category: "Region", order: 3);
        _enablePerimeterFilter = AddParameter("EnablePerimeterFilter", false, "Enable Perimeter Filter", category: "Region", order: 4);
        _minPerimeter = AddParameter("MinPerimeter", 0.0, "Min Perimeter", 0.0, 100_000.0, category: "Region", order: 5);
        _maxPerimeter = AddParameter("MaxPerimeter", 100_000.0, "Max Perimeter", 0.0, 100_000.0, category: "Region", order: 6);
        _enablePointCountFilter = AddParameter("EnablePointCountFilter", false, "Enable Point Count Filter", category: "Region", order: 7);
        _minPointCount = AddParameter("MinPointCount", 3, "Min Point Count", 3, 100_000, category: "Region", order: 8);
        _maxPointCount = AddParameter("MaxPointCount", 100_000, "Max Point Count", 3, 100_000, category: "Region", order: 9);
        _enableConvexityFilter = AddParameter("EnableConvexityFilter", false, "Enable Convexity Filter", category: "Region", order: 10);
        _minConvexity = AddParameter("MinConvexity", 0.0, "Min Convexity", 0.0, 1.0, category: "Region", order: 11);
        _enableSolidityFilter = AddParameter("EnableSolidityFilter", false, "Enable Solidity Filter", category: "Region", order: 12);
        _minSolidity = AddParameter("MinSolidity", 0.0, "Min Solidity", 0.0, 1.0, category: "Region", order: 13);
        _enableCircularityFilter = AddParameter("EnableCircularityFilter", false, "Enable Circularity Filter", category: "Region", order: 14);
        _minCircularity = AddParameter("MinCircularity", 0.0, "Min Circularity", 0.0, 1.0, category: "Region", order: 15);
        _maxCircularity = AddParameter("MaxCircularity", 1.0, "Max Circularity", 0.0, 1.0, category: "Region", order: 16);

        // ----- Tab Output -----
        _sortMode = AddChoiceParameter("SortMode", "AreaDescending",
            new[] { "None", "AreaDescending", "AreaAscending", "PerimeterDescending", "PointCountDescending" },
            "Sort Mode", category: "Output", order: 1);
        _maxResults = AddParameter("MaxResults", 1000, "Max Results", 1, 100_000, category: "Output", order: 2);
        _drawContours = AddParameter("DrawContours", true, "Draw Contours", category: "Output", order: 3);
        _drawFilledContours = AddParameter("DrawFilledContours", false, "Draw Filled Contours", category: "Output", order: 4);
        _contoursThickness = AddParameter("ContoursThickness", 2, "Contours Thickness", 1, 20, category: "Output", order: 5);
        _drawBoundingBoxes = AddParameter("DrawBoundingBoxes", false, "Draw Bounding Boxes", category: "Output", order: 6);
        _drawMinAreaRects = AddParameter("DrawMinAreaRects", false, "Draw MinArea Rects", category: "Output", order: 7);
        _drawConvexHull = AddParameter("DrawConvexHull", false, "Draw Convex Hull", category: "Output", order: 8);
        _drawCentroid = AddParameter("DrawCentroid", false, "Draw Centroid", category: "Output", order: 9);
        _outputAsColorImage = AddParameter("OutputAsColorImage", true, "Output As Color Image", category: "Output", order: 10);

        // ----- Tab Advanced -----
        _enableApproximation = AddParameter("EnableApproximation", false, "Enable Approximation", category: "Advanced", order: 1);
        _approximationEpsilon = AddParameter("ApproximationEpsilon", 2.0, "Approximation Epsilon", 0.1, 100.0, category: "Advanced", order: 2);
        _enableConvexHullReplace = AddParameter("EnableConvexHull", false, "Replace With Convex Hull", category: "Advanced", order: 3);
    }

    /// <summary>Cấu trúc dữ liệu tạm chứa mọi đặc trưng đã tính của 1 contour, dùng nội bộ trong OnExecute.</summary>
    private sealed class ContourFeature
    {
        public Point[] Points = Array.Empty<Point>();
        public double Area;
        public double Perimeter;
        public P2 Centroid;
        public RectRegion BoundingBox;
        public double Convexity;   // HullPerimeter / Perimeter -> càng gần 1 càng lồi
        public double Solidity;    // Area / HullArea -> càng gần 1 càng đặc (ít lõm)
        public double Circularity; // 4*PI*Area / Perimeter^2 -> 1.0 là hình tròn hoàn hảo
    }

    protected override void OnExecute(IToolContext context)
    {
        Mat src = _input.Value!.AsMat(); // Lấy ma trận ảnh nhị phân từ cổng vào

        // FindContours chỉ chạy được trên ảnh 1 kênh -> tự convert nếu lỡ nhận ảnh màu
        Mat binary;
        bool binaryIsTemporary = false;
        if (src.Channels() == 1) { binary = src; }
        else { binary = new Mat(); Cv2.CvtColor(src, binary, ColorConversionCodes.BGR2GRAY); binaryIsTemporary = true; }

        // ----- Bước 1: Tìm toàn bộ contour thô theo RetrievalMode/ApproximationMode đã chọn -----
        Cv2.FindContours(binary, out Point[][] rawContours, out HierarchyIndex[] _hierarchy,
            _retrievalMode.Value, _approximationMode.Value);
        int rawCount = rawContours.Length;

        // ----- Bước 2: Tính toán đặc trưng hình học cho từng contour (kèm Approximation/ConvexHull nếu bật) -----
        var features = new List<ContourFeature>();
        foreach (var raw in rawContours)
        {
            Point[] pts = raw;

            // Tab Advanced: đơn giản hóa contour bằng Douglas-Peucker trước khi tính đặc trưng
            if (_enableApproximation.Value && pts.Length > 2)
                pts = Cv2.ApproxPolyDP(pts, _approximationEpsilon.Value, true);

            // Tab Advanced: thay contour bằng convex hull của chính nó (lấp phần lõm)
            if (_enableConvexHullReplace.Value && pts.Length > 2)
                pts = Cv2.ConvexHull(pts);

            if (pts.Length < 3) continue; // Contour quá ít điểm, không tính được diện tích/chu vi có ý nghĩa

            double area = Cv2.ContourArea(pts);
            double perimeter = Cv2.ArcLength(pts, true);
            Rect bbox = Cv2.BoundingRect(pts);
            Moments m = Cv2.Moments(pts);
            P2 centroid = m.M00 > 1e-6 ? new P2(m.M10 / m.M00, m.M01 / m.M00) : new P2(bbox.X + bbox.Width / 2.0, bbox.Y + bbox.Height / 2.0);

            Point[] hull = Cv2.ConvexHull(pts);
            double hullPerimeter = Cv2.ArcLength(hull, true);
            double hullArea = Cv2.ContourArea(hull);
            double convexity = perimeter > 1e-6 ? Math.Clamp(hullPerimeter / perimeter, 0.0, 1.0) : 0.0;
            double solidity = hullArea > 1e-6 ? Math.Clamp(area / hullArea, 0.0, 1.0) : 0.0;
            double circularity = perimeter > 1e-6 ? Math.Clamp(4.0 * Math.PI * area / (perimeter * perimeter), 0.0, 1.0) : 0.0;

            features.Add(new ContourFeature
            {
                Points = pts,
                Area = area,
                Perimeter = perimeter,
                Centroid = centroid,
                BoundingBox = new RectRegion(bbox.X, bbox.Y, bbox.Width, bbox.Height),
                Convexity = convexity,
                Solidity = solidity,
                Circularity = circularity
            });
        }

        // ----- Bước 3: Áp dụng các bộ lọc Tab Region (chỉ áp dụng khi Enable tương ứng = true) -----
        IEnumerable<ContourFeature> filtered = features;
        if (_enableAreaFilter.Value)
            filtered = filtered.Where(f => f.Area >= _minArea.Value && f.Area <= _maxArea.Value);
        if (_enablePerimeterFilter.Value)
            filtered = filtered.Where(f => f.Perimeter >= _minPerimeter.Value && f.Perimeter <= _maxPerimeter.Value);
        if (_enablePointCountFilter.Value)
            filtered = filtered.Where(f => f.Points.Length >= _minPointCount.Value && f.Points.Length <= _maxPointCount.Value);
        if (_enableConvexityFilter.Value)
            filtered = filtered.Where(f => f.Convexity >= _minConvexity.Value);
        if (_enableSolidityFilter.Value)
            filtered = filtered.Where(f => f.Solidity >= _minSolidity.Value);
        if (_enableCircularityFilter.Value)
            filtered = filtered.Where(f => f.Circularity >= _minCircularity.Value && f.Circularity <= _maxCircularity.Value);

        // ----- Bước 4: Sắp xếp theo SortMode -----
        var sorted = _sortMode.Value switch
        {
            "AreaAscending" => filtered.OrderBy(f => f.Area),
            "PerimeterDescending" => filtered.OrderByDescending(f => f.Perimeter),
            "PointCountDescending" => filtered.OrderByDescending(f => f.Points.Length),
            "None" => filtered,
            _ => filtered.OrderByDescending(f => f.Area), // Mặc định AreaDescending
        };

        // ----- Bước 5: Giới hạn MaxResults -----
        var finalList = sorted.Take(Math.Max(1, _maxResults.Value)).ToList();

        // ----- Bước 6: Vẽ overlay theo Tab Output -----
        Mat overlay = new Mat();
        if (_outputAsColorImage.Value && binary.Channels() == 1)
            Cv2.CvtColor(binary, overlay, ColorConversionCodes.GRAY2BGR);
        else
            overlay = binary.Clone();

        foreach (var f in finalList)
        {
            if (_drawFilledContours.Value)
                Cv2.DrawContours(overlay, new[] { f.Points }, -1, Scalar.Yellow, -1); // -1 = tô kín toàn bộ vùng
            else if (_drawContours.Value)
                Cv2.DrawContours(overlay, new[] { f.Points }, -1, Scalar.Yellow, _contoursThickness.Value);

            if (_drawBoundingBoxes.Value)
            {
                var bb = f.BoundingBox;
                Cv2.Rectangle(overlay, new Rect((int)bb.X, (int)bb.Y, (int)bb.Width, (int)bb.Height), Scalar.Cyan, 1);
            }
            if (_drawMinAreaRects.Value && f.Points.Length >= 3)
            {
                RotatedRect rr = Cv2.MinAreaRect(f.Points);
                Point2f[] box = rr.Points();
                for (int i = 0; i < 4; i++)
                    Cv2.Line(overlay, (Point)box[i], (Point)box[(i + 1) % 4], Scalar.Magenta, 1);
            }
            if (_drawConvexHull.Value && f.Points.Length >= 3)
                Cv2.Polylines(overlay, new[] { Cv2.ConvexHull(f.Points) }, true, Scalar.Orange, 1);
            if (_drawCentroid.Value)
                Cv2.Circle(overlay, new Point((int)f.Centroid.X, (int)f.Centroid.Y), 3, Scalar.Red, -1);
        }

        // ----- Bước 7: Đẩy toàn bộ kết quả ra cổng output -----
        _outImage.Value = new MatVisionImage(overlay);
        _outContourCount.Value = rawCount;
        _outFilteredContourCount.Value = finalList.Count;
        _outContourAreas.Value = finalList.Select(f => f.Area).ToArray();
        _outContourPerimeters.Value = finalList.Select(f => f.Perimeter).ToArray();
        _outContourCentroids.Value = finalList.Select(f => f.Centroid).ToArray();
        _outBoundingBoxes.Value = finalList.Select(f => f.BoundingBox).ToArray();
        _outLargestContour.Value = finalList.Count > 0
            ? finalList.OrderByDescending(f => f.Area).First().Points.Select(p => new P2(p.X, p.Y)).ToArray()
            : Array.Empty<P2>();
        _outAllContours.Value = finalList.Select(f => f.Points.Select(p => new P2(p.X, p.Y)).ToArray()).ToArray();

        if (binaryIsTemporary) binary.Dispose();

        context.Log($"FindContours: {rawCount} raw -> {finalList.Count} after filter (RetrievalMode={_retrievalMode.Value}).");
    }
}