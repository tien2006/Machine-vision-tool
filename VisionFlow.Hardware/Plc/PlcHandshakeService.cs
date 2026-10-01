// ==================== Vai trò chính:                Vòng lặp bắt tay (handshake) với PLC: đọc trigger -> chạy kiểm tra -> ghi kết quả -> chờ PLC xác nhận; giữ heartbeat; tự nối lại khi mất kết nối
// ==================== Thành phần / Class tiêu biểu: PlcHandshakeService, PlcHandshakeOptions, HandshakeState, HandshakeCycle, HandshakeStats
// ==================== Phụ thuộc vào:                IPlcLink, IInspectionRunner, PlcRegisterMap
// ==================== Pattern / Kỹ thuật nổi bật:   Máy trạng thái (Ready -> Busy -> WaitingAck) + "ghi Status SAU CÙNG" (commit flag) + exponential backoff có chặn tràn số
//
// VỊ TRÍ ĐẶT FILE: thư mục Plc/ — namespace VisionFlow.Hardware.Plc.
//
// GIAO THỨC BẮT TAY (PLC là bên chủ động, PC chỉ đáp ứng):
//
//   PLC                                   PC (VisionFlow)
//   ---                                   ---------------
//   đặt ProductId, rồi Command = 1  --->  thấy Command = 1 khi đang Ready
//                                         Status = Busy, Result = 0            (báo "đã nhận, đang làm")
//                                         chụp ảnh + chạy flow
//                                         ghi Values -> ghi Result/Error/Echo/Cycle -> ghi Status = Done (hoặc Error) SAU CÙNG
//   thấy Status = Done/Error        <---
//   đọc Result/Values (hoặc ErrorCode)
//   Command = 0                     --->  thấy Command = 0 -> Status = Ready
//
// QUY TẮC:
//   1. Status được ghi CUỐI CÙNG vì nó là cờ "dữ liệu đã sẵn sàng": PLC không bao giờ thấy Done mà Result còn cũ.
//   2. PC không chạy lại khi Command vẫn = 1 sau lúc Done: phải thấy PLC xoá về 0 (rồi mới tới trigger kế tiếp).
//   3. Mất kết nối giữa chừng: bỏ kết quả chưa gửi, nối lại, đồng bộ về Ready. Nếu PLC còn giữ Command = 1 thì kiểm tra lại từ đầu
//      (sản phẩm còn nằm đó vì PLC chưa nhận được kết quả). Không bao giờ "đoán" kết quả.
//   4. Lỗi kiểm tra (camera, node...) báo bằng Status = Error + ErrorCode, TUYỆT ĐỐI không đổi thành NG.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace VisionFlow.Hardware.Plc;

/// <summary>Cấu hình handshake, đọc từ mục "PlcHandshake" của appsettings.json. Mọi giá trị có mặc định hợp lý.</summary>
public sealed class PlcHandshakeOptions
{
    /// <summary>Địa chỉ gốc khối Command (3 word: Command, Model, ProductId).</summary>
    public string CommandBase { get; set; } = "D1000";

    /// <summary>Địa chỉ gốc khối Status (6 word: Heartbeat, Status, Result, ErrorCode, EchoProductId, CycleMs).</summary>
    public string StatusBase { get; set; } = "D1010";

    /// <summary>Địa chỉ gốc khối số đo (12 word = 6 số đo x 2 word).</summary>
    public string ValuesBase { get; set; } = "D1020";

    /// <summary>Chu kỳ đọc trigger (ms). Nhỏ hơn = phản ứng nhanh hơn nhưng tốn băng thông mạng.</summary>
    public int PollIntervalMs { get; set; } = 20;

    /// <summary>Chu kỳ tăng heartbeat (ms). PLC nên chịu được ít nhất 3-4 chu kỳ này không đổi.</summary>
    public int HeartbeatIntervalMs { get; set; } = 500;

