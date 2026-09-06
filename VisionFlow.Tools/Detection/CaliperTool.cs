// ==================== Vai trò chính:                Đo cạnh (edge) sub-pixel kiểu Cognex Caliper: quét N caliper song song, đo Width/Distance/Position
// ==================== Thành phần / Class tiêu biểu: CaliperTool
// ==================== Phụ thuộc vào:                OpenCvSharp + Core.Models + Core.Ports + Core.Tools + Tools.Imaging
// ==================== Pattern / Kỹ thuật nổi bật:   Sub-pixel edge detection (nội suy Parabol), Non-max suppression 1D, 4 chế độ tìm cạnh

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

namespace VisionFlow.Tools.Detection;

/// <summary>
/// Công cụ đo cạnh (edge) độ chính xác sub-pixel theo phong cách Cognex Caliper.
/// Quy trình: Rải N caliper song song trong ROI xoay (RotatedRectRegion) -> mỗi caliper quét profile mức xám 1D
/// bằng nội suy song tuyến tính -> tìm cạnh bằng Gradient + Non-max suppression + nội suy Parabol (sub-pixel)
/// -> tùy Mode (SingleEdge/EdgePair/FirstEdge/LastEdge) chọn cạnh phù hợp -> tính Measurements (Width/Distance/Position).
/// </summary>
[ToolMetadata("CaliperTool", DisplayName = "Caliper Tool", Category = "Detection",
    Description = "Edge-finding sub-pixel theo phong cách Cognex Caliper: đo Width/Distance/Position giữa các cạnh.")]
public sealed class CaliperTool : VisionTool
{
    #region 1. Khai báo các Cổng dữ liệu (Inputs / Outputs)
    private readonly InputPort<IVisionImage> _input; // Cổng vào: ảnh gốc (xám hoặc màu, tool tự convert sang xám)

    private readonly OutputPort<IVisionImage> _outImage;                    // Ảnh gốc + overlay caliper/edge/measurement
    private readonly OutputPort<CaliperToolResult> _outResult;              // Kết quả tổng hợp (Judge, EdgeCount, BestEdge...)
    private readonly OutputPort<int> _outEdgeCount;                         // Tổng số cạnh phát hiện (tách riêng để nối nhanh vào CompareTool)
    private readonly OutputPort<List<CaliperEdge>> _outEdgePoints;          // Danh sách chi tiết từng edge point
    private readonly OutputPort<CaliperEdge?> _outBestEdge;                 // Cạnh có score cao nhất
    private readonly OutputPort<List<CaliperEdgePair>> _outEdgePairs;       // Danh sách cặp cạnh (chỉ có dữ liệu khi Mode = EdgePair)
    private readonly OutputPort<List<CaliperMeasurement>> _outMeasurements; // Kết quả đo theo từng caliper
    #endregion

    #region 2. Khai báo các Tham số cấu hình (Parameters)
    // --- Tab Region ---
    private readonly ToolParameter<RotatedRectRegion> _region; // ROI xoay: Width = chiều dài quét của MỖI caliper (phải vuông góc cạnh cần đo)
                                                               // Height = bề rộng dải ROI (nơi N caliper được rải đều dọc theo)

    // --- Tab Detection ---
    private readonly ToolParameter<int> _numberOfCalipers;   // Số caliper rải đều dọc theo Region.Height
    private readonly ToolParameter<double> _caliperLength;   // Chiều dài quét thực tế của 1 caliper (0 = dùng Region.Width)
    private readonly ToolParameter<double> _caliperWidth;    // Bề ngang lấy trung bình trước khi tính gradient (chống nhiễu răng cưa)
    private readonly ToolParameter<double> _caliperSpacing;  // Khoảng cách giữa 2 caliper liền kề (tham số tham khảo/hiển thị;
                                                             // việc rải caliper thực tế luôn chia đều theo NumberOfCalipers)
    private readonly ToolParameter<bool> _useInteractiveCalipers; // Cờ dự phòng cho UI vẽ caliper trực tiếp (bản này chưa có UI riêng)
    private readonly ToolParameter<string> _mode;             // SingleEdge | EdgePair | FirstEdge | LastEdge
    private readonly ToolParameter<int> _maxEdgesPerCaliper;  // Số cạnh ứng viên tối đa giữ lại/1 caliper trước khi chọn theo score

