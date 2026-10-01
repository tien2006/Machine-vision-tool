// ==================== Vai trò chính:                Biểu diễn 1 phép biến đổi Similarity 2D (xoay + tịnh tiến + scale đều) dạng DỮ LIỆU THUẦN (serialize được)
// ==================== Thành phần / Class tiêu biểu: AffineTransform2D
// ==================== Phụ thuộc vào:                Core.Models (Point2d)
// ==================== Pattern / Kỹ thuật nổi bật:   POCO chỉ gồm 5 số double (CosA/SinA/Tx/Ty/Scale) - KHÁC Func<> (không serialize được), nhưng vẫn gọi .Transform() y hệt
// ==================== SỬA (tổng quát hoá): Bổ sung Scale (mặc định 1.0) để dùng chung được cho cả trường hợp
//                                            xoay thuần (buổi 99, Fixture) LẪN trường hợp có scale (VD PMAlign
//                                            ước lượng được scale khác 1 khi vật chụp gần/xa camera khác lúc dạy).
//                                            Khi Scale=1.0 (giá trị mặc định), mọi công thức bên dưới thu gọn
//                                            về ĐÚNG Y HỆT bản cũ (chỉ xoay thuần) - hoàn toàn tương thích ngược,
//                                            không phá vỡ bất kỳ code nào đang dùng class này với Scale ngầm định = 1.

using System;

namespace VisionFlow.Core.Models;

/// <summary>
/// Phép biến đổi Similarity 2D (xoay + tịnh tiến + scale đều) - tổng quát hoá từ công thức thuần xoay đã học
/// ở buổi 99 (buổi 99 chỉ dùng trường hợp riêng Scale=1). Thiết kế dạng DỮ LIỆU THUẦN (chỉ 5 property double)
/// thay vì Func&lt;Point2d,Point2d&gt; vì Output Port cần serialize được ra JSON để hiển thị UI - delegate/hàm
/// không serialize được, gây lỗi "Serialization of System.Func... is not supported". Cách dùng thì giống hệt:
/// gọi <see cref="Transform"/>.
/// </summary>
public sealed class AffineTransform2D
{
    public double CosA { get; set; }
    public double SinA { get; set; }
    public double Tx { get; set; }
    public double Ty { get; set; }

    /// <summary>
    /// MỚI: Hệ số scale đều (uniform scale) của phép biến đổi. Mặc định = 1.0 (không phóng to/thu nhỏ) - đúng
    /// giả định của buổi 99 (Fixture chỉ xoay + tịnh tiến, KHÔNG scale). Chỉ khác 1.0 khi phép biến đổi được
    /// dựng từ 1 nguồn CÓ scale thực sự (VD trực tiếp từ ma trận Affine mà PMAlign/EstimateAffinePartial2D
    /// ước lượng được, nếu sau này muốn truyền cả scale qua Fixture thay vì bỏ qua như hiện tại).
    /// </summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>Constructor rỗng - BẮT BUỘC phải có để hệ thống JSON deserialize lại được (VD khi load lại project đã lưu).</summary>
    public AffineTransform2D() { }

    /// <summary>Dựng phép biến đổi THUẦN XOAY (Scale=1) từ góc xoay (radian) + độ dịch (Tx, Ty) - đúng cách buổi 99 khởi tạo AffineTransform2D(angle, tx, ty).</summary>
    public AffineTransform2D(double angleRad, double tx, double ty)
    {
        CosA = Math.Cos(angleRad);
        SinA = Math.Sin(angleRad);
        Tx = tx;
        Ty = ty;
        Scale = 1.0; // Tường minh cho dễ đọc, dù đây cũng là giá trị mặc định của property phía trên
    }

    /// <summary>
    /// MỚI: Dựng phép biến đổi TỔNG QUÁT (có scale) từ góc xoay (radian) + độ dịch (Tx, Ty) + hệ số scale.
    /// Dùng khi nguồn dữ liệu có mang theo thông tin scale thực sự (VD ma trận Affine ước lượng bằng RANSAC).
    /// </summary>
    public AffineTransform2D(double angleRad, double tx, double ty, double scale)
    {
        CosA = Math.Cos(angleRad);
        SinA = Math.Sin(angleRad);
        Tx = tx;
        Ty = ty;
        Scale = scale;
    }

