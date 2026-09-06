using System.Diagnostics; // Cung cấp lớp Stopwatch để đo thời gian thực thi
using VisionFlow.Core.Imaging; // Nạp các interface/lớp liên quan tới xử lý ảnh (IVisionImage)
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools; // Nạp các interface/lớp liên quan tới Tool, IToolContext
using VisionFlow.Engine.Graph; // Nạp các cấu trúc đồ thị (FlowGraph, FlowNode)

namespace VisionFlow.Engine.Execution; // Khai báo namespace file-scoped

/// <summary>
/// Engine thực thi flow theo mô hình DAG (thay cho cơ chế chạy-song-song-busy-wait của bản cũ):
/// <list type="number">
/// <item>Sắp xếp topo (Kahn) + phát hiện chu trình.</item>
/// <item>Chạy node tuần tự theo đúng thứ tự phụ thuộc (deterministic, dễ đo thời gian).</item>
/// <item>Trước mỗi node: gom giá trị từ output thượng nguồn vào input (clone ảnh khi fan-out).</item>
/// <item>Lan truyền "skip" cho node phía sau nếu thượng nguồn (bắt buộc) lỗi.</item>
/// </list>
/// <para>
/// Quản lý vòng đời ảnh tập trung: ảnh sinh ra trong một lần chạy được giữ để UI xem kết quả,
/// và bị dispose ở đầu lần chạy kế tiếp (hoặc khi <see cref="Dispose"/>).
/// </para>
/// </summary>
public sealed class FlowExecutor : IDisposable // Triển khai IDisposable để quản lý/dọn dẹp bộ nhớ ảnh
{
    // Danh sách lưu trữ tất cả các ảnh được sinh ra trong quá trình chạy để quản lý vòng đời (dispose)
    private readonly List<IVisionImage> _trackedImages = new();

    /// <summary>Chạy toàn bộ flow theo thứ tự topo.</summary>
    public ExecutionResult Run(FlowGraph graph, IToolContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(graph); // Kiểm tra null cho tham số graph
        context ??= new ToolContext(graph.PixelSize); // Nếu context null thì tạo mới với PixelSize của graph
        DisposeTracked(); // Giải phóng bộ nhớ ảnh của lần chạy trước
        return ExecuteOrdered(graph, TopologicalSort(graph), context); // Sắp xếp topo và thực thi
    }

    /// <summary>
    /// Chạy đúng prefix topo kết thúc ở <paramref name="target"/> (tất cả tổ tiên + chính nó) — dùng để
    /// lấy ảnh đầu vào thật cho một node khi mở Parameter Editor.
    /// </summary>
    public ExecutionResult RunUpTo(FlowGraph graph, FlowNode target, IToolContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(graph);  // Kiểm tra null cho graph
        ArgumentNullException.ThrowIfNull(target); // Kiểm tra null cho target node
        context ??= new ToolContext(graph.PixelSize); // Khởi tạo context mặc định nếu null
        DisposeTracked(); // Giải phóng bộ nhớ ảnh cũ

        var ancestors = AncestorsInclusive(graph, target); // Tìm tất cả node tổ tiên và chính target node
        // Lọc danh sách sắp xếp topo để chỉ giữ lại các node thuộc tập tổ tiên
        var ordered = TopologicalSort(graph).Where(n => ancestors.Contains(n.Id)).ToList();
        return ExecuteOrdered(graph, ordered, context); // Thực thi các node đã lọc theo thứ tự
    }

    /// <summary>
    /// Chỉ chạy lại MỘT node, lấy input từ output đã cache của thượng nguồn (lần chạy trước). Dùng cho
    /// nút "Run node" + preview ROI realtime trong Parameter Editor.
    /// </summary>
    public ExecutionResult RunSingleNode(FlowGraph graph, FlowNode node, IToolContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(graph); // Kiểm tra null
        ArgumentNullException.ThrowIfNull(node);  // Kiểm tra null
        context ??= new ToolContext(graph.PixelSize); // Tạo context mặc định nếu null
        // Thực thi duy nhất node được chỉ định; không lan truyền skip, không giải phóng ảnh trước đó
        return ExecuteOrdered(graph, new[] { node }, context, propagateSkip: false, disposeFirst: false);
    }