    // --- Tab Advanced ---
    private readonly ToolParameter<bool> _enableStrengthScoring;
    private readonly ToolParameter<double> _minStrengthScore;   // 0..1, lọc bỏ cạnh có Score thấp hơn ngưỡng này
    private readonly ToolParameter<bool> _enablePositionScoring;
    private readonly ToolParameter<double> _expectedPosition;   // 0..1 dọc theo caliper
    private readonly ToolParameter<double> _positionTolerance;  // bán kính dung sai quanh ExpectedPosition (đơn vị 0..1)
    private readonly ToolParameter<bool> _enableMeasurement;
    private readonly ToolParameter<string> _measurementType;    // Width | Distance | Position
    private readonly ToolParameter<double> _pixelToWorldScale;  // Hệ số quy đổi pixel -> đơn vị thực (mm/inch)
    private readonly ToolParameter<bool> _drawProjectionRegion;
    private readonly ToolParameter<bool> _drawCalipers;
    private readonly ToolParameter<bool> _drawEdgePoints;
    private readonly ToolParameter<bool> _drawMeasurements;
    private readonly ToolParameter<bool> _useUIOverlay; // Dự phòng renderer overlay theo zoom UI (chưa dùng ở bản này)

    // --- Tab Threshold ---
    private readonly ToolParameter<double> _startEdgeThreshold;
    private readonly ToolParameter<double> _endEdgeThreshold;
    private readonly ToolParameter<string> _startEdgePolarity; // DarkToLight | LightToDark
    private readonly ToolParameter<string> _endEdgePolarity;   // LightToDark | DarkToLight
    private readonly ToolParameter<double> _filterWidth;       // Độ rộng làm mịn profile trước khi tính gradient
    private readonly ToolParameter<bool> _subPixelAccuracy;

    // --- Bổ sung để Judge có ý nghĩa thực tế (tài liệu gốc không nêu tiêu chí OK/NG rõ ràng) ---
    private readonly ToolParameter<int> _minValidEdges;
    #endregion