    /// <summary>
    /// Trả về phép biến đổi NGƯỢC dưới dạng 1 object AffineTransform2D MỚI (không sửa object gốc) — object
    /// trả về khi gọi .Transform(p) sẽ cho kết quả y hệt gọi InverseTransform(p) trên object gốc. Dùng khi cần
    /// TRUYỀN ĐI phép nghịch đảo này cho tool khác (VD FixtureTool xuất InverseTransformPoint), để tool đó luôn
    /// chỉ cần gọi .Transform(p) một cách thống nhất, không cần biết/phân biệt đây là forward hay inverse.
    /// SỬA (tổng quát hoá - có Scale): công thức nghịch đảo giờ tổng quát cho cả trường hợp Scale != 1, suy ra
    /// bằng cách giải ngược Transform(p) tổng quát, so khớp lại đúng dạng Transform() để tìm bộ hệ số mới:
    ///   Scale' = 1/Scale ; CosA' = CosA ; SinA' = -SinA
    ///   Tx' = -(CosA*Tx + SinA*Ty)/Scale ; Ty' = (SinA*Tx - CosA*Ty)/Scale
    /// Khi Scale=1.0, các công thức trên thu gọn về ĐÚNG bộ hệ số của phiên bản thuần xoay trước đây
    /// (Tx'=-(CosA*Tx+SinA*Ty), Ty'=SinA*Tx-CosA*Ty) - xác nhận đây là bản mở rộng tương thích ngược hoàn toàn.
    /// </summary>
    public AffineTransform2D Inverse() => new AffineTransform2D
    {
        Scale = 1.0 / Scale,
        CosA = CosA,
        SinA = -SinA,
        Tx = -(CosA * Tx + SinA * Ty) / Scale,
        Ty = (SinA * Tx - CosA * Ty) / Scale
    };


    /// <summary>
    /// Áp phép biến đổi lên 1 điểm - công thức TỔNG QUÁT (có Scale):
    ///   x' = Scale*(CosA*x - SinA*y) + Tx ; y' = Scale*(SinA*x + CosA*y) + Ty
    /// Khi Scale=1.0 (mặc định), công thức này thu gọn về ĐÚNG công thức thuần xoay của buổi 99:
    ///   x' = CosA*x - SinA*y + Tx ; y' = SinA*x + CosA*y + Ty
    /// </summary>
    public Point2d Transform(Point2d p) => new Point2d(
        Scale * (CosA * p.X - SinA * p.Y) + Tx,
        Scale * (SinA * p.X + CosA * p.Y) + Ty);

    /// <summary>
    /// Áp phép biến đổi NGƯỢC lên 1 điểm - công thức TỔNG QUÁT (có Scale), suy ra bằng cách giải ngược từ
    /// Transform(p): dx = x-Tx, dy = y-Ty => x_goc = (CosA*dx + SinA*dy)/Scale ; y_goc = (-SinA*dx + CosA*dy)/Scale.
    /// Khi Scale=1.0, thu gọn về đúng công thức cũ của buổi 99 (chỉ khác phép chia cho 1, không đổi giá trị).
    /// LƯU Ý: nếu Scale=0 sẽ chia cho 0 (NaN/Infinity) - về mặt hình học, Scale=0 nghĩa là "thu nhỏ vật về đúng
    /// 1 điểm", vốn KHÔNG THỂ có phép nghịch đảo (không thể suy ngược lại toạ độ ban đầu từ 1 điểm duy nhất) -
    /// đây là giới hạn toán học tất yếu, không phải lỗi code.
    /// </summary>
    public Point2d InverseTransform(Point2d p)
    {
        double dx = p.X - Tx, dy = p.Y - Ty;
        return new Point2d(
            (CosA * dx + SinA * dy) / Scale,
            (-SinA * dx + CosA * dy) / Scale);
    }

    /// <summary>Phép biến đổi đồng nhất (không xoay, không dịch, không scale) - dùng làm giá trị mặc định an toàn khi chưa thiết lập được Fixture.</summary>
    public static AffineTransform2D Identity => new AffineTransform2D(0.0, 0.0, 0.0); // Scale mặc định = 1.0 từ property, không cần truyền thêm
}