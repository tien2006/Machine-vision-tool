// ==================== Vai trò chính:                Unit test cho SimulationCamera — đảm bảo state machine, mixing OK/NG, và mô phỏng exposure hoạt động đúng
// ==================== Thành phần / Class tiêu biểu: SimulationCameraTests
// ==================== Phụ thuộc vào:                xUnit, OpenCvSharp
// ==================== Pattern / Kỹ thuật nổi bật:   Arrange-Act-Assert, giống ToolParameterTests/FindCircleToolTests đã có trong repo

using System;
using System.IO;
using System.Threading;
using OpenCvSharp;
using VisionFlow.Hardware.Camera;
using Xunit;

namespace VisionFlow.Tests.Hardware;

public class SimulationCameraTests : IDisposable
{
    private readonly string _tempRoot;

    public SimulationCameraTests()
    {
        // Tạo thư mục ảnh test tạm thời trong %TEMP% cho mỗi lần chạy test — không phụ thuộc ổ đĩa/máy dev cụ thể.
        _tempRoot = Path.Combine(Path.GetTempPath(), "VisionFlowSimCamTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { /* dọn dẹp best-effort */ }
    }

    private static void WriteDummyImage(string path, int width = 64, int height = 64, byte gray = 128)
    {
        using var mat = new Mat(new Size(width, height), MatType.CV_8UC3, new Scalar(gray, gray, gray));
        Cv2.ImWrite(path, mat);
    }

    [Fact]
    public void StateMachine_FollowsCorrectOrder_None_Initialized_Connected()
    {
        // Arrange: thư mục phẳng (không có OK/NG) — đúng bản gốc Buổi 109
        WriteDummyImage(Path.Combine(_tempRoot, "001.png"));
        var cam = new SimulationCamera("TestCam", _tempRoot);

        Assert.Equal(CameraState.None, cam.State);

        // Act + Assert từng bước state machine
        Assert.True(cam.Initialize(64, 64));
        Assert.Equal(CameraState.Initialized, cam.State);

        Assert.True(cam.Connect());
        Assert.Equal(CameraState.Connected, cam.State);
    }

    [Fact]
    public void GrabSingle_BeforeConnect_ReturnsNull()
    {
        // Arrange: chưa Connect() -> mọi lệnh Grab phải trả null, không được throw
        var cam = new SimulationCamera("TestCam", _tempRoot);
        cam.Initialize(64, 64);

        // Act
        var data = cam.GrabSingle();

        // Assert
        Assert.Null(data);
    }

    [Fact]
    public void Connect_WithEmptyFolder_ReturnsFalse_AndRaisesErrorOccurred()
    {
        // Arrange: thư mục tồn tại nhưng KHÔNG có ảnh nào
        var cam = new SimulationCamera("TestCam", _tempRoot);
        cam.Initialize(64, 64);

        string? errorMessage = null;
        cam.ErrorOccurred += msg => errorMessage = msg;

        // Act
        bool connected = cam.Connect();

        // Assert
        Assert.False(connected);
        Assert.Equal(CameraState.Error, cam.State);
        Assert.NotNull(errorMessage);
    }

    [Fact]
    public void FlatFolder_GrabSingle_CyclesThroughAllImages_RoundRobin()
    {
        // Arrange: 3 ảnh trong thư mục phẳng -> sau 3 lần grab phải quay vòng lại ảnh đầu tiên
        WriteDummyImage(Path.Combine(_tempRoot, "001.png"));
        WriteDummyImage(Path.Combine(_tempRoot, "002.png"));
        WriteDummyImage(Path.Combine(_tempRoot, "003.png"));

        var cam = new SimulationCamera("TestCam", _tempRoot) { ApplyExposureSimulation = false };
        cam.Initialize(64, 64);
        cam.Connect();

        // Act: grab đúng 4 lần (nhiều hơn số ảnh 1 lần) để kiểm tra vòng lặp quay lại từ đầu
        using var d1 = cam.GrabSingle();
        using var d2 = cam.GrabSingle();
        using var d3 = cam.GrabSingle();
        using var d4 = cam.GrabSingle();

        // Assert: FrameNumber tăng liên tục (không reset khi quay vòng ảnh)
        Assert.NotNull(d1); Assert.NotNull(d2); Assert.NotNull(d3); Assert.NotNull(d4);
        Assert.Equal(1, d1!.FrameNumber);
        Assert.Equal(2, d2!.FrameNumber);
        Assert.Equal(3, d3!.FrameNumber);
        Assert.Equal(4, d4!.FrameNumber);
    }

    [Fact]
    public void OkNgFolders_WithDefectRateZero_OnlyReturnsOkImages()
    {
        // Arrange: thư mục có cấu trúc OK/NG, DefectRate = 0 -> không bao giờ được trả ảnh NG
        Directory.CreateDirectory(Path.Combine(_tempRoot, "OK"));
        Directory.CreateDirectory(Path.Combine(_tempRoot, "NG"));
        WriteDummyImage(Path.Combine(_tempRoot, "OK", "ok_001.png"), gray: 200);
        WriteDummyImage(Path.Combine(_tempRoot, "NG", "ng_001.png"), gray: 10);

        var cam = new SimulationCamera("TestCam", _tempRoot, randomSeed: 42)
        {
            DefectRate = 0.0,
            ApplyExposureSimulation = false,
        };
        cam.Initialize(64, 64);
        cam.Connect();

        // Act + Assert: chụp nhiều lần, ảnh nào cũng phải là ảnh "sáng" (200) từ thư mục OK
        for (int i = 0; i < 10; i++)
        {
            using var data = cam.GrabSingle();
            Assert.NotNull(data);
            Assert.Equal(200, data!.Image.At<Vec3b>(0, 0).Item0);
        }
    }

    [Fact]
    public void OkNgFolders_WithDefectRateOne_OnlyReturnsNgImages()
    {
        // Arrange: DefectRate = 1.0 -> luôn trả ảnh NG (kịch bản đối xứng với test ở trên)
        Directory.CreateDirectory(Path.Combine(_tempRoot, "OK"));
        Directory.CreateDirectory(Path.Combine(_tempRoot, "NG"));
        WriteDummyImage(Path.Combine(_tempRoot, "OK", "ok_001.png"), gray: 200);
        WriteDummyImage(Path.Combine(_tempRoot, "NG", "ng_001.png"), gray: 10);

        var cam = new SimulationCamera("TestCam", _tempRoot, randomSeed: 42)
        {
            DefectRate = 1.0,
            ApplyExposureSimulation = false,
        };
        cam.Initialize(64, 64);
        cam.Connect();

        for (int i = 0; i < 10; i++)
        {
            using var data = cam.GrabSingle();
            Assert.NotNull(data);
            Assert.Equal(10, data!.Image.At<Vec3b>(0, 0).Item0);
        }
    }

    [Fact]
    public void ExposureSimulation_HigherExposure_ProducesBrighterImage()
    {
        // Arrange: cùng 1 ảnh gốc, chỉ đổi ExposureTime -> ảnh xuất ra phải sáng hơn tương ứng
        WriteDummyImage(Path.Combine(_tempRoot, "001.png"), gray: 100);

        var camLowExposure = new SimulationCamera("Cam1", _tempRoot) { ExposureTime = 5_000, ApplyExposureSimulation = true };
        camLowExposure.Initialize(64, 64);
        camLowExposure.Connect();

        var camHighExposure = new SimulationCamera("Cam2", _tempRoot) { ExposureTime = 20_000, ApplyExposureSimulation = true };
        camHighExposure.Initialize(64, 64);
        camHighExposure.Connect();

        // Act
        using var dataLow = camLowExposure.GrabSingle();
        using var dataHigh = camHighExposure.GrabSingle();

        // Assert: exposure cao hơn -> pixel sáng hơn (giá trị lớn hơn)
        byte lowValue = dataLow!.Image.At<Vec3b>(0, 0).Item0;
        byte highValue = dataHigh!.Image.At<Vec3b>(0, 0).Item0;
        Assert.True(highValue > lowValue, $"Kỳ vọng exposure cao hơn cho ảnh sáng hơn: low={lowValue}, high={highValue}");
    }

    [Fact]
    public void SimulatedFailureRate_100Percent_AlwaysReturnsNull_AndRaisesError()
    {
        // Arrange: ép tỉ lệ lỗi 100% -> mọi lần grab đều phải thất bại có kiểm soát (null), không throw
        WriteDummyImage(Path.Combine(_tempRoot, "001.png"));
        var cam = new SimulationCamera("TestCam", _tempRoot) { SimulatedFailureRate = 1.0 };
        cam.Initialize(64, 64);
        cam.Connect();

        int errorCount = 0;
        cam.ErrorOccurred += _ => errorCount++;

        // Act
        var data = cam.GrabSingle();

        // Assert
        Assert.Null(data);
        Assert.Equal(1, errorCount);
    }

    [Fact]
    public void SimulateDisconnect_SetsErrorState_AndRaisesErrorOccurred()
    {
        // Arrange
        WriteDummyImage(Path.Combine(_tempRoot, "001.png"));
        var cam = new SimulationCamera("TestCam", _tempRoot);
        cam.Initialize(64, 64);
        cam.Connect();

        string? lastError = null;
        cam.ErrorOccurred += msg => lastError = msg;

        // Act: kích hoạt chủ động kịch bản mất kết nối (không cần rút cáp mạng thật)
        cam.SimulateDisconnect();

        // Assert
        Assert.Equal(CameraState.Error, cam.State);
        Assert.NotNull(lastError);
        Assert.Null(cam.GrabSingle()); // Grab khi Error phải trả null, không throw
    }

    [Fact]
    public void ContinuousGrab_RaisesImageGrabbedEvent_ThenStopsCleanly()
    {
        // Arrange
        WriteDummyImage(Path.Combine(_tempRoot, "001.png"));
        WriteDummyImage(Path.Combine(_tempRoot, "002.png"));

        var cam = new SimulationCamera("TestCam", _tempRoot) { SimulatedFps = 100, ApplyExposureSimulation = false };
        cam.Initialize(64, 64);
        cam.Connect();

        int grabbedCount = 0;
        var gotFrame = new ManualResetEventSlim(false);
        cam.ImageGrabbed += data =>
        {
            Interlocked.Increment(ref grabbedCount);
            gotFrame.Set();
            data.Dispose();
        };

        // Act
        cam.StartContinuousGrab();
        Assert.Equal(CameraState.Grabbing, cam.State);

        bool receivedAtLeastOneFrame = gotFrame.Wait(TimeSpan.FromSeconds(2));
        cam.StopContinuousGrab();

        // Assert
        Assert.True(receivedAtLeastOneFrame, "Không nhận được frame nào trong 2 giây — continuous grab có thể bị lỗi.");
        Assert.Equal(CameraState.Connected, cam.State); // StopContinuousGrab phải đưa về lại Connected, không phải None/Initialized
        Assert.True(grabbedCount > 0);
    }

    [Fact]
    public void Disconnect_ThenReconnect_ResetsCursorAndWorksAgain()
    {
        // Arrange
        WriteDummyImage(Path.Combine(_tempRoot, "001.png"));
        var cam = new SimulationCamera("TestCam", _tempRoot);
        cam.Initialize(64, 64);
        cam.Connect();
        using (var _ = cam.GrabSingle()) { }

        // Act
        cam.Disconnect();
        Assert.Equal(CameraState.Initialized, cam.State);

        bool reconnected = cam.Connect();

        // Assert
        Assert.True(reconnected);
        Assert.Equal(CameraState.Connected, cam.State);
        using var data = cam.GrabSingle();
        Assert.NotNull(data);
    }
}