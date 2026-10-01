// ==================== Vai trò chính:                Chạy flow KHÔNG cần UI: nạp file flow JSON vào một graph RIÊNG, chạy tuần tự từng lần kiểm tra, trả về InspectionResult
// ==================== Thành phần / Class tiêu biểu: InspectionService
// ==================== Phụ thuộc vào:                IFlowRepository, FlowExecutor, FlowGraph, IInspectionResultSource, InspectionResult
// ==================== Pattern / Kỹ thuật nổi bật:   Application Service + SemaphoreSlim(1,1) làm "cổng" tuần tự hoá (mutual exclusion) + Fail-safe (mọi lỗi -> Error, không bao giờ ra OK giả)
//
// VỊ TRÍ ĐẶT FILE: project Engine, thư mục Runtime/ (cùng nơi với InspectionResult.cs) — namespace VisionFlow.Engine.Runtime.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VisionFlow.Core.Models;
using VisionFlow.Core.Tools;
using VisionFlow.Engine.Execution;
using VisionFlow.Engine.Graph;
using VisionFlow.Engine.Persistence;

namespace VisionFlow.Engine.Runtime;

/// <summary>
/// Dịch vụ chạy kiểm tra "không giao diện". Khác nút ▶ Run của editor ở 3 điểm:
/// <list type="number">
/// <item>Dùng graph RIÊNG nạp từ file JSON (không dùng chung Tool với canvas), nên người dùng kéo thả/sửa tham số trên editor
/// trong lúc máy đang chạy sẽ không làm hỏng lần kiểm tra.</item>
/// <item>Tuần tự hoá: tại một thời điểm chỉ 1 lần kiểm tra chạy (Tool giữ dữ liệu trong port nên KHÔNG chạy song song trên cùng graph được).
/// Các yêu cầu đến sau sẽ chờ đến lượt.</item>
/// <item>Trả về <see cref="InspectionResult"/> gọn (OK/NG + số đo + lỗi) thay vì bắt người gọi đi đọc từng node.</item>
/// </list>
/// Đây là lớp mà bước sau (PLC handshake) sẽ gọi khi PLC bật trigger.
/// </summary>
public sealed class InspectionService : IDisposable
{
    private readonly IFlowRepository _repository;
    private readonly FlowExecutor _executor = new();           // Executor riêng: tự giải phóng ảnh của lần chạy trước ở đầu lần chạy sau
    private readonly SemaphoreSlim _gate = new(1, 1);          // Cổng tuần tự hoá: nạp flow và chạy kiểm tra không bao giờ chồng nhau

    private FlowGraph? _graph;                                 // Graph đang dùng để chạy (null = chưa nạp flow)
    private FlowNode? _publisherNode;                          // Node Result Publisher duy nhất trong graph
    private long _sequence;                                    // Bộ đếm lần kiểm tra
    private bool _disposed;

