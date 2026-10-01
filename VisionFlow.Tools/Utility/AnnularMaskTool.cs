// ==================== Vai trò chính:                Cô lập vùng kiểm tra dạng VÀNH KHĂN (ring) cho vật thể hình tròn - vòng bi, nắp chai, linh kiện tròn
// ==================== Thành phần / Class tiêu biểu: AnnularMaskTool
// ==================== Phụ thuộc vào:                OpenCvSharp, Core.Models (CircleResult, Circle, Point2d)
// ==================== Pattern / Kỹ thuật nổi bật:   Composite Mask bằng 2 hình tròn - vẽ đè hình tròn nhỏ
//                       (giá trị nền) lên trên hình tròn lớn (giá trị điền đầy) để "khoét lỗ" tạo hình vành khăn

using System;
using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Models; // CircleResult, Circle, Point2d
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;

namespace VisionFlow.Tools.Utility; // Cùng thư mục với RegionSelectorTool / ImageSelectorTool

/// <summary>Chế độ tạo mặt nạ dựa trên 2 đường tròn Inner/Outer.</summary>
public enum AnnularMaskMode
{
    Between,     // Mặc định theo tài liệu: giữ lại vùng NẰM GIỮA 2 đường tròn (hình vành khăn thật sự)
    InsideInner, // Chỉ giữ lại vùng bên TRONG đường tròn nhỏ (bỏ qua OuterCircle) - hữu ích khi chỉ cần kiểm tra phần lõi
    OutsideOuter // Chỉ giữ lại vùng NẰM NGOÀI đường tròn lớn (bỏ qua InnerCircle) - hữu ích khi cần kiểm tra nền xung quanh vật thể tròn
}

/// <summary>
/// AnnularMask: cô lập vùng kiểm tra dạng vành khăn cho các vật thể hình tròn (vòng bi, nắp chai, linh
/// kiện điện tử dạng tròn), giúp các Tool phía sau (BlobAnalysis, Threshold...) chỉ tập trung xử lý đúng
/// vùng vành khăn, bỏ qua hoàn toàn lỗ rỗng bên trong và nền thừa bên ngoài.
/// InnerCircle/OuterCircle là 2 CỔNG VÀO (không phải tham số cố định) vì trong thực tế vị trí/bán kính
/// vòng tròn thường đến từ 1 node FindCircleTool/HoughCircleDetection chạy động mỗi khung hình (sản phẩm
/// dịch chuyển trên băng chuyền), không phải giá trị cố định gõ tay 1 lần.
/// </summary>
[ToolMetadata(
    "AnnularMask",
    DisplayName = "Annular Mask",
    Category = "Utility",
    Description = "Create a ring-shaped (annulus) mask between two circles to isolate the inspection area of round objects")]
public sealed class AnnularMaskTool : VisionTool
{
    #region 1. Khai báo Port (In/Out)
    private readonly InputPort<IVisionImage> _imageMatrix;   // Ảnh gốc lấy từ camera
    private readonly InputPort<CircleResult> _innerCircle;   // Đường tròn bên trong (bán kính nhỏ) - vùng lỗ rỗng cần loại bỏ
    private readonly InputPort<CircleResult> _outerCircle;   // Đường tròn bên ngoài (bán kính lớn) - biên ngoài của vật thể

    private readonly OutputPort<IVisionImage> _outImageMatrix; // Ảnh sau xử lý ĐÃ CẮT theo khung bao (bounding box) của OuterCircle
    private readonly OutputPort<IVisionImage> _outMaskImage;   // Ảnh mặt nạ đen trắng, KHÔNG cắt, giữ nguyên kích thước ảnh gốc
    private readonly OutputPort<IVisionImage> _outMaskedImage; // Ảnh gốc (không cắt) với vùng ngoài vành khăn bị đổ đen/nền
    #endregion

