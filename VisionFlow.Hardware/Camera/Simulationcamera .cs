// ==================== Vai trò chính:                Camera giả lập (không cần hardware) — implement đầy đủ ICamera để dev/test toàn bộ ứng dụng
// ==================== Thành phần / Class tiêu biểu: SimulationCamera
// ==================== Phụ thuộc vào:                ICamera, OpenCvSharp
// ==================== Pattern / Kỹ thuật nổi bật:   Adapter Pattern + State Machine + mô phỏng đặc tính camera thật (exposure/gain/FPS/lỗi ngẫu nhiên)
//
// MỤC TIÊU THIẾT KẾ: không chỉ "trả về 1 ảnh có sẵn" như bản tối thiểu trong tài liệu Buổi 109, mà cố
// gắng mô phỏng CÀNG GIỐNG CAMERA THẬT CÀNG TỐT để lộ ra sớm những lỗi mà code chỉ đọc ảnh tĩnh sẽ
// không bao giờ gặp phải:
//   1. ExposureTime/Gain THỰC SỰ ảnh hưởng lên ảnh xuất ra (sáng/tối, nhiễu) — test được logic tự
//      động điều chỉnh exposure của ứng dụng, không phải tham số vô tri.
//   2. Trộn ảnh OK/NG theo tỉ lệ cấu hình (giống line sản xuất thật có cả hàng lỗi lẫn hàng tốt) —
//      QUAN TRỌNG: Camera KHÔNG gắn nhãn "đây là ảnh NG" vào GrabData, giống hệt camera thật không
//      biết sản phẩm nó vừa chụp là hàng lỗi hay không — việc phán định OK/NG là việc của VisionFlow
//      (Tool + Logic), không phải của camera. Điều này giữ đúng ranh giới trách nhiệm.
//   3. Mô phỏng FPS có jitter (không đều tuyệt đối như hardware thật) và có thể áp timeout/lỗi ngẫu
//      nhiên (rớt frame, mất kết nối) với tần suất cấu hình được — để test retry/heartbeat/reconnect
//      logic (Buổi 118) mà không phải rút cáp mạng thật.
//   4. Cung cấp SimulateDisconnect()/SimulateRecoverableError() để chủ động kích hoạt kịch bản lỗi
//      trong Unit Test hoặc demo, thay vì phải chờ lỗi ngẫu nhiên xảy ra.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;

namespace VisionFlow.Hardware.Camera;

/// <summary>
/// Camera giả lập nạp ảnh từ thư mục thay vì hardware thật. Hỗ trợ 2 kiểu thư mục:
/// <list type="bullet">
/// <item><b>Thư mục phẳng</b>: mọi ảnh coi như cùng loại, xoay vòng tuần tự (đúng bản gốc Buổi 109).</item>
/// <item><b>Thư mục có OK/ và NG/ bên trong</b>: trộn ảnh theo <see cref="DefectRate"/> để mô phỏng
/// tỉ lệ hàng lỗi thật của một dây chuyền — hữu ích để test Yield/UPH/cảnh báo (Buổi 123) mà không
/// cần chờ hàng lỗi thật xuất hiện.</item>
/// </list>
/// </summary>
public sealed class SimulationCamera : ICamera
{
    private CameraState _state = CameraState.None;
    private CancellationTokenSource? _grabCts;
    private Task? _grabTask;

    // Danh sách ảnh đã quét, tách riêng theo OK/NG để có thể trộn theo tỉ lệ
    private string[] _okFiles = Array.Empty<string>();
    private string[] _ngFiles = Array.Empty<string>();
    private string[] _flatFiles = Array.Empty<string>(); // dùng khi KHÔNG có cấu trúc OK/NG
    private bool _useOkNgFolders;

    private int _okCursor;
    private int _ngCursor;
    private int _flatCursor;
    private int _frameCounter;

    private readonly object _grabLock = new(); // GrabSingle() có thể bị gọi từ nhiều thread (vd UI preview + production loop) — khoá lại cho an toàn, giống pattern "lock" ở RobotTcpClient (Buổi 120)
    private readonly Random _rng;

