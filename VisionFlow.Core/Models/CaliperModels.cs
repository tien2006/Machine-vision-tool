// ==================== Vai trò chính:                Định nghĩa các kiểu dữ liệu kết quả riêng cho CaliperTool (điểm cạnh, cặp cạnh, phép đo)
// ==================== Thành phần / Class tiêu biểu: CaliperEdge, CaliperEdgePair, CaliperMeasurement, CaliperToolResult
// ==================== Phụ thuộc vào:                VisionResult, Judge, Point2d (đã có sẵn trong Core.Models)
// ==================== Pattern / Kỹ thuật nổi bật:   POCO/DTO, kế thừa VisionResult để tương thích CompareTool/LogicGate

using System.Collections.Generic;

namespace VisionFlow.Core.Models;

/// <summary>
/// Một điểm cạnh (edge) đơn lẻ do CaliperTool tìm được trên 1 thước đo (caliper).
/// </summary>
public sealed class CaliperEdge
{
    /// <summary>Chỉ số (index) của caliper đã tìm ra cạnh này, tính từ 0.</summary>
    public int CaliperIndex { get; set; }

    /// <summary>Tọa độ sub-pixel của điểm cạnh trên ảnh.</summary>
    public Point2d Position { get; set; }

    /// <summary>Độ lớn Gradient (độ dốc mức xám) tại điểm cạnh - càng lớn càng "sắc nét".</summary>
    public double GradientStrength { get; set; }

    /// <summary>Cực tính chuyển màu: "DarkToLight" (tối sang sáng) hoặc "LightToDark" (sáng sang tối).</summary>
    public string Polarity { get; set; } = "DarkToLight";

    /// <summary>Điểm số chất lượng tổng hợp (0.0 -> 1.0): kết hợp độ mạnh Gradient + độ gần vị trí kỳ vọng (nếu bật).</summary>
    public double Score { get; set; }

    /// <summary>Vị trí tương đối dọc theo caliper (0.0 = đầu, 1.0 = cuối) - dùng cho EnablePositionScoring.</summary>
    public double RelativePosition { get; set; }
}

/// <summary>
/// Một cặp cạnh (sáng-tối-sáng hoặc tối-sáng-tối) trên cùng 1 caliper - chỉ có dữ liệu khi Mode = EdgePair.
/// </summary>
public sealed class CaliperEdgePair
{
    public int CaliperIndex { get; set; }

    /// <summary>Cạnh đầu tiên gặp trên đường quét (theo StartEdgeThreshold/StartEdgePolarity).</summary>
    public CaliperEdge StartEdge { get; set; } = null!;

    /// <summary>Cạnh thứ hai, tìm SAU StartEdge (theo EndEdgeThreshold/EndEdgePolarity).</summary>
    public CaliperEdge EndEdge { get; set; } = null!;

    /// <summary>Khoảng cách pixel giữa StartEdge và EndEdge - chính là độ rộng (Width) của vật đo được.</summary>
    public double WidthPx { get; set; }
}

/// <summary>
/// Kết quả đo lường đã quy đổi theo từng caliper (Width/Distance/Position), có áp dụng PixelToWorldScale.
/// </summary>
public sealed class CaliperMeasurement
{
    public int CaliperIndex { get; set; }

    /// <summary>Loại phép đo: "Width" | "Distance" | "Position".</summary>
    public string Type { get; set; } = "Width";

    /// <summary>Giá trị đo được theo đơn vị pixel.</summary>
    public double ValuePx { get; set; }

    /// <summary>Giá trị đo được đã quy đổi ra đơn vị thực (mm/inch) qua PixelToWorldScale.</summary>
    public double ValueWorld { get; set; }
}

/// <summary>
/// Kết quả tổng hợp trả về từ CaliperTool - kế thừa VisionResult để nối trực tiếp
/// vào CompareTool/LogicGate như các tool đo lường khác (CircleResult, LineResult).
/// </summary>
public sealed class CaliperToolResult : VisionResult
{
    /// <summary>Tổng số cạnh (edge) hợp lệ tìm được trên toàn bộ các caliper.</summary>
    public int EdgeCount { get; set; }

    /// <summary>Cạnh có điểm số (Score) cao nhất - null nếu không tìm thấy cạnh nào.</summary>
    public CaliperEdge? BestEdge { get; set; }

    /// <summary>Giá trị trung bình các phép đo (đơn vị thực) - tiện so sánh nhanh bằng CompareTool.</summary>
    public double MeanMeasurementWorld { get; set; }
}