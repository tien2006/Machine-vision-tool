// ==================== Vai trò chính:                Kết quả trọn vẹn của MỘT lần kiểm tra (phán quyết, số đo, thời gian, lỗi) — đối tượng duy nhất mà PLC/robot/log/UI cần
// ==================== Thành phần / Class tiêu biểu: InspectionResult
// ==================== Phụ thuộc vào:                Core.Models (Judge), Core.Tools (MeasuredValue), Engine.Execution (NodeExecutionResult)
// ==================== Pattern / Kỹ thuật nổi bật:   Immutable Result Object (record) — không chứa ảnh/Mat nên không có vấn đề vòng đời bộ nhớ
//
// VỊ TRÍ ĐẶT FILE: project Engine, TẠO thư mục mới Runtime/ — namespace VisionFlow.Engine.Runtime.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using VisionFlow.Core.Models;
using VisionFlow.Core.Tools;
using VisionFlow.Engine.Execution;

namespace VisionFlow.Engine.Runtime;

/// <summary>
/// Kết quả một lần <see cref="InspectionService.RunOnceAsync"/>.
/// <para>
/// QUY ƯỚC QUAN TRỌNG (để sau này map sang thanh ghi PLC không nhầm):
/// <list type="bullet">
/// <item><see cref="HasError"/> = false: flow chạy trọn vẹn, <see cref="Judge"/> là OK hoặc NG.</item>
/// <item><see cref="HasError"/> = true: flow KHÔNG cho ra kết luận đáng tin (node lỗi, camera lỗi, đồ thị sai...);
/// <see cref="Judge"/> = None. Tuyệt đối KHÔNG coi đây là NG hay OK — PLC phải nhận mã lỗi riêng.</item>
/// </list>
/// </para>
/// </summary>
public sealed record InspectionResult(
    long Sequence,                                  // Số thứ tự lần kiểm tra kể từ khi nạp flow (1, 2, 3...)
    DateTime Timestamp,                             // Thời điểm bắt đầu lần kiểm tra
    Judge Judge,                                    // OK / NG; None khi có lỗi
    IReadOnlyList<MeasuredValue> Values,            // Các số đo có tên (theo slot)
    long CycleMs,                                   // Tổng thời gian chạy flow (ms) — gồm cả chụp ảnh
    string? ErrorMessage,                           // null = không lỗi
    IReadOnlyList<NodeExecutionResult> Nodes,       // Trạng thái/thời gian từng node (để hiển thị bảng Timings)
    IReadOnlyList<string> Logs)                     // Các dòng context.Log() của tool trong lần chạy này
{
    /// <summary>true nếu lần kiểm tra không cho ra kết luận đáng tin.</summary>
    public bool HasError => ErrorMessage is not null;

    /// <summary>true chỉ khi chạy trọn vẹn VÀ phán quyết là OK.</summary>
    public bool IsOk => !HasError && Judge == Judge.OK;

    /// <summary>Tìm số đo theo tên (không phân biệt hoa/thường); null nếu không có.</summary>
    public MeasuredValue? FindValue(string name)
        => Values.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Tóm tắt một dòng để hiện lên thanh trạng thái / ghi log.</summary>
    public string Summary
    {
        get
        {
            if (HasError)
                return $"Inspection #{Sequence}: ERROR | {ErrorMessage} | {CycleMs} ms";

            string values = Values.Count == 0
                ? string.Empty
                : " | " + string.Join(", ", Values.Select(v => string.Create(CultureInfo.InvariantCulture, $"{v.Name}={v.Value:0.###}")));

            return $"Inspection #{Sequence}: {Judge}{values} | {CycleMs} ms";
        }
    }
}