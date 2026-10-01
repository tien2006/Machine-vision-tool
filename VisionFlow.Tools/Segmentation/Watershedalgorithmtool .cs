// ==================== Vai trò chính:                Tách các vật thể DÍNH/CHỒNG lên nhau (VD ốc vít chồng đè) mà BlobAnalysis thường gộp nhầm thành 1 khối
// ==================== Thành phần / Class tiêu biểu: WatershedAlgorithmTool
// ==================== Phụ thuộc vào:                OpenCvSharp (Cv2.Watershed, Cv2.DistanceTransform, Cv2.ConnectedComponents)
// ==================== Pattern / Kỹ thuật nổi bật:   Watershed cổ điển kiểu "coins tutorial" của OpenCV: sinh
//                       marker (sure background/foreground qua Distance Transform) rồi để Cv2.Watershed tự vẽ
//                       đường phân thủy (-1) tại nơi 2 "vùng nước" từ các marker khác nhau gặp nhau.

using System;
using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Models; // Point2d
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;
using Point2d = VisionFlow.Core.Models.Point2d;

namespace VisionFlow.Tools.Segmentation; // Cùng thư mục với ActiveContourTool

/// <summary>Nguồn sinh marker (hạt giống) ban đầu cho Watershed.</summary>
public enum WatershedMarkerSource
{
    Auto,    // Tool tự sinh marker bằng pipeline Threshold + Distance Transform
    External // Dùng ma trận marker CV_32SC1 do người dùng tự cung cấp qua cổng Markers
}

/// <summary>
/// WatershedAlgorithm: mô phỏng "đổ nước" từ các điểm hạt giống (marker) - nơi 2 vùng nước gặp nhau tạo
/// thành đường phân thủy (watershed line), chính là biên giới giữa các vật thể. Mạnh nhất khi cần TÁCH
/// các vật thể dính nhau mà Threshold+BlobAnalysis thông thường sẽ gộp nhầm thành 1 khối duy nhất.
/// </summary>
[ToolMetadata(
    "WatershedAlgorithm",
    DisplayName = "Watershed Algorithm",
    Category = "Segmentation",
    Description = "Separate touching/overlapping objects using the watershed (flood-fill from markers) algorithm")]
public sealed class WatershedAlgorithmTool : VisionTool
{
    #region 1. Khai báo Port (In/Out)
    private readonly InputPort<IVisionImage> _imageMatrix;
    private readonly InputPort<IVisionImage> _markers; // CV_32SC1 - chỉ dùng khi MarkerSource=External

    private readonly OutputPort<IVisionImage> _outImageMatrix;
    private readonly OutputPort<IVisionImage> _outLabelMatrix; // Xuất dưới dạng ảnh CV_32SC1 bọc trong IVisionImage, giống quy ước LabelMatrix của ConnectedComponents
    private readonly OutputPort<int> _outRegionCount;
    private readonly OutputPort<IReadOnlyList<double>> _outRegionAreas;
    private readonly OutputPort<IReadOnlyList<Point2d>> _outRegionCentroids;
    private readonly OutputPort<IReadOnlyList<Rect>> _outBoundingBoxes;
    private readonly OutputPort<IVisionImage> _outInteractiveVisualization;
    #endregion

    #region 2. Khai báo Parameter
    // --- Tab Markers ---
    private readonly ToolParameter<WatershedMarkerSource> _markerSource;
    private readonly ToolParameter<int> _thresholdValue;
    private readonly ToolParameter<bool> _useOtsu;
    private readonly ToolParameter<bool> _invertBinary;
    private readonly ToolParameter<double> _foregroundRatio;
    private readonly ToolParameter<int> _dilateBackgroundIterations;

    // --- Tab Region ---
    private readonly ToolParameter<bool> _enableAreaFilter;
    private readonly ToolParameter<double> _minArea;
    private readonly ToolParameter<double> _maxArea;

    // --- Tab Output ---
    private readonly ToolParameter<bool> _drawContours;
    private readonly ToolParameter<int> _contourThickness;
    private readonly ToolParameter<bool> _drawBoundaries;
    private readonly ToolParameter<bool> _drawBoundingBoxes;
    private readonly ToolParameter<bool> _drawCentroids;
    private readonly ToolParameter<bool> _colorizeRegions;
    private readonly ToolParameter<bool> _outputAsColorImage;
    #endregion

