// ==================== Vai trò chính:                Gắn 1 hệ toạ độ mới (Fixture) lên vật thể tìm được, cung cấp hàm chuyển đổi toạ độ TransformPoint/InverseTransformPoint để Tool phía sau tự bù trừ ROI theo vị trí/góc thực tế
// ==================== Thành phần / Class tiêu biểu: FixtureTool
// ==================== Phụ thuộc vào:                OpenCvSharp (chỉ dùng cho phần WarpAffine tuỳ chọn) + Core.Models (XYThetaOffset) + Core.Ports + Core.Tools
// ==================== Pattern / Kỹ thuật nổi bật:   Đúng công thức AffineTransform2D + Fixture.Update()/TransformPoint()/InverseTransform() đã học ở buổi 99, đóng gói thành object AffineTransform2D (serialize được) để Tool khác gọi .Transform()

using System;
using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Models;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;
using P2 = VisionFlow.Core.Models.Point2d;

namespace VisionFlow.Tools.Alignment;

/// <summary>
/// Fixture (buổi 99): sản phẩm trên băng chuyền không bao giờ đứng yên 1 chỗ - Tool này KHÔNG warp lại pixel
/// mà thay vào đó tính ra 2 HÀM CHUYỂN ĐỔI TOẠ ĐỘ (TransformPoint/InverseTransformPoint) đúng công thức Affine
/// Transform 2D đã học: mọi ROI/tool đo phía sau chỉ cần gọi TransformPoint(trainX, trainY) để tự động "bám theo"
/// vị trí/góc thực tế của sản phẩm, dù nó dịch chuyển/xoay tới đâu - KHÔNG cần warp lại toàn bộ ảnh (nhanh hơn,
/// không mất chi tiết do nội suy). Vẫn giữ tuỳ chọn xuất ResultImage đã warp cho trường hợp cần xem trực quan.
///
/// Công thức chính xác theo buổi 99:
///   dTheta = AlignmentAngle - ReferenceAngle
///   tx = AlignmentPoint.X - (cosA*RefX - sinA*RefY) ; ty = AlignmentPoint.Y - (sinA*RefX + cosA*RefY)
///   TransformPoint(px,py)        = (cosA*px - sinA*py + tx, sinA*px + cosA*py + ty)
///   InverseTransformPoint(px,py) = phép biến đổi ngược của trên (đưa toạ độ ảnh hiện tại về hệ toạ độ Fixture)
/// </summary>
[ToolMetadata("FixtureTool", DisplayName = "Fixture Tool", Category = "Alignment",
    Description = "Attach a new coordinate space to a found feature (Affine Transform 2D) - downstream tools call TransformPoint to follow the product's position/angle.")]
public sealed class FixtureTool : VisionTool
{
    #region 1. Ports
    private readonly InputPort<IVisionImage> _input;         // ImageMatrix: ảnh đầu vào
    private readonly InputPort<P2> _alignmentPoint;          // AlignmentPoint: điểm mốc tìm được trên ảnh runtime hiện tại (VD nối từ PMAlignt.BestMatchPoint)
    private readonly InputPort<double> _alignmentAngle;      // AlignmentAngle: góc xoay tìm được (VD nối từ PMAlignt.MatchAngle)
    private readonly InputPort<bool> _isFound;               // (Bổ sung thực tế) Cờ báo khung hình này có tìm thấy vật thể hợp lệ không (VD nối từ PMAlignt.Isfound) - optional, mặc định coi như true nếu không nối

    private readonly OutputPort<IVisionImage> _outResultImage;            // ResultImage
    private readonly OutputPort<AffineTransform2D> _outTransformPoint;         // TransformPoint: đối tượng biến đổi Reference (lúc dạy) -> hệ toạ độ hiện tại - gọi .Transform(p) để dùng
    private readonly OutputPort<AffineTransform2D> _outInverseTransformPoint;  // InverseTransformPoint: đối tượng biến đổi ngược lại - cũng gọi .Transform(p) (tham số đã "nạp sẵn" chiều ngược)
    private readonly OutputPort<XYThetaOffset> _outFixtureOffset;         // FixtureOffset: (dX, dY, dTheta) so với Reference
    private readonly OutputPort<double> _outFixtureAngle;                 // FixtureAngle: góc lệch (dTheta) của vật thể so với chuẩn
    private readonly OutputPort<P2> _outAlignmentCenter;                  // AlignmentCenter: toạ độ tâm vật thể đã dùng để lập Fixture (pass-through AlignmentPoint)
    private readonly OutputPort<bool> _outIsEstablished;                  // IsEstablished: Fixture khung hình này có được thiết lập thành công không
    #endregion

    #region 2. Parameters
    // --- Tab Reference ---
    private readonly ToolParameter<double> _referenceOriginX; // ReferenceOrigin.X - toạ độ điểm mốc lúc "dạy" (training)
    private readonly ToolParameter<double> _referenceOriginY; // ReferenceOrigin.Y
    private readonly ToolParameter<double> _referenceAngle;   // ReferenceAngle - góc chuẩn lúc "dạy" (mặc định 0)

