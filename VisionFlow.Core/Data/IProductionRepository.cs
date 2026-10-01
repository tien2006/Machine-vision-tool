// ==================== Vai trò chính:                Hợp đồng (interface) cho tầng lưu trữ dữ liệu sản xuất — Y HỆT vai trò IPlcLink
//                                                     đang đóng cho PLC: mọi tầng trên (MesService, ViewModel) chỉ biết tới interface
//                                                     này, KHÔNG biết bên dưới là SQL Server thật hay danh sách trong RAM.
// ==================== Thành phần / Class tiêu biểu: IProductionRepository
// ==================== Phụ thuộc vào:                ProductionModels.cs (cùng thư mục)
// ==================== Pattern / Kỹ thuật nổi bật:   Interface + 2 cách hiện thực (Sql thật / InMemory mô phỏng) — ĐÚNG PATTERN
//                                                     IPlcLink/McProtocolClient/SimulatedPlc đã có sẵn trong VisionFlow.Hardware.Plc,
//                                                     áp dụng lại cho tầng dữ liệu để bạn chạy được ngay cả khi CHƯA có SQL Server thật.
//
// VỊ TRÍ ĐẶT FILE: project VisionFlow.Core, thư mục Data/ — namespace VisionFlow.Core.Data.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VisionFlow.Core.Data;

public interface IProductionRepository
{
    /// <summary>Chuẩn bị schema (tạo bảng nếu chưa có) — gọi 1 lần lúc khởi động app. InMemory thì không làm gì.</summary>
    Task EnsureSchemaAsync(CancellationToken ct = default);

    Task SaveInspectionAsync(InspectionRecord record, CancellationToken ct = default);
    Task SaveActivityAsync(ActivityRecord record, CancellationToken ct = default);
    Task SaveAlarmAsync(AlarmRecord record, CancellationToken ct = default);

    /// <summary>Lấy N dòng lịch sử thao tác gần nhất — dùng để CHỨNG MINH chiều đọc ngược từ DB lên UI (tab Báo cáo).</summary>
    Task<IReadOnlyList<ActivityRecord>> GetRecentActivityAsync(int count, CancellationToken ct = default);

    /// <summary>Tổng hợp số liệu sản xuất trong ngày hôm nay (đếm trực tiếp từ bảng InspectionRecord).</summary>
    Task<ProductionSummary> GetTodaySummaryAsync(CancellationToken ct = default);
}