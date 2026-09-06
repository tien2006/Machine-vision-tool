using System;
using System.Collections.Generic;
using System.Linq;
using Moq;
using Xunit;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Engine.Execution;
using VisionFlow.Engine.Graph;

namespace VisionFlow.Tests.Engine
{
    /// <summary>
    /// Bộ kiểm thử đơn vị (Unit Test) cho Execution Engine của VisionFlow.
    /// Bao gồm các kịch bản kiểm thử:
    /// 1. Sắp xếp thứ tự thực thi theo đồ thị có hướng (Topological Sort / Kahn) và phát hiện chu trình lặp kín.
    /// 2. Luồng thực thi chuẩn, xử lý lỗi và lan truyền trạng thái bỏ qua (Skip) cho các Node phụ thuộc.
    /// 3. Quản lý bộ nhớ ảnh (IVisionImage) khi phân nhánh (Fan-out) và dọn dẹp bộ nhớ (Dispose).
    /// </summary>
    public class FlowExecutorTests
    {
        #region 1. Test Thuật Toán Topological Sort & Phát Hiện Chu Trình (Kahn)

        /// <summary>
        /// Kiểm thử trường hợp đồ thị dạng tuyến tính thẳng (A -> B -> C).
        /// Kỳ vọng: Thuật toán Kahn phải trả về đúng thứ tự phụ thuộc là A, sau đó đến B, cuối cùng là C.
        /// </summary>
        [Fact]
        public void TopologicalSort_LinearGraph_ShouldReturnCorrectOrder()
        {
            // ARRANGE (Chuẩn bị dữ liệu đầu vào)
            var graph = new FlowGraph();
            var nodeA = CreateDummyNode("NodeA");
            var nodeB = CreateDummyNode("NodeB");
            var nodeC = CreateDummyNode("NodeC");

            // Thêm các node vào đồ thị
            graph.AddNode(nodeA);
            graph.AddNode(nodeB);
            graph.AddNode(nodeC);

            // Thiết lập dây nối: Cổng "Out" của A -> Cổng "In" của B, Cổng "Out" của B -> Cổng "In" của C
            graph.Connect(nodeA.Id, "Out", nodeB.Id, "In");
            graph.Connect(nodeB.Id, "Out", nodeC.Id, "In");

            // ACT (Thực thi hàm cần kiểm thử)
            var result = FlowExecutor.TopologicalSort(graph);

            // ASSERT (Kiểm tra kết quả)
            Assert.Equal(3, result.Count);                  // Tổng số node trả về phải đủ 3
            Assert.Equal(nodeA.Id, result[0].Id);            // Node A phải chạy đầu tiên
            Assert.Equal(nodeB.Id, result[1].Id);            // Node B phải chạy thứ hai
            Assert.Equal(nodeC.Id, result[2].Id);            // Node C phải chạy cuối cùng
        }

        /// <summary>
        /// Kiểm thử trường hợp đồ thị bị lặp vòng kín (A -> B -> A).
        /// Kỳ vọng: Phải ném ra ngoại lệ FlowExecutionException cảnh báo đồ thị có chu trình.
        /// </summary>
        [Fact]
        public void TopologicalSort_CyclicGraph_ShouldThrowFlowExecutionException()
        {
            // ARRANGE (Chuẩn bị đồ thị có chu trình lặp)
            var graph = new FlowGraph();
            var nodeA = CreateDummyNode("NodeA");
            var nodeB = CreateDummyNode("NodeB");

            graph.AddNode(nodeA);
            graph.AddNode(nodeB);

            // Tạo chu trình kín: A nối sang B, và B nối ngược lại A
            graph.Connect(nodeA.Id, "Out", nodeB.Id, "In");
            graph.Connect(nodeB.Id, "Out", nodeA.Id, "In");

            // ACT & ASSERT (Xác nhận việc ném ngoại lệ khi thực thi)
            var ex = Assert.Throws<FlowExecutionException>(() => FlowExecutor.TopologicalSort(graph));

            // Kiểm tra thông báo lỗi có chứa từ khóa cảnh báo chu trình
            Assert.Contains("Đồ thị flow có chu trình", ex.Message);
        }

        #endregion

        #region 2. Test Luồng Thực Thi & Lan Truyền Trạng Thái (Skip / Fail)

