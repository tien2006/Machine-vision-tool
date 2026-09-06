// ==================== Vai trò chính:                Lưu sơ đồ (FlowGraph) ra file JSON và đọc lại — như tính năng "Save Job / Load Job"
// ==================== Thành phần / Class tiêu biểu: FlowDto, NodeDto, ConnectionDto, FlowSerializer, IFlowRepository, JsonFlowRepository
// ==================== Phụ thuộc vào:                Graph + Registry + System.Text.Json
// ==================== Pattern / Kỹ thuật nổi bật:   DTO Pattern (tách model runtime khỏi model lưu trữ) + Repository Pattern

namespace VisionFlow.Engine.Persistence; // Namespace phục vụ lưu trữ/đọc ghi dữ liệu (Persistence)

// DTO trung gian cho serialization. Dùng object? cho giá trị tham số: khi ghi là giá trị thật,
// khi đọc STJ trả về JsonElement (sẽ được FlowSerializer quy đổi theo ValueType của tham số).

internal sealed class FlowDto // internal: Chỉ dùng nội bộ trong assembly Engine. sealed: Chặn kế thừa
{
    public string Name { get; set; } = "Untitled"; // Tên flow khi lưu, mặc định "Untitled"
    public double PixelSize { get; set; } = 1.0;   // Hệ số mm/pixel khi lưu, mặc định 1.0
    public List<NodeDto> Nodes { get; set; } = new(); // Danh sách node dạng DTO phẳng
    public List<ConnectionDto> Connections { get; set; } = new(); // Danh sách dây nối dạng DTO phẳng
}

internal sealed class NodeDto // Cấu trúc lưu trữ phẳng của một node đơn lẻ
{
    public string Id { get; set; } = string.Empty;      // Khóa ID của node, mặc định chuỗi rỗng
    public string TypeKey { get; set; } = string.Empty; // Từ khóa định danh loại tool (.NET type key) để tái tạo
    public double X { get; set; } // Tọa độ X của node trên UI canvas
    public double Y { get; set; } // Tọa độ Y của node trên UI canvas
    public Dictionary<string, object?> Parameters { get; set; } = new(); // Lưu các tham số (Tên -> Giá trị thô)
}

internal sealed class ConnectionDto // Cấu trúc lưu trữ phẳng của một dây nối
{
    public string SourceNodeId { get; set; } = string.Empty; // ID node nguồn
    public string SourcePort { get; set; } = string.Empty;   // Tên cổng output nguồn
    public string TargetNodeId { get; set; } = string.Empty; // ID node đích
    public string TargetPort { get; set; } = string.Empty;   // Tên cổng input đích
}