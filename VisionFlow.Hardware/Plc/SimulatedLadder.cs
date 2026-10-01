// ==================== Vai trò chính:                "Chương trình ladder" GIẢ LẬP của PLC: tự bật trigger, chờ VisionFlow trả kết quả, đọc kết quả, xác nhận, và giám sát heartbeat — để thử cả chuỗi handshake mà không cần PLC thật
// ==================== Thành phần / Class tiêu biểu: SimulatedLadder, LadderCycle
// ==================== Phụ thuộc vào:                SimulatedPlc, PlcRegisterMap, HandshakeStatus/Command/Result
// ==================== Pattern / Kỹ thuật nổi bật:   Vòng quét (scan cycle) kiểu PLC: mỗi chu kỳ đọc-xử lý-ghi; chỉ chạm bộ nhớ qua GetWord/SetWord nên cùng "nhìn thấy" dữ liệu như một chương trình ladder thật
//
// VỊ TRÍ ĐẶT FILE: thư mục Plc/ — namespace VisionFlow.Hardware.Plc.
//
// Ladder này đóng vai PLC ĐÚNG THEO GIAO THỨC ghi ở đầu PlcHandshakeService.cs. Nó cũng là bản mô tả chuẩn cho người viết ladder thật:
//
//   IDLE        : Status = Ready  VÀ  Command = 0  VÀ đã đủ thời gian nghỉ  =>  ProductId += 1 ; Command = 1  -> WAIT_RESULT
//   WAIT_RESULT : Status = Done  => đọc Result + số đo -> ACK
//                 Status = Error => đọc ErrorCode      -> ACK
//                 quá ResponseTimeoutMs mà PC không trả lời => báo động "PC không phản hồi" -> ACK
//   ACK         : Command = 0  -> WAIT_READY
//   WAIT_READY  : Status = Ready => IDLE   (PC chưa Ready trong ResponseTimeoutMs => báo động)
//   LUÔN LUÔN   : Heartbeat không đổi quá HeartbeatTimeoutMs  =>  PC coi như chết

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace VisionFlow.Hardware.Plc;

/// <summary>Một chu kỳ mà "PLC giả" đã hoàn tất, nhìn từ phía PLC.</summary>
public sealed record LadderCycle(
    ushort ProductId,
    ushort Result,          // 0 chưa có, 1 OK, 2 NG
    ushort ErrorCode,       // 0 = không lỗi
    ushort EchoProductId,   // PC lặp lại ProductId — phải bằng ProductId
    ushort CycleMs,
    int[] Values,           // 6 số đo (đã ở dạng số nguyên x ValueScale)
    bool TimedOut,          // true = PC không trả lời kịp (không có Result/Error)
    long RoundTripMs);      // Từ lúc bật trigger đến lúc thấy Done/Error

public sealed class SimulatedLadder : IDisposable
{
    private enum Phase { Idle, WaitResult, Ack, WaitReady }

    private readonly SimulatedPlc _plc;
    private readonly PlcRegisterMap _map;

    private CancellationTokenSource? _cts;
    private Task? _task;
    private bool _disposed;

    private Phase _phase = Phase.Idle;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _phaseStartedAt;
    private long _lastCycleEndedAt;
    private ushort _productId;
    private long _triggerAt;
    private volatile bool _manualTriggerRequested;

    // Giám sát heartbeat
    private ushort _lastHeartbeat;
    private long _lastHeartbeatChangeAt;
    private bool _pcAlive;

    private int _cycles, _ok, _ng, _errors, _timeouts;

    public SimulatedLadder(SimulatedPlc plc, PlcRegisterMap map, int triggerIntervalMs = 2000)
    {
        _plc = plc ?? throw new ArgumentNullException(nameof(plc));
        _map = map ?? throw new ArgumentNullException(nameof(map));
        TriggerIntervalMs = triggerIntervalMs;
    }

    /// <summary>Thời gian nghỉ giữa hai lần tự bật trigger (ms), tính từ lúc kết thúc chu kỳ trước.</summary>
    public int TriggerIntervalMs { get; set; }

    /// <summary>Chu kỳ quét của ladder (ms). PLC thật thường quét vài ms.</summary>
    public int ScanIntervalMs { get; set; } = 5;

