// ==================== Vai trò chính:                Node nguồn — nạp ảnh cho pipeline bằng cách chụp qua ICamera (SimulationCamera hoặc camera thật) thay vì đọc thẳng file tĩnh
// ==================== Thành phần / Class tiêu biểu: LiveCameraSourceTool
// ==================== Phụ thuộc vào:                VisionFlow.Hardware.Camera (ICamera/CameraHub), VisionFlow.Core.*, VisionFlow.Tools.Imaging
// ==================== Pattern / Kỹ thuật nổi bật:   Adapter Pattern (GrabData.Image -> IVisionImage) + Service Locator (CameraHub, xem ghi chú trong file đó)
//
// LƯU Ý ĐẶT FILE: về mặt namespace, class này thuộc VisionFlow.Tools.Acquisition (cùng nhóm với
// GrabImageTool/VideoLoaderTool đã có) nên khi đưa vào solution thật, nên di chuyển file này sang đúng
// thư mục Tools/Acquisition/ thay vì để chung với Hardware/Camera — ở đây gộp chung 1 chỗ để tiện gửi
// cho bạn xem trọn bộ.
//
// SO SÁNH VỚI GrabImageTool (đã có sẵn trong repo):
//   - GrabImageTool: đọc thẳng file/thư mục trên đĩa, KHÔNG đi qua khái niệm "camera" nào cả — không
//     có Connect/Disconnect, không có ExposureTime hardware, không có state Error/reconnect.
//   - LiveCameraSourceTool: đi qua ĐÚNG interface ICamera (Buổi 109) — nghĩa là flow build & test hôm
//     nay bằng SimulationCamera có thể chạy y nguyên (không sửa 1 dòng flow JSON đã lưu, chỉ đổi DI
//     registration trong App.xaml.cs) khi lắp camera Basler/Hikvision thật vào sau này. Đây chính là
//     lợi ích cốt lõi của Camera Abstraction Layer mà GrabImageTool hiện tại chưa tận dụng được.
// Cả 2 tool có thể tồn tại song song: GrabImageTool cho việc test nhanh 1 tấm ảnh mẫu "vàng", còn
// LiveCameraSourceTool cho việc test/production với vòng đời camera đầy đủ.

using System;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Hardware.Camera;
using VisionFlow.Tools.Imaging;

namespace VisionFlow.Tools.Acquisition;

/// <summary>
/// Node nguồn (Input Source) chụp ảnh từ một <see cref="ICamera"/> đã đăng ký trong
/// <see cref="CameraHub"/> — theo kiểu Software Trigger: mỗi lần Execute() gọi đúng 1 lần
/// <see cref="ICamera.GrabSingle"/>. Dùng SimulationCamera khi dev/test, camera hardware thật khi
/// production, chỉ khác nhau ở CameraConfig.Type trong App.xaml.cs.
/// </summary>
[ToolMetadata("LiveCameraSource", DisplayName = "Camera Source", Category = "InputSource",
    Description = "Grab 1 frame from an ICamera registered in CameraHub (SimulationCamera or real hardware) via software trigger.")]
public sealed class LiveCameraSourceTool : VisionTool
{
    // Tên camera cần tra cứu trong CameraHub (vd "TopCam") — đổ ComboBox từ CameraHub.RegisteredNames trên Parameter Editor.
    private readonly ToolParameter<string> _cameraName;

    // Cho phép override exposure ngay tại node (vd khi test nhanh 1 giá trị khác Recipe hiện tại). 0 = giữ nguyên giá trị đang set trên camera.
    private readonly ToolParameter<double> _exposureOverrideUs;

    // true = nếu camera đang ở trạng thái Error, tool tự gọi Disconnect()+Connect() lại 1 lần trước khi grab, thay vì throw ngay.
    private readonly ToolParameter<bool> _autoRecoverOnError;

    private readonly OutputPort<IVisionImage> _output;

    public LiveCameraSourceTool()
    {
        _cameraName = AddParameter("CameraName", string.Empty, "Camera", category: "Source", order: 1);
        _exposureOverrideUs = AddParameter("ExposureOverrideUs", 0.0, "Exposure Override (us)",
            min: 0.0, max: 1_000_000.0, category: "Source", order: 2);
        _autoRecoverOnError = AddParameter("AutoRecoverOnError", true, "Auto-reconnect on Error",
            category: "Source", order: 3);

        _output = AddOutput<IVisionImage>("Image");
    }

    protected override void OnExecute(IToolContext context)
    {
        string name = _cameraName.Value;
        if (string.IsNullOrWhiteSpace(name))
            throw new ToolExecutionException(
                "Camera Source: chưa chọn CameraName. Đăng ký camera trong CameraHub (App.xaml.cs) trước, " +
                "rồi chọn tên tương ứng trong Parameter Editor.");

        ICamera? camera = CameraHub.Find(name);
        if (camera is null)
            throw new ToolExecutionException(
                $"Camera Source: không tìm thấy camera '{name}' trong CameraHub. " +
                "Kiểm tra lại DI registration hoặc tên có gõ sai chính tả không.");

        EnsureConnected(camera, name);

        if (_exposureOverrideUs.Value > 0)
            camera.ExposureTime = _exposureOverrideUs.Value;

        GrabData? data = camera.GrabSingle();
        if (data is null)
            throw new ToolExecutionException(
                $"Camera Source: GrabSingle() trả về null cho camera '{name}' " +
                "(timeout hoặc lỗi giả lập — xem ErrorOccurred/log của camera để biết chi tiết).");

        // Chuyển giao quyền sở hữu Mat sang MatVisionImage. KHÔNG gọi data.Dispose() ở đây: GrabData
        // chỉ là "phong bì" chứa Mat, Dispose() của nó sẽ Dispose() luôn Mat bên trong — nếu gọi thì
        // ảnh sẽ bị giải phóng ngay trước khi các Tool phía sau kịp dùng. MatVisionImage/FlowExecutor
        // sẽ chịu trách nhiệm Dispose Mat này khi kết thúc vòng chạy (xem FlowExecutor.DisposeTracked).
        _output.Value = new MatVisionImage(data.Image);

        context.Log(
            $"CameraSource[{name}]: frame #{data.FrameNumber} @ {data.Timestamp:HH:mm:ss.fff}, " +
            $"exposure={data.ExposureUsed:F0}us, state={camera.State}");
    }

    private void EnsureConnected(ICamera camera, string name)
    {
        switch (camera.State)
        {
            case CameraState.Connected:
            case CameraState.Grabbing:
                return; // sẵn sàng, không cần làm gì thêm

            case CameraState.Initialized:
                if (!camera.Connect())
                    throw new ToolExecutionException($"Camera Source: Connect() thất bại cho camera '{name}'.");
                return;

            case CameraState.Error:
                if (!_autoRecoverOnError.Value)
                    throw new ToolExecutionException(
                        $"Camera Source: camera '{name}' đang ở trạng thái Error. " +
                        "Bật 'Auto-reconnect on Error' hoặc tự Disconnect()+Connect() lại thủ công.");

                camera.Disconnect();
                if (!camera.Connect())
                    throw new ToolExecutionException(
                        $"Camera Source: tự động reconnect thất bại cho camera '{name}' (vẫn ở trạng thái Error).");
                return;

            case CameraState.None:
                throw new ToolExecutionException(
                    $"Camera Source: camera '{name}' chưa được Initialize() — kiểm tra lại CameraFactory.Create() trong App.xaml.cs.");

            default:
                throw new ToolExecutionException($"Camera Source: trạng thái camera '{name}' không xác định: {camera.State}.");
        }
    }
}