    public CaliperTool()
    {
        // ---- Ports ----
        _input = AddInput<IVisionImage>("Image", "Image");
        _outImage = AddOutput<IVisionImage>("Image", "Overlay");
        _outResult = AddOutput<CaliperToolResult>("Result", "Result");
        _outEdgeCount = AddOutput<int>("EdgeCount", "Edge Count");
        _outEdgePoints = AddOutput<List<CaliperEdge>>("EdgePoints", "Edge Points");
        _outBestEdge = AddOutput<CaliperEdge?>("BestEdge", "Best Edge");
        _outEdgePairs = AddOutput<List<CaliperEdgePair>>("EdgePairs", "Edge Pairs");
        _outMeasurements = AddOutput<List<CaliperMeasurement>>("Measurements", "Measurements");

        // ---- Tab Region ----
        _region = AddParameter("Region", new RotatedRectRegion(new P2(320, 240), 60, 200, 0),
            "Search Region", category: "Region", order: 1, interaction: ParameterInteraction.RotatedRectRegion);

        // ---- Tab Detection ----
        _numberOfCalipers = AddParameter("NumberOfCalipers", 10, "Number Of Calipers", 1, 200, category: "Detection", order: 1);
        _caliperLength = AddParameter("CaliperLength", 0.0, "Caliper Length (0 = dùng Region.Width)", 0.0, 10000.0, category: "Detection", order: 2);
        _caliperWidth = AddParameter("CaliperWidth", 5.0, "Caliper Width", 1.0, 50.0, category: "Detection", order: 3);
        _caliperSpacing = AddParameter("CaliperSpacing", 10.0, "Caliper Spacing", 0.0, 10000.0, category: "Detection", order: 4);
        _useInteractiveCalipers = AddParameter("UseInteractiveCalipers", false, "Use Interactive Calipers", category: "Detection", order: 5);
        _mode = AddChoiceParameter("Mode", "EdgePair", new[] { "SingleEdge", "EdgePair", "FirstEdge", "LastEdge" }, "Mode", category: "Detection", order: 6);
        _maxEdgesPerCaliper = AddParameter("MaxEdgesPerCaliper", 4, "Max Edges Per Caliper", 1, 50, category: "Detection", order: 7);

        // ---- Tab Advanced ----
        _enableStrengthScoring = AddParameter("EnableStrengthScoring", true, "Enable Strength Scoring", category: "Advanced", order: 1);
        _minStrengthScore = AddParameter("MinStrengthScore", 0.05, "Min Strength Score", 0.0, 1.0, category: "Advanced", order: 2);
        _enablePositionScoring = AddParameter("EnablePositionScoring", false, "Enable Position Scoring", category: "Advanced", order: 3);
        _expectedPosition = AddParameter("ExpectedPosition", 0.5, "Expected Position", 0.0, 1.0, category: "Advanced", order: 4);
        _positionTolerance = AddParameter("PositionTolerance", 0.3, "Position Tolerance", 0.01, 1.0, category: "Advanced", order: 5);
        _enableMeasurement = AddParameter("EnableMeasurement", true, "Enable Measurement", category: "Advanced", order: 6);
        _measurementType = AddChoiceParameter("MeasurementType", "Width", new[] { "Width", "Distance", "Position" }, "Measurement Type", category: "Advanced", order: 7);
        _pixelToWorldScale = AddParameter("PixelToWorldScale", 1.0, "Pixel To World Scale", 0.0001, 100000.0, category: "Advanced", order: 8);
        _drawProjectionRegion = AddParameter("DrawProjectionRegion", true, "Draw Projection Region", category: "Advanced", order: 9);
        _drawCalipers = AddParameter("DrawCalipers", true, "Draw Calipers", category: "Advanced", order: 10);
        _drawEdgePoints = AddParameter("DrawEdgePoints", true, "Draw Edge Points", category: "Advanced", order: 11);
        _drawMeasurements = AddParameter("DrawMeasurements", true, "Draw Measurements", category: "Advanced", order: 12);
        _useUIOverlay = AddParameter("UseUIOverlay", false, "Use UI Overlay", category: "Advanced", order: 13);

        // ---- Tab Threshold ----
        _startEdgeThreshold = AddParameter("StartEdgeThreshold", 20.0, "Start Edge Threshold", 0.0, 255.0, category: "Threshold", order: 1);
        _endEdgeThreshold = AddParameter("EndEdgeThreshold", 20.0, "End Edge Threshold", 0.0, 255.0, category: "Threshold", order: 2);
        _startEdgePolarity = AddChoiceParameter("StartEdgePolarity", "DarkToLight", new[] { "DarkToLight", "LightToDark" }, "Start Edge Polarity", category: "Threshold", order: 3);
        _endEdgePolarity = AddChoiceParameter("EndEdgePolarity", "LightToDark", new[] { "DarkToLight", "LightToDark" }, "End Edge Polarity", category: "Threshold", order: 4);
        _filterWidth = AddParameter("FilterWidth", 1.0, "Filter Width", 0.0, 20.0, category: "Threshold", order: 5);
        _subPixelAccuracy = AddParameter("SubPixelAccuracy", true, "Sub-Pixel Accuracy", category: "Threshold", order: 6);

        _minValidEdges = AddParameter("MinValidEdges", 1, "Min Valid Edges (OK/NG)", 0, 1000, category: "Threshold", order: 7);
    }

