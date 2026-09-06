// Kiểm thử thuật toán đo lường đường thẳng và tính toán góc xoay sản phẩm trên dây chuyền.

using Microsoft.VisualStudio.TestPlatform.Utilities;
using Moq;
using OpenCvSharp;
using System;
using System.Threading;
using VisionFlow.Core.Models;
using VisionFlow.Core.Tools;
using VisionFlow.Tests.Helpers;
using VisionFlow.Tools.Finding;
using VisionFlow.Tools.Imaging;
using Xunit;
using Xunit.Abstractions;

namespace VisionFlow.Tests.Tools;

public class FindLineToolTests : IDisposable
{
    private readonly FindLineTool _tool;
    private MatVisionImage? _sampleImage;
    private readonly ITestOutputHelper _output; // Thêm bộ hỗ trợ in log của xUnit

    public FindLineToolTests(ITestOutputHelper output)
    {
        _tool = new FindLineTool();
        _output = output;
    }

    // Đoạn 1: Mục đích: Kiểm thử "Kịch bản lý tưởng" (Happy Path) để xác định thuật toán đo lường góc và tìm đường thẳng
    // có hoạt động chính xác về mặt toán học hay không.
    [Fact]
    public void Execute_WithPerfectHorizontalLine_ShouldReturnZeroDegree_AndJudgeOK()
    {
        // 1. Arrange: Tạo một đường thẳng nằm ngang song song trục X từ X=50 đến X=350, tại Y=200
        _sampleImage = ImageGenerator.CreatePerfectLine(400, 400, new Point(50, 240), new Point(350, 240));
        _tool.FindInput("Image")!.Value = _sampleImage;

        // CẤU HÌNH BỔ SUNG: Ép Caliper chỉ bắt điểm từ Tối sang Sáng (Rìa ngoài đầu tiên)
        var polarityParam = _tool.FindParameter("EdgePolarity") as ToolParameter<string>;
        if (polarityParam != null) polarityParam.Value = "DarkToLight";

        // (Tùy chọn) Hạ thấp ngưỡng score yêu cầu nếu ảnh có yếu tố bo viền làm giảm score
        var minScoreParam = _tool.FindParameter("MinScore") as ToolParameter<double>;
        if (minScoreParam != null) minScoreParam.Value = 0.1;

        var mockContext = new Mock<IToolContext>();
        mockContext.Setup(c => c.CancellationToken).Returns(CancellationToken.None);

        // 2. Act: Thực thi thuật toán
        _tool.Execute(mockContext.Object);

        // Đọc trực tiếp giá trị tính toán từ cổng Output
        double actualRms = (double)_tool.FindOutput("RMSError")!.Value!;
        double actualScore = (double)_tool.FindOutput("Score")!.Value!;

        // In ra cửa sổ Test Detail Summary
        _output.WriteLine($"     - RMS Error tính được: {actualRms}");
        _output.WriteLine($"     - Score tính được: {actualScore}");

        // 3. Assert: Kiểm tra góc xoay đường thẳng
        var result = (LineResult)_tool.FindOutput("Line")!.Value!;

        Assert.Equal(Judge.OK, result.Judge);

        // Lấy trị tuyệt đối và chia lấy dư cho 180 để quy đổi cả 0 và 180 về 0
        double normalizedAngle = Math.Abs(result.AngleDeg) % 180;
        Assert.Equal(0.0, normalizedAngle, 1);
    }

    // Đoạn 2: Mục đích: Kiểm thử "Kịch bản lỗi/Ngoại lệ" (Sad Path) để đảm bảo tính ổn định (Robustness) và cơ chế phòng vệ (Defensive Programming) của Tool.
    [Fact]
    public void Execute_MissingInputImage_ShouldThrowToolExecutionException()
    {
        // Arrange: Cố tình không cấp nguồn ảnh cho cổng "Image"
        var mockContext = new Mock<IToolContext>();
        mockContext.Setup(c => c.CancellationToken).Returns(CancellationToken.None);

        // Act & Assert: Hệ thống gọi ValidateInputs() kiểm tra cổng bắt buộc trống 
        // -> Ném ra ngoại lệ quy định của Domain là ToolExecutionException chứ không phải ArgumentNullException
        Assert.Throws<ToolExecutionException>(() => _tool.Execute(mockContext.Object));
    }

    public void Dispose()
    {
        // Giải phóng tài nguyên đồ họa native OpenCV ngay khi ca kiểm thử kết thúc
        _sampleImage?.Dispose();
    }
}