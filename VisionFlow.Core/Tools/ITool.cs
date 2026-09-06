// ==================== Vai trò chính:                Hợp đồng chuẩn của một Tool + lớp nền (base class) dùng chung cho mọi thuật toán
// ==================== Thành phần / Class tiêu biểu: ITool, IToolContext, VisionTool (abstract), ToolParameter<T>, ToolMetadataAttribute, ToolState, ToolExecutionException
// ==================== Phụ thuộc vào:                Ports
// ==================== Pattern / Kỹ thuật nổi bật:   Strategy Pattern + Template Method Pattern

using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;

namespace VisionFlow.Core.Tools;

/// <summary>
/// Hợp đồng (Interface) định nghĩa một công cụ xử lý ảnh (đóng vai trò là một "Node" trong luồng xử lý - Flow).
/// Áp dụng mẫu thiết kế Strategy Pattern: Mỗi Tool tự đóng gói thuật toán riêng, tự đọc dữ liệu từ cổng vào (Input Ports),
/// xử lý theo cấu hình (Parameters), và ghi kết quả ra cổng ra (Output Ports).
/// </summary>
public interface ITool
{
    /// <summary>
    /// Mã định danh duy nhất cho từng "thể hiện" (Instance ID) của Tool trong một Flow.
    /// Ví dụ: "GaussianBlur_1", "GaussianBlur_2" trong cùng một sơ đồ luồng.
    /// </summary>
    string Id { get; set; }

    /// <summary>
    /// Khóa phân biệt loại Tool (Ví dụ: "VISION_BLUR", "VISION_THRESHOLD").
    /// Khóa này trùng khớp với thuộc tính [ToolMetadataAttribute.Key] dùng để đăng ký Tool vào hệ thống.
    /// </summary>
    string TypeKey { get; }

    /// <summary>
    /// Tên hiển thị thân thiện của Tool trên giao diện người dùng (Ví dụ: "Lọc nhiễu Gaussian").
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Nhóm phân loại của Tool để dễ tìm kiếm (Ví dụ: "Filter", "Detection", "IO").
    /// </summary>
    string Category { get; }

    /// <summary>
    /// Danh sách các cổng nhận dữ liệu đầu vào (Ví dụ: cổng nhận "Ảnh nguồn", "Bán kính lọc").
    /// </summary>
    IReadOnlyList<IInputPort> Inputs { get; }

    /// <summary>
    /// Danh sách các cổng xuất dữ liệu đầu ra sau khi xử lý xong (Ví dụ: "Ảnh kết quả").
    /// </summary>
    IReadOnlyList<IOutputPort> Outputs { get; }

    /// <summary>
    /// Danh sách các tham số cấu hình tĩnh của Tool (Ví dụ: ngưỡng Threshold, số lần lặp Lặp lại).
    /// Khác với Input Port (nhận dữ liệu động từ node khác), Parameter thường do người dùng cấu hình cố định trên UI.
    /// </summary>
    IReadOnlyList<IToolParameter> Parameters { get; }

    /// <summary>
    /// Trạng thái hoạt động của Tool ở lượt chạy gần nhất (Ví dụ: Chưa chạy, Đang chạy, Thành công, Thất bại).
    /// Trạng thái này sẽ do Execution Engine (bộ thực thi) cập nhật sau mỗi chu kỳ.
    /// </summary>
    ToolState State { get; }

    /// <summary>
    /// Thời gian thực thi thuật toán của lượt chạy gần nhất (tính bằng mili-giây).
    /// Dùng để đo hiệu năng (Profiling) và hiển thị thời gian phản hồi của từng Node trên UI.
    /// </summary>
    long ElapsedMs { get; }

    /// <summary>
    /// Chi tiết thông báo lỗi nếu lượt chạy gần nhất bị thất bại (State == Failed).
    /// Nếu chạy thành công hoặc chưa chạy, giá trị này sẽ là null.
    /// </summary>
    string? ErrorMessage { get; }

    /// <summary>
    /// Kích hoạt chạy thuật toán xử lý chính của Tool.
    /// Luồng xử lý: Đọc dữ liệu từ <see cref="Inputs"/> kết hợp cấu hình từ <see cref="Parameters"/>,
    /// tính toán thuật toán xử lý ảnh, và ghi kết quả trả ra vào <see cref="Outputs"/>.
    /// </summary>
    /// <param name="context">Ngữ cảnh thực thi (cung cấp logger, bộ cấp phát bộ nhớ tạm, hoặc thông tin hủy luồng cancel token).</param>
    /// <remarks>
    /// LƯU Ý QUAN TRỌNG CHO ĐƠN VỊ PHÁT TRIỂN TOOL:
    /// - KHÔNG tự ý giải phóng (Dispose) dữ liệu ở Input Port (vì các node phía sau có thể vẫn cần dùng chung vùng nhớ đó).
    /// - KHÔNG cần viết code chờ đợi dữ liệu ở Input Port, vì Execution Engine đã đảm bảo toàn bộ dữ liệu đầu vào sẵn sàng trước khi gọi hàm này.
    /// </remarks>
    void Execute(IToolContext context);
}