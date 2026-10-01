// ==================== Vai trò chính:                Node nguồn — đọc khung hình từ file video
// ==================== Thành phần / Class tiêu biểu: VideoLoaderTool
// ==================== Phụ thuộc vào:                OpenCvSharp (VideoCapture), Core.Models (VideoSourceRef, VideoInfo)
// ==================== Pattern / Kỹ thuật nổi bật:   Mở/đóng VideoCapture trong phạm vi 1 lần đọc (giống
//                       ObjectDetectionEngineTool.ReadVideoFrame) -> không giữ handle sống giữa các lần Execute.
//                       4 cổng ra tách vai trò rõ ràng theo đúng tài liệu thuật toán:
//                       VideoPath (log/đặt tên file) | VideoCapture ("trái tim" - cho downstream tự seek frame)
//                       | PreviewFrame (xem trước nhẹ, phục vụ setup/căn ROI) | VideoInfo (metadata kỹ thuật)

using System;
using System.IO;
using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Models; // VideoSourceRef, VideoInfo
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;

namespace VisionFlow.Tools.Acquisition;

/// <summary>
/// Node nguồn đọc video file. Theo đúng tài liệu thuật toán, node có 4 cổng ra với vai trò tách biệt:
/// <list type="bullet">
/// <item><b>VideoPath</b>: đường dẫn file đang tải - dùng để ghi log hoặc đặt tên file kết quả.</item>
/// <item><b>VideoCapture</b>: "trái tim" của node - tham chiếu tới video (<see cref="VideoSourceRef"/>) để
/// các Tool downstream (VD ObjectDetectionEngineTool) tự seek đúng frame chúng cần, không phải mọi frame
/// đều phải chảy qua node này trước.</item>
/// <item><b>PreviewFrame</b>: MỘT khung hình được chọn để xem trước/căn ROI khi setup, tách biệt khỏi luồng
/// xử lý chính qua VideoCapture - không cần chạy toàn bộ pipeline nặng để xem video có lỗi hay không.</item>
/// <item><b>VideoInfo</b>: metadata kỹ thuật (độ phân giải, tổng số frame, FPS, thời lượng) - cần cho các phép
/// đo phụ thuộc thời gian, VD tính vận tốc vật thể dựa trên FPS.</item>
/// </list>
/// Tab "Playback" (AutoAdvance/FrameIndex/FrameStep/Loop) là tính năng BỔ SUNG không có trong tài liệu gốc,
/// dùng để quyết định PreviewFrame lấy từ frame nào mỗi lần Execute() - giữ lại vì hữu ích khi test/soi video.
/// </summary>
[ToolMetadata("VideoLoader", DisplayName = "Video Loader", Category = "InputSource",
    Description = "Read a video file: expose it as a seekable VideoCapture stream, a decoded PreviewFrame, and VideoInfo metadata.")]
public sealed class VideoLoaderTool : VisionTool
{
    // --- Tab "VideoLoader" (đúng tài liệu): chỉ duy nhất tham số FilePath ---
    private readonly ToolParameter<string> _filePath;

    // --- Tab "Playback" (TÍNH NĂNG BỔ SUNG - không có trong tài liệu, giữ nguyên vì hữu ích) ---
    private readonly ToolParameter<bool> _autoAdvance; // true: tự tăng frame mỗi lần Execute | false: đứng yên tại FrameIndex nhập tay
    private readonly ToolParameter<int> _frameIndex;   // Chỉ dùng khi AutoAdvance = false -> kéo slider để soi từng frame cụ thể cho PreviewFrame
    private readonly ToolParameter<int> _frameStep;    // Số frame nhảy qua mỗi lần advance
    private readonly ToolParameter<bool> _loop;        // Hết video: true = quay lại frame 0 | false = đứng yên ở frame cuối

    // --- Outputs: đúng 4 cổng theo tài liệu thuật toán, đúng thứ tự hiển thị trên node ---
    private readonly OutputPort<string> _videoPathOutput;             // VideoPath
    private readonly OutputPort<VideoSourceRef> _videoCaptureOutput;  // VideoCapture
    private readonly OutputPort<IVisionImage> _previewFrameOutput;    // PreviewFrame
    private readonly OutputPort<VideoInfo> _videoInfoOutput;          // VideoInfo

    // Trạng thái nội bộ: vị trí frame kế tiếp khi AutoAdvance = true (giống _cursor của GrabImageTool)
    private int _cursorFrame;
    private string _cachedPath = string.Empty; // Phát hiện khi người dùng đổi sang video khác -> reset về frame 0

