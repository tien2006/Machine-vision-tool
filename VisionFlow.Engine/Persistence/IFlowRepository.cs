// ==================== Vai trò chính:                Lưu sơ đồ (FlowGraph) ra file JSON và đọc lại — như tính năng "Save Job / Load Job"
// ==================== Thành phần / Class tiêu biểu: FlowDto, NodeDto, ConnectionDto, FlowSerializer, IFlowRepository, JsonFlowRepository
// ==================== Phụ thuộc vào:                Graph + Registry + System.Text.Json
// ==================== Pattern / Kỹ thuật nổi bật:   DTO Pattern (tách model runtime khỏi model lưu trữ) + Repository Pattern

using VisionFlow.Core.Registry; // Kéo interface IToolRegistry vào dùng cho chức năng Load
using VisionFlow.Engine.Graph; // Quản lý cấu trúc FlowGraph đầu vào/đầu ra

namespace VisionFlow.Engine.Persistence;

/// <summary>Repository lưu/đọc flow ra đĩa.</summary>
public interface IFlowRepository // Định nghĩa hợp đồng trừu tượng (Contract) cho tầng nghiệp vụ
{
    void Save(FlowGraph graph, string path); // Phương thức yêu cầu ghi đồ thị ra đường dẫn file chỉ định
    FlowGraph Load(string path);             // Phương thức yêu cầu đọc file và dựng lại đồ thị
}

/// <summary>Cài đặt repository dùng JSON.</summary>
public sealed class JsonFlowRepository : IFlowRepository // sealed: Cài đặt cụ thể cuối cùng, chặn kế thừa
{
    private readonly IToolRegistry _registry; // Lưu trữ registry để phục vụ quá trình Load tái tạo tool

    public JsonFlowRepository(IToolRegistry registry)
    {
        _registry = registry; // Dependency Injection: Nhận registry từ ngoài đổ vào constructor
    }

    // Ghi file: Serialize đồ thị thành chuỗi JSON và viết thẳng xuống đĩa thông qua biểu thức expression-bodied
    public void Save(FlowGraph graph, string path)
        => File.WriteAllText(path, FlowSerializer.Serialize(graph));

    // Đọc file: Đọc toàn bộ chuỗi chữ trong file và gọi Deserialize kết hợp registry để dựng lại graph sống
    public FlowGraph Load(string path)
        => FlowSerializer.Deserialize(File.ReadAllText(path), _registry);
}