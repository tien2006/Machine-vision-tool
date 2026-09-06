// ==================== Vai trò chính:                Lưu sơ đồ (FlowGraph) ra file JSON và đọc lại — như tính năng "Save Job / Load Job"
// ==================== Thành phần / Class tiêu biểu: FlowDto, NodeDto, ConnectionDto, FlowSerializer, IFlowRepository, JsonFlowRepository
// ==================== Phụ thuộc vào:                Graph + Registry + System.Text.Json
// ==================== Pattern / Kỹ thuật nổi bật:   DTO Pattern (tách model runtime khỏi model lưu trữ) + Repository Pattern

using System.Text.Json; // Thư viện xử lý JSON tích hợp của .NET
using System.Text.Json.Serialization; // Các cấu hình chuyển đổi nâng cao cho JSON
using VisionFlow.Core.Registry; // Interface IToolRegistry phục vụ tái tạo tool
using VisionFlow.Engine.Graph; // Quản lý mô hình FlowGraph runtime

namespace VisionFlow.Engine.Persistence;

/// <summary>
/// Lưu/đọc <see cref="FlowGraph"/> ra JSON. Dùng <c>typeKey</c> (chuỗi) làm khoá kiểu thay cho
/// TypeNameHandling — an sau và bền theo version. Khi nạp, tạo tool qua <see cref="IToolRegistry"/>.
/// </summary>
public static class FlowSerializer // Lớp static tiện ích, không lưu trạng thái đối tượng
{
    // Cấu hình dùng chung cho cả đọc và ghi JSON, static readonly giúp tối ưu bộ nhớ
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true, // Tự động thụt lề JSON cho đẹp và dễ đọc bằng mắt thường
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, // Đổi tên thuộc tính sang kiểu chữ camelCase
        Converters = { new JsonStringEnumConverter() } // Lưu/đọc Enum dạng chuỗi chữ (vd: "Gray") thay vì số
    };

    public static string Serialize(FlowGraph graph)
    {
        // BƯỚC 1: Khởi tạo và map các thông tin cơ bản từ FlowGraph sang FlowDto
        var dto = new FlowDto { Name = graph.Name, PixelSize = graph.PixelSize };

        // BƯỚC 2: Duyệt và ánh xạ toàn bộ tập node sống sang cấu trúc NodeDto phẳng
        foreach (var node in graph.Nodes)
        {
            var nodeDto = new NodeDto
            {
                Id = node.Id,
                TypeKey = node.Tool.TypeKey, // Lưu typeKey dạng chuỗi để độc lập phiên bản lớp .NET
                X = node.X,
                Y = node.Y
            };
            // Sao chép toàn bộ tham số hiện tại của Tool vào Dictionary của DTO
            foreach (var p in node.Tool.Parameters)
                nodeDto.Parameters[p.Name] = p.Value;

            dto.Nodes.Add(nodeDto); // Đưa Node DTO vào danh sách tổng
        }

        // BƯỚC 3: Ánh xạ toàn bộ tập cạnh (kết nối) sang cấu trúc ConnectionDto phẳng
        foreach (var c in graph.Connections)
        {
            dto.Connections.Add(new ConnectionDto
            {
                SourceNodeId = c.SourceNodeId,
                SourcePort = c.SourcePort,
                TargetNodeId = c.TargetNodeId,
                TargetPort = c.TargetPort
            });
        }

        // BƯỚC 4: Thực hiện chuyển đổi cây DTO thành chuỗi chuỗi JSON theo cấu hình Options
        return JsonSerializer.Serialize(dto, Options);
    }

    public static FlowGraph Deserialize(string json, IToolRegistry registry)
    {
        // Giải mã JSON -> FlowDto. Toán tử ?? throw ném lỗi ngay nếu chuỗi rỗng/sai định dạng
        var dto = JsonSerializer.Deserialize<FlowDto>(json, Options)
                  ?? throw new InvalidOperationException("Nội dung flow JSON rỗng hoặc không hợp lệ.");

        // Tạo graph runtime mới và khôi phục các thông số cấu hình chung
        var graph = new FlowGraph { Name = dto.Name, PixelSize = dto.PixelSize };

        // Duyệt danh sách NodeDto để tái tạo lại các đối tượng Tool sống
        foreach (var n in dto.Nodes)
        {
            var tool = registry.Create(n.TypeKey); // Yêu cầu registry dựng đúng thực thể tool qua typeKey chuỗi
            tool.Id = n.Id; // Khôi phục lại đúng ID gốc của tool

            // Khôi phục giá trị các tham số của tool thông qua cú pháp Tuple Deconstruction (name, raw)
            foreach (var (name, raw) in n.Parameters)
            {
                var param = tool.FindParameter(name); // Tìm tham số theo tên
                if (param is null) continue; // Nếu tham số không tồn tại (do code cũ/file hỏng), bỏ qua an toàn
                param.Value = ExtractJson(raw, param.ValueType); // Quy đổi JsonElement về kiểu dữ liệu thật
            }

            graph.AddNode(tool, n.X, n.Y); // Đưa node đã khôi phục vào đồ thị kèm tọa độ canvas
        }

        // Duyệt danh sách ConnectionDto để khôi phục các dây nối
        foreach (var c in dto.Connections)
        {
            // Kiểm tra an toàn: Bỏ qua nếu dây nối trỏ vào node không tồn tại trong đồ thị hiện tại
            if (graph.GetNode(c.SourceNodeId) is null || graph.GetNode(c.TargetNodeId) is null) continue;
            // Thêm trực tiếp kết nối hợp lệ vào đồ thị
            graph.AddConnection(new FlowConnection(c.SourceNodeId, c.SourcePort, c.TargetNodeId, c.TargetPort));
        }

        return graph; // Trả về đồ thị hoàn chỉnh đã dựng xong
    }

    /// <summary>Quy đổi giá trị thô (JsonElement sau deserialize) về kiểu nguyên thuỷ phù hợp với tham số.</summary>
    private static object? ExtractJson(object? raw, Type targetType)
    {
        // Pattern matching: Nếu dữ liệu thô không phải là cấu trúc JsonElement thì trả về luôn (đã chuẩn)
        if (raw is not JsonElement el) return raw;

        // Bóc lớp Nullable<T> nếu có để lấy kiểu cốt lõi bên dưới (ví dụ int? -> int)
        var t = Nullable.GetUnderlyingType(targetType) ?? targetType;

        // Rẽ nhánh xử lý dựa theo kiểu JSON gốc lưu trong file
        switch (el.ValueKind)
        {
            case JsonValueKind.String:
                return el.GetString(); // Trả về kiểu chuỗi (String)
            case JsonValueKind.Number:
                // Nếu đích đến là Enum hoặc kiểu int nguyên bản thì đọc dạng Int32
                if (t.IsEnum || t == typeof(int)) return el.GetInt32();
                if (t == typeof(long)) return el.GetInt64();  // Đọc số nguyên lớn
                if (t == typeof(float)) return el.GetSingle(); // Đọc số thực đơn float
                return el.GetDouble(); // Mặc định chuyển thành số thực đôi double
            case JsonValueKind.True:
            case JsonValueKind.False:
                return el.GetBoolean(); // Trả về kiểu luận lý bool
            case JsonValueKind.Null:
                return null; // Trả về giá trị null
            case JsonValueKind.Object:
            case JsonValueKind.Array:
                // Xử lý kiểu dữ liệu phức (Vùng chọn Region, Hình mẫu TemplateImageRef...) bằng cách đệ quy deserialize
                return el.Deserialize(t, Options);
            default:
                return el.GetRawText(); // Phương án phòng hờ: Trả về văn bản thô dạng chuỗi JSON
        }
    }
}