// ==================== Vai trò chính:                Đọc mục "PlcLink" trong appsettings.json rồi dựng đường truyền PLC (PLC giả lập trong app HOẶC PLC thật) và dọn dẹp khi thoát
// ==================== Thành phần / Class tiêu biểu: PlcLinkOptions, PlcLinkHost
// ==================== Phụ thuộc vào:                IPlcLink, McProtocolClient, SimulatedPlc, PlcAddress, System.Text.Json (có sẵn, không cần NuGet)
// ==================== Pattern / Kỹ thuật nổi bật:   Config-driven + IDisposable cho DI (cùng kiểu CameraBootstrapper) + fail-safe: cấu hình sai KHÔNG bao giờ âm thầm rơi về PLC giả
//
// VỊ TRÍ ĐẶT FILE: thư mục Plc/ — namespace VisionFlow.Hardware.Plc.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace VisionFlow.Hardware.Plc;

/// <summary>Cấu hình đường truyền PLC, đọc từ mục "PlcLink" của appsettings.json.</summary>
public sealed class PlcLinkOptions
{
    /// <summary>Tên hiển thị trong log/thông báo.</summary>
    public string Name { get; set; } = "PLC1";

    /// <summary>"Simulated" = dùng PLC giả lập chạy trong app (không cần phần cứng); "Real" = nối PLC thật qua Host:Port.</summary>
    public string Mode { get; set; } = "Simulated";

    /// <summary>IP của PLC thật (chỉ dùng khi Mode = Real).</summary>
    public string Host { get; set; } = "192.168.3.39";

    /// <summary>Cổng đã đặt trong Open Setting của PLC (TCP + MC Protocol), viết dạng THẬP PHÂN.</summary>
    public int Port { get; set; } = 5011;

    /// <summary>Thời gian tối đa chờ mở kết nối TCP (ms).</summary>
    public int ConnectTimeoutMs { get; set; } = 3000;

    /// <summary>Thời gian tối đa chờ PLC trả lời một yêu cầu (ms).</summary>
    public int ReceiveTimeoutMs { get; set; } = 2000;

    /// <summary>Cổng mà PLC giả lập lắng nghe trên máy này (chỉ dùng khi Mode = Simulated).</summary>
    public int SimulatedPort { get; set; } = 5011;

    /// <summary>Thanh ghi dùng cho phép thử "PLC Test": đọc 4 word từ đây.</summary>
    public string TestAddress { get; set; } = "D1000";

    /// <summary>
    /// Thanh ghi "nháp" để thử GHI (ghi giá trị mẫu, đọc lại, rồi khôi phục giá trị cũ).
    /// ĐỂ TRỐNG = phép thử chỉ ĐỌC, không ghi gì vào PLC. Chỉ điền địa chỉ chắc chắn không được chương trình PLC dùng.
    /// </summary>
    public string ScratchAddress { get; set; } = "";
}

