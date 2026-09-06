// ==================== Vai trò chính:                Mô hình dữ liệu đồ thị (node + cạnh) của một sơ đồ xử lý hoàn chỉnh, độc lập UI
// ==================== Thành phần / Class tiêu biểu: FlowGraph, FlowNode, FlowConnection
// ==================== Phụ thuộc vào:                Core.Tools
// ==================== Pattern / Kỹ thuật nổi bật:   Graph Data Structure (Directed Graph), ràng buộc 1-input-1-dây, hỗ trợ fan-out ở output

using VisionFlow.Core.Tools; // Kéo namespace chứa VisionTool để dùng tên ngắn gọn

namespace VisionFlow.Engine.Graph; // File-scoped namespace (C# 10+), giảm 1 cấp thụt lề cho toàn file

/// <summary>
/// Một node trong đồ thị flow: bọc một <see cref="VisionTool"/> kèm vị trí trên canvas.
/// Đây là model dữ liệu thuần — không phụ thuộc UI.
/// </summary>
public sealed class FlowNode // sealed: chặn kế thừa để tối ưu hiệu năng và rõ ràng thiết kế
{
    public FlowNode(VisionTool tool)
    {
        // Toán tử ?? throw: Nếu tool null thì ném lỗi ngay. nameof(tool) giúp tự động cập nhật tên biến nếu đổi tên
        Tool = tool ?? throw new ArgumentNullException(nameof(tool));
    }

    public string Id => Tool.Id; // Expression-bodied property: Lấy Id của node chính là Id của tool để đồng bộ dữ liệu

    public VisionTool Tool { get; } // Auto-property chỉ đọc: Chỉ gán 1 lần trong constructor, bất biến sau đó

    public double X { get; set; } // Tọa độ X trên canvas, có cả get/set để thay đổi khi người dùng kéo thả
    public double Y { get; set; } // Tọa độ Y trên canvas, có cả get/set để thay đổi khi người dùng kéo thả
}