// ==================== Vai trò chính:                Dựng và sở hữu IProductionRepository theo cấu hình — MIRROR ĐÚNG 100% cách
//                                                     PlcLinkHost đang dựng IPlcLink: đọc mục "Database" trong appsettings.json
//                                                     bằng System.Text.Json thuần (không dùng Microsoft.Extensions.Configuration,
//                                                     giữ đồng bộ với cách PlcLinkHost đã làm), Mode = "InMemory" | "Sql".
// ==================== Thành phần / Class tiêu biểu: DatabaseOptions, ProductionRepositoryHost
// ==================== Phụ thuộc vào:                IProductionRepository, InMemoryProductionRepository, SqlProductionRepository
// ==================== Pattern / Kỹ thuật nổi bật:   Config-driven Host — Y HỆT PlcLinkHost: nếu cấu hình sai thì Repository vẫn
//                                                     có (fallback InMemory) NHƯNG Errors sẽ ghi rõ lý do, KHÔNG throw exception
//                                                     làm sập app lúc khởi động (khác 1 chút so với PlcLinkHost: PLC thật thì
//                                                     TUYỆT ĐỐI không fallback, còn DB thì fallback InMemory là hợp lý vì
//                                                     mất dữ liệu lịch sử không nguy hiểm bằng chạy nhầm PLC).
//
// VỊ TRÍ ĐẶT FILE: project VisionFlow.Core, thư mục Data/ — namespace VisionFlow.Core.Data.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace VisionFlow.Core.Data;

public sealed class DatabaseOptions
{
    /// <summary>"InMemory" = lưu tạm trong RAM, mất khi tắt app (mặc định, không cần cài gì); "Sql" = SQL Server thật qua ConnectionString.</summary>
    public string Mode { get; set; } = "InMemory";

    /// <summary>Chuỗi kết nối SQL Server (chỉ cần khi Mode = Sql). Ví dụ:
    /// "Server=localhost;Database=VisionFlowMes;Trusted_Connection=True;TrustServerCertificate=True;"</summary>
    public string ConnectionString { get; set; } = "";
}

public sealed class ProductionRepositoryHost : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly List<string> _errors = new();

    public DatabaseOptions Options { get; private set; } = new();
    public IProductionRepository Repository { get; private set; }
    public IReadOnlyList<string> Errors => _errors;
    public bool IsSql { get; private set; }
    public string Description { get; private set; } = "Chưa cấu hình Database";

    /// <param name="configPath">File JSON có mục "Database" (thường CÙNG FILE appsettings.json đang dùng cho "PlcLink").</param>
    public ProductionRepositoryHost(string configPath)
    {
        if (!Path.IsPathRooted(configPath))
            configPath = Path.Combine(AppContext.BaseDirectory, configPath);

        Repository = new InMemoryProductionRepository(); // Mặc định an toàn — luôn có cái để dùng ngay cả khi Load() lỗi
        Load(configPath);
    }

    private void Load(string configPath)
    {
        DatabaseOptions? options = null;
        try
        {
            if (!File.Exists(configPath))
            {
                _errors.Add($"Không tìm thấy file cấu hình: {configPath} — dùng InMemory tạm thời.");
            }
            else
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(configPath),
                    new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

                if (doc.RootElement.TryGetProperty("Database", out var section))
                    options = section.Deserialize<DatabaseOptions>(JsonOptions);
                else
                    _errors.Add($"File {Path.GetFileName(configPath)} không có mục \"Database\" — dùng InMemory tạm thời.");
            }
        }
        catch (Exception ex)
        {
            _errors.Add($"Lỗi đọc cấu hình Database: {ex.Message} — dùng InMemory tạm thời.");
        }

        options ??= new DatabaseOptions();
        Options = options;

        if (string.Equals(options.Mode, "Sql", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(options.ConnectionString))
            {
                _errors.Add("Database.Mode = \"Sql\" nhưng ConnectionString rỗng — dùng InMemory tạm thời.");
                return; // Repository giữ nguyên InMemoryProductionRepository đã gán ở constructor
            }

            // THÊM: bọc SqlProductionRepository bằng BufferedProductionRepository — nếu ghi SQL lỗi (SQL Server
            // tắt/bảo trì...), bản ghi được xếp vào hàng đợi (RAM + file "mes-pending-queue.jsonl" cạnh file .exe)
            // thay vì mất, rồi tự đẩy lại mỗi 15 giây cho tới khi SQL Server sống lại.
            var sqlRepo = new SqlProductionRepository(options.ConnectionString);
            var queueFilePath = Path.Combine(AppContext.BaseDirectory, "mes-pending-queue.jsonl");
            Repository = new BufferedProductionRepository(sqlRepo, queueFilePath);

            IsSql = true;
            Description = "SQL Server (" + SafeServerName(options.ConnectionString) + ")";
        }
        else
        {
            Description = "InMemory (RAM — mất dữ liệu khi tắt app)";
        }
    }

    /// <summary>Lấy tên Server trong ConnectionString để hiển thị, KHÔNG lộ mật khẩu/thông tin nhạy cảm ra log/UI.</summary>
    private static string SafeServerName(string connectionString)
    {
        foreach (var part in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0].Trim().Equals("Server", StringComparison.OrdinalIgnoreCase))
                return kv[1].Trim();
        }
        return "?";
    }

    /// <summary>Dừng Timer đẩy hàng đợi (nếu đang dùng SQL) khi app đóng — DI Container tự gọi hàm này vì
    /// ProductionRepositoryHost được đăng ký Singleton và implement IDisposable (giống PlcLinkHost).</summary>
    public void Dispose()
    {
        if (Repository is IDisposable disposable) disposable.Dispose();
    }
}