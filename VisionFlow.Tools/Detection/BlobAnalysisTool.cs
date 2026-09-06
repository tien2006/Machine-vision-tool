// ==================== Vai trò chính:                Tìm và đếm các "vật" (blob) trong ảnh bằng phân ngưỡng sáng-tối + phân tích contour/hierarchy, hỗ trợ vùng Inclusion/Exclusion và chế độ Blob/Hole/All
// ==================== Thành phần / Class tiêu biểu: BlobAnalysisTool
// ==================== Phụ thuộc vào:                OpenCvSharp (FindContours + Hierarchy, FitEllipse, ConvexHull) + Core.Models (VisionBlob, BlobAnalysisResult, ExclusionPolygonInfo) + System.Text.Json
// ==================== Pattern / Kỹ thuật nổi bật:   Contour Hierarchy (RetrievalModes.CComp) để tách Blob/Hole tự nhiên + Inclusion/Exclusion Region Masking + Sub-pixel qua Upscale-rồi-Rescale-ngược

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Models;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;
using P2 = VisionFlow.Core.Models.Point2d;

namespace VisionFlow.Tools.Detection;

/// <summary>
/// Công cụ phân tích Blob (Connected-Component Analysis) - phiên bản đầy đủ theo đúng Tài liệu thuật toán mục tiêu:
/// 1. (Tuỳ chọn) Tiền xử lý sub-pixel: làm mịn biên (EdgeRefinementMethod) rồi phóng to ảnh InterpolationFactor lần.
/// 2. Nhị phân hoá bằng BinaryThreshold đơn giản (KHÔNG có Otsu built-in - cần Otsu/Adaptive thì dùng Threshold
///    tool riêng rồi đưa ảnh binary vào, đúng như tài liệu ghi chú).
/// 3. (Tuỳ chọn) NoiseReduction: Open rồi Close morphology nhẹ để gỡ nhiễu chấm pixel.
/// 4. Áp Inclusion Region (nếu có: AND với binary) rồi Exclusion Region (tô đen đè lên binary).
/// 5. Tách Blob/Hole tự nhiên bằng Cv2.FindContours với RetrievalModes.CComp (2 tầng hierarchy):
///    contour có Parent = -1 là Blob (vùng sáng/vật), contour có Parent != -1 là Hole (lỗ bên trong vật).
///    ConnectivityLabel quyết định giữ Blob-only / Hole-only / All (cả 2).
/// 6. Tính đầy đủ đặc trưng hình học từng contour rồi RESCALE NGƯỢC về đúng toạ độ ảnh gốc (nếu có dùng sub-pixel).
/// 7. Lọc MinArea/MaxArea, sắp xếp SortBy, giới hạn MaxBlobCount.
/// 8. Vẽ 2 lớp overlay riêng: ImageMatrix (đè lên ảnh gốc) và InteractiveVisualization (đè lên nền ĐEN - để
///    OverlayRendererTool ghép lại sau này bằng kỹ thuật "đen = trong suốt").
/// </summary>
[ToolMetadata("BlobAnalysis", DisplayName = "Blob Analysis", Category = "Detection",
    Description = "Connected-component blob analysis with Inclusion/Exclusion regions and Blob/Hole/All labeling.")]
public sealed class BlobAnalysisTool : VisionTool
{
    #region 1. Ports
    private readonly InputPort<IVisionImage> _input; // ImageMatrix: ảnh gốc (xám hoặc màu, tự convert + tự threshold theo BinaryThreshold)

    private readonly OutputPort<IVisionImage> _outImage;                            // ImageMatrix: ảnh gốc + overlay (số thứ tự, hộp bao, đường viền)
    private readonly OutputPort<int> _outBlobCount;                                 // BlobCount: số blob sau khi đã lọc + giới hạn
    private readonly OutputPort<IReadOnlyList<VisionBlob>> _outBlobs;               // Blobs: danh sách chi tiết từng blob
    private readonly OutputPort<P2[]> _outExclusionPolygonPoints;                   // ExclusionPolygonPoints: gộp toàn bộ điểm của mọi Exclusion/Inclusion Region
    private readonly OutputPort<IReadOnlyList<ExclusionPolygonInfo>> _outExclusionPolygons; // ExclusionPolygons: thông tin từng vùng riêng biệt
    private readonly OutputPort<IVisionImage> _outInteractiveVisualization;         // InteractiveVisualization: overlay trên nền ĐEN, dùng cho OverlayRenderer