    public WatershedAlgorithmTool()
    {
        _imageMatrix = AddInput<IVisionImage>("ImageMatrix", "Image Matrix");
        _markers = AddInput<IVisionImage>("Markers", "Markers", optional: true);

        _outImageMatrix = AddOutput<IVisionImage>("ImageMatrix", "Image Matrix");
        _outLabelMatrix = AddOutput<IVisionImage>("LabelMatrix", "Label Matrix");
        _outRegionCount = AddOutput<int>("RegionCount", "Region Count");
        _outRegionAreas = AddOutput<IReadOnlyList<double>>("RegionAreas", "Region Areas");
        _outRegionCentroids = AddOutput<IReadOnlyList<Point2d>>("RegionCentroids", "Region Centroids");
        _outBoundingBoxes = AddOutput<IReadOnlyList<Rect>>("BoundingBoxes", "Bounding Boxes");
        _outInteractiveVisualization = AddOutput<IVisionImage>("InteractiveVisualization", "Interactive Visualization");

        _markerSource = AddParameter("MarkerSource", WatershedMarkerSource.Auto, "Marker Source", category: "Markers", order: 1);
        _thresholdValue = AddParameter("ThresholdValue", 128, "Threshold Value", min: 0, max: 255, category: "Markers", order: 2);
        _useOtsu = AddParameter("UseOtsu", true, "Use Otsu", category: "Markers", order: 3);
        _invertBinary = AddParameter("InvertBinary", false, "Invert Binary", category: "Markers", order: 4);
        _foregroundRatio = AddParameter("ForegroundRatio", 0.5, "Foreground Ratio", min: 0.01, max: 0.99, category: "Markers", order: 5);
        _dilateBackgroundIterations = AddParameter("DilateBackgroundIterations", 3, "Dilate Background Iterations", min: 1, max: 20, category: "Markers", order: 6);

        _enableAreaFilter = AddParameter("EnableAreaFilter", true, "Enable Area Filter", category: "Region", order: 1);
        _minArea = AddParameter("MinArea", 100.0, "Min Area", min: 0.0, max: 10_000_000.0, category: "Region", order: 2);
        _maxArea = AddParameter("MaxArea", 100000.0, "Max Area", min: 1.0, max: 100_000_000.0, category: "Region", order: 3);

        _drawContours = AddParameter("DrawContours", true, "Draw Contours", category: "Output", order: 1);
        _contourThickness = AddParameter("ContourThickness", 2, "Contour Thickness", min: 1, max: 20, category: "Output", order: 2);
        _drawBoundaries = AddParameter("DrawBoundaries", true, "Draw Boundaries", category: "Output", order: 3);
        _drawBoundingBoxes = AddParameter("DrawBoundingBoxes", false, "Draw Bounding Boxes", category: "Output", order: 4);
        _drawCentroids = AddParameter("DrawCentroids", true, "Draw Centroids", category: "Output", order: 5);
        _colorizeRegions = AddParameter("ColorizeRegions", false, "Colorize Regions", category: "Output", order: 6);
        _outputAsColorImage = AddParameter("OutputAsColorImage", true, "Output As Color Image", category: "Output", order: 7);
    }

    protected override void OnExecute(IToolContext context)
    {
        Mat src = _imageMatrix.Value!.AsMat();
        Mat colorSrc = ToColor(src); // Watershed yêu cầu ảnh 3 kênh - tự chuyển đổi nếu ảnh xám/BGRA

        // ----- Bước 1: chuẩn bị ma trận marker (CV_32SC1) -----
        Mat markers = _markerSource.Value == WatershedMarkerSource.External
            ? BuildExternalMarkers(context)
            : BuildAutoMarkers(src, context);

        // ----- Bước 2: chạy Watershed - OpenCV tự ghi kết quả ĐÈ LÊN markers -----
        // Sau lệnh này: markers chứa -1 tại đường biên watershed, 1 = nền, >=2 = từng vật thể riêng biệt
        Cv2.Watershed(colorSrc, markers);

        // ----- Bước 3: gom thống kê từng vùng (label >= 2), áp dụng bộ lọc diện tích -----
        var regions = CollectRegions(markers, src.Width, src.Height);

        if (_enableAreaFilter.Value)
        {
            var kept = new List<(int Label, double Area, Point2d Centroid, Rect BBox)>();
            foreach (var r in regions)
            {
                if (r.Area >= _minArea.Value && r.Area <= _maxArea.Value)
                    kept.Add(r);
                else
                    ResetLabelToBackground(markers, r.Label); // Vùng bị loại -> gán lại về nền (1) trong LabelMatrix để nhất quán với RegionCount đã lọc
            }
            regions = kept;
        }

        // ----- Bước 4: vẽ 2 lớp output theo đúng pattern ImageMatrix (nền ảnh gốc) + InteractiveVisualization (nền đen) -----
        Mat baseImage = _outputAsColorImage.Value ? colorSrc.Clone() : src.Clone();
        Mat overlayOnBlack = new Mat(src.Size(), MatType.CV_8UC3, Scalar.Black);

        if (_colorizeRegions.Value)
            ColorizeInPlace(baseImage, markers, regions); // Đây là "kết quả thuật toán" tô màu trực tiếp, không phải overlay

        DrawAnnotations(baseImage, markers, regions);
        DrawAnnotations(overlayOnBlack, markers, regions);

        _outImageMatrix.Value = new MatVisionImage(baseImage);
        _outInteractiveVisualization.Value = new MatVisionImage(overlayOnBlack);
        _outLabelMatrix.Value = new MatVisionImage(markers); // Giữ nguyên CV_32SC1 - downstream tự đọc bằng .At<int>() nếu cần xử lý tiếp
        _outRegionCount.Value = regions.Count;
        _outRegionAreas.Value = regions.Select(r => r.Area).ToList();
        _outRegionCentroids.Value = regions.Select(r => r.Centroid).ToList();
        _outBoundingBoxes.Value = regions.Select(r => r.BBox).ToList();

        colorSrc.Dispose();

        context.Log($"WatershedAlgorithm: {regions.Count} region(s) after filtering (source={_markerSource.Value})");
    }

