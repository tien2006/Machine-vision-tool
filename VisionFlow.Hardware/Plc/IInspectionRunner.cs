// ==================== Vai trò chính:                Cổng trừu tượng "chạy một lần kiểm tra" để PlcHandshakeService gọi mà KHÔNG phải tham chiếu project Engine/InspectionService
// ==================== Thành phần / Class tiêu biểu: IInspectionRunner, InspectionOutcome, OutcomeValue, InspectionVerdict
// ==================== Phụ thuộc vào:                Không phụ thuộc gì (0 dependency)
// ==================== Pattern / Kỹ thuật nổi bật:   Ports & Adapters — interface nằm ở tầng thiết bị (Hardware/Plc), adapter mỏng ở project WPF nối sang InspectionService.
//                                                     Nhờ vậy không phải thêm tham chiếu giữa các project, và kiểm thử handshake được bằng runner giả.
//
// VỊ TRÍ ĐẶT FILE: thư mục Plc/ — namespace VisionFlow.Hardware.Plc.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VisionFlow.Hardware.Plc;

/// <summary>Kết luận của một lần kiểm tra ở mức handshake cần biết.</summary>
public enum InspectionVerdict
{
    /// <summary>Không kết luận được (lỗi camera/flow...). KHÔNG phải NG.</summary>
    Error = 0,
    Ok = 1,
    Ng = 2
}

/// <summary>Một số đo có tên đặt ở slot cố định (1..6).</summary>
public readonly record struct OutcomeValue(int Slot, string Name, double Value);

/// <summary>Kết quả một lần kiểm tra, dạng gọn chỉ chứa thứ cần gửi PLC.</summary>
public sealed record InspectionOutcome(
    InspectionVerdict Verdict,
    IReadOnlyList<OutcomeValue> Values,
    long CycleMs,
    string? ErrorMessage);

/// <summary>Thứ mà PlcHandshakeService cần từ phía kiểm tra.</summary>
public interface IInspectionRunner
{
    /// <summary>true nếu đã nạp flow và có thể chạy ngay.</summary>
    bool IsReady { get; }

    /// <summary>Nạp flow từ file (gọi lúc bắt đầu handshake nếu cấu hình có FlowFile).</summary>
    Task LoadAsync(string flowPath, CancellationToken ct = default);

    /// <summary>
    /// Chạy đúng một lần kiểm tra. Lỗi của flow phải trả về bằng <see cref="InspectionVerdict.Error"/>, không ném exception.
    /// Được phép ném <see cref="System.OperationCanceledException"/> khi bị huỷ (ví dụ hết thời gian).
    /// </summary>
    Task<InspectionOutcome> RunAsync(CancellationToken ct = default);
}