// ==================== Vai trò chính:                Client TCP nói MC Protocol 3E nhị phân với PLC Mitsubishi Q/L (đọc/ghi hàng loạt word và bit) — cài đặt IPlcLink cho PLC thật
// ==================== Thành phần / Class tiêu biểu: McProtocolClient
// ==================== Phụ thuộc vào:                IPlcLink, McProtocol3E, PlcAddress
// ==================== Pattern / Kỹ thuật nổi bật:   Request-Response tuần tự hoá bằng SemaphoreSlim + timeout bằng CancellationToken + "đóng ngay khi I/O lỗi" (fail-closed)
//
// VỊ TRÍ ĐẶT FILE: thư mục Plc/ — namespace VisionFlow.Hardware.Plc.
//
// QUY TẮC AN TOÀN QUAN TRỌNG (rút ra từ các lỗi của tài liệu Buổi 118/120/125):
//   1. Đọc ĐỦ số byte (vòng lặp) — TCP là luồng byte, một lần Read() có thể trả thiếu.
//   2. Giới hạn độ dài phản hồi — khung rác không được làm treo hay cấp phát bộ nhớ khổng lồ.
//   3. Sau BẤT KỲ lỗi I/O nào (timeout, rớt mạng, bị huỷ giữa chừng) phải ĐÓNG kết nối: phản hồi đến muộn sẽ làm lệch khung
//      và lần sau đọc nhầm dữ liệu của lần trước. Kết nối lại luôn sạch hơn cố cứu.
//   4. Không tin TcpClient.Connected: trạng thái chỉ đổi theo kết quả trao đổi thật.
//   5. Một lỗi do PLC BÁO (end code khác 0) không làm hỏng đường truyền: khung đã được đọc trọn vẹn, vẫn dùng tiếp.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace VisionFlow.Hardware.Plc;

/// <summary>Kết nối tới PLC Mitsubishi bằng MC Protocol 3E nhị phân qua cổng Ethernet tích hợp (đã mở bằng Open Setting: TCP + MC Protocol).</summary>
public sealed class McProtocolClient : IPlcLink
{
    private readonly string _host;
    private readonly int _port;
    private readonly int _connectTimeoutMs;
    private readonly int _receiveTimeoutMs;

    // Cổng tuần tự hoá: tại một thời điểm chỉ MỘT yêu cầu đang bay (PLC trả lời theo thứ tự nên không được chen ngang)
    private readonly SemaphoreSlim _gate = new(1, 1);

    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private volatile PlcLinkState _state = PlcLinkState.Disconnected;
    private bool _disposed;

    public string Name { get; }
    public PlcLinkState State => _state;
    public bool IsConnected => _state == PlcLinkState.Connected;

    public event Action<PlcLinkState>? StateChanged;

    /// <summary>Bắn mỗi khung gửi/nhận (frame, true = gửi đi). Dùng để hiển thị/ghi log hex khi gỡ lỗi (giống Serial Monitor của Buổi 117).</summary>
    public event Action<byte[], bool>? FrameTransferred;

    public McProtocolClient(string name, string host, int port, int connectTimeoutMs = 3000, int receiveTimeoutMs = 2000)
    {
        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("Host không được rỗng.", nameof(host));
        if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (connectTimeoutMs < 1) throw new ArgumentOutOfRangeException(nameof(connectTimeoutMs));
        if (receiveTimeoutMs < 1) throw new ArgumentOutOfRangeException(nameof(receiveTimeoutMs));

        Name = name;
        _host = host;
        _port = port;
        _connectTimeoutMs = connectTimeoutMs;
        _receiveTimeoutMs = receiveTimeoutMs;
    }

