using VisionFlow.Core.Tools; // Nạp namespace chứa interface IToolContext

namespace VisionFlow.Engine.Execution; // Khai báo namespace theo cú pháp file-scoped

/// <summary>Cài đặt mặc định của <see cref="IToolContext"/> do engine cấp cho tool.</summary>
public sealed class ToolContext : IToolContext // sealed: Chặn không cho các lớp khác kế thừa
{
    // Trường lưu trữ Action logger truyền từ bên ngoài vào (có thể null nếu không cần log)
    private readonly Action<string>? _logger;

    // Constructor khởi tạo ToolContext với các giá trị mặc định nếu không được truyền vào
    public ToolContext(double pixelSize = 1.0, CancellationToken cancellationToken = default, Action<string>? logger = null)
    {
        PixelSize = pixelSize; // Tỉ lệ quy đổi từ pixel sang đơn vị thực tế
        CancellationToken = cancellationToken; // Token dùng để hủy tác vụ giữa chừng
        _logger = logger; // Gán logger
    }

    // Property chỉ-đọc (get) nhận CancellationToken
    public CancellationToken CancellationToken { get; }

    // Property chỉ-đọc (get) nhận tỉ lệ PixelSize
    public double PixelSize { get; }

    // Phương thức ghi log: Sử dụng toán tử ?. (null-conditional) để tránh NullReferenceException nếu _logger null
    public void Log(string message) => _logger?.Invoke(message);
}