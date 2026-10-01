// ==================== Vai trò chính:                Mã hoá / giải mã khung MC Protocol 3E dạng nhị phân (đọc/ghi hàng loạt word và bit của PLC Mitsubishi dòng Q/L)
// ==================== Thành phần / Class tiêu biểu: McProtocol3E
// ==================== Phụ thuộc vào:                PlcAddress
// ==================== Pattern / Kỹ thuật nổi bật:   Lớp static thuần (không I/O) => kiểm thử được bằng cách so từng byte với khung mẫu của tài liệu Mitsubishi
//
// VỊ TRÍ ĐẶT FILE: thư mục Plc/ — namespace VisionFlow.Hardware.Plc.
//
// CẤU TRÚC KHUNG YÊU CẦU (PC -> PLC), mọi số nhiều byte theo thứ tự byte THẤP TRƯỚC (little-endian):
//   [0..1]  50 00         Subheader: khung 3E nhị phân, yêu cầu
//   [2]     00            Network No.        (0 = mạng của chính PLC)
//   [3]     FF            PLC No.            (FF = trạm chính PLC đang nối)
//   [4..5]  FF 03         Request destination module I/O No. (0x03FF = CPU của trạm chính)
//   [6]     00            Request destination module station No.
//   [7..8]  LL LL         Độ dài dữ liệu = số byte TỪ [9] đến hết khung
//   [9..10] 10 00         Monitoring timer (đơn vị 250 ms; 0x0010 = 4 giây PLC chờ xử lý)
//   [11..12] 01 04        Lệnh: 0x0401 = đọc hàng loạt, 0x1401 = ghi hàng loạt
//   [13..14] 00 00        Lệnh phụ: 0x0000 = theo WORD, 0x0001 = theo BIT
//   [15..17] nn nn nn     Số thiết bị đầu tiên (3 byte)
//   [18]    A8            Mã thiết bị (D = 0xA8, M = 0x90, X = 0x9C, Y = 0x9D ...)
//   [19..20] pp pp        Số điểm (points) cần đọc/ghi
//   [21..]  dữ liệu       Chỉ khi GHI
//
// CẤU TRÚC KHUNG TRẢ LỜI (PLC -> PC):
//   [0..1]  D0 00         Subheader trả lời
//   [2..6]  ...           Network / PLC / I-O / station (PLC lặp lại)
//   [7..8]  LL LL         Độ dài = số byte từ [9] đến hết
//   [9..10] ee ee         End code: 0x0000 = thành công, khác 0 = lỗi
//   [11..]  dữ liệu       Khi đọc thành công: dữ liệu; khi lỗi: 9 byte thông tin lỗi

using System;

namespace VisionFlow.Hardware.Plc;

/// <summary>Bộ mã hoá/giải mã khung MC Protocol 3E nhị phân. Không có I/O nên kiểm thử được bằng so sánh byte.</summary>
public static class McProtocol3E
{
    public const ushort CommandBatchRead = 0x0401;
    public const ushort CommandBatchWrite = 0x1401;
    public const ushort SubcommandWord = 0x0000;
    public const ushort SubcommandBit = 0x0001;

    /// <summary>Số byte phần đầu khung trả lời (từ Subheader đến hết trường độ dài).</summary>
    public const int ResponseHeaderLength = 9;

    /// <summary>Giới hạn an toàn cho độ dài phản hồi: lớn hơn thế này coi là khung rác (chống treo/đọc vô hạn khi dữ liệu hỏng).</summary>
    public const int MaxResponseBodyLength = 8192;

    /// <summary>Số word tối đa mỗi lần đọc/ghi hàng loạt theo tài liệu (960).</summary>
    public const int MaxWordPoints = 960;

    /// <summary>Số bit tối đa mỗi lần đọc/ghi (chọn mức thấp hơn của lệnh ghi = 3584 cho an toàn cả hai chiều).</summary>
    public const int MaxBitPoints = 3584;

    private const ushort MonitoringTimer = 0x0010; // 16 x 250 ms = 4 giây