    // ====================================================================
    // KẾT NỐI
    // ====================================================================

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_state == PlcLinkState.Connected && _stream is not null) return; // Đã kết nối: không làm gì

            CloseSocketLocked();
            SetState(PlcLinkState.Connecting);

            var tcp = new TcpClient { NoDelay = true }; // NoDelay: gửi ngay, không gom gói nhỏ (giảm trễ cho gói lệnh ngắn)
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(_connectTimeoutMs);
                await tcp.ConnectAsync(_host, _port, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                tcp.Dispose();
                SetState(PlcLinkState.Faulted);
                throw new PlcTimeoutException($"Không kết nối được {_host}:{_port} trong {_connectTimeoutMs} ms (kiểm tra IP, cáp, và Open Setting đã ghi xuống PLC chưa).");
            }
            catch (OperationCanceledException)
            {
                tcp.Dispose();
                SetState(PlcLinkState.Disconnected);
                throw; // Người gọi huỷ: không phải lỗi đường truyền
            }
            catch (Exception ex)
            {
                tcp.Dispose();
                SetState(PlcLinkState.Faulted);
                throw new PlcConnectionException($"Không kết nối được {_host}:{_port}: {ex.Message}", ex);
            }

            EnableKeepAlive(tcp.Client);
            _tcp = tcp;
            _stream = tcp.GetStream();
            SetState(PlcLinkState.Connected);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisconnectAsync()
    {
        if (_disposed) return;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            CloseSocketLocked();
            SetState(PlcLinkState.Disconnected);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ====================================================================
    // ĐỌC / GHI
    // ====================================================================

    public async Task<ushort[]> ReadWordsAsync(PlcAddress start, int count, CancellationToken ct = default)
    {
        ValidateWordAccess(start, count);

        var request = McProtocol3E.BuildRequest(McProtocol3E.CommandBatchRead, McProtocol3E.SubcommandWord, start, count, ReadOnlySpan<byte>.Empty);
        byte[] data = await ExchangeAsync(request, ct).ConfigureAwait(false);

        if (data.Length != count * 2)
            throw await FaultAsync($"PLC trả {data.Length} byte, mong đợi {count * 2} byte cho {count} word.").ConfigureAwait(false);

        return McProtocol3E.BytesToWords(data);
    }

    public async Task WriteWordsAsync(PlcAddress start, IReadOnlyList<ushort> values, CancellationToken ct = default)
    {
        if (values is null) throw new ArgumentNullException(nameof(values));
        ValidateWordAccess(start, values.Count);

        var request = McProtocol3E.BuildRequest(McProtocol3E.CommandBatchWrite, McProtocol3E.SubcommandWord, start, values.Count, McProtocol3E.WordsToBytes(values));
        await ExchangeAsync(request, ct).ConfigureAwait(false);
    }

    public async Task<bool[]> ReadBitsAsync(PlcAddress start, int count, CancellationToken ct = default)
    {
        ValidateBitAccess(start, count);

        var request = McProtocol3E.BuildRequest(McProtocol3E.CommandBatchRead, McProtocol3E.SubcommandBit, start, count, ReadOnlySpan<byte>.Empty);
        byte[] data = await ExchangeAsync(request, ct).ConfigureAwait(false);

        int expected = (count + 1) / 2;
        if (data.Length != expected)
            throw await FaultAsync($"PLC trả {data.Length} byte, mong đợi {expected} byte cho {count} bit.").ConfigureAwait(false);

        return McProtocol3E.UnpackBits(data, count);
    }

    public async Task WriteBitsAsync(PlcAddress start, IReadOnlyList<bool> values, CancellationToken ct = default)
    {
        if (values is null) throw new ArgumentNullException(nameof(values));
        ValidateBitAccess(start, values.Count);

        var request = McProtocol3E.BuildRequest(McProtocol3E.CommandBatchWrite, McProtocol3E.SubcommandBit, start, values.Count, McProtocol3E.PackBits(values));
        await ExchangeAsync(request, ct).ConfigureAwait(false);
    }

    // ====================================================================
    // LÕI: MỘT LẦN GỬI - NHẬN
    // ====================================================================

    /// <summary>Gửi một khung yêu cầu, đọc trọn một khung trả lời, kiểm tra end code; trả về phần dữ liệu (sau end code).</summary>
    private async Task<byte[]> ExchangeAsync(byte[] request, CancellationToken ct)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var stream = _stream;
            if (_state != PlcLinkState.Connected || stream is null)
                throw new PlcConnectionException($"Chưa kết nối PLC ({_host}:{_port}). Hãy gọi ConnectAsync() trước.");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_receiveTimeoutMs); // Timeout tính cho cả gửi + nhận của một lần trao đổi

            try
            {
                //FrameTransferred?.Invoke(request, true);
                //await stream.WriteAsync(request, 0, request.Length, cts.Token).ConfigureAwait(false);

                FrameTransferred?.Invoke(request, true);
                System.Diagnostics.Debug.WriteLine($"[PLC TX] {_host}:{_port} -> {BitConverter.ToString(request)}");
                await stream.WriteAsync(
                    request,
                    0,
                    request.Length,
                    cts.Token).ConfigureAwait(false);

                // 1. Đọc phần đầu 9 byte để biết độ dài phần còn lại
                var header = new byte[McProtocol3E.ResponseHeaderLength];
                await ReadExactAsync(stream, header, header.Length, cts.Token).ConfigureAwait(false);

                if (header[0] != 0xD0 || header[1] != 0x00)
                    throw new PlcConnectionException($"Khung trả lời sai Subheader ({header[0]:X2} {header[1]:X2}, mong đợi D0 00): PLC không nói khung 3E nhị phân? Kiểm tra Communication data code = Binary.");

                int length = header[7] | (header[8] << 8);
                if (length < 2 || length > McProtocol3E.MaxResponseBodyLength)
                    throw new PlcConnectionException($"Độ dài phản hồi bất thường ({length} byte) — khung bị hỏng.");

                // 2. Đọc trọn phần thân (end code + dữ liệu)
                var body = new byte[length];
                await ReadExactAsync(stream, body, length, cts.Token).ConfigureAwait(false);

                if (FrameTransferred is { } handler)
                {
                    var whole = new byte[header.Length + body.Length];
                    Buffer.BlockCopy(header, 0, whole, 0, header.Length);
                    Buffer.BlockCopy(body, 0, whole, header.Length, body.Length);
                    handler(whole, false);
                }

                ushort endCode = (ushort)(body[0] | (body[1] << 8));
                if (endCode != 0)
                {
                    // PLC đã trả lời đầy đủ => đường truyền vẫn đồng bộ, KHÔNG đóng kết nối
                    throw new PlcProtocolException(endCode,
                        $"PLC báo lỗi 0x{endCode:X4}: {McProtocol3E.DescribeEndCode(endCode)}.");
                }

                var data = new byte[length - 2];
                Buffer.BlockCopy(body, 2, data, 0, data.Length);
                return data;
            }
            catch (PlcProtocolException)
            {
                throw; // Lỗi do PLC báo: giữ nguyên kết nối
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                CloseSocketLocked();
                SetState(PlcLinkState.Faulted);
                throw new PlcTimeoutException($"PLC {_host}:{_port} không trả lời trong {_receiveTimeoutMs} ms. Đã đóng kết nối để tránh lệch khung.");
            }
            catch (OperationCanceledException)
            {
                // Người gọi huỷ khi dữ liệu còn dở: khung có thể đã gửi mà chưa nhận => phải bỏ kết nối
                CloseSocketLocked();
                SetState(PlcLinkState.Faulted);
                throw;
            }
            catch (PlcLinkException)
            {
                CloseSocketLocked();
                SetState(PlcLinkState.Faulted);
                throw;
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or InvalidOperationException)
            {
                CloseSocketLocked();
                SetState(PlcLinkState.Faulted);
                throw new PlcConnectionException($"Mất kết nối PLC {_host}:{_port}: {ex.Message}", ex);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Đọc ĐỦ <paramref name="count"/> byte (TCP có thể trả từng mẩu nhỏ). Đối phương đóng kết nối giữa chừng thì ném lỗi.</summary>
    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, int count, CancellationToken ct)
    {
        int offset = 0;
        while (offset < count)
        {
            int n = await stream.ReadAsync(buffer, offset, count - offset, ct).ConfigureAwait(false);
            if (n == 0)
                throw new PlcConnectionException("PLC đã đóng kết nối giữa chừng khi đang nhận phản hồi.");
            offset += n;
        }
    }

    // ====================================================================
    // KIỂM TRA THAM SỐ
    // ====================================================================

    private static void ValidateWordAccess(PlcAddress start, int count)
    {
        if (count < 1 || count > McProtocol3E.MaxWordPoints)
            throw new ArgumentOutOfRangeException(nameof(count), $"Số word mỗi lần phải từ 1 đến {McProtocol3E.MaxWordPoints}.");
        if (start.Number < 0 || start.Number > PlcAddress.MaxNumber)
            throw new ArgumentOutOfRangeException(nameof(start));

        // Thiết bị bit đọc theo word: mỗi word = 16 bit liên tiếp, địa chỉ đầu phải là bội số của 16 (quy định của PLC)
        if (start.Unit == PlcDeviceUnit.Bit && start.Number % 16 != 0)
            throw new ArgumentException($"{start} là thiết bị bit: khi đọc/ghi theo word, số thiết bị phải chia hết cho 16 (ví dụ {start.Kind}0, {start.Kind}10 nếu hex...).", nameof(start));
    }

    private static void ValidateBitAccess(PlcAddress start, int count)
    {
        if (start.Unit != PlcDeviceUnit.Bit)
            throw new ArgumentException($"{start} là thiết bị word, không đọc/ghi từng bit được (dùng M, L, B, X, Y).", nameof(start));
        if (count < 1 || count > McProtocol3E.MaxBitPoints)
            throw new ArgumentOutOfRangeException(nameof(count), $"Số bit mỗi lần phải từ 1 đến {McProtocol3E.MaxBitPoints}.");
        if (start.Number < 0 || start.Number > PlcAddress.MaxNumber)
            throw new ArgumentOutOfRangeException(nameof(start));
    }

    // ====================================================================
    // TIỆN ÍCH NỘI BỘ
    // ====================================================================

    /// <summary>Phản hồi sai kích thước dữ liệu: coi đường truyền không tin cậy và đóng lại.</summary>
    private async Task<PlcConnectionException> FaultAsync(string message)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            CloseSocketLocked();
            SetState(PlcLinkState.Faulted);
        }
        finally
        {
            _gate.Release();
        }
        return new PlcConnectionException(message);
    }

    /// <summary>Đóng socket. PHẢI gọi khi đang giữ _gate.</summary>
    private void CloseSocketLocked()
    {
        try { _stream?.Dispose(); } catch { /* nuốt lỗi khi dọn dẹp */ }
        try { _tcp?.Dispose(); } catch { /* nuốt lỗi khi dọn dẹp */ }
        _stream = null;
        _tcp = null;
    }

    /// <summary>
    /// Bật TCP keep-alive để hệ điều hành tự phát hiện cáp bị rút sau ~15 giây (mặc định Windows là 2 giờ).
    /// Không phải hệ điều hành nào cũng hỗ trợ đủ tuỳ chọn nên bọc try/catch — thiếu thì bỏ qua, heartbeat ở bước sau vẫn lo.
    /// </summary>
    private static void EnableKeepAlive(Socket socket)
    {
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 10);       // giây rảnh trước khi thăm dò
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 3);    // giây giữa các lần thăm dò
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 2);  // số lần thăm dò hỏng thì coi là chết
        }
        catch
        {
            // bỏ qua
        }
    }

    private void SetState(PlcLinkState state)
    {
        if (_state == state) return;
        _state = state;
        try { StateChanged?.Invoke(state); } catch { /* handler lỗi không được làm hỏng đường truyền */ }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(McProtocolClient));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        CloseSocketLocked();
        _state = PlcLinkState.Disconnected;
        _gate.Dispose();
    }
}