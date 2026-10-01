// ==================== Vai trò chính:                Nạp cấu hình camera từ file JSON -> tạo camera -> đăng ký vào CameraHub (thay cho code hard-code trong App.xaml.cs)
// ==================== Thành phần / Class tiêu biểu: CameraBootstrapper
// ==================== Phụ thuộc vào:                CameraConfig, CameraFactory, CameraHub, System.Text.Json (có sẵn trong .NET, không cần NuGet)
// ==================== Pattern / Kỹ thuật nổi bật:   Config-driven (Buổi 127) + IDisposable để DI container tự dọn dẹp khi ứng dụng thoát
//
// CÁCH DÙNG: đặt file này CÙNG THƯ MỤC / CÙNG PROJECT với CameraFactory.cs (namespace VisionFlow.Hardware.Camera).
// Sau đó đăng ký 1 dòng trong App.ConfigureServices và resolve 1 lần lúc khởi động (xem hướng dẫn trong chat).

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace VisionFlow.Hardware.Camera;

/// <summary>
/// Đọc mục "Cameras" trong file cấu hình (mặc định appsettings.json cạnh file .exe), tạo từng camera bằng
/// <see cref="CameraFactory"/> rồi đăng ký vào <see cref="CameraHub"/>.
/// <para>
/// Nhờ vậy: đổi/thêm/bớt camera (Simulation, Webcam, sau này Basler/Hikvision) chỉ cần SỬA FILE JSON,
/// KHÔNG phải sửa App.xaml.cs hay build lại. Chỉ khi thêm một LOẠI camera mới (Type mới) mới phải thêm 1 case
/// vào CameraFactory — đúng 1 chỗ duy nhất.
/// </para>
/// <para>
/// Lớp này IDisposable: khi đăng ký làm Singleton trong DI, container sẽ gọi Dispose() lúc ứng dụng thoát
/// và Dispose() sẽ gỡ + giải phóng toàn bộ camera đã đăng ký (nhả webcam, dừng luồng nền).
/// </para>
/// </summary>
public sealed class CameraBootstrapper : IDisposable
{
    // Không phân biệt hoa/thường tên thuộc tính JSON ("name" hay "Name" đều được); cho phép chú thích và dấu phẩy thừa
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // Không phân biệt chữ hoa/thường khi map tên thuộc tính từ JSON sang C# (ví dụ: "name" hay "Name" đều khớp)
        PropertyNameCaseInsensitive = true,

        // Cho phép và tự động bỏ qua các dòng chú thích (comment) bên trong file JSON
        ReadCommentHandling = JsonCommentHandling.Skip,

        // Cho phép dấu phẩy thừa ở phần tử cuối cùng của mảng hoặc đối tượng JSON mà không báo lỗi
        AllowTrailingCommas = true
    };

    private readonly List<string> _registeredNames = new(); // Tên các camera đã đăng ký thành công (để Dispose gỡ đúng những cái này)
    private readonly List<string> _errors = new();          // Lỗi cấu hình — KHÔNG ném exception để 1 camera hỏng không làm sập cả ứng dụng

    /// <summary>Tên các camera đã tạo và đăng ký thành công.</summary>
    public IReadOnlyList<string> RegisteredNames => _registeredNames;

    /// <summary>Danh sách lỗi khi nạp cấu hình (file thiếu, JSON sai, Type không hỗ trợ, trùng tên...). Rỗng = mọi thứ ổn.</summary>
    public IReadOnlyList<string> Errors => _errors;

    /// <param name="configPath">Đường dẫn file JSON có mục "Cameras". Nếu là đường dẫn tương đối thì tính từ thư mục chạy ứng dụng.</param>
    public CameraBootstrapper(string configPath)
    {
        // Kiểm tra xem đường dẫn truyền vào có phải là đường dẫn tuyệt đối hay không.
        // Nếu là đường dẫn tương đối (ví dụ: "appsettings.json"), tiến hành nối nó với thư mục gốc chạy ứng dụng (AppContext.BaseDirectory).
        if (!Path.IsPathRooted(configPath))
            configPath = Path.Combine(AppContext.BaseDirectory, configPath);

        // Gọi phương thức Load để tiến hành đọc file cấu hình, khởi tạo và đăng ký các camera.
        Load(configPath);
    }

    private void Load(string configPath)
    {
        // 1. Đọc file và lấy mảng "Cameras"
        if (!File.Exists(configPath))
        {
            AddError($"Không tìm thấy file cấu hình camera: {configPath}");
            return;
        }

        List<CameraConfig> configs;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(configPath),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

            if (!doc.RootElement.TryGetProperty("Cameras", out var camerasElement))
            {
                AddError($"File {Path.GetFileName(configPath)} không có mục \"Cameras\".");
                return;
            }

            configs = camerasElement.Deserialize<List<CameraConfig>>(JsonOptions) ?? new List<CameraConfig>();
        }
        catch (Exception ex)
        {
            AddError($"Không đọc được {Path.GetFileName(configPath)}: {ex.Message}");
            return;
        }

        // 2. Tạo và đăng ký từng camera; lỗi của camera nào chỉ ảnh hưởng camera đó
        foreach (var cfg in configs)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(cfg.Name))
                    throw new InvalidOperationException("thiếu \"Name\" (tên camera dùng để chọn trong tham số CameraName của tool).");

                if (_registeredNames.Contains(cfg.Name, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException("trùng tên với camera đã đăng ký trước đó.");

                // ImageFolder dạng tương đối (vd "SampleImages") -> tính từ thư mục chạy ứng dụng
                if (!string.IsNullOrWhiteSpace(cfg.ImageFolder) && !Path.IsPathRooted(cfg.ImageFolder))
                    cfg.ImageFolder = Path.Combine(AppContext.BaseDirectory, cfg.ImageFolder);

                // CameraFactory.Create chỉ Initialize() (đặt cấu hình), CHƯA mở phần cứng —
                // camera được Connect() lần đầu khi LiveCameraSourceTool chạy.
                CameraHub.Register(CameraFactory.Create(cfg));
                _registeredNames.Add(cfg.Name);
            }
            catch (Exception ex)
            {
                AddError($"Camera '{cfg.Name}' (Type={cfg.Type}): {ex.Message}");
            }
        }
    }

    private void AddError(string message)
    {
        _errors.Add(message);
        System.Diagnostics.Debug.WriteLine($"[CameraBootstrapper] {message}"); // Hiện ở cửa sổ Output (Debug) của Visual Studio
    }

    /// <summary>Gỡ đăng ký và giải phóng (Dispose) tất cả camera do bootstrapper này tạo ra.</summary>
    public void Dispose()
    {
        foreach (var name in _registeredNames)
            CameraHub.Unregister(name); // Unregister() sẽ gọi camera.Dispose() -> Disconnect() -> nhả webcam
        _registeredNames.Clear();
    }
}