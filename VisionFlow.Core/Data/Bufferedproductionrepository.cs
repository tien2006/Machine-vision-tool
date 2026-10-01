// ==================== Vai trò chính:                "Store-and-forward" — bọc quanh 1 IProductionRepository bất kỳ (thường là
//                                                     SqlProductionRepository). Khi ghi xuống Repository thật bị lỗi (SQL Server
//                                                     tắt, mất mạng...), bản ghi KHÔNG bị mất mà được xếp vào hàng đợi (RAM +
//                                                     file .jsonl trên đĩa để sống sót qua cả việc tắt/mở lại app), rồi 1 Timer
//                                                     nền tự thử đẩy lại định kỳ cho tới khi Repository thật sống lại.
// ==================== Thành phần / Class tiêu biểu: BufferedProductionRepository
// ==================== Phụ thuộc vào:                IProductionRepository, ProductionModels.cs
// ==================== Pattern / Kỹ thuật nổi bật:   Decorator pattern (bọc ngoài 1 interface, thêm hành vi mà không sửa
//                                                     SqlProductionRepository/MesService/ViewModel nào cả) + hàng đợi bền
//                                                     (durable queue) ghi ra file JSON Lines (.jsonl, mỗi dòng 1 bản ghi JSON).
//
// VỊ TRÍ ĐẶT FILE: project VisionFlow.Core, thư mục Data/ — namespace VisionFlow.Core.Data.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VisionFlow.Core.Data;

public sealed class BufferedProductionRepository : IProductionRepository, IDisposable
{
    private readonly IProductionRepository _inner;
    private readonly string _queueFilePath;
    private readonly object _fileLock = new();          // Bảo vệ file .jsonl — nhiều thread có thể ghi/đọc cùng lúc
    private readonly ConcurrentQueue<PendingItem> _queue = new();
    private readonly Timer _retryTimer;
    private int _flushing;                               // 0/1 dùng Interlocked — chặn 2 lượt Flush chạy chồng nhau

    private static readonly JsonSerializerOptions JsonOptions = new();

    /// <summary>Số bản ghi hiện đang chờ trong hàng đợi — gắn lên UI để người vận hành biết SQL Server đang có vấn đề
    /// dù MesService không còn ném lỗi ra ngoài nữa.</summary>
    public int PendingCount => _queue.Count;

    public BufferedProductionRepository(IProductionRepository inner, string queueFilePath, TimeSpan? retryInterval = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _queueFilePath = queueFilePath;
        LoadPendingFromDisk(); // Nạp lại phần chưa gửi được từ lần chạy app trước (nếu tắt app khi SQL còn đang tắt)

        var interval = retryInterval ?? TimeSpan.FromSeconds(15);
        _retryTimer = new Timer(_ => _ = FlushAsync(), null, interval, interval);
    }

    public Task EnsureSchemaAsync(CancellationToken ct = default) => _inner.EnsureSchemaAsync(ct);

    public Task SaveInspectionAsync(InspectionRecord record, CancellationToken ct = default) =>
        SaveWithFallbackAsync("Inspection", record, () => _inner.SaveInspectionAsync(record, ct));

    public Task SaveActivityAsync(ActivityRecord record, CancellationToken ct = default) =>
        SaveWithFallbackAsync("Activity", record, () => _inner.SaveActivityAsync(record, ct));

    public Task SaveAlarmAsync(AlarmRecord record, CancellationToken ct = default) =>
        SaveWithFallbackAsync("Alarm", record, () => _inner.SaveAlarmAsync(record, ct));

    // Đọc: KHÔNG bọc thêm gì — trả thẳng dữ liệu Repository thật đang có. Bản ghi còn kẹt trong hàng đợi
    // sẽ CHƯA xuất hiện ở đây cho tới khi Flush thành công (giới hạn đã biết của bản đơn giản này).
    public Task<IReadOnlyList<ActivityRecord>> GetRecentActivityAsync(int count, CancellationToken ct = default) =>
        _inner.GetRecentActivityAsync(count, ct);

