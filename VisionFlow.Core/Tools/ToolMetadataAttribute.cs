// ==================== Vai trò chính:                Hợp đồng chuẩn của một Tool + lớp nền (base class) dùng chung cho mọi thuật toán
// ==================== Thành phần / Class tiêu biểu: ITool, IToolContext, VisionTool (abstract), ToolParameter<T>, ToolMetadataAttribute, ToolState, ToolExecutionException
// ==================== Phụ thuộc vào:                Ports
// ==================== Pattern / Kỹ thuật nổi bật:   Strategy Pattern + Template Method Pattern

using System;

namespace VisionFlow.Core.Tools;

/// <summary>
/// Custom Attribute định nghĩa nhãn Metadata gắn lên các lớp Tool xử lý ảnh[cite: 1].
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)] // Chỉ cho gắn lên Class, không cho kế thừa tự động[cite: 1]
public sealed class ToolMetadataAttribute : Attribute // 'sealed' ngăn chặn mở rộng phân cấp attribute[cite: 1]
{
    public string Key { get; } // Khóa định danh chuỗi tĩnh duy nhất cho Tool (dùng cho JSON Serializer/Factory)[cite: 1]
    public string DisplayName { get; set; } = string.Empty; // Tên hiển thị trên bảng Palette UI[cite: 1]
    public string Category { get; set; } = "General";        // Danh mục gom nhóm tool trên Palette UI[cite: 1]
    public string Description { get; set; } = string.Empty; // Chuỗi mô tả ngắn gọn tính năng của Tool[cite: 1]

    public ToolMetadataAttribute(string key) // Constructor tiếp nhận Positional Argument (hằng số compile-time)[cite: 1]
    {
        Key = key; // Lưu trữ khóa định danh tĩnh[cite: 1]
    }
}