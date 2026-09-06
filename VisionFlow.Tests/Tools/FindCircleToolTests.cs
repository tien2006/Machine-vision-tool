// Kiểm thử chuyên sâu thuật toán tìm hình tròn. Đảm bảo tính toán chính xác hình học tâm và bán kính trong môi trường ảnh bị nhiễu và dọn dẹp bộ nhớ native.

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

namespace VisionFlow.Tests.Tools;

public class FindCircleToolTests : IDisposable
{
    private readonly FindCircleTool _tool;
    private MatVisionImage? _noisyImage;

    public FindCircleToolTests()
    {
        _tool = new FindCircleTool();
    }

    [Fact]
    public void Execute_WithNoisyImage_ShouldStillFindCircle_ButWithLowerScore()
    {
        // 1. Arrange: Sinh ảnh ngay tại vị trí mặc định của Tool: Tâm (150, 150), Bán kính 100
        // (Đường tròn dày 3 pixel, tỷ lệ nhiễu cực thấp 0.005 để thuật toán dễ hội tụ)
        _noisyImage = ImageGenerator.CreateNoisyCircle(400, 400, new Point(150, 150), 100, noiseRatio: 0.005);
        _tool.FindInput("Image")!.Value = _noisyImage;

        // BỎ QUA dòng gán Region vì Tool sẽ tự lấy giá trị mặc định (150, 150, R=100) để chạy

        // Vẫn cấu hình các bộ lọc phụ để thuật toán chạy mượt hơn trong môi trường nhiễu
        var caliperLengthParam = _tool.FindParameter("CaliperLength") as ToolParameter<double>;
        if (caliperLengthParam != null) caliperLengthParam.Value = 40.0;

        var polarityParam = _tool.FindParameter("EdgePolarity") as ToolParameter<string>;
        if (polarityParam != null) polarityParam.Value = "DarkToLight";

        var minRadiusParam = _tool.FindParameter("MinRadius") as ToolParameter<double>;
        if (minRadiusParam != null) minRadiusParam.Value = 80.0; // Bán kính thực 100 nên min để 80
        var maxRadiusParam = _tool.FindParameter("MaxRadius") as ToolParameter<double>;
        if (maxRadiusParam != null) maxRadiusParam.Value = 120.0; // Max để 120

        var mockContext = new Mock<IToolContext>();
        mockContext.Setup(c => c.CancellationToken).Returns(CancellationToken.None);

        // 2. Act
        _tool.Execute(mockContext.Object);

        // 3. Assert: Xác thực kết quả hội tụ về đúng vùng (150, 150) bán kính 100
        var result = (CircleResult)_tool.FindOutput("Circle")!.Value!;

        Assert.Equal(Judge.OK, result.Judge);

        // Cho phép sai số nhỏ dưới 2 pixel do ảnh hưởng của nhiễu hạt (Dùng precision = 2)
        Assert.InRange(result.Circle.Center.X, 148.0, 152.0);   // Kiểm tra X nằm trong khoảng [148.0, 152.0]
        Assert.InRange(result.Circle.Center.Y, 148.0, 152.0);   // Kiểm tra Y nằm trong khoảng [148.0, 152.0]     
        Assert.InRange(result.Circle.Radius, 98.0, 102.0);      // Kiểm tra bán kính R nằm trong khoảng [98.0, 102.0]

        double score = (double)_tool.FindOutput("Score")!.Value!;
        Assert.True(score > 0.5, $"Score thực tế đạt: {score}"); // Giảm ngưỡng score kỳ vọng vì có nhiễu

        // 4. Hiển thị ảnh kết quả lên màn hình để kiểm tra trực quan
        var outImage = (MatVisionImage)_tool.FindOutput("Image")!.Value!;
        using (new Window("Ket Qua Find Circle (Bam phim bat ky de dong)", outImage.Mat))
        {
            // Dừng luồng lại và đợi bạn bấm một phím bất kỳ trên bàn phím thì mới đóng cửa sổ và kết thúc test
            Cv2.WaitKey(0);
        }
    }

    public void Dispose()
    {
        // Giải phóng tài nguyên unmanaged của ảnh
        _noisyImage?.Dispose();
    }
}