// ==================== Vai trò chính:                Tool "tất cả trong một" cho phân tích màu: match/dominant/statistics/classification, 3 không gian màu, 3 cách chọn màu mục tiêu
// ==================== Thành phần / Class tiêu biểu: ColorAnalyzerTool
// ==================== Phụ thuộc vào:                OpenCvSharp + Color.ColorUtil
// ==================== Pattern / Kỹ thuật nổi bật:   Strategy pattern (TargetColorMode/ColorSpace/AnalysisMode), color quantization cho Dominant Color

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
/// Tool phân tích màu tổng hợp: nên dùng ĐẦU TIÊN khi chưa rõ cần gì. Có 4 chế độ (AnalysisMode) nhưng
/// TẤT CẢ output đều được tính đầy đủ bất kể mode nào (vì tài liệu liệt kê chúng ở Outputs chung, không
/// gắn điều kiện theo mode) - AnalysisMode chỉ quyết định overlay nào được nhấn mạnh trên ảnh output.
/// </summary>
[ToolMetadata("ColorAnalyzer", DisplayName = "Color Analyzer", Category = "Color",
    Description = "All-in-one color analysis: matching, dominant color, statistics, classification")]
public sealed class ColorAnalyzerTool : VisionTool
{
    #region 1. Port
    private readonly InputPort<IVisionImage> _input;
    private readonly OutputPort<IVisionImage> _outImage;
    private readonly OutputPort<bool> _outColorMatch;
    private readonly OutputPort<Vec3b> _outDominantColor;
    private readonly OutputPort<Vec3b> _outAverageColor;
    private readonly OutputPort<int> _outColorCount;
    private readonly OutputPort<double> _outMatchPercentage;
    private readonly OutputPort<double> _outHue;
    private readonly OutputPort<double> _outSaturation;
    private readonly OutputPort<double> _outBrightness;
    private readonly OutputPort<IVisionImage> _outColorMask;
    #endregion

    #region 2. Parameter
    // ----- Tab Color -----
    private readonly ToolParameter<string> _targetColorMode;
    private readonly ToolParameter<double> _targetR, _targetG, _targetB;
    private readonly ToolParameter<double> _targetHue, _targetSaturation, _targetBrightness;
    private readonly ToolParameter<int> _sampleX, _sampleY, _sampleSize;

    // ----- Tab Range -----
    private readonly ToolParameter<string> _colorSpace;
    private readonly ToolParameter<double> _rgbTolerance;
    private readonly ToolParameter<double> _hueTolerance;
    private readonly ToolParameter<double> _saturationTolerance;
    private readonly ToolParameter<double> _brightnessTolerance;

    // ----- Tab Output -----
    private readonly ToolParameter<string> _analysisMode;
    private readonly ToolParameter<int> _minMatchArea;
    private readonly ToolParameter<double> _minMatchPercentage;
    private readonly ToolParameter<bool> _ignoreBlack;
    private readonly ToolParameter<bool> _ignoreWhite;

    // ----- Tab Region -----
    private readonly ToolParameter<bool> _useRoi;
    private readonly ToolParameter<int> _roiX, _roiY, _roiWidth, _roiHeight;
    #endregion