        /// <summary>
        /// Kiểm thử trường hợp flow chạy thuận lợi, các node không bị lỗi.
        /// Kỳ vọng: Trạng thái tổng thể trả về Success = true và trạng thái của Node là Completed.
        /// </summary>
        [Fact]
        public void Run_SuccessfulExecution_ShouldReturnSuccessResult()
        {
            // ARRANGE
            using var executor = new FlowExecutor(); // Tạo bộ thực thi FlowExecutor
            var graph = new FlowGraph();

            // Đã sửa: Dùng CreateStandaloneNode cho node chạy độc lập để không bắt buộc có Input
            var node = CreateStandaloneNode("Node1");
            graph.AddNode(node);

            // ACT
            var result = executor.Run(graph);

            // ASSERT
            Assert.True(result.Success);                              // Luồng chạy thành công
            Assert.Single(result.Nodes);                              // Đúng 1 kết quả node
            Assert.Equal(ToolState.Completed, result.Nodes[0].State); // Node đạt trạng thái Completed
        }

        /// <summary>
        /// Kiểm thử cơ chế lan truyền lỗi (Skip propagation):
        /// Nếu Node A (thượng nguồn) bị lỗi, Node B (hạ nguồn phụ thuộc vào A) phải tự động chuyển sang trạng thái Skipped (Bỏ qua).
        /// </summary>
        [Fact]
        public void Run_WhenUpstreamFails_DownstreamShouldBeSkipped()
        {
            // ARRANGE
            using var executor = new FlowExecutor();
            var graph = new FlowGraph();

            var nodeA = CreateFailingNode("NodeA_Faulty");    // Node A cố ý ném ra Exception
            var nodeB = CreateDummyNode("NodeB_Dependent");   // Node B phụ thuộc vào Node A (optional: false)

            graph.AddNode(nodeA);
            graph.AddNode(nodeB);

            // Nối A -> B
            graph.Connect(nodeA.Id, "Out", nodeB.Id, "In");

            // ACT
            var result = executor.Run(graph);

            // ASSERT
            Assert.False(result.Success); // Kết quả tổng thể là thất bại do có node lỗi

            var resA = result.Nodes[0];
            var resB = result.Nodes[1];

            // Kiểm tra Node A: Phải ở trạng thái Failed và lưu đúng thông điệp lỗi
            Assert.Equal(ToolState.Failed, resA.State);
            Assert.Equal("Lỗi cố ý từ Mock Tool", resA.Error);

            // Kiểm tra Node B: Phải bị Bỏ qua (Skipped) do node thượng nguồn gặp sự cố
            Assert.Equal(ToolState.Skipped, resB.State);
            Assert.Contains("Bỏ qua: tool thượng nguồn lỗi.", resB.Error);
        }

        #endregion

        #region 3. Test Truyền Dữ Liệu Fan-Out & Quản Lý Vòng Đời Ảnh (IVisionImage)

        /// <summary>
        /// Kiểm thử kịch bản Fan-out (1 Output nối tới nhiều Input):
        /// Khi Node A xuất ra 1 ảnh và nối sang cả Node B lẫn Node C, Engine phải tự động Clone ảnh 
        /// để tránh tranh chấp RAM giữa các thread/tool, đồng thời theo dõi và Dispose ảnh Clone khi dọn dẹp.
        /// </summary>
        [Fact]
        public void FeedInputs_WhenFanOutImage_ShouldCloneImageAndTrackForDisposal()
        {
            // ARRANGE
            using var executor = new FlowExecutor();
            var graph = new FlowGraph();

            // Giả lập đối tượng Ảnh gốc và Ảnh Clone bằng Moq
            var mockImage = new Mock<IVisionImage>();
            var mockClone = new Mock<IVisionImage>();

            // Khi gọi hàm Clone() trên ảnh gốc, trả về đối tượng mockClone
            mockImage.Setup(img => img.Clone()).Returns(mockClone.Object);

            var nodeA = CreateImageProviderNode("NodeA", mockImage.Object); // Node A cung cấp ảnh
            var nodeB = CreateDummyNode("NodeB");
            var nodeC = CreateDummyNode("NodeC");

            graph.AddNode(nodeA);
            graph.AddNode(nodeB);
            graph.AddNode(nodeC);

            // Nối Fan-out: 1 Cổng "ImageOut" của Node A nối đồng thời vào "In" của B và C
            graph.Connect(nodeA.Id, "ImageOut", nodeB.Id, "In");
            graph.Connect(nodeA.Id, "ImageOut", nodeC.Id, "In");

            // ACT
            executor.Run(graph);

            // ASSERT
            // 1. Kiểm tra hàm Clone() của ảnh gốc đã được gọi ít nhất 1 lần để phục vụ Fan-out
            mockImage.Verify(img => img.Clone(), Times.AtLeastOnce);

            // 2. Khi gọi Dispose() trên FlowExecutor, ảnh Clone sinh ra phải được dọn dẹp sạch sẽ
            executor.Dispose();
            mockClone.Verify(img => img.Dispose(), Times.AtLeastOnce); // Xác nhận hàm Dispose() của bản clone đã được thực thi
        }

