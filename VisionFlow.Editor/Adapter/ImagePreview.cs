using System.Windows.Media.Imaging; // Namespace chứa BitmapSource của WPF
using OpenCvSharp.WpfExtensions; // Thư viện mở rộng chuyển đổi giữa OpenCV Mat và WPF Imaging
using VisionFlow.Core.Imaging; // Namespace chứa interface IVisionImage của dự án
using VisionFlow.Tools.Imaging;

namespace VisionFlow.Editor.Adapter;

/// <summary>
/// Lớp tiện ích tĩnh dùng để chuyển đổi hình ảnh từ tầng xử lý OpenCV sang dạng BitmapSource để hiển thị trên giao diện WPF.
/// </summary>
public static class ImagePreview
{
    /// <summary>
    /// Chuyển đổi đối tượng IVisionImage thành BitmapSource dùng cho hiển thị WPF UI.
    /// </summary>
    /// <param name="image">Đối tượng ảnh IVisionImage cần chuyển đổi</param>
    /// <returns>Trả về BitmapSource đã được Freeze nếu thành công, ngược lại trả về null</returns>
    public static BitmapSource? ToBitmapSource(IVisionImage? image)
    {
        // 1. Kiểm tra phòng vệ: Nếu ảnh null hoặc đã bị giải phóng tài nguyên (Disposed)
        if (image == null || image.IsDisposed) return null;

        // Lấy đối tượng Mat OpenCV từ image
        var mat = image.AsMat();

        // 2. Kiểm tra nếu ma trận ảnh rỗng (không có dữ liệu pixel)
        if (mat.Empty()) return null;

        // 3. Thực hiện chuyển đổi Mat thành BitmapSource của WPF
        var bitmap = mat.ToBitmapSource();

        // 4. Đóng băng đối tượng BitmapSource để có thể truyền và truy cập an toàn qua các Thread khác nhau (Cross-Thread UI)
        bitmap.Freeze();

        return bitmap;
    }
}