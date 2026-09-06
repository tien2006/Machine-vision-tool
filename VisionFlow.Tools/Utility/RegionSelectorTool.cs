// ==================== Vai trò chính:                Trích xuất 1 vùng ảnh chữ nhật (xoay được) từ ảnh gốc - hỗ trợ tự động "bám theo" vị trí/góc sản phẩm qua FixtureTool.TransformPoint
// ==================== Thành phần / Class tiêu biểu: RegionSelectorTool
// ==================== Phụ thuộc vào:                OpenCvSharp (WarpAffine, GetRectSubPix, FillPoly) + Core.Models (RegionInfo, RectRegion) + Core.Ports + Core.Tools
// ==================== Pattern / Kỹ thuật nổi bật:   ROI cropping + xoay ảnh (WarpAffine) + tích hợp Fixture Coordinate Space (buổi 99) qua AffineTransform2D optional input (serialize được)

using System;
using System.Linq;
using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Models;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;
using P2 = VisionFlow.Core.Models.Point2d;

namespace VisionFlow.Tools.Utility;

/// <summary>
/// Trích xuất (crop) 1 vùng hình chữ nhật (xoay được) từ ảnh gốc để tập trung xử lý.
///
/// TÍCH HỢP FIXTURE (buổi 99): Region được định nghĩa theo toạ độ "chuẩn" (lúc dạy/thiết kế pipeline). Nếu nối
/// <c>FixtureTransform</c> (lấy từ FixtureTool.TransformPoint) + <c>FixtureAngleOffset</c> (lấy từ
/// FixtureTool.FixtureAngle), Tool này sẽ TỰ ĐỘNG chuyển tâm ROI qua đúng vị trí thực tế của sản phẩm trên từng
/// khung hình (dù sản phẩm dịch chuyển/xoay tới đâu) TRƯỚC KHI cắt ảnh - không cần chỉnh lại Region thủ công.
/// Nếu không nối Fixture, Tool hoạt động y hệt kiểu ROI cố định tuyệt đối như trước.
///
/// Quy trình xử lý:
/// 1. Lấy Region (tâm, kích thước, góc) đã cấu hình; nếu có nối Fixture thì transform tâm + cộng thêm góc lệch.
/// 2. Nếu góc khác 0: xoay toàn bộ ảnh gốc quanh tâm ROI (WarpAffine, border = BackgroundColor).
/// 3. Cắt vùng chữ nhật đã "nắn thẳng" bằng GetRectSubPix (hỗ trợ sub-pixel).
/// 4. Xuất ảnh theo OutputMode: CroppedImage (chỉ trả vùng đã cắt) hoặc MaskedImage (giữ nguyên kích thước ảnh
///    gốc, tô nền BackgroundColor bên ngoài vùng ROI).
/// 5. Tính RegionRect (bounding box của ROI trong hệ toạ độ ảnh HIỆN TẠI - tiện gửi PLC/Robot) và RegionInfo
///    (diện tích, mức xám trung bình, độ tương phản bên trong vùng).
/// </summary>
[ToolMetadata("RegionSelector", DisplayName = "Region Selector", Category = "Utility",
    Description = "Extract a rotated rectangular ROI; optionally follow a product's live position/angle via Fixture.")]
public sealed class RegionSelectorTool : VisionTool
{
    #region 1. Ports
    private readonly InputPort<IVisionImage> _input; // ImageMatrix
    private readonly InputPort<AffineTransform2D> _fixtureTransform;  // (Tuỳ chọn) Nối từ FixtureTool.TransformPoint - tự bám theo vị trí sản phẩm
    private readonly InputPort<double> _fixtureAngleOffset;      // (Tuỳ chọn) Nối từ FixtureTool.FixtureAngle - cộng thêm vào RegionAngle để ROI xoay theo đúng sản phẩm

    private readonly OutputPort<IVisionImage> _outImage; // OutputImage
    private readonly OutputPort<RectRegion> _outRegionRect; // RegionRect: (x,y góc trên-trái, width, height) trong hệ toạ độ ảnh HIỆN TẠI
    private readonly OutputPort<RegionInfo> _outRegionInfo; // RegionInfo
    #endregion