    protected override void OnExecute(IToolContext context)
    {
        if (_input.Value == null)
            throw new ArgumentNullException(nameof(_input), "Ảnh đầu vào của CaliperTool không được rỗng!");

        var src = ((MatVisionImage)_input.Value).Mat;

        // Bước 1: Chuẩn bị ảnh xám để tính toán + ảnh màu để vẽ overlay
        bool isSrcGray = src.Channels() == 1;
        Mat gray = isSrcGray ? src : src.CvtColor(ColorConversionCodes.BGR2GRAY);
        Mat overlay = isSrcGray ? src.CvtColor(ColorConversionCodes.GRAY2BGR) : src.Clone();

        // Bước 2: Rải N caliper song song trong ROI xoay
        var region = _region.Value;
        int n = Math.Max(1, _numberOfCalipers.Value);
        double scanLength = _caliperLength.Value > 0 ? _caliperLength.Value : region.Width; // Chiều dài quét thực tế
        var calipers = GenerateCalipers(region, n, scanLength);

        // Bước 3: Quét từng caliper, tìm cạnh theo Mode đã chọn
        var mode = _mode.Value;
        var allEdges = new List<CaliperEdge>();
        var pairs = new List<CaliperEdgePair>();

        for (int i = 0; i < calipers.Count; i++)
        {
            var (start, end) = calipers[i];

            // Lấy profile mức xám 1D dọc theo caliper (đã làm mịn theo CaliperWidth + FilterWidth)
            var profile = ExtractProfile(gray, start, end, _caliperWidth.Value, _filterWidth.Value);
            if (profile.Length < 5) continue; // Quá ngắn, bỏ qua caliper này

            // Tìm toàn bộ ứng viên cạnh theo cực tính "Start" (dùng chung cho mọi Mode)
            var startCandidates = FindEdgeCandidates(profile, start, end, _startEdgeThreshold.Value, _startEdgePolarity.Value, i);
            startCandidates = ScoreAndFilter(startCandidates);
            if (startCandidates.Count == 0) continue;
            startCandidates = startCandidates.Take(_maxEdgesPerCaliper.Value).ToList();

            switch (mode)
            {
                case "SingleEdge":
                    // Chọn cạnh có Score cao nhất trên caliper này
                    allEdges.Add(startCandidates.OrderByDescending(e => e.Score).First());
                    break;

                case "FirstEdge":
                    // Cạnh xuất hiện đầu tiên dọc theo hướng quét (RelativePosition nhỏ nhất)
                    allEdges.Add(startCandidates.OrderBy(e => e.RelativePosition).First());
                    break;

                case "LastEdge":
                    // Cạnh xuất hiện cuối cùng dọc theo hướng quét (RelativePosition lớn nhất)
                    allEdges.Add(startCandidates.OrderBy(e => e.RelativePosition).Last());
                    break;

                case "EdgePair":
                default:
                    // Cạnh đầu (Start) = ứng viên Start tốt nhất
                    var startEdge = startCandidates.OrderByDescending(e => e.Score).First();
                    allEdges.Add(startEdge);

                    // Cạnh cuối (End) = ứng viên End tốt nhất, tìm SAU vị trí startEdge trên cùng caliper
                    var endCandidates = FindEdgeCandidates(profile, start, end, _endEdgeThreshold.Value, _endEdgePolarity.Value, i)
                        .Where(e => e.RelativePosition > startEdge.RelativePosition).ToList();
                    endCandidates = ScoreAndFilter(endCandidates);
                    var endEdge = endCandidates.OrderByDescending(e => e.Score).FirstOrDefault();
                    if (endEdge != null)
                    {
                        allEdges.Add(endEdge);
                        double widthPx = Distance(startEdge.Position, endEdge.Position);
                        pairs.Add(new CaliperEdgePair { CaliperIndex = i, StartEdge = startEdge, EndEdge = endEdge, WidthPx = widthPx });
                    }
                    break;
            }
        }

        // Bước 4: Tính Measurements (Width/Distance/Position) theo MeasurementType đã chọn
        var measurements = _enableMeasurement.Value
            ? BuildMeasurements(_measurementType.Value, allEdges, pairs, _pixelToWorldScale.Value)
            : new List<CaliperMeasurement>();

        // Bước 5: Tổng hợp kết quả + đánh giá Judge OK/NG
        var bestEdge = allEdges.Count > 0 ? allEdges.OrderByDescending(e => e.Score).First() : null;
        double meanWorld = measurements.Count > 0 ? measurements.Average(m => m.ValueWorld) : 0.0;

        var result = new CaliperToolResult
        {
            EdgeCount = allEdges.Count,
            BestEdge = bestEdge,
            MeanMeasurementWorld = meanWorld,
            Judge = allEdges.Count >= _minValidEdges.Value ? Judge.OK : Judge.NG
        };

        // Bước 6: Vẽ overlay trực quan lên ảnh kết quả
        DrawOverlay(overlay, region, calipers, allEdges, pairs);

        // Giải phóng ảnh xám tạm nếu do tool tự tạo ra (tránh Memory Leak khi chạy liên tục ngoài dây chuyền)
        if (!isSrcGray) gray.Dispose();

        // Gán dữ liệu ra các cổng Output
        _outImage.Value = new MatVisionImage(overlay);
        _outResult.Value = result;
        _outEdgeCount.Value = allEdges.Count;
        _outEdgePoints.Value = allEdges;
        _outBestEdge.Value = bestEdge;
        _outEdgePairs.Value = pairs;
        _outMeasurements.Value = measurements;

        context.Log($"CaliperTool: calipers={calipers.Count} edges={allEdges.Count} pairs={pairs.Count} judge={result.Judge} meanWorld={meanWorld:F3}");
    }

