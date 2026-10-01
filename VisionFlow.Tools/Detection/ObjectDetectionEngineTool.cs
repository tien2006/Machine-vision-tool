// ==================== Vai trò chính:                Nhận diện đối tượng bằng AI (YOLOv11 qua YoloSharp) - trả về vị trí, tên loại, độ tin cậy từng đối tượng
// ==================== Thành phần / Class tiêu biểu: ObjectDetectionEngineTool
// ==================== Phụ thuộc vào:                YoloSharp (NuGet) + OpenCvSharp (VideoCapture cho nguồn Video) + Core.Models (YoloDetectionResult, VideoSourceRef) + Core.Ports + Core.Tools
// ==================== Pattern / Kỹ thuật nổi bật:   Adapter Pattern cô lập lời gọi thư viện ngoài (RunYoloInference) + Auto Input Source Resolution (Image/Video) + Palette 8 màu cố định

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Compunet.YoloSharp;
using OpenCvSharp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Models;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;

namespace VisionFlow.Tools.Detection;

/// <summary>
/// Nhận diện đối tượng bằng mô hình AI YOLOv11 (qua thư viện YoloSharp) - khác hoàn toàn các Tool Detection khác
/// trong nhóm (Blob/Caliper/Template Matching...) vốn dựa 100% vào xử lý ảnh cổ điển: ObjectDetectionEngine
/// dùng mạng neural đã huấn luyện sẵn (bộ dữ liệu COCO, 80 lớp: person, car, bottle...) để nhận diện.
///
/// QUAN TRỌNG - PHỤ THUỘC THƯ VIỆN NGOÀI:
/// Lời gọi thực tế vào thư viện YoloSharp được cô lập trong hàm <see cref="RunYoloInference"/> ở cuối file.
/// API chính xác (tên class, tên hàm, tham số) có thể khác nhau giữa các phiên bản YoloSharp - nếu build lỗi
/// ngay tại hàm đó, CHỈ CẦN SỬA ĐÚNG BÊN TRONG HÀM ĐÓ theo đúng API bản bạn đang cài, phần còn lại của Tool
/// (Ports, Parameters, luồng xử lý, vẽ overlay...) không cần đổi gì.
///
/// Quy trình xử lý:
/// 1. Xác định nguồn ảnh đang hoạt động (ActiveInputType) theo InputSource: Auto (ưu tiên Video > Image), Image, hoặc Video.
/// 2. Nếu nguồn là Video: mở file bằng Cv2.VideoCapture, seek tới đúng VideoFrameIndex rồi đọc 1 khung hình.
/// 3. Chạy YOLO inference qua RunYoloInference() -> danh sách YoloDetection thô.
/// 4. Lọc theo SelectedClasses (nếu có chỉ định), giới hạn MaxDetections.
/// 5. Vẽ overlay (khung + nhãn + confidence, mỗi đối tượng 1 màu theo index trong bảng 8 màu cố định).
/// 6. Xuất toàn bộ kết quả ra các cổng Output.
/// </summary>
[ToolMetadata("ObjectDetectionEngine", DisplayName = "Object Detection Engine (YOLO)", Category = "Detection",
    Description = "AI object detection using YOLOv11 (via YoloSharp). Supports static image, realtime camera, and video file input.")]
public sealed class ObjectDetectionEngineTool : VisionTool, IDisposable
{
    // ----- Cache YoloPredictor giữa các lần Execute liên tiếp: load model ONNX rất chậm (vài trăm ms tới vài giây),
    // nếu tạo mới YoloPredictor mỗi lần Execute sẽ làm pipeline chạy realtime cực kỳ chậm. Chỉ tạo lại khi ModelName
    // hoặc UseGPU thay đổi.
    private YoloPredictor? _cachedPredictor;
    private string? _cachedModelPath;
    private bool _cachedUseGpu;
    /// <summary>Bảng 8 màu cố định gán cho từng đối tượng theo thứ tự index, đúng theo mô tả trong tài liệu mục tiêu.</summary>
    private static readonly Scalar[] Palette =
    {
        new Scalar(0, 255, 0),     // Xanh lá
        new Scalar(0, 255, 255),   // Vàng
        new Scalar(255, 255, 0),   // Cyan
        new Scalar(0, 165, 255),   // Cam
        new Scalar(255, 0, 255),   // Magenta
        new Scalar(255, 200, 100), // Xanh nhạt
        new Scalar(255, 255, 255), // Trắng
        new Scalar(180, 105, 255), // Hồng
    };

