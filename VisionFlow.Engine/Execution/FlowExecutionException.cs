namespace VisionFlow.Engine.Execution; // Khai báo namespace file-scoped

/// <summary>Lỗi cấp đồ thị (vd phát hiện chu trình) khiến không thể thực thi flow.</summary>
// Exception tùy chỉnh dùng riêng cho các lỗi liên quan đến đồ thị/luồng thực thi
public sealed class FlowExecutionException : Exception
{
    // Constructor truyền message lỗi lên cho class cha (Exception) xử lý
    public FlowExecutionException(string message) : base(message) { }
}