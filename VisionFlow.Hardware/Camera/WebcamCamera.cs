// ==================== Vai trò chính:                Camera thật của laptop (webcam / USB camera) — implement ICamera bằng OpenCvSharp VideoCapture
// ==================== Thành phần / Class tiêu biểu: WebcamCamera
// ==================== Phụ thuộc vào:                ICamera, GrabData, CameraState, OpenCvSharp (VideoCapture)
// ==================== Pattern / Kỹ thuật nổi bật:   Adapter Pattern (VideoCapture -> ICamera) + State Machine (cùng quy tắc với SimulationCamera)
//
// CÁCH DÙNG: đặt file này CÙNG THƯ MỤC / CÙNG PROJECT với SimulationCamera.cs (namespace VisionFlow.Hardware.Camera).
// Sau đó sửa CameraConfig + CameraFactory + App.xaml.cs theo hướng dẫn trong chat.

using System;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;

namespace VisionFlow.Hardware.Camera;

/// <summary>
/// Camera của laptop (hoặc USB camera cắm vào) dùng để chạy VisionFlow với ảnh THẬT khi chưa có camera công nghiệp.
/// Tuân thủ đúng máy trạng thái của <see cref="ICamera"/>:
/// None -> Initialized -> Connected -> (Grabbing) -> ... -> Error.
/// <para>
/// GIỚI HẠN so với camera công nghiệp (cần biết để không hiểu nhầm khi test):
/// <list type="bullet">
/// <item>Không có hardware trigger — chỉ software trigger (mỗi lần GrabSingle() là một lần "bấm chụp").</item>
/// <item>ExposureTime/Gain KHÔNG điều khiển driver webcam (driver thường tự động phơi sáng);
/// thay vào đó áp dụng bằng phần mềm lên pixel giống SimulationCamera, để recipe đổi exposure/gain vẫn thấy ảnh thay đổi.</item>
/// <item>Độ phân giải thực tế do driver quyết định — Width/Height sẽ được cập nhật lại theo giá trị thật sau Connect().</item>
/// </list>
/// </para>
/// </summary>
public sealed class WebcamCamera : ICamera
{
    // volatile: _state được đọc/ghi từ nhiều thread (UI/production loop + luồng continuous grab nền)
    private volatile CameraState _state = CameraState.None;

    private VideoCapture? _capture;          // Đối tượng OpenCV giữ kết nối tới webcam
    private CancellationTokenSource? _grabCts; // Dùng để dừng luồng continuous grab
    private Task? _grabTask;                 // Luồng nền của continuous grab
    private int _frameCounter;               // Số thứ tự frame kể từ lần Connect() gần nhất

    // Khoá bảo vệ VideoCapture: GrabSingle() và luồng continuous grab không được đọc cùng lúc
    private readonly object _grabLock = new();

    public string Name { get; }
    public CameraState State => _state;
    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>Chỉ số thiết bị video: 0 = webcam mặc định của laptop, 1 = camera thứ hai (USB)...</summary>
    public int DeviceIndex { get; }

    /// <summary>Phơi sáng (micro giây) — chỉ áp dụng bằng phần mềm lên ảnh, KHÔNG gửi xuống driver webcam.</summary>
    public double ExposureTime { get; set; } = 10_000;

    /// <summary>Độ khuếch đại — chỉ áp dụng bằng phần mềm (thêm nhiễu), KHÔNG gửi xuống driver webcam.</summary>
    public double Gain { get; set; } = 1.0;

    /// <summary>Bật/tắt việc áp ExposureTime/Gain lên pixel. Với mặc định (10000us, gain 1.0) ảnh không đổi.</summary>
    public bool ApplyExposureSimulation { get; set; } = true;

    /// <summary>Số frame bỏ đi ngay trước mỗi lần GrabSingle(). Webcam có bộ đệm trong driver nên frame đầu tiên
    /// đọc ra thường là ảnh CŨ (chụp trước lúc bạn bấm chụp); xả bớt để ảnh trả về gần với thời điểm trigger nhất.</summary>
    public int FlushFrames { get; set; } = 2;

    /// <summary>Số frame bỏ đi ngay sau khi mở webcam — chờ auto-exposure / auto-white-balance ổn định (frame đầu thường tối hoặc ám màu).</summary>
    public int WarmupFrames { get; set; } = 5;

    /// <summary>Yêu cầu định dạng MJPG từ webcam. Định dạng mặc định (YUY2) thường chỉ đạt vài FPS ở độ phân giải cao;
    /// MJPG cho phép đạt ~30 FPS. Nếu driver không hỗ trợ thì lệnh bị bỏ qua (không lỗi).</summary>
    public bool PreferMjpg { get; set; } = true;