    // Bonus tiện dụng (không có trong tài liệu nhưng giữ lại để tương thích các pipeline đã nối CompareTool/OverlayRenderer từ trước)
    private readonly OutputPort<BlobAnalysisResult> _outResult;
    private readonly OutputPort<double> _outTotalArea;
    #endregion

    #region 2. Parameters
    // --- Tab ExclusionRegions ---
    private readonly ToolParameter<bool> _enableExclusionRoi;     // Vai trò MẶC ĐỊNH cho ROI mới vẽ trên UI (không phải công tắc toàn cục)
    private readonly ToolParameter<string> _exclusionRegionsJson; // Danh sách vùng ROI (Rectangle/Circle/Polygon), lưu dạng JSON

    // --- Tab Detection ---
    private readonly ToolParameter<string> _objectType;        // "WhiteObjects" (mặc định) | "BlackObjects"
    private readonly ToolParameter<double> _binaryThreshold;   // Ngưỡng sáng-tối đơn giản (0-255) - KHÔNG có Otsu built-in
    private readonly ToolParameter<double> _minArea;
    private readonly ToolParameter<double> _maxArea;
    private readonly ToolParameter<string> _connectivityLabel; // "Blob" (mặc định) | "Hole" | "All"

    // --- Tab Advanced ---
    private readonly ToolParameter<bool> _enableDebugMode;
    private readonly ToolParameter<string> _sortBy;                  // "None" | "Area" | "CenterX" | "CenterY"
    private readonly ToolParameter<bool> _useSubPixelAccuracy;
    private readonly ToolParameter<string> _edgeRefinementMethod;    // "None" | "Gaussian" | "Bilateral" | "AnisotropicDiffusion"
    private readonly ToolParameter<int> _processingTimeout;          // ms
    private readonly ToolParameter<int> _interpolationFactor;        // 1-10
    private readonly ToolParameter<bool> _noiseReduction;

    // --- Tab Results ---
    private readonly ToolParameter<int> _maxBlobCount; // 1-500
    #endregion