    public InspectionService(IFlowRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    /// <summary>Đường dẫn file flow đang nạp (null nếu chưa nạp).</summary>
    public string? FlowPath { get; private set; }

    /// <summary>true nếu đã nạp flow hợp lệ và sẵn sàng chạy.</summary>
    public bool IsLoaded => _graph is not null;

    /// <summary>
    /// Bắn ra sau MỖI lần kiểm tra (kể cả lần lỗi). Được gọi trên thread nền — handler nào cập nhật UI phải tự Dispatcher.Invoke.
    /// Exception trong handler bị nuốt để không làm hỏng vòng kiểm tra.
    /// </summary>
    public event Action<InspectionResult>? InspectionCompleted;

    // ====================================================================
    // NẠP FLOW
    // ====================================================================

    /// <summary>
    /// Nạp flow từ file JSON và kiểm tra flow có dùng được cho chế độ máy không (phải có đúng 1 node Result Publisher).
    /// Nạp lại lần nữa để thay flow (ví dụ đổi recipe); nếu nạp lỗi thì flow cũ vẫn giữ nguyên, không bị mất.
    /// </summary>
    public async Task LoadFlowAsync(string path, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Đường dẫn flow không được rỗng.", nameof(path));

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Nạp file trên thread nền (đọc đĩa + dựng Tool bằng reflection) để không đơ UI
            FlowGraph graph = await Task.Run(() => _repository.Load(path), ct).ConfigureAwait(false);

            var publishers = graph.Nodes.Where(n => n.Tool is IInspectionResultSource).ToList();

            if (publishers.Count == 0)
                throw new InvalidOperationException(
                    "Flow chưa có node 'Result Publisher'. Hãy thêm node này ở cuối flow, nối cổng Pass từ Compare/Logic Gate rồi lưu lại.");
            if (publishers.Count > 1)
                throw new InvalidOperationException(
                    $"Flow có {publishers.Count} node 'Result Publisher'. Chế độ máy chỉ cho phép đúng 1 để kết quả không mơ hồ.");

            // Chỉ gán khi mọi kiểm tra đã qua => nạp lỗi không làm mất flow đang chạy tốt
            _graph = graph;
            _publisherNode = publishers[0];
            FlowPath = path;
            _sequence = 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ====================================================================
    // CHẠY KIỂM TRA
    // ====================================================================

    /// <summary>
    /// Chạy flow đúng một lần và trả về kết quả. Nếu đang có lần kiểm tra khác chạy thì chờ đến lượt.
    /// Mọi lỗi của flow (camera, node, đồ thị) được đóng gói vào <see cref="InspectionResult.ErrorMessage"/> — hàm KHÔNG ném exception
    /// vì lỗi flow. Chỉ ném khi: chưa nạp flow, đã Dispose, hoặc người gọi huỷ (<see cref="OperationCanceledException"/>).
    /// </summary>
    public async Task<InspectionResult> RunOnceAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();

        InspectionResult result;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var graph = _graph;
            var publisherNode = _publisherNode;
            if (graph is null || publisherNode is null)
                throw new InvalidOperationException("InspectionService chưa nạp flow. Hãy gọi LoadFlowAsync() trước.");

            long sequence = ++_sequence;

            // Chạy flow trên thread nền để không chặn thread gọi (UI hoặc vòng lặp PLC)
            result = await Task.Run(() => Execute(graph, publisherNode, sequence, ct), ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        // Bắn sự kiện NGOÀI vùng khoá: handler chậm không được làm nghẽn lần kiểm tra kế tiếp
        RaiseCompleted(result);
        return result;
    }

    /// <summary>Thực thi thật sự (chạy trên thread nền, đã giữ khoá).</summary>
    private InspectionResult Execute(FlowGraph graph, FlowNode publisherNode, long sequence, CancellationToken ct)
    {
        var source = (IInspectionResultSource)publisherNode.Tool;

        // Xoá kết quả cũ TRƯỚC khi chạy để không bao giờ đọc nhầm số liệu của sản phẩm trước
        source.ResetSnapshot();

        var logs = new List<string>();
        var context = new ToolContext(graph.PixelSize, ct, msg => logs.Add(msg));

        DateTime timestamp = DateTime.Now;
        var stopwatch = Stopwatch.StartNew();

        ExecutionResult? execution = null;
        string? error = null;

        try
        {
            execution = _executor.Run(graph, context);
        }
        catch (OperationCanceledException)
        {
            throw; // Huỷ theo yêu cầu người gọi: không phải lỗi kiểm tra
        }
        catch (Exception ex)
        {
            // FlowExecutor có thể ném ra ngoài với: đồ thị có vòng lặp (FlowExecutionException), nối dây sai kiểu dữ liệu (ArgumentException)...
            error = $"{ex.GetType().Name}: {ex.Message}";
        }

        stopwatch.Stop();

        IReadOnlyList<NodeExecutionResult> nodes = execution?.Nodes ?? Array.Empty<NodeExecutionResult>();

        // Bất kỳ node nào lỗi => Error (kể cả nhánh phụ). Chủ ý CHẶT: thà báo lỗi còn hơn gửi "OK" khi một phần flow đã hỏng.
        if (error is null)
        {
            var failed = nodes.Where(n => n.State == ToolState.Failed).ToList();
            if (failed.Count > 0)
            {
                error = $"Node '{failed[0].DisplayName}' lỗi: {failed[0].Error}";
                if (failed.Count > 1) error += $" (và {failed.Count - 1} node lỗi khác)";
            }
        }

        InspectionSnapshot? snapshot = error is null ? source.Snapshot : null;
        if (error is null && snapshot is null)
            error = "Result Publisher không tạo ra kết quả (node không được thực thi).";

        Judge judge = error is not null
            ? Judge.None
            : (snapshot!.IsOk ? Judge.OK : Judge.NG);

        return new InspectionResult(
            Sequence: sequence,
            Timestamp: timestamp,
            Judge: judge,
            Values: snapshot?.Values ?? Array.Empty<MeasuredValue>(),
            CycleMs: stopwatch.ElapsedMilliseconds,
            ErrorMessage: error,
            Nodes: nodes,
            Logs: logs);
    }

    private void RaiseCompleted(InspectionResult result)
    {
        try
        {
            InspectionCompleted?.Invoke(result);
        }
        catch
        {
            // Handler của người dùng lỗi không được phép làm hỏng dịch vụ kiểm tra
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(InspectionService));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _executor.Dispose();   // Giải phóng ảnh còn giữ từ lần chạy cuối
        _gate.Dispose();
    }
}