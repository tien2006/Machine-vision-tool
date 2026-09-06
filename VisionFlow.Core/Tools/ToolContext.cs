// ==================== Vai trò chính:                Hợp đồng chuẩn của một Tool + lớp nền (base class) dùng chung cho mọi thuật toán
// ==================== Thành phần / Class tiêu biểu: ITool, IToolContext, VisionTool (abstract), ToolParameter<T>, ToolMetadataAttribute, ToolState, ToolExecutionException
// ==================== Phụ thuộc vào:                Ports
// ==================== Pattern / Kỹ thuật nổi bật:   Strategy Pattern + Template Method Pattern

using System.Threading;

namespace VisionFlow.Core.Tools;

/// <summary>
/// Giao diện ngữ cảnh thực thi chứa các dịch vụ hệ thống xuyên suốt do Engine truyền cho Tool[cite: 1].
/// </summary>
public interface IToolContext
{
    CancellationToken CancellationToken { get; } // Token quản lý hủy luồng, giúp ngắt tool sớm khi bấm nút dừng[cite: 1]
    double PixelSize { get; }                   // Hệ số hiệu chuẩn: 1 pixel bằng bao nhiêu mm thực tế ngoài đời[cite: 1]
    void Log(string message);                   // Phương thức cho tool xuất dòng nhật ký chẩn đoán ra ngoài[cite: 1]
}