    // --- Tab Warp (tuỳ chọn - phục vụ ResultImage trực quan, KHÔNG bắt buộc để dùng TransformPoint) ---
    private readonly ToolParameter<bool> _enableImageWarp;    // Bật thì warp ảnh trực quan; tắt thì ResultImage = ảnh gốc nguyên vẹn (tiết kiệm CPU khi chỉ cần TransformPoint)
    private readonly ToolParameter<string> _borderMode;
    private readonly ToolParameter<string> _interpolation;

    // --- Tab Behavior ---
    private readonly ToolParameter<bool> _skipWarpWhenNotFound; // Khi IsFound=false: bỏ qua warp, trả ảnh gốc + IsEstablished=false
    #endregion

    public FixtureTool()
    {
        _input = AddInput<IVisionImage>("Image", "Image Matrix");
        _alignmentPoint = AddInput<P2>("AlignmentPoint", "Alignment Point");
        _alignmentAngle = AddInput<double>("AlignmentAngle", "Alignment Angle");
        _isFound = AddInput<bool>("IsFound", "Is Found", optional: true);

        _outResultImage = AddOutput<IVisionImage>("ResultImage", "Result Image");
        _outTransformPoint = AddOutput<AffineTransform2D>("TransformPoint", "Transform Point (Reference -> Current)");
        _outInverseTransformPoint = AddOutput<AffineTransform2D>("InverseTransformPoint", "Inverse Transform Point (Current -> Reference)");
        _outFixtureOffset = AddOutput<XYThetaOffset>("FixtureOffset", "Fixture Offset");
        _outFixtureAngle = AddOutput<double>("FixtureAngle", "Fixture Angle");
        _outAlignmentCenter = AddOutput<P2>("AlignmentCenter", "Alignment Center");
        _outIsEstablished = AddOutput<bool>("IsEstablished", "Is Established");

        _referenceOriginX = AddParameter<double>("ReferenceOriginX", 0.0, "Reference Origin X", min: -100000.0, max: 100000.0, category: "Reference", order: 1);
        _referenceOriginY = AddParameter<double>("ReferenceOriginY", 0.0, "Reference Origin Y", min: -100000.0, max: 100000.0, category: "Reference", order: 2);
        _referenceAngle = AddParameter<double>("ReferenceAngle", 0.0, "Reference Angle (deg)", min: -180.0, max: 180.0, category: "Reference", order: 3);

        _enableImageWarp = AddParameter<bool>("EnableImageWarp", true, "Enable Image Warp", category: "Warp", order: 1);
        _borderMode = AddChoiceParameter("BorderMode", "Replicate", new[] { "Replicate", "ConstantBlack", "ConstantWhite" }, "Border Mode", category: "Warp", order: 2);
        _interpolation = AddChoiceParameter("Interpolation", "Linear", new[] { "Nearest", "Linear", "Cubic" }, "Interpolation", category: "Warp", order: 3);

        _skipWarpWhenNotFound = AddParameter<bool>("SkipWarpWhenNotFound", false, "Skip Warp When Not Found", category: "Behavior", order: 1); // Mặc định FALSE - bắt buộc chủ động nối IsFound rồi mới bật true, tránh bỏ qua nhầm khi IsFound chưa được nối dây
    }

