// ==================== Vai trò chính:                Định nghĩa các kiểu dữ liệu hình học & kết quả thuật toán thuần túy (POCO/DTO)
// ==================== Thành phần / Class tiêu biểu: Point2d, Circle, LineSegment, RectRegion, RotatedRectRegion, CircleRegion,
                                                      // CaliperRegion, Judge, VisionResult, CircleResult, LineResult, AlignResult, VisionBlob
// ==================== Phụ thuộc vào:                Không phụ thuộc gì
// ==================== Pattern / Kỹ thuật nổi bật:   record struct (value type, immutable, hiệu năng cao, không rác GC)

using System;
using System.Collections.Generic;

namespace VisionFlow.Core.Models;

/// <summary>
/// Mô hình hóa một vùng pixel liền nhau (Connected Component / Blob) 
/// sau khi tách đối tượng khỏi nền ảnh, kèm theo các đặc trưng hình học đi kèm.
/// </summary>
public sealed class VisionBlob
{
    /// <summary>
    /// Định danh duy nhất (ID) của Blob trong danh sách kết quả.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Diện tích của Blob (tổng số lượng pixel thuộc về blob hoặc vùng diện tích thực).
    /// </summary>
    public double Area { get; set; }

    /// <summary>
    /// Chu vi của Blob (độ dài đường biên bao quanh).
    /// </summary>
    public double Perimeter { get; set; }

    /// <summary>
    /// Tọa độ tâm khối lượng (Center of Mass / Centroid) của Blob.
    /// </summary>
    public Point2d CenterOfMass { get; set; }

    /// <summary>
    /// Hình chữ nhật thẳng đứng nhỏ nhất bao quanh toàn bộ Blob (Bounding Box).
    /// </summary>
    public RectRegion BoundingBox { get; set; }

    /// <summary>
    /// Góc của trục chính ellipse xấp xỉ Blob, thể hiện hướng quay của đối tượng (đơn vị: Độ).
    /// </summary>
    public double Orientation { get; set; }

    /// <summary>
    /// Chiều dài trục lớn của ellipse xấp xỉ Blob.
    /// </summary>
    public double MajorAxisLength { get; set; }

    /// <summary>
    /// Chiều dài trục nhỏ của ellipse xấp xỉ Blob.
    /// </summary>
    public double MinorAxisLength { get; set; }

    /// <summary>
    /// Tỷ lệ biên dạng (Aspect Ratio), tính bằng tỉ lệ giữa trục lớn và trục nhỏ.
    /// </summary>
    public double AspectRatio { get; set; }

    /// <summary>
    /// Độ tròn (Roundness) của Blob. Giá trị nằm trong khoảng (0, 1], bằng 1 nếu là hình tròn hoàn hảo.
    /// </summary>
    public double Roundness { get; set; }

    /// <summary>
    /// Độ đặc/độ bền vững (Solidity). Là tỉ lệ giữa diện tích thực của Blob trên diện tích của đa giác lồi (Convex Hull) bao quanh nó.
    /// </summary>
    public double Solidity { get; set; }

    /// <summary>
    /// Danh sách các điểm tọa độ tạo nên đường biên ngoài (Contour) của Blob.
    /// Được khởi tạo mặc định bằng mảng rỗng để tránh lỗi NullReferenceException khi duyệt dữ liệu.
    /// </summary>
    public IReadOnlyList<Point2d> Contour { get; set; } = Array.Empty<Point2d>();
}