    private static readonly string[] SupportedExtensions = { ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff" };

    public string Name { get; }
    public CameraState State => _state;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public double ExposureTime { get; set; } = 10_000; // 10ms mặc định — giá trị "chuẩn" dùng làm mốc mô phỏng độ sáng
    public double Gain { get; set; } = 1.0;

    /// <summary>Đường dẫn thư mục chứa ảnh test — có thể là thư mục phẳng hoặc chứa 2 thư mục con OK/NG.</summary>
    public string ImageFolder { get; }

    /// <summary>FPS mục tiêu khi StartContinuousGrab() — mặc định 30 (giống camera công nghiệp phổ thông).</summary>
    public double SimulatedFps { get; set; } = 30.0;

    /// <summary>Độ rung thời gian giữa các frame (0..1, tỉ lệ trên chu kỳ 1/FPS) — camera thật KHÔNG BAO
    /// GIỜ có FPS tuyệt đối đều, luôn có jitter nhỏ do xử lý ảnh/USB/network. Mặc định 10%.</summary>
    public double FpsJitter { get; set; } = 0.10;

    /// <summary>Tỉ lệ ảnh NG được trộn vào khi thư mục có cấu trúc OK/NG (0..1). Mặc định 0.15 (15%) —
    /// gần với tỉ lệ lỗi thực tế của nhiều dây chuyền SMT/lắp ráp.</summary>
    public double DefectRate { get; set; } = 0.15;

    /// <summary>Xác suất một lần GrabSingle()/frame continuous bị lỗi giả lập (timeout hoặc frame rỗng)
    /// (0..1) — dùng để test retry logic (RunSingleInspectionWithRetry, Buổi 116) và Heartbeat/Reconnect
    /// (Buổi 118) mà không cần rút cáp mạng thật. Mặc định 0 (tắt) để không làm phiền các test khác.</summary>
    public double SimulatedFailureRate { get; set; } = 0.0;

    /// <summary>Nếu true, ExposureTime/Gain hiện tại sẽ thực sự áp dụng lên pixel ảnh (brightness +
    /// noise) trước khi trả ra ngoài — bật mặc định vì đây chính là lý do tồn tại của lớp này (mô phỏng
    /// đặc tính camera thật thay vì chỉ "phát lại" ảnh có sẵn).</summary>
    public bool ApplyExposureSimulation { get; set; } = true;

    public event Action<GrabData>? ImageGrabbed;
    public event Action<string>? ErrorOccurred;

    public SimulationCamera(string name, string imageFolder, int? randomSeed = null)
    {
        Name = name;
        ImageFolder = imageFolder;
        // Seed cố định khi truyền vào (Unit Test cần kết quả lặp lại được), ngẫu nhiên thật khi chạy demo/dev.
        _rng = randomSeed.HasValue ? new Random(randomSeed.Value) : new Random();
    }

    public bool Initialize(int width, int height)
    {
        if (_state != CameraState.None) return false;
        Width = width;
        Height = height;
        _state = CameraState.Initialized;
        return true;
    }

    public bool Connect()
    {
        if (_state != CameraState.Initialized) return false;

        if (!Directory.Exists(ImageFolder))
        {
            ErrorOccurred?.Invoke($"SimulationCamera '{Name}': thư mục không tồn tại: {ImageFolder}");
            _state = CameraState.Error;
            return false;
        }

        string okDir = Path.Combine(ImageFolder, "OK");
        string ngDir = Path.Combine(ImageFolder, "NG");
        _useOkNgFolders = Directory.Exists(okDir) && Directory.Exists(ngDir);

        if (_useOkNgFolders)
        {
            _okFiles = ScanImages(okDir);
            _ngFiles = ScanImages(ngDir);

            if (_okFiles.Length == 0 && _ngFiles.Length == 0)
            {
                ErrorOccurred?.Invoke($"SimulationCamera '{Name}': thư mục OK/NG không có ảnh hợp lệ nào.");
                _state = CameraState.Error;
                return false;
            }
        }
        else
        {
            _flatFiles = ScanImages(ImageFolder);

            if (_flatFiles.Length == 0)
            {
                ErrorOccurred?.Invoke($"SimulationCamera '{Name}': không tìm thấy ảnh test trong thư mục {ImageFolder}.");
                _state = CameraState.Error;
                return false;
            }
        }

        _okCursor = 0;
        _ngCursor = 0;
        _flatCursor = 0;
        _state = CameraState.Connected;
        return true;
    }

    public GrabData? GrabSingle()
    {
        if (_state != CameraState.Connected) return null;

        lock (_grabLock)
        {
            // Mô phỏng lỗi ngẫu nhiên trước khi tốn công load ảnh — giống timeout thật xảy ra ngay ở bước bắt tín hiệu.
            if (SimulatedFailureRate > 0 && _rng.NextDouble() < SimulatedFailureRate)
            {
                ErrorOccurred?.Invoke($"SimulationCamera '{Name}': mô phỏng lỗi grab (timeout ngẫu nhiên).");
                return null;
            }

            try
            {
                return LoadNextFrame();
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke($"SimulationCamera '{Name}': lỗi khi đọc ảnh giả lập — {ex.Message}");
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

        _grabTask = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                GrabData? data;
                lock (_grabLock)
                {
                    data = (SimulatedFailureRate > 0 && _rng.NextDouble() < SimulatedFailureRate)
                        ? null
                        : SafeLoadNextFrame();
                }

                if (data is not null)
                {
                    ImageGrabbed?.Invoke(data);
                }
                else
                {
                    ErrorOccurred?.Invoke($"SimulationCamera '{Name}': rớt frame trong continuous grab (mô phỏng).");
                }

                // Mô phỏng chu kỳ FPS có jitter — camera thật không bao giờ đều tuyệt đối.
                double periodMs = 1000.0 / Math.Max(1.0, SimulatedFps);
                double jitterFactor = 1.0 + (_rng.NextDouble() * 2 - 1) * FpsJitter; // vd FpsJitter=0.1 -> dao động [0.9, 1.1]
                int delayMs = Math.Max(1, (int)(periodMs * jitterFactor));

                try
                {
                    await Task.Delay(delayMs, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }, token);
    }

    public void StopContinuousGrab()
    {
        _grabCts?.Cancel();
        if (_state == CameraState.Grabbing) _state = CameraState.Connected;
    }

    public void Disconnect()
    {
        StopContinuousGrab();
        _okFiles = Array.Empty<string>();
        _ngFiles = Array.Empty<string>();
        _flatFiles = Array.Empty<string>();
        if (_state != CameraState.None) _state = CameraState.Initialized;
    }

    /// <summary>
    /// Kích hoạt CHỦ ĐỘNG kịch bản mất kết nối — dùng trong demo/Unit Test để kiểm tra logic
    /// Reconnect/Heartbeat của ứng dụng (Buổi 118) mà không cần rút cáp mạng thật.
    /// </summary>
    public void SimulateDisconnect()
    {
        _state = CameraState.Error;
        ErrorOccurred?.Invoke($"SimulationCamera '{Name}': mất kết nối (mô phỏng chủ động).");
    }

    public void Dispose() => Disconnect();

    // ====================================================================
    // NỘI BỘ
    // ====================================================================

    private static string[] ScanImages(string dir) =>
        Directory.GetFiles(dir)
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    // Bọc LoadNextFrame để dùng an toàn trong vòng lặp continuous grab (không để 1 file ảnh hỏng làm chết cả Task nền)
    private GrabData? SafeLoadNextFrame()
    {
        try { return LoadNextFrame(); }
        catch { return null; }
    }

    private GrabData LoadNextFrame()
    {
        string filePath = PickNextFile();
        _frameCounter++;

        Mat mat = Cv2.ImRead(filePath, ImreadModes.Color);
        if (mat.Empty())
            throw new InvalidOperationException($"Không đọc được ảnh: {filePath}");

        if (mat.Width != Width || mat.Height != Height)
            Cv2.Resize(mat, mat, new Size(Width, Height));

        if (ApplyExposureSimulation)
            ApplyExposureAndGain(mat);

        return new GrabData
        {
            Image = mat,
            Width = mat.Width,
            Height = mat.Height,
            Timestamp = DateTime.Now,
            FrameNumber = _frameCounter,
            ExposureUsed = ExposureTime
        };
    }

    /// <summary>Chọn file ảnh kế tiếp — tuần tự trong mỗi nhóm OK/NG, trộn theo <see cref="DefectRate"/>
    /// khi có cấu trúc thư mục OK/NG; nếu không thì xoay vòng tuần tự trên toàn bộ thư mục phẳng
    /// (giữ đúng hành vi gốc của tài liệu Buổi 109 khi người dùng không cần mô phỏng tỉ lệ lỗi).</summary>
    private string PickNextFile()
    {
        if (!_useOkNgFolders)
        {
            if (_flatFiles.Length == 0) throw new InvalidOperationException("Chưa Connect() hoặc thư mục rỗng.");
            string f = _flatFiles[_flatCursor];
            _flatCursor = (_flatCursor + 1) % _flatFiles.Length;
            return f;
        }

        bool pickNg = _ngFiles.Length > 0 && (_okFiles.Length == 0 || _rng.NextDouble() < DefectRate);

        if (pickNg)
        {
            string f = _ngFiles[_ngCursor];
            _ngCursor = (_ngCursor + 1) % _ngFiles.Length;
            return f;
        }
        else
        {
            string f = _okFiles[_okCursor];
            _okCursor = (_okCursor + 1) % _okFiles.Length;
            return f;
        }
    }

    /// <summary>
    /// Mô phỏng ảnh hưởng thật của ExposureTime/Gain lên pixel — KHÔNG PHẢI để tính chính xác vật lý
    /// cảm biến, chỉ cần đủ "giống thật" để lộ ra lỗi khi ứng dụng chỉnh exposure/gain mà không thấy
    /// ảnh thay đổi (bug rất hay gặp khi code quên bind tham số hardware thật).
    /// - ExposureTime cao hơn baseline (10000us) -> ảnh sáng hơn tuyến tính (alpha).
    /// - Gain cao hơn 1.0 -> cộng thêm nhiễu Gaussian (mô phỏng nhiễu cảm biến khi khuếch đại tín hiệu).
    /// </summary>
    private void ApplyExposureAndGain(Mat mat)
    {
        const double baselineExposureUs = 10_000.0;
        double alpha = Math.Clamp(ExposureTime / baselineExposureUs, 0.1, 4.0); // hệ số nhân độ sáng, giới hạn để không tràn/tối om
        double beta = 0; // không dịch offset, chỉ nhân tuyến tính giống thay đổi exposure thật

        Cv2.ConvertScaleAbs(mat, mat, alpha, beta);

        if (Gain > 1.0)
        {
            using var noise = new Mat(mat.Size(), mat.Type());
            double noiseStdDev = Math.Clamp((Gain - 1.0) * 8.0, 0, 40); // Gain càng cao, nhiễu càng nhiều
            Cv2.Randn(noise, new Scalar(0, 0, 0), new Scalar(noiseStdDev, noiseStdDev, noiseStdDev));
            Cv2.Add(mat, noise, mat);
        }
    }
}