    /// <summary>true = tự bật trigger theo TriggerIntervalMs; false = chỉ bật khi gọi <see cref="TriggerNow"/>.</summary>
    public bool AutoTrigger { get; set; } = true;

    /// <summary>false = mô phỏng "chương trình PLC quên xoá Command" để thử cảnh báo và việc PC không chạy lại.</summary>
    public bool AcknowledgeResults { get; set; } = true;

    /// <summary>PC không trả lời (Done/Error) trong ngần này ms thì ladder báo động và bỏ chu kỳ.</summary>
    public int ResponseTimeoutMs { get; set; } = 5000;

    /// <summary>Heartbeat đứng yên quá ngần này ms thì <see cref="PcAlive"/> = false.</summary>
    public int HeartbeatTimeoutMs { get; set; } = 2000;

    /// <summary>true nếu heartbeat của PC còn thay đổi.</summary>
    public bool PcAlive => _pcAlive;

    public bool IsRunning => _task is { IsCompleted: false };
    public int CompletedCycles => Volatile.Read(ref _cycles);
    public int OkCount => Volatile.Read(ref _ok);
    public int NgCount => Volatile.Read(ref _ng);
    public int ErrorCount => Volatile.Read(ref _errors);
    public int TimeoutCount => Volatile.Read(ref _timeouts);

    /// <summary>Bắn khi ladder đã đọc xong kết quả của một chu kỳ. Gọi trên thread nền.</summary>
    public event Action<LadderCycle>? CycleCompleted;

    /// <summary>Nhật ký của ladder (bật trigger, nhận kết quả, báo động). Gọi trên thread nền.</summary>
    public event Action<string>? Log;