    public BlobAnalysisTool()
    {
        _input = AddInput<IVisionImage>("Image", "Image Matrix");

        _outImage = AddOutput<IVisionImage>("Image", "Image Matrix");
        _outBlobCount = AddOutput<int>("BlobCount", "Blob Count");
        _outBlobs = AddOutput<IReadOnlyList<VisionBlob>>("Blobs", "Blobs");
        _outExclusionPolygonPoints = AddOutput<P2[]>("ExclusionPolygonPoints", "Exclusion Polygon Points");
        _outExclusionPolygons = AddOutput<IReadOnlyList<ExclusionPolygonInfo>>("ExclusionPolygons", "Exclusion Polygons");
        _outInteractiveVisualization = AddOutput<IVisionImage>("InteractiveVisualization", "Interactive Visualization");

        _outResult = AddOutput<BlobAnalysisResult>("Result", "Blob Result (bonus)");
        _outTotalArea = AddOutput<double>("TotalArea", "Total Area (bonus)");

        _enableExclusionRoi = AddParameter<bool>("EnableExclusionROI", false, "Enable Exclusion ROI (default role)", category: "ExclusionRegions", order: 1);
        _exclusionRegionsJson = AddParameter<string>("ExclusionRegions", "[]", "Exclusion Regions (JSON)", category: "ExclusionRegions", order: 2);

        _objectType = AddChoiceParameter("ObjectType", "WhiteObjects", new[] { "WhiteObjects", "BlackObjects" }, "Object Type", category: "Detection", order: 1);
        _binaryThreshold = AddParameter<double>("BinaryThreshold", 128.0, "Binary Threshold", min: 0.0, max: 255.0, category: "Detection", order: 2);
        _minArea = AddParameter<double>("MinArea", 50.0, "Min Area", min: 0.0, max: 10_000_000.0, category: "Detection", order: 3);
        _maxArea = AddParameter<double>("MaxArea", 1_000_000.0, "Max Area", min: 0.0, max: 10_000_000.0, category: "Detection", order: 4);
        _connectivityLabel = AddChoiceParameter("ConnectivityLabel", "Blob", new[] { "Blob", "Hole", "All" }, "Connectivity Label", category: "Detection", order: 5);

        _enableDebugMode = AddParameter<bool>("EnableDebugMode", false, "Enable Debug Mode", category: "Advanced", order: 1);
        _sortBy = AddChoiceParameter("SortBy", "Area", new[] { "None", "Area", "CenterX", "CenterY" }, "Sort By", category: "Advanced", order: 2);
        _useSubPixelAccuracy = AddParameter<bool>("UseSubPixelAccuracy", false, "Use Sub-Pixel Accuracy", category: "Advanced", order: 3);
        _edgeRefinementMethod = AddChoiceParameter("EdgeRefinementMethod", "Gaussian",
            new[] { "None", "Gaussian", "Bilateral", "AnisotropicDiffusion" }, "Edge Refinement Method", category: "Advanced", order: 4);
        _processingTimeout = AddParameter<int>("ProcessingTimeout", 5000, "Processing Timeout (ms)", min: 100, max: 60000, category: "Advanced", order: 5);
        _interpolationFactor = AddParameter<int>("InterpolationFactor", 2, "Interpolation Factor", min: 1, max: 10, category: "Advanced", order: 6);
        _noiseReduction = AddParameter<bool>("NoiseReduction", true, "Noise Reduction", category: "Advanced", order: 7);

        _maxBlobCount = AddParameter<int>("MaxBlobCount", 200, "Max Blob Count", min: 1, max: 500, category: "Results", order: 1);
    }

    protected override void OnExecute(IToolContext context)
    {
        var stopwatch = Stopwatch.StartNew();

        Mat src = _input.Value!.AsMat();
        Mat gray = ToGray(src, out bool grayOwned);

        // ----- Bước 1: Tiền xử lý sub-pixel (tuỳ chọn) -----
        double scaleFactor = 1.0;
        Mat working = gray;
        bool workingOwned = false;
        if (_useSubPixelAccuracy.Value)
        {
            using Mat refined = ApplyEdgeRefinement(gray, _edgeRefinementMethod.Value);
            scaleFactor = Math.Max(1, _interpolationFactor.Value);
            working = new Mat();
            Cv2.Resize(refined, working, new Size(0, 0), scaleFactor, scaleFactor, InterpolationFlags.Cubic);
            workingOwned = true;
        }

        // ----- Bước 2: Nhị phân hoá đơn giản theo BinaryThreshold (KHÔNG Otsu) -----
        Mat binary = new Mat();
        bool darkObjects = _objectType.Value == "BlackObjects";
        var thresholdFlags = darkObjects ? ThresholdTypes.BinaryInv : ThresholdTypes.Binary;
        Cv2.Threshold(working, binary, _binaryThreshold.Value, 255, thresholdFlags);

        // ----- Bước 3: Khử nhiễu nhẹ (tuỳ chọn) -----
        if (_noiseReduction.Value)
        {
            using Mat kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(3, 3));
            Cv2.MorphologyEx(binary, binary, MorphTypes.Open, kernel);
            Cv2.MorphologyEx(binary, binary, MorphTypes.Close, kernel);
        }

        // ----- Bước 4: Parse + áp dụng Inclusion/Exclusion Region -----
        List<ExclusionRegionDef> regions = ParseExclusionRegions(_exclusionRegionsJson.Value);
        var inclusionRegions = regions.Where(r => r.Enabled && !r.IsExclusion).ToList();
        var exclusionRegions = regions.Where(r => r.Enabled && r.IsExclusion).ToList();