    /// <summary>
    /// Chỉ chạy các node THƯỢNG NGUỒN của <paramref name="node"/> (không chạy chính nó) rồi nạp input
    /// cho nó — để hiển thị ảnh đầu vào NGAY mà không phải chờ thuật toán nặng của node chạy xong.
    /// </summary>
    public void PrepareInputs(FlowGraph graph, FlowNode node, IToolContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(graph); // Kiểm tra null
        ArgumentNullException.ThrowIfNull(node);  // Kiểm tra null
        context ??= new ToolContext(graph.PixelSize); // Tạo context mặc định nếu null
        DisposeTracked(); // Giải phóng ảnh cũ

        var ancestors = AncestorsInclusive(graph, node); // Lấy tập các node tổ tiên
        ancestors.Remove(node.Id); // Loại bỏ chính node hiện tại ra khỏi danh sách
        // Lọc ra thứ tự thực thi của các node thượng nguồn
        var ordered = TopologicalSort(graph).Where(n => ancestors.Contains(n.Id)).ToList();
        ExecuteOrdered(graph, ordered, context); // Chạy các node thượng nguồn
        FeedInputs(graph, node); // Nạp dữ liệu vào các cổng Input của node hiện tại
    }

    // Phương thức cốt lõi thực thi các node đã được sắp xếp theo thứ tự
    private ExecutionResult ExecuteOrdered(
        FlowGraph graph, IReadOnlyList<FlowNode> order, IToolContext context,
        bool propagateSkip = true, bool disposeFirst = false)
    {
        if (disposeFirst) DisposeTracked(); // Giải phóng ảnh cũ nếu tham số cho phép

        var bad = new HashSet<string>(StringComparer.Ordinal); // Lưu Id của các node bị lỗi hoặc bị skip,dùng để lưu trữ
                                                               // danh sách Mã định danh (ID) của các node bị hỏng (Failed) hoặc bị bỏ qua (Skipped)
        // HashSet<T>: - Không chứa phần tử trùng lặp: Nếu bạn thêm một chuỗi đã tồn tại vào HashSet, nó sẽ tự động bỏ qua.
        //             - Tốc độ tra cứu cực nhanh O(1): Hàm kiểm tra sự tồn tại bad.Contains(...) hoạt động với thời gian hằng số O(1),
        //               nhanh hơn rất nhiều so với dùng List.Contains(...) phải duyệt qua từng phần tử O(n). 
        
        var results = new List<NodeExecutionResult>(order.Count); // Danh sách chứa kết quả chạy từng node
        var swTotal = Stopwatch.StartNew(); // Bắt đầu đo tổng thời gian chạy

        foreach (var node in order) // Duyệt qua từng node theo đúng thứ tự
        {
            context.CancellationToken.ThrowIfCancellationRequested(); // Bật cờ dừng nếu có yêu cầu hủy từ người dùng
            var tool = node.Tool;

            // Kiểm tra xem node thượng nguồn bắt buộc có bị lỗi/skip hay không
            if (propagateSkip && HasBadRequiredUpstream(graph, node, bad))
            {
                tool.State = ToolState.Skipped; // Đánh dấu trạng thái bị bỏ qua
                tool.ElapsedMs = 0;
                tool.ErrorMessage = "Bỏ qua: tool thượng nguồn lỗi.";
                bad.Add(node.Id); // Đưa node này vào danh sách bad
                results.Add(new NodeExecutionResult(node.Id, tool.DisplayName, tool.State, 0, tool.ErrorMessage));
                continue; // Chuyển sang node tiếp theo, không thực thi node này
            }

            FeedInputs(graph, node); // Nạp dữ liệu đầu vào cho node từ output của các node trước

            var sw = Stopwatch.StartNew(); // Đo thời gian thực thi của riêng node này
            try
            {
                tool.State = ToolState.Running; // Cập nhật trạng thái đang chạy
                tool.Execute(context); // Chạy thuật toán của Tool
                sw.Stop(); // Dừng đo thời gian
                tool.State = ToolState.Completed; // Đánh dấu đã hoàn thành
                tool.ElapsedMs = sw.ElapsedMilliseconds; // Lưu thời gian chạy
                tool.ErrorMessage = null; // Xóa thông báo lỗi (nếu có từ trước)
                TrackOutputs(node); // Lưu vết các ảnh output để quản lý vòng đời
            }
            catch (OperationCanceledException)
            {
                throw; // Nếu là tác vụ bị hủy thì ném ngoại lệ lên cấp cao hơn, không nuốt lỗi
            }
            catch (Exception ex)
            {
                sw.Stop(); // Dừng đo thời gian
                tool.State = ToolState.Failed; // Đánh dấu thất bại
                tool.ElapsedMs = sw.ElapsedMilliseconds;
                tool.ErrorMessage = ex.Message; // Lưu tin nhắn lỗi
                bad.Add(node.Id); // Thêm vào danh sách node hỏng
            }

            // Đưa kết quả của node vào danh sách tổng hợp
            results.Add(new NodeExecutionResult(node.Id, tool.DisplayName, tool.State, tool.ElapsedMs, tool.ErrorMessage));
        }

        swTotal.Stop(); // Dừng đo tổng thời gian
        return new ExecutionResult(results, swTotal.ElapsedMilliseconds); // Trả về kết quả tổng thể
    }