    #region 1. Ports
    private readonly InputPort<IVisionImage> _input;        // ImageMatrix: dùng cho cả ảnh tĩnh (ImageLoader) và camera realtime (CameraCapture)
    private readonly InputPort<VideoSourceRef> _videoInput;  // VideoCapture: dùng cho video file (VideoLoader)

    private readonly OutputPort<IVisionImage> _outImage;                 // Ảnh gốc đã clone ra - KHÔNG vẽ đè
    private readonly OutputPort<IVisionImage> _outVisualization;         // Lớp kết quả vẽ (khung + nhãn) - đưa vào OverlayRenderer
    private readonly OutputPort<YoloDetectionResult> _outResult;         // YOLODetectionResult tổng hợp
    private readonly OutputPort<int> _outCount;                          // DetectionCount
    private readonly OutputPort<string> _outSummary;                    // DetectionSummary (VD: "3 person, 1 car")
    private readonly OutputPort<RectRegion[]> _outBoundingBoxes;         // BoundingBoxes
    private readonly OutputPort<string[]> _outLabels;                   // DetectionLabels
    private readonly OutputPort<double[]> _outConfidences;              // Confidences
    private readonly OutputPort<double> _outProcessingTime;             // ProcessingTime (ms)
    private readonly OutputPort<string> _outActiveInputType;            // ActiveInputType: "Image" / "Video" / "None"
    #endregion

    #region 2. Parameters
    // --- Tab Source ---
    private readonly ToolParameter<string> _inputSource;      // "Auto" | "Image" | "Video"
    private readonly ToolParameter<int> _videoFrameIndex;     // Số thứ tự khung hình cần đọc khi nguồn là Video

    // --- Tab Detection ---
    private readonly ToolParameter<string> _modelName;             // Tên/đường dẫn file .onnx (VD: "yolov11n.onnx")
    private readonly ToolParameter<double> _confidenceThreshold;   // Ngưỡng tin cậy tối thiểu để giữ lại kết quả
    private readonly ToolParameter<double> _iouThreshold;          // Ngưỡng IoU cho NMS loại khung trùng lặp
    private readonly ToolParameter<int> _maxDetections;            // Số lượng kết quả tối đa mỗi khung hình
    private readonly ToolParameter<string> _selectedClasses;       // Danh sách lớp được phép, phân cách bởi dấu phẩy; rỗng = tất cả 80 lớp COCO

    // --- Tab Display ---
    private readonly ToolParameter<bool> _drawBoundingBoxes;
    private readonly ToolParameter<bool> _drawLabels;
    private readonly ToolParameter<bool> _drawConfidence;
    private readonly ToolParameter<int> _boxThickness;

    // --- Tab Advanced ---
    private readonly ToolParameter<bool> _useGpu; // Bật CUDA nếu máy có GPU NVIDIA phù hợp - sai cấu hình sẽ lỗi load model, khi đó chuyển về false
    #endregion

    public ObjectDetectionEngineTool()
    {
        _input = AddInput<IVisionImage>("Image", "Image", optional: true);
        _videoInput = AddInput<VideoSourceRef>("VideoCapture", "Video Capture", optional: true);

        _outImage = AddOutput<IVisionImage>("Image", "Image (Clean)");
        _outVisualization = AddOutput<IVisionImage>("InteractiveVisualization", "Interactive Visualization");
        _outResult = AddOutput<YoloDetectionResult>("DetectionResult", "YOLODetectionResult");
        _outCount = AddOutput<int>("DetectionCount", "Detection Count");
        _outSummary = AddOutput<string>("DetectionSummary", "Detection Summary");
        _outBoundingBoxes = AddOutput<RectRegion[]>("BoundingBoxes", "Bounding Boxes");
        _outLabels = AddOutput<string[]>("DetectionLabels", "Detection Labels");
        _outConfidences = AddOutput<double[]>("Confidences", "Confidences");
        _outProcessingTime = AddOutput<double>("ProcessingTime", "Processing Time (ms)");
        _outActiveInputType = AddOutput<string>("ActiveInputType", "Active Input Type");

        _inputSource = AddChoiceParameter("InputSource", "Auto", new[] { "Auto", "Image", "Video" }, "Input Source", category: "Source", order: 1);
        _videoFrameIndex = AddParameter<int>("VideoFrameIndex", 0, "Video Frame Index", min: 0, max: 1_000_000, category: "Source", order: 2);

        _modelName = AddParameter<string>("ModelPath", "yolo11n.onnx", "Model Path", category: "Detection", order: 1);
        _confidenceThreshold = AddParameter<double>("ConfidenceThreshold", 0.5, "Confidence Threshold", min: 0.0, max: 1.0, category: "Detection", order: 2);
        _iouThreshold = AddParameter<double>("IoUThreshold", 0.45, "IoU Threshold", min: 0.0, max: 1.0, category: "Detection", order: 3);
        _maxDetections = AddParameter<int>("MaxDetections", 300, "Max Detections", min: 1, max: 5000, category: "Detection", order: 4);
        _selectedClasses = AddParameter<string>("SelectedClasses", "", "Selected Classes (comma-separated, empty = all)", category: "Detection", order: 5);

        _drawBoundingBoxes = AddParameter<bool>("DrawBoundingBoxes", true, "Draw Bounding Boxes", category: "Display", order: 1);
        _drawLabels = AddParameter<bool>("DrawLabels", true, "Draw Labels", category: "Display", order: 2);
        _drawConfidence = AddParameter<bool>("DrawConfidence", true, "Draw Confidence", category: "Display", order: 3);
        _boxThickness = AddParameter<int>("BoxThickness", 2, "Box Thickness", min: 1, max: 20, category: "Display", order: 4);

        _useGpu = AddParameter<bool>("UseGPU", false, "Use GPU (CUDA)", category: "Advanced", order: 1);
    }

