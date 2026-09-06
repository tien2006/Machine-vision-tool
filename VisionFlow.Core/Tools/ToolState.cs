// ==================== Vai trò chính:                Hợp đồng chuẩn của một Tool + lớp nền (base class) dùng chung cho mọi thuật toán
// ==================== Thành phần / Class tiêu biểu: ITool, IToolContext, VisionTool (abstract), ToolParameter<T>, ToolMetadataAttribute, ToolState, ToolExecutionException
// ==================== Phụ thuộc vào:                Ports
// ==================== Pattern / Kỹ thuật nổi bật:   Strategy Pattern + Template Method Pattern

namespace VisionFlow.Core.Tools;

/// <summary>
/// Kiểu liệt kê các trạng thái vòng đời chạy của một Tool trong một chu kỳ Pipeline[cite: 1].
/// </summary>
public enum ToolState
{
    Idle = 0,       // Trạng thái chờ chạy, cũng là giá trị mặc định của enum khi chưa gán (= 0)[cite: 1]
    Running = 1,    // Tool đang thực thi các phép tính toán học, thuật toán xử lý ảnh[cite: 1]
    Completed = 2,  // Thuật toán chạy xong trọn vẹn và thành công[cite: 1]
    Failed = 3,     // Xử lý thất bại (lỗi thuật toán hoặc dữ liệu đầu vào kiểm tra validate bị sai)[cite: 1]
    Skipped = 4     // Bị bỏ qua do node upstream trước đó bị lỗi hoặc thiếu input bắt buộc[cite: 1]
}