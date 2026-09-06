// ==================== Vai trò chính:                Mô hình dữ liệu đồ thị (node + cạnh) của một sơ đồ xử lý hoàn chỉnh, độc lập UI
// ==================== Thành phần / Class tiêu biểu: FlowGraph, FlowNode, FlowConnection
// ==================== Phụ thuộc vào:                Core.Tools
// ==================== Pattern / Kỹ thuật nổi bật:   Graph Data Structure (Directed Graph), ràng buộc 1-input-1-dây, hỗ trợ fan-out ở output

namespace VisionFlow.Engine.Graph; // File-scoped namespace: Xác định namespace cho toàn file kết thúc bằng dấu chấm phẩy

/// <summary>
/// Một cạnh có hướng: output port (<see cref="SourceNodeId"/>.<see cref="SourcePort"/>)
/// → input port (<see cref="TargetNodeId"/>.<see cref="TargetPort"/>).
/// </summary>
public sealed class FlowConnection // sealed: Lớp lá, không cho phép kế thừa để đảm bảo tính bất biến
{
    public FlowConnection(string sourceNodeId, string sourcePort, string targetNodeId, string targetPort)
    {
        SourceNodeId = sourceNodeId; // Gán ID node nguồn
        SourcePort = sourcePort;     // Gán tên cổng output nguồn
        TargetNodeId = targetNodeId; // Gán ID node đích
        TargetPort = targetPort;     // Gán tên cổng input đích
    }

    // Các thuộc tính get-only (Auto-property chỉ đọc) -> Đối tượng bất biến (Immutable) sau khi tạo
    public string SourceNodeId { get; } // ID của node nguồn chứa cổng output
    public string SourcePort { get; }   // Tên cổng output nguồn
    public string TargetNodeId { get; } // ID của node đích chứa cổng input
    public string TargetPort { get; }   // Tên cổng input đích
}