    #region 3. Hình học: rải Caliper trong ROI xoay
    /// <summary>
    /// Rải đều N caliper dọc theo chiều Height của ROI. Mỗi caliper là 1 đoạn thẳng dài "scanLength" (theo chiều Width),
    /// vuông góc với trục rải (Height); toàn bộ hệ được xoay theo AngleDeg quanh tâm ROI.
    /// </summary>
    private static List<(P2 Start, P2 End)> GenerateCalipers(RotatedRectRegion region, int n, double scanLength)
    {
        var list = new List<(P2 Start, P2 End)>(n);
        double angleRad = region.AngleDeg * Math.PI / 180.0;

        // u = vector đơn vị hướng quét (dọc theo Width) ; v = vector đơn vị hướng rải caliper (dọc theo Height, vuông góc u)
        double ux = Math.Cos(angleRad), uy = Math.Sin(angleRad);
        double vx = -Math.Sin(angleRad), vy = Math.Cos(angleRad);

        double halfScan = scanLength / 2.0;

        for (int i = 0; i < n; i++)
        {
            // t chạy đều từ 0 -> 1 để rải caliper dọc theo trục v trong phạm vi [-Height/2, +Height/2]
            double t = n > 1 ? (double)i / (n - 1) : 0.5;
            double spreadOffset = (t - 0.5) * region.Height;

            double baseX = region.Center.X + spreadOffset * vx;
            double baseY = region.Center.Y + spreadOffset * vy;

            var start = new P2(baseX - halfScan * ux, baseY - halfScan * uy);
            var end = new P2(baseX + halfScan * ux, baseY + halfScan * uy);
            list.Add((start, end));
        }
        return list;
    }
    #endregion