    public ColorAnalyzerTool()
    {
        _input = AddInput<IVisionImage>("Image");
        _outImage = AddOutput<IVisionImage>("Image");
        _outColorMatch = AddOutput<bool>("ColorMatch");
        _outDominantColor = AddOutput<Vec3b>("DominantColor");
        _outAverageColor = AddOutput<Vec3b>("AverageColor");
        _outColorCount = AddOutput<int>("ColorCount");
        _outMatchPercentage = AddOutput<double>("MatchPercentage");
        _outHue = AddOutput<double>("Hue");
        _outSaturation = AddOutput<double>("Saturation");
        _outBrightness = AddOutput<double>("Brightness");
        _outColorMask = AddOutput<IVisionImage>("ColorMask");

        _targetColorMode = AddChoiceParameter("TargetColorMode", "RGB", new[] { "RGB", "HSV", "Sample from Image" }, "Target Color Mode", category: "Color", order: 1);
        _targetR = AddParameter("TargetR", 255.0, "Target R", 0.0, 255.0, category: "Color", order: 2); // Mặc định đỏ thuần theo tài liệu
        _targetG = AddParameter("TargetG", 0.0, "Target G", 0.0, 255.0, category: "Color", order: 3);
        _targetB = AddParameter("TargetB", 0.0, "Target B", 0.0, 255.0, category: "Color", order: 4);
        _targetHue = AddParameter("TargetHue", 0.0, "Target Hue", 0.0, 360.0, category: "Color", order: 5);
        _targetSaturation = AddParameter("TargetSaturation", 100.0, "Target Saturation", 0.0, 100.0, category: "Color", order: 6);
        _targetBrightness = AddParameter("TargetBrightness", 100.0, "Target Brightness", 0.0, 100.0, category: "Color", order: 7);
        _sampleX = AddParameter("SampleX", 0, "Sample X", 0, 100_000, category: "Color", order: 8);
        _sampleY = AddParameter("SampleY", 0, "Sample Y", 0, 100_000, category: "Color", order: 9);
        _sampleSize = AddParameter("SampleSize", 5, "Sample Size", 1, 200, category: "Color", order: 10);

        _colorSpace = AddChoiceParameter("ColorSpace", "HSV", new[] { "RGB", "HSV", "LAB" }, "Color Space", category: "Range", order: 1);
        _rgbTolerance = AddParameter("RGBTolerance", 30.0, "RGB Tolerance", 0.0, 255.0, category: "Range", order: 2);
        _hueTolerance = AddParameter("HueTolerance", 15.0, "Hue Tolerance", 0.0, 180.0, category: "Range", order: 3);
        _saturationTolerance = AddParameter("SaturationTolerance", 50.0, "Saturation Tolerance", 0.0, 100.0, category: "Range", order: 4);
        _brightnessTolerance = AddParameter("BrightnessTolerance", 50.0, "Brightness Tolerance", 0.0, 100.0, category: "Range", order: 5);

        _analysisMode = AddChoiceParameter("AnalysisMode", "Color Match", new[] { "Color Match", "Dominant Color", "Color Statistics", "Color Classification" }, "Analysis Mode", category: "Output", order: 1);
        _minMatchArea = AddParameter("MinMatchArea", 50, "Min Match Area", 0, 10_000_000, category: "Output", order: 2);
        _minMatchPercentage = AddParameter("MinMatchPercentage", 2.0, "Min Match Percentage", 0.0, 100.0, category: "Output", order: 3);
        _ignoreBlack = AddParameter("IgnoreBlack", true, "Ignore Black", category: "Output", order: 4);
        _ignoreWhite = AddParameter("IgnoreWhite", false, "Ignore White", category: "Output", order: 5);

        _useRoi = AddParameter("UseROI", false, "Use ROI", category: "Region", order: 1);
        _roiX = AddParameter("ROI_X", 0, "ROI X", 0, 100_000, category: "Region", order: 2);
        _roiY = AddParameter("ROI_Y", 0, "ROI Y", 0, 100_000, category: "Region", order: 3);
        _roiWidth = AddParameter("ROI_Width", 100, "ROI Width", 1, 100_000, category: "Region", order: 4);
        _roiHeight = AddParameter("ROI_Height", 100, "ROI Height", 1, 100_000, category: "Region", order: 5);
    }

