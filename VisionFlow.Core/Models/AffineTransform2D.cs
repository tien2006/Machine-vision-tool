// ==================== Vai trò chính:                Biểu diễn 1 phép biến đổi Affine 2D (xoay + tịnh tiến) dạng DỮ LIỆU THUẦN (serialize được), đúng y hệt class AffineTransform2D đã học ở buổi 99
// ==================== Thành phần / Class tiêu biểu: AffineTransform2D
// ==================== Phụ thuộc vào:                Core.Models (Point2d)
// ==================== Pattern / Kỹ thuật nổi bật:   POCO chỉ gồm 4 số double (CosA/SinA/Tx/Ty) - KHÁC Func<> (không serialize được), nhưng vẫn gọi .Transform() y hệt

using System;

namespace VisionFlow.Core.Models;

/// <summary>
/// Phép biến đổi Affine 2D (xoay thuần + tịnh tiến, KHÔNG scale) - đúng công thức đã học ở buổi 99.
/// Thiết kế dạng DỮ LIỆU THUẦN (chỉ 4 property double) thay vì Func&lt;Point2d,Point2d&gt; vì Output Port cần
/// serialize được ra JSON để hiển thị UI - delegate/hàm không serialize được, gây lỗi
/// "Serialization of System.Func... is not supported". Cách dùng thì giống hệt: gọi <see cref="Transform"/>.
/// </summary>
public sealed class AffineTransform2D
{
    public double CosA { get; set; }
    public double SinA { get; set; }
    public double Tx { get; set; }
    public double Ty { get; set; }

    /// <summary>Constructor rỗng - BẮT BUỘC phải có để hệ thống JSON deserialize lại được (VD khi load lại project đã lưu).</summary>
    public AffineTransform2D() { }

    /// <summary>Dựng phép biến đổi từ góc xoay (radian) + độ dịch (Tx, Ty) - đúng cách buổi 99 khởi tạo AffineTransform2D(angle, tx, ty).</summary>
    public AffineTransform2D(double angleRad, double tx, double ty)
    {
        CosA = Math.Cos(angleRad);
        SinA = Math.Sin(angleRad);
        Tx = tx;
        Ty = ty;
    }

    /// <summary>Áp phép biến đổi lên 1 điểm - đúng công thức buổi 99: x' = cosA*x - sinA*y + tx ; y' = sinA*x + cosA*y + ty.</summary>
    public Point2d Transform(Point2d p) => new Point2d(
        CosA * p.X - SinA * p.Y + Tx,
        SinA * p.X + CosA * p.Y + Ty);

    /// <summary>Áp phép biến đổi NGƯỢC lên 1 điểm (chỉ đúng khi object này thật sự là phép xoay thuần, det luôn = 1) - đúng công thức buổi 99.</summary>
    public Point2d InverseTransform(Point2d p)
    {
        double dx = p.X - Tx, dy = p.Y - Ty;
        return new Point2d(CosA * dx + SinA * dy, -SinA * dx + CosA * dy);
    }

    /// <summary>Phép biến đổi đồng nhất (không xoay, không dịch) - dùng làm giá trị mặc định an toàn khi chưa thiết lập được Fixture.</summary>
    public static AffineTransform2D Identity => new AffineTransform2D(0.0, 0.0, 0.0);
}