    /// <summary>Thời gian tối đa cho một lần kiểm tra (ms); quá hạn báo ErrorCode = InspectionTimeout.</summary>
    public int InspectionTimeoutMs { get; set; } = 10000;

    /// <summary>Sau Done/Error mà PLC chưa xoá Command quá thời gian này thì ghi cảnh báo vào log (ms).</summary>
    public int AckWarningMs { get; set; } = 5000;

    /// <summary>Thời gian chờ đầu tiên trước khi nối lại (ms); mỗi lần thất bại liên tiếp gấp đôi.</summary>
    public int ReconnectInitialMs { get; set; } = 1000;

    /// <summary>Thời gian chờ tối đa giữa hai lần nối lại (ms).</summary>
    public int ReconnectMaxMs { get; set; } = 10000;

    /// <summary>Hệ số nhân số đo trước khi ghi thành số nguyên 32-bit (1000 => 12.503 mm ghi thành 12503).</summary>
    public int ValueScale { get; set; } = 1000;

    /// <summary>File flow nạp khi bắt đầu handshake. Để trống = dùng flow đã nạp bằng nút "Load Inspection Flow".</summary>
    public string FlowFile { get; set; } = "";

    /// <summary>Chu kỳ tự bật trigger của "chương trình ladder" GIẢ LẬP (ms) — chỉ dùng khi PlcLink.Mode = Simulated.</summary>
    public int SimulatedTriggerIntervalMs { get; set; } = 2000;
}

/// <summary>Trạng thái của dịch vụ handshake (phía PC).</summary>
public enum HandshakeState
{
    Stopped,
    /// <summary>Đang nối PLC lần đầu.</summary>
    Connecting,
    /// <summary>Sẵn sàng nhận trigger.</summary>
    Ready,
    /// <summary>Đang kiểm tra.</summary>
    Busy,
    /// <summary>Đã ghi kết quả, chờ PLC xoá Command.</summary>
    WaitingAck,
    /// <summary>Mất kết nối, đang chờ để nối lại.</summary>
    Reconnecting
}

/// <summary>Một chu kỳ trigger đã hoàn tất và đã ghi xuống PLC.</summary>
public sealed record HandshakeCycle(
    ushort ProductId,
    InspectionVerdict Verdict,
    ushort ErrorCode,
    string? ErrorMessage,
    IReadOnlyList<OutcomeValue> Values,
    long InspectionMs,
    long TotalMs);

/// <summary>Số liệu tích luỹ kể từ khi tạo dịch vụ.</summary>
public readonly record struct HandshakeStats(int Triggers, int Ok, int Ng, int Errors, int LinkFailures);

/// <summary>Dịch vụ handshake với PLC. Một instance ứng với một PLC; Start()/StopAsync() an toàn khi gọi lặp.</summary>
public sealed class PlcHandshakeService : IDisposable
{
    private readonly IPlcLink _link;
    private readonly IInspectionRunner _runner;
    private readonly PlcRegisterMap _map;
    private readonly PlcHandshakeOptions _options;

    private readonly object _lifeLock = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;

    private volatile HandshakeState _state = HandshakeState.Stopped;
    private ushort _heartbeat;                 // Bộ đếm heartbeat (quay vòng 0..65535)
    private ushort _publishedStatus;           // Status đã công bố xuống PLC (dùng lại khi chỉ làm mới heartbeat)

    // ---- Lần kiểm tra đang chạy (chỉ đụng tới trong vòng lặp chính nên không cần khoá) ----
    private Task<InspectionOutcome>? _inspectionTask;
    private CancellationTokenSource? _inspectionCts;
    private ushort _preErrorCode;              // Lỗi biết trước khi chạy (ví dụ chưa nạp flow)
    private ushort _currentProductId;
    private Stopwatch _cycleWatch = new();
    private Stopwatch _ackWatch = new();
    private bool _ackWarned;
    private ushort _lastUnknownCommand;

    // ---- Số liệu ----
    private int _triggers, _ok, _ng, _errors, _linkFailures;

