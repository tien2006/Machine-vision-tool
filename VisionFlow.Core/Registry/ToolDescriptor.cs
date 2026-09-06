// ==================== Vai trò chính:                "Danh bạ" trung tâm: đăng ký loại Tool nào tồn tại + biết cách khởi tạo (factory) Tool đó theo tên khóa (string)
// ==================== Thành phần / Class tiêu biểu: IToolRegistry, ToolRegistry, ToolDescriptor
// ==================== Phụ thuộc vào:                Tools + System.Reflection
// ==================== Pattern / Kỹ thuật nổi bật:   Abstract Factory Pattern + Reflection auto-discovery (quét Assembly tìm class có [ToolMetadata])

using System;
using VisionFlow.Core.Tools; // Kéo vào namespace chứa lớp VisionTool để factory có thể tham chiếu và trả về đối tượng

namespace VisionFlow.Core.Registry; // Định vị thuộc phân hệ quản lý và lưu trữ siêu dữ liệu của hệ thống

/// <summary>
/// Bản ghi siêu dữ liệu (Metadata) bất biến mô tả một loại tool đã được đăng ký trong hệ thống[cite: 3].
/// Tách biệt thông tin hiển thị trên UI Palette với logic khởi tạo thông qua một delegate Factory nhằm tối ưu tài nguyên[cite: 3].
/// </summary>
public sealed class ToolDescriptor // Sử dụng sealed để ngăn kế thừa, giúp tối ưu hiệu năng gọi phương thức nhờ triệt tiêu virtual dispatch[cite: 3]
{
    public string Key { get; } // Khóa định danh kỹ thuật duy nhất của loại tool (dùng để phục hồi flow từ file)[cite: 3]
    public string DisplayName { get; } // Tên hiển thị thân thiện của tool trên giao diện người dùng (UI)[cite: 3]
    public string Category { get; } // Nhóm phân loại của tool để sắp xếp vào menu hoặc hộp công cụ (Toolbox)[cite: 3]
    public string Description { get; } // Đoạn văn bản ngắn mô tả chức năng hoặc công dụng cốt lõi của tool[cite: 3]
    public Type ToolType { get; } // Đối tượng Type (Reflection) đại diện cho kiểu dữ liệu thực tế của lớp tool[cite: 3]
    public Func<VisionTool> Factory { get; } // Hàm delegate không tham số, mỗi lần gọi sẽ sinh ra một instance tool hoàn toàn mới độc lập trạng thái[cite: 3]

    public ToolDescriptor(
        string key,
        string displayName,
        string category,
        string description,
        Type toolType,
        Func<VisionTool> factory)
    {
        Key = key; // Thiết lập giá trị duy nhất cho khóa loại tool[cite: 3]
        DisplayName = displayName; // Gán tên hiển thị của cấu trúc node[cite: 3]
        Category = category; // Phân nhóm cụ thể cho tool[cite: 3]
        Description = description; // Lưu trữ chuỗi thông tin mô tả chi tiết[cite: 3]
        ToolType = toolType; // Gán đối tượng định danh kiểu dữ liệu[cite: 3]
        Factory = factory; // Gán hàm khởi tạo nhanh để tạo thực thể riêng biệt khi được yêu cầu[cite: 3]
    }
}