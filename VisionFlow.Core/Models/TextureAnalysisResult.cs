// ==================== Vai trò chính:                Định nghĩa kiểu dữ liệu kết quả cho TextureAnalysisTool (đặc trưng kết cấu bề mặt tính từ GLCM)
// ==================== Thành phần / Class tiêu biểu: TextureAnalysisResult
// ==================== Phụ thuộc vào:                Core.Models (VisionResult)
// ==================== Pattern / Kỹ thuật nổi bật:   POCO/DTO kế thừa VisionResult, đúng theo TextureAnalysisOutputModel trong tài liệu mục tiêu

namespace VisionFlow.Core.Models;

/// <summary>
/// Kết quả phân tích kết cấu bề mặt (texture) bằng GLCM (Gray-Level Co-occurrence Matrix).
/// 5 đặc trưng texture kinh điển + thống kê cơ bản + thông tin vùng ROI đã phân tích.
/// </summary>
public sealed class TextureAnalysisResult : VisionResult
{
    /// <summary>Độ tương phản - biến thiên cường độ giữa các pixel lân cận. Bề mặt xước/sần sùi → giá trị cao.</summary>
    public double Contrast { get; set; }

    /// <summary>Tương quan (-1..1) - mức độ "có tổ chức" của texture. Hoa văn lặp lại → gần 1; ngẫu nhiên → gần 0.</summary>
    public double Correlation { get; set; }

    /// <summary>Năng lượng / Angular Second Moment (0..1) - độ đồng nhất. Bề mặt mịn đồng đều → gần 1.</summary>
    public double Energy { get; set; }

    /// <summary>Tính đồng nhất / Inverse Difference Moment (0..1) - mức gần gũi của các giá trị xám liền kề.</summary>
    public double Homogeneity { get; set; }

    /// <summary>Entropy - độ hỗn loạn/thông tin. Bề mặt phức tạp, nhiều chi tiết → giá trị cao.</summary>
    public double Entropy { get; set; }

    /// <summary>Mức xám trung bình của vùng ROI (0-255, tính trên ảnh gốc chưa lượng tử hoá).</summary>
    public double MeanGrayLevel { get; set; }

    /// <summary>Độ lệch chuẩn mức xám của vùng ROI.</summary>
    public double StandardDeviation { get; set; }

    /// <summary>Toạ độ X góc trên-trái của vùng ROI đã phân tích (pixel).</summary>
    public double RegionX { get; set; }

    /// <summary>Toạ độ Y góc trên-trái của vùng ROI đã phân tích (pixel).</summary>
    public double RegionY { get; set; }

    /// <summary>Chiều rộng vùng ROI (pixel).</summary>
    public double RegionWidth { get; set; }

    /// <summary>Chiều cao vùng ROI (pixel).</summary>
    public double RegionHeight { get; set; }

    /// <summary>Cờ thành công: true nếu ROI hợp lệ và GLCM tính toán được (đủ pixel, nằm trong ảnh).</summary>
    public bool Success { get; set; }

    /// <summary>Thời gian thực thi toàn bộ thuật toán, tính bằng mili-giây.</summary>
    public double ExecutionTimeMs { get; set; }
}