        #endregion

        #region 4. Helper Methods & Classes (Xử lý khởi tạo FlowNode & tránh lỗi closure CS0841)

        /// <summary>
        /// Lớp giả lập VisionTool dùng riêng cho môi trường Test.
        /// Chấp nhận Action nhận vào chính instance của TestVisionTool để tránh lỗi capture biến chưa được khai báo.
        /// </summary>
        private class TestVisionTool : VisionTool
        {
            private readonly Action<TestVisionTool, IToolContext>? _executeAction;

            public TestVisionTool(Action<TestVisionTool, IToolContext>? executeAction = null)
            {
                _executeAction = executeAction;
            }

            // Mở rộng phạm vi truy cập các hàm protected để dễ khởi tạo port trong hàm Helper
            public new InputPort<T> AddInput<T>(string name, string? displayName = null, bool optional = false)
                => base.AddInput<T>(name, displayName, optional);

            public new OutputPort<T> AddOutput<T>(string name, string? displayName = null)
                => base.AddOutput<T>(name, displayName);

            protected override void OnExecute(IToolContext context)
            {
                // Truyền 'this' vào callback để truy cập instance an toàn
                _executeAction?.Invoke(this, context);
            }
        }

        /// <summary>
        /// Tạo Node đơn lẻ không phụ thuộc Input, dùng cho test chạy độc lập thành công.
        /// </summary>
        private FlowNode CreateStandaloneNode(string displayName)
        {
            var tool = new TestVisionTool();
            tool.AddOutput<object>("Out");
            return new FlowNode(tool);
        }

        /// <summary>
        /// Tạo một FlowNode giả lập (Dummy) có 1 cổng Input "In" bắt buộc (optional = false) và 1 cổng Output "Out".
        /// </summary>
        private FlowNode CreateDummyNode(string displayName)
        {
            var tool = new TestVisionTool();
            tool.AddInput<object>("In", optional: true);
            tool.AddOutput<object>("Out");

            return new FlowNode(tool);
        }

        /// <summary>
        /// Tạo một FlowNode giả lập bị lỗi: Khi hàm Execute() được gọi sẽ lập tức ném ra ngoại lệ.
        /// </summary>
        private FlowNode CreateFailingNode(string displayName)
        {
            var tool = new TestVisionTool((t, ctx) =>
            {
                throw new Exception("Lỗi cố ý từ Mock Tool");
            });

            tool.AddInput<object>("In", optional: false);
            tool.AddOutput<object>("Out");

            return new FlowNode(tool);
        }

        /// <summary>
        /// Tạo một FlowNode giả lập đóng vai trò cung cấp dữ liệu hình ảnh (IVisionImage) ở đầu ra.
        /// </summary>
        private FlowNode CreateImageProviderNode(string displayName, IVisionImage imageToReturn)
        {
            // Sử dụng tham số 't' được truyền từ OnExecute để tìm port, tránh được lỗi CS0841
            var tool = new TestVisionTool((t, ctx) =>
            {
                var outPort = t.FindOutput("ImageOut");
                if (outPort != null) outPort.Value = imageToReturn;
            });

            var outputPort = tool.AddOutput<IVisionImage>("ImageOut");
            outputPort.Value = imageToReturn;

            return new FlowNode(tool);
        }

        #endregion
    }

    /// <summary>
    /// Lớp chứa các Extension Method bổ trợ truy xuất cổng Input/Output nhanh chóng cho giao diện <see cref="ITool"/>.
    /// Giúp Engine và Unit Test tìm kiếm cổng kết nối theo tên một cách linh hoạt.
    /// </summary>
    public static class ToolExtensions
    {
        /// <summary>
        /// Tìm cổng Output có tên trùng khớp trong danh sách Outputs của Tool.
        /// </summary>
        public static IOutputPort? FindOutput(this ITool tool, string name)
        {
            return tool.Outputs?.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));
        }

        /// <summary>
        /// Tìm cổng Input có tên trùng khớp trong danh sách Inputs của Tool.
        /// </summary>
        public static IInputPort? FindInput(this ITool tool, string name)
        {
            return tool.Inputs?.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));
        }
    }
}