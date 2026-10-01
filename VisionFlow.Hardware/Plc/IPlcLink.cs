// ==================== Vai trò chính:                Hợp đồng chuẩn để phần mềm nói chuyện với PLC (đọc/ghi word, bit) mà không quan tâm bên dưới là PLC thật hay giả lập
// ==================== Thành phần / Class tiêu biểu: IPlcLink, PlcLinkState, PlcLinkException (+ 3 lớp con), PlcLinkExtensions
// ==================== Phụ thuộc vào:                PlcAddress
// ==================== Pattern / Kỹ thuật nổi bật:   Dependency Inversion (giống ICamera / IRobot) + phân loại exception để tầng trên biết lỗi nào cần kết nối lại
//
// VỊ TRÍ ĐẶT FILE: thư mục Plc/ (cùng nơi với PlcAddress.cs) — namespace VisionFlow.Hardware.Plc.

using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VisionFlow.Hardware.Plc;

/// <summary>Trạng thái đường truyền tới PLC.</summary>
public enum PlcLinkState
{
    /// <summary>Chưa kết nối (hoặc đã chủ động ngắt).</summary>
    Disconnected,

    /// <summary>Đang mở kết nối TCP.</summary>
    Connecting,

    /// <summary>Lần kết nối/lần trao đổi gần nhất thành công.</summary>
    Connected,

    /// <summary>Đường truyền hỏng (timeout, rớt mạng, khung sai). Phải gọi ConnectAsync() lại mới dùng tiếp được.</summary>
    Faulted
}

/// <summary>Lỗi gốc của lớp giao tiếp PLC.</summary>
public class PlcLinkException : Exception
{
    public PlcLinkException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Mất kết nối / chưa kết nối / phản hồi sai khung: đường truyền không còn tin cậy, cần kết nối lại.</summary>
public sealed class PlcConnectionException : PlcLinkException
{
    public PlcConnectionException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>PLC không trả lời kịp thời gian cho phép. Đường truyền bị đóng (vì phản hồi đến muộn sẽ làm lệch khung).</summary>
public sealed class PlcTimeoutException : PlcLinkException
{
    public PlcTimeoutException(string message) : base(message) { }
}

/// <summary>
/// PLC đã trả lời nhưng báo lỗi (end code khác 0), ví dụ địa chỉ vượt phạm vi.
/// Đường truyền vẫn tốt — KHÔNG cần kết nối lại, chỉ cần sửa yêu cầu.
/// </summary>
public sealed class PlcProtocolException : PlcLinkException
{
    public ushort EndCode { get; }

    public PlcProtocolException(ushort endCode, string message) : base(message)
    {
        EndCode = endCode;
    }
}

/// <summary>
/// Đường truyền tới một PLC. Mọi thao tác là bất đồng bộ (mạng có thể chậm) và được tuần tự hoá bên trong:
/// gọi từ nhiều luồng cùng lúc vẫn an toàn, các yêu cầu xếp hàng chờ nhau.
/// <para>
/// LƯU Ý: <see cref="IsConnected"/> chỉ phản ánh lần trao đổi gần nhất thành công, KHÔNG bảo đảm dây mạng còn sống ngay lúc này
/// (giống cảnh báo <c>TcpClient.Connected</c> ở Buổi 118). Muốn biết PLC còn sống phải trao đổi thật (heartbeat) — việc của bước handshake.
/// </para>
/// </summary>
public interface IPlcLink : IDisposable
{
    string Name { get; }
    PlcLinkState State { get; }
    bool IsConnected { get; }

    /// <summary>Bắn mỗi khi State đổi. Được gọi trên thread bất kỳ — handler cập nhật UI phải tự Dispatcher.Invoke.</summary>
    event Action<PlcLinkState>? StateChanged;

    /// <summary>Mở kết nối TCP tới PLC (đã kết nối rồi thì không làm gì). Ném PlcTimeoutException / PlcConnectionException nếu thất bại.</summary>
    Task ConnectAsync(CancellationToken ct = default);
    // Task là kiểu dữ liệu đại diện cho một tác vụ bất đồng bộ(Asynchronous Operation).
    // tương đương với void nhưng dành cho lập trình bất đồng bộ
    // Khi một phương thức có chữ Async ở cuối và trả về Task, điều đó có nghĩa là phương thức đó chạy ngầm, không làm đơ giao diện (UI),
    // và cho phép sử dụng từ khóa await để chờ nó thực thi xong