    /// <summary>Dựng khung yêu cầu đọc/ghi hàng loạt.</summary>
    /// <param name="command">0x0401 đọc hoặc 0x1401 ghi.</param>
    /// <param name="subcommand">0x0000 theo word hoặc 0x0001 theo bit.</param>
    /// <param name="start">Thiết bị đầu tiên.</param>
    /// <param name="points">Số điểm (word hoặc bit tuỳ subcommand).</param>
    /// <param name="data">Dữ liệu cần ghi (rỗng khi đọc).</param>
    public static byte[] BuildRequest(ushort command, ushort subcommand, PlcAddress start, int points, ReadOnlySpan<byte> data)
    {
        if (points < 1 || points > 0xFFFF)
            throw new ArgumentOutOfRangeException(nameof(points));

        // Độ dài = timer(2) + lệnh(2) + lệnh phụ(2) + số thiết bị(3) + mã thiết bị(1) + số điểm(2) + dữ liệu
        int length = 12 + data.Length;
        var frame = new byte[9 + length];

        frame[0] = 0x50; frame[1] = 0x00;          // Subheader yêu cầu 3E
        frame[2] = 0x00;                           // Network No.
        frame[3] = 0xFF;                           // PLC No.
        frame[4] = 0xFF; frame[5] = 0x03;          // Module I/O No. = 0x03FF
        frame[6] = 0x00;                           // Station No.
        frame[7] = (byte)(length & 0xFF);          // Độ dài (byte thấp)
        frame[8] = (byte)(length >> 8);            // Độ dài (byte cao)
        frame[9] = (byte)(MonitoringTimer & 0xFF);
        frame[10] = (byte)(MonitoringTimer >> 8);
        frame[11] = (byte)(command & 0xFF);
        frame[12] = (byte)(command >> 8);
        frame[13] = (byte)(subcommand & 0xFF);
        frame[14] = (byte)(subcommand >> 8);
        frame[15] = (byte)(start.Number & 0xFF);          // Số thiết bị đầu: 3 byte, byte thấp trước
        frame[16] = (byte)((start.Number >> 8) & 0xFF);
        frame[17] = (byte)((start.Number >> 16) & 0xFF);
        frame[18] = start.DeviceCode;
        frame[19] = (byte)(points & 0xFF);
        frame[20] = (byte)(points >> 8);
        data.CopyTo(frame.AsSpan(21));
        return frame;
    }

    /// <summary>Đổi mảng word thành byte theo thứ tự byte thấp trước (định dạng dữ liệu word của MC Protocol).</summary>
    public static byte[] WordsToBytes(System.Collections.Generic.IReadOnlyList<ushort> words)
    {
        var bytes = new byte[words.Count * 2];
        for (int i = 0; i < words.Count; i++)
        {
            bytes[2 * i] = (byte)(words[i] & 0xFF);
            bytes[2 * i + 1] = (byte)(words[i] >> 8);
        }
        return bytes;
    }

    /// <summary>Đổi byte (thấp trước) thành mảng word.</summary>
    public static ushort[] BytesToWords(ReadOnlySpan<byte> bytes)
    {
        var words = new ushort[bytes.Length / 2];
        for (int i = 0; i < words.Length; i++)
            words[i] = (ushort)(bytes[2 * i] | (bytes[2 * i + 1] << 8));
        return words;
    }

    /// <summary>
    /// Gói bit theo lệnh phụ 0x0001 (nhị phân): mỗi bit chiếm 4 bit (nibble) — 1 = ON, 0 = OFF;
    /// điểm thứ nhất nằm ở NIBBLE CAO của byte đầu, điểm thứ hai ở nibble thấp... Số điểm lẻ thì nibble cuối để 0.
    /// </summary>
    public static byte[] PackBits(System.Collections.Generic.IReadOnlyList<bool> bits)
    {
        var bytes = new byte[(bits.Count + 1) / 2];
        for (int i = 0; i < bits.Count; i++)
        {
            if (!bits[i]) continue;
            bytes[i / 2] |= (i % 2 == 0) ? (byte)0x10 : (byte)0x01;
        }
        return bytes;
    }

    /// <summary>Giải mã <paramref name="count"/> bit từ dữ liệu nibble (ngược của <see cref="PackBits"/>).</summary>
    public static bool[] UnpackBits(ReadOnlySpan<byte> data, int count)
    {
        var bits = new bool[count];
        for (int i = 0; i < count; i++)
        {
            byte b = data[i / 2];
            int nibble = (i % 2 == 0) ? (b >> 4) & 0x0F : b & 0x0F;
            bits[i] = nibble != 0;
        }
        return bits;
    }

    /// <summary>Mô tả ngắn cho end code thường gặp (tra manual "MELSEC-Q/L MC Protocol Reference" khi gặp mã khác).</summary>
    public static string DescribeEndCode(ushort endCode) => endCode switch
    {
        0xC050 => "dữ liệu ASCII không đổi được sang nhị phân",
        0xC051 or 0xC052 or 0xC053 or 0xC054 => "số điểm đọc/ghi vượt giới hạn cho phép",
        0xC056 => "địa chỉ thiết bị vượt phạm vi của PLC",
        0xC058 => "độ dài dữ liệu không khớp nội dung yêu cầu",
        0xC059 => "lệnh hoặc lệnh phụ không được hỗ trợ",
        0xC05B => "CPU không đọc/ghi được thiết bị này",
        0xC05C => "nội dung yêu cầu sai (ví dụ kiểu truy cập không hợp thiết bị)",
        0xC060 => "nội dung yêu cầu sai (ví dụ dữ liệu bit)",
        0xC061 => "độ dài yêu cầu không khớp",
        0xC200 or 0xC201 => "lỗi/khoá mật khẩu từ xa của PLC",
        _ => "mã lỗi khác — tra trong manual MC Protocol của Mitsubishi"
    };
}