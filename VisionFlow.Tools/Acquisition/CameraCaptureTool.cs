

/// đây là tool duy nhất phụ thuộc SDK phần cứng độc quyền, code trước sau này dùng


/*
// ==================== Vai trò chính:                Node nguồn — lấy ảnh SỐNG từ camera công nghiệp (GigE/USB3) qua Hikrobot MVS SDK
// ==================== Thành phần / Class tiêu biểu: CameraCaptureTool
// ==================== Phụ thuộc vào:                MvCameraControl.Net (Hikrobot MVS SDK, add reference MvCameraControl.Net.dll)
// ==================== Pattern / Kỹ thuật nổi bật:   Tool tự sở hữu vòng đời SDK (giống _cachedPredictor của
//                       ObjectDetectionEngineTool) + Once-Auto convergence + ApplyMvsSettings() đẩy lại tham số mỗi lần Execute
//
// LƯU Ý QUAN TRỌNG: các lời gọi MV_CC_*_NET bên dưới viết theo đúng API cổ điển của MvCameraControl.Net
// (bản SDK phổ biến nhất, class MyCamera). Nếu bản SDK bạn cài có chữ ký hàm/tên struct hơi khác
// (VD SDK rất mới dùng namespace MvCameraControl kiểu Device/IDevice), gõ "cam." trong IntelliSense
// để xem đúng danh sách hàm rồi chỉnh lại bên trong #region "Hikrobot MVS SDK Interop" - phần logic
// Tool (Parameters/Outputs/OnExecute) ở ngoài KHÔNG cần đổi gì.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using MvCameraControl.Net; // using MyCamera, MV_CC_DEVICE_INFO_LIST, MVCC_FLOATVALUE, MV_FRAME_OUT...
using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;

namespace VisionFlow.Tools.Acquisition;

public enum CameraConnectionType { All, GigEVision, USB3Vision }
public enum AutoMode { Off, Once, Continuous }
public enum TriggerModeOption { Off, On }
public enum TriggerSourceOption { Software, Line0, Line1, Line2, Line3, Counter0 }
public enum AcquisitionModeOption { Continuous, SingleFrame, MultiFrame }
public enum RotateAngle { Deg0 = 0, Deg90 = 90, Deg180 = 180, Deg270 = 270 }
public enum PixelFormatOption { Mono8, Mono10, Mono12, Mono16, RGB8, BGR8, BayerRG8, BayerBG8, BayerGR8, BayerGB8 }

/// <summary>
/// Node nguồn duy nhất không có Input - lấy ảnh trực tiếp từ camera công nghiệp qua Hikrobot MVS SDK.
/// Camera phải được Scan + Open + Start (qua tab "Camera" trên UI, gọi các public method của chính Tool này)
/// TRƯỚC khi Run Node - đúng theo hành vi camera thật (không tự ý mở/đóng camera ngầm mỗi lần chạy flow).
/// Mỗi lần Execute() chỉ: đẩy lại toàn bộ tham số xuống camera (ApplyMvsSettings) rồi đọc frame mới nhất.
/// </summary>
[ToolMetadata("CameraCapture", DisplayName = "Camera Capture", Category = "InputSource",
    Description = "Grab live frames from an industrial GigE/USB3 camera via the Hikrobot MVS SDK.")]
public sealed class CameraCaptureTool : VisionTool, IDisposable
{
    #region 1. Outputs (không có Input - Camera là nguồn đầu pipeline)
    private readonly OutputPort<IVisionImage> _outImage;
    private readonly OutputPort<string> _outCameraInfo;
    private readonly OutputPort<long> _outFrameNumber;
    private readonly OutputPort<DateTime> _outTimestamp;
    private readonly OutputPort<bool> _outConnectionStatus;
    private readonly OutputPort<double> _outActualFps;
    #endregion

    #region 2. Parameters
    // --- Tab Source ---
    private readonly ToolParameter<CameraConnectionType> _connectionType;
    private readonly ToolParameter<string> _selectedCamera;   // Điền tự động qua nút Scan trên UI, không gõ tay
    private readonly ToolParameter<bool> _autoDiscovery;
    private readonly ToolParameter<int> _connectionTimeout;   // ms

    // --- Tab Processing ---
    private readonly ToolParameter<RotateAngle> _rotateImage;
    private readonly ToolParameter<AutoMode> _exposureAuto;
    private readonly ToolParameter<double> _exposureTime;     // micro giây (μs)
    private readonly ToolParameter<AutoMode> _gainAuto;
    private readonly ToolParameter<double> _gain;             // dB
    private readonly ToolParameter<double> _gamma;
    private readonly ToolParameter<double> _blackLevel;
    private readonly ToolParameter<AutoMode> _balanceWhiteAuto; // chỉ có tác dụng với camera màu
    private readonly ToolParameter<TriggerModeOption> _triggerMode;
    private readonly ToolParameter<TriggerSourceOption> _triggerSource;
    private readonly ToolParameter<AcquisitionModeOption> _acquisitionMode;
    private readonly ToolParameter<bool> _frameRateEnable;
    private readonly ToolParameter<double> _acquisitionFrameRate;
    private readonly ToolParameter<int> _offsetX;
    private readonly ToolParameter<int> _offsetY;
    private readonly ToolParameter<bool> _reverseX;
    private readonly ToolParameter<bool> _reverseY;
    private readonly ToolParameter<int> _gevScpsPacketSize; // 0 = auto
    private readonly ToolParameter<int> _gevScpd;           // 0 = auto

    // --- Tab Output ---
    private readonly ToolParameter<int> _width;
    private readonly ToolParameter<int> _height;
    private readonly ToolParameter<PixelFormatOption> _pixelFormat;
    private readonly ToolParameter<double> _fps; // chỉ là gợi ý hiển thị, không ép cứng camera (dùng FrameRateEnable để ép cứng)
    #endregion

    #region 3. Trạng thái nội bộ (SDK handle, frame buffer, Once-Auto bookkeeping)
    private MyCamera? _cam;
    private bool _isGrabbing;
    private readonly object _frameLock = new();
    private byte[]? _latestRawBuffer;
    private uint _latestWidth, _latestHeight;
    private MvGvspPixelType _latestPixelType;
    private long _frameNumber;
    private DateTime _latestTimestamp;
    private readonly Stopwatch _fpsWatch = Stopwatch.StartNew();
    private double _lastElapsedMs = 1;

    // Once-Auto: đợi camera hội tụ vài frame rồi tự đọc giá trị hội tụ về và tự set lại Off,
    // đúng hành vi mô tả trong tài liệu: "Once" chỉ chạy 1 lần, không lặp lại ở các lần Execute sau.
    private bool _exposureOnceHandled;
    private bool _gainOnceHandled;

    public bool IsOpen => _cam != null;
    public bool IsGrabbing => _isGrabbing;
    #endregion

    public CameraCaptureTool()
    {
        _outImage = AddOutput<IVisionImage>("Image", "Image");
        _outCameraInfo = AddOutput<string>("CameraInfo", "Camera Info");
        _outFrameNumber = AddOutput<long>("FrameNumber", "Frame Number");
        _outTimestamp = AddOutput<DateTime>("Timestamp", "Timestamp");
        _outConnectionStatus = AddOutput<bool>("ConnectionStatus", "Connection Status");
        _outActualFps = AddOutput<double>("ActualFPS", "Actual FPS");

        _connectionType = AddParameter("ConnectionType", CameraConnectionType.All, "Connection Type", category: "Source", order: 1);
        _selectedCamera = AddParameter("SelectedCamera", string.Empty, "Selected Camera", category: "Source", order: 2);
        _autoDiscovery = AddParameter("AutoDiscovery", true, "Auto Discovery", category: "Source", order: 3);
        _connectionTimeout = AddParameter("ConnectionTimeout", 5000, "Connection Timeout (ms)", min: 100, max: 60000, category: "Source", order: 4);

        _rotateImage = AddParameter("RotateImage", RotateAngle.Deg0, "Rotate Image", category: "Processing", order: 1);
        _exposureAuto = AddParameter("ExposureAuto", AutoMode.Off, "Exposure Auto", category: "Processing", order: 2);
        _exposureTime = AddParameter("ExposureTime", 10000.0, "Exposure Time (μs)", min: 1, max: 1_000_000, category: "Processing", order: 3);
        _gainAuto = AddParameter("GainAuto", AutoMode.Off, "Gain Auto", category: "Processing", order: 4);
        _gain = AddParameter("Gain", 0.0, "Gain (dB)", min: 0, max: 30, category: "Processing", order: 5);
        _gamma = AddParameter("Gamma", 1.0, "Gamma", min: 0.1, max: 4.0, category: "Processing", order: 6);
        _blackLevel = AddParameter("BlackLevel", 0.0, "Black Level", min: 0, max: 255, category: "Processing", order: 7);
        _balanceWhiteAuto = AddParameter("BalanceWhiteAuto", AutoMode.Off, "Balance White Auto", category: "Processing", order: 8);
        _triggerMode = AddParameter("TriggerMode", TriggerModeOption.Off, "Trigger Mode", category: "Processing", order: 9);
        _triggerSource = AddParameter("TriggerSource", TriggerSourceOption.Software, "Trigger Source", category: "Processing", order: 10);
        _acquisitionMode = AddParameter("AcquisitionMode", AcquisitionModeOption.Continuous, "Acquisition Mode", category: "Processing", order: 11);
        _frameRateEnable = AddParameter("FrameRateEnable", false, "Frame Rate Enable", category: "Processing", order: 12);
        _acquisitionFrameRate = AddParameter("AcquisitionFrameRate", 30.0, "Acquisition Frame Rate", min: 1, max: 1000, category: "Processing", order: 13);
        _offsetX = AddParameter("OffsetX", 0, "Offset X", min: 0, max: 10000, category: "Processing", order: 14);
        _offsetY = AddParameter("OffsetY", 0, "Offset Y", min: 0, max: 10000, category: "Processing", order: 15);
        _reverseX = AddParameter("ReverseX", false, "Reverse X", category: "Processing", order: 16);
        _reverseY = AddParameter("ReverseY", false, "Reverse Y", category: "Processing", order: 17);
        _gevScpsPacketSize = AddParameter("GevSCPSPacketSize", 0, "Gev SCPS Packet Size", min: 0, max: 16384, category: "Processing", order: 18);
        _gevScpd = AddParameter("GevSCPD", 0, "Gev SCPD", min: 0, max: 1_000_000, category: "Processing", order: 19);

        _width = AddParameter("Width", 0, "Width", min: 0, max: 20000, category: "Output", order: 1); // 0 = giữ nguyên mặc định của camera
        _height = AddParameter("Height", 0, "Height", min: 0, max: 20000, category: "Output", order: 2);
        _pixelFormat = AddParameter("PixelFormat", PixelFormatOption.Mono8, "Pixel Format", category: "Output", order: 3);
        _fps = AddParameter("FPS", 30.0, "FPS (hint)", min: 1, max: 1000, category: "Output", order: 4);
    }

    protected override void OnExecute(IToolContext context)
    {
        if (_cam == null)
            throw new ToolExecutionException("No camera open. Use the Camera tab to scan and open a device.");

        bool hasFrame;
        lock (_frameLock) hasFrame = _latestRawBuffer != null;
        if (!hasFrame)
            throw new ToolExecutionException("Camera is open but no frame yet — click Start in the Camera tab first.");

        ApplyMvsSettings(context); // Đẩy lại toàn bộ tham số xuống camera - có thể chỉnh sống mà không cần đóng/mở lại

        Mat frameMat;
        long frameNumber;
        DateTime timestamp;
        lock (_frameLock)
        {
            frameMat = BuildMatFromRawBuffer(_latestRawBuffer!, (int)_latestWidth, (int)_latestHeight, _latestPixelType);
            frameNumber = _frameNumber;
            timestamp = _latestTimestamp;
        }

        int angle = (int)_rotateImage.Value;
        if (angle != 0)
        {
            var rotated = new Mat();
            var rotateCode = angle switch
            {
                90 => RotateFlags.Rotate90Clockwise,
                180 => RotateFlags.Rotate180,
                270 => RotateFlags.Rotate90CounterClockwise,
                _ => (RotateFlags?)null
            };
            if (rotateCode.HasValue)
            {
                Cv2.Rotate(frameMat, rotated, rotateCode.Value);
                frameMat.Dispose();
                frameMat = rotated;
            }
            else rotated.Dispose();
        }

        double actualFps = _lastElapsedMs > 0 ? 1000.0 / _lastElapsedMs : 0;

        _outImage.Value = new MatVisionImage(frameMat);
        _outCameraInfo.Value = _selectedCamera.Value;
        _outFrameNumber.Value = frameNumber;
        _outTimestamp.Value = timestamp;
        _outConnectionStatus.Value = true;
        _outActualFps.Value = actualFps;

        context.Log($"CameraCapture: frame #{frameNumber} @ {actualFps:F1} FPS from '{_selectedCamera.Value}'");
    }

    // ============================================================
    // Đẩy toàn bộ tham số GenICam xuống camera - gọi mỗi lần Execute() để có thể "chỉnh sống"
    // ============================================================
    private void ApplyMvsSettings(IToolContext context)
    {
        var cam = _cam!;

        SetEnum(cam, "TriggerMode", _triggerMode.Value.ToString());
        if (_triggerMode.Value == TriggerModeOption.On)
            SetEnum(cam, "TriggerSource", _triggerSource.Value.ToString());
        SetEnum(cam, "AcquisitionMode", _acquisitionMode.Value.ToString());

        SetBool(cam, "AcquisitionFrameRateEnable", _frameRateEnable.Value);
        if (_frameRateEnable.Value)
            SetFloat(cam, "AcquisitionFrameRate", (float)_acquisitionFrameRate.Value);

        // --- Exposure + Once-Auto convergence ---
        SetEnum(cam, "ExposureAuto", _exposureAuto.Value.ToString());
        if (_exposureAuto.Value == AutoMode.Off)
        {
            SetFloat(cam, "ExposureTime", (float)_exposureTime.Value);
            _exposureOnceHandled = false; // reset để lần sau chọn lại Once vẫn chạy được
        }
        else if (_exposureAuto.Value == AutoMode.Once && !_exposureOnceHandled)
        {
            // Đợi vài frame để camera hội tụ trước khi đọc giá trị về, theo đúng mô tả tài liệu
            System.Threading.Thread.Sleep(200);
            if (TryGetFloat(cam, "ExposureTime", out float convergedExposure))
            {
                context.Log($"CameraCapture: ExposureAuto converged at {convergedExposure} μs");
                SetEnum(cam, "ExposureAuto", "Off");
                _exposureAuto.Value = AutoMode.Off; // tự đặt lại Off trên UI, đúng hành vi "Once" mô tả trong tài liệu
                _exposureTime.Value = convergedExposure;
            }
            _exposureOnceHandled = true;
        }

        // --- Gain + Once-Auto convergence (tương tự Exposure) ---
        SetEnum(cam, "GainAuto", _gainAuto.Value.ToString());
        if (_gainAuto.Value == AutoMode.Off)
        {
            SetFloat(cam, "Gain", (float)_gain.Value);
            _gainOnceHandled = false;
        }
        else if (_gainAuto.Value == AutoMode.Once && !_gainOnceHandled)
        {
            System.Threading.Thread.Sleep(200);
            if (TryGetFloat(cam, "Gain", out float convergedGain))
            {
                SetEnum(cam, "GainAuto", "Off");
                _gainAuto.Value = AutoMode.Off;
                _gain.Value = convergedGain;
            }
            _gainOnceHandled = true;
        }

        SetFloat(cam, "Gamma", (float)_gamma.Value);
        SetFloat(cam, "BlackLevel", (float)_blackLevel.Value);
        SetEnum(cam, "BalanceWhiteAuto", _balanceWhiteAuto.Value.ToString()); // SDK tự bỏ qua nếu camera đen-trắng

        SetInt(cam, "OffsetX", _offsetX.Value);
        SetInt(cam, "OffsetY", _offsetY.Value);
        SetBool(cam, "ReverseX", _reverseX.Value);
        SetBool(cam, "ReverseY", _reverseY.Value);

        if (_width.Value > 0) SetInt(cam, "Width", _width.Value);
        if (_height.Value > 0) SetInt(cam, "Height", _height.Value);
        SetEnum(cam, "PixelFormat", _pixelFormat.Value.ToString());

        if (_gevScpsPacketSize.Value > 0) SetInt(cam, "GevSCPSPacketSize", _gevScpsPacketSize.Value);
        if (_gevScpd.Value > 0) SetInt(cam, "GevSCPD", _gevScpd.Value);
    }

    // ============================================================
    // Public API cho tab "Camera" trên UI gọi trực tiếp (Scan / Open / Start / Stop / Close)
    // ============================================================
    #region 4. Điều khiển vòng đời Camera (gọi từ tab "Camera" trên UI, KHÔNG gọi trong OnExecute)

    /// <summary>Quét danh sách camera theo ConnectionType, trả về tên hiển thị để đổ vào dropdown UI.</summary>
    public List<string> ScanCameras()
    {
        var result = new List<string>();
        uint layerType = _connectionType.Value switch
        {
            CameraConnectionType.GigEVision => MyCamera.MV_GIGE_DEVICE,
            CameraConnectionType.USB3Vision => MyCamera.MV_USB_DEVICE,
            _ => MyCamera.MV_GIGE_DEVICE | MyCamera.MV_USB_DEVICE
        };

        var deviceList = new MV_CC_DEVICE_INFO_LIST();
        int ret = MyCamera.MV_CC_EnumDevices_NET(layerType, ref deviceList);
        if (ret != MyCamera.MV_OK) return result;

        for (int i = 0; i < deviceList.nDeviceNum; i++)
        {
            var info = (MV_CC_DEVICE_INFO)Marshal.PtrToStructure(deviceList.pDeviceInfo[i], typeof(MV_CC_DEVICE_INFO))!;
            result.Add(MyCamera.MV_CC_GetDeviceNameSummary_NET(info)); // vd: "MV-CA050-12GC (00F12345678)"
        }
        return result;
    }

    /// <summary>Mở camera theo tên/serial đã chọn từ ScanCameras(). Không tự Start grabbing.</summary>
    public void OpenCamera(string nameOrSerial)
    {
        CloseCamera(); // đảm bảo không mở chồng lên camera cũ nếu đang mở

        uint layerType = _connectionType.Value switch
        {
            CameraConnectionType.GigEVision => MyCamera.MV_GIGE_DEVICE,
            CameraConnectionType.USB3Vision => MyCamera.MV_USB_DEVICE,
            _ => MyCamera.MV_GIGE_DEVICE | MyCamera.MV_USB_DEVICE
        };

        var deviceList = new MV_CC_DEVICE_INFO_LIST();
        if (MyCamera.MV_CC_EnumDevices_NET(layerType, ref deviceList) != MyCamera.MV_OK)
            throw new ToolExecutionException("CameraCapture: enumerate devices failed.");

        MV_CC_DEVICE_INFO? matched = null;
        for (int i = 0; i < deviceList.nDeviceNum; i++)
        {
            var info = (MV_CC_DEVICE_INFO)Marshal.PtrToStructure(deviceList.pDeviceInfo[i], typeof(MV_CC_DEVICE_INFO))!;
            if (MyCamera.MV_CC_GetDeviceNameSummary_NET(info).Contains(nameOrSerial, StringComparison.OrdinalIgnoreCase))
            {
                matched = info;
                break;
            }
        }
        if (matched == null)
            throw new ToolExecutionException($"CameraCapture: device '{nameOrSerial}' not found. Scan again.");

        var cam = new MyCamera();
        var info2 = matched.Value;
        if (cam.MV_CC_CreateHandle_NET(ref info2) != MyCamera.MV_OK)
            throw new ToolExecutionException("CameraCapture: create handle failed.");
        if (cam.MV_CC_OpenDevice_NET() != MyCamera.MV_OK)
            throw new ToolExecutionException($"CameraCapture: cannot open device '{nameOrSerial}'.");

        _cam = cam;
        _selectedCamera.Value = nameOrSerial;
        lock (_frameLock) { _latestRawBuffer = null; }
        _frameNumber = 0;
    }

    /// <summary>Bắt đầu bắn frame - phải gọi trước khi Run Node, đúng theo hành vi camera thật.</summary>
    public void StartGrabbing()
    {
        if (_cam == null) throw new ToolExecutionException("No camera open. Use the Camera tab to scan and open a device.");
        if (_isGrabbing) return;

        // Đăng ký callback nhận frame realtime thay vì poll thủ công - đúng cách dùng khuyến nghị của SDK
        _cam.MV_CC_RegisterImageCallBackEx_NET(OnMvsServiceFrame, IntPtr.Zero);
        if (_cam.MV_CC_StartGrabbing_NET() != MyCamera.MV_OK)
            throw new ToolExecutionException("CameraCapture: start grabbing failed.");
        _isGrabbing = true;
        _fpsWatch.Restart();
    }

    public void StopGrabbing()
    {
        if (_cam != null && _isGrabbing)
            _cam.MV_CC_StopGrabbing_NET();
        _isGrabbing = false;
    }

    public void CloseCamera()
    {
        StopGrabbing();
        if (_cam != null)
        {
            _cam.MV_CC_CloseDevice_NET();
            _cam.MV_CC_DestroyHandle_NET();
            _cam = null;
        }
        lock (_frameLock) { _latestRawBuffer = null; }
    }

    /// <summary>Callback SDK gọi mỗi khi có frame mới - chỉ copy dữ liệu ra rồi trả quyền điều khiển ngay, không xử lý nặng ở đây.</summary>
    private void OnMvsServiceFrame(IntPtr pData, ref MV_FRAME_OUT_INFO_EX frameInfo, IntPtr pUser)
    {
        int size = frameInfo.nWidth * frameInfo.nHeight * 3; // cấp dư bộ đệm, đủ cho cả ảnh màu lẫn ảnh xám
        var buffer = new byte[size];
        Marshal.Copy(pData, buffer, 0, Math.Min(size, frameInfo.nFrameLen));

        lock (_frameLock)
        {
            _latestRawBuffer = buffer;
            _latestWidth = (uint)frameInfo.nWidth;
            _latestHeight = (uint)frameInfo.nHeight;
            _latestPixelType = frameInfo.enPixelType;
            _latestTimestamp = DateTime.Now; // giờ máy tính, không phải giờ camera - đúng ghi chú trong tài liệu
        }
        _frameNumber++; // tăng nội bộ, KHÔNG phải số frame của camera - đúng ghi chú trong tài liệu
        _lastElapsedMs = _fpsWatch.Elapsed.TotalMilliseconds;
        _fpsWatch.Restart();
    }

    #endregion

    // ============================================================
    // Chuyển buffer thô (Mono/Bayer/RGB) do SDK trả về thành OpenCvSharp.Mat để đưa vào pipeline
    // ============================================================
    private static Mat BuildMatFromRawBuffer(byte[] buffer, int width, int height, MvGvspPixelType pixelType)
    {
        // Mono8: copy thẳng 1 kênh. Các định dạng màu/Bayer khác: SDK MVS có MV_CC_ConvertPixelType_NET
        // để chuyển sang BGR8 - nếu dự án cần hỗ trợ đầy đủ Bayer/Mono10-16, gọi hàm đó trước khi tới đây.
        if (pixelType == MvGvspPixelType.PixelType_Gvsp_Mono8)
        {
            var mat = new Mat(height, width, MatType.CV_8UC1);
            Marshal.Copy(buffer, 0, mat.Data, width * height);
            return mat;
        }

        // Mặc định coi như buffer đã ở dạng BGR8 liên tục (3 kênh) - khớp với PixelFormat=BGR8 đã set ở ApplyMvsSettings
        var colorMat = new Mat(height, width, MatType.CV_8UC3);
        Marshal.Copy(buffer, 0, colorMat.Data, width * height * 3);
        return colorMat;
    }

    #region 5. Hikrobot MVS SDK Interop helpers (bọc lại các hàm MV_CC_Set*_NET cho gọn)
    private static void SetEnum(MyCamera cam, string node, string value) => cam.MV_CC_SetEnumValueByString_NET(node, value);
    private static void SetFloat(MyCamera cam, string node, float value) => cam.MV_CC_SetFloatValue_NET(node, value);
    private static void SetInt(MyCamera cam, string node, int value) => cam.MV_CC_SetIntValue_NET(node, (uint)value);
    private static void SetBool(MyCamera cam, string node, bool value) => cam.MV_CC_SetBoolValue_NET(node, value);

    private static bool TryGetFloat(MyCamera cam, string node, out float value)
    {
        var v = new MVCC_FLOATVALUE();
        int ret = cam.MV_CC_GetFloatValue_NET(node, ref v);
        value = v.fCurValue;
        return ret == MyCamera.MV_OK;
    }
    #endregion

    /// <summary>FlowPageViewModel phải gọi Dispose() khi xóa node hoặc đóng project để giải phóng SDK đúng cách (không dùng Finalizer).</summary>
    public void Dispose() => CloseCamera();
}
*/