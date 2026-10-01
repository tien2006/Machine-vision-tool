// ==================== Vai trò chính:                Gắn 1 hệ toạ độ mới (Fixture) lên vật thể tìm được, cung cấp hàm chuyển đổi toạ độ TransformPoint/InverseTransformPoint để Tool phía sau tự bù trừ ROI theo vị trí/góc thực tế
// ==================== Thành phần / Class tiêu biểu: FixtureTool
// ==================== Phụ thuộc vào:                OpenCvSharp (chỉ dùng cho phần WarpAffine tuỳ chọn) + Core.Models (XYThetaOffset, AlignResult, AffineTransform2D) + Core.Ports + Core.Tools
// ==================== Pattern / Kỹ thuật nổi bật:   Đúng công thức AffineTransform2D + Fixture.Update()/TransformPoint()/InverseTransform() đã học ở buổi 99, đóng gói thành object AffineTransform2D (serialize được) để Tool khác gọi .Transform()
// ==================== SỬA (trường phái gộp):        Thay 3 input rời (AlignmentPoint/AlignmentAngle/IsFound) bằng DUY NHẤT 1 input AlignResult
//                                                     — đúng cách nối "CogPMAlignTool -> CogFixtureTool" 1 dây gộp mà VisionPro QuickBuild dùng,
//                                                     thay vì phải kéo 3 dây rời như bản trước.
// ==================== SỬA (bỏ trùng lặp công thức nghịch đảo): Dùng AffineTransform2D.Inverse() thay vì tự tính lại
//                                                     (invCos/invSin/invTx/invTy chia cho det) — công thức nghịch đảo
//                                                     giờ chỉ tồn tại đúng 1 nơi duy nhất trong AffineTransform2D.
// ==================== SỬA (dùng Scale thật):        AlignResult giờ mang theo Scale thật từ PMAlign (thay vì
//                                                     luôn ngầm định 1.0) — Fixture đọc ra và gán thẳng vào
//                                                     transformPoint.Scale, để bù được cả trường hợp vật chụp
//                                                     gần/xa camera khác lúc dạy mẫu (không chỉ xoay+tịnh tiến).

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
/// Công thức chính xác theo buổi 99 (mục 4.1 — Tính Transform từ 2 trạng thái Reference/Current):
///   dTheta = AlignmentAngle - ReferenceAngle
///   tx = AlignmentPoint.X - (cosA*RefX - sinA*RefY) ; ty = AlignmentPoint.Y - (sinA*RefX + cosA*RefY)
///   TransformPoint(px,py)        = (cosA*px - sinA*py + tx, sinA*px + cosA*py + ty)
///   InverseTransformPoint(px,py) = phép biến đổi ngược của trên (đưa toạ độ ảnh hiện tại về hệ toạ độ Fixture)
/// (Đây là trường hợp riêng Scale=1.0 của công thức Similarity tổng quát trong AffineTransform2D - từ bản này
/// trở đi, Scale được lấy THẬT từ AlignResult.Scale do PMAlign ước lượng, không còn ngầm định = 1.0 nữa.)
///
/// LƯU Ý (RefX/RefY/RefAngle là gì): đây KHÔNG phải gốc toạ độ (0,0) của ảnh, mà là toạ độ + góc của điểm mốc
/// (thường là tâm ROI Template) TẠI THỜI ĐIỂM DẠY MẪU (training) trên ảnh Golden. Nó vừa là điểm gốc quy chiếu,
/// vừa là tâm xoay trong công thức phía trên — PHẢI đặt đúng bằng Center/AngleDeg của Template trong PMAlign,
/// nếu để mặc định (0,0,0) trong khi vật không thực sự nằm ở góc ảnh, Transform sẽ bị tính sai hoàn toàn.
/// </summary>
[ToolMetadata("FixtureTool", DisplayName = "Fixture Tool", Category = "Alignment",
    Description = "Attach a new coordinate space to a found feature (Affine Transform 2D) - downstream tools call TransformPoint to follow the product's position/angle.")]