    #region 2. Parameters
    // --- Tab Region ---
    private readonly ToolParameter<RotatedRectRegion> _region; // Khung chữ nhật xoay định nghĩa vùng cắt - hỗ trợ kéo-thả vẽ trực tiếp trên UI (ParameterInteraction.RotatedRectRegion)

    // --- Tab Output ---
    private readonly ToolParameter<string> _outputMode;      // "CroppedImage" (mặc định) | "MaskedImage"
    private readonly ToolParameter<string> _backgroundColor; // Màu nền "B,G,R" cho vùng trống (ROI xoay ra ngoài ảnh gốc, hoặc phần ngoài ROI ở chế độ MaskedImage)
    #endregion

    public RegionSelectorTool()
    {
        _input = AddInput<IVisionImage>("Image", "Image Matrix");
        _fixtureTransform = AddInput<AffineTransform2D>("FixtureTransform", "Fixture Transform (optional)", optional: true);
        _fixtureAngleOffset = AddInput<double>("FixtureAngleOffset", "Fixture Angle Offset (optional)", optional: true);

        _outImage = AddOutput<IVisionImage>("OutputImage", "Output Image");
        _outRegionRect = AddOutput<RectRegion>("RegionRect", "Region Rect");
        _outRegionInfo = AddOutput<RegionInfo>("RegionInfo", "Region Info");

        _region = AddParameter("Region",
            new RotatedRectRegion(new P2(150, 120), 200, 150, 0),
            "ROI", category: "Region", order: 1, interaction: ParameterInteraction.RotatedRectRegion);

        _outputMode = AddChoiceParameter("OutputMode", "CroppedImage", new[] { "CroppedImage", "MaskedImage" }, "Output Mode", category: "Output", order: 1);
        _backgroundColor = AddParameter<string>("BackgroundColor", "0,0,0", "Background Color (B,G,R)", category: "Output", order: 2);
    }

