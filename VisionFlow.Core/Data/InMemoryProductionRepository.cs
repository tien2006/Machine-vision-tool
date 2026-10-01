// ==================== Vai trò chính:                Hiện thực "mô phỏng" của IProductionRepository — giữ dữ liệu trong RAM
//                                                     (List + lock, KHÔNG cần cài SQL Server) — ĐÚNG VAI TRÒ SimulatedPlc đang đóng
//                                                     cho PLC: cho phép bạn chạy & test toàn bộ luồng MES ngay cả khi laptop
//                                                     chưa cài SQL Server, dữ liệu mất khi tắt app (chấp nhận được ở giai đoạn
//                                                     "phát triển theo chiều dọc" bạn đang muốn).
// ==================== Thành phần / Class tiêu biểu: InMemoryProductionRepository
// ==================== Phụ thuộc vào:                IProductionRepository, ProductionModels.cs
//
// VỊ TRÍ ĐẶT FILE: project VisionFlow.Core, thư mục Data/ — namespace VisionFlow.Core.Data.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VisionFlow.Core.Data;

public sealed class InMemoryProductionRepository : IProductionRepository
{
    private readonly object _gate = new();
    private readonly List<InspectionRecord> _inspections = new();
    private readonly List<ActivityRecord> _activities = new();
    private readonly List<AlarmRecord> _alarms = new();
    private long _nextId = 1;

    public Task EnsureSchemaAsync(CancellationToken ct = default) => Task.CompletedTask; // Không cần schema với RAM

    public Task SaveInspectionAsync(InspectionRecord record, CancellationToken ct = default)
    {
        lock (_gate) { record.Id = _nextId++; _inspections.Add(record); }
        return Task.CompletedTask;
    }

    public Task SaveActivityAsync(ActivityRecord record, CancellationToken ct = default)
    {
        lock (_gate) { record.Id = _nextId++; _activities.Add(record); }
        return Task.CompletedTask;
    }

    public Task SaveAlarmAsync(AlarmRecord record, CancellationToken ct = default)
    {
        lock (_gate) { record.Id = _nextId++; _alarms.Add(record); }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ActivityRecord>> GetRecentActivityAsync(int count, CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<ActivityRecord> result = _activities.OrderByDescending(a => a.Timestamp).Take(count).ToList();
            return Task.FromResult(result);
        }
    }

    public Task<ProductionSummary> GetTodaySummaryAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            var today = DateTime.Today;
            var todays = _inspections.Where(i => i.Timestamp.Date == today).ToList();
            return Task.FromResult(new ProductionSummary
            {
                Date = today,
                TotalCount = todays.Count,
                OkCount = todays.Count(i => i.IsOk),
                NgCount = todays.Count(i => !i.IsOk),
            });
        }
    }
}