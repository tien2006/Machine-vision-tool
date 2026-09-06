// ==================== Vai trò chính:                Định nghĩa các kiểu dữ liệu hình học & kết quả thuật toán thuần túy (POCO/DTO)
// ==================== Thành phần / Class tiêu biểu: Point2d, Circle, LineSegment, RectRegion, RotatedRectRegion, CircleRegion,
                                                      // CaliperRegion, Judge, VisionResult, CircleResult, LineResult, AlignResult, VisionBlob
// ==================== Phụ thuộc vào:                Không phụ thuộc gì
// ==================== Pattern / Kỹ thuật nổi bật:   record struct (value type, immutable, hiệu năng cao, không rác GC)

using System;
using System.Collections.Generic;

namespace VisionFlow.Core.Models;

/// <summary>
/// Vùng hình chữ nhật xoay, định nghĩa bằng tâm, kích thước và góc xoay.
/// </summary>
public readonly record struct RotatedRectRegion(
    Point2d Center,
    double Width,
    double Height,
    double AngleDeg // Góc xoay tính bằng đơn vị độ
);

/// <summary>
/// Vùng hình tròn, định nghĩa bằng tâm và bán kính.
/// </summary>
public readonly record struct CircleRegion(
    Point2d Center,
    double Radius
);

/// <summary>
/// Vùng đa giác bất kỳ, chứa danh sách các đỉnh tùy ý.
/// Dùng class record vì kích thước danh sách thay đổi và nằm trên heap.
/// </summary>
public sealed record class PolygonRegion(
    IReadOnlyList<Point2d> Points
);

/// <summary>
/// Vùng Caliper mở rộng từ hình chữ nhật xoay, bổ sung số lượng caliper rải đều để dò cạnh.
/// </summary>
public readonly record struct CaliperRegion(
    Point2d Center,
    double Width,
    double Height,
    double AngleDeg,
    int CaliperCount // Số lượng caliper dùng để tìm đường biên/cạnh
);

/// <summary>
/// Mô tả nguồn của một ảnh mẫu (template) theo hai cách loại trừ nhau:
/// Hoặc cắt từ một vùng chữ nhật xoay trên ảnh nguồn, hoặc nạp từ đường dẫn file.
/// </summary>
public sealed record class TemplateImageRef(
    RotatedRectRegion? SourceRegion = null,
    string? FilePath = null
)
{
    // Lưu ý: Logic kiểm tra một trong hai trường không được null 
    // sẽ do code ứng dụng tự kiểm tra khi sử dụng.
}