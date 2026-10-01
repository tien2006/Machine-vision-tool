// ==================== Vai trò chính:                Đọc mục "PlcHandshake" trong appsettings.json, kiểm tra hợp lệ, dựng PlcHandshakeService (và SimulatedLadder khi dùng PLC giả), cung cấp Start/Stop cho giao diện
// ==================== Thành phần / Class tiêu biểu: PlcHandshakeHost
// ==================== Phụ thuộc vào:                PlcLinkHost, IInspectionRunner, PlcHandshakeService, SimulatedLadder, PlcRegisterMap, System.Text.Json
// ==================== Pattern / Kỹ thuật nổi bật:   Config-driven + IDisposable cho DI (cùng kiểu PlcLinkHost/CameraBootstrapper) + fail-safe: cấu hình sai thì KHÔNG dựng dịch vụ, không âm thầm chạy với giá trị đoán
//
// VỊ TRÍ ĐẶT FILE: thư mục Plc/ — namespace VisionFlow.Hardware.Plc.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace VisionFlow.Hardware.Plc;

/// <summary>
/// Gom toàn bộ phần handshake để giao diện chỉ cần gọi <see cref="Start"/> / <see cref="StopAsync"/>.
/// Đăng ký Singleton trong DI: khi ứng dụng thoát, container tự Dispose => dừng vòng lặp, dừng ladder giả.
/// <para>
/// Khi PLC là bản giả lập (PlcLink.Mode = Simulated), Start() cũng bật luôn ladder giả để tự sinh trigger;
/// khi PLC thật, chương trình PLC của bạn là bên bật trigger.
/// </para>
/// </summary>
public sealed class PlcHandshakeHost : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly List<string> _errors = new();

    public PlcHandshakeOptions Options { get; private set; } = new();
    public PlcRegisterMap? Map { get; private set; }

    /// <summary>Dịch vụ handshake; null nếu cấu hình sai hoặc chưa có đường truyền PLC (xem <see cref="Errors"/>).</summary>
    public PlcHandshakeService? Service { get; private set; }

    /// <summary>Ladder giả — chỉ có khi PLC là bản giả lập.</summary>
    public SimulatedLadder? Ladder { get; private set; }

    public IReadOnlyList<string> Errors => _errors;

    /// <summary>true nếu bấm Start được.</summary>
    public bool CanStart => Service is not null;

    public bool IsRunning => Service?.IsRunning == true;

    /// <param name="configPath">File JSON; mục "PlcHandshake" không bắt buộc (thiếu thì dùng mặc định).</param>
    /// <param name="linkHost">Đường truyền PLC đã dựng ở bước 3.</param>
    /// <param name="runner">Cổng gọi kiểm tra (adapter bọc InspectionService).</param>
    public PlcHandshakeHost(string configPath, PlcLinkHost linkHost, IInspectionRunner runner)
    {
        if (linkHost is null) throw new ArgumentNullException(nameof(linkHost));
        if (runner is null) throw new ArgumentNullException(nameof(runner));

        if (!Path.IsPathRooted(configPath))
            configPath = Path.Combine(AppContext.BaseDirectory, configPath);

        if (!TryLoadOptions(configPath)) return;
        if (!Validate()) return;

        if (linkHost.Link is null)
        {
            _errors.Add("Đường truyền PLC chưa sẵn sàng (xem cảnh báo cấu hình PlcLink) nên không dựng được handshake.");
            return;
        }

        // Đường dẫn flow tương đối tính từ thư mục chạy ứng dụng
        if (!string.IsNullOrWhiteSpace(Options.FlowFile) && !Path.IsPathRooted(Options.FlowFile))
            Options.FlowFile = Path.Combine(AppContext.BaseDirectory, Options.FlowFile);

        Service = new PlcHandshakeService(linkHost.Link, runner, Map!, Options);

        if (linkHost.Simulator is not null)
            Ladder = new SimulatedLadder(linkHost.Simulator, Map!, Options.SimulatedTriggerIntervalMs);
    }

    private bool TryLoadOptions(string configPath)
    {
        try
        {
            if (!File.Exists(configPath)) return true; // Không có file: dùng mặc định (PlcLinkHost đã báo thiếu file rồi)

            using var doc = JsonDocument.Parse(File.ReadAllText(configPath),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

            if (doc.RootElement.TryGetProperty("PlcHandshake", out var section))
                Options = section.Deserialize<PlcHandshakeOptions>(JsonOptions) ?? new PlcHandshakeOptions();

            return true;
        }
        catch (Exception ex)
        {
            AddError($"Không đọc được mục PlcHandshake: {ex.Message}");
            return false;
        }
    }

    private bool Validate()
    {
        int before = _errors.Count;
        var o = Options;

        if (PlcRegisterMap.TryCreate(o.CommandBase, o.StatusBase, o.ValuesBase, out var map, out string mapError))
            Map = map;
        else
            AddError($"PlcHandshake: {mapError}");

        if (o.PollIntervalMs < 5 || o.PollIntervalMs > 1000) AddError("PlcHandshake.PollIntervalMs phải từ 5 đến 1000.");
        if (o.HeartbeatIntervalMs < 50 || o.HeartbeatIntervalMs > 60000) AddError("PlcHandshake.HeartbeatIntervalMs phải từ 50 đến 60000.");
        if (o.InspectionTimeoutMs < 100) AddError("PlcHandshake.InspectionTimeoutMs phải từ 100 trở lên.");
        if (o.AckWarningMs < 100) AddError("PlcHandshake.AckWarningMs phải từ 100 trở lên.");
        if (o.ReconnectInitialMs < 100) AddError("PlcHandshake.ReconnectInitialMs phải từ 100 trở lên.");
        if (o.ReconnectMaxMs < o.ReconnectInitialMs) AddError("PlcHandshake.ReconnectMaxMs phải lớn hơn hoặc bằng ReconnectInitialMs.");
        if (o.ValueScale < 1 || o.ValueScale > 1_000_000) AddError("PlcHandshake.ValueScale phải từ 1 đến 1.000.000.");
        if (o.SimulatedTriggerIntervalMs < 50) AddError("PlcHandshake.SimulatedTriggerIntervalMs phải từ 50 trở lên.");

        return _errors.Count == before;
    }

    private void AddError(string message)
    {
        _errors.Add(message);
        System.Diagnostics.Debug.WriteLine($"[PlcHandshakeHost] {message}");
    }

    // ====================================================================
    // ĐIỀU KHIỂN
    // ====================================================================

    /// <summary>Bắt đầu handshake (và ladder giả nếu đang dùng PLC giả). Ném InvalidOperationException nếu cấu hình sai.</summary>
    public void Start()
    {
        if (Service is null)
            throw new InvalidOperationException("Handshake chưa dùng được: " + (_errors.Count > 0 ? string.Join("; ", _errors) : "chưa cấu hình"));

        Service.Start();
        Ladder?.Start();
    }

    /// <summary>Dừng ladder giả (nếu có) rồi dừng handshake (ghi Status = Offline xuống PLC).</summary>
    public async Task StopAsync()
    {
        Ladder?.Stop();
        if (Service is not null) await Service.StopAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        Ladder?.Dispose();
        Service?.Dispose();
    }
}