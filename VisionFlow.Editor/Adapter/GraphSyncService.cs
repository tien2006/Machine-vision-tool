using DynamicData; // Thư viện xử lý tập hợp dynamic/reactive
using NodeNetwork.ViewModels; // Namespace chứa NetworkViewModel, ConnectionViewModel
using System.Windows; // Sử dụng cho kiểu Point (toạ độ X, Y trên WPF canvas)
using VisionFlow.Editor.ViewModels; // Namespace chứa các ToolNodeViewModel, PortInputViewModel,...
using VisionFlow.Engine.Graph; // Namespace chứa FlowGraph (Engine)

namespace VisionFlow.Editor.Adapter;

/// <summary>
/// Service chuyển đổi đồng bộ giữa NetworkViewModel (UI) và FlowGraph (Engine).
/// </summary>
public static class GraphSyncService
{
    /// <summary>
    /// Trích xuất và chuyển đổi NetworkViewModel hiển thị trên canvas thành cấu trúc FlowGraph của Engine.
    /// </summary>
    /// <param name="network">Đồ thị NetworkViewModel từ UI</param>
    /// <param name="name">Tên đồ thị</param>
    /// <param name="pixelSize">Kích thước pixel đại diện</param>
    /// <returns>Đối tượng FlowGraph hoàn chỉnh để thực thi hoặc lưu trữ</returns>
    public static FlowGraph ToFlowGraph(NetworkViewModel network, string name = "Untitled", double pixelSize = 1.0)
    {
        var graph = new FlowGraph
        {
            Name = name,
            PixelSize = pixelSize
        };

        // Duyệt tất cả các Node trên canvas, lọc chỉ lấy những Node là ToolNodeViewModel
        foreach (var node in network.Nodes.Items.OfType<ToolNodeViewModel>())
        {
            // Thêm Tool domain và vị trí toạ độ X, Y vào FlowGraph
            graph.AddNode(node.Tool, node.Position.X, node.Position.Y);
        }

        // Duyệt qua tất cả các dây nối (Connections) hiện có trên canvas UI
        foreach (var conn in network.Connections.Items)
        {
            // Pattern matching ép kiểu và kiểm tra điều kiện an toàn đồng thời
            if (conn.Output is PortOutputViewModel output
                && conn.Input is PortInputViewModel input
                && output.Parent is ToolNodeViewModel sourceNode
                && input.Parent is ToolNodeViewModel targetNode)
            {
                // Thực hiện kết nối 2 Node trong FlowGraph theo ID của Tool và tên cổng
                graph.Connect(sourceNode.Tool.Id, output.DomainPort.Name, targetNode.Tool.Id, input.DomainPort.Name);
            }
        }

        return graph;
    }

    /// <summary>
    /// Tải dữ liệu từ một FlowGraph dựng lại giao diện các Node và Connection lên NetworkViewModel.
    /// </summary>
    /// <param name="network">Đồ thị UI target</param>
    /// <param name="graph">Đồ thị FlowGraph nguồn từ Engine/File</param>
    public static void LoadInto(NetworkViewModel network, FlowGraph graph)
    {
        // 1. Dọn dẹp sạch sẽ giao diện cũ (Phải Clear Connections trước rồi mới Clear Nodes)
        network.Connections.Clear();
        network.Nodes.Clear();

        // Dictionary ánh xạ ID của Node domain sang ToolNodeViewModel trên UI
        var nodeMap = new Dictionary<string, ToolNodeViewModel>(StringComparer.Ordinal);

        // 2. Tạo danh sách các Node trên UI từ danh sách Nodes trong FlowGraph
        foreach (var fn in graph.Nodes)
        {
            var node = new ToolNodeViewModel(fn.Tool)
            {
                Position = new Point(fn.X, fn.Y) // Phục hồi lại vị trí hiển thị trên Canvas
            };
            network.Nodes.Add(node); // Thêm Node vào Canvas UI
            nodeMap[fn.Id] = node; // Lưu vào Dictionary để nối dây ở bước sau
        }

        // 3. Tái tạo danh sách dây nối (Connections) trên Canvas UI
        foreach (var c in graph.Connections)
        {
            // Tra cứu kiểm tra an toàn xem cả Node nguồn và Node đích có tồn tại trong Map hay không
            if (nodeMap.TryGetValue(c.SourceNodeId, out var source)
                && nodeMap.TryGetValue(c.TargetNodeId, out var target))
            {
                // Sử dụng ConnectionFactory của NodeNetwork để tạo kết nối giữa Input và Output tương ứng
                var connection = network.ConnectionFactory(target.GetInput(c.TargetPort), source.GetOutput(c.SourcePort));
                network.Connections.Add(connection); // Thêm dây nối vào đồ thị UI
            }
        }
    }
}