    #region Sinh marker
    private Mat BuildAutoMarkers(Mat src, IToolContext context)
    {
        using Mat gray = src.Channels() == 1 ? src.Clone() : ToGray(src);

        // ----- Nhị phân hoá: Otsu hoặc ngưỡng cố định -----
        using Mat binary = new Mat();
        var thresholdType = _invertBinary.Value ? ThresholdTypes.BinaryInv : ThresholdTypes.Binary;
        if (_useOtsu.Value)
            Cv2.Threshold(gray, binary, 0, 255, thresholdType | ThresholdTypes.Otsu);
        else
            Cv2.Threshold(gray, binary, _thresholdValue.Value, 255, thresholdType);

        // ----- Sure background: giãn nở (dilate) vùng foreground - phần CÒN LẠI chắc chắn là nền -----
        using Mat kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        using Mat sureBg = new Mat();
        Cv2.Dilate(binary, sureBg, kernel, iterations: _dilateBackgroundIterations.Value);

        // ----- Sure foreground: Distance Transform + ngưỡng theo ForegroundRatio * khoảng cách lớn nhất -----
        using Mat distTransform = new Mat();
        Cv2.DistanceTransform(binary, distTransform, DistanceTypes.L2, DistanceTransformMasks.Mask5);
        Cv2.MinMaxLoc(distTransform, out _, out double maxDist);

        using Mat sureFg8U = new Mat();
        Cv2.Threshold(distTransform, sureFg8U, _foregroundRatio.Value * maxDist, 255, ThresholdTypes.Binary);
        sureFg8U.ConvertTo(sureFg8U, MatType.CV_8U);

        // ----- Unknown = sureBg - sureFg: vùng "chưa chắc" sẽ được Watershed tự phân định -----
        using Mat unknown = new Mat();
        Cv2.Subtract(sureBg, sureFg8U, unknown);

        // ----- ConnectedComponents trên sureFg để đánh số nhãn ban đầu cho từng "hạt giống" -----
        using Mat labels32S = new Mat();
        Cv2.ConnectedComponents(sureFg8U, labels32S, PixelConnectivity.Connectivity8, MatType.CV_32S);

        // Theo đúng quy ước OpenCV: cộng thêm 1 để nền không còn là 0 (0 dành riêng cho vùng "unknown" trước khi gọi Watershed),
        // sau đó gán 0 cho vùng unknown để Cv2.Watershed tự lấp đầy từ các hạt giống lân cận.
        Mat markers = new Mat();
        labels32S.ConvertTo(markers, MatType.CV_32S, 1, 1);
        markers.SetTo(0, unknown);

        context.Log($"WatershedAlgorithm: auto markers - maxDist={maxDist:F1}, seeds found via ConnectedComponents.");
        return markers;
    }

    private Mat BuildExternalMarkers(IToolContext context)
    {
        var markerImg = _markers.Value ?? throw new ToolExecutionException(
            "WatershedAlgorithm: MarkerSource=External requires the Markers input to be connected.");

        Mat m = markerImg.AsMat();
        if (m.Type() != MatType.CV_32SC1)
        {
            context.Log("WatershedAlgorithm: Markers input is not CV_32SC1, converting automatically.");
            var converted = new Mat();
            m.ConvertTo(converted, MatType.CV_32S);
            return converted;
        }
        return m.Clone(); // Clone để Cv2.Watershed (ghi đè in-place) không làm hỏng dữ liệu gốc của node phía trước
    }
    #endregion

