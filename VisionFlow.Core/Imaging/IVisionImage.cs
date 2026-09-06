// ==================== Vai trò chính:                Trừu tượng hóa ảnh — tách biệt logic nghiệp vụ khỏi thư viện xử lý ảnh cụ thể
// ==================== Thành phần / Class tiêu biểu: IVisionImage (interface)
// ==================== Phụ thuộc vào:                Không phụ thuộc gì (0 dependency)
// ==================== Pattern / Kỹ thuật nổi bật:   Dependency Inversion Principle (DIP)

using System;

namespace VisionFlow.Core.Imaging;

/// <summary>
/// Giao diện trừu tượng hóa một tấm ảnh chảy qua pipeline xử lý của VisionFlow.
/// Tách biệt tầng Core khỏi các thư viện xử lý ảnh cụ thể (như OpenCV).
/// </summary>
public interface IVisionImage : IDisposable
{
    /// <summary>
    /// Chiều rộng của tấm ảnh (tính theo pixel).
    /// </summary>
    int Width { get; }

    /// <summary>
    /// Chiều cao của tấm ảnh (tính theo pixel).
    /// </summary>
    int Height { get; }

    /// <summary>
    /// Số kênh màu của ảnh (ví dụ: 1 = ảnh xám, 3 = BGR, 4 = BGRA).
    /// </summary>
    int Channels { get; }

    /// <summary>
    /// Cho biết ảnh đã bị giải phóng tài nguyên (Dispose) hay chưa.
    /// Giúp hệ thống quản lý vòng đời và tránh lỗi double-dispose.
    /// </summary>
    bool IsDisposed { get; }
        
    /// <summary>
    /// Tạo một bản sao sâu (deep copy) hoàn chỉnh của tấm ảnh, 
    /// bao gồm cả việc sao chép toàn bộ vùng nhớ pixel sang vùng nhớ mới.
    /// </summary>
    /// <returns>Một đối tượng IVisionImage mới độc lập dữ liệu.</returns>
    IVisionImage Clone();
}