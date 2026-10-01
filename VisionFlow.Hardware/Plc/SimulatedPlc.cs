// ==================== Vai trò chính:                PLC giả lập chạy ngay trong ứng dụng: server TCP hiểu MC Protocol 3E (đọc/ghi hàng loạt) trên bộ nhớ RAM, để phát triển/kiểm thử mà không cần PLC thật
// ==================== Thành phần / Class tiêu biểu: SimulatedPlc
// ==================== Phụ thuộc vào:                PlcAddress, MelsecDevice
// ==================== Pattern / Kỹ thuật nổi bật:   Test Double kiểu "server thật": McProtocolClient nối vào bằng TCP y như nối PLC thật => kiểm thử cả code giao thức, không chỉ logic phía trên
//
// VỊ TRÍ ĐẶT FILE: thư mục Plc/ — namespace VisionFlow.Hardware.Plc.
//
// PHÍA SERVER CỐ Ý TỰ PHÂN TÍCH KHUNG (không dùng lại hàm dựng khung của McProtocol3E) để hai bên kiểm chéo nhau:
// nếu client dựng sai một byte thì server giả sẽ báo lỗi thay vì "cùng sai cùng chạy".
// Mọi khung yêu cầu/phản hồi xem chú thích ở đầu file McProtocol3E.cs.

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace VisionFlow.Hardware.Plc;

/// <summary>
/// PLC giả lập. Hỗ trợ lệnh 0x0401 (đọc hàng loạt) và 0x1401 (ghi hàng loạt), lệnh phụ 0x0000 (word) và 0x0001 (bit),
/// các thiết bị D, W, R, ZR, M, L, B, X, Y — đủ cho handshake Vision. Lệnh khác trả end code 0xC059 như PLC thật.
/// <para>
/// Công cụ chèn lỗi để thử độ bền của phía handshake:
/// <see cref="ResponseDelayMs"/> (mạng chậm), <see cref="IgnoreRequests"/> (PLC "treo", không trả lời),
/// <see cref="KickAllClients"/> (rút cáp).
/// </para>
/// </summary>
public sealed class SimulatedPlc : IDisposable
{
    // Kích thước bộ nhớ giả lập từng loại thiết bị (số điểm). Vượt quá => end code 0xC056 như PLC thật.
    private static int SizeOf(PlcDeviceKind kind) => kind switch
    {
        PlcDeviceKind.D => 65536,
        PlcDeviceKind.W => 65536,
        PlcDeviceKind.R => 32768,
        PlcDeviceKind.ZR => 131072,
        PlcDeviceKind.M => 32768,
        PlcDeviceKind.L => 32768,
        PlcDeviceKind.B => 65536,
        PlcDeviceKind.X => 8192,
        PlcDeviceKind.Y => 8192,
        _ => 0
    };

    private readonly object _memLock = new();                       // Bảo vệ toàn bộ bộ nhớ (nhiều client + code kiểm thử truy cập đồng thời)
    private readonly Dictionary<PlcDeviceKind, ushort[]> _words = new();
    private readonly Dictionary<PlcDeviceKind, bool[]> _bits = new();

    private readonly object _clientsLock = new();
    private readonly List<TcpClient> _clients = new();

    private readonly int _requestedPort;
    private readonly bool _loopbackOnly;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private int _requestCount;
    private bool _disposed;