    #region 4. Trích xuất Profile 1D + tìm ứng viên cạnh
    /// <summary>
    /// Lấy mẫu mức xám dọc theo caliper bằng nội suy song tuyến tính (Bilinear), có làm mịn ngang (CaliperWidth)
    /// bằng cách lấy trung bình các đường quét song song lệch nhau theo phương vuông góc, cộng thêm làm mịn dọc (FilterWidth)
    /// bằng box filter để giảm nhiễu trước khi tính Gradient.
    /// </summary>
    private static double[] ExtractProfile(Mat gray, P2 start, P2 end, double caliperWidth, double filterWidth)
    {
        double dx = end.X - start.X, dy = end.Y - start.Y;
        double dist = Math.Sqrt(dx * dx + dy * dy);
        int n = (int)Math.Ceiling(dist);
        if (n < 2) return Array.Empty<double>();

        // Vector vuông góc đơn vị (dùng để lấy trung bình theo bề ngang CaliperWidth, giúp giảm nhiễu răng cưa)
        double nx = -dy / dist, ny = dx / dist;
        int halfSamples = Math.Max(0, (int)Math.Round(caliperWidth / 2.0));

        var raw = new double[n];
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / (n - 1);
            double cx = start.X + t * dx, cy = start.Y + t * dy;

            double sum = 0; int count = 0;
            for (int k = -halfSamples; k <= halfSamples; k++)
            {
                double sx = cx + k * nx, sy = cy + k * ny;
                sum += SampleBilinear(gray, sx, sy);
                count++;
            }
            raw[i] = sum / Math.Max(1, count); // Trung bình mức xám theo bề ngang caliper tại vị trí i
        }

        // Làm mịn dọc theo chiều dài bằng box filter đơn giản, độ rộng theo FilterWidth
        int fw = Math.Max(0, (int)Math.Round(filterWidth));
        if (fw == 0) return raw;

