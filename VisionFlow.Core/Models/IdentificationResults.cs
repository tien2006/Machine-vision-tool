// ==================== Vai trò chính:                Định nghĩa kiểu dữ liệu kết quả cho nhóm Tool Identification (OCREngine, BarcodeReader, QRCodeReader)
// ==================== Thành phần / Class tiêu biểu: OcrResult, DecodedCode, BarcodeReadResult, QrReadResult
// ==================== Phụ thuộc vào:                Core.Models (RectRegion, Point2d)
// ==================== Pattern / Kỹ thuật nổi bật:   POCO/DTO, gộp chung 1 file vì cùng nhóm Identification và đều là kiểu dữ liệu nhỏ

using System;
using System.Collections.Generic;

namespace VisionFlow.Core.Models;

/// <summary>Kết quả OCR (Optical Character Recognition) từ OCREngineTool.</summary>
public sealed class OcrResult : VisionResult
{
    /// <summary>Chuỗi văn bản nhận dạng được. Rỗng nếu không có text hoặc Confidence dưới MinConfidence.</summary>
    public string RecognizedText { get; set; } = "";

    /// <summary>Độ tin cậy trung bình của kết quả OCR (0.0 - 1.0).</summary>
    public double Confidence { get; set; }

    /// <summary>Khung bao quanh vùng text thực sự nhận dạng được (hợp của các từ đã detect), toạ độ trên ảnh gốc.</summary>
    public RectRegion BoundingBox { get; set; }

    /// <summary>Thời gian xử lý (ms).</summary>
    public double ProcessingTimeMs { get; set; }

    /// <summary>true nếu kết quả không đủ tin cậy (Confidence &lt; MinConfidence hoặc không có text) - cần con người dán nhãn lại (Active Learning).</summary>
    public bool NeedsLabeling { get; set; }
}

/// <summary>Mô tả MỘT mã (barcode/QR) đã giải mã được, kèm khung bao quanh trên ảnh gốc.</summary>
public readonly record struct DecodedCode(string Text, RectRegion BoundingBox);

/// <summary>Kết quả đọc mã vạch (1D barcode) từ BarcodeReaderTool - có thể tìm được nhiều mã trong 1 ảnh.</summary>
public sealed class BarcodeReadResult : VisionResult
{
    /// <summary>Toàn bộ mã vạch giải mã được trong khung hình.</summary>
    public IReadOnlyList<DecodedCode> Codes { get; set; } = Array.Empty<DecodedCode>();

    /// <summary>true nếu tìm thấy ít nhất 1 mã vạch hợp lệ.</summary>
    public bool Found { get; set; }
}

/// <summary>Kết quả đọc QR code từ QRCodeReaderTool - tập trung vào 1 QR chính (theo đúng output Position/Corners dạng số ít trong tài liệu).</summary>
public sealed class QrReadResult : VisionResult
{
    /// <summary>true nếu tìm thấy và giải mã được QR code hợp lệ (đã qua Validation MinTextLength/RequiredLength).</summary>
    public bool Found { get; set; }

    /// <summary>Dữ liệu thô giải mã được từ QR (URL, mã sản phẩm, text tự do...).</summary>
    public string Text { get; set; } = "";

    /// <summary>Chỉ số chất lượng ước lượng (0-100) dựa trên độ nét (sharpness) vùng QR - giá trị thấp gợi ý ống kính bẩn/QR mờ/hư.</summary>
    public double Quality { get; set; }

    /// <summary>Toạ độ tâm QR code trên ảnh gốc.</summary>
    public Point2d Position { get; set; }

    /// <summary>4 góc của QR code trên ảnh gốc (thứ tự theo chiều kim đồng hồ, bắt đầu từ góc trên-trái) - dùng để tính độ nghiêng/xoay/phối cảnh.</summary>
    public IReadOnlyList<Point2d> Corners { get; set; } = Array.Empty<Point2d>();
}