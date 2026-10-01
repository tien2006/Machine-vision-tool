// ==================== Vai trò chính:                Định nghĩa kiểu dữ liệu kết quả cho ObjectDetectionEngineTool (YOLO)
// ==================== Thành phần / Class tiêu biểu: YoloDetection, YoloDetectionResult, VideoSourceRef
// ==================== Phụ thuộc vào:                Core.Models (RectRegion) - KHÔNG phụ thuộc OpenCvSharp để giữ tầng Core sạch
// ==================== Pattern / Kỹ thuật nổi bật:   record struct (value type) + POCO/DTO, đúng theo YOLODetectionResult trong tài liệu mục tiêu

using System;
using System.Collections.Generic;

namespace VisionFlow.Core.Models;

/// <summary>Mô tả MỘT đối tượng do YOLO phát hiện được trong 1 khung hình.</summary>
public readonly record struct YoloDetection(
    string Label,           // Tên lớp đối tượng (VD: "person", "car", "bottle"...)
    int ClassId,            // Chỉ số lớp trong bộ dữ liệu (0-79 với COCO)
    double Confidence,      // Độ tin cậy (0.0 - 1.0)
    RectRegion BoundingBox  // Khung bao quanh đối tượng, tính bằng pixel trên ảnh gốc
);

/// <summary>Kết quả tổng hợp trả về từ ObjectDetectionEngineTool sau khi chạy YOLO trên 1 khung hình.</summary>
public sealed class YoloDetectionResult : VisionResult
{
    /// <summary>Toàn bộ đối tượng phát hiện được, đã lọc theo ConfidenceThreshold/IoUThreshold/SelectedClasses/MaxDetections.</summary>
    public IReadOnlyList<YoloDetection> Detections { get; set; } = Array.Empty<YoloDetection>();

    /// <summary>Cờ thành công: true nếu model load được và chạy inference không lỗi (kể cả khi 0 đối tượng được tìm thấy).</summary>
    public bool Success { get; set; }

    /// <summary>Thời gian xử lý 1 khung hình (bao gồm cả tiền xử lý + inference + hậu xử lý), tính bằng mili-giây.</summary>
    public double ExecutionTimeMs { get; set; }
}

/// <summary>
/// Tham chiếu tới 1 nguồn video (dùng cho cổng "VideoCapture"). Chỉ giữ đường dẫn file - việc mở/đọc frame
/// do chính Tool tiêu thụ nó tự thực hiện. Khi VisionFlow có VideoLoaderTool chính thức, kiểu này có thể được
/// nâng cấp để giữ sẵn handle VideoCapture đã mở (tránh phải mở lại file mỗi lần Execute).
/// </summary>
public readonly record struct VideoSourceRef(string FilePath, int TotalFrames = -1);
// Thêm ngay cạnh: public readonly record struct VideoSourceRef(string FilePath, int TotalFrames = -1);
/// <summary>Metadata kỹ thuật của video - dùng để tính toán các phép đo phụ thuộc thời gian (VD: vận tốc vật thể dựa vào FPS).</summary>
public readonly record struct VideoInfo(int Width, int Height, int TotalFrames, double Fps, TimeSpan Duration);