public sealed class FixtureTool : VisionTool
{
    #region 1. Ports
    private readonly InputPort<IVisionImage> _input;         // ImageMatrix: ảnh đầu vào
    private readonly InputPort<AlignResult> _alignResult;    // AlignResult: object gộp (MatchedCenter, MatchedAngleDeg, Scale, Judge) - nối từ PMAlignt.Result

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
    private readonly ToolParameter<double> _referenceOriginX; // ReferenceOrigin.X - toạ độ điểm mốc lúc "dạy" (training) - tương ứng "RefX" trong tài liệu Buổi 99
    private readonly ToolParameter<double> _referenceOriginY; // ReferenceOrigin.Y - tương ứng "RefY"
    private readonly ToolParameter<double> _referenceAngle;   // ReferenceAngle - góc chuẩn lúc "dạy" (mặc định 0) - tương ứng "RefAngle"

    // --- Tab Warp (tuỳ chọn - phục vụ ResultImage trực quan, KHÔNG bắt buộc để dùng TransformPoint) ---
    private readonly ToolParameter<bool> _enableImageWarp;    // Bật thì warp ảnh trực quan; tắt thì ResultImage = ảnh gốc nguyên vẹn (tiết kiệm CPU khi chỉ cần TransformPoint)
    private readonly ToolParameter<string> _borderMode;
    private readonly ToolParameter<string> _interpolation;

    // --- Tab Behavior ---
    private readonly ToolParameter<bool> _skipWarpWhenNotFound; // Khi Judge != OK: bỏ qua warp, trả ảnh gốc + IsEstablished=false
    #endregion

    public FixtureTool()
    {
        _input = AddInput<IVisionImage>("Image", "Image Matrix");
        _alignResult = AddInput<AlignResult>("AlignResult", "Align Result");

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

        // Mặc định FALSE giữ nguyên tinh thần bản gốc: bắt buộc chủ động hiểu rõ AlignResult.Judge trước khi bật true,
        // tránh trường hợp Judge mặc định (nếu vì lý do nào đó AlignResult chưa có giá trị thật) bị hiểu nhầm là OK.
        _skipWarpWhenNotFound = AddParameter<bool>("SkipWarpWhenNotFound", false, "Skip Warp When Not Found", category: "Behavior", order: 1);
    }

    protected override void OnExecute(IToolContext context)
    {
        if (_input.Value == null)
            throw new ArgumentNullException(nameof(_input), "Ảnh đầu vào của FixtureTool không được rỗng!");

        if (_alignResult.Value is null)
            throw new ArgumentNullException(nameof(_alignResult), "Cổng 'AlignResult' chưa được nối! Hãy nối từ PMAlignt.Result (hoặc tool alignment tương đương) sang FixtureTool.");

        var src = _input.Value!.AsMat();

        var ar = _alignResult.Value;
        P2 alignPoint = ar.MatchedCenter;          // Tương ứng "Xcur, Ycur" trong công thức Buổi 99
        double alignAngleDeg = ar.MatchedAngleDeg; // Tương ứng "θcur" trong công thức Buổi 99 (GÓC TUYỆT ĐỐI, không phải Offset.Theta là góc lệch)
        double alignScale = ar.Scale;              // MỚI: hệ số scale thật tìm được từ PMAlign (mặc định 1.0 nếu tool nguồn chưa hỗ trợ/không tính được)
        bool isFoundEffective = ar.Judge == Judge.OK; // Thay thế hoàn toàn vai trò của cổng IsFound rời trước đây

        // refX, refY, refAngle: toạ độ + góc của điểm mốc (tâm ROI Template) LÚC DẠY MẪU trên ảnh Golden —
        // KHÔNG phải gốc (0,0) của ảnh. Xem giải thích chi tiết ở docstring class phía trên.
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
            context.Log("FixtureTool: AlignResult.Judge != OK, bỏ qua thiết lập Fixture (trả về ảnh gốc + hàm identity).");
            return;
        }

        // ----- Đúng công thức Fixture.Update() buổi 99 (mục 4.1) -----
        double dThetaDeg = NormalizeAngleDiff(alignAngleDeg - refAngle);
        double rad = dThetaDeg * Math.PI / 180.0;
        double cosA = Math.Cos(rad), sinA = Math.Sin(rad);
        double tx = alignPoint.X - (cosA * refX - sinA * refY);
        double ty = alignPoint.Y - (sinA * refX + cosA * refY);
        // GHI CHÚ: công thức tx, ty ở trên vẫn giữ nguyên dạng thuần xoay (không nhân alignScale) đúng y hệt
        // tài liệu Buổi 99 - lý do: tx/ty mô tả độ TỊNH TIẾN của điểm mốc (refX,refY) -> (alignPoint.X,Y), bản
        // thân phép tịnh tiến này không phụ thuộc scale. Scale chỉ ảnh hưởng đến việc CÁC ĐIỂM KHÁC (không phải
        // điểm mốc) bị "phóng to/thu nhỏ" quanh điểm mốc đó khi áp Transform() - đúng theo công thức Similarity
        // Transform tổng quát trong AffineTransform2D.Transform().