    protected override void OnExecute(IToolContext context)
    {
        var stopwatch = Stopwatch.StartNew();

        // ----- Bước 1: Xác định nguồn ảnh đang hoạt động (ActiveInputType) -----
        bool hasImage = _input.Value != null;
        bool hasVideo = !string.IsNullOrWhiteSpace(_videoInput.Value.FilePath);

        string activeType = _inputSource.Value switch
        {
            "Image" => hasImage ? "Image" : "None",
            "Video" => hasVideo ? "Video" : "None",
            _ => hasVideo ? "Video" : (hasImage ? "Image" : "None"), // Auto: ưu tiên Video > Image, đúng theo tài liệu
        };

        Mat? frame = null;
        bool frameOwned = false; // true nếu frame do chính Tool này tạo ra (đọc từ video) -> cần Dispose sau khi dùng xong

        if (activeType == "Image")
        {
            frame = _input.Value!.AsMat();
        }
        else if (activeType == "Video")
        {
            frame = ReadVideoFrame(_videoInput.Value, _videoFrameIndex.Value);
            frameOwned = frame != null;
        }

        if (frame == null)
        {
            // Không có nguồn ảnh nào hợp lệ -> trả về kết quả rỗng thay vì crash, đúng tinh thần "None" của ActiveInputType
            stopwatch.Stop();
            var emptyResult = new YoloDetectionResult { Detections = Array.Empty<YoloDetection>(), Success = false, Judge = Judge.NG, ExecutionTimeMs = stopwatch.Elapsed.TotalMilliseconds };
            _outResult.Value = emptyResult;
            _outCount.Value = 0;
            _outSummary.Value = "No input source.";
            _outBoundingBoxes.Value = Array.Empty<RectRegion>();
            _outLabels.Value = Array.Empty<string>();
            _outConfidences.Value = Array.Empty<double>();
            _outProcessingTime.Value = stopwatch.Elapsed.TotalMilliseconds;
            _outActiveInputType.Value = "None";
            context.Log("ObjectDetectionEngine: không có ảnh/video hợp lệ để xử lý.");
            return;
        }

        // ----- Bước 2: Resolve đường dẫn model .onnx -----
        string modelPath = ResolveModelPath(_modelName.Value);

        // ----- Bước 3: Chạy YOLO inference (xem chi tiết + lưu ý API trong RunYoloInference) -----
        List<YoloDetection> raw = RunYoloInference(frame, modelPath, _confidenceThreshold.Value, _iouThreshold.Value, _useGpu.Value);

        // ----- Bước 4: Lọc theo SelectedClasses (nếu có) + giới hạn MaxDetections -----
        HashSet<string>? allowedClasses = null;
        if (!string.IsNullOrWhiteSpace(_selectedClasses.Value))
        {
            allowedClasses = _selectedClasses.Value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => s.ToLowerInvariant())
                .ToHashSet();
        }

        var filtered = raw
            .Where(d => allowedClasses == null || allowedClasses.Contains(d.Label.ToLowerInvariant()))
            .OrderByDescending(d => d.Confidence)
            .Take(Math.Max(1, _maxDetections.Value))
            .ToList();

