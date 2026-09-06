// ==================== Vai trò chính:                Adapter nối interface ảnh trừu tượng của Core với kiểu Mat thật của OpenCV
// ==================== Thành phần / Class tiêu biểu: MatVisionImage, VisionImageExtensions (.AsMat())
// ==================== Phụ thuộc vào:                OpenCvSharp + Core.Imaging
// ==================== Pattern / Kỹ thuật nổi bật:   Adapter Pattern

using System;
using OpenCvSharp; // Nạp thư viện ngoài OpenCvSharp để thao tác trực tiếp với đối tượng Mat[cite: 4]
using VisionFlow.Core.Imaging; // Kéo vào interface VisionImage từ tầng trừu tượng Core[cite: 4]

namespace VisionFlow.Tools.Imaging; // Định vị thuộc phân hệ xử lý hình ảnh của tầng Tools[cite: 4]

/// <summary>
/// Lớp cài đặt cụ thể của VisionImage đóng vai trò là một adapter bọc đối tượng Mat của OpenCV[cite: 4].
/// Là cầu nối duy nhất giúp tầng Core xử lý ảnh trừu tượng mà không bị phụ thuộc vào kiểu dữ liệu native của OpenCV[cite: 4].
/// </summary>
public sealed class MatVisionImage : IVisionImage // Khóa kế thừa bằng sealed để tối ưu hiệu năng cho một adapter mỏng[cite: 4]
{
    public Mat Mat { get; } // Thuộc tính bất biến chỉ đọc giữ thực thể ma trận ảnh của OpenCV[cite: 4]

    public int Width => Mat.Width; // Chuyển tiếp biểu thức lấy chiều rộng trực tiếp từ đối tượng Mat[cite: 4]
    public int Height => Mat.Height; // Chuyển tiếp biểu thức lấy chiều cao trực tiếp từ đối tượng Mat[cite: 4]
    public int Channels => Mat.Channels(); // Gọi phương thức lấy số kênh màu của đối tượng Mat[cite: 4]
    public bool IsDisposed => Mat.IsDisposed; // Kiểm tra trạng thái giải phóng bộ nhớ của ma trận ảnh[cite: 4]

    public MatVisionImage(Mat mat)
    {
        Mat = mat ?? throw new ArgumentNullException(nameof(mat)); // Kiểm tra fast-fail bảo đảm đối tượng không bao giờ tồn tại với bộ nhớ null[cite: 4]
    }

    public IVisionImage Clone()
    {
        return new MatVisionImage(Mat.Clone()); // Thực hiện sao chép sâu dữ liệu pixel vùng nhớ unmanaged của OpenCV[cite: 4]
    }

    public void Dispose()
    {
        if (!Mat.IsDisposed) // Phòng thủ kiểm tra trạng thái nhằm tránh việc giải phóng vùng nhớ unmanaged hai lần[cite: 4]
        {
            Mat.Dispose(); // Hủy vùng nhớ native của OpenCV để tránh rò rỉ bộ nhớ hệ thống[cite: 4]
        }
    }
}

/// <summary>
/// Lớp tiện ích mở rộng cung cấp các Extension Method cho interface VisionImage[cite: 4].
/// </summary>
public static class VisionImageExtensions
{
    public static Mat AsMat(this IVisionImage image)
    {
        if (image is MatVisionImage matImage) // Sử dụng pattern matching để mở hộp kiểm tra kiểu dữ liệu thực tế lúc runtime[cite: 4]
        {
            return matImage.Mat; // Trả về đối tượng Mat gốc phục vụ các thuật toán OpenCV[cite: 4]
        }
        throw new InvalidOperationException($"The image type '{image?.GetType().Name}' is not compatible with OpenCV Mat."); // Chặn sớm lỗi không tương thích hệ thống[cite: 4]
    }
}