        var smoothed = new double[n];
        for (int i = 0; i < n; i++)
        {
            double sum = 0; int count = 0;
            for (int k = -fw; k <= fw; k++)
            {
                int idx = i + k;
                if (idx < 0 || idx >= n) continue;
                sum += raw[idx]; count++;
            }
            smoothed[i] = sum / count;
        }
        return smoothed;
    }

    /// <summary>
    /// Tìm toàn bộ ứng viên cạnh trên 1 profile: tính Gradient bằng sai phân trung tâm, giữ lại các đỉnh cực trị cục bộ
    /// (Non-max suppression 1D) vượt ngưỡng và đúng cực tính, rồi tinh chỉnh vị trí sub-pixel bằng nội suy Parabol.
    /// </summary>
    private static List<CaliperEdge> FindEdgeCandidates(double[] profile, P2 start, P2 end, double threshold, string polarity, int caliperIndex)
    {
        int n = profile.Length;
        var result = new List<CaliperEdge>();
        if (n < 5) return result;

        // Bước a: Tính mảng Gradient bằng sai phân trung tâm
        var grad = new double[n];
        for (int i = 1; i < n - 1; i++)
            grad[i] = (profile[i + 1] - profile[i - 1]) / 2.0;

        // Bước b: Tìm các đỉnh cực trị cục bộ (Non-max suppression 1D) thỏa cực tính + ngưỡng
        for (int i = 2; i < n - 2; i++)
        {
            double g = grad[i];
            bool passPolarity = polarity == "DarkToLight" ? g > 0 : g < 0; // DarkToLight: mức xám tăng dần -> gradient dương
            if (!passPolarity) continue;

            double mag = Math.Abs(g);
            if (mag < threshold) continue;

            bool isLocalPeak = mag >= Math.Abs(grad[i - 1]) && mag >= Math.Abs(grad[i + 1]);
            if (!isLocalPeak) continue;

            // Nội suy Parabol quanh đỉnh Gradient để đạt độ chính xác sub-pixel
            double gm1 = Math.Abs(grad[i - 1]), g0 = mag, gp1 = Math.Abs(grad[i + 1]);
            double denom = gm1 - 2 * g0 + gp1;
            double subIdx = i;
            if (Math.Abs(denom) > 1e-6)
                subIdx = i + 0.5 * (gm1 - gp1) / denom;
            subIdx = Math.Clamp(subIdx, 0, n - 1);

            double tt = subIdx / (n - 1);
            var pos = new P2(start.X + (end.X - start.X) * tt, start.Y + (end.Y - start.Y) * tt);

            result.Add(new CaliperEdge
            {
                CaliperIndex = caliperIndex,
                Position = pos,
                GradientStrength = mag,
                Polarity = polarity,
                RelativePosition = tt
                // Score sẽ được tính ở bước ScoreAndFilter
            });
        }
        return result;
    }

    /// <summary>
    /// Tính điểm số Score cho từng ứng viên cạnh dựa trên EnableStrengthScoring/EnablePositionScoring,
    /// lọc bỏ các cạnh có Score dưới MinStrengthScore (nếu EnableStrengthScoring bật), rồi sắp xếp giảm dần theo Score.
    /// </summary>
    private List<CaliperEdge> ScoreAndFilter(List<CaliperEdge> candidates)
    {
        foreach (var e in candidates)
        {
            double strengthScore = _enableStrengthScoring.Value ? Math.Clamp(e.GradientStrength / 255.0, 0, 1) : 1.0;
            double posScore = 1.0;
            if (_enablePositionScoring.Value)
            {
                double dPos = Math.Abs(e.RelativePosition - _expectedPosition.Value);
                posScore = Math.Max(0, 1.0 - dPos / Math.Max(1e-6, _positionTolerance.Value));
            }
            e.Score = strengthScore * posScore;
        }

        var filtered = _enableStrengthScoring.Value
            ? candidates.Where(e => e.Score >= _minStrengthScore.Value).ToList()
            : candidates;

        return filtered.OrderByDescending(e => e.Score).ToList();
    }
    #endregion

    #region 5. Tính toán Measurements
    private static List<CaliperMeasurement> BuildMeasurements(string type, List<CaliperEdge> edges, List<CaliperEdgePair> pairs, double scale)
    {
        var list = new List<CaliperMeasurement>();
        switch (type)
        {
            case "Width":
                // Chỉ có ý nghĩa khi Mode = EdgePair: độ rộng giữa StartEdge và EndEdge trên từng caliper
                foreach (var p in pairs)
                    list.Add(new CaliperMeasurement { CaliperIndex = p.CaliperIndex, Type = "Width", ValuePx = p.WidthPx, ValueWorld = p.WidthPx * scale });
                break;

            case "Distance":
                // Khoảng cách giữa cạnh của 2 caliper liên tiếp (theo thứ tự CaliperIndex)
                var ordered = edges.OrderBy(e => e.CaliperIndex).ToList();
                for (int i = 1; i < ordered.Count; i++)
                {
                    double d = Distance(ordered[i - 1].Position, ordered[i].Position);
                    list.Add(new CaliperMeasurement { CaliperIndex = ordered[i].CaliperIndex, Type = "Distance", ValuePx = d, ValueWorld = d * scale });
                }
                break;

            case "Position":
            default:
                // Vị trí tương đối (0=đầu, 1=cuối) của từng cạnh dọc theo caliper của chính nó
                foreach (var e in edges)
                    list.Add(new CaliperMeasurement { CaliperIndex = e.CaliperIndex, Type = "Position", ValuePx = e.RelativePosition, ValueWorld = e.RelativePosition * scale });
                break;
        }
        return list;
    }

    private static double Distance(P2 a, P2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    #endregion

    #region 6. Nội suy song tuyến tính (Bilinear Sampling)
    private static double SampleBilinear(Mat gray, double x, double y)
    {
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        if (x0 < 0 || y0 < 0 || x0 >= gray.Width - 1 || y0 >= gray.Height - 1)
        {
            int cx = Math.Clamp((int)Math.Round(x), 0, gray.Width - 1);
            int cy = Math.Clamp((int)Math.Round(y), 0, gray.Height - 1);
            return gray.At<byte>(cy, cx);
        }
        double fx = x - x0, fy = y - y0;
        double i00 = gray.At<byte>(y0, x0), i10 = gray.At<byte>(y0, x0 + 1);
        double i01 = gray.At<byte>(y0 + 1, x0), i11 = gray.At<byte>(y0 + 1, x0 + 1);
        return i00 * (1 - fx) * (1 - fy) + i10 * fx * (1 - fy) + i01 * (1 - fx) * fy + i11 * fx * fy;
    }
    #endregion

    #region 7. Vẽ Overlay
    private void DrawOverlay(Mat overlay, RotatedRectRegion region, List<(P2 Start, P2 End)> calipers,
        List<CaliperEdge> edges, List<CaliperEdgePair> pairs)
    {
        if (_drawProjectionRegion.Value)
        {
            // Vẽ 4 cạnh của ROI xoay (màu cyan)
            var pts = GetRotatedRectCorners(region);
            for (int i = 0; i < 4; i++)
            {
                var a = pts[i]; var b = pts[(i + 1) % 4];
                Cv2.Line(overlay, (int)a.X, (int)a.Y, (int)b.X, (int)b.Y, new Scalar(255, 255, 0), 1);
            }
        }

        if (_drawCalipers.Value)
        {
            foreach (var (s, e) in calipers)
                Cv2.Line(overlay, (int)s.X, (int)s.Y, (int)e.X, (int)e.Y, new Scalar(255, 190, 0), 1);
        }

        if (_drawEdgePoints.Value)
        {
            foreach (var e in edges)
            {
                var color = e.Polarity == "DarkToLight" ? new Scalar(0, 255, 0) : new Scalar(0, 128, 255);
                Cv2.Circle(overlay, (int)e.Position.X, (int)e.Position.Y, 3, color, -1);
            }
        }

        if (_drawMeasurements.Value)
        {
            foreach (var p in pairs)
            {
                Cv2.Line(overlay, (int)p.StartEdge.Position.X, (int)p.StartEdge.Position.Y,
                                   (int)p.EndEdge.Position.X, (int)p.EndEdge.Position.Y, new Scalar(0, 0, 255), 1);
                var mid = new P2((p.StartEdge.Position.X + p.EndEdge.Position.X) / 2, (p.StartEdge.Position.Y + p.EndEdge.Position.Y) / 2);
                Cv2.PutText(overlay, $"{p.WidthPx:F1}px", new Point((int)mid.X + 4, (int)mid.Y - 4),
                    HersheyFonts.HersheySimplex, 0.4, new Scalar(0, 0, 255), 1);
            }
        }
    }

    /// <summary>Tính tọa độ 4 góc của RotatedRectRegion để vẽ khung ROI lên overlay.</summary>
    private static P2[] GetRotatedRectCorners(RotatedRectRegion r)
    {
        double angleRad = r.AngleDeg * Math.PI / 180.0;
        double ux = Math.Cos(angleRad), uy = Math.Sin(angleRad);
        double vx = -Math.Sin(angleRad), vy = Math.Cos(angleRad);
        double hw = r.Width / 2.0, hh = r.Height / 2.0;

        return new[]
        {
            new P2(r.Center.X - hw * ux - hh * vx, r.Center.Y - hw * uy - hh * vy),
            new P2(r.Center.X + hw * ux - hh * vx, r.Center.Y + hw * uy - hh * vy),
            new P2(r.Center.X + hw * ux + hh * vx, r.Center.Y + hw * uy + hh * vy),
            new P2(r.Center.X - hw * ux + hh * vx, r.Center.Y - hw * uy + hh * vy),
        };
    }
    #endregion
}