        // ----- Bước 5: Vẽ overlay -----
        Mat cleanImage = frame.Clone(); // ImageMatrix output: ảnh gốc đã clone ra, KHÔNG vẽ đè
        Mat visualization = frame.Clone(); // InteractiveVisualization: lớp có vẽ khung/nhãn, dùng riêng cho OverlayRenderer

        for (int i = 0; i < filtered.Count; i++)
        {
            var d = filtered[i];
            Scalar color = Palette[i % Palette.Length];
            var rect = new Rect((int)d.BoundingBox.X, (int)d.BoundingBox.Y, (int)d.BoundingBox.Width, (int)d.BoundingBox.Height);

            if (_drawBoundingBoxes.Value)
                Cv2.Rectangle(visualization, rect, color, _boxThickness.Value, LineTypes.AntiAlias);

            if (_drawLabels.Value || _drawConfidence.Value)
            {
                string text = _drawLabels.Value && _drawConfidence.Value ? $"{d.Label} {d.Confidence * 100:F0}%"
                    : _drawLabels.Value ? d.Label
                    : $"{d.Confidence * 100:F0}%";
                Cv2.PutText(visualization, text, new OpenCvSharp.Point(rect.X, Math.Max(12, rect.Y - 6)),
                    HersheyFonts.HersheySimplex, 0.5, color, 1, LineTypes.AntiAlias);
            }
        }

        stopwatch.Stop();