    #region 2. Khai báo Parameter
    private readonly ToolParameter<AnnularMaskMode> _maskMode;
    private readonly ToolParameter<int> _fillValue;         // Giá trị pixel cho vùng GIỮ LẠI trên MaskImage (mặc định 255 = trắng)
    private readonly ToolParameter<int> _backgroundValue;   // Giá trị pixel cho vùng LOẠI BỎ trên MaskImage, đồng thời là màu tô lên MaskedImage (mặc định 0 = đen)
    private readonly ToolParameter<double> _minRingWidth;   // Độ dày tối thiểu chấp nhận được của vành khăn (OuterRadius - InnerRadius), pixel
    private readonly ToolParameter<bool> _validateCircleOrder; // true: bắt buộc kiểm tra InnerRadius < OuterRadius trước khi xử lý
    #endregion

    public AnnularMaskTool()
    {
        _imageMatrix = AddInput<IVisionImage>("ImageMatrix", "Image Matrix");
        _innerCircle = AddInput<CircleResult>("InnerCircle", "Inner Circle");
        _outerCircle = AddInput<CircleResult>("OuterCircle", "Outer Circle");

        _outImageMatrix = AddOutput<IVisionImage>("ImageMatrix", "Image Matrix");
        _outMaskImage = AddOutput<IVisionImage>("MaskImage", "Mask Image");
        _outMaskedImage = AddOutput<IVisionImage>("MaskedImage", "Masked Image");

        _maskMode = AddParameter("MaskMode", AnnularMaskMode.Between, "Mask Mode", category: "Mask", order: 1);
        _fillValue = AddParameter("FillValue", 255, "Fill Value", min: 0, max: 255, category: "Mask", order: 2);
        _backgroundValue = AddParameter("BackgroundValue", 0, "Background Value", min: 0, max: 255, category: "Mask", order: 3);
        _minRingWidth = AddParameter("MinRingWidth", 1.0, "Min Ring Width", min: 0.0, max: 10000.0, category: "Mask", order: 4);
        _validateCircleOrder = AddParameter("ValidateCircleOrder", true, "Validate Circle Order", category: "Mask", order: 5);
    }