        if (inclusionRegions.Count > 0)
        {
            using Mat inclusionMask = Mat.Zeros(binary.Size(), MatType.CV_8UC1);
            foreach (var r in inclusionRegions)
                FillRegionScaled(inclusionMask, r, scaleFactor, Scalar.White);
            Cv2.BitwiseAnd(binary, inclusionMask, binary); // Lấy hợp (OR) các vùng Inclusion đã được vẽ chung vào 1 mask, rồi AND 1 lần
        }
        foreach (var r in exclusionRegions)
            FillRegionScaled(binary, r, scaleFactor, Scalar.Black); // Tô đen từng vùng Exclusion, loại pixel khỏi vùng cấm

        // ----- Bước 5: Tách Blob/Hole bằng Contour Hierarchy (CComp = 2 tầng: outer + hole) -----
        Cv2.FindContours(binary, out Point[][] contours, out HierarchyIndex[] hierarchy,
            RetrievalModes.CComp, ContourApproximationModes.ApproxSimple);

        string mode = _connectivityLabel.Value;
        double inv = 1.0 / scaleFactor; // Hệ số quy đổi ngược từ toạ độ "working" (có thể đã phóng to) về toạ độ ảnh gốc
        var rawBlobs = new List<VisionBlob>();
        bool timedOut = false;

        for (int i = 0; i < contours.Length; i++)
        {
            if (stopwatch.ElapsedMilliseconds > _processingTimeout.Value) { timedOut = true; break; } // ----- ProcessingTimeout -----

            bool isHole = hierarchy[i].Parent != -1; // Contour có "cha" -> nó nằm bên trong 1 contour khác -> đó là Hole
            bool include = mode switch { "Hole" => isHole, "All" => true, _ => !isHole }; // "Blob" (mặc định): chỉ contour ngoài cùng
            if (!include) continue;

            var contour = contours[i];
            double areaScaled = Math.Abs(Cv2.ContourArea(contour));
            if (areaScaled < 1e-6) continue;

            double perimeterScaled = Cv2.ArcLength(contour, true);
            Moments m = Cv2.Moments(contour);
            if (Math.Abs(m.M00) < 1e-9) continue;
            double cxScaled = m.M10 / m.M00, cyScaled = m.M01 / m.M00;
            Rect bboxScaled = Cv2.BoundingRect(contour);

            double orientation = 0, majorScaled = 0, minorScaled = 0;
            if (contour.Length >= 5)
            {
                RotatedRect ellipse = Cv2.FitEllipse(contour);
                orientation = ellipse.Angle;
                majorScaled = Math.Max(ellipse.Size.Width, ellipse.Size.Height);
                minorScaled = Math.Min(ellipse.Size.Width, ellipse.Size.Height);
            }
            double aspectRatio = minorScaled > 1e-6 ? majorScaled / minorScaled : 0;
            double roundness = perimeterScaled > 1e-6 ? Math.Clamp(4.0 * Math.PI * areaScaled / (perimeterScaled * perimeterScaled), 0.0, 1.0) : 0.0;

            Point[] hull = Cv2.ConvexHull(contour);
            double hullAreaScaled = Cv2.ContourArea(hull);
            double solidity = hullAreaScaled > 1e-6 ? Math.Clamp(areaScaled / hullAreaScaled, 0.0, 1.0) : 0.0;

            // ----- Rescale NGƯỢC toàn bộ số đo về đúng hệ toạ độ ảnh GỐC (quan trọng khi có dùng Sub-Pixel/InterpolationFactor) -----
            rawBlobs.Add(new VisionBlob
            {
                Id = i,
                Area = areaScaled * inv * inv,
                Perimeter = perimeterScaled * inv,
                CenterOfMass = new P2(cxScaled * inv, cyScaled * inv),
                BoundingBox = new RectRegion(bboxScaled.X * inv, bboxScaled.Y * inv, bboxScaled.Width * inv, bboxScaled.Height * inv),
                Orientation = orientation,
                MajorAxisLength = majorScaled * inv,
                MinorAxisLength = minorScaled * inv,
                AspectRatio = aspectRatio,
                Roundness = roundness,
                Solidity = solidity,
                Contour = contour.Select(p => new P2(p.X * inv, p.Y * inv)).ToList(),
            });
        }