/// <summary>
/// Dựng và sở hữu đường truyền PLC theo cấu hình. Đăng ký Singleton trong DI: khi ứng dụng thoát, container tự Dispose
/// => đóng kết nối và dừng PLC giả lập.
/// <para>
/// AN TOÀN: nếu cấu hình sai (thiếu file, Mode lạ, cổng sai...) thì <see cref="Link"/> = null và lỗi nằm ở <see cref="Errors"/>.
/// Tuyệt đối không tự chuyển sang PLC giả lập, vì trên máy thật điều đó sẽ khiến hệ thống "chạy bình thường" mà không nối PLC nào.
/// </para>
/// </summary>
public sealed class PlcLinkHost : IDisposable
{
    // - khởi tạo một bộ quy tắc cấu hình (JsonSerializerOptions) cho thư viện System.Text.Json của .NET. Bộ quy tắc này
    //   giúp việc đọc (deserialize) và ghi (serialize) dữ liệu JSON trở nên linh hoạt, "dễ tính" hơn và tối ưu hóa hiệu năng cho ứng dụng.
    // - JsonSerializerOptions: Đây là class có sẵn trong .NET dùng để tinh chỉnh cách thư viện System.Text.Json hoạt động
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true, // Cho phép khớp tên thuộc tính không phân biệt chữ hoa và chữ thường (Case-Insensitive).
        ReadCommentHandling = JsonCommentHandling.Skip, // Cho phép file JSON chứa các dòng chú thích (comment) dạng // hoặc /* ... */,
                                                        // và yêu cầu thư viện bỏ qua (Skip) các dòng comment đó thay vì báo lỗi.
        AllowTrailingCommas = true  // Tùy chọn này bật tính năng cho phép dấu phẩy thừa ở cuối (Trailing Commas).
    };

    private readonly List<string> _errors = new();

    public PlcLinkOptions Options { get; private set; } = new();

    /// <summary>Đường truyền PLC; null nếu cấu hình sai (xem <see cref="Errors"/>).</summary>
    public IPlcLink? Link { get; private set; }

    /// <summary>PLC giả lập (chỉ có khi Mode = Simulated) — code kiểm thử dùng để đóng vai chương trình ladder.</summary>
    public SimulatedPlc? Simulator { get; private set; }

    public IReadOnlyList<string> Errors => _errors;

    public bool IsSimulated => Simulator is not null;

    /// <summary>Địa chỉ thử nghiệm đã phân tích sẵn.</summary>
    public PlcAddress TestAddress { get; private set; }

    /// <summary>Địa chỉ nháp để thử ghi; null = không thử ghi.</summary>
    public PlcAddress? ScratchAddress { get; private set; }

    /// <summary>Mô tả ngắn cho thông báo: "PLC giả lập 127.0.0.1:5011" hoặc "PLC thật 192.168.3.39:5011".</summary>
    public string Description { get; private set; } = "PLC chưa cấu hình";

    /// <param name="configPath">File JSON có mục "PlcLink". Đường dẫn tương đối tính từ thư mục chạy ứng dụng.</param>
    public PlcLinkHost(string configPath)
    {
        if (!Path.IsPathRooted(configPath)) // Kiểm tra xem đường dẫn truyền vào có phải là đường dẫn tuyệt đối hay không
            configPath = Path.Combine(AppContext.BaseDirectory, configPath);
        // Path.Combine(...): Ghép nối thư mục gốc của ứng dụng với tên file tương đối để tạo thành một đường dẫn tuyệt đối hoàn chỉnh.
        // AppContext.BaseDirectory: Lấy đường dẫn thư mục thực tế nơi ứng dụng đang chạy (thư mục chứa file .exe hoặc các file DLL của chương trình).

        Load(configPath);   // đọc file JSON (từ đường dẫn tuyệt đối), phân tích cấu hình và thiết lập kết nối PLC.
    }

    private void Load(string configPath)
    {
        // 1. Đọc mục PlcLink
        PlcLinkOptions? options;
        try
        {
            if (!File.Exists(configPath))
            {
                AddError($"Không tìm thấy file cấu hình PLC: {configPath}");
                return;
            }

            // đọc toàn bộ nội dung file cấu hình dạng JSON và phân tích (parse) nó thành một cấu trúc dữ liệu trên bộ nhớ, sẵn sàng trích xuất thông tin.
            using var doc = JsonDocument.Parse(File.ReadAllText(configPath),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            // - File.ReadAllText(configPath):
            //    -> Mở file tại đường dẫn configPath, đọc toàn bộ nội dung văn bản bên trong file đó và trả về dưới dạng một chuỗi kiểu string.
            // - JsonDocument.Parse(...): phân tích chuỗi JSON vừa đọc thành một JsonDocument (một đối tượng DOM - Document Object Model chỉ đọc,
            //   giúp bạn dễ dàng tìm kiếm và bóc tách các thuộc tính bên trong file JSON).

            // kiểm tra xem file cấu hình JSON có chứa mục bắt buộc tên là "PlcLink" hay không, và xử lý an toàn nếu người dùng quên khai báo mục này.
            if (!doc.RootElement.TryGetProperty("PlcLink", out var section))
            {
                AddError($"File {Path.GetFileName(configPath)} không có mục \"PlcLink\".");
                return;
            }

            options = section.Deserialize<PlcLinkOptions>(JsonOptions);
            // - JsonOptions: bộ quy tắc JsonOptions (mà ta đã phân tích ở câu hỏi đầu tiên) vào để hướng dẫn cách đọc.
            // - đọc các cặp Key: Value trong JSON và tự động điền (gán) giá trị tương ứng vào các thuộc tính (properties) của class PlcLinkOptions.
            // - options: sau dòng này sẽ là một đối tượng C# thực thụ (kiểu PlcLinkOptions?), giúp bạn dễ dàng truy cập dữ liệu bằng chấm câu lệnh
            //   thông thường (ví dụ: options.Host, options.Port) thay vì phải thao tác với chuỗi JSON phức tạp.
        }
        catch (Exception ex)
        {
            AddError($"Không đọc được mục PlcLink: {ex.Message}");
            return;
        }

        if (options is null)
        {
            AddError("Mục \"PlcLink\" rỗng.");
            return;
        }

        // 2. Kiểm tra giá trị — sai thì dừng, KHÔNG dựng gì hết
        if (!Validate(options)) return;
        Options = options;

        // 3. Dựng đường truyền
        try
        {
            if (string.Equals(options.Mode, "Simulated", StringComparison.OrdinalIgnoreCase))
            {
                var simulator = new SimulatedPlc(options.SimulatedPort, loopbackOnly: true);
                simulator.Start();
                Simulator = simulator;
                Link = new McProtocolClient(options.Name, "127.0.0.1", simulator.Port, options.ConnectTimeoutMs, options.ReceiveTimeoutMs);
                Description = $"PLC giả lập 127.0.0.1:{simulator.Port}";
            }
            else
            {
                Link = new McProtocolClient(options.Name, options.Host, options.Port, options.ConnectTimeoutMs, options.ReceiveTimeoutMs);
                Description = $"PLC thật {options.Host}:{options.Port}";
            }
        }
        catch (Exception ex)
        {
            AddError($"Không dựng được đường truyền PLC: {ex.Message}");
            Simulator?.Dispose();
            Simulator = null;
            Link = null;
        }
    }

    private bool Validate(PlcLinkOptions o)
    {
        int before = _errors.Count;

        bool simulated = string.Equals(o.Mode, "Simulated", StringComparison.OrdinalIgnoreCase);
        bool real = string.Equals(o.Mode, "Real", StringComparison.OrdinalIgnoreCase);
        if (!simulated && !real)
            AddError($"PlcLink.Mode = '{o.Mode}' không hợp lệ: chỉ nhận \"Simulated\" hoặc \"Real\".");

        if (real)
        {
            if (string.IsNullOrWhiteSpace(o.Host)) AddError("PlcLink.Host không được rỗng khi Mode = Real.");
            if (o.Port < 1 || o.Port > 65535) AddError($"PlcLink.Port = {o.Port} ngoài phạm vi 1..65535.");
        }

        // SimulatedPort = 0 nghĩa là để hệ điều hành chọn cổng trống — hợp lệ
        if (simulated && (o.SimulatedPort < 0 || o.SimulatedPort > 65535))
            AddError($"PlcLink.SimulatedPort = {o.SimulatedPort} ngoài phạm vi 0..65535.");

        if (o.ConnectTimeoutMs < 1) AddError("PlcLink.ConnectTimeoutMs phải lớn hơn 0.");
        if (o.ReceiveTimeoutMs < 1) AddError("PlcLink.ReceiveTimeoutMs phải lớn hơn 0.");

        if (PlcAddress.TryParse(o.TestAddress, out var test)) TestAddress = test;
        else AddError($"PlcLink.TestAddress = '{o.TestAddress}' không phải địa chỉ PLC hợp lệ (ví dụ D1000).");

        if (!string.IsNullOrWhiteSpace(o.ScratchAddress))
        {
            if (PlcAddress.TryParse(o.ScratchAddress, out var scratch))
            {
                if (scratch.Unit != PlcDeviceUnit.Word)
                    AddError($"PlcLink.ScratchAddress = '{o.ScratchAddress}' phải là thiết bị word (D, W, R, ZR).");
                else
                    ScratchAddress = scratch;
            }
            else
            {
                AddError($"PlcLink.ScratchAddress = '{o.ScratchAddress}' không phải địa chỉ PLC hợp lệ.");
            }
        }

        return _errors.Count == before;
    }

    private void AddError(string message)
    {
        _errors.Add(message);
        System.Diagnostics.Debug.WriteLine($"[PlcLinkHost] {message}");
    }

    public void Dispose()
    {
        Link?.Dispose();
        Simulator?.Dispose();
    }
}