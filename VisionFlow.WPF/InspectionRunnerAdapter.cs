// ==================== Vai trò chính:                Adapter mỏng: bọc InspectionService (Engine) thành IInspectionRunner (Hardware/Plc) để handshake gọi kiểm tra mà hai project không phải tham chiếu nhau
// ==================== Thành phần / Class tiêu biểu: InspectionRunnerAdapter
// ==================== Phụ thuộc vào:                InspectionService, InspectionResult (Engine.Runtime), IInspectionRunner (Hardware.Plc), Judge (Core.Models)
// ==================== Pattern / Kỹ thuật nổi bật:   Adapter Pattern (Ports & Adapters) — nằm ở project WPF vì đây là nơi duy nhất tham chiếu được cả Engine lẫn Hardware
//
// VỊ TRÍ ĐẶT FILE: project WPF (cùng project với App.xaml.cs) — namespace VisionFlow.WPF.
// Nếu namespace project của bạn khác, chỉ cần sửa dòng "namespace" bên dưới cho khớp.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VisionFlow.Core.Models;
using VisionFlow.Engine.Runtime;
using VisionFlow.Hardware.Plc;

namespace VisionFlow.WPF;

/// <summary>Chuyển <see cref="InspectionService"/> thành <see cref="IInspectionRunner"/> cho <see cref="PlcHandshakeService"/>.</summary>
public sealed class InspectionRunnerAdapter : IInspectionRunner
{
    private readonly InspectionService _inspection;

    public InspectionRunnerAdapter(InspectionService inspection)
    {
        _inspection = inspection ?? throw new ArgumentNullException(nameof(inspection));
    }

    public bool IsReady => _inspection.IsLoaded;

    public Task LoadAsync(string flowPath, CancellationToken ct = default)
        => _inspection.LoadFlowAsync(flowPath, ct);

    public async Task<InspectionOutcome> RunAsync(CancellationToken ct = default)
    {
        InspectionResult result = await _inspection.RunOnceAsync(ct).ConfigureAwait(false);

        // Lỗi (HasError) phải thành Verdict.Error chứ KHÔNG được thành NG: sản phẩm chưa được kết luận
        InspectionVerdict verdict = result.HasError
            ? InspectionVerdict.Error
            : (result.Judge == Judge.OK ? InspectionVerdict.Ok : InspectionVerdict.Ng);

        IReadOnlyList<OutcomeValue> values = result.Values
            .Select(v => new OutcomeValue(v.Slot, v.Name, v.Value))
            .ToList();

        return new InspectionOutcome(verdict, values, result.CycleMs, result.ErrorMessage);
    }
}