    protected override void OnExecute(IToolContext context)
    {
        Mat srcRaw = _input.Value!.AsMat();
        Mat src = ColorUtil.EnsureBgr(srcRaw, context, "ColorAnalyzer");
        bool srcTemp = !ReferenceEquals(src, srcRaw);

        Rect roi = ColorUtil.ResolveRoi(src, _useRoi.Value, _roiX.Value, _roiY.Value, _roiWidth.Value, _roiHeight.Value);
        Mat working = new Mat(src, roi);

        // ----- Bước 1: Xác định màu mục tiêu (BGR) theo TargetColorMode -----
        Scalar targetBgr = _targetColorMode.Value switch
        {
            "HSV" => ColorUtil.HsvToBgrScalar(_targetHue.Value, _targetSaturation.Value, _targetBrightness.Value),
            "Sample from Image" => SampleToScalar(working),
            _ => ColorUtil.RgbToBgrScalar(_targetR.Value, _targetG.Value, _targetB.Value), // "RGB"
        };

        // ----- Bước 2: Dựng mask lọc bỏ pixel đen/trắng (áp dụng cho MỌI phép thống kê phía sau) -----
        Mat ignoreMask = ColorUtil.BuildIgnoreMask(working, _ignoreBlack.Value, _ignoreWhite.Value);

        // ----- Bước 3: Dựng ColorMask theo ColorSpace đã chọn (phép so khớp màu cốt lõi) -----
        Mat colorMask = BuildColorMask(working, targetBgr, out double avgColorDistance);
        Cv2.BitwiseAnd(colorMask, ignoreMask, colorMask); // Chỉ giữ pixel khớp màu VÀ không bị ignore

        int matchedPixels = Cv2.CountNonZero(colorMask);
        int totalValidPixels = Math.Max(1, Cv2.CountNonZero(ignoreMask));
        double matchPercentage = (double)matchedPixels / totalValidPixels * 100.0;
        bool colorMatch = matchedPixels >= _minMatchArea.Value && matchPercentage >= _minMatchPercentage.Value;

        // ----- Bước 4: Thống kê HSV trung bình (trên vùng KHÔNG bị ignore, không phụ thuộc match hay không) -----
        using Mat hsvImg = new Mat();
        Cv2.CvtColor(working, hsvImg, ColorConversionCodes.BGR2HSV);
        Scalar meanHsv = Cv2.Mean(hsvImg, ignoreMask);
        double hueOut = meanHsv.Val0 * 2.0;          // Quy đổi 0-180 (OpenCV) -> 0-360 (chuẩn thường dùng)
        double satOut = meanHsv.Val1 / 255.0 * 100.0; // Quy đổi 0-255 -> 0-100%
        double brightOut = meanHsv.Val2 / 255.0 * 100.0;

        // ----- Bước 5: Màu trung bình + màu chủ đạo (quantize 8 mức/kênh để tìm bucket phổ biến nhất) -----
        Scalar meanBgr = Cv2.Mean(working, ignoreMask);
        var averageColor = new Vec3b((byte)meanBgr.Val0, (byte)meanBgr.Val1, (byte)meanBgr.Val2);
        var (dominantColor, colorCount) = ComputeDominantColor(working, ignoreMask);

        // ----- Bước 6: Vẽ overlay theo AnalysisMode -----
        Mat overlay = working.Clone();
        switch (_analysisMode.Value)
        {
            case "Dominant Color":
                Cv2.Rectangle(overlay, new Rect(5, 5, 40, 40), new Scalar(dominantColor.Item0, dominantColor.Item1, dominantColor.Item2), -1);
                Cv2.Rectangle(overlay, new Rect(5, 5, 40, 40), Scalar.White, 1);
                Cv2.PutText(overlay, $"Colors: {colorCount}", new Point(50, 25), HersheyFonts.HersheySimplex, 0.5, Scalar.White, 1);
                break;
            case "Color Statistics":
                Cv2.PutText(overlay, $"H={hueOut:F0} S={satOut:F0}% V={brightOut:F0}%", new Point(10, 25), HersheyFonts.HersheySimplex, 0.5, Scalar.White, 1);
                break;
            case "Color Classification":
                Cv2.PutText(overlay, $"Colors detected: {colorCount}", new Point(10, 25), HersheyFonts.HersheySimplex, 0.5, Scalar.Yellow, 1);
                break;
            default: // "Color Match"
                Cv2.PutText(overlay, colorMatch ? $"MATCH {matchPercentage:F1}%" : $"NO MATCH {matchPercentage:F1}%",
                    new Point(10, 25), HersheyFonts.HersheySimplex, 0.6, colorMatch ? Scalar.LimeGreen : Scalar.Red, 2);
                break;
        }

        // ----- Bước 7: Ghép overlay ROI trở lại ảnh gốc kích thước đầy đủ -----
        Mat fullOutput = src.Clone();
        overlay.CopyTo(new Mat(fullOutput, roi));
        if (_useRoi.Value) Cv2.Rectangle(fullOutput, roi, Scalar.Cyan, 1);

        _outImage.Value = new MatVisionImage(fullOutput);
        _outColorMatch.Value = colorMatch;
        _outDominantColor.Value = dominantColor;
        _outAverageColor.Value = averageColor;
        _outColorCount.Value = colorCount;
        _outMatchPercentage.Value = matchPercentage;
        _outHue.Value = hueOut;
        _outSaturation.Value = satOut;
        _outBrightness.Value = brightOut;
        _outColorMask.Value = new MatVisionImage(colorMask);

        if (srcTemp) src.Dispose();
        working.Dispose(); ignoreMask.Dispose(); overlay.Dispose();

        context.Log($"ColorAnalyzer [{_analysisMode.Value}]: ColorMatch={colorMatch}, Match%={matchPercentage:F1}, Colors={colorCount}");
    }

