// ==================== Vai trò chính:                Service Locator cho PLC — cho phép các Tool (bắt buộc constructor KHÔNG THAM SỐ,
//                                                     xem ToolRegistry.Register) truy cập IPlcLink đã được PlcLinkHost dựng sẵn từ
//                                                     appsettings.json (Simulated hoặc Real — PLC Q04UDEHCPU thật qua Ethernet MC Protocol 3E).
// ==================== Thành phần / Class tiêu biểu: PlcHub
// ==================== Phụ thuộc vào:                PlcLinkHost, IPlcLink (đã có sẵn trong VisionFlow.Hardware.Plc)
// ==================== Pattern / Kỹ thuật nổi bật:   Service Locator tĩnh — CÙNG PATTERN với CameraHub (VisionFlow.Hardware.Camera)
//                                                     để giữ nhất quán kiến trúc: Tool không "with" DI container được, nên mọi truy cập
//                                                     phần cứng từ trong Tool đều đi qua 1 Hub tĩnh giống nhau (CameraHub, nay có thêm PlcHub).
//
// VỊ TRÍ ĐẶT FILE: project VisionFlow.Hardware, thư mục Plc/ (cùng nơi với PlcLinkHost.cs, IPlcLink.cs) — namespace VisionFlow.Hardware.Plc.
//
// CÁCH DÙNG (chỉ cần sửa 1 dòng trong App.xaml.cs — xem HUONG_DAN_TICH_HOP.md mục 2):
//   var plc = _provider.GetRequiredService<PlcLinkHost>();
//   ...
//   PlcHub.Register(plc);   // <-- THÊM DÒNG NÀY, ngay sau khi resolve PlcLinkHost
//
// Từ đó mọi Tool trong VisionFlow.Tools.PCControl (PlcWriteBitTool, PlcReadWordTool, ...) gọi PlcHub.Link để đọc/ghi PLC,
// và PcControlViewModel (màn hình vận hành tay) cũng gọi PlcHub.Host để lấy trạng thái/Description hiển thị lên UI.

using System;

namespace VisionFlow.Hardware.Plc;

/// <summary>
/// Điểm truy cập tĩnh (Service Locator) tới PLC duy nhất của hệ thống, đã được <see cref="PlcLinkHost"/> dựng sẵn
/// (đọc Mode/Host/Port từ appsettings.json ngay lúc khởi động App).
/// <para>
/// LÝ DO CẦN LỚP NÀY: <c>ToolRegistry.Register</c> chỉ chấp nhận Tool có constructor KHÔNG THAM SỐ và tự khởi tạo
/// bằng <c>Activator.CreateInstance</c> — vì vậy PlcWriteBitTool/PlcReadWordTool... không thể nhận PlcLinkHost qua
/// Dependency Injection kiểu constructor thông thường như FlowEditorViewModel. Đây CHÍNH XÁC là lý do
/// VisionFlow.Hardware.Camera đã có sẵn CameraHub cho GrabImageTool/LiveCameraSourceTool — PlcHub áp dụng lại
/// đúng pattern đó cho PLC, KHÔNG phát minh cách làm mới.
/// </para>
/// </summary>
public static class PlcHub
{
    private static PlcLinkHost? _host;

    /// <summary>Gọi đúng 1 lần trong App.xaml.cs (OnStartup), ngay sau khi resolve PlcLinkHost từ DI container.</summary>
    public static void Register(PlcLinkHost host) => _host = host ?? throw new ArgumentNullException(nameof(host));

    /// <summary>PlcLinkHost gốc — dùng khi UI (màn hình PC Control) cần đọc Description/Errors/Options để hiển thị.</summary>
    public static PlcLinkHost? Host => _host;

    /// <summary>Đường truyền PLC hiện dùng; null nếu chưa Register hoặc appsettings.json cấu hình sai
    /// (kiểm tra PlcHub.Host?.Errors để biết lý do cụ thể).</summary>
    public static IPlcLink? Link => _host?.Link;

    /// <summary>true nếu đã có đường truyền PLC sẵn sàng dùng (đã Register + cấu hình hợp lệ — KHÔNG có nghĩa là đã Connect,
    /// hãy kiểm tra thêm Link.IsConnected hoặc tự gọi ConnectAsync trước khi Đọc/Ghi lần đầu).</summary>
    public static bool IsAvailable => Link is not null;
}