    protected override void OnExecute(IToolContext context)
    {
        Mat src = _imageMatrix.Value!.AsMat();

        var inner = _innerCircle.Value ?? throw new ToolExecutionException("AnnularMask: InnerCircle input is not connected.");
        var outer = _outerCircle.Value ?? throw new ToolExecutionException("AnnularMask: OuterCircle input is not connected.");

        VisionFlow.Core.Models.Point2d innerCenter = inner.Circle.Center;
        double innerR = inner.Circle.Radius;
        VisionFlow.Core.Models.Point2d outerCenter = outer.Circle.Center;
        double outerR = outer.Circle.Radius;

        // ----- Kiểm tra hợp lệ hình học theo yêu cầu tài liệu -----
        if (_validateCircleOrder.Value && innerR >= outerR)
            throw new ToolExecutionException(
                $"AnnularMask: InnerCircle radius ({innerR:F1}) must be smaller than OuterCircle radius ({outerR:F1}). " +
                "Set ValidateCircleOrder=false to bypass this check (not recommended).");

        double ringWidth = outerR - innerR;
        if (ringWidth < _minRingWidth.Value)
            throw new ToolExecutionException(
                $"AnnularMask: ring width ({ringWidth:F1}px) is smaller than MinRingWidth ({_minRingWidth.Value}px).");

        int fill = _fillValue.Value;
        int bg = _backgroundValue.Value;

        // ----- Bước 1: dựng "keepMask" nội bộ chuẩn 0/255, KHÔNG phụ thuộc FillValue/BackgroundValue người dùng chọn -----
        // (tách riêng khỏi ảnh MaskImage xuất ra, để logic ghép ảnh MaskedImage luôn đúng dù người dùng đặt
        // FillValue/BackgroundValue thành bất kỳ cặp giá trị nào, kể cả khi họ đặt ngược 0/255).
        using Mat keepMask = new Mat(src.Size(), MatType.CV_8UC1, Scalar.Black); // 0 = loại bỏ (mặc định ban đầu)

        var outerCenterPt = new Point((int)Math.Round(outerCenter.X), (int)Math.Round(outerCenter.Y));
        var innerCenterPt = new Point((int)Math.Round(innerCenter.X), (int)Math.Round(innerCenter.Y));

        switch (_maskMode.Value)
        {
            case AnnularMaskMode.InsideInner:
                Cv2.Circle(keepMask, innerCenterPt, (int)Math.Round(innerR), Scalar.White, thickness: -1);
                break;

            case AnnularMaskMode.OutsideOuter:
                keepMask.SetTo(Scalar.White); // Giữ toàn bộ ảnh trước...
                Cv2.Circle(keepMask, outerCenterPt, (int)Math.Round(outerR), Scalar.Black, thickness: -1); // ...rồi khoét bỏ phần bên trong OuterCircle
                break;

            default: // Between - đúng hành vi mặc định theo tài liệu: vành khăn giữa 2 đường tròn
                Cv2.Circle(keepMask, outerCenterPt, (int)Math.Round(outerR), Scalar.White, thickness: -1); // Tô đầy vòng ngoài
                Cv2.Circle(keepMask, innerCenterPt, (int)Math.Round(innerR), Scalar.Black, thickness: -1); // Khoét lỗ vòng trong -> chừa lại đúng hình vành khăn
                break;
        }

        // ----- Bước 2: xuất MaskImage theo đúng FillValue/BackgroundValue người dùng chọn -----
        Mat maskOutput = new Mat(src.Size(), MatType.CV_8UC1, new Scalar(bg));
        maskOutput.SetTo(new Scalar(fill), keepMask); // SetTo(giá trị, mask): chỉ ghi đè tại pixel có mask khác 0

        // ----- Bước 3: xuất MaskedImage - ảnh gốc (không cắt), tô BackgroundValue vào vùng bị loại bỏ -----
        using Mat excludeMask = new Mat();
        Cv2.BitwiseNot(keepMask, excludeMask); // Đảo ngược: giờ "khác 0" nghĩa là vùng CẦN loại bỏ

        Mat maskedFull = src.Clone();
        Scalar bgColorForImage = src.Channels() == 1 ? new Scalar(bg) : new Scalar(bg, bg, bg);
        maskedFull.SetTo(bgColorForImage, excludeMask);

        // ----- Bước 4: cắt (crop) MaskedImage theo khung bao của OuterCircle để làm output "ImageMatrix" -----
        var bounding = new Rect(
            (int)Math.Floor(outerCenter.X - outerR),
            (int)Math.Floor(outerCenter.Y - outerR),
            (int)Math.Ceiling(outerR * 2),
            (int)Math.Ceiling(outerR * 2));

        Rect clamped = ClampToImage(bounding, src.Width, src.Height);
        if (clamped.Width <= 0 || clamped.Height <= 0)
            throw new ToolExecutionException("AnnularMask: OuterCircle bounding box falls entirely outside the image.");

        Mat cropped = new Mat(maskedFull, clamped).Clone();

        _outImageMatrix.Value = new MatVisionImage(cropped);
        _outMaskImage.Value = new MatVisionImage(maskOutput);
        _outMaskedImage.Value = new MatVisionImage(maskedFull);

        context.Log($"AnnularMask: mode={_maskMode.Value}, innerR={innerR:F1}, outerR={outerR:F1}, ringWidth={ringWidth:F1}px");
    }

    /// <summary>Kẹp 1 hình chữ nhật (có thể tràn ra ngoài ảnh khi OuterCircle nằm sát mép) về đúng phạm vi ảnh gốc.</summary>
    private static Rect ClampToImage(Rect rect, int imgWidth, int imgHeight)
    {
        int x = Math.Max(0, rect.X);
        int y = Math.Max(0, rect.Y);
        int right = Math.Min(imgWidth, rect.X + rect.Width);
        int bottom = Math.Min(imgHeight, rect.Y + rect.Height);
        return new Rect(x, y, Math.Max(0, right - x), Math.Max(0, bottom - y));
    }
}