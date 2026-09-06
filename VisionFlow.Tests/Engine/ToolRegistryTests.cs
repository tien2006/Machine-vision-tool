// Kiểm thử bộ đăng ký Factory. Đảm bảo tính năng Reflection tự động nhận diện đúng Metadata của Plugin và kiểm soát tốt các chuỗi ký tự lỗi tránh làm treo Engine.

using System;
using System.Collections.Generic;
using VisionFlow.Core.Registry;
using VisionFlow.Tools.Finding;
using Xunit;

namespace VisionFlow.Tests.Engine;

public class ToolRegistryTests
{
    [Fact]
    public void Register_ValidToolType_ShouldBeRetrievableFromRegistry()
    {
        // Arrange: Khởi tạo bộ đăng ký trung tâm
        var registry = new ToolRegistry();

        // Act: Đăng ký một kiểu dữ liệu Tool thuật toán thực tế vào hệ thống
        registry.Register(typeof(FindCircleTool));
        // typeof(FindCircleTool)": Bản thiết kế (System.Type): Là thông tin lý thuyết về cấu trúc của công cụ đó (Nó tên gì? Có những hàm nào? Dán nhãn gì?)

        // Assert: Truy vấn lại bằng chuỗi String (Tên Tool) dùng để cấu hình từ tệp JSON
        var descriptor = registry.Find("FindCircle");

        // 1. Khẳng định: Phải tìm thấy cấu hình đăng ký, không được phép null
        Assert.NotNull(descriptor);

        // 2. Khẳng định: Key kỹ thuật lưu trong descriptor phải trùng khớp
        Assert.Equal("FindCircle", descriptor.Key);

        // 3. Khẳng định: Metadata phân loại (Category) phải đúng với [ToolMetadata] là "Detection"
        Assert.Equal("Detection", descriptor.Category);
    }

    [Fact]
    public void Create_UnregisteredToolKey_ShouldThrowKeyNotFoundException()
    {
        // Arrange: Bộ đăng ký trống
        var registry = new ToolRegistry();

        // Act & Assert: Yêu cầu khởi tạo một Tool không tồn tại -> Bắt buộc ném lỗi KeyNotFoundException công khai
        Assert.Throws<KeyNotFoundException>(() => registry.Create("NonExistentTool"));
    }
}