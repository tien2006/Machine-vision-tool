// ==================== Vai trò chính:                Tầng MES (Manufacturing Execution System) — đứng GIỮA ViewModel (UI) và
//                                                     Repository (lưu trữ), y hệt tinh thần CoreApp tách nghiệp vụ (RecipeExecute,
//                                                     WorkHist...) ra khỏi UserControl thay vì viết thẳng SQL/IPC trong code-behind.
//                                                     Hiện tại (bản "chiều dọc") MesService chỉ chuyển tiếp dữ liệu xuống Repository
//                                                     + đọc ngược lên cho tab Báo cáo — chỗ TỰ NHIÊN để sau này thêm nghiệp vụ MES
//                                                     thật (OEE, Lot/Work Order, truy vết theo ca...) mà KHÔNG phải sửa UI hay Repository.
// ==================== Thành phần / Class tiêu biểu: MesService
// ==================== Phụ thuộc vào:                IProductionRepository (VisionFlow.Core.Data)
// ==================== Pattern / Kỹ thuật nổi bật:   Service Layer (tách UI khỏi Data) — ViewModel gọi MesService, MesService gọi
//                                                     Repository; ViewModel KHÔNG BAO GIỜ tự viết SQL hay biết Mode Sql/InMemory.
//
// VỊ TRÍ ĐẶT FILE: project MỚI VisionFlow.Mes (hoặc project VisionFlow.Core nếu bạn không muốn tách project riêng),
// thư mục gốc — namespace VisionFlow.Mes.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VisionFlow.Core.Data;

namespace VisionFlow.Mes;

public sealed class MesService
{
    private readonly IProductionRepository _repository;

    public MesService(IProductionRepository repository) => _repository = repository;

    /// <summary>Gọi 1 lần lúc App khởi động (App.xaml.cs) — tự tạo bảng nếu đang dùng SQL Server thật.</summary>
    public Task InitializeAsync() => _repository.EnsureSchemaAsync();

    // ================= GHI (Write side) =================

    public Task RecordInspectionAsync(bool isOk, double gripX, double gripY, double gripR, string recipeName) =>
        _repository.SaveInspectionAsync(new InspectionRecord
        {
            IsOk = isOk,
            GripX = gripX,
            GripY = gripY,
            GripR = gripR,
            RecipeName = recipeName
        });

    public Task RecordActivityAsync(string eventText, string result) =>
        _repository.SaveActivityAsync(new ActivityRecord { EventText = eventText, Result = result });

    public Task RecordAlarmAsync(string ruleName, string message) =>
        _repository.SaveAlarmAsync(new AlarmRecord { RuleName = ruleName, Message = message });

    // ================= ĐỌC (Read side) — chứng minh chiều đọc ngược từ DB lên UI =================

    public Task<ProductionSummary> GetTodaySummaryAsync() => _repository.GetTodaySummaryAsync();

    public Task<IReadOnlyList<ActivityRecord>> GetRecentActivityAsync(int count = 50) =>
        _repository.GetRecentActivityAsync(count);

    /// <summary>Số bản ghi đang kẹt trong hàng đợi chờ đẩy xuống SQL (0 nếu đang dùng InMemory, hoặc đang dùng Sql
    /// mà không có gì tồn đọng). Gắn lên UI để người vận hành biết SQL Server đang có vấn đề dù thao tác vẫn "thành công".</summary>
    public int PendingWriteCount => _repository is BufferedProductionRepository buffered ? buffered.PendingCount : 0;
}