    protected override void OnExecute(IToolContext context)
    {
        var src = _input.Value!.AsMat();
        var r = _region.Value;

        // ----- Bước 1: Áp Fixture Transform nếu có nối - tâm ROI "bám theo" vị trí thực tế của sản phẩm -----
        P2 center = r.Center;
        double angleDeg = r.AngleDeg;
        if (_fixtureTransform.Value != null)
        {
            center = _fixtureTransform.Value.Transform(center); // Chuyển tâm ROI từ hệ toạ độ "chuẩn" (lúc dạy) sang toạ độ ảnh HIỆN TẠI
            angleDeg += _fixtureAngleOffset.Value;     // Cộng thêm góc lệch tìm được để ROI xoay theo đúng hướng sản phẩm
        }

        var centerF = new Point2f((float)center.X, (float)center.Y);
        var size = new Size((int)Math.Max(1, r.Width), (int)Math.Max(1, r.Height));
        Scalar bg = ParseBgrColor(_backgroundColor.Value, Scalar.Black);

        // ----- Bước 2+3: Xoay (nếu cần) + cắt vùng chữ nhật đã "nắn thẳng" bằng GetRectSubPix -----
        Mat source = src;
        bool rotated = false;
        if (Math.Abs(angleDeg) > 1e-3)
        {
            using var rot = Cv2.GetRotationMatrix2D(centerF, angleDeg, 1.0);
            source = new Mat();
            Cv2.WarpAffine(src, source, rot, src.Size(), InterpolationFlags.Linear, BorderTypes.Constant, bg); // Constant + BackgroundColor thay vì Replicate cứng như bản cũ, đúng tham số BackgroundColor trong tài liệu
            rotated = true;
        }

        Mat straightCrop = new Mat();
        Cv2.GetRectSubPix(source, size, centerF, straightCrop);
        if (rotated) source.Dispose();

        // ----- Bước 4: Tính RegionInfo (diện tích, mức xám trung bình, độ tương phản) trên vùng đã cắt -----
        using Mat grayForInfo = straightCrop.Channels() == 1 ? straightCrop.Clone() : straightCrop.CvtColor(ColorConversionCodes.BGR2GRAY);
        Cv2.MeanStdDev(grayForInfo, out Scalar meanScalar, out Scalar stdScalar);
        var regionInfo = new RegionInfo
        {
            Area = (double)r.Width * r.Height,
            MeanIntensity = meanScalar.Val0,
            Contrast = stdScalar.Val0,
            Width = r.Width,
            Height = r.Height,
            AngleDeg = angleDeg,
        };

        // ----- Bước 5: Tính RegionRect (bounding box của ROI trong hệ toạ độ ảnh HIỆN TẠI, có xoay) -----
        var corners = Compute4Corners(center, r.Width, r.Height, angleDeg);
        double minX = corners.Min(p => p.X), maxX = corners.Max(p => p.X);
        double minY = corners.Min(p => p.Y), maxY = corners.Max(p => p.Y);
        var regionRect = new RectRegion(minX, minY, maxX - minX, maxY - minY);

        // ----- Bước 6: Xuất ảnh theo OutputMode -----
        IVisionImage outputImage;
        if (_outputMode.Value == "MaskedImage")
        {
            // Giữ nguyên kích thước ảnh gốc, chỉ giữ nội dung bên trong ROI (có thể xoay), tô nền BackgroundColor bên ngoài
            Mat masked = new Mat(src.Size(), src.Type(), bg);
            using Mat maskBin = Mat.Zeros(src.Size(), MatType.CV_8UC1);
            var cvCorners = corners.Select(p => new Point((int)p.X, (int)p.Y)).ToArray();
            Cv2.FillPoly(maskBin, new[] { cvCorners }, Scalar.White);
            src.CopyTo(masked, maskBin);
            outputImage = new MatVisionImage(masked); // masked "cho đi" thẳng vào Output -> KHÔNG Dispose(masked) sau đây
            straightCrop.Dispose(); // Không dùng làm Output ở chế độ này nữa (chỉ đã dùng xong để tính RegionInfo) -> dọn dẹp tránh rò rỉ bộ nhớ
        }
        else
        {
            outputImage = new MatVisionImage(straightCrop); // "cho đi" thẳng vào Output -> KHÔNG Dispose(straightCrop) sau đây
        }

        // ----- Bước 7: Xuất kết quả -----
        _outImage.Value = outputImage;
        _outRegionRect.Value = regionRect;
        _outRegionInfo.Value = regionInfo;

        context.Log($"RegionSelector: Mode={_outputMode.Value}, Center=({center.X:F1},{center.Y:F1}), Angle={angleDeg:F1}deg" +
            (_fixtureTransform.Value != null ? " [Fixture-tracked]" : ""));
    }

    #region 3. Helpers

    /// <summary>Tính 4 góc của hình chữ nhật (tâm, kích thước, góc xoay) trong hệ toạ độ ảnh.</summary>
    private static P2[] Compute4Corners(P2 center, double width, double height, double angleDeg)
    {
        double hw = width / 2.0, hh = height / 2.0;
        double rad = angleDeg * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);
        P2 Rot(double dx, double dy) => new P2(center.X + dx * cos - dy * sin, center.Y + dx * sin + dy * cos);
        return new[] { Rot(-hw, -hh), Rot(hw, -hh), Rot(hw, hh), Rot(-hw, hh) };
    }

    /// <summary>Parse chuỗi màu định dạng "B,G,R" (ví dụ "0,0,0") thành Scalar OpenCV; trả về màu mặc định nếu parse lỗi.</summary>
    private static Scalar ParseBgrColor(string text, Scalar fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        var parts = text.Split(',');
        if (parts.Length != 3) return fallback;
        if (byte.TryParse(parts[0].Trim(), out byte b) && byte.TryParse(parts[1].Trim(), out byte g) && byte.TryParse(parts[2].Trim(), out byte rr))
            return new Scalar(b, g, rr);
        return fallback;
    }

    #endregion
}