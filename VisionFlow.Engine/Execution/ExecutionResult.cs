using VisionFlow.Core.Tools; // Nạp namespace chứa enum ToolState

namespace VisionFlow.Engine.Execution; // Khai báo namespace file-scoped

/// <summary>Kết quả thực thi một node (để UI hiển thị timing/state/lỗi).</summary>
// Record bất biến chứa thông tin chi tiết về việc chạy từng node
public sealed record NodeExecutionResult(
    string NodeId,       // Mã định danh của Node
    string DisplayName,  // Tên hiển thị của Tool/Node
    ToolState State,     // Trạng thái của Tool (Completed, Failed, Skipped, ...)
    long ElapsedMs,      // Thời gian thực thi tính bằng mili-giây
    string? Error);      // Thông báo lỗi (null nếu không có lỗi)

/// <summary>Kết quả tổng của một lần chạy flow.</summary>
public sealed class ExecutionResult
{
    // Constructor nhận danh sách kết quả từng node và tổng thời gian chạy
    public ExecutionResult(IReadOnlyList<NodeExecutionResult> nodes, long totalMs)
    {
        Nodes = nodes;
        TotalMs = totalMs;
    }

    // Danh sách chỉ-đọc chứa kết quả thực thi của từng node
    public IReadOnlyList<NodeExecutionResult> Nodes { get; }

    // Tổng thời gian thực thi của cả Flow (ms)
    public long TotalMs { get; }

    /// <summary>Toàn bộ node chạy không lỗi (Skipped không tính là lỗi).</summary>
    // Trả về true nếu không có node nào ở trạng thái Failed
    public bool Success => Nodes.All(n => n.State != ToolState.Failed);
}