    /// <summary>Tập Id của <paramref name="target"/> và mọi node thượng nguồn (đệ quy).</summary>
    private static HashSet<string> AncestorsInclusive(FlowGraph graph, FlowNode target)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        stack.Push(target.Id); // Đẩy target node vào stack

        while (stack.Count > 0) // Duyệt DFS tìm ngược lên các node thượng nguồn
        {
            var id = stack.Pop();
            if (!set.Add(id)) continue; // Nếu node đã tồn tại trong set thì bỏ qua
            // Tìm tất cả các kết nối nối VÀO node hiện tại và đẩy node nguồn vào stack
            foreach (var conn in graph.Connections.Where(c => c.TargetNodeId == id))
                stack.Push(conn.SourceNodeId);
        }
        return set; // Trả về tập hợp các Id tổ tiên (bao gồm cả target)
    }

    // ---- Truyền dữ liệu output → input ----

    private void FeedInputs(FlowGraph graph, FlowNode node)
    {
        foreach (var input in node.Tool.Inputs) // Duyệt qua từng đầu vào (Input) của node
        {
            // ----- NHÁNH MỚI: Multi-Input Port - gom TẤT CẢ dây nối vào port này, không chỉ lấy dây đầu tiên -----
            if (input is IMultiInputPort multiInput)
            {
                var gathered = new Dictionary<string, object?>();

                foreach (var conn in graph.ConnectionsInto(node.Id, input.Name)) // TẤT CẢ dây, không FirstOrDefault()
                {
                    var source = graph.GetNode(conn.SourceNodeId);
                    var outPort = source?.Tool.FindOutput(conn.SourcePort);
                    if (outPort is null) continue; // Nguồn không tồn tại (node/port đã bị xoá) -> bỏ qua an toàn

                    var value = outPort.Value;

                    // Áp dụng đúng logic Clone-on-fanout như Input thường: nếu output này còn nối ra chỗ khác nữa và là ảnh
                    if (value is IVisionImage img && graph.ConnectionsFrom(source!.Id, outPort.Name).Count() > 1)
                    {
                        var clone = img.Clone();
                        _trackedImages.Add(clone);
                        value = clone;
                    }

                    // Key "TênNode.TênCổng" - dùng DisplayName của Tool nguồn (đổi sang Tool.Id nếu cần định danh
                    // duy nhất tuyệt đối khi có nhiều node cùng loại trên canvas)
                    string key = $"{source!.Tool.DisplayName}.{outPort.Name}";
                    gathered[key] = value;
                }

                multiInput.SetValues(gathered); // Nạp toàn bộ 1 lần - Tool con (VD AggregatorTool) đọc qua port.Values
                continue; // Xong nhánh Multi-Input, sang input tiếp theo
            }

            // ----- NHÁNH CŨ: Input thường - giữ NGUYÊN 100% logic gốc, không đổi gì -----
            var singleConn = graph.ConnectionsInto(node.Id, input.Name).FirstOrDefault();
            if (singleConn is null) continue; // Không có dây nối thì bỏ qua

            var singleSource = graph.GetNode(singleConn.SourceNodeId); // Lấy node nguồn
            var singleOutPort = singleSource?.Tool.FindOutput(singleConn.SourcePort); // Lấy cổng output nguồn
            if (singleOutPort is null) continue;

            var singleValue = singleOutPort.Value; // Lấy giá trị từ cổng output

            // Fan-out: Nếu output này nối ra nhiều input khác nhau và giá trị là Ảnh
            if (singleValue is IVisionImage singleImg && graph.ConnectionsFrom(singleSource!.Id, singleOutPort.Name).Count() > 1)
            {
                var clone = singleImg.Clone(); // Clone ra một bản sao mới để tránh xung đột chỉnh sửa
                _trackedImages.Add(clone); // Đưa ảnh clone vào danh sách theo dõi
                singleValue = clone; // Dùng ảnh clone làm dữ liệu truyền đi
            }

            input.Value = singleValue; // Gán giá trị vào input của node hiện tại
        }
    }

    // Lưu vết các ảnh sinh ra ở output của node để giải phóng bộ nhớ sau này
    private void TrackOutputs(FlowNode node)
    {
        foreach (var output in node.Tool.Outputs)
        {
            if (output.Value is IVisionImage img)
                _trackedImages.Add(img); // Thêm ảnh vào danh sách quản lý
        }
    }

    // Kiểm tra xem node có input BẮT BUỘC nào nối với một node bị lỗi/skip hay không
    private static bool HasBadRequiredUpstream(FlowGraph graph, FlowNode node, HashSet<string> bad)
    {
        foreach (var input in node.Tool.Inputs)
        {
            if (input.IsOptional) continue; // Nếu là input tùy chọn thì bỏ qua
            // Duyệt qua các kết nối đi vào input bắt buộc này
            foreach (var conn in graph.ConnectionsInto(node.Id, input.Name))
            {
                if (bad.Contains(conn.SourceNodeId)) return true; // Có nguồn hỏng -> Trả về true
            }
        }
        return false; // Không có vấn đề gì
    }

    // ---- Topological sort (Kahn) ----

    /// <summary>Sắp xếp topo. Ném <see cref="FlowExecutionException"/> nếu đồ thị có chu trình.</summary>
    public static IReadOnlyList<FlowNode> TopologicalSort(FlowGraph graph)
    {
        // Tạo Dictionary lưu bán bậc vào (số lượng dây nối đi vào) của từng node
        var indegree = graph.Nodes.ToDictionary(n => n.Id, _ => 0, StringComparer.Ordinal);
        // Tạo danh sách kề (node hiện tại chỉ đến những node nào)
        var adjacency = graph.Nodes.ToDictionary(n => n.Id, _ => new List<string>(), StringComparer.Ordinal);
        // Map tra cứu nhanh từ Id sang FlowNode
        var map = graph.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);

        // Xây dựng danh sách kề và tính bán bậc vào cho từng node
        foreach (var c in graph.Connections)
        {
            if (!indegree.ContainsKey(c.SourceNodeId) || !indegree.ContainsKey(c.TargetNodeId)) continue;
            adjacency[c.SourceNodeId].Add(c.TargetNodeId);
            indegree[c.TargetNodeId]++; // Tăng bán bậc vào của node đích
        }

        // Hàng chờ chứa các node có bán bậc vào bằng 0 (không phụ thuộc node nào)
        var queue = new Queue<string>(indegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var order = new List<FlowNode>(graph.Nodes.Count); // Danh sách kết quả sắp xếp

        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            order.Add(map[id]); // Thêm node vào thứ tự thực thi

            // Giảm bán bậc vào của các node kề sau nó
            foreach (var next in adjacency[id])
            {
                if (--indegree[next] == 0) queue.Enqueue(next); // Nếu bán bậc vào về 0 thì đưa vào queue
            }
        }

        // Nếu số node trong kết quả khác tổng số node ban đầu -> Đồ thị bị lặp vòng (Chu trình)
        if (order.Count != graph.Nodes.Count)
        {
            // Lấy ra danh sách các node nằm trong chu trình (có indegree > 0)
            var inCycle = indegree.Where(kv => kv.Value > 0).Select(kv => map[kv.Key].Tool.DisplayName);
            throw new FlowExecutionException(
                "Đồ thị flow có chu trình, không thể thực thi. Node liên quan: " + string.Join(", ", inCycle));
        }

        return order; // Trả về danh sách node đã xếp đúng thứ tự
    }

    // Giải phóng toàn bộ bộ nhớ ảnh đã thu thập trong quá trình chạy
    private void DisposeTracked()
    {
        foreach (var img in _trackedImages)
        {
            try
            {
                if (!img.IsDisposed) img.Dispose(); // Gọi Dispose nếu ảnh chưa giải phóng
            }
            catch
            {
                // nuốt lỗi dispose để không che lỗi chính
            }
        }
        _trackedImages.Clear(); // Xóa sạch danh sách tracking
    }

    // Thực thi giải phóng tài nguyên khi đối tượng FlowExecutor bị Dispose
    public void Dispose() => DisposeTracked();
}