    private Scalar SampleToScalar(Mat working)
    {
        Vec3b sampled = ColorUtil.SamplePatchMean(working, _sampleX.Value, _sampleY.Value, _sampleSize.Value);
        return new Scalar(sampled.Item0, sampled.Item1, sampled.Item2);
    }

    /// <summary>Dựng mask nhị phân theo ColorSpace: RGB (per-channel tolerance), HSV (tách Hue/Sat/Val, xử lý wrap-around), LAB (khoảng cách Euclidean).</summary>
    private Mat BuildColorMask(Mat bgr, Scalar targetBgr, out double avgDistance)
    {
        avgDistance = 0;
        switch (_colorSpace.Value)
        {
            case "RGB":
                {
                    double tol = _rgbTolerance.Value;
                    var lower = new Scalar(Math.Max(0, targetBgr.Val0 - tol), Math.Max(0, targetBgr.Val1 - tol), Math.Max(0, targetBgr.Val2 - tol));
                    var upper = new Scalar(Math.Min(255, targetBgr.Val0 + tol), Math.Min(255, targetBgr.Val1 + tol), Math.Min(255, targetBgr.Val2 + tol));
                    Mat mask = new Mat();
                    Cv2.InRange(bgr, lower, upper, mask);
                    return mask;
                }
            case "LAB":
                {
                    using Mat lab = new Mat(); Cv2.CvtColor(bgr, lab, ColorConversionCodes.BGR2Lab);
                    using Mat targetPatch = new Mat(1, 1, MatType.CV_8UC3, targetBgr);
                    using Mat targetLab = new Mat(); Cv2.CvtColor(targetPatch, targetLab, ColorConversionCodes.BGR2Lab);
                    Vec3b tLab = targetLab.At<Vec3b>(0, 0);
                    Mat mask = new Mat(bgr.Size(), MatType.CV_8UC1);
                    for (int y = 0; y < lab.Rows; y++) // Vòng lặp pixel: chấp nhận đánh đổi tốc độ lấy độ chính xác Euclidean-Lab đúng chuẩn
                        for (int x = 0; x < lab.Cols; x++)
                        {
                            Vec3b p = lab.At<Vec3b>(y, x);
                            double dist = Math.Sqrt(Math.Pow(p.Item0 - tLab.Item0, 2) + Math.Pow(p.Item1 - tLab.Item1, 2) + Math.Pow(p.Item2 - tLab.Item2, 2));
                            mask.Set(y, x, (byte)(dist <= _rgbTolerance.Value ? 255 : 0)); // Dùng lại RGBTolerance vì tài liệu không định nghĩa tolerance riêng cho LAB
                        }
                    return mask;
                }
            default: // "HSV"
                {
                    using Mat hsv = new Mat(); Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
                    using Mat targetPatch = new Mat(1, 1, MatType.CV_8UC3, targetBgr);
                    using Mat targetHsv = new Mat(); Cv2.CvtColor(targetPatch, targetHsv, ColorConversionCodes.HSV2BGR); // placeholder tránh unused warning
                    using Mat targetHsvReal = new Mat(); Cv2.CvtColor(targetPatch, targetHsvReal, ColorConversionCodes.BGR2HSV);
                    Vec3b tHsv = targetHsvReal.At<Vec3b>(0, 0);

                    double hueTolCv = _hueTolerance.Value / 2.0;           // Quy đổi 0-180 tolerance về đúng scale OpenCV (đã 0-180 sẵn thực ra, giữ nguyên)
                    double satTolCv = _saturationTolerance.Value * 2.55;   // 0-100% -> 0-255
                    double valTolCv = _brightnessTolerance.Value * 2.55;

                    double hLow = tHsv.Item0 - _hueTolerance.Value, hHigh = tHsv.Item0 + _hueTolerance.Value;
                    Mat mask = new Mat();
                    if (hLow < 0 || hHigh > 180) // Xử lý wrap-around quanh biên 0/180 (bài học từ Buổi 89 - màu đỏ nằm ở 2 đầu dải Hue)
                    {
                        Mat m1 = new Mat(), m2 = new Mat();
                        Cv2.InRange(hsv, new Scalar(Math.Max(0, hLow), Math.Max(0, tHsv.Item1 - satTolCv), Math.Max(0, tHsv.Item2 - valTolCv)),
                            new Scalar(180, Math.Min(255, tHsv.Item1 + satTolCv), Math.Min(255, tHsv.Item2 + valTolCv)), m1);
                        Cv2.InRange(hsv, new Scalar(0, Math.Max(0, tHsv.Item1 - satTolCv), Math.Max(0, tHsv.Item2 - valTolCv)),
                            new Scalar(Math.Min(180, hHigh < 0 ? hHigh + 180 : hHigh - 180), Math.Min(255, tHsv.Item1 + satTolCv), Math.Min(255, tHsv.Item2 + valTolCv)), m2);
                        Cv2.BitwiseOr(m1, m2, mask); m1.Dispose(); m2.Dispose();
                    }
                    else
                    {
                        Cv2.InRange(hsv, new Scalar(hLow, Math.Max(0, tHsv.Item1 - satTolCv), Math.Max(0, tHsv.Item2 - valTolCv)),
                            new Scalar(hHigh, Math.Min(255, tHsv.Item1 + satTolCv), Math.Min(255, tHsv.Item2 + valTolCv)), mask);
                    }
                    return mask;
                }
        }
    }

