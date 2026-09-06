// ==================== Vai trò chính:                Định nghĩa kiểu dữ liệu thông tin bổ sung về 1 vùng ROI (diện tích, độ tương phản...) cho RegionSelectorTool
// ==================== Thành phần / Class tiêu biểu: RegionInfo
// ==================== Phụ thuộc vào:                Không phụ thuộc gì
// ==================== Pattern / Kỹ thuật nổi bật:   POCO/DTO thuần, đúng theo RegionInfo trong tài liệu mục tiêu

namespace VisionFlow.Core.Models;

/// <summary>Thông tin bổ sung về vùng ROI đã chọn - diện tích, độ tương phản và các thuộc tính đặc trưng khác bên trong vùng.</summary>
public sealed class RegionInfo
{
    /// <summary>Diện tích vùng ROI (Width x Height, pixel²).</summary>
    public double Area { get; set; }

    /// <summary>Mức xám trung bình bên trong vùng ROI (0-255).</summary>
    public double MeanIntensity { get; set; }

    /// <summary>Độ tương phản (độ lệch chuẩn mức xám) bên trong vùng ROI - vùng phẳng/đồng đều sẽ có giá trị thấp.</summary>
    public double Contrast { get; set; }

    /// <summary>Chiều rộng vùng ROI (pixel).</summary>
    public double Width { get; set; }

    /// <summary>Chiều cao vùng ROI (pixel).</summary>
    public double Height { get; set; }

    /// <summary>Góc xoay THỰC TẾ đã dùng để cắt (RegionAngle gốc + FixtureAngleOffset nếu có nối Fixture).</summary>
    public double AngleDeg { get; set; }
}