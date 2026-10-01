// ==================== Vai trò chính:                Model DỮ LIỆU THUẦN (POCO) cho tầng lưu trữ — KHÔNG phụ thuộc WPF/ObservableObject,
//                                                     để Core.Data dùng được cả khi chạy headless (không có UI), giống triết lý
//                                                     "Core không phụ thuộc UI" đã thấy ở VisionFlow.Core.Tools/VisionFlow.Hardware.
// ==================== Thành phần / Class tiêu biểu: InspectionRecord, ActivityRecord, AlarmRecord, ProductionSummary
// ==================== Phụ thuộc vào:                Không gì cả (chỉ System) — đây là tầng thấp nhất, mọi tầng khác phụ thuộc vào nó.
//
// VỊ TRÍ ĐẶT FILE: project VisionFlow.Core (hoặc project mới VisionFlow.Core.Data nếu bạn tách), thư mục Data/ — namespace VisionFlow.Core.Data.

using System;

namespace VisionFlow.Core.Data;

/// <summary>1 lần chạy Flow vision xong — OK/NG + toạ độ gắp. Tương ứng đúng dữ liệu ReportInspectionResult
/// đang đẩy vào Dashboard, giờ đẩy thêm 1 bản xuống DB.</summary>
public sealed class InspectionRecord
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public bool IsOk { get; set; }
    public double GripX { get; set; }
    public double GripY { get; set; }
    public double GripR { get; set; }
    public string RecipeName { get; set; } = "";
}

/// <summary>1 dòng lịch sử thao tác (Start/Stop/Gắp/Đặt OK/Đặt NG/Đổi Recipe...) — tương ứng ActivityLog trên Dashboard.</summary>
public sealed class ActivityRecord
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string EventText { get; set; } = "";
    public string Result { get; set; } = "-";
}

/// <summary>1 lần cảnh báo được kích hoạt — tương ứng AlarmLog.</summary>
public sealed class AlarmRecord
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string RuleName { get; set; } = "";
    public string Message { get; set; } = "";
}

/// <summary>Số liệu tổng hợp 1 ngày — dùng cho tab "Báo cáo" đọc ngược lại từ DB (chứng minh chiều đọc, không chỉ ghi).</summary>
public sealed class ProductionSummary
{
    public DateTime Date { get; set; }
    public int TotalCount { get; set; }
    public int OkCount { get; set; }
    public int NgCount { get; set; }
}