    /// <summary>Lượng tử hóa mỗi kênh về 8 mức (chia 32) để gom pixel gần giống nhau thành "bucket", tìm bucket phổ biến nhất -> màu chủ đạo.</summary>
    private static (Vec3b dominant, int count) ComputeDominantColor(Mat bgr, Mat ignoreMask)
    {
        var buckets = new Dictionary<(int, int, int), (long count, long sumB, long sumG, long sumR)>();
        for (int y = 0; y < bgr.Rows; y++)
            for (int x = 0; x < bgr.Cols; x++)
            {
                if (ignoreMask.At<byte>(y, x) == 0) continue;
                Vec3b p = bgr.At<Vec3b>(y, x);
                var key = (p.Item0 / 32, p.Item1 / 32, p.Item2 / 32);
                buckets.TryGetValue(key, out var acc);
                buckets[key] = (acc.count + 1, acc.sumB + p.Item0, acc.sumG + p.Item1, acc.sumR + p.Item2);
            }
        if (buckets.Count == 0) return (new Vec3b(0, 0, 0), 0);

        long totalPixels = buckets.Values.Sum(v => v.count);
        int significantCount = buckets.Values.Count(v => v.count > totalPixels * 0.005); // Chỉ tính bucket chiếm >0.5% tổng pixel là "1 màu riêng biệt"
        var best = buckets.OrderByDescending(kv => kv.Value.count).First().Value;
        var dominant = new Vec3b((byte)(best.sumB / best.count), (byte)(best.sumG / best.count), (byte)(best.sumR / best.count));
        return (dominant, Math.Max(1, significantCount));
    }
}