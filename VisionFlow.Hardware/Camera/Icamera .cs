// ==================== Vai trò chính:                Trừu tượng hóa camera công nghiệp — tách Application khỏi SDK phần cứng cụ thể (Basler/Hikvision/...)
// ==================== Thành phần / Class tiêu biểu: ICamera (interface), GrabData, CameraState
// ==================== Phụ thuộc vào:                OpenCvSharp (Mat)
// ==================== Pattern / Kỹ thuật nổi bật:   Dependency Inversion Principle (DIP) — đúng tinh thần Buổi 109 "Camera Abstraction Layer"
//
// GHI CHÚ TÍCH HỢP: namespace này (VisionFlow.Hardware.Camera) hiện KHÔNG tồn tại trong project VisionFlow
// đã có (chỉ có VisionFlow.Core / Engine / Tools / Editor / WPF). Đây là phân hệ MỚI cần thêm vào solution
// để lấp khoảng trống: hệ thống hiện tại có ~100 Vision Tool rất mạnh nhưng CHƯA có bất kỳ Camera
// Abstraction Layer nào — GrabImageTool chỉ đọc file tĩnh từ đĩa, không mô phỏng vòng đời camera thật
// (Connect/Disconnect/State machine/ExposureTime hardware/reconnect...). Xem báo cáo đánh giá kiến trúc
// đã trao đổi để biết đầy đủ lý do.

using System;
using OpenCvSharp;

namespace VisionFlow.Hardware.Camera;

/// <summary>
/// Trạng thái vòng đời (lifecycle) của một camera công nghiệp. Mọi implementation của
/// <see cref="ICamera"/> (thật hoặc giả lập) đều PHẢI tuân thủ đúng máy trạng thái này:
/// None -> Initialized -> Connected -> (Grabbing) -> ... -> Error. Mỗi method chỉ hợp lệ ở
/// đúng trạng thái tương ứng (xem bảng quy tắc chuyển trạng thái trong tài liệu Buổi 109).
/// </summary>
public enum CameraState
{
    /// <summary>Vừa khởi tạo object, chưa gọi Initialize().</summary>
    None,
    /// <summary>Đã cấu hình Width/Height qua Initialize(), sẵn sàng Connect().</summary>
    Initialized,
    /// <summary>Đã mở kết nối hardware (GigE/USB) hoặc nguồn ảnh giả lập, sẵn sàng GrabSingle().</summary>
    Connected,
    /// <summary>Đang chụp liên tục (free-run/continuous mode) nền — dữ liệu trả về qua event.</summary>
    Grabbing,
    /// <summary>Lỗi hardware (mất kết nối, timeout...) — cần Disconnect() rồi Connect() lại để khôi phục.</summary>
    Error
}

/// <summary>
/// Kết quả trả về THỐNG NHẤT của một lần chụp ảnh, dùng chung cho camera thật lẫn giả lập.
/// Dùng <see cref="Mat"/> (không phải byte[] thô) vì đây là container ảnh chuẩn công nghiệp,
/// tự chứa metadata (kích thước, kênh, kiểu dữ liệu) và cho phép gọi thẳng hàm OpenCV mà không
/// cần convert. Mỗi Adapter (HikvisionCamera, SimulationCamera, ...) chịu trách nhiệm chuyển
/// buffer SDK riêng của mình sang Mat.
/// </summary>
public sealed class GrabData : IDisposable
{
    /// <summary>Ảnh vừa chụp — object này SỞ HỮU vùng nhớ Mat, phải Dispose khi dùng xong
    /// (hoặc chuyển giao quyền sở hữu cho lớp khác — xem <c>LiveCameraSourceTool</c>).</summary>
    public Mat Image { get; init; } = new();

    /// <summary>Thời điểm chụp theo giờ máy tính — dùng để tính FPS thực tế / latency.</summary>
    public DateTime Timestamp { get; init; } = DateTime.Now;

    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>Số thứ tự frame kể từ lần Connect() gần nhất — tăng dần liên tục, không reset
    /// khi StopContinuousGrab(), giúp phát hiện frame bị bỏ sót khi debug.</summary>
    public int FrameNumber { get; init; }

    /// <summary>ExposureTime (micro giây) thực tế đã dùng để chụp frame này — có thể khác giá trị
    /// đang set trên camera nếu hardware giới hạn (clamp), nên luôn đọc lại giá trị thật ở đây.</summary>
    public double ExposureUsed { get; init; }

    public void Dispose() => Image?.Dispose();
}

/// <summary>
/// Hợp đồng chuẩn cho MỌI loại camera công nghiệp (Basler, Hikvision, Cognex, hoặc giả lập).
/// Application/ViewModel chỉ được phép phụ thuộc vào interface này — KHÔNG BAO GIỜ import trực
/// tiếp SDK hãng camera cụ thể trong tầng business logic (Dependency Inversion Principle). Đổi
/// hãng camera = đổi 1 dòng DI registration, không sửa business logic (xem <c>CameraFactory</c>).
/// </summary>
public interface ICamera : IDisposable
{
    /// <summary>Tên định danh camera trong hệ thống (vd "TopCam", "SideCam") — dùng cho multi-camera,
    /// log, và để <c>CameraHub</c>/<c>LiveCameraSourceTool</c> tra cứu bằng tên.</summary>
    string Name { get; }

    /// <summary>Trạng thái hiện tại — luôn kiểm tra trước khi gọi operation khác để tránh crash runtime.</summary>
    CameraState State { get; }

    int Width { get; }
    int Height { get; }

    /// <summary>Thời gian phơi sáng (micro giây). Set giá trị mới có hiệu lực từ frame kế tiếp.</summary>
    double ExposureTime { get; set; }

    /// <summary>Độ khuếch đại tín hiệu cảm biến (đơn vị theo hãng, thường là dB). Buổi 116 (Recipe
    /// Management) lưu kèm ExposureTime trong VisionRecipe nên interface đưa luôn vào đây thay vì để
    /// mỗi adapter tự thêm riêng — tránh mỗi hãng lại có API set gain khác tên nhau.</summary>
    double Gain { get; set; }

    /// <summary>Cấu hình resolution ban đầu. Gọi đúng 1 lần khi khởi tạo ứng dụng, trước Connect().</summary>
    bool Initialize(int width, int height);

    /// <summary>Mở kết nối hardware (GigE/USB) hoặc nguồn ảnh giả lập.</summary>
    bool Connect();

    void Disconnect();

    /// <summary>Chụp đúng 1 frame theo kiểu Software Trigger — dùng cho vòng lặp production đồng bộ
    /// kiểu "grab → xử lý → đánh giá OK/NG → grab tiếp" (xem Production Workflow, Buổi 116).</summary>
    GrabData? GrabSingle();

    /// <summary>Bắt đầu chụp liên tục nền (free-run) — kết quả trả về qua sự kiện <see cref="ImageGrabbed"/>,
    /// dùng cho màn hình Live Preview trước khi bấm START sản xuất thật.</summary>
    void StartContinuousGrab();

    void StopContinuousGrab();

    /// <summary>Bắn ra mỗi khi có frame mới trong chế độ continuous grab.</summary>
    event Action<GrabData>? ImageGrabbed;

    /// <summary>Bắn ra khi có lỗi hardware (mất kết nối, timeout...) — Application dùng để cảnh báo
    /// hoặc tự động reconnect (xem ReconnectableClient pattern ở Buổi 118), KHÔNG được throw exception
    /// thẳng lên UI thread.</summary>
    event Action<string>? ErrorOccurred;
}