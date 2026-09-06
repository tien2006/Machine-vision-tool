// ==================== Vai trò chính:                "Danh bạ" trung tâm: đăng ký loại Tool nào tồn tại + biết cách khởi tạo (factory) Tool đó theo tên khóa (string)
// ==================== Thành phần / Class tiêu biểu: IToolRegistry, ToolRegistry, ToolDescriptor
// ==================== Phụ thuộc vào:                Tools + System.Reflection
// ==================== Pattern / Kỹ thuật nổi bật:   Abstract Factory Pattern + Reflection auto-discovery (quét Assembly tìm class có [ToolMetadata])

using System;
using System.Collections.Generic; // Cung cấp kiểu tập hợp IReadOnlyCollection[cite: 3]
using System.Reflection; // Hỗ trợ làm việc với kiểu dữ liệu Assembly để quét tự động[cite: 3]
using VisionFlow.Core.Tools; // Kéo vào lớp cơ sở VisionTool và các định nghĩa liên quan[cite: 3]

namespace VisionFlow.Core.Registry; // Định vị thuộc phân hệ quản lý và đăng ký hệ thống[cite: 3]

/// <summary>
/// Giao diện hợp đồng (Interface) cho sổ đăng ký tool theo mẫu Abstract Factory[cite: 3].
/// Vừa giữ danh mục các tool đã đăng ký phục vụ UI, vừa chịu trách nhiệm khởi tạo instance dựa trên khóa loại tool (TypeKey)[cite: 3].
/// </summary>
public interface IToolRegistry
{
    IReadOnlyCollection<ToolDescriptor> Descriptors { get; } // Danh mục chứa thông tin mô tả của toàn bộ tool đã đăng ký trong hệ thống[cite: 3]

    void Register(Type toolType); // Đăng ký một kiểu tool đơn lẻ vào hệ thống[cite: 3]

    void RegisterAssembly(Assembly assembly); // Quét toàn bộ một assembly để tự động tìm và đăng ký các tool hợp lệ[cite: 3]

    ToolDescriptor? Find(string typeKey); // Tìm kiếm mềm thông tin mô tả của tool qua khóa định danh, trả về null nếu không tìm thấy[cite: 3]

    VisionTool Create(string typeKey); // Khởi tạo nghiêm ngặt một instance tool mới từ khóa định danh, ném ngoại lệ nếu sai khóa[cite: 3]
}