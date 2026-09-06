// ==================== Vai trò chính:                Đo màu trung bình trong không gian CIE Lab (chuẩn ISO cho đo lệch màu công nghiệp)
// ==================== Thành phần / Class tiêu biểu: LabColorDetectorTool
// ==================== Phụ thuộc vào:                OpenCvSharp + Core.Models (RotatedRectRegion)
// ==================== Pattern / Kỹ thuật nổi bật:   ROI fixture-aware (dùng chung ParameterInteraction.RotatedRectRegion như FindLine/FindCircle), CV_32F Lab thật (không scale)

using System;
using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Models;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;
using P2 = VisionFlow.Core.Models.Point2d;

namespace VisionFlow.Tools.Color;

/// <summary>
/// Tool ĐO (không phán quyết) màu trung bình trong không gian Lab - không gian gần với cảm nhận con
/// người nhất, chuẩn ISO cho đo lệch màu (ΔE). Nối L/A/B output sang DeltaECalculator để ra PASS/FAIL.
/// ROI fixture-aware: dùng chung kiểu RotatedRectRegion + ParameterInteraction như FindLine/FindCircle,
/// nên khi nối downstream từ PMAlignTool, vùng đo sẽ tự dịch/xoay theo vật (đúng cơ chế fixture có sẵn).
/// </summary>
[ToolMetadata("LabColorDetector", DisplayName = "Lab Color Detector", Category = "Color",
    Description = "Measure average CIE Lab color within a fixture-aware region")]
public sealed class LabColorDetectorTool : VisionTool
{
    private readonly InputPort<IVisionImage> _input;
    private readonly OutputPort<IVisionImage> _outImage;
    private readonly OutputPort<double> _outL;
    private readonly OutputPort<double> _outA;
    private readonly OutputPort<double> _outB;
    private readonly OutputPort<string> _outLabString;
    private readonly OutputPort<int> _outPixelCount;
    private readonly OutputPort<IVisionImage> _outLabImage;

    // ----- Tab Region (fixture-aware) -----
    private readonly ToolParameter<bool> _useRoi;
    private readonly ToolParameter<RotatedRectRegion> _region;

    // ----- Tab Sampling -----
    private readonly ToolParameter<bool> _ignoreBlack;
    private readonly ToolParameter<bool> _ignoreWhite;

    // ----- Tab Display -----
    private readonly ToolParameter<bool> _drawRegion;

    public LabColorDetectorTool()
    {
        _input = AddInput<IVisionImage>("Image");
        _outImage = AddOutput<IVisionImage>("Image");
        _outL = AddOutput<double>("L");
        _outA = AddOutput<double>("A");
        _outB = AddOutput<double>("B");
        _outLabString = AddOutput<string>("LabString");
        _outPixelCount = AddOutput<int>("PixelCount");
        _outLabImage = AddOutput<IVisionImage>("LabImage");

        _useRoi = AddParameter("UseROI", false, "Use ROI", category: "Region", order: 1);
        // Dùng chung kiểu RotatedRectRegion + ParameterInteraction như FindLineTool -> UI Editor tự hỗ trợ kéo-thả,
        // và tương thích trực tiếp với cơ chế fixture-aware nếu PMAlignTool cũng dùng transform tương tự.
        _region = AddParameter("Region", new RotatedRectRegion(new P2(320, 240), 100, 100, 0), "Region", category: "Region", order: 2, interaction: ParameterInteraction.RotatedRectRegion);

        _ignoreBlack = AddParameter("IgnoreBlack", false, "Ignore Black", category: "Sampling", order: 1);
        _ignoreWhite = AddParameter("IgnoreWhite", false, "Ignore White", category: "Sampling", order: 2);

        _drawRegion = AddParameter("DrawRegion", true, "Draw Region", category: "Display", order: 1);
    }

    protected override void OnExecute(IToolContext context)
    {
        Mat srcRaw = _input.Value!.AsMat();
        Mat src = ColorUtil.EnsureBgr(srcRaw, context, "LabColorDetector");

        // ----- Bước 1: Chuyển toàn ảnh sang Lab dạng float32 THẬT (không scale 0-255 như OpenCV mặc định hiển thị) -----
        Mat labFloat = new Mat();
        Mat bgrFloat = new Mat();
        src.ConvertTo(bgrFloat, MatType.CV_32FC3, 1.0 / 255.0); // Chuẩn hóa BGR về [0,1] trước - bắt buộc để Lab ra đúng thang L*(0-100), a*/b*(-128..127)
        Cv2.CvtColor(bgrFloat, labFloat, ColorConversionCodes.BGR2Lab);
        bgrFloat.Dispose();

        // ----- Bước 2: Xác định vùng đo (ROI xoay) -----
        var r = _region.Value;
        Mat mask = new Mat(src.Size(), MatType.CV_8UC1, Scalar.All(255)); // Mặc định: đo cả ảnh
        if (_useRoi.Value)
        {
            mask.SetTo(Scalar.All(0));
            var rect = new RotatedRect(new Point2f((float)r.Center.X, (float)r.Center.Y), new Size2f((float)r.Width, (float)r.Height), (float)r.AngleDeg);
            Point2f[] pts = rect.Points();
            var ptsInt = Array.ConvertAll(pts, p => (Point)p);
            Cv2.FillConvexPoly(mask, ptsInt, Scalar.All(255)); // Vẽ đúng hình chữ nhật XOAY, không phải axis-aligned
        }

        // ----- Bước 3: Lọc thêm pixel đen/trắng nếu bật -----
        if (_ignoreBlack.Value || _ignoreWhite.Value)
        {
            using Mat ignoreMask = ColorUtil.BuildIgnoreMask(src, _ignoreBlack.Value, _ignoreWhite.Value);
            Cv2.BitwiseAnd(mask, ignoreMask, mask);
        }

        int pixelCount = Cv2.CountNonZero(mask);

        // ----- Bước 4: Tính trung bình L*/a*/b* CHỈ trên vùng mask (Cv2.Mean hỗ trợ mask trực tiếp trên ảnh float) -----
        Scalar meanLab = Cv2.Mean(labFloat, mask);
        double lVal = meanLab.Val0, aVal = meanLab.Val1, bVal = meanLab.Val2;

        // ----- Bước 5: Vẽ overlay ROI -----
        Mat overlay = src.Clone();
        if (_drawRegion.Value && _useRoi.Value)
        {
            var rect = new RotatedRect(new Point2f((float)r.Center.X, (float)r.Center.Y), new Size2f((float)r.Width, (float)r.Height), (float)r.AngleDeg);
            Point2f[] pts = rect.Points();
            for (int i = 0; i < 4; i++) Cv2.Line(overlay, (Point)pts[i], (Point)pts[(i + 1) % 4], Scalar.Cyan, 2);
        }
        Cv2.PutText(overlay, $"L={lVal:F1} a={aVal:F1} b={bVal:F1}", new Point(10, 25), HersheyFonts.HersheySimplex, 0.5, Scalar.White, 1);

        _outImage.Value = new MatVisionImage(overlay);
        _outL.Value = lVal;
        _outA.Value = aVal;
        _outB.Value = bVal;
        _outLabString.Value = $"L={lVal:F1}, a={aVal:F1}, b={bVal:F1}";
        _outPixelCount.Value = pixelCount;
        _outLabImage.Value = new MatVisionImage(labFloat);

        mask.Dispose();
        if (!ReferenceEquals(src, srcRaw)) src.Dispose();

        context.Log($"LabColorDetector: L={lVal:F1}, a={aVal:F1}, b={bVal:F1}, pixels={pixelCount}");
    }
}