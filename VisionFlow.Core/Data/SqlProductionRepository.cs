// ==================== Vai trò chính:                Hiện thực THẬT của IProductionRepository — ghi/đọc SQL Server qua ADO.NET
//                                                     thuần (Microsoft.Data.SqlClient), KHÔNG dùng Entity Framework để giữ nhẹ,
//                                                     dễ hiểu luồng SQL chạy thật — đúng tinh thần "nhìn thấy giao tiếp giữa các
//                                                     tầng" bạn muốn, thay vì để ORM che hết câu SQL thật đi.
// ==================== Thành phần / Class tiêu biểu: SqlProductionRepository
// ==================== Phụ thuộc vào:                Microsoft.Data.SqlClient (cài NuGet), IProductionRepository, ProductionModels.cs
// ==================== Pattern / Kỹ thuật nổi bật:   Tự tạo bảng lúc khởi động (EnsureSchemaAsync) — "self-provisioning schema" —
//                                                     để bạn KHÔNG cần tự tay chạy script SQL trước, chỉ cần có SQL Server rỗng.
//
// VỊ TRÍ ĐẶT FILE: project VisionFlow.Core, thư mục Data/ — namespace VisionFlow.Core.Data.
// CẦN CÀI NUGET: Microsoft.Data.SqlClient (project VisionFlow.Core, hoặc project chứa file này).

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace VisionFlow.Core.Data;

public sealed class SqlProductionRepository : IProductionRepository
{
    private readonly string _connectionString;

    public SqlProductionRepository(string connectionString) => _connectionString = connectionString;

    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        const string sql = """
            IF OBJECT_ID('dbo.InspectionRecord', 'U') IS NULL
            CREATE TABLE dbo.InspectionRecord (
                Id BIGINT IDENTITY PRIMARY KEY,
                Timestamp DATETIME2 NOT NULL,
                IsOk BIT NOT NULL,
                GripX FLOAT NOT NULL, GripY FLOAT NOT NULL, GripR FLOAT NOT NULL,
                RecipeName NVARCHAR(100) NOT NULL
            );

            IF OBJECT_ID('dbo.ActivityRecord', 'U') IS NULL
            CREATE TABLE dbo.ActivityRecord (
                Id BIGINT IDENTITY PRIMARY KEY,
                Timestamp DATETIME2 NOT NULL,
                EventText NVARCHAR(200) NOT NULL,
                Result NVARCHAR(20) NOT NULL
            );

            IF OBJECT_ID('dbo.AlarmRecord', 'U') IS NULL
            CREATE TABLE dbo.AlarmRecord (
                Id BIGINT IDENTITY PRIMARY KEY,
                Timestamp DATETIME2 NOT NULL,
                RuleName NVARCHAR(100) NOT NULL,
                Message NVARCHAR(300) NOT NULL
            );
            """;

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SaveInspectionAsync(InspectionRecord r, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO dbo.InspectionRecord (Timestamp, IsOk, GripX, GripY, GripR, RecipeName)
            VALUES (@Timestamp, @IsOk, @GripX, @GripY, @GripR, @RecipeName);
            """;
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Timestamp", r.Timestamp);
        cmd.Parameters.AddWithValue("@IsOk", r.IsOk);
        cmd.Parameters.AddWithValue("@GripX", r.GripX);
        cmd.Parameters.AddWithValue("@GripY", r.GripY);
        cmd.Parameters.AddWithValue("@GripR", r.GripR);
        cmd.Parameters.AddWithValue("@RecipeName", r.RecipeName);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SaveActivityAsync(ActivityRecord r, CancellationToken ct = default)
    {
        const string sql = "INSERT INTO dbo.ActivityRecord (Timestamp, EventText, Result) VALUES (@Timestamp, @EventText, @Result);";
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Timestamp", r.Timestamp);
        cmd.Parameters.AddWithValue("@EventText", r.EventText);
        cmd.Parameters.AddWithValue("@Result", r.Result);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task SaveAlarmAsync(AlarmRecord r, CancellationToken ct = default)
    {
        const string sql = "INSERT INTO dbo.AlarmRecord (Timestamp, RuleName, Message) VALUES (@Timestamp, @RuleName, @Message);";
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Timestamp", r.Timestamp);
        cmd.Parameters.AddWithValue("@RuleName", r.RuleName);
        cmd.Parameters.AddWithValue("@Message", r.Message);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<ActivityRecord>> GetRecentActivityAsync(int count, CancellationToken ct = default)
    {
        const string sql = "SELECT TOP (@Count) Id, Timestamp, EventText, Result FROM dbo.ActivityRecord ORDER BY Timestamp DESC;";
        var list = new List<ActivityRecord>();
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@Count", count);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new ActivityRecord
            {
                Id = reader.GetInt64(0),
                Timestamp = reader.GetDateTime(1),
                EventText = reader.GetString(2),
                Result = reader.GetString(3)
            });
        }
        return list;
    }

    public async Task<ProductionSummary> GetTodaySummaryAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT
                COUNT(*) AS TotalCount,
                SUM(CASE WHEN IsOk = 1 THEN 1 ELSE 0 END) AS OkCount,
                SUM(CASE WHEN IsOk = 0 THEN 1 ELSE 0 END) AS NgCount
            FROM dbo.InspectionRecord
            WHERE CAST(Timestamp AS DATE) = CAST(GETDATE() AS DATE);
            """;
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var summary = new ProductionSummary { Date = DateTime.Today };
        if (await reader.ReadAsync(ct) && !await reader.IsDBNullAsync(0, ct))
        {
            summary.TotalCount = reader.GetInt32(0);
            summary.OkCount = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
            summary.NgCount = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
        }
        return summary;
    }
}