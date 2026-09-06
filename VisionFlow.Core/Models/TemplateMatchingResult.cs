// ==================== Vai trò chính:                Định nghĩa kiểu dữ liệu kết quả DÙNG CHUNG cho các Tool multi-instance template matching
// ==================== Thành phần / Class tiêu biểu: TemplateMatchInstance, TemplateMatchingNCCResult
// ==================== Phụ thuộc vào:                Core.Models (Point2d)
// ==================== Pattern / Kỹ thuật nổi bật:   record struct (value type) + POCO/DTO, tái sử dụng lại cho TemplateMatchingNCC/ContourTemplateMatching/ORBTemplateMatching

using System;
using System.Collections.Generic;

namespace VisionFlow.Core.Models;

/// <summary>
/// Mô tả MỘT kết quả khớp mẫu (1 vật thể tìm thấy) trong bài toán multi-instance template matching.
/// Dùng chung cho TemplateMatchingNCC, ContourTemplateMatching, ORBTemplateMatching (đúng theo tài liệu mục tiêu,
/// cả 3 tool này đều xuất ra "TemplateMatchingNCCOutputModel" có cùng 1 cấu trúc dữ liệu).
/// </summary>
public readonly record struct TemplateMatchInstance(
    Point2d LeftTop,      // Góc trên-trái của khung template sau khi đã xoay/dịch tới đúng vị trí tìm thấy
    Point2d RightTop,     // Góc trên-phải
    Point2d RightBottom,  // Góc dưới-phải
    Point2d LeftBottom,   // Góc dưới-trái
    Point2d Center,       // Tâm hình học của khung (trung bình 4 góc) - toạ độ hay dùng nhất để truyền cho robot gắp
    double MatchScore,    // Điểm số khớp mẫu (0.0 - 1.0), càng gần 1 càng giống mẫu chuẩn
    double MatchedAngle,  // Góc xoay (độ) của vật thể so với mẫu gốc tại vị trí khớp này
    int Index             // Số thứ tự của kết quả trong danh sách (0-based), tiện để vẽ số/debug
);

/// <summary>
/// Kết quả tổng hợp trả về từ các Tool multi-instance template matching.
/// Theo đúng pattern VisionResult (Judge) như các kết quả khác trong hệ thống (BlobAnalysisResult, CircleResult...).
/// </summary>
public sealed class TemplateMatchingNCCResult : VisionResult
{
    /// <summary>Danh sách toàn bộ các vị trí khớp mẫu tìm được, đã lọc theo Score và đã áp dụng NMS theo MaxOverlap.</summary>
    public IReadOnlyList<TemplateMatchInstance> Matches { get; set; } = Array.Empty<TemplateMatchInstance>();

    /// <summary>Cờ thành công: true nếu tìm được ít nhất 1 kết quả thoả ngưỡng Score.</summary>
    public bool Success { get; set; }

    /// <summary>Thời gian thực thi toàn bộ thuật toán (bao gồm cả quét nhiều góc xoay), tính bằng mili-giây.</summary>
    public double ExecutionTimeMs { get; set; }
}