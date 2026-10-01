// === Vai trò chính:                Biểu diễn địa chỉ thiết bị PLC Mitsubishi (D1000, M100, X1F...) và bảng mã thiết bị của MC Protocol
// === Thành phần / Class tiêu biểu: PlcAddress, PlcDeviceKind, PlcDeviceUnit, MelsecDevice
// === Phụ thuộc vào:                Không phụ thuộc gì (0 dependency)
// === Pattern / Kỹ thuật nổi bật:   Value Object bất biến (readonly record struct) + fail-early: địa chỉ sai bị từ chối ngay khi đọc cấu hình, không đợi đến lúc gửi xuống PLC
//
// VỊ TRÍ ĐẶT FILE: project chứa thư mục Camera (Hardware), TẠO thư mục mới Plc/ — namespace VisionFlow.Hardware.Plc.

using System;
using System.Globalization;

namespace VisionFlow.Hardware.Plc;

/// <summary>Loại thiết bị (device) của PLC dòng Q/L mà bản này hỗ trợ.</summary>
public enum PlcDeviceKind
{
    D,   // Data register        (word, số thập phân)
    W,   // Link register        (word, số HEX)
    R,   // File register        (word, số thập phân)
    ZR,  // File register liên tục (word, số thập phân)
    M,   // Internal relay       (bit,  số thập phân)
    L,   // Latch relay          (bit,  số thập phân)
    B,   // Link relay           (bit,  số HEX)
    X,   // Input                (bit,  số HEX)
    Y    // Output               (bit,  số HEX)
}

/// <summary>Đơn vị truy cập tự nhiên của thiết bị: theo word (16 bit) hay theo bit.</summary>
public enum PlcDeviceUnit { Word, Bit }

/// <summary>Bảng tra thông tin từng loại thiết bị theo tài liệu "MELSEC-Q/L MC Protocol Reference Manual".</summary>
public static class MelsecDevice
{
    // (loại, tiền tố, mã thiết bị nhị phân, đơn vị, số địa chỉ viết dạng HEX?)
    private static readonly (PlcDeviceKind Kind, string Prefix, byte Code, PlcDeviceUnit Unit, bool Hex)[] Table =
    {
        (PlcDeviceKind.D,  "D",  0xA8, PlcDeviceUnit.Word, false),
        (PlcDeviceKind.W,  "W",  0xB4, PlcDeviceUnit.Word, true),
        (PlcDeviceKind.R,  "R",  0xAF, PlcDeviceUnit.Word, false),
        (PlcDeviceKind.ZR, "ZR", 0xB0, PlcDeviceUnit.Word, false),
        (PlcDeviceKind.M,  "M",  0x90, PlcDeviceUnit.Bit,  false),
        (PlcDeviceKind.L,  "L",  0x92, PlcDeviceUnit.Bit,  false),
        (PlcDeviceKind.B,  "B",  0xA0, PlcDeviceUnit.Bit,  true),
        (PlcDeviceKind.X,  "X",  0x9C, PlcDeviceUnit.Bit,  true),
        (PlcDeviceKind.Y,  "Y",  0x9D, PlcDeviceUnit.Bit,  true),
    };

    /// <summary>Tra cứu thông tin thiết bị PLC từ bảng tra.</summary>
    private static (PlcDeviceKind Kind, string Prefix, byte Code, PlcDeviceUnit Unit, bool Hex) Get(PlcDeviceKind kind)
    {
        foreach (var row in Table)
            if (row.Kind == kind) return row; // Trả về dòng cấu hình nếu tìm thấy loại thiết bị khớp
        throw new ArgumentOutOfRangeException(nameof(kind), kind, "Loại thiết bị chưa được hỗ trợ."); // Ném ngoại lệ nếu loại thiết bị không tồn tại trong bảng
    }

    public static byte CodeOf(PlcDeviceKind kind) => Get(kind).Code;          // Lấy mã byte MC Protocol của thiết bị
    public static PlcDeviceUnit UnitOf(PlcDeviceKind kind) => Get(kind).Unit; // Lấy đơn vị truy cập (Word hoặc Bit) của thiết bị
    public static string PrefixOf(PlcDeviceKind kind) => Get(kind).Prefix;   // Lấy tiền tố ký tự của thiết bị (ví dụ: D, M, X)
    public static bool IsHex(PlcDeviceKind kind) => Get(kind).Hex;           // Kiểm tra xem phần số địa chỉ có dùng hệ Hex hay không

    /// <summary>Tra ngược từ mã thiết bị trong khung MC Protocol (dùng cho phía PLC giả lập).</summary>
    public static bool TryFromCode(byte code, out PlcDeviceKind kind)
    {
        foreach (var row in Table)
        {
            if (row.Code == code) { kind = row.Kind; return true; }
        }
        kind = default;
        return false;
    }

