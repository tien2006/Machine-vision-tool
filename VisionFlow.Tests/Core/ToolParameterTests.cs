// Kiểm thử tầng tham số cấu hình. Đảm bảo UI hoặc tệp JSON cấu hình khi truyền sai kiểu dữ liệu
// thì hệ thống không bị crash (văng ứng dụng) và tự động giữ lại giá trị an toàn trước đó.

using System;
using VisionFlow.Core.Models;
using VisionFlow.Core.Tools;
using Xunit;

namespace VisionFlow.Tests.Core;

public class ToolParameterTests
{
    [Fact]
    public void ToolParameter_ValidTypeAssignment_ShouldUpdateValue()
    {
        // Arrange: Khởi tạo tham số cấu hình kiểu double với giá trị mặc định là 10.0
        var param = new ToolParameter<double>("Exposure", 10.0);

        // Ép sang interface không định kiểu (object) để giả lập hành vi của tầng UI/Engine tương tác động
        IToolParameter iParam = param;

        // Act: Gán một giá trị hợp lệ cùng kiểu dữ liệu (double) thông qua interface object
        iParam.Value = 45.5;

        // Assert: Giá trị thực tế bên trong kiểu mạnh (TypedValue) phải được cập nhật chính xác
        // Dòng này là một câu lệnh Khẳng định (Assertion). Nó dùng để kiểm tra xem giá trị thực tế của biến có đúng như kỳ vọng hay không.
        // Mục đích: Xác nhận rằng khi ta thay đổi dữ liệu thông qua một cổng trung gian không định kiểu (IToolParameter.Value = 45.5).
        Assert.Equal(45.5, param.Value);
    }

    [Fact]
    public void ToolParameter_InvalidTypeAssignment_ShouldKeepFallbackValue_AndNotCrash()
    {
        // Arrange: Khởi tạo tham số kiểu int với giá trị mặc định là 128
        var param = new ToolParameter<int>("Threshold", 128);
        IToolParameter iParam = param;

        // Act: Giả lập kịch bản người dùng nhập lỗi một chuỗi văn bản chữ trên UI thay vì nhập số
        iParam.Value = "NotANumber";

        // Assert: Cơ chế phòng thủ bên trong Parameter phải chặn lại, không crash và giữ nguyên giá trị 128
        Assert.Equal(128, param.Value);
    }

    [Fact]
    public void ToolParameter_EnumStringAssignment_ShouldParseCorrectly()
    {
        // Arrange: Khởi tạo tham số nhận kiểu dữ liệu là một Enum (Judge)
        var param = new ToolParameter<Judge>("ResultJudge", Judge.None);
        IToolParameter iParam = param;

        // Act: Giả lập file JSON cấu hình đẩy xuống một chuỗi String đại diện cho Enum tên là "OK"
        iParam.Value = "OK";

        // Assert: Hệ thống tự động phân tích (Parse) chuỗi chữ thành giá trị Enum chính xác
        Assert.Equal(Judge.OK, param.Value);
    }
}