    /// <summary>Đóng kết nối chủ động.</summary>
    Task DisconnectAsync();

    /// <summary>Đọc <paramref name="count"/> word liên tiếp bắt đầu từ <paramref name="start"/>.</summary>
    Task<ushort[]> ReadWordsAsync(PlcAddress start, int count, CancellationToken ct = default);

    /// <summary>Ghi các word liên tiếp bắt đầu từ <paramref name="start"/>.</summary>
    Task WriteWordsAsync(PlcAddress start, IReadOnlyList<ushort> values, CancellationToken ct = default);

    /// <summary>Đọc <paramref name="count"/> bit liên tiếp (chỉ cho thiết bị bit: M, L, B, X, Y).</summary>
    Task<bool[]> ReadBitsAsync(PlcAddress start, int count, CancellationToken ct = default);

    /// <summary>Ghi các bit liên tiếp (chỉ cho thiết bị bit: M, L, B, X, Y).</summary>
    Task WriteBitsAsync(PlcAddress start, IReadOnlyList<bool> values, CancellationToken ct = default);
}

/// <summary>Hàm tiện ích: đọc/ghi một giá trị đơn và số nguyên 32-bit (2 word).</summary>
public static class PlcLinkExtensions
{
    public static async Task<ushort> ReadWordAsync(this IPlcLink link, PlcAddress address, CancellationToken ct = default)
        => (await link.ReadWordsAsync(address, 1, ct).ConfigureAwait(false))[0];
    // - static: Đây là bắt buộc đối với một Extension Method. Nó giúp mở rộng tính năng cho interface IPlcLink mà ko cần sửa đổi code gốc của interface đó.
    // - async Task<ushort>: Phương thức này chạy bất đồng bộ
    //    -> Kiểu trả về là Task<ushort>, nghĩa là khi hàm chạy xong và được await, nó sẽ trả về một giá trị kiểu ushort (số nguyên không dấu 16-bit)
    // - Đây là cú pháp của Extension Method. Nhờ có chữ this, bạn có thể gọi hàm này trực tiếp từ một đối tượng
    //   kết nối PLC như sau: link.ReadWordAsync(address) thay vì phải gọi theo kiểu static thông thường.
    // - PlcAddress address: Tham số truyền vào xác định địa chỉ ô nhớ cần đọc trong PLC (ví dụ: thanh ghi D1000).
    // - CancellationToken ct = default: Tham số tùy chọn dùng để hủy bỏ tác vụ (Cancellation Token). Nếu không truyền vào,
    //   nó sẽ lấy giá trị mặc định là default (tức là không hủy). Rất hữu ích khi người dùng muốn dừng ngang quá trình đọc ghi hoặc khi form/app bị đóng.
    // - link.ReadWordsAsync(address, 1, ct): Chiến thuật Tái sử dụng code (DRY):
    //    -> Thay vì viết lại toàn bộ logic kết nối mạng phức tạp để đọc 1 Word, lập trình viên tận dụng lại một hàm có sẵn là ReadWordsAsync (hàm đọc nhiều Word).
    // - 1: "Hãy đọc bắt đầu từ địa chỉ này, với số lượng là 1 Word thôi" -> Từ đó biến việc đọc nhiều word thành đọc 1 word.
    // - await ...: Tạm dừng việc thực thi dòng code này đến khi PLC phản hồi dữ liệu trả về mảng các ushort mà không làm đơ giao diện hay thread chính.
    // - .ConfigureAwait(false): Kỹ thuật tối ưu hiệu năng:
    //    -> Mặc định khi dùng await, C# sẽ cố gắng mang luồng xử lý quay trở lại thread ban đầu (ví dụ: UI Thread trong WPF/WinForms).
    // - Khi viết code thư viện hoặc helper ngầm, việc quay lại UI thread là không cần thiết và có thể gây lãng phí tài nguyên hoặc gây deadlock.
    //    -> .ConfigureAwait(false) nói với chương trình rằng: "Cứ chạy tiếp trên bất kỳ thread nào rảnh rỗi đi, không cần quay lại thread cũ đâu".
    // - [0] (Lấy phần tử đầu tiên): Vì ở bước gọi ReadWordsAsync(address, 1, ...) ta chỉ yêu cầu đọc 1 word,
    //   nên kết quả trả về là một mảng ushort[] có độ dài bằng 1 (chứa đúng 1 phần tử ở vị trí index 0).

