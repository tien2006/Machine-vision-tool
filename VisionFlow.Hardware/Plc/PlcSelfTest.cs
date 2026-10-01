// ==================== Vai trò chính:                Phép thử tự động một chạm cho đường truyền PLC: kết nối -> đọc -> (tuỳ chọn) ghi thử và đọc lại -> đo thời gian phản hồi
// ==================== Thành phần / Class tiêu biểu: PlcSelfTest, PlcSelfTestResult
// ==================== Phụ thuộc vào:                PlcLinkHost, IPlcLink, PlcAddress
// ==================== Pattern / Kỹ thuật nổi bật:   Diagnostic helper — trả về kết quả dạng dữ liệu (không tự hiện UI) để nút bấm, log hay unit test đều dùng được
//
// VỊ TRÍ ĐẶT FILE: thư mục Plc/ — namespace VisionFlow.Hardware.Plc.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace VisionFlow.Hardware.Plc;

/// <summary>Kết quả phép thử PLC.</summary>
public sealed record PlcSelfTestResult(bool Success, string Summary, IReadOnlyList<string> Lines);

public static class PlcSelfTest
{
    private const ushort Pattern = 0x1234;   // Giá trị mẫu dùng cho phép thử ghi (chỉ ghi vào ScratchAddress nếu được cấu hình)
    private const int TimingSamples = 10;    // Số lần đọc để đo thời gian phản hồi

    /// <summary>
    /// Chạy phép thử. AN TOÀN: nếu <c>ScratchAddress</c> để trống thì CHỈ ĐỌC, không ghi gì vào PLC.
    /// Khi có ScratchAddress: đọc giá trị cũ, ghi mẫu, đọc lại so sánh, rồi KHÔI PHỤC giá trị cũ (kể cả khi so sánh sai).
    /// </summary>
    public static async Task<PlcSelfTestResult> RunAsync(PlcLinkHost host, CancellationToken ct = default)
    {
        var lines = new List<string>();

        var link = host.Link;
        if (link is null)
        {
            string reason = host.Errors.Count > 0 ? string.Join("; ", host.Errors) : "chưa có cấu hình";
            return new PlcSelfTestResult(false, $"PLC chưa dùng được: {reason}", lines);
        }

        try
        {
            // 1. Kết nối
            var sw = Stopwatch.StartNew();
            await link.ConnectAsync(ct).ConfigureAwait(false);
            lines.Add($"Kết nối {host.Description}: {sw.ElapsedMilliseconds} ms");

            // 2. Đọc 4 word tại TestAddress
            var words = await link.ReadWordsAsync(host.TestAddress, 4, ct).ConfigureAwait(false);
            lines.Add($"Đọc {host.TestAddress} x4: {string.Join(", ", words)}");

            // 3. Thử ghi (tuỳ chọn)
            string writeNote = "chỉ đọc (chưa cấu hình ScratchAddress)";
            if (host.ScratchAddress is { } scratch)
            {
                ushort original = await link.ReadWordAsync(scratch, ct).ConfigureAwait(false);
                bool matched;
                try
                {
                    await link.WriteWordAsync(scratch, Pattern, ct).ConfigureAwait(false);
                    ushort back = await link.ReadWordAsync(scratch, ct).ConfigureAwait(false);
                    matched = back == Pattern;
                }
                finally
                {
                    // Luôn khôi phục giá trị cũ dù ghi/đọc lỗi giữa chừng
                    try { await link.WriteWordAsync(scratch, original, CancellationToken.None).ConfigureAwait(false); }
                    catch { /* nếu mất kết nối thì đã có lỗi chính báo lên */ }
                }

                if (!matched)
                    return new PlcSelfTestResult(false, $"Ghi {scratch} rồi đọc lại KHÔNG khớp — kiểm tra vùng nhớ này có bị chương trình PLC ghi đè không.", lines);
                writeNote = $"ghi/đọc lại {scratch} OK (đã khôi phục giá trị cũ)";
            }
            lines.Add($"Thử ghi: {writeNote}");

            // 4. Đo thời gian phản hồi
            long min = long.MaxValue, max = 0, total = 0;
            for (int i = 0; i < TimingSamples; i++)
            {
                var t = Stopwatch.StartNew();
                await link.ReadWordsAsync(host.TestAddress, 4, ct).ConfigureAwait(false);
                long ms = t.ElapsedMilliseconds;
                min = Math.Min(min, ms); max = Math.Max(max, ms); total += ms;
            }
            double avg = (double)total / TimingSamples;
            lines.Add($"{TimingSamples} lần đọc: trung bình {avg:0.0} ms, min {min} ms, max {max} ms");

            string summary = $"PLC OK ({host.Description}) | {host.TestAddress}..+3 = {string.Join(",", words)} | {writeNote} | đọc TB {avg:0.0} ms (max {max} ms)";
            return new PlcSelfTestResult(true, summary, lines);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PlcLinkException ex)
        {
            lines.Add($"LỖI: {ex.Message}");
            return new PlcSelfTestResult(false, $"PLC lỗi ({host.Description}): {ex.Message}", lines);
        }
        catch (Exception ex)
        {
            lines.Add($"LỖI: {ex.Message}");
            return new PlcSelfTestResult(false, $"PLC lỗi ({host.Description}): {ex.GetType().Name}: {ex.Message}", lines);
        }
    }
}