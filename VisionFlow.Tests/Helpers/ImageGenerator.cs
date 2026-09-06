// Khi chạy Unit Test trên hệ thống CI/CD (như GitHub Actions hoặc Jenkins), việc đọc file ảnh .bmp hoặc .png từ ổ đĩa
// rất dễ bị lỗi đường dẫn và làm chậm tốc độ test. Lớp Helper này sử dụng OpenCvSharp để tạo ảnh trực tiếp trong RAM.

using System;
using OpenCvSharp;
using VisionFlow.Tools.Imaging; // Namespace chứa class bọc ảnh MatVisionImage của hệ thống

namespace VisionFlow.Tests.Helpers;

public static class ImageGenerator
{
    /// <summary>
    /// Tạo một ảnh nhị phân nền đen (0) chứa một hình tròn màu trắng (255) hoàn hảo.
    /// </summary>
    public static MatVisionImage CreatePerfectCircle(int width, int height, Point center, int radius)
    {
        // Khởi tạo một ma trận ảnh 8-bit, 1 kênh màu (Grayscale) nền đen hoàn toàn (Scalar 0)
        var mat = new Mat(height, width, MatType.CV_8UC1, new Scalar(0));
        // CV_8UC1: báo cho máy tính biết: "Ảnh này là ảnh trắng đen/xám, mỗi điểm ảnh là một con số từ 0 đến 255".
        // Scalar: gồm 4 con số số thực dùng để biểu diễn giá trị màu sắc.
        // Vì ảnh của bạn cấu hình là CV_8UC1 (chỉ có 1 kênh màu), nên hệ thống chỉ quan tâm đến con số đầu tiên trong Scalar.

        // Vẽ đường tròn với độ dày = 1 pixel, sử dụng cơ chế chống răng cưa (AntiAlias) 
        // để hỗ trợ kiểm thử độ chính xác ở mức dưới điểm ảnh (Sub-pixel).
        Cv2.Circle(mat, center, radius, new Scalar(255), 1, LineTypes.AntiAlias);

        // Bọc cấu trúc Mat native của OpenCV vào Class quản lý ảnh của hệ thống VisionFlow
        return new MatVisionImage(mat);
    }

    /// <summary>
    /// Tạo một ảnh nhị phân nền đen chứa một đoạn thẳng màu trắng hoàn hảo phục vụ test FindLineTool.
    /// </summary>
    public static MatVisionImage CreatePerfectLine(int width, int height, Point pt1, Point pt2)
    {
        // Khởi tạo ma trận ảnh Grayscale nền đen
        var mat = new Mat(height, width, MatType.CV_8UC1, new Scalar(0));

        // Vẽ đoạn thẳng nối từ điểm pt1 đến pt2 với màu trắng (255)
        Cv2.Line(mat, pt1, pt2, new Scalar(255), 1, LineTypes.AntiAlias);

        // Trả về đối tượng ảnh chuẩn của hệ thống
        return new MatVisionImage(mat);
    }

    /// <summary>
    /// Tạo ảnh chứa hình tròn nhưng bị nhiễu hạt (Salt & Pepper) để kiểm tra độ ổn định (Robustness) của thuật toán.
    /// </summary>
    public static MatVisionImage CreateNoisyCircle(int width, int height, Point center, int radius, double noiseRatio)
    {
        // Bước 1: Tạo trước một hình tròn hoàn hảo
        var visionImage = CreatePerfectCircle(width, height, center, radius);

        // Bước 2: Lấy đối tượng Mat native ra để can thiệp trực tiếp vào từng Pixel
        var mat = visionImage.AsMat();

        // Sử dụng một Seed cố định (42) để đảm bảo dữ liệu ngẫu nhiên giống nhau 100% qua mọi lần chạy test
        var rand = new Random(42);

        // Tính toán số lượng điểm ảnh sẽ bị gán nhiễu dựa trên tỷ lệ phần trăm đầu vào
        int noisePixels = (int)(width * height * noiseRatio);

        for (int i = 0; i < noisePixels; i++)
        {
            // Chọn ngẫu nhiên tọa độ X, Y nằm trong phạm vi kích thước ảnh
            int randX = rand.Next(0, width);
            int randY = rand.Next(0, height);

            // Gán giá trị sáng tuyệt đối (255) tạo thành các đốm nhiễu trắng ngẫu nhiên
            mat.Set<byte>(randY, randX, 255);
        }

        return visionImage;
    }
}