        // ----- Bước 6: Lọc MinArea/MaxArea (đơn vị pixel² ảnh GỐC, không phụ thuộc sub-pixel) -----
        var filtered = rawBlobs.Where(b => b.Area >= _minArea.Value && b.Area <= _maxArea.Value).ToList();

        // ----- Bước 7: Sắp xếp theo SortBy -----
        filtered = _sortBy.Value switch
        {
            "Area" => filtered.OrderByDescending(b => b.Area).ToList(),
            "CenterX" => filtered.OrderBy(b => b.CenterOfMass.X).ToList(),
            "CenterY" => filtered.OrderBy(b => b.CenterOfMass.Y).ToList(),
            _ => filtered, // "None": giữ nguyên thứ tự phát hiện
        };

        // ----- Bước 8: Giới hạn MaxBlobCount -----
        if (filtered.Count > _maxBlobCount.Value)
            filtered = filtered.Take(_maxBlobCount.Value).ToList();

        int count = filtered.Count;
        double totalArea = filtered.Sum(b => b.Area);
        Judge judge = count > 0 ? Judge.OK : Judge.NG; // Mặc định đơn giản: có ít nhất 1 blob = OK. Muốn kiểm tra số lượng chính xác (VD phải đúng 5), nối BlobCount sang CompareTool phía sau.

        var result = new BlobAnalysisResult { Blobs = filtered, Count = count, TotalArea = totalArea, Judge = judge };

        // ----- Bước 9: Xây ExclusionPolygons/ExclusionPolygonPoints (LUÔN theo toạ độ ảnh GỐC, không scale) -----
        var exclusionPolygons = regions.Select(r => new ExclusionPolygonInfo
        {
            Id = r.Id ?? "",
            Name = r.Name ?? "",
            IsExclusion = r.IsExclusion,
            Points = RegionToPoints(r),
        }).ToList();
        P2[] allPolygonPoints = exclusionPolygons.SelectMany(p => p.Points).ToArray();

        // ----- Bước 10: Vẽ 2 lớp overlay: ImageMatrix (nền ảnh gốc) + InteractiveVisualization (nền ĐEN) -----
        Mat overlayOnBackground = new Mat();
        Cv2.CvtColor(gray, overlayOnBackground, ColorConversionCodes.GRAY2BGR);
        Mat overlayOnBlack = Mat.Zeros(gray.Size(), MatType.CV_8UC3);

        DrawRegions(overlayOnBackground, inclusionRegions, exclusionRegions);
        DrawRegions(overlayOnBlack, inclusionRegions, exclusionRegions);
        DrawBlobs(overlayOnBackground, filtered);
        DrawBlobs(overlayOnBlack, filtered);

        // ----- Bước 11: Xuất kết quả -----
        // overlayOnBackground/overlayOnBlack "cho đi" thẳng vào Output -> KHÔNG Dispose(...) chúng sau đây
        _outImage.Value = new MatVisionImage(overlayOnBackground);
        _outInteractiveVisualization.Value = new MatVisionImage(overlayOnBlack);
        _outBlobCount.Value = count;
        _outBlobs.Value = filtered;
        _outExclusionPolygonPoints.Value = allPolygonPoints;
        _outExclusionPolygons.Value = exclusionPolygons;
        _outResult.Value = result;
        _outTotalArea.Value = totalArea;

        // ----- Bước 12: Dọn dẹp tài nguyên Mat trung gian -----
        binary.Dispose();
        if (workingOwned) working.Dispose();
        if (grayOwned) gray.Dispose();

        if (timedOut)
            context.Log($"BlobAnalysis: CẢNH BÁO vượt ProcessingTimeout ({_processingTimeout.Value}ms) - kết quả có thể THIẾU blob, dừng sớm ở contour đang xử lý dở.");