    public PlcHandshakeService(IPlcLink link, IInspectionRunner runner, PlcRegisterMap map, PlcHandshakeOptions options)
    {
        _link = link ?? throw new ArgumentNullException(nameof(link));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _map = map ?? throw new ArgumentNullException(nameof(map));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public HandshakeState State => _state;
    public bool IsRunning => _loop is { IsCompleted: false };
    public PlcRegisterMap Map => _map;

    public HandshakeStats Stats => new(
        Volatile.Read(ref _triggers), Volatile.Read(ref _ok), Volatile.Read(ref _ng),
        Volatile.Read(ref _errors), Volatile.Read(ref _linkFailures));

    /// <summary>Bắn khi State đổi. Gọi trên thread nền — handler cập nhật UI phải tự chuyển về UI thread.</summary>
    public event Action<HandshakeState>? StateChanged;

    /// <summary>Bắn sau khi một chu kỳ trigger đã ghi xong kết quả xuống PLC. Gọi trên thread nền.</summary>
    public event Action<HandshakeCycle>? CycleCompleted;

    /// <summary>Dòng nhật ký (kết nối, trigger, kết quả, lỗi). Gọi trên thread nền.</summary>
    public event Action<string>? Log;

    // ====================================================================
    // VÒNG ĐỜI
    // ====================================================================

    /// <summary>Bắt đầu chạy nền. Gọi lại khi đang chạy thì không làm gì.</summary>
    public void Start()
    {
        lock (_lifeLock)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PlcHandshakeService));
            if (_loop is { IsCompleted: false }) return;

            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _loop = Task.Run(() => RunAsync(ct));
        }
    }

    /// <summary>Dừng vòng lặp, chờ nó kết thúc (đã ghi Status = Offline xuống PLC nếu còn kết nối).</summary>
    public async Task StopAsync()
    {
        Task? loop;
        CancellationTokenSource? cts;
        lock (_lifeLock)
        {
            loop = _loop;
            cts = _cts;
        }
        if (loop is null || cts is null) return;

        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        try { await loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        lock (_lifeLock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        try { _cts?.Cancel(); } catch { }
        try { _loop?.Wait(2000); } catch { }
        _cts?.Dispose();
    }

    // ====================================================================
    // VÒNG LẶP CHÍNH: nối -> đồng bộ -> phục vụ; lỗi thì chờ (backoff) rồi làm lại
    // ====================================================================

    private async Task RunAsync(CancellationToken ct)
    {
        int attempt = 0;
        try
        {
            await LoadFlowIfConfiguredAsync(ct).ConfigureAwait(false);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    SetState(HandshakeState.Connecting);
                    await _link.ConnectAsync(ct).ConfigureAwait(false);
                    await SynchronizeAsync(ct).ConfigureAwait(false);

                    attempt = 0; // Nối + đồng bộ thành công => thời gian chờ nối lại quay về ban đầu
                    Emit($"Đã kết nối PLC, sẵn sàng nhận trigger (Command = {_map.Command})");

                    await ServeAsync(ct).ConfigureAwait(false); // Chỉ thoát bằng exception (lỗi hoặc dừng)
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    AbandonInspection();
                    Interlocked.Increment(ref _linkFailures);

                    int delay = ComputeBackoffMs(attempt, _options.ReconnectInitialMs, _options.ReconnectMaxMs);
                    attempt++;

                    SetState(HandshakeState.Reconnecting);
                    Emit($"{Describe(ex)} — thử lại sau {delay} ms (lần {attempt})");

                    try { await Task.Delay(delay, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }
        finally
        {
            AbandonInspection();
            await TryWriteOfflineAsync().ConfigureAwait(false);
            SetState(HandshakeState.Stopped);
        }
    }

    /// <summary>
    /// Thời gian chờ trước lần nối lại thứ <paramref name="attempt"/> (bắt đầu từ 0): initial, 2*initial, 4*initial... tối đa max.
    /// Tính bằng vòng lặp có chặn trên nên KHÔNG BAO GIỜ tràn số (công thức 1000 * 2^n trong tài liệu Buổi 118 tràn sau ~22 lần).
    /// </summary>
    public static int ComputeBackoffMs(int attempt, int initialMs, int maxMs)
    {
        if (initialMs < 1) initialMs = 1;
        if (maxMs < initialMs) maxMs = initialMs;

        long delay = initialMs;
        for (int i = 0; i < attempt && delay < maxMs; i++)
            delay *= 2;

        return (int)Math.Min(delay, maxMs);
    }

    private async Task LoadFlowIfConfiguredAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.FlowFile)) return;

        try
        {
            await _runner.LoadAsync(_options.FlowFile, ct).ConfigureAwait(false);
            Emit($"Đã nạp flow: {_options.FlowFile}");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Không chặn handshake: trigger đến sẽ nhận ErrorCode = FlowNotLoaded để PLC biết rõ nguyên nhân
            Emit($"KHÔNG nạp được flow '{_options.FlowFile}': {ex.Message}");
        }
    }

    /// <summary>Sau khi (nối lại) kết nối: đọc thử PLC rồi đưa Status về Ready, xoá kết quả cũ.</summary>
    private async Task SynchronizeAsync(CancellationToken ct)
    {
        var cmd = await ReadCommandBlockAsync(ct).ConfigureAwait(false);
        await WriteStatusBlockAsync(HandshakeStatus.Ready, HandshakeResult.None, HandshakeError.None, cmd.ProductId, 0, ct).ConfigureAwait(false);
        SetState(HandshakeState.Ready);
    }

    private async Task ServeAsync(CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        long nextHeartbeatAt = clock.ElapsedMilliseconds + _options.HeartbeatIntervalMs;
        int poll = Math.Max(1, _options.PollIntervalMs);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            long iterationStart = clock.ElapsedMilliseconds;

            // Heartbeat làm mới định kỳ, kể cả khi đang Busy (kiểm tra lâu vẫn không làm PLC tưởng PC chết)
            if (iterationStart >= nextHeartbeatAt)
            {
                await WriteHeartbeatAsync(ct).ConfigureAwait(false);
                nextHeartbeatAt = clock.ElapsedMilliseconds + _options.HeartbeatIntervalMs;
            }

            switch (_state)
            {
                case HandshakeState.Ready:
                    await HandleReadyAsync(ct).ConfigureAwait(false);
                    break;
                case HandshakeState.Busy:
                    await HandleBusyAsync(ct).ConfigureAwait(false);
                    break;
                case HandshakeState.WaitingAck:
                    await HandleWaitingAckAsync(ct).ConfigureAwait(false);
                    break;
            }

            long spent = clock.ElapsedMilliseconds - iterationStart;
            int wait = (int)Math.Max(1, poll - spent);
            await Task.Delay(wait, ct).ConfigureAwait(false);
        }
    }

    // ====================================================================
    // TỪNG TRẠNG THÁI
    // ====================================================================

    private async Task HandleReadyAsync(CancellationToken ct)
    {
        var cmd = await ReadCommandBlockAsync(ct).ConfigureAwait(false);

        if (cmd.Command == HandshakeCommand.Trigger)
        {
            await BeginInspectionAsync(cmd.ProductId, ct).ConfigureAwait(false);
        }
        else if (cmd.Command != HandshakeCommand.Idle && cmd.Command != _lastUnknownCommand)
        {
            _lastUnknownCommand = cmd.Command; // Chỉ cảnh báo một lần cho mỗi giá trị lạ, tránh ngập log
            Emit($"CẢNH BÁO: Command = {cmd.Command} không được hỗ trợ (chỉ 0 = rảnh, 1 = trigger) — bỏ qua");
        }
    }

    private async Task BeginInspectionAsync(ushort productId, CancellationToken ct)
    {
        Interlocked.Increment(ref _triggers);
        _currentProductId = productId;
        _cycleWatch = Stopwatch.StartNew();
        _preErrorCode = HandshakeError.None;
        Emit($"TRIGGER nhận được (ProductId = {productId}) — bắt đầu kiểm tra");

        // Khởi chạy kiểm tra TRƯỚC khi ghi Busy để camera bắt đầu chụp ngay, không đợi thêm một vòng mạng
        if (!_runner.IsReady)
        {
            _preErrorCode = HandshakeError.FlowNotLoaded;
            _inspectionTask = Task.FromResult(new InspectionOutcome(InspectionVerdict.Error, Array.Empty<OutcomeValue>(), 0, "Chưa nạp flow kiểm tra"));
        }
        else
        {
            _inspectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _inspectionCts.CancelAfter(_options.InspectionTimeoutMs);
            var token = _inspectionCts.Token;
            // Task.Run: runner có thể chạy đồng bộ một đoạn dài; vòng poll/heartbeat không được bị chặn
            _inspectionTask = Task.Run(() => _runner.RunAsync(token), CancellationToken.None);
        }

        await WriteStatusBlockAsync(HandshakeStatus.Busy, HandshakeResult.None, HandshakeError.None, productId, 0, ct).ConfigureAwait(false);
        SetState(HandshakeState.Busy);
    }

    private async Task HandleBusyAsync(CancellationToken ct)
    {
        var task = _inspectionTask;
        if (task is null || !task.IsCompleted) return; // Chưa xong: vòng lặp tiếp tục làm heartbeat

        await PublishAsync(task, ct).ConfigureAwait(false);
    }

    /// <summary>Đổi kết quả thành các thanh ghi và ghi xuống PLC theo thứ tự: số đo -> Result/Error/Echo/Cycle -> Status (cuối cùng).</summary>
    private async Task PublishAsync(Task<InspectionOutcome> task, CancellationToken ct)
    {
        InspectionOutcome? outcome = null;
        ushort errorCode = _preErrorCode;
        string? message = null;

        if (errorCode == HandshakeError.None)
        {
            try
            {
                outcome = await task.ConfigureAwait(false); // Đã hoàn tất nên không chờ thêm
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                errorCode = HandshakeError.InspectionTimeout;
                message = $"Kiểm tra vượt {_options.InspectionTimeoutMs} ms";
            }
            catch (OperationCanceledException)
            {
                throw; // Đang dừng dịch vụ
            }
            catch (Exception ex)
            {
                errorCode = HandshakeError.InternalError;
                message = $"{ex.GetType().Name}: {ex.Message}";
            }
        }
        else
        {
            message = "Chưa nạp flow kiểm tra";
        }

        var valueWords = new ushort[PlcRegisterMap.ValuesBlockLength]; // Mặc định 0: slot không dùng, hoặc khi lỗi
        if (errorCode == HandshakeError.None && outcome is not null)
        {
            if (outcome.Verdict == InspectionVerdict.Error)
            {
                errorCode = HandshakeError.InspectionFailed;
                message = outcome.ErrorMessage;
            }
            else if (!TryScaleValues(outcome.Values, valueWords, out message))
            {
                errorCode = HandshakeError.ValueOutOfRange;
                Array.Clear(valueWords, 0, valueWords.Length);
            }
        }

        bool failed = errorCode != HandshakeError.None;
        ushort status = failed ? HandshakeStatus.Error : HandshakeStatus.Done;
        ushort result = failed ? HandshakeResult.None : (outcome!.Verdict == InspectionVerdict.Ok ? HandshakeResult.Ok : HandshakeResult.Ng);
        long inspectionMs = outcome?.CycleMs ?? _cycleWatch.ElapsedMilliseconds;
        ushort cycleWord = (ushort)Math.Min(Math.Max(inspectionMs, 0), ushort.MaxValue);

        // (1) Số đo -> (2) Result/ErrorCode/Echo/Cycle -> (3) Status. Status ghi CUỐI: PLC thấy Done thì mọi thứ trước đó đã có mặt.
        await _link.WriteWordsAsync(_map.ValuesBase, valueWords, ct).ConfigureAwait(false);
        await _link.WriteWordsAsync(_map.Result, new[] { result, errorCode, _currentProductId, cycleWord }, ct).ConfigureAwait(false);
        await WriteHeartbeatAndStatusAsync(status, ct).ConfigureAwait(false);

        // Chỉ tính số liệu SAU KHI ghi thành công (ghi lỗi giữa chừng => bị bỏ và làm lại từ đầu sau khi nối lại)
        if (failed) Interlocked.Increment(ref _errors);
        else if (outcome!.Verdict == InspectionVerdict.Ok) Interlocked.Increment(ref _ok);
        else Interlocked.Increment(ref _ng);

        var cycle = new HandshakeCycle(_currentProductId,
            failed ? InspectionVerdict.Error : outcome!.Verdict,
            errorCode, message,
            outcome?.Values ?? Array.Empty<OutcomeValue>(),
            inspectionMs, _cycleWatch.ElapsedMilliseconds);

        ReleaseInspection();
        _ackWatch = Stopwatch.StartNew();
        _ackWarned = false;
        SetState(HandshakeState.WaitingAck);

        Emit(failed
            ? $"KẾT QUẢ: LỖI (mã {errorCode}) — {message} | tổng {cycle.TotalMs} ms"
            : $"KẾT QUẢ: {(outcome!.Verdict == InspectionVerdict.Ok ? "OK" : "NG")} | kiểm tra {inspectionMs} ms, tổng {cycle.TotalMs} ms | đã ghi xuống PLC");

        try { CycleCompleted?.Invoke(cycle); } catch { /* handler lỗi không được làm hỏng handshake */ }
    }

    private async Task HandleWaitingAckAsync(CancellationToken ct)
    {
        var cmd = await ReadCommandBlockAsync(ct).ConfigureAwait(false);

        if (cmd.Command == HandshakeCommand.Idle)
        {
            await WriteHeartbeatAndStatusAsync(HandshakeStatus.Ready, ct).ConfigureAwait(false);
            SetState(HandshakeState.Ready);
            return;
        }

        if (!_ackWarned && _ackWatch.ElapsedMilliseconds >= _options.AckWarningMs)
        {
            _ackWarned = true;
            Emit($"CẢNH BÁO: PLC chưa xoá Command sau {_options.AckWarningMs} ms — PC vẫn chờ, không chạy lại (kiểm tra chương trình PLC)");
        }
    }

    // ====================================================================
    // ĐỌC/GHI THANH GHI
    // ====================================================================

    private readonly record struct CommandBlock(ushort Command, ushort Model, ushort ProductId);

    private async Task<CommandBlock> ReadCommandBlockAsync(CancellationToken ct)
    {
        var w = await _link.ReadWordsAsync(_map.Command, PlcRegisterMap.CommandBlockLength, ct).ConfigureAwait(false);
        return new CommandBlock(w[0], w[1], w[2]);
    }

    /// <summary>Ghi cả khối Status (6 word) trong MỘT lệnh: PLC thấy khối nhất quán, không thấy nửa cũ nửa mới.</summary>
    private async Task WriteStatusBlockAsync(ushort status, ushort result, ushort errorCode, ushort echoProductId, ushort cycleMs, CancellationToken ct)
    {
        await _link.WriteWordsAsync(_map.StatusBase, new[] { NextHeartbeat(), status, result, errorCode, echoProductId, cycleMs }, ct).ConfigureAwait(false);
        _publishedStatus = status;
    }

    /// <summary>Ghi Heartbeat + Status (2 word liên tiếp) trong một lệnh.</summary>
    private async Task WriteHeartbeatAndStatusAsync(ushort status, CancellationToken ct)
    {
        await _link.WriteWordsAsync(_map.Heartbeat, new[] { NextHeartbeat(), status }, ct).ConfigureAwait(false);
        _publishedStatus = status;
    }

    private Task WriteHeartbeatAsync(CancellationToken ct) => WriteHeartbeatAndStatusAsync(_publishedStatus, ct);

    private ushort NextHeartbeat() => unchecked(++_heartbeat);

    private bool TryScaleValues(IReadOnlyList<OutcomeValue> values, ushort[] words, out string? message)
    {
        message = null;
        foreach (var v in values)
        {
            if (v.Slot < 1 || v.Slot > PlcRegisterMap.MaxSlots) continue; // Slot ngoài phạm vi bị bỏ qua

            double scaled = Math.Round(v.Value * _options.ValueScale, MidpointRounding.AwayFromZero);
            if (double.IsNaN(scaled) || scaled < int.MinValue || scaled > int.MaxValue)
            {
                message = $"Số đo '{v.Name}' = {v.Value} vượt phạm vi 32-bit sau khi nhân {_options.ValueScale}";
                return false;
            }

            uint raw = unchecked((uint)(int)scaled);
            int index = 2 * (v.Slot - 1);
            words[index] = (ushort)(raw & 0xFFFF);   // word thấp ở địa chỉ thấp (quy ước DINT của Mitsubishi)
            words[index + 1] = (ushort)(raw >> 16);
        }
        return true;
    }

    /// <summary>
    /// Lúc dừng: cố ghi Status = Offline để PLC biết ngay PC đã thoát (không đợi heartbeat hết hạn). Lỗi thì bỏ qua.
    /// Khi dừng, thao tác PLC đang dở bị huỷ và đường truyền bị đóng (quy tắc "huỷ giữa chừng thì bỏ kết nối"),
    /// nên ở đây có thể phải nối lại một lần (tối đa 1 giây). Bỏ qua nếu PLC đã được biết là mất kết nối.
    /// </summary>
    private async Task TryWriteOfflineAsync()
    {
        if (_state == HandshakeState.Reconnecting) return; // PLC đang mất kết nối: không cố nối thêm làm chậm việc dừng

        try
        {
            using var cts = new CancellationTokenSource(1000);
            if (!_link.IsConnected)
                await _link.ConnectAsync(cts.Token).ConfigureAwait(false);

            await _link.WriteWordsAsync(_map.Heartbeat, new[] { NextHeartbeat(), HandshakeStatus.Offline }, cts.Token).ConfigureAwait(false);
        }
        catch
        {
            // PLC không nhận được thì heartbeat đứng yên cũng đủ báo cho PLC
        }
    }

    // ====================================================================
    // TIỆN ÍCH
    // ====================================================================

    /// <summary>Huỷ và bỏ lần kiểm tra đang dở (mất kết nối/đang dừng). Kết quả của nó bị vứt: PLC chưa nhận được nên không được coi là đã xong.</summary>
    private void AbandonInspection()
    {
        var task = _inspectionTask;
        try { _inspectionCts?.Cancel(); } catch { }

        // Đọc Exception để tránh "unobserved task exception" khi lần kiểm tra bị bỏ đó kết thúc bằng lỗi/huỷ
        task?.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);

        ReleaseInspection();
    }

    private void ReleaseInspection()
    {
        try { _inspectionCts?.Dispose(); } catch { }
        _inspectionCts = null;
        _inspectionTask = null;
    }

    private static string Describe(Exception ex) => ex switch
    {
        PlcProtocolException p => $"PLC báo lỗi 0x{p.EndCode:X4} (kiểm tra địa chỉ thanh ghi trong cấu hình PlcHandshake): {p.Message}",
        PlcLinkException => $"Đường truyền PLC lỗi: {ex.Message}",
        _ => $"Lỗi không lường trước ({ex.GetType().Name}): {ex.Message}"
    };

    private void SetState(HandshakeState state)
    {
        if (_state == state) return;
        _state = state;
        try { StateChanged?.Invoke(state); } catch { }
    }

    private void Emit(string message)
    {
        try { Log?.Invoke($"{DateTime.Now:HH:mm:ss.fff} {message}"); } catch { }
    }
}