    /// <summary>Yêu cầu bật trigger ở lần quét kế tiếp (nếu đang rảnh), bất kể AutoTrigger.</summary>
    public void TriggerNow() => _manualTriggerRequested = true;

    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SimulatedLadder));
        if (IsRunning) return;

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _phase = Phase.Idle;
        _lastCycleEndedAt = _clock.ElapsedMilliseconds;
        _lastHeartbeat = _plc.GetWord(_map.Heartbeat);
        _lastHeartbeatChangeAt = _clock.ElapsedMilliseconds;
        _pcAlive = false;

        // Trạng thái ban đầu của vùng lệnh như PLC vừa khởi động: Command = 0
        _plc.SetWord(_map.Command, HandshakeCommand.Idle);

        _task = Task.Run(() => ScanLoopAsync(ct));
        Emit("Ladder giả lập BẮT ĐẦU");
    }

    public void Stop()
    {
        var cts = _cts;
        _cts = null;
        try { cts?.Cancel(); } catch { }
        try { _task?.Wait(1000); } catch { }
        cts?.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    // ====================================================================
    // VÒNG QUÉT
    // ====================================================================

    private async Task ScanLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Scan();
                await Task.Delay(Math.Max(1, ScanIntervalMs), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Emit($"Lỗi ladder: {ex.Message}");
                try { await Task.Delay(100, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private void Scan()
    {
        long now = _clock.ElapsedMilliseconds;

        // ---- Giám sát heartbeat (chạy ở mọi pha) ----
        ushort heartbeat = _plc.GetWord(_map.Heartbeat);
        if (heartbeat != _lastHeartbeat)
        {
            _lastHeartbeat = heartbeat;
            _lastHeartbeatChangeAt = now;
        }
        bool alive = now - _lastHeartbeatChangeAt < HeartbeatTimeoutMs;
        if (alive != _pcAlive)
        {
            _pcAlive = alive;
            Emit(alive ? "Heartbeat của PC: CÓ" : "Heartbeat của PC: MẤT — PC coi như chết/treo");
        }

        ushort status = _plc.GetWord(_map.Status);

        switch (_phase)
        {
            case Phase.Idle:
                {
                    bool due = AutoTrigger && now - _lastCycleEndedAt >= TriggerIntervalMs;
                    bool wanted = due || _manualTriggerRequested;
                    if (wanted && status == HandshakeStatus.Ready && _plc.GetWord(_map.Command) == HandshakeCommand.Idle)
                    {
                        _manualTriggerRequested = false;
                        _productId = unchecked((ushort)(_productId + 1));
                        _plc.SetWord(_map.ProductId, _productId);         // đặt ProductId TRƯỚC
                        _plc.SetWord(_map.Command, HandshakeCommand.Trigger); // rồi mới bật trigger
                        _triggerAt = now;
                        ChangePhase(Phase.WaitResult, now);
                        Emit($"Bật TRIGGER (ProductId = {_productId})");
                    }
                    break;
                }

            case Phase.WaitResult:
                {
                    if (status == HandshakeStatus.Done || status == HandshakeStatus.Error)
                    {
                        // Status là cờ commit: thấy Done/Error thì Result/số đo đã sẵn sàng
                        var values = new int[PlcRegisterMap.MaxSlots];
                        for (int i = 0; i < values.Length; i++)
                        {
                            ushort lo = _plc.GetWord(_map.ValueOf(i + 1));
                            ushort hi = _plc.GetWord(_map.ValueOf(i + 1).Offset(1));
                            values[i] = unchecked((int)((uint)lo | ((uint)hi << 16)));
                        }

                        var cycle = new LadderCycle(
                            _productId,
                            _plc.GetWord(_map.Result),
                            _plc.GetWord(_map.ErrorCode),
                            _plc.GetWord(_map.EchoProductId),
                            _plc.GetWord(_map.CycleMs),
                            values,
                            TimedOut: false,
                            RoundTripMs: now - _triggerAt);

                        Complete(cycle, status == HandshakeStatus.Error);
                        ChangePhase(Phase.Ack, now);
                    }
                    else if (now - _phaseStartedAt >= ResponseTimeoutMs)
                    {
                        Emit($"BÁO ĐỘNG: PC không trả lời trong {ResponseTimeoutMs} ms (Status = {status})");
                        Interlocked.Increment(ref _timeouts);
                        Complete(new LadderCycle(_productId, 0, 0, 0, 0, new int[PlcRegisterMap.MaxSlots], TimedOut: true, RoundTripMs: now - _triggerAt), isError: false, countAsCycle: false);
                        ChangePhase(Phase.Ack, now);
                    }
                    break;
                }

            case Phase.Ack:
                {
                    if (AcknowledgeResults)
                    {
                        _plc.SetWord(_map.Command, HandshakeCommand.Idle);   // xoá trigger = xác nhận đã đọc
                        ChangePhase(Phase.WaitReady, now);
                    }
                    break;
                }

            case Phase.WaitReady:
                {
                    if (status == HandshakeStatus.Ready)
                    {
                        _lastCycleEndedAt = now;
                        ChangePhase(Phase.Idle, now);
                    }
                    else if (now - _phaseStartedAt >= ResponseTimeoutMs)
                    {
                        // PC không quay lại Ready (đã chết?) — cứ để Command = 0 và quay về Idle; Idle sẽ đợi Status = Ready
                        Emit($"BÁO ĐỘNG: PC không quay lại Ready sau {ResponseTimeoutMs} ms (Status = {status})");
                        _lastCycleEndedAt = now;
                        ChangePhase(Phase.Idle, now);
                    }
                    break;
                }
        }
    }

    private void Complete(LadderCycle cycle, bool isError, bool countAsCycle = true)
    {
        if (countAsCycle)
        {
            Interlocked.Increment(ref _cycles);
            if (isError) Interlocked.Increment(ref _errors);
            else if (cycle.Result == HandshakeResult.Ok) Interlocked.Increment(ref _ok);
            else if (cycle.Result == HandshakeResult.Ng) Interlocked.Increment(ref _ng);

            Emit(isError
                ? $"Nhận LỖI từ PC (ProductId = {cycle.ProductId}, ErrorCode = {cycle.ErrorCode}) sau {cycle.RoundTripMs} ms"
                : $"Nhận KẾT QUẢ (ProductId = {cycle.ProductId}, Echo = {cycle.EchoProductId}): {(cycle.Result == HandshakeResult.Ok ? "OK" : "NG")} sau {cycle.RoundTripMs} ms");
        }

        try { CycleCompleted?.Invoke(cycle); } catch { }
    }

    private void ChangePhase(Phase phase, long now)
    {
        _phase = phase;
        _phaseStartedAt = now;
    }

    private void Emit(string message)
    {
        try { Log?.Invoke($"{DateTime.Now:HH:mm:ss.fff} [LADDER] {message}"); } catch { }
    }
}