    public static Task WriteWordAsync(this IPlcLink link, PlcAddress address, ushort value, CancellationToken ct = default)
        => link.WriteWordsAsync(address, new[] { value }, ct);
    // public static Task: Hàm bất đồng bộ chạy ngầm, không trả về giá trị dữ liệu nào sau khi ghi xong (chỉ trả về Task để báo hiệu đã hoàn thành).

    public static async Task<bool> ReadBitAsync(this IPlcLink link, PlcAddress address, CancellationToken ct = default)
        => (await link.ReadBitsAsync(address, 1, ct).ConfigureAwait(false))[0];

    public static Task WriteBitAsync(this IPlcLink link, PlcAddress address, bool value, CancellationToken ct = default)
        => link.WriteBitsAsync(address, new[] { value }, ct);

    /// <summary>
    /// Đọc số nguyên 32-bit có dấu (kiểu DINT của PLC) từ 2 word liên tiếp.
    /// Quy ước Mitsubishi: word ở địa chỉ THẤP là 16 bit thấp (little-endian theo word).
    /// </summary>
    public static async Task<int> ReadInt32Async(this IPlcLink link, PlcAddress address, CancellationToken ct = default)
    {
        var words = await link.ReadWordsAsync(address, 2, ct).ConfigureAwait(false);
        return unchecked((int)((uint)words[0] | ((uint)words[1] << 16)));
        // ((uint)words[1] << 16): Lấy giá trị của Word cao (words[1]), ép sang kiểu số không dấu 32-bit (uint), sau đó dịch trái 16 bit để nhường chỗ cho phần thấp.
        // | (Bitwise OR): Kết hợp phần thấp (words[0]) vào 16-bit trống ở bên phải. Lúc này ta được một số nguyên 32-bit hoàn chỉnh.
        // Ép kết quả cuối cùng từ kiểu không dấu (uint) về lại kiểu có dấu (int).
        // Từ khóa unchecked dùng để tắt cơ chế kiểm tra tràn bộ nhớ (overflow check) của C# khi thực hiện ép kiểu các bit dấu,
        //  -> giúp tránh các ngoại lệ tràn bộ nhớ (overflow) trong trường hợp giá trị nằm ở biên giới hạn.
    }

    /// <summary>Ghi số nguyên 32-bit có dấu vào 2 word liên tiếp (word thấp trước).</summary>
    public static Task WriteInt32Async(this IPlcLink link, PlcAddress address, int value, CancellationToken ct = default)
    {
        uint raw = unchecked((uint)value);
        return link.WriteWordsAsync(address, new[] { (ushort)(raw & 0xFFFF), (ushort)(raw >> 16) }, ct);
    }
    // - public static Task: Hàm bất đồng bộ chạy ngầm, không trả về giá trị dữ liệu nào sau khi ghi xong (chỉ trả về Task để báo hiệu đã hoàn thành).
    // - uint raw = unchecked((uint)value); Chuyển đổi số nguyên có dấu (int) sang số không dấu (uint) để việc xử lý các
    //    phép toán bit (dịch bit, lấy mặt nạ bit) không bị ảnh hưởng bởi bit dấu âm/dương
    // - (ushort)(raw & 0xFFFF): giữ lại đúng 16 bit thấp ở bên phải và xóa sạch 16 bit cao bên trái.
    //    -> Ép về kiểu ushort và đặt ở vị trí đầu tiên trong mảng (sẽ được ghi vào địa chỉ address, ví dụ D1000).
    // - (ushort)(raw >> 16): dịch chuyển 16 bit cao sang phải để đưa chúng về vùng thấp.
    //    -> Ép về kiểu ushort và đặt ở vị trí thứ hai trong mảng (sẽ được ghi vào địa chỉ tiếp theo, ví dụ D1001).
}