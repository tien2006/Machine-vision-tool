// ==================== Vai trò chính:                "Danh bạ" trung tâm: đăng ký loại Tool nào tồn tại + biết cách khởi tạo (factory) Tool đó theo tên khóa (string)
// ==================== Thành phần / Class tiêu biểu: IToolRegistry, ToolRegistry, ToolDescriptor
// ==================== Phụ thuộc vào:                Tools + System.Reflection
// ==================== Pattern / Kỹ thuật nổi bật:   Abstract Factory Pattern + Reflection auto-discovery (quét Assembly tìm class có [ToolMetadata])

using System;
using System.Collections.Generic; // Sử dụng Dictionary và ReadOnlyCollection cho việc lưu trữ[cite: 3]
using System.Collections.ObjectModel; // Hỗ trợ đóng gói danh sách chỉ đọc an toàn[cite: 3]
using System.Linq; // Phục vụ việc chuyển đổi mảng dữ liệu LINQ[cite: 3]
using System.Reflection; // Phục vụ thao tác kiểm tra cấu trúc qua Reflection[cite: 3]
using VisionFlow.Core.Tools; // Kéo vào lớp nền VisionTool và bộ Attribute Metadata[cite: 3]

namespace VisionFlow.Core.Registry; // Đặt trong phân hệ quản lý đăng ký Core của dự án[cite: 3]

/// <summary>
/// Phân hệ triển khai cụ thể của IToolRegistry đóng vai trò như một danh bạ trung tâm[cite: 3].
/// Quản lý việc lưu trữ, tự động khám phá (auto-discovery) thông qua Reflection và khởi tạo các đối tượng xử lý ảnh[cite: 3].
/// </summary>
public sealed class ToolRegistry : IToolRegistry // Sử dụng từ khóa sealed để khóa thiết kế, không cho phép lớp khác kế thừa[cite: 3]
{
    private readonly Dictionary<string, ToolDescriptor> _byKey = new(StringComparer.Ordinal); // Từ điển ánh xạ từ khóa string sang Descriptor, so sánh byte phân biệt hoa thường[cite: 3]

    public IReadOnlyCollection<ToolDescriptor> Descriptors => new ReadOnlyCollection<ToolDescriptor>(_byKey.Values.ToList()); // Trả về một bản xem sống chỉ đọc của các giá trị trong từ điển[cite: 3]

    public void Register(Type toolType)
    {
        if (!typeof(VisionTool).IsAssignableFrom(toolType) || toolType.IsAbstract) // Kiểm tra bảo đảm kiểu truyền vào phải kế thừa VisionTool và không abstract[cite: 3]
        {
            throw new ArgumentException($"Type {toolType.Name} is not a concrete VisionTool.", nameof(toolType)); // Ném lỗi nếu sai kiểu quy định[cite: 3]
        }

        ToolMetadataAttribute meta = toolType.GetCustomAttribute<ToolMetadataAttribute>() // Soi xem lớp có dán nhãn [ToolMetadata] trên đầu không[cite: 3]
            ?? throw new ArgumentException($"Type {toolType.Name} is missing ToolMetadataAttribute.", nameof(toolType)); // Ném lỗi bằng biểu thức throw ngắn gọn nếu thiếu attribute[cite: 3]

        if (toolType.GetConstructor(Type.EmptyTypes) == null) // Tìm kiếm constructor mặc định không tham số[cite: 3]
        {
            throw new ArgumentException($"Type {toolType.Name} must have a parameterless constructor.", nameof(toolType)); // Bắt buộc phải có để Activator hoạt động ổn định[cite: 3]
        }

        VisionTool Factory() => (VisionTool)Activator.CreateInstance(toolType)!; // Hàm cục bộ sử dụng kỹ thuật closure để bắt biến toolType phục vụ khởi tạo instance sau này[cite: 3]

        string displayName = string.IsNullOrEmpty(meta.DisplayName) ? toolType.Name : meta.DisplayName; // Dự phòng lấy tên class làm tên hiển thị nếu chuỗi trống[cite: 3]

        ToolDescriptor descriptor = new ToolDescriptor( // Tạo bản ghi mô tả bất biến cho tool[cite: 3]
            meta.Key,
            displayName,
            meta.Category,
            meta.Description,
            toolType,
            Factory
        );

        _byKey[meta.Key] = descriptor; // Lưu hoặc ghi đè vào từ điển theo khóa định danh kỹ thuật[cite: 3]
    }

    public void RegisterAssembly(Assembly assembly)
    {
        foreach (Type type in assembly.GetTypes()) // Duyệt qua toàn bộ các kiểu dữ liệu định nghĩa trong assembly[cite: 3]
        {
            if (!typeof(VisionTool).IsAssignableFrom(type) || type.IsAbstract) continue; // Bỏ qua nếu không phải tool hoặc là lớp trừu tượng[cite: 3]

            if (type.GetCustomAttribute<ToolMetadataAttribute>() == null) continue; // Bỏ qua nếu lớp này không được gắn nhãn metadata[cite: 3]

            Register(type); // Đưa vào hàm đăng ký đơn lẻ để thực hiện các bước guard và lưu trữ[cite: 3]
        }
    }

    public ToolDescriptor? Find(string typeKey)
    {
        return _byKey.TryGetValue(typeKey, out ToolDescriptor? descriptor) ? descriptor : null; // Tìm kiếm an toàn, trả về đối tượng nếu có hoặc null nếu thiếu[cite: 3]
    }

    public VisionTool Create(string typeKey)
    {
        ToolDescriptor descriptor = Find(typeKey) // Thực hiện tra cứu qua hàm Find mềm[cite: 3]
            ?? throw new KeyNotFoundException($"Tool type key '{typeKey}' is not registered in the system."); // Coi việc sai khóa nạp cấu hình flow là lỗi nghiêm trọng và ném exception[cite: 3]

        return descriptor.Factory(); // Thực thi delegate factory để sinh ra một thực thể tool độc lập về trạng thái[cite: 3]
    }
}