        // ----- Bước 6: Đóng gói kết quả -----
        var summary = filtered
            .GroupBy(d => d.Label)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Count()} {g.Key}");
        string summaryText = filtered.Count > 0 ? string.Join(", ", summary) : "No detections.";

        var result = new YoloDetectionResult
        {
            Detections = filtered,
            Success = true,
            Judge = filtered.Count > 0 ? Judge.OK : Judge.NG,
            ExecutionTimeMs = stopwatch.Elapsed.TotalMilliseconds,
        };

        // ----- Bước 7: Xuất kết quả ra các cổng Output -----
        // cleanImage/visualization đều "cho đi" thẳng vào Output -> KHÔNG Dispose(...) chúng sau đây
        _outImage.Value = new MatVisionImage(cleanImage);
        _outVisualization.Value = new MatVisionImage(visualization);
        _outResult.Value = result;
        _outCount.Value = filtered.Count;
        _outSummary.Value = summaryText;
        _outBoundingBoxes.Value = filtered.Select(d => d.BoundingBox).ToArray();
        _outLabels.Value = filtered.Select(d => d.Label).ToArray();
        _outConfidences.Value = filtered.Select(d => d.Confidence).ToArray();
        _outProcessingTime.Value = stopwatch.Elapsed.TotalMilliseconds;
        _outActiveInputType.Value = activeType;

        // ----- Bước 8: Dọn dẹp tài nguyên -----
        if (frameOwned) frame.Dispose(); // Chỉ Dispose frame khi chính Tool này đọc ra từ video (không đụng tới ảnh do Input port sở hữu)

        context.Log($"ObjectDetectionEngine: {filtered.Count} detection(s) [{activeType}] - {summaryText} - {stopwatch.Elapsed.TotalMilliseconds:F1}ms.");
    }

    #region 3. Helpers

    /// <summary>
    /// Mở file video, seek tới đúng VideoFrameIndex (tự kẹp trong khoảng hợp lệ) rồi đọc 1 khung hình.
    /// LƯU Ý: mở lại VideoCapture mỗi lần Execute là đơn giản/an toàn nhất khi chưa có VideoLoaderTool chính thức
    /// giữ sẵn handle video xuyên suốt pipeline; với video lớn/chạy realtime nhiều lần, nên thay bằng 1 VideoLoaderTool
    /// riêng giữ VideoCapture mở sẵn để tăng tốc.
    /// </summary>
    private static Mat? ReadVideoFrame(VideoSourceRef videoRef, int frameIndex)
    {
        if (string.IsNullOrWhiteSpace(videoRef.FilePath) || !File.Exists(videoRef.FilePath))
            return null;

        using var capture = new VideoCapture(videoRef.FilePath);
        if (!capture.IsOpened()) return null;

        int totalFrames = (int)capture.Get(VideoCaptureProperties.FrameCount);
        int safeIndex = totalFrames > 0 ? Math.Clamp(frameIndex, 0, totalFrames - 1) : Math.Max(0, frameIndex); // Tự giới hạn không vượt quá tổng số frame, đúng theo tài liệu

        capture.Set(VideoCaptureProperties.PosFrames, safeIndex);
        var frame = new Mat();
        bool ok = capture.Read(frame);
        if (!ok || frame.Empty()) { frame.Dispose(); return null; }
        return frame;
    }

    /// <summary>Suy ra đường dẫn đầy đủ tới file .onnx từ ModelName: chấp nhận đường dẫn tuyệt đối, tên file trong thư mục Models/, hoặc tên thiếu đuôi .onnx.</summary>
    private static string ResolveModelPath(string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName)) return modelName;
        if (File.Exists(modelName)) return modelName;

        string withExt = modelName.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase) ? modelName : modelName + ".onnx";
        string underModels = Path.Combine("Models", withExt);
        return File.Exists(underModels) ? underModels : withExt;
    }

    /// <summary>
    /// Gọi thư viện YoloSharp (Compunet.YoloSharp) để chạy inference trên 1 khung hình.
    /// Tự động tạo lại YoloPredictor khi ModelName/UseGPU thay đổi so với lần chạy trước, còn lại thì tái sử dụng
    /// (_cachedPredictor) để tránh load lại model ONNX mỗi lần Execute - rất tốn thời gian nếu không cache.
    /// LƯU Ý: tên thuộc tính của "result" (Boxes/Name/Confidence/Bounds) lấy theo cấu trúc phổ biến của
    /// Compunet.YoloSharp 6.x - nếu bản bạn cài lệch tên, gõ "result." trong IntelliSense để xem đúng rồi sửa lại
    /// đúng bên trong vòng foreach bên dưới, phần còn lại của Tool không cần đổi.
    /// </summary>
    private List<YoloDetection> RunYoloInference(Mat frame, string modelPath, double confThreshold, double iouThreshold, bool useGpu)
    {
        var detections = new List<YoloDetection>();
        if (!File.Exists(modelPath))
            throw new ToolExecutionException($"ObjectDetectionEngine: không tìm thấy model '{modelPath}'. Đặt file .onnx vào thư mục Models/ hoặc chỉnh lại tham số ModelName.");

        // ----- Tạo lại YoloPredictor CHỈ KHI cần thiết (đổi model hoặc đổi chế độ GPU) -----
        bool needNewPredictor = _cachedPredictor == null || _cachedModelPath != modelPath || _cachedUseGpu != useGpu;
        if (needNewPredictor)
        {
            _cachedPredictor?.Dispose();
            _cachedPredictor = new YoloPredictor(modelPath); // Nếu cài package YoloSharp.Gpu thay vì YoloSharp, GPU sẽ được dùng tự động khi có sẵn CUDA - useGpu ở bản CPU-only không có tác dụng, giữ tham số để tương thích khi bạn nâng cấp sang YoloSharp.Gpu
            _cachedModelPath = modelPath;
            _cachedUseGpu = useGpu;
        }

        // ----- Encode Mat (OpenCvSharp) -> byte[] PNG -> Image (SixLabors.ImageSharp) -----
        // YoloSharp đọc ảnh qua SixLabors.ImageSharp chứ không nhận thẳng OpenCvSharp.Mat, nên cần bước cầu nối này.
        Cv2.ImEncode(".png", frame, out byte[] pngBytes);
        using var image = Image.Load<Rgba32>(pngBytes);

        // ----- Chạy inference -----
        var result = _cachedPredictor!.Detect(image);

        // ----- Map kết quả YoloSharp -> YoloDetection (kiểm tra lại tên field qua IntelliSense nếu build lỗi ở đây) -----
        // Đã xác nhận qua Object Browser: YoloResult<Detection> tự implement IEnumerable<Detection> (không có .Boxes),
        // Detection kế thừa YoloPrediction (Name: YoloName, Confidence: float) + tự có Bounds: Rectangle,
        // YoloName có 2 field Id (int) và Name (string).
        foreach (var box in result)
        {
            string label = box.Name.Name;
            int classId = box.Name.Id;
            double confidence = box.Confidence;

            if (confidence < confThreshold) continue; // Lọc lại theo ConfidenceThreshold (đề phòng YoloSharp không tự lọc theo ngưỡng truyền vào)

            detections.Add(new YoloDetection(label, classId, confidence,
                new RectRegion(box.Bounds.X, box.Bounds.Y, box.Bounds.Width, box.Bounds.Height)));
        }

        return detections;
    }

    /// <summary>Giải phóng YoloPredictor đã cache khi Tool bị huỷ (nếu Engine của bạn gọi Dispose() trên Tool; nếu không, predictor sẽ tự giải phóng khi ứng dụng thoát).</summary>
    public void Dispose()
    {
        _cachedPredictor?.Dispose();
        _cachedPredictor = null;
    }

    #endregion
}