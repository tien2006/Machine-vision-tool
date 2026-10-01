// === Vai trò chính:                Định nghĩa "hợp đồng thanh ghi" giữa PLC và VisionFlow: vị trí từng thanh ghi và ý nghĩa các mã Command / Status / Result / Error
// === Thành phần / Class tiêu biểu: PlcRegisterMap, HandshakeCommand, HandshakeStatus, HandshakeResult, HandshakeError
// === Phụ thuộc vào:                PlcAddress
// === Pattern / Kỹ thuật nổi bật:   Một nguồn sự thật duy nhất (single source of truth) cho cả PC lẫn PLC giả lập; 3 địa chỉ gốc cấu hình được, còn bố cục bên trong cố định
//
// VỊ TRÍ ĐẶT FILE: thư mục Plc/ — namespace VisionFlow.Hardware.Plc.
//
// BẢNG THANH GHI (mặc định; 3 địa chỉ gốc đổi được trong appsettings.json, offset bên trong thì cố định):
//
//   KHỐI LỆNH  (PLC ghi, PC đọc)   gốc = D1000
//     +0  D1000  Command      0 = rảnh, 1 = TRIGGER (yêu cầu chụp và kiểm tra một sản phẩm)
//     +1  D1001  Model        số model/recipe (dành cho sau này, hiện PC chưa dùng)
//     +2  D1002  ProductId    mã sản phẩm do PLC đặt, PC lặp lại ở EchoProductId để PLC đối chiếu
//
//   KHỐI TRẠNG THÁI  (PC ghi, PLC đọc)   gốc = D1010
//     +0  D1010  Heartbeat    PC tăng dần đều; PLC thấy đứng yên quá N giây => PC chết/treo
//     +1  D1011  Status       0 Offline | 1 Ready | 2 Busy | 3 Done | 9 Error
//     +2  D1012  Result       0 chưa có | 1 OK | 2 NG          (chỉ hợp lệ khi Status = Done)
//     +3  D1013  ErrorCode    0 = không lỗi; mã khác xem HandshakeError (hợp lệ khi Status = Error)
//     +4  D1014  EchoProductId  bản sao ProductId của lần trigger đang xử lý
//     +5  D1015  CycleMs      thời gian kiểm tra (ms), tối đa 65535
//
//   KHỐI SỐ ĐO  (PC ghi, PLC đọc)   gốc = D1020
//     6 số đo x 2 word (số nguyên 32-bit có dấu = giá trị x ValueScale). Số đo slot n nằm ở gốc + 2*(n-1), word thấp trước.
//     Slot không dùng = 0.

using System;

namespace VisionFlow.Hardware.Plc;

/// <summary>Giá trị của thanh ghi Command (PLC -> PC).</summary>
public static class HandshakeCommand
{
    public const ushort Idle = 0;
    public const ushort Trigger = 1;
}

/// <summary>Giá trị của thanh ghi Status (PC -> PLC).</summary>
public static class HandshakeStatus
{
    /// <summary>PC không chạy handshake (đã dừng hoặc chưa khởi động).</summary>
    public const ushort Offline = 0;
    /// <summary>Sẵn sàng nhận trigger.</summary>
    public const ushort Ready = 1;
    /// <summary>Đang chụp và kiểm tra.</summary>
    public const ushort Busy = 2;
    /// <summary>Đã có kết quả (Result, số đo hợp lệ). PLC đọc xong phải xoá Command về 0.</summary>
    public const ushort Done = 3;
    /// <summary>Lần kiểm tra này thất bại (xem ErrorCode). PLC vẫn phải xoá Command về 0 để PC quay lại Ready.</summary>
    public const ushort Error = 9;
}

/// <summary>Giá trị của thanh ghi Result (PC -> PLC).</summary>
public static class HandshakeResult
{
    public const ushort None = 0;
    public const ushort Ok = 1;
    public const ushort Ng = 2;
}

/// <summary>Mã lỗi ghi ở thanh ghi ErrorCode khi Status = Error.</summary>
public static class HandshakeError
{
    public const ushort None = 0;
    /// <summary>Flow chạy lỗi (camera, node, đồ thị...). Không phải NG: sản phẩm CHƯA được kết luận.</summary>
    public const ushort InspectionFailed = 1;
    /// <summary>Chưa nạp flow kiểm tra.</summary>
    public const ushort FlowNotLoaded = 2;
    /// <summary>Kiểm tra chạy quá InspectionTimeoutMs.</summary>
    public const ushort InspectionTimeout = 3;
    /// <summary>Lỗi không lường trước trong PC.</summary>
    public const ushort InternalError = 4;
    /// <summary>Một số đo quá lớn không đưa vừa số nguyên 32-bit sau khi nhân ValueScale.</summary>
    public const ushort ValueOutOfRange = 5;
}