    public VideoLoaderTool()
    {
        _filePath = AddParameter("FilePath", string.Empty, "File Path", category: "VideoLoader", order: 1);

        _autoAdvance = AddParameter("AutoAdvance", true, "Auto Advance", category: "Playback", order: 1);
        _frameIndex = AddParameter("FrameIndex", 0, "Frame Index", min: 0, max: 10_000_000, category: "Playback", order: 2);
        _frameStep = AddParameter("FrameStep", 1, "Frame Step", min: 1, max: 10_000, category: "Playback", order: 3);
        _loop = AddParameter("Loop", true, "Loop", category: "Playback", order: 4);

        _videoPathOutput = AddOutput<string>("VideoPath", "Video Path");
        _videoCaptureOutput = AddOutput<VideoSourceRef>("VideoCapture", "Video Capture");
        _previewFrameOutput = AddOutput<IVisionImage>("PreviewFrame", "Preview Frame");
        _videoInfoOutput = AddOutput<VideoInfo>("VideoInfo", "Video Info");
    }

    protected override void OnExecute(IToolContext context)
    {
        string path = _filePath.Value;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new ToolExecutionException($"Video file does not exist: '{path}'");

        // Đổi sang video khác so với lần chạy trước -> reset con trỏ tuần tự về đầu video
        if (!string.Equals(_cachedPath, path, StringComparison.OrdinalIgnoreCase))
        {
            _cachedPath = path;
            _cursorFrame = 0;
        }

        // Mở VideoCapture trong phạm vi using: đọc xong đóng ngay, không giữ handle giữa các lần Execute
        // (đúng convention đã dùng ở ObjectDetectionEngineTool.ReadVideoFrame để tránh trạng thái bị kẹt).
        using var capture = new VideoCapture(path);
        if (!capture.IsOpened())
            throw new ToolExecutionException($"Cannot open video file: '{path}'");

        int totalFrames = (int)capture.Get(VideoCaptureProperties.FrameCount);
        if (totalFrames <= 0)
            throw new ToolExecutionException($"Video has no readable frames: '{path}'");

        double fps = capture.Get(VideoCaptureProperties.Fps);
        int width = (int)capture.Get(VideoCaptureProperties.FrameWidth);
        int height = (int)capture.Get(VideoCaptureProperties.FrameHeight);
        TimeSpan duration = fps > 0 ? TimeSpan.FromSeconds(totalFrames / fps) : TimeSpan.Zero;

        // ----- Chọn frame cho PreviewFrame theo cấu hình Tab Playback -----
        int index;
        if (_autoAdvance.Value)
        {
            if (_cursorFrame >= totalFrames) _cursorFrame = 0; // Phòng trường hợp video mới ít frame hơn vị trí đang lưu

            index = _cursorFrame;
            int next = _cursorFrame + Math.Max(1, _frameStep.Value);
            next = next >= totalFrames
                ? (_loop.Value ? 0 : totalFrames - 1) // Loop=true: quay lại đầu | Loop=false: đứng yên ở frame cuối
                : next;
            _cursorFrame = next;
        }
        else
        {
            // Chế độ thủ công: dùng đúng FrameIndex người dùng nhập, tự kẹp trong khoảng hợp lệ
            index = Math.Clamp(_frameIndex.Value, 0, totalFrames - 1);
        }

        capture.Set(VideoCaptureProperties.PosFrames, index);
        var frame = new Mat();
        bool ok = capture.Read(frame);
        if (!ok || frame.Empty())
        {
            frame.Dispose();
            throw new ToolExecutionException($"Failed to read frame {index} from video: '{path}'");
        }

        // ----- Xuất đúng 4 cổng theo tài liệu thuật toán -----
        _videoPathOutput.Value = path;
        _videoCaptureOutput.Value = new VideoSourceRef(path, totalFrames); // "Trái tim" của node - downstream tự Set(PosFrames,...) theo nhu cầu riêng
        _previewFrameOutput.Value = new MatVisionImage(frame);            // Ảnh xem trước nhẹ, tách biệt khỏi luồng VideoCapture chính
        _videoInfoOutput.Value = new VideoInfo(width, height, totalFrames, fps, duration);

        context.Log($"VideoLoader: preview frame {index}/{totalFrames - 1} ({width}x{height} @ {fps:F1}fps, {duration:mm\\:ss}) from '{path}'");
    }
}