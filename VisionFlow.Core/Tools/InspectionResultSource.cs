// ==================== Vai trò chính:                Hợp đồng để một Tool "công bố" kết quả kiểm tra (OK/NG + các số đo có tên) cho tầng runtime mà Engine không cần biết Tool cụ thể
// ==================== Thành phần / Class tiêu biểu: IInspectionResultSource, InspectionSnapshot, MeasuredValue
// ==================== Phụ thuộc vào:                Không phụ thuộc gì (0 dependency)
// ==================== Pattern / Kỹ thuật nổi bật:   Dependency Inversion Principle — Engine (InspectionService) phụ thuộc interface nằm ở Core,
//                                                     còn Tool cụ thể (ResultPublisherTool, project Tools) implement interface đó => Engine KHÔNG phải tham chiếu project Tools
//
// VỊ TRÍ ĐẶT FILE: project Core, thư mục Tools/ (cùng nơi với VisionTool.cs, ITool.cs) — namespace VisionFlow.Core.Tools.

using System;
using System.Collections.Generic;

namespace VisionFlow.Core.Tools;

/// <summary>
/// Một số đo có tên do flow công bố (ví dụ "Diameter" = 12.503).
/// <see cref="Slot"/> là vị trí cố định 1..N trên node Result Publisher: PLC map thanh ghi theo SLOT
/// (Value1 -> D1020, Value2 -> D1022 ...) nên slot KHÔNG đổi khi người dùng đổi tên số đo hoặc bỏ trống một cổng.
/// </summary>
public sealed record MeasuredValue(int Slot, string Name, double Value);

/// <summary>
/// Kết quả bất biến (immutable) mà node Result Publisher chụp lại sau mỗi lần Execute:
/// phán quyết đạt/không đạt và danh sách số đo. Dùng record để lần chạy sau không thể sửa ngược lên kết quả cũ.
/// </summary>
public sealed record InspectionSnapshot(bool IsOk, IReadOnlyList<MeasuredValue> Values);

/// <summary>
/// Tool nào implement interface này sẽ được <c>InspectionService</c> tìm thấy trong FlowGraph và đọc kết quả sau khi chạy xong flow.
/// </summary>
public interface IInspectionResultSource
{
    /// <summary>Kết quả của lần Execute gần nhất; null nếu chưa chạy, đã Reset, hoặc lần chạy đó bị lỗi/bị bỏ qua.</summary>
    InspectionSnapshot? Snapshot { get; }

    /// <summary>
    /// Xoá kết quả cũ. InspectionService gọi TRƯỚC mỗi lần chạy: nếu node bị Skip (thượng nguồn lỗi) thì OnExecute không chạy,
    /// nếu không Reset sẽ đọc nhầm kết quả của sản phẩm trước và gửi cho PLC như thể đó là sản phẩm hiện tại.
    /// </summary>
    void ResetSnapshot();
}