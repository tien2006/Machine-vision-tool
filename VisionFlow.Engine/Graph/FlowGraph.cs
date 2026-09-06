// ==================== Vai trò chính:                Mô hình dữ liệu đồ thị (node + cạnh) của một sơ đồ xử lý hoàn chỉnh, độc lập UI
// ==================== Thành phần / Class tiêu biểu: FlowGraph, FlowNode, FlowConnection
// ==================== Phụ thuộc vào:                Core.Tools
// ==================== Pattern / Kỹ thuật nổi bật:   Graph Data Structure (Directed Graph), ràng buộc 1-input-1-dây, hỗ trợ fan-out ở output

using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools; // Kéo namespace chứa VisionTool vào sử dụng

namespace VisionFlow.Engine.Graph; // Định nghĩa namespace quản lý cấu trúc đồ thị

/// <summary>
/// Model dữ liệu thuần của một flow: tập node + tập cạnh. Không phụ thuộc UI/Rx.
/// UI (NodeNetwork) đồng bộ 2 chiều với graph này qua adapter; <c>FlowExecutor</c> đọc graph để chạy.
/// </summary>
public sealed class FlowGraph // Lớp quản lý đồ thị dòng chảy, không cho phép kế thừa
{
    // Cú pháp new() (Target-typed new từ C# 9): Khởi tạo list nội bộ chỉ đọc về tham chiếu (readonly)
    private readonly List<FlowNode> _nodes = new();
    private readonly List<FlowConnection> _connections = new();

    public string Name { get; set; } = "Untitled"; // Tên của flow, mặc định là "Untitled" khi tạo mới

    public double PixelSize { get; set; } = 1.0; // Hệ số hiệu chuẩn mm/pixel, kiểu double, mặc định là 1.0

    // Phơi danh sách ra ngoài dạng IReadOnlyList để bên ngoài chỉ đọc/duyệt, không tự ý Add/Remove
    public IReadOnlyList<FlowNode> Nodes => _nodes;
    public IReadOnlyList<FlowConnection> Connections => _connections;

    // Overload 1: Tạo mới node từ VisionTool và toạ độ mặc định (x=0, y=0 là optional parameter)
    public FlowNode AddNode(VisionTool tool, double x = 0, double y = 0)
    {
        var node = new FlowNode(tool) { X = x, Y = y }; // Dùng Object Initializer để gán nhanh X, Y
        _nodes.Add(node); // Thêm node vào danh sách quản lý nội bộ
        return node;      // Trả về node vừa tạo để tầng trên sử dụng tiếp
    }

    // Overload 2: Thêm trực tiếp một FlowNode có sẵn (dùng khi nạp flow từ file JSON)
    public void AddNode(FlowNode node) => _nodes.Add(node);

    public void RemoveNode(FlowNode node)
    {
        // Ràng buộc: Xóa node thì phải xóa sạch các cạnh kết nối liên quan (tránh cạnh "treo")
        _connections.RemoveAll(c => c.SourceNodeId == node.Id || c.TargetNodeId == node.Id);
        _nodes.Remove(node); // Sau khi dọn sạch cạnh, tiến hành gỡ bỏ node khỏi danh sách
    }

    // Tra cứu node bằng Id qua LINQ FirstOrDefault. Ký tự ? chỉ định kiểu trả về có thể là null
    public FlowNode? GetNode(string id) => _nodes.FirstOrDefault(n => n.Id == id);

    public FlowConnection Connect(string sourceNodeId, string sourcePort, string targetNodeId, string targetPort)
    {
        // Tra port đích để biết đây là Input thường hay Multi-Input Port - quyết định có áp ràng buộc "1 dây" hay không
        var targetNode = GetNode(targetNodeId);
        var targetInput = targetNode?.Tool.FindInput(targetPort);
        bool isMultiInput = targetInput is IMultiInputPort;

        if (isMultiInput)
        {
            // Multi-Input Port: CHO PHÉP nhiều dây cùng đổ vào - chỉ loại bỏ dây TRÙNG LẶP Y HỆT
            // (cùng Source + SourcePort nối lại vào đúng port đó lần nữa), tránh cộng dồn 2 lần cùng 1 nguồn.
            _connections.RemoveAll(c => c.TargetNodeId == targetNodeId && c.TargetPort == targetPort
                && c.SourceNodeId == sourceNodeId && c.SourcePort == sourcePort);
        }
        else
        {
            // Input thường: GIỮ NGUYÊN ràng buộc cứng cũ - 1 input port chỉ nhận tối đa 1 dây nối đổ vào
            _connections.RemoveAll(c => c.TargetNodeId == targetNodeId && c.TargetPort == targetPort);
        }

        var conn = new FlowConnection(sourceNodeId, sourcePort, targetNodeId, targetPort); // Dựng kết nối mới
        _connections.Add(conn); // Lưu kết nối vào danh sách nội bộ
        return conn;            // Trả về đối tượng kết nối vừa tạo
    }

    // Thêm kết nối dựng sẵn (dùng khi nạp file, không qua kiểm tra ràng buộc vì file đã hợp lệ sẵn)
    public void AddConnection(FlowConnection connection) => _connections.Add(connection);

    // Gỡ bỏ trực tiếp một dây nối cụ thể ra khỏi đồ thị
    public void Disconnect(FlowConnection connection) => _connections.Remove(connection);

    // LINQ Where (Deferred Execution): Tìm các cạnh đi vào một input port cụ thể (tối đa 1 cạnh)
    public IEnumerable<FlowConnection> ConnectionsInto(string nodeId, string inputPort)
        => _connections.Where(c => c.TargetNodeId == nodeId && c.TargetPort == inputPort);

    // LINQ Where: Tìm các cạnh đi ra từ một output port cụ thể (hỗ trợ fan-out - một cổng ra nhiều nơi)
    public IEnumerable<FlowConnection> ConnectionsFrom(string nodeId, string outputPort)
        => _connections.Where(c => c.SourceNodeId == nodeId && c.SourcePort == outputPort);
}