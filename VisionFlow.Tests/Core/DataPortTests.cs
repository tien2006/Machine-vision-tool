using System;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Models;
using VisionFlow.Core.Ports;
using VisionFlow.Engine.Graph;
using VisionFlow.Tools.Finding; 
using Xunit;

namespace VisionFlow.Tests.Core;

public class DataPortTests
{
    // Kịch bản 1: Kết nối 2 cổng hợp lệ và giả lập Engine truyền dữ liệu thành công.
    [Fact]
    public void ConnectNodes_WithCompatiblePorts_ShouldEstablishConnectionInGraph()
    {
        // 1. Arrange (Khởi tạo môi trường test)
        var graph = new FlowGraph();                // Tạo một đồ thị xử lý FlowGraph.
        var toolSource = new FindCircleTool();      // Tạo 2 công cụ FindCircleTool đại diện cho Tool nguồn 
        var toolTarget = new FindCircleTool();      // và Tool đích.

        var sourceNode = graph.AddNode(toolSource);
        var targetNode = graph.AddNode(toolTarget);

        // Giả lập dữ liệu ảnh đầu ra hợp lệ cho cổng "Image"
        // (Nếu bạn chưa có Mock, dùng null hoặc khởi tạo một instance IVisionImage phù hợp)
        IVisionImage? expectedImage = null;

        var outputPort = (OutputPort<IVisionImage>)toolSource.FindOutput("Image")!;
        outputPort.Value = expectedImage;

        // 2. Act: Cắm cổng ra "Image" của Node nguồn vào cổng vào "Image" của Node đích (Cùng kiểu IVisionImage)
        FlowConnection connection = graph.Connect(
            sourceNodeId: sourceNode.Id,
            sourcePort: "Image",
            targetNodeId: targetNode.Id,
            targetPort: "Image" 
        );

        // 3. Giả lập Engine truyền dữ liệu
        var sourceNodeRef = graph.GetNode(connection.SourceNodeId)!;
        var targetNodeRef = graph.GetNode(connection.TargetNodeId)!;

        IPort srcPort = sourceNodeRef.Tool.FindOutput(connection.SourcePort)!;
        IPort tgtPort = targetNodeRef.Tool.FindInput(connection.TargetPort)!;

        // Gán dữ liệu thông qua IPort interface
        tgtPort.Value = srcPort.Value;

        // 4. Assert: Kiểm tra cổng "Image" của Node đích nhận đúng kiểu IVisionImage
        var inputPortStrongType = (InputPort<IVisionImage>)toolTarget.FindInput("Image")!;

        Assert.Contains(connection, graph.Connections);
        Assert.Equal(expectedImage, inputPortStrongType.Value);
    }

    [Fact]
    public void ConnectNodes_WithIncompatiblePorts_ShouldThrowExceptionOnEngineExecution()
    {
        // Arrange
        var graph = new FlowGraph();
        var toolCircle = new FindCircleTool(); 
        var toolLine = new FindLineTool(); // Giả sử Tool này có cổng vào nhận IVisionImage tên là "Image"

        var sourceNode = graph.AddNode(toolCircle);
        var targetNode = graph.AddNode(toolLine);

        // Cắm cổng ra "Circle" (kiểu CircleResult) vào cổng vào "Image" (định dạng mong đợi khác)
        var connection = graph.Connect(sourceNode.Id, "Circle", targetNode.Id, "Image");

        toolCircle.FindOutput("Circle")!.Value = new CircleResult();

        // Act & Assert: Gán sai cấu trúc dữ liệu mong đợi kích hoạt ngoại lệ ArgumentException
        Assert.Throws<ArgumentException>(() =>
        {
            var srcPort = graph.GetNode(connection.SourceNodeId)!.Tool.FindOutput(connection.SourcePort)!;
            var tgtPort = graph.GetNode(connection.TargetNodeId)!.Tool.FindInput(connection.TargetPort)!;

            tgtPort.Value = srcPort.Value;
        });
    }
}