        if (_enableDebugMode.Value)
            context.Log($"BlobAnalysis [DEBUG]: ObjectType={_objectType.Value}, BinaryThreshold={_binaryThreshold.Value}, " +
                $"ConnectivityLabel={mode}, SubPixel={_useSubPixelAccuracy.Value}(x{scaleFactor}), " +
                $"InclusionRegions={inclusionRegions.Count}, ExclusionRegions={exclusionRegions.Count}, " +
                $"RawContours={contours.Length}, AfterFilter={rawBlobs.Count}, AfterSort/Limit={count}.");

        context.Log($"BlobAnalysis: found {rawBlobs.Count} region(s) ({mode}), {count} after filter. TotalArea={totalArea:F0}. Judge={judge}. {stopwatch.Elapsed.TotalMilliseconds:F1}ms.");
    }

    #region 3. Helpers - Exclusion Region

    /// <summary>DTO nội bộ để parse JSON tham số ExclusionRegions - KHÔNG public, chỉ dùng riêng trong Tool này.</summary>
    private sealed class ExclusionRegionDef
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string Type { get; set; } = "Rectangle"; // "Rectangle" | "Circle" | "Polygon"
        public bool Enabled { get; set; } = true;
        public bool IsExclusion { get; set; }
        public double CenterX { get; set; }
        public double CenterY { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public double Radius { get; set; }
        public double Angle { get; set; }
        public List<PointDef>? Points { get; set; }
    }

    private sealed class PointDef { public double X { get; set; } public double Y { get; set; } }

    private static List<ExclusionRegionDef> ParseExclusionRegions(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<ExclusionRegionDef>();
        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return JsonSerializer.Deserialize<List<ExclusionRegionDef>>(json, options) ?? new List<ExclusionRegionDef>();
        }
        catch (JsonException ex)
        {
            throw new ToolExecutionException($"BlobAnalysis: tham số ExclusionRegions không phải JSON hợp lệ - {ex.Message}");
        }
    }

    /// <summary>Quy đổi 1 vùng ROI (Rectangle/Circle/Polygon) thành danh sách điểm đa giác, LUÔN theo hệ toạ độ ảnh GỐC (chưa scale).</summary>
    private static List<P2> RegionToPoints(ExclusionRegionDef r)
    {
        switch (r.Type)
        {
            case "Circle":
                {
                    var pts = new List<P2>();
                    const int n = 36; // Xấp xỉ hình tròn bằng đa giác 36 cạnh - đủ mượt để hiển thị/tính toán
                    for (int i = 0; i < n; i++)
                    {
                        double a = i * 2 * Math.PI / n;
                        pts.Add(new P2(r.CenterX + r.Radius * Math.Cos(a), r.CenterY + r.Radius * Math.Sin(a)));
                    }
                    return pts;
                }
            case "Polygon":
                return (r.Points ?? new List<PointDef>()).Select(p => new P2(p.X, p.Y)).ToList();
            default: // "Rectangle"
                {
                    double hw = r.Width / 2.0, hh = r.Height / 2.0;
                    double rad = r.Angle * Math.PI / 180.0;
                    double cos = Math.Cos(rad), sin = Math.Sin(rad);
                    P2 Rot(double dx, double dy) => new P2(r.CenterX + dx * cos - dy * sin, r.CenterY + dx * sin + dy * cos);
                    return new List<P2> { Rot(-hw, -hh), Rot(hw, -hh), Rot(hw, hh), Rot(-hw, hh) };
                }
        }
    }

    /// <summary>Vẽ 1 vùng ROI đã tô đặc (filled) lên mask, có nhân toạ độ với scaleFactor để khớp không gian xử lý sub-pixel.</summary>
    private static void FillRegionScaled(Mat mask, ExclusionRegionDef r, double scaleFactor, Scalar color)
    {
        var pts = RegionToPoints(r)
            .Select(p => new Point((int)Math.Round(p.X * scaleFactor), (int)Math.Round(p.Y * scaleFactor)))
            .ToArray();
        if (pts.Length < 3) return; // Không đủ điểm để tạo đa giác hợp lệ - bỏ qua an toàn
        Cv2.FillPoly(mask, new[] { pts }, color);
    }

    /// <summary>Vẽ viền các vùng Inclusion (xanh lá, nét liền) và Exclusion (đỏ, nét liền) lên overlay để người vận hành thấy rõ vùng đang áp dụng.</summary>
    private static void DrawRegions(Mat overlay, List<ExclusionRegionDef> inclusionRegions, List<ExclusionRegionDef> exclusionRegions)
    {
        foreach (var r in inclusionRegions)
            DrawRegionOutline(overlay, r, new Scalar(0, 200, 0));
        foreach (var r in exclusionRegions)
            DrawRegionOutline(overlay, r, new Scalar(0, 0, 200));
    }

    private static void DrawRegionOutline(Mat overlay, ExclusionRegionDef r, Scalar color)
    {
        var pts = RegionToPoints(r).Select(p => new Point((int)p.X, (int)p.Y)).ToArray();
        if (pts.Length < 3) return;
        Cv2.Polylines(overlay, new[] { pts }, true, color, 1, LineTypes.AntiAlias);
    }

    #endregion

    #region 4. Helpers - Sub-pixel & Overlay Blob

    /// <summary>Làm mịn biên trước khi phóng to sub-pixel, theo đúng EdgeRefinementMethod đã chọn.</summary>
    private static Mat ApplyEdgeRefinement(Mat gray, string method)
    {
        Mat result = gray.Clone();
        switch (method)
        {
            case "Gaussian":
                Cv2.GaussianBlur(result, result, new Size(5, 5), 1.0);
                break;
            case "Bilateral":
                {
                    Mat bf = new Mat();
                    Cv2.BilateralFilter(result, bf, 9, 50, 50);
                    result.Dispose();
                    result = bf;
                    break;
                }
            case "AnisotropicDiffusion":
                // OpenCV không có Anisotropic Diffusion (Perona-Malik) built-in - xấp xỉ bằng vài lượt Bilateral
                // Filter nhỏ liên tiếp: giữ cạnh tốt hơn Gaussian thuần, gần hiệu ứng khuếch tán dị hướng thật.
                for (int i = 0; i < 3; i++)
                {
                    Mat bf2 = new Mat();
                    Cv2.BilateralFilter(result, bf2, 5, 30, 30);
                    result.Dispose();
                    result = bf2;
                }
                break;
            case "None":
            default:
                break; // Giữ nguyên, không làm mịn
        }
        return result;
    }

    /// <summary>Vẽ contour + bounding box + tâm khối + nhãn số thứ tự cho từng Blob lên overlay (dùng chung cho cả 2 lớp Image/InteractiveVisualization).</summary>
    private static void DrawBlobs(Mat overlay, List<VisionBlob> blobs)
    {
        int idx = 1;
        foreach (var b in blobs)
        {
            var contourPts = b.Contour.Select(p => new Point((int)p.X, (int)p.Y)).ToArray();

            if (contourPts.Length > 0)
                Cv2.Polylines(overlay, new[] { contourPts }, true, Scalar.Yellow, 2, LineTypes.AntiAlias);

            var bb = b.BoundingBox;
            Cv2.Rectangle(overlay, new Rect((int)bb.X, (int)bb.Y, (int)bb.Width, (int)bb.Height), Scalar.Cyan, 1, LineTypes.AntiAlias);

            Cv2.Circle(overlay, new Point((int)b.CenterOfMass.X, (int)b.CenterOfMass.Y), 4, Scalar.Red, -1, LineTypes.AntiAlias);

            string text = $"#{idx} A={b.Area:F0}";
            Cv2.PutText(overlay, text, new Point((int)b.CenterOfMass.X + 6, (int)b.CenterOfMass.Y - 6),
                HersheyFonts.HersheySimplex, 0.4, Scalar.LimeGreen, 1, LineTypes.AntiAlias);
            idx++;
        }
    }

    private static Mat ToGray(Mat src, out bool owned)
    {
        if (src.Channels() == 1) { owned = false; return src; }
        Mat gray = new Mat();
        Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
        owned = true;
        return gray;
    }

    #endregion
}