    public event Action<GrabData>? ImageGrabbed;
    public event Action<string>? ErrorOccurred;

    public WebcamCamera(string name, int deviceIndex = 0)
    {
        Name = name;
        DeviceIndex = deviceIndex;
    }

    // ====================================================================
    // VÒNG ĐỜI: Initialize -> Connect -> (GrabSingle | StartContinuousGrab) -> Disconnect
    // ====================================================================

    public bool Initialize(int width, int height)
    {
        // Giống SimulationCamera: chỉ được gọi 1 lần khi đang ở trạng thái None
        if (_state != CameraState.None) return false;

        Width = width;   // Độ phân giải MONG MUỐN — giá trị thật sẽ được cập nhật sau Connect()
        Height = height;
        _state = CameraState.Initialized;
        return true;
    }

    public bool Connect()
    {
        if (_state != CameraState.Initialized) return false;

        try
        {
            lock (_grabLock)
            {
                // Ưu tiên DirectShow (DSHOW): trên Windows mở nhanh và ổn định hơn backend mặc định (MSMF).
                _capture = new VideoCapture(DeviceIndex, VideoCaptureAPIs.DSHOW);

                // Nếu DSHOW không mở được thì thử lại bằng backend mặc định của OpenCV
                if (!_capture.IsOpened())
                {
                    _capture.Dispose();
                    _capture = new VideoCapture(DeviceIndex);
                }

                if (!_capture.IsOpened())
                {
                    ReleaseCaptureLocked();
                    Fail($"WebcamCamera '{Name}': không mở được camera index {DeviceIndex}. " +
                         "Kiểm tra: (1) đã bật quyền camera cho ứng dụng desktop trong Windows Settings > Privacy > Camera, " +
                         "(2) không có ứng dụng khác (Zoom/Teams/Camera) đang giữ camera, (3) thử DeviceIndex khác (1, 2...).");
                    return false;
                }

                // Yêu cầu MJPG TRƯỚC khi đặt độ phân giải (một số driver chỉ cho phép độ phân giải cao ở MJPG)
                if (PreferMjpg)
                    _capture.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('M', 'J', 'P', 'G'));

                // Yêu cầu độ phân giải — driver sẽ chọn mức gần nhất mà camera hỗ trợ
                _capture.Set(VideoCaptureProperties.FrameWidth, Width);
                _capture.Set(VideoCaptureProperties.FrameHeight, Height);

                // Đọc lại kích thước THẬT để ROI/toạ độ trong flow khớp với ảnh nhận được
                int actualW = (int)_capture.Get(VideoCaptureProperties.FrameWidth);
                int actualH = (int)_capture.Get(VideoCaptureProperties.FrameHeight);
                if (actualW > 0 && actualH > 0)
                {
                    Width = actualW;
                    Height = actualH;
                }

                // Bỏ vài frame đầu để camera ổn định phơi sáng/cân bằng trắng
                using var warmup = new Mat();
                for (int i = 0; i < WarmupFrames; i++)
                    _capture.Read(warmup);

                _frameCounter = 0; // Theo quy ước ICamera: đếm frame kể từ lần Connect() gần nhất
                _state = CameraState.Connected;
                return true;
            }
        }
        catch (Exception ex)
        {
            lock (_grabLock) ReleaseCaptureLocked();
            Fail($"WebcamCamera '{Name}': lỗi khi Connect() — {ex.Message}");
            return false;
        }
    }

    public void Disconnect()
    {
        // 1. Dừng luồng continuous grab (nếu đang chạy) và chờ nó kết thúc trước khi giải phóng camera
        StopContinuousGrab();
        try { _grabTask?.Wait(2000); } catch (AggregateException) { /* bỏ qua lỗi từ luồng nền đã bị huỷ */ }
        _grabTask = null;

        // 2. Nhả webcam (đèn báo của camera sẽ tắt)
        lock (_grabLock) ReleaseCaptureLocked();

        // 3. Giống SimulationCamera: quay về Initialized để có thể Connect() lại
        if (_state != CameraState.None) _state = CameraState.Initialized;
    }

    public void Dispose() => Disconnect();

    // ====================================================================
    // CHỤP ẢNH
    // ====================================================================

    /// <summary>Chụp đúng 1 frame (software trigger). Trả về null nếu camera chưa Connected hoặc chụp lỗi.</summary>
    public GrabData? GrabSingle()
    {
        // Giống SimulationCamera: chỉ hợp lệ khi đang Connected (đang Grabbing liên tục thì không chụp đơn được)
        if (_state != CameraState.Connected) return null;

        lock (_grabLock)
        {
            if (_capture is null) return null;

            try
            {
                // Xả các frame cũ đang nằm trong bộ đệm driver để ảnh trả về là ảnh "mới nhất"
                for (int i = 0; i < FlushFrames; i++)
                    _capture.Grab();

                return ReadFrameLocked();
            }
            catch (Exception ex)
            {
                _state = CameraState.Error;
                ErrorOccurred?.Invoke($"WebcamCamera '{Name}': lỗi khi chụp — {ex.Message}");
                return null;
            }
        }
    }

    public void StartContinuousGrab()
    {
        if (_state != CameraState.Connected) return;

        _state = CameraState.Grabbing;
        _grabCts = new CancellationTokenSource();
        var token = _grabCts.Token;

        _grabTask = Task.Run(() =>
        {
            while (!token.IsCancellationRequested)
            {
                GrabData? data;
                lock (_grabLock)
                {
                    // Read() tự chặn cho đến khi webcam có frame mới => tốc độ vòng lặp chính là FPS của webcam
                    data = _capture is null ? null : ReadFrameLocked();
                }

                if (data is null)
                {
                    // ReadFrameLocked() đã đặt state = Error và bắn ErrorOccurred; dừng luồng nền
                    break;
                }

                var handler = ImageGrabbed;
                if (handler is null)
                    data.Dispose();   // Không ai nhận thì giải phóng luôn, tránh rò rỉ bộ nhớ Mat
                else
                    handler(data);    // Bên nhận sở hữu GrabData và chịu trách nhiệm Dispose()
            }
        }, token);
    }

    public void StopContinuousGrab()
    {
        _grabCts?.Cancel();
        if (_state == CameraState.Grabbing) _state = CameraState.Connected;
    }

    // ====================================================================
    // NỘI BỘ
    // ====================================================================

    /// <summary>Đọc 1 frame từ webcam. PHẢI được gọi khi đang giữ _grabLock. Trả về null (và chuyển state = Error) nếu lỗi.</summary>
    private GrabData? ReadFrameLocked()
    {
        var mat = new Mat();

        if (_capture is null || !_capture.Read(mat) || mat.Empty())
        {
            mat.Dispose();
            Fail($"WebcamCamera '{Name}': không đọc được frame (camera bị rút, bị ứng dụng khác chiếm hoặc timeout).");
            return null;
        }

        if (ApplyExposureSimulation)
            ApplyExposureAndGain(mat);

        _frameCounter++;

        return new GrabData
        {
            Image = mat,                 // GrabData sở hữu Mat này (xem LiveCameraSourceTool: chuyển giao sang MatVisionImage)
            Width = mat.Width,
            Height = mat.Height,
            Timestamp = DateTime.Now,
            FrameNumber = _frameCounter,
            ExposureUsed = ExposureTime
        };
    }

    /// <summary>Áp ExposureTime/Gain lên pixel bằng phần mềm — cùng công thức với SimulationCamera để hai loại camera cho kết quả nhất quán.</summary>
    private void ApplyExposureAndGain(Mat mat)
    {
        const double baselineExposureUs = 10_000.0; // Mốc chuẩn: đúng giá trị này thì ảnh không đổi
        double alpha = Math.Clamp(ExposureTime / baselineExposureUs, 0.1, 4.0);

        // Chỉ nhân độ sáng khi khác mốc — tránh tốn CPU trên mỗi frame khi không cần
        if (Math.Abs(alpha - 1.0) > 1e-3)
            Cv2.ConvertScaleAbs(mat, mat, alpha, 0);

        // Gain > 1 => thêm nhiễu Gaussian để mô phỏng nhiễu cảm biến khi khuếch đại
        if (Gain > 1.0)
        {
            using var noise = new Mat(mat.Size(), mat.Type());
            double sigma = Math.Clamp((Gain - 1.0) * 8.0, 0, 40);
            Cv2.Randn(noise, new Scalar(0, 0, 0), new Scalar(sigma, sigma, sigma));
            Cv2.Add(mat, noise, mat);
        }
    }

    /// <summary>Đưa camera vào trạng thái Error và báo lỗi ra ngoài (KHÔNG throw lên UI thread — đúng hợp đồng của ICamera.ErrorOccurred).</summary>
    private void Fail(string message)
    {
        _state = CameraState.Error;
        ErrorOccurred?.Invoke(message);
    }

    /// <summary>Giải phóng VideoCapture. PHẢI được gọi khi đang giữ _grabLock.</summary>
    private void ReleaseCaptureLocked()
    {
        if (_capture is null) return;
        try { _capture.Release(); } catch { /* nuốt lỗi khi giải phóng để không che lỗi chính */ }
        _capture.Dispose();
        _capture = null;
    }
}