// ==================== Vai trò chính:                Định nghĩa kiểu dữ liệu mô tả 1 vùng ROI loại trừ/bao gồm (Exclusion/Inclusion Region) cho BlobAnalysisTool
// ==================== Thành phần / Class tiêu biểu: ExclusionPolygonInfo
// ==================== Phụ thuộc vào:                Core.Models (Point2d)
// ==================== Pattern / Kỹ thuật nổi bật:   POCO/DTO, đúng theo ExclusionPolygonInfo trong tài liệu mục tiêu

using System;
using System.Collections.Generic;

namespace VisionFlow.Core.Models;

/// <summary>
/// Thông tin 1 vùng ROI riêng biệt (Rectangle/Circle/Polygon) dùng để giới hạn (Inclusion) hoặc loại trừ
/// (Exclusion) vùng tìm Blob trong BlobAnalysisTool. Toạ độ Points luôn tính theo hệ pixel gốc của ảnh đầu vào.
/// </summary>
public sealed class ExclusionPolygonInfo
{
    /// <summary>Định danh duy nhất của vùng (khớp với Id trong JSON cấu hình ExclusionRegions).</summary>
    public string Id { get; set; } = "";

    /// <summary>Tên hiển thị do người dùng đặt (có thể rỗng).</summary>
    public string Name { get; set; } = "";

    /// <summary>true = vùng loại trừ (Exclusion, bỏ qua Blob bên trong); false = vùng bao gồm (Inclusion, chỉ tìm Blob bên trong).</summary>
    public bool IsExclusion { get; set; }

    /// <summary>Danh sách điểm tạo thành đa giác của vùng (Rectangle/Circle đã được xấp xỉ thành đa giác nhiều điểm).</summary>
    public IReadOnlyList<Point2d> Points { get; set; } = Array.Empty<Point2d>();
}