    public Task<ProductionSummary> GetTodaySummaryAsync(CancellationToken ct = default) =>
        _inner.GetTodaySummaryAsync(ct);

    /// <summary>Ghi thẳng nếu được; lỗi thì xếp vào hàng đợi (RAM + file) rồi NUỐT lỗi — người gọi (MesService) coi như
    /// đã "lưu xong", dữ liệu an toàn trong hàng đợi và sẽ tự đẩy lại sau, KHÔNG mất.</summary>
    private async Task SaveWithFallbackAsync<T>(string kind, T record, Func<Task> saveAction)
    {
        try
        {
            await saveAction();
        }
        catch
        {
            Enqueue(kind, record);
        }
    }

    private void Enqueue<T>(string kind, T record)
    {
        var item = new PendingItem { Kind = kind, PayloadJson = JsonSerializer.Serialize(record, JsonOptions) };
        _queue.Enqueue(item);
        lock (_fileLock)
        {
            File.AppendAllText(_queueFilePath, JsonSerializer.Serialize(item, JsonOptions) + Environment.NewLine);
        }
    }

    private void LoadPendingFromDisk()
    {
        if (!File.Exists(_queueFilePath)) return;
        lock (_fileLock)
        {
            foreach (var line in File.ReadAllLines(_queueFilePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var item = JsonSerializer.Deserialize<PendingItem>(line, JsonOptions);
                    if (item is not null) _queue.Enqueue(item);
                }
                catch
                {
                    // 1 dòng trong file bị hỏng (ghi dở lúc mất điện...) -> bỏ qua riêng dòng đó, KHÔNG làm sập app lúc khởi động
                }
            }
        }
    }

    private void RewriteDiskFromQueue()
    {
        lock (_fileLock)
        {
            var remaining = _queue.ToArray();
            if (remaining.Length == 0)
            {
                if (File.Exists(_queueFilePath)) File.Delete(_queueFilePath);
                return;
            }
            File.WriteAllLines(_queueFilePath, remaining.Select(i => JsonSerializer.Serialize(i, JsonOptions)));
        }
    }

    /// <summary>Thử đẩy lại toàn bộ hàng đợi xuống Repository thật. Timer tự gọi định kỳ; cũng có thể gọi tay
    /// (ví dụ thêm nút "Đẩy lại ngay" trên UI) ngay sau khi biết chắc SQL Server đã chạy lại.</summary>
    public async Task FlushAsync()
    {
        if (Interlocked.Exchange(ref _flushing, 1) == 1) return; // Đang có 1 lượt Flush khác chạy rồi -> bỏ qua lượt này
        try
        {
            int roundCount = _queue.Count; // Chỉ thử đúng số lượng hiện có — tránh vòng lặp vô hạn nếu có mục cứ lỗi lại
            for (int i = 0; i < roundCount; i++)
            {
                if (!_queue.TryDequeue(out var item)) break;
                try
                {
                    await RestoreAsync(item).ConfigureAwait(false);
                }
                catch
                {
                    _queue.Enqueue(item); // SQL vẫn chưa sống lại -> trả lại hàng đợi, dừng vòng này, thử tiếp ở chu kỳ Timer sau
                    break;
                }
            }
        }
        finally
        {
            RewriteDiskFromQueue();
            Interlocked.Exchange(ref _flushing, 0);
        }
    }

    private Task RestoreAsync(PendingItem item) => item.Kind switch
    {
        "Inspection" => _inner.SaveInspectionAsync(JsonSerializer.Deserialize<InspectionRecord>(item.PayloadJson, JsonOptions)!),
        "Activity" => _inner.SaveActivityAsync(JsonSerializer.Deserialize<ActivityRecord>(item.PayloadJson, JsonOptions)!),
        "Alarm" => _inner.SaveAlarmAsync(JsonSerializer.Deserialize<AlarmRecord>(item.PayloadJson, JsonOptions)!),
        _ => Task.CompletedTask
    };

    public void Dispose() => _retryTimer.Dispose();

    private sealed class PendingItem
    {
        public string Kind { get; set; } = "";
        public string PayloadJson { get; set; } = "";
    }
}