// ==================== Vai trò chính:                Hợp đồng chuẩn của một Tool + lớp nền (base class) dùng chung cho mọi thuật toán
// ==================== Thành phần / Class tiêu biểu: ITool, IToolContext, VisionTool (abstract), ToolParameter<T>, ToolMetadataAttribute, ToolState, ToolExecutionException
// ==================== Phụ thuộc vào:                Ports
// ==================== Pattern / Kỹ thuật nổi bật:   Strategy Pattern + Template Method Pattern

using System;

namespace VisionFlow.Core.Tools;

/// <summary>
/// Ngoại lệ chuyên biệt (Custom Domain Exception) ném ra khi một Tool xử lý ảnh gặp lỗi thuật toán hoặc dữ liệu[cite: 1].
/// </summary>
public sealed class ToolExecutionException : Exception // Kế thừa Exception để sử dụng được với từ khóa 'throw'[cite: 1]
{
    public ToolExecutionException(string message) : base(message) // Constructor ném kèm chuỗi thông báo lỗi[cite: 1]
    {
    }

    public ToolExecutionException(string message, Exception innerException) : base(message, innerException) // Constructor bọc lỗi gốc hệ thống[cite: 1]
    {
    }
}