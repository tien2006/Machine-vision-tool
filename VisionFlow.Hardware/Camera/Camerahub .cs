// ==================== Vai trò chính:                Cầu nối giữa DI container (App.xaml.cs) và VisionTool (constructor rỗng, tạo qua Activator.CreateInstance)
// ==================== Thành phần / Class tiêu biểu: CameraHub
// ==================== Phụ thuộc vào:                ICamera
// ==================== Pattern / Kỹ thuật nổi bật:   Service Locator có kiểm soát — giải pháp thực dụng, xem ghi chú bên dưới

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace VisionFlow.Hardware.Camera;

/// <summary>
/// VisionFlow.Core.Registry.ToolRegistry.Register() BẮT BUỘC mỗi loại Tool phải có constructor không
/// tham số ("must have a parameterless constructor") vì Tool được khởi tạo qua
/// <c>Activator.CreateInstance(toolType)</c> mỗi khi người dùng kéo-thả từ Palette, hoặc khi
/// JsonFlowRepository nạp lại flow từ file. Điều đó có nghĩa <c>LiveCameraSourceTool</c> KHÔNG THỂ
/// nhận <see cref="ICamera"/> qua constructor injection như các ViewModel khác trong hệ thống
/// (FlowEditorViewModel, MainViewModel...) vẫn đang làm.
///
/// <see cref="CameraHub"/> là một Service Locator có kiểm soát, đóng vai trò "bảng điện" trung gian:
/// <list type="number">
/// <item>App.xaml.cs (nơi CÓ DI container) tạo camera qua <see cref="CameraFactory"/> rồi
/// <see cref="Register"/> vào Hub theo tên — y hệt bước AddKeyedSingleton trong tài liệu Buổi 109,
/// chỉ khác nơi lưu trữ.</item>
/// <item><c>LiveCameraSourceTool</c> (KHÔNG có DI) chỉ cần biết TÊN camera — một
/// <c>ToolParameter&lt;string&gt;</c> bình thường, giống hệt cách <c>GrabImageTool</c> hiện tại dùng
/// tham số <c>FolderPath</c> — để tra cứu lại đúng instance khi <c>OnExecute()</c> chạy.</item>
/// </list>
///
/// ĐÂY LÀ GIẢI PHÁP THỰC DỤNG, ít xâm lấn kiến trúc hiện có nhất. Về lâu dài, nếu muốn "sạch" hơn theo
/// đúng Dependency Inversion tuyệt đối (không dùng static state), nên cân nhắc mở rộng
/// <c>IToolContext</c> (VisionFlow.Core.Tools) để Engine (FlowExecutor) bơm một <c>ICameraProvider</c>
/// vào Tool ngay tại tham số <c>context</c> của <c>OnExecute(IToolContext context)</c> thay vì Tool tự
/// đi tra Hub tĩnh. Việc đó đòi hỏi sửa ToolContext + FlowExecutor (đổi kiến trúc rộng hơn) nên KHÔNG
/// làm trong lần bổ sung này — CameraHub cho phép dùng được ngay hôm nay mà không đụng vào Core/Engine
/// đã có.
/// </summary>
public static class CameraHub
{
    private static readonly ConcurrentDictionary<string, ICamera> Cameras = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Đăng ký (hoặc ghi đè nếu trùng tên) một camera — gọi trong App.xaml.cs ngay sau khi
    /// tạo bằng CameraFactory.Create(...).</summary>
    public static void Register(ICamera camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        Cameras[camera.Name] = camera;
    }

    /// <summary>Tra cứu camera theo tên. Trả về null nếu chưa đăng ký — LiveCameraSourceTool sẽ tự báo
    /// lỗi rõ ràng (ToolExecutionException) thay vì để NullReferenceException mù mờ.</summary>
    public static ICamera? Find(string name) =>
        !string.IsNullOrWhiteSpace(name) && Cameras.TryGetValue(name, out var cam) ? cam : null;

    /// <summary>Danh sách tên camera đã đăng ký — dùng để đổ vào ComboBox chọn camera trên UI
    /// Parameter Editor của LiveCameraSourceTool.</summary>
    public static IReadOnlyCollection<string> RegisteredNames => Cameras.Keys.ToArray();

    /// <summary>Gỡ đăng ký + Dispose 1 camera — gọi khi App.OnExit() hoặc khi đổi cấu hình camera lúc
    /// runtime (vd người dùng chuyển từ Simulation sang camera thật mà không khởi động lại app).</summary>
    public static void Unregister(string name)
    {
        if (Cameras.TryRemove(name, out var cam))
            cam.Dispose();
    }

    /// <summary>Gỡ + Dispose TẤT CẢ camera đã đăng ký — gọi trong App.OnExit() để đảm bảo giải phóng
    /// tài nguyên hardware/Task nền đúng cách khi tắt ứng dụng.</summary>
    public static void UnregisterAll()
    {
        foreach (var name in Cameras.Keys.ToArray())
            Unregister(name);
    }
}