/// <summary>Vị trí các thanh ghi handshake, dựng từ 3 địa chỉ gốc.</summary>
public sealed class PlcRegisterMap
{
    public const int CommandBlockLength = 3;
    public const int StatusBlockLength = 6;

    /// <summary>Số slot số đo tối đa (khớp với số cổng Value của node Result Publisher).</summary>
    public const int MaxSlots = 6;
    public const int ValuesBlockLength = MaxSlots * 2;

    public PlcAddress CommandBase { get; }
    public PlcAddress StatusBase { get; }
    public PlcAddress ValuesBase { get; }

    // Khối lệnh
    public PlcAddress Command => CommandBase;
    public PlcAddress Model => CommandBase.Offset(1);
    public PlcAddress ProductId => CommandBase.Offset(2);

    // Khối trạng thái
    public PlcAddress Heartbeat => StatusBase;
    public PlcAddress Status => StatusBase.Offset(1);
    public PlcAddress Result => StatusBase.Offset(2);
    public PlcAddress ErrorCode => StatusBase.Offset(3);
    public PlcAddress EchoProductId => StatusBase.Offset(4);
    public PlcAddress CycleMs => StatusBase.Offset(5);

    /// <summary>Địa chỉ word đầu của số đo ở slot <paramref name="slot"/> (1..6); chiếm 2 word.</summary>
    public PlcAddress ValueOf(int slot)
    {
        if (slot < 1 || slot > MaxSlots) throw new ArgumentOutOfRangeException(nameof(slot), $"Slot phải từ 1 đến {MaxSlots}.");
        return ValuesBase.Offset(2 * (slot - 1));
    }

    private PlcRegisterMap(PlcAddress commandBase, PlcAddress statusBase, PlcAddress valuesBase)
    {
        CommandBase = commandBase;
        StatusBase = statusBase;
        ValuesBase = valuesBase;
    }

    /// <summary>
    /// Dựng bản đồ từ ba địa chỉ gốc dạng chuỗi ("D1000"). Trả false kèm lý do nếu địa chỉ sai,
    /// không phải thiết bị word, hoặc ba khối chồng lên nhau (chồng lên nhau sẽ làm PC và PLC ghi đè dữ liệu của nhau).
    /// </summary>
    public static bool TryCreate(string commandBase, string statusBase, string valuesBase, out PlcRegisterMap? map, out string error)
    {
        map = null;

        if (!PlcAddress.TryParse(commandBase, out var c)) { error = $"CommandBase '{commandBase}' không phải địa chỉ PLC hợp lệ."; return false; }
        if (!PlcAddress.TryParse(statusBase, out var s)) { error = $"StatusBase '{statusBase}' không phải địa chỉ PLC hợp lệ."; return false; }
        if (!PlcAddress.TryParse(valuesBase, out var v)) { error = $"ValuesBase '{valuesBase}' không phải địa chỉ PLC hợp lệ."; return false; }

        foreach (var (name, a) in new[] { ("CommandBase", c), ("StatusBase", s), ("ValuesBase", v) })
        {
            if (a.Unit != PlcDeviceUnit.Word)
            {
                error = $"{name} = {a} phải là thiết bị word (D, W, R, ZR).";
                return false;
            }
        }

        if (Overlaps(c, CommandBlockLength, s, StatusBlockLength)) { error = "Khối Command và khối Status chồng lên nhau."; return false; }
        if (Overlaps(c, CommandBlockLength, v, ValuesBlockLength)) { error = "Khối Command và khối Values chồng lên nhau."; return false; }
        if (Overlaps(s, StatusBlockLength, v, ValuesBlockLength)) { error = "Khối Status và khối Values chồng lên nhau."; return false; }

        map = new PlcRegisterMap(c, s, v);
        error = string.Empty;
        return true;
    }

    private static bool Overlaps(PlcAddress a, int aLength, PlcAddress b, int bLength)
        => a.Kind == b.Kind && a.Number < b.Number + bLength && b.Number < a.Number + aLength;
}