    /// <summary>Tra từ tiền tố chữ ("D", "ZR", "x"...) — không phân biệt hoa/thường.</summary>
    internal static bool TryFromPrefix(string text, out PlcDeviceKind kind, out int prefixLength)
    {
        // Duyệt tiền tố dài trước để "ZR10" không bị hiểu nhầm; hiện chỉ "ZR" dài 2 ký tự
        foreach (var row in Table)
        {
            if (row.Prefix.Length == 2 && text.StartsWith(row.Prefix, StringComparison.OrdinalIgnoreCase))
            {
                kind = row.Kind; prefixLength = 2; return true;
            }
        }
        foreach (var row in Table)
        {
            if (row.Prefix.Length == 1 && text.StartsWith(row.Prefix, StringComparison.OrdinalIgnoreCase))
            {
                kind = row.Kind; prefixLength = 1; return true;
            }
        }
        kind = default; prefixLength = 0;
        return false;
    }
}

/// <summary>
/// Địa chỉ một thiết bị PLC, ví dụ <c>D1000</c>, <c>M100</c>, <c>X1F</c> (X/Y/B/W viết số dạng HEX theo quy ước Mitsubishi).
/// Bất biến (readonly) nên dùng làm khoá/cấu hình an toàn.
/// </summary>
public readonly record struct PlcAddress(PlcDeviceKind Kind, int Number)
{
    /// <summary>Số thiết bị tối đa mà khung 3E biểu diễn được (3 byte).</summary>
    public const int MaxNumber = 0xFFFFFF;

    public PlcDeviceUnit Unit => MelsecDevice.UnitOf(Kind);
    public byte DeviceCode => MelsecDevice.CodeOf(Kind);

    /// <summary>Địa chỉ cách địa chỉ này <paramref name="delta"/> thiết bị (cộng số học trên phần số).</summary>
    public PlcAddress Offset(int delta) => this with { Number = Number + delta };

    /// <summary>Đọc "D1000" -> PlcAddress; sai định dạng thì ném FormatException với thông báo rõ ràng.</summary>
    public static PlcAddress Parse(string text)
    {
        if (!TryParse(text, out var address)) // Thử phân tích chuỗi, nếu thất bại (không đúng định dạng)...
            throw new FormatException($"Địa chỉ PLC '{text}' không hợp lệ. Ví dụ đúng: D1000, M100, X1F, W1A0."); // ...ném ngoại lệ FormatException kèm thông báo hướng dẫn
        return address; // Trả về đối tượng PlcAddress đã được phân tích thành công
    }

    public static bool TryParse(string? text, out PlcAddress address)
    {
        address = default; // Gán giá trị mặc định cho out
        if (string.IsNullOrWhiteSpace(text)) return false; // Thoát nếu chuỗi rỗng/null

        text = text.Trim(); // Xóa khoảng trắng thừa
        if (!MelsecDevice.TryFromPrefix(text, out var kind, out int prefixLength)) return false; // Thoát nếu không khớp tiền tố nào

        string numberText = text.Substring(prefixLength); // Cắt lấy phần số sau tiền tố
        if (numberText.Length == 0) return false; // Thoát nếu thiếu phần số

        var style = MelsecDevice.IsHex(kind) ? NumberStyles.AllowHexSpecifier : NumberStyles.None; // Chọn kiểu số Hex hay Thập phân
        if (!int.TryParse(numberText, style, CultureInfo.InvariantCulture, out int number)) return false; // Thoát nếu parse số thất bại
        if (number < 0 || number > MaxNumber) return false; // Thoát nếu vượt quá giới hạn cho phép

        address = new PlcAddress(kind, number); // Tạo đối tượng địa chỉ hoàn chỉnh
        return true; // Trả về thành công
    }

    /// <summary>Dạng chuỗi chuẩn: "D1000", "X1F" (hex viết hoa).</summary>
    /// Ghi đè (override) phương thức ToString() mặc định của C# (vốn chỉ trả về tên namespace/class).
    /// Mục đích: Khi bạn gọi address.ToString() hoặc in đối tượng ra (ví dụ: Console.WriteLine(address) hoặc đưa vào chuỗi 
    ///           nội suy $"Địa chỉ: {address}"), nó sẽ tự động trả về định dạng chuỗi địa chỉ PLC dễ đọc.
    public override string ToString()
        => MelsecDevice.PrefixOf(Kind) + (MelsecDevice.IsHex(Kind)   // Lấy tiền tố rồi nối với phần số (định dạng Hex nếu thiết bị dùng hệ Hex...)
            ? Number.ToString("X", CultureInfo.InvariantCulture)     // ...chuyển sang chuỗi Hex viết hoa
                // Ý nghĩa của chữ "X": Bảo C# dịch giá trị số nguyên hiện tại (ví dụ: 31) sang hệ 16.
                // Chữ X viết hoa quy định các chữ cái đại diện từ 10 đến 15 sẽ được hiển thị bằng chữ in hoa (A, B, C, D, E, F).
                // (Nếu bạn dùng chữ x thường, kết quả sẽ ra chữ thường như 1f thay vì 1F).
            : Number.ToString(CultureInfo.InvariantCulture));        // ...hoặc chuyển sang chuỗi số thập phân thông thường
}  