    /// <param name="port">Cổng TCP lắng nghe; 0 = để hệ điều hành chọn cổng trống (đọc lại ở <see cref="Port"/>).</param>
    /// <param name="loopbackOnly">true = chỉ nhận kết nối từ chính máy này (127.0.0.1) — an toàn khi thử nghiệm.</param>
    public SimulatedPlc(int port = 5011, bool loopbackOnly = true)
    {
        if (port < 0 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        _requestedPort = port;
        _loopbackOnly = loopbackOnly;

        foreach (PlcDeviceKind kind in Enum.GetValues(typeof(PlcDeviceKind)))
        {
            if (MelsecDevice.UnitOf(kind) == PlcDeviceUnit.Word) _words[kind] = new ushort[SizeOf(kind)];
            else _bits[kind] = new bool[SizeOf(kind)];
        }
    }

    /// <summary>Cổng đang lắng nghe (đúng cả khi khởi tạo với port = 0).</summary>
    public int Port { get; private set; }

    public bool IsRunning => _listener is not null;

    /// <summary>Chờ thêm bao nhiêu ms trước khi trả lời mỗi yêu cầu (mô phỏng mạng/PLC chậm).</summary>
    public int ResponseDelayMs { get; set; }

    /// <summary>true = nhận yêu cầu nhưng KHÔNG trả lời (mô phỏng PLC treo) => phía client phải gặp timeout.</summary>
    public bool IgnoreRequests { get; set; }

    /// <summary>Tổng số yêu cầu đã nhận (kể cả khi đang IgnoreRequests).</summary>
    public int RequestCount => Volatile.Read(ref _requestCount);

    /// <summary>Số client đang kết nối.</summary>
    public int ClientCount
    {
        get { lock (_clientsLock) return _clients.Count; }
    }

    public event Action<string>? Log;

    // ====================================================================
    // VÒNG ĐỜI
    // ====================================================================

    public void Start()
    {
        ThrowIfDisposed();
        if (_listener is not null) return;

        var listener = new TcpListener(_loopbackOnly ? IPAddress.Loopback : IPAddress.Any, _requestedPort);
        listener.Start();

        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _cts = new CancellationTokenSource();
        _acceptTask = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
        Emit($"PLC giả lập lắng nghe cổng {Port} ({(_loopbackOnly ? "chỉ máy này" : "mọi giao diện mạng")})");
    }

    public void Stop()
    {
        var cts = _cts;
        var listener = _listener;
        _cts = null;
        _listener = null;

        try { cts?.Cancel(); } catch { }
        try { listener?.Stop(); } catch { }
        KickAllClients();
        try { _acceptTask?.Wait(1000); } catch { /* bỏ qua lỗi huỷ của vòng nhận */ }
        cts?.Dispose();
    }

    /// <summary>Đóng đột ngột mọi kết nối đang mở (mô phỏng rút cáp / PLC khởi động lại).</summary>
    public void KickAllClients()
    {
        List<TcpClient> snapshot;
        lock (_clientsLock)
        {
            snapshot = new List<TcpClient>(_clients);
            _clients.Clear();
        }
        foreach (var c in snapshot)
        {
            try { c.Client.Close(); } catch { }
            try { c.Dispose(); } catch { }
        }
        if (snapshot.Count > 0) Emit($"Đã ngắt {snapshot.Count} kết nối");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    // ====================================================================
    // TRUY CẬP BỘ NHỚ TỪ CODE (để kiểm thử đóng vai "chương trình ladder của PLC")
    // ====================================================================

    public ushort GetWord(PlcAddress address)
    {
        lock (_memLock) return WordArray(address)[CheckIndex(address, WordArray(address).Length)];
    }

    public void SetWord(PlcAddress address, ushort value)
    {
        lock (_memLock) WordArray(address)[CheckIndex(address, WordArray(address).Length)] = value;
    }

    public bool GetBit(PlcAddress address)
    {
        lock (_memLock) return BitArray(address)[CheckIndex(address, BitArray(address).Length)];
    }

    public void SetBit(PlcAddress address, bool value)
    {
        lock (_memLock) BitArray(address)[CheckIndex(address, BitArray(address).Length)] = value;
    }

    private ushort[] WordArray(PlcAddress a)
        => _words.TryGetValue(a.Kind, out var arr) ? arr : throw new ArgumentException($"{a} không phải thiết bị word.");

    private bool[] BitArray(PlcAddress a)
        => _bits.TryGetValue(a.Kind, out var arr) ? arr : throw new ArgumentException($"{a} không phải thiết bị bit.");

    private static int CheckIndex(PlcAddress a, int size)
        => a.Number >= 0 && a.Number < size ? a.Number : throw new ArgumentOutOfRangeException(nameof(a), $"{a} vượt phạm vi bộ nhớ giả lập.");

    // ====================================================================
    // SERVER
    // ====================================================================

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                break; // Listener đã dừng
            }

            client.NoDelay = true;
            lock (_clientsLock) _clients.Add(client);
            Emit($"Client kết nối: {client.Client.RemoteEndPoint}");
            _ = Task.Run(() => HandleClientAsync(client, ct));
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            var stream = client.GetStream();
            var header = new byte[9];

            while (!ct.IsCancellationRequested)
            {
                if (!await ReadExactAsync(stream, header, 9, ct).ConfigureAwait(false)) break; // Client đóng kết nối

                // Chỉ nhận khung 3E nhị phân yêu cầu (50 00); khác thì đóng như PLC thật khi nhận rác
                if (header[0] != 0x50 || header[1] != 0x00) { Emit("Khung sai Subheader — đóng kết nối"); break; }

                int length = header[7] | (header[8] << 8);
                if (length < 6 || length > 8192) { Emit($"Độ dài khung bất thường ({length}) — đóng kết nối"); break; }

                var body = new byte[length];
                if (!await ReadExactAsync(stream, body, length, ct).ConfigureAwait(false)) break;

                Interlocked.Increment(ref _requestCount);
                if (IgnoreRequests) continue;                       // Mô phỏng PLC treo: nhận mà không trả lời

                int delay = ResponseDelayMs;
                if (delay > 0) await Task.Delay(delay, ct).ConfigureAwait(false);

                byte[] response = ProcessRequest(header, body);
                await stream.WriteAsync(response, 0, response.Length, ct).ConfigureAwait(false);
            }
        }
        catch
        {
            // Kết nối bị đóng/huỷ: kết thúc luồng xử lý client này
        }
        finally
        {
            lock (_clientsLock) _clients.Remove(client);
            try { client.Dispose(); } catch { }
        }
    }

    private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, int count, CancellationToken ct)
    {
        int offset = 0;
        while (offset < count)
        {
            int n = await stream.ReadAsync(buffer, offset, count - offset, ct).ConfigureAwait(false);
            if (n == 0) return false;
            offset += n;
        }
        return true;
    }

    // ====================================================================
    // XỬ LÝ MỘT YÊU CẦU
    // ====================================================================

    private byte[] ProcessRequest(byte[] header, byte[] body)
    {
        // body: [0..1] timer, [2..3] lệnh, [4..5] lệnh phụ, [6..8] số thiết bị, [9] mã thiết bị, [10..11] số điểm, [12..] dữ liệu
        ushort command = (ushort)(body[2] | (body[3] << 8));
        ushort sub = (ushort)(body[4] | (body[5] << 8));

        bool isRead = command == 0x0401;
        bool isWrite = command == 0x1401;
        if ((!isRead && !isWrite) || (sub != 0x0000 && sub != 0x0001))
            return ErrorResponse(header, 0xC059, command, sub);

        if (body.Length < 12)
            return ErrorResponse(header, 0xC061, command, sub);

        int number = body[6] | (body[7] << 8) | (body[8] << 16);
        byte deviceCode = body[9];
        int points = body[10] | (body[11] << 8);
        int dataLength = body.Length - 12;

        if (!MelsecDevice.TryFromCode(deviceCode, out var kind))
            return ErrorResponse(header, 0xC05C, command, sub);

        bool bitUnit = sub == 0x0001;
        var unit = MelsecDevice.UnitOf(kind);

        lock (_memLock)
        {
            // ---- Truy cập theo BIT (chỉ thiết bị bit) ----
            if (bitUnit)
            {
                if (unit != PlcDeviceUnit.Bit) return ErrorResponse(header, 0xC05C, command, sub);
                if (points < 1 || points > 7168) return ErrorResponse(header, 0xC051, command, sub);

                var bits = _bits[kind];
                if (number + points > bits.Length) return ErrorResponse(header, 0xC056, command, sub);

                if (isRead)
                {
                    var data = new byte[(points + 1) / 2];
                    for (int i = 0; i < points; i++)
                        if (bits[number + i]) data[i / 2] |= (i % 2 == 0) ? (byte)0x10 : (byte)0x01;
                    Emit($"ĐỌC bit {MelsecDevice.PrefixOf(kind)}{number} x{points}");
                    return OkResponse(header, data);
                }

                if (dataLength != (points + 1) / 2) return ErrorResponse(header, 0xC061, command, sub);
                for (int i = 0; i < points; i++)
                {
                    byte b = body[12 + i / 2];
                    int nibble = (i % 2 == 0) ? (b >> 4) & 0x0F : b & 0x0F;
                    bits[number + i] = nibble != 0;
                }
                Emit($"GHI bit {MelsecDevice.PrefixOf(kind)}{number} x{points}");
                return OkResponse(header, Array.Empty<byte>());
            }

            // ---- Truy cập theo WORD ----
            if (points < 1 || points > 960) return ErrorResponse(header, 0xC051, command, sub);

            if (unit == PlcDeviceUnit.Word)
            {
                var words = _words[kind];
                if (number + points > words.Length) return ErrorResponse(header, 0xC056, command, sub);

                if (isRead)
                {
                    var data = new byte[points * 2];
                    for (int i = 0; i < points; i++)
                    {
                        data[2 * i] = (byte)(words[number + i] & 0xFF);
                        data[2 * i + 1] = (byte)(words[number + i] >> 8);
                    }
                    Emit($"ĐỌC word {MelsecDevice.PrefixOf(kind)}{number} x{points}");
                    return OkResponse(header, data);
                }

                if (dataLength != points * 2) return ErrorResponse(header, 0xC061, command, sub);
                for (int i = 0; i < points; i++)
                    words[number + i] = (ushort)(body[12 + 2 * i] | (body[12 + 2 * i + 1] << 8));
                Emit($"GHI word {MelsecDevice.PrefixOf(kind)}{number} x{points}");
                return OkResponse(header, Array.Empty<byte>());
            }

            // ---- Thiết bị bit truy cập theo word: mỗi word = 16 bit liên tiếp, bit thấp = thiết bị số nhỏ ----
            {
                var bits = _bits[kind];
                if (number % 16 != 0) return ErrorResponse(header, 0xC05C, command, sub);
                if (number + points * 16 > bits.Length) return ErrorResponse(header, 0xC056, command, sub);

                if (isRead)
                {
                    var data = new byte[points * 2];
                    for (int w = 0; w < points; w++)
                    {
                        ushort value = 0;
                        for (int bit = 0; bit < 16; bit++)
                            if (bits[number + w * 16 + bit]) value |= (ushort)(1 << bit);
                        data[2 * w] = (byte)(value & 0xFF);
                        data[2 * w + 1] = (byte)(value >> 8);
                    }
                    return OkResponse(header, data);
                }

                if (dataLength != points * 2) return ErrorResponse(header, 0xC061, command, sub);
                for (int w = 0; w < points; w++)
                {
                    ushort value = (ushort)(body[12 + 2 * w] | (body[12 + 2 * w + 1] << 8));
                    for (int bit = 0; bit < 16; bit++)
                        bits[number + w * 16 + bit] = (value & (1 << bit)) != 0;
                }
                return OkResponse(header, Array.Empty<byte>());
            }
        }
    }

    /// <summary>Khung trả lời thành công: D0 00 + phần địa chỉ lặp lại + độ dài + end code 0000 + dữ liệu.</summary>
    private static byte[] OkResponse(byte[] requestHeader, byte[] data)
    {
        int length = 2 + data.Length; // end code (2) + dữ liệu
        var frame = new byte[9 + length];
        WriteResponseHeader(frame, requestHeader, length);
        // end code = 0x0000 (đã là 0 sẵn)
        Buffer.BlockCopy(data, 0, frame, 11, data.Length);
        return frame;
    }

    /// <summary>Khung báo lỗi: end code khác 0 + 9 byte thông tin (network, PLC, I/O, station, lệnh, lệnh phụ).</summary>
    private static byte[] ErrorResponse(byte[] requestHeader, ushort endCode, ushort command, ushort sub)
    {
        const int infoLength = 9;
        int length = 2 + infoLength;
        var frame = new byte[9 + length];
        WriteResponseHeader(frame, requestHeader, length);
        frame[9] = (byte)(endCode & 0xFF);
        frame[10] = (byte)(endCode >> 8);
        frame[11] = requestHeader[2];   // Network No.
        frame[12] = requestHeader[3];   // PLC No.
        frame[13] = requestHeader[4];   // I/O No. thấp
        frame[14] = requestHeader[5];   // I/O No. cao
        frame[15] = requestHeader[6];   // Station No.
        frame[16] = (byte)(command & 0xFF);
        frame[17] = (byte)(command >> 8);
        frame[18] = (byte)(sub & 0xFF);
        frame[19] = (byte)(sub >> 8);
        return frame;
    }

    private static void WriteResponseHeader(byte[] frame, byte[] requestHeader, int length)
    {
        frame[0] = 0xD0; frame[1] = 0x00;        // Subheader trả lời
        frame[2] = requestHeader[2];             // Network No.  (lặp lại từ yêu cầu)
        frame[3] = requestHeader[3];             // PLC No.
        frame[4] = requestHeader[4];             // I/O No. thấp
        frame[5] = requestHeader[5];             // I/O No. cao
        frame[6] = requestHeader[6];             // Station No.
        frame[7] = (byte)(length & 0xFF);
        frame[8] = (byte)(length >> 8);
    }

    private void Emit(string message)
    {
        try { Log?.Invoke(message); } catch { }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SimulatedPlc));
    }
}