        // TransformPoint: đối tượng dữ liệu thuần (CosA/SinA/Tx/Ty/Scale) - gọi .Transform(p) sẽ ra đúng công
        // thức Similarity Transform tổng quát: điểm trong hệ toạ độ Reference (lúc dạy) -> toạ độ hiện tại.
        // SỬA: thêm "Scale = alignScale" — trước đây không set nên luôn ngầm định 1.0 (chỉ xoay+tịnh tiến).
        // Giờ nếu PMAlign ước lượng được scale khác 1 (vật chụp gần/xa camera khác lúc dạy), Fixture sẽ bù
        // đúng cả phần phóng to/thu nhỏ này cho mọi điểm ROI/Caliper phía sau, không chỉ xoay+tịnh tiến.
        var transformPoint = new AffineTransform2D { CosA = cosA, SinA = sinA, Tx = tx, Ty = ty, Scale = alignScale };

        // Dùng lại đúng 1 công thức nghịch đảo duy nhất (đã tổng quát hoá hỗ trợ Scale) định nghĩa trong
        // AffineTransform2D - giờ tự động bù đúng cả Scale khi tính nghịch đảo, không cần sửa gì thêm ở đây.
        var inverseTransformPoint = transformPoint.Inverse();

        var offset = new XYThetaOffset(alignPoint.X - refX, alignPoint.Y - refY, dThetaDeg);

        // ----- (Tuỳ chọn) Warp cả ảnh cho mục đích trực quan - KHÔNG bắt buộc để dùng TransformPoint -----
        IVisionImage resultImage;
        if (_enableImageWarp.Value)
        {
            Point2f alignPointF = new Point2f((float)alignPoint.X, (float)alignPoint.Y);
            // dThetaDeg dương = vật đang nghiêng CW so với Reference -> cần xoay ảnh CCW cùng độ lớn để đưa về
            // thẳng -> theo quy ước OpenCV (dương = CCW), phải truyền +dThetaDeg (KHÔNG đảo dấu), đúng khớp với
            // quy ước CW-dương đã dùng xuyên suốt hệ thống (giống cách PMAlignTool làm thẳng Template).
            // GHI CHÚ: đoạn WarpAffine này CHƯA áp dụng alignScale (vẫn truyền scale=1.0 cố định vào
            // GetRotationMatrix2D) - đây là phần warp ẢNH TRỰC QUAN, tách biệt với TransformPoint/Inverse ở
            // trên (đã bù đúng Scale). Nếu muốn ResultImage cũng phóng to/thu nhỏ đúng theo Scale thật, cần đổi
            // tham số thứ 3 của GetRotationMatrix2D dưới đây từ "1.0" thành "1.0 / alignScale" - hiện để nguyên
            // 1.0 vì đây là thay đổi ngoài phạm vi câu hỏi (chỉ tập trung TransformPoint), tránh sửa lan ra
            // phần không được yêu cầu.
            using Mat rotMat = Cv2.GetRotationMatrix2D(alignPointF, dThetaDeg, 1.0 / alignScale);
            rotMat.Set(0, 2, rotMat.At<double>(0, 2) + (refX - alignPoint.X));
            // Cộng thêm một lượng lệch theo trục X bằng hiệu số refX - alignPoint.X.
            // Lượng này giúp dịch chuyển bức ảnh sao cho điểm gốc mốc lúc dạy (refX) dịch về đúng vị trí thực tế của vật (alignPoint.X).

            rotMat.Set(1, 2, rotMat.At<double>(1, 2) + (refY - alignPoint.Y));
            // Tương tự, cộng thêm lượng lệch theo trục Y (refY - alignPoint.Y) để khớp trục dọc.

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
            // Cv2.WarpAffine: là hàm thực hiện phép biến đổi Affine (Affine Transformation) lên toàn bộ bức ảnh.

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

        context.Log($"FixtureTool: Offset(dX={offset.X:F1}, dY={offset.Y:F1}, dTheta={dThetaDeg:F1}deg, Scale={alignScale:F3}) quanh điểm ({alignPoint.X:F1},{alignPoint.Y:F1}).");
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