    protected override void OnExecute(IToolContext context)
    {
        if (_input.Value == null)
            throw new ArgumentNullException(nameof(_input), "Ảnh đầu vào của FixtureTool không được rỗng!");

        var src = _input.Value!.AsMat();
        P2 alignPoint = _alignmentPoint.Value;
        double alignAngleDeg = _alignmentAngle.Value;

        // LƯU Ý: nếu cổng IsFound không được nối dây, giá trị mặc định của bool là false. Vì hệ thống Port hiện
        // tại không phân biệt được "chưa nối" và "nối nhưng = false", SkipWarpWhenNotFound mặc định = FALSE để
        // an toàn (không tự động bỏ qua Fixture khi người dùng chưa kịp nối IsFound). Muốn dùng tính năng này,
        // BẮT BUỘC nối IsFound (VD từ PMAlignt.Isfound) VÀ bật SkipWarpWhenNotFound = true.
        bool isFoundEffective = _isFound.Value;

        double refX = _referenceOriginX.Value, refY = _referenceOriginY.Value, refAngle = _referenceAngle.Value;

        if (_skipWarpWhenNotFound.Value && !isFoundEffective)
        {
            _outResultImage.Value = _input.Value!.Clone();
            _outTransformPoint.Value = AffineTransform2D.Identity;   // Không có dữ liệu tin cậy -> trả về phép biến đổi đồng nhất (identity), an toàn hơn null
            _outInverseTransformPoint.Value = AffineTransform2D.Identity;
            _outFixtureOffset.Value = default;
            _outFixtureAngle.Value = 0.0;
            _outAlignmentCenter.Value = alignPoint;
            _outIsEstablished.Value = false;
            context.Log("FixtureTool: IsFound=false, bỏ qua thiết lập Fixture (trả về ảnh gốc + hàm identity).");
            return;
        }

        // ----- Đúng công thức Fixture.Update() buổi 99 -----
        double dThetaDeg = NormalizeAngleDiff(alignAngleDeg - refAngle);
        double rad = dThetaDeg * Math.PI / 180.0;
        double cosA = Math.Cos(rad), sinA = Math.Sin(rad);
        double tx = alignPoint.X - (cosA * refX - sinA * refY);
        double ty = alignPoint.Y - (sinA * refX + cosA * refY);

        // TransformPoint: đối tượng dữ liệu thuần (CosA/SinA/Tx/Ty) - gọi .Transform(p) sẽ ra đúng công thức
        // buổi 99: điểm trong hệ toạ độ Reference (lúc dạy) -> toạ độ hiện tại trên ảnh runtime.
        var transformPoint = new AffineTransform2D { CosA = cosA, SinA = sinA, Tx = tx, Ty = ty };

        // InverseTransformPoint: cũng là 1 object AffineTransform2D, nhưng "nạp sẵn" đúng tham số NGHỊCH ĐẢO
        // (đúng công thức buổi 99, det luôn = 1 vì đây là phép xoay thuần, không scale) - vẫn gọi .Transform(p)
        // y hệt cách dùng ở trên, KHÔNG cần gọi .InverseTransform() - tham số đã tự "đảo chiều" sẵn trong object này.
        double det = cosA * cosA + sinA * sinA;
        double invCos = cosA / det, invSin = -sinA / det;
        double invTx = -(invCos * tx - invSin * ty), invTy = -(invSin * tx + invCos * ty);
        var inverseTransformPoint = new AffineTransform2D { CosA = invCos, SinA = invSin, Tx = invTx, Ty = invTy };

        var offset = new XYThetaOffset(alignPoint.X - refX, alignPoint.Y - refY, dThetaDeg);

        // ----- (Tuỳ chọn) Warp cả ảnh cho mục đích trực quan - KHÔNG bắt buộc để dùng TransformPoint -----
        IVisionImage resultImage;
        if (_enableImageWarp.Value)
        {
            Point2f alignPointF = new Point2f((float)alignPoint.X, (float)alignPoint.Y);
            using Mat rotMat = Cv2.GetRotationMatrix2D(alignPointF, -dThetaDeg, 1.0); // Xoay ngược lại đúng góc lệch, quanh điểm đã tìm thấy
            rotMat.Set(0, 2, rotMat.At<double>(0, 2) + (refX - alignPoint.X));
            rotMat.Set(1, 2, rotMat.At<double>(1, 2) + (refY - alignPoint.Y));

            InterpolationFlags interp = _interpolation.Value switch
            {
                "Nearest" => InterpolationFlags.Nearest,
                "Cubic" => InterpolationFlags.Cubic,
                _ => InterpolationFlags.Linear,
            };
            (BorderTypes borderType, Scalar borderValue) = _borderMode.Value switch
            {
                "ConstantBlack" => (BorderTypes.Constant, new Scalar(0, 0, 0)),
                "ConstantWhite" => (BorderTypes.Constant, new Scalar(255, 255, 255)),
                _ => (BorderTypes.Replicate, new Scalar(0, 0, 0)),
            };

            Mat dst = new Mat();
            Cv2.WarpAffine(src, dst, rotMat, src.Size(), interp, borderType, borderValue);
            resultImage = new MatVisionImage(dst); // dst "cho đi" thẳng vào Output -> KHÔNG Dispose(dst) sau đây
        }
        else
        {
            resultImage = _input.Value!.Clone(); // Không warp -> trả bản sao nguyên vẹn ảnh gốc
        }

        // ----- Xuất kết quả -----
        _outResultImage.Value = resultImage;
        _outTransformPoint.Value = transformPoint;
        _outInverseTransformPoint.Value = inverseTransformPoint;
        _outFixtureOffset.Value = offset;
        _outFixtureAngle.Value = dThetaDeg;
        _outAlignmentCenter.Value = alignPoint;
        _outIsEstablished.Value = true;

        context.Log($"FixtureTool: Offset(dX={offset.X:F1}, dY={offset.Y:F1}, dTheta={dThetaDeg:F1}deg) quanh điểm ({alignPoint.X:F1},{alignPoint.Y:F1}).");
    }

    #region 3. Helpers

    /// <summary>Chuẩn hoá hiệu 2 góc về khoảng [-180, 180] độ, xử lý đúng trường hợp wraparound.</summary>
    private static double NormalizeAngleDiff(double diffDeg)
    {
        double d = diffDeg % 360.0;
        if (d > 180.0) d -= 360.0;
        if (d < -180.0) d += 360.0;
        return d;
    }

    #endregion
}