    #region Thống kê vùng
    private static List<(int Label, double Area, Point2d Centroid, Rect BBox)> CollectRegions(Mat markers, int width, int height)
    {
        // Gom toạ độ tất cả pixel theo từng label >= 2 (label 1 = nền, -1 = biên watershed, bỏ qua cả 2)
        var sumX = new Dictionary<int, double>();
        var sumY = new Dictionary<int, double>();
        var count = new Dictionary<int, int>();
        var minX = new Dictionary<int, int>();
        var minY = new Dictionary<int, int>();
        var maxX = new Dictionary<int, int>();
        var maxY = new Dictionary<int, int>();

        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int label = markers.At<int>(y, x);
                if (label < 2) continue;

                sumX[label] = sumX.GetValueOrDefault(label) + x;
                sumY[label] = sumY.GetValueOrDefault(label) + y;
                count[label] = count.GetValueOrDefault(label) + 1;
                minX[label] = Math.Min(minX.GetValueOrDefault(label, int.MaxValue), x);
                minY[label] = Math.Min(minY.GetValueOrDefault(label, int.MaxValue), y);
                maxX[label] = Math.Max(maxX.GetValueOrDefault(label, int.MinValue), x);
                maxY[label] = Math.Max(maxY.GetValueOrDefault(label, int.MinValue), y);
            }

        var result = new List<(int, double, Point2d, Rect)>();
        foreach (var label in count.Keys)
        {
            double area = count[label];
            var centroid = new Point2d(sumX[label] / area, sumY[label] / area);
            var bbox = new Rect(minX[label], minY[label], maxX[label] - minX[label] + 1, maxY[label] - minY[label] + 1);
            result.Add((label, area, centroid, bbox));
        }
        return result;
    }

    /// <summary>Gán lại toàn bộ pixel của 1 label bị loại (không đạt Area filter) về nhãn nền (1).</summary>
    private static void ResetLabelToBackground(Mat markers, int label)
    {
        using Mat mask = new Mat();
        Cv2.InRange(markers, new Scalar(label), new Scalar(label), mask); // InRange thay Compare vì hỗ trợ chắc chắn với Mat kiểu CV_32S
        markers.SetTo(1, mask);
    }
    #endregion

    #region Vẽ kết quả
    private static void ColorizeInPlace(Mat image, Mat markers, List<(int Label, double Area, Point2d Centroid, Rect BBox)> regions)
    {
        foreach (var r in regions)
        {
            // Màu ngẫu nhiên nhưng CỐ ĐỊNH theo Label (seed = label) - cùng 1 vùng sẽ luôn ra cùng 1 màu giữa các lần chạy, tiện debug
            var rng = new Random(r.Label * 9973);
            var color = new Scalar(rng.Next(60, 255), rng.Next(60, 255), rng.Next(60, 255));

            using Mat mask = new Mat();
            Cv2.InRange(markers, new Scalar(r.Label), new Scalar(r.Label), mask);
            image.SetTo(color, mask);
        }
    }

    private void DrawAnnotations(Mat target, Mat markers, List<(int Label, double Area, Point2d Centroid, Rect BBox)> regions)
    {
        if (_drawBoundaries.Value)
        {
            // Đường watershed = pixel có giá trị -1 trong markers
            using Mat boundaryMask = new Mat();
            Cv2.InRange(markers, new Scalar(-1), new Scalar(-1), boundaryMask); // Đường watershed = pixel giá trị đúng -1
            target.SetTo(new Scalar(0, 0, 255), boundaryMask); // Đỏ - đúng màu kinh điển hay dùng minh hoạ watershed
        }

        foreach (var r in regions)
        {
            if (_drawContours.Value)
            {
                using Mat regionMask = new Mat();
                Cv2.InRange(markers, new Scalar(r.Label), new Scalar(r.Label), regionMask);
                Cv2.FindContours(regionMask, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
                Cv2.DrawContours(target, contours, -1, new Scalar(0, 255, 0), _contourThickness.Value); // Xanh lá
            }

            if (_drawBoundingBoxes.Value)
                Cv2.Rectangle(target, r.BBox, new Scalar(255, 255, 0), thickness: 1); // Vàng nhạt (cyan-ish trong BGR)

            if (_drawCentroids.Value)
                Cv2.Circle(target, new Point((int)r.Centroid.X, (int)r.Centroid.Y), 3, new Scalar(0, 165, 255), thickness: -1); // Cam
        }
    }

    private static Mat ToGray(Mat src)
    {
        var gray = new Mat();
        Cv2.CvtColor(src, gray, src.Channels() == 4 ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY);
        return gray;
    }

    private static Mat ToColor(Mat src)
    {
        if (src.Channels() == 3) return src.Clone();
        var color = new Mat();
        Cv2.CvtColor(src, color, src.Channels() == 1 ? ColorConversionCodes.GRAY2BGR : ColorConversionCodes.BGRA2BGR);
        return color;
    }
    #endregion
}