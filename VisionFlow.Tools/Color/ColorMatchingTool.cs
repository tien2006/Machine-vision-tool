// ==================== Vai trò chính:                Tìm và khoanh vùng (bounding box) từng khu vực khớp màu mẫu riêng biệt trong ảnh
// ==================== Thành phần / Class tiêu biểu: ColorMatchingTool
// ==================== Phụ thuộc vào:                OpenCvSharp + Core.Models (RectRegion) + Color.ColorUtil
// ==================== Pattern / Kỹ thuật nổi bật:   InRange + FindContours + lọc theo Min/MaxMatchArea, tính ColorDistance làm điểm chất lượng

using System;
using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Models;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;

namespace VisionFlow.Tools.Color;

/// <summary>
/// Khác ColorAnalyzer (phán quyết match/không toàn ảnh), tool này ĐỊNH VỊ từng vùng khớp riêng biệt -
/// tiện đếm vật theo màu, vẽ overlay từng vật, hoặc đưa từng vùng vào tool tiếp theo.
/// </summary>
[ToolMetadata("ColorMatching", DisplayName = "Color Matching", Category = "Color",
    Description = "Locate individual regions matching a template color")]
public sealed class ColorMatchingTool : VisionTool
{
    private readonly InputPort<IVisionImage> _input;
    private readonly InputPort<Vec3b> _templateColor; // "Màu chuẩn" - nhận trực tiếp Vec3b, có thể lấy từ ColorAnalyzer.DominantColor/AverageColor

    private readonly OutputPort<IVisionImage> _outImage;
    private readonly OutputPort<double> _outMatchScore;
    private readonly OutputPort<bool> _outMatchFound;
    private readonly OutputPort<RectRegion[]> _outMatchedRegions;
    private readonly OutputPort<double> _outColorDistance;

    // ----- Tab Display -----
    private readonly ToolParameter<bool> _drawMatches;
    private readonly ToolParameter<bool> _showHeatmap;

    // ----- Tab Matching -----
    private readonly ToolParameter<string> _colorSpace;
    private readonly ToolParameter<double> _tolerance;
    private readonly ToolParameter<double> _minMatchArea;
    private readonly ToolParameter<double> _maxMatchArea;
    private readonly ToolParameter<string> _matchMethod; // Chỉ "Euclidean" được implement, đúng ghi chú tài liệu

    public ColorMatchingTool()
    {
        _input = AddInput<IVisionImage>("Image");
        _templateColor = AddInput<Vec3b>("TemplateColor");

        _outImage = AddOutput<IVisionImage>("Image");
        _outMatchScore = AddOutput<double>("MatchScore");
        _outMatchFound = AddOutput<bool>("MatchFound");
        _outMatchedRegions = AddOutput<RectRegion[]>("MatchedRegions");
        _outColorDistance = AddOutput<double>("ColorDistance");

        _drawMatches = AddParameter("DrawMatches", true, "Draw Matches", category: "Display", order: 1);
        _showHeatmap = AddParameter("ShowHeatmap", false, "Show Heatmap", category: "Display", order: 2);

        _colorSpace = AddChoiceParameter("ColorSpace", "HSV", new[] { "HSV", "RGB", "LAB" }, "Color Space", category: "Matching", order: 1);
        _tolerance = AddParameter("Tolerance", 20.0, "Tolerance", 0.0, 255.0, category: "Matching", order: 2); // Mặc định phù hợp HSV (15-25) theo tài liệu
        _minMatchArea = AddParameter("MinMatchArea", 200.0, "Min Match Area", 0.0, 10_000_000.0, category: "Matching", order: 3);
        _maxMatchArea = AddParameter("MaxMatchArea", 1_000_000.0, "Max Match Area", 0.0, 10_000_000.0, category: "Matching", order: 4);
        _matchMethod = AddChoiceParameter("MatchMethod", "Euclidean", new[] { "Euclidean" }, "Match Method", category: "Matching", order: 5);
    }

    protected override void OnExecute(IToolContext context)
    {
        Mat srcRaw = _input.Value!.AsMat();
        Mat src = ColorUtil.EnsureBgr(srcRaw, context, "ColorMatching");
        Vec3b template = _templateColor.Value;
        var targetBgr = new Scalar(template.Item0, template.Item1, template.Item2);

        // ----- Bước 1: Chuyển ảnh + màu mẫu sang không gian màu đã chọn -----
        Mat working; using Mat targetPatchBgr = new Mat(1, 1, MatType.CV_8UC3, targetBgr);
        Scalar targetInSpace;
        ColorConversionCodes code = _colorSpace.Value switch { "HSV" => ColorConversionCodes.BGR2HSV, "LAB" => ColorConversionCodes.BGR2Lab, _ => ColorConversionCodes.BGR2BGR555 /*placeholder, RGB dùng thẳng BGR*/ };

        if (_colorSpace.Value == "RGB")
        {
            working = src;
            targetInSpace = targetBgr;
        }
        else
        {
            working = new Mat(); Cv2.CvtColor(src, working, code);
            using Mat tConv = new Mat(); Cv2.CvtColor(targetPatchBgr, tConv, code);
            Vec3b tv = tConv.At<Vec3b>(0, 0);
            targetInSpace = new Scalar(tv.Item0, tv.Item1, tv.Item2);
        }

        // ----- Bước 2: Tính khoảng cách Euclidean từng pixel tới màu mẫu -> mask khớp theo Tolerance -----
        Mat distanceMap = new Mat(working.Size(), MatType.CV_32FC1);
        Mat mask = new Mat(working.Size(), MatType.CV_8UC1);
        double tol = _tolerance.Value;
        double sumMatchedDist = 0; long matchedCount = 0;

        for (int y = 0; y < working.Rows; y++)
            for (int x = 0; x < working.Cols; x++)
            {
                Vec3b p = working.At<Vec3b>(y, x);
                double d = Math.Sqrt(Math.Pow(p.Item0 - targetInSpace.Val0, 2) + Math.Pow(p.Item1 - targetInSpace.Val1, 2) + Math.Pow(p.Item2 - targetInSpace.Val2, 2));
                distanceMap.Set(y, x, (float)d);
                bool isMatch = d <= tol;
                mask.Set(y, x, (byte)(isMatch ? 255 : 0));
                if (isMatch) { sumMatchedDist += d; matchedCount++; }
            }

        // ----- Bước 3: Tìm từng vùng riêng biệt bằng FindContours, lọc theo Min/MaxMatchArea -----
        Cv2.FindContours(mask, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        var regions = contours
            .Select(c => (Rect: Cv2.BoundingRect(c), Area: Cv2.ContourArea(c)))
            .Where(r => r.Area >= _minMatchArea.Value && r.Area <= _maxMatchArea.Value)
            .Select(r => new RectRegion(r.Rect.X, r.Rect.Y, r.Rect.Width, r.Rect.Height))
            .ToArray();

        double colorDistance = matchedCount > 0 ? sumMatchedDist / matchedCount : double.MaxValue;
        double matchScore = matchedCount > 0 ? Math.Clamp(1.0 - colorDistance / Math.Max(1.0, tol * 2), 0.0, 1.0) : 0.0;
        bool matchFound = regions.Length > 0;

        // ----- Bước 4: Vẽ overlay -----
        Mat overlay = src.Clone();
        if (_showHeatmap.Value)
        {
            using Mat distNorm = new Mat(); Cv2.Normalize(distanceMap, distNorm, 0, 255, NormTypes.MinMax);
            using Mat dist8u = new Mat(); distNorm.ConvertTo(dist8u, MatType.CV_8UC1);
            using Mat heat = new Mat(); Cv2.ApplyColorMap(dist8u, heat, ColormapTypes.Jet);
            Cv2.AddWeighted(overlay, 0.5, heat, 0.5, 0, overlay);
        }
        if (_drawMatches.Value)
        {
            foreach (var r in regions)
                Cv2.Rectangle(overlay, new Rect((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height), Scalar.LimeGreen, 2);
        }

        if (!ReferenceEquals(working, src)) working.Dispose();
        distanceMap.Dispose(); mask.Dispose();
        if (!ReferenceEquals(src, srcRaw)) src.Dispose();

        _outImage.Value = new MatVisionImage(overlay);
        _outMatchScore.Value = matchScore;
        _outMatchFound.Value = matchFound;
        _outMatchedRegions.Value = regions;
        _outColorDistance.Value = colorDistance;

        context.Log($"ColorMatching: {regions.Length} regions, MatchFound={matchFound}, Score={matchScore:F2}, ColorDist={colorDistance:F1}");
    }
}