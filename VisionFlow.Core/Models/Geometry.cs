// ==================== Vai trò chính:                Định nghĩa các kiểu dữ liệu hình học & kết quả thuật toán thuần túy (POCO/DTO)
// ==================== Thành phần / Class tiêu biểu: Point2d, Circle, LineSegment, RectRegion, RotatedRectRegion, CircleRegion,
                                                      // CaliperRegion, Judge, VisionResult, CircleResult, LineResult, AlignResult, VisionBlob
// ==================== Phụ thuộc vào:                Không phụ thuộc gì
// ==================== Pattern / Kỹ thuật nổi bật:   record struct (value type, immutable, hiệu năng cao, không rác GC)

using System;

namespace VisionFlow.Core.Models;

/// <summary>
/// Kiểu liệt kê ba trạng thái phân định kết quả kiểm tra trong công nghiệp.
/// </summary>
public enum Judge
{
    /// <summary>
    /// Chưa kiểm tra hoặc chưa có kết quả. Giá trị ngầm định là 0.
    /// </summary>
    None = 0,

    /// <summary>
    /// Đạt (Acceptable / Good).
    /// </summary>
    OK,

    /// <summary>
    /// Không đạt (Not Good).
    /// </summary>
    NG
}

/// <summary>
/// Tọa độ của một điểm trong không gian 2D.
/// </summary>
public readonly record struct Point2d(double X, double Y);  // record: báo cho C# biết đây là một kiểu dữ liệu đặc biệt chuyên dùng để chứa dữ liệu (data container).
                                                            //   C# sẽ tự động sinh ra cho bạn các hàm so sánh bằng giá trị (Equals), hàm hiển thị (ToString).
                                                            // struct: Thay vì là một class (nằm trên vùng nhớ Heap), struct sẽ nằm trên vùng nhớ Stack
                                                            //   (hoặc đi liền với object chứa nó). Nó là Value Type (kiểu tham trị). Điều này giúp tăng hiệu năng rõ rệt
                                                            //   vì máy tính không mất công dọn rác (GC - Garbage Collection), rất phù hợp cho các cấu trúc dữ liệu nhỏ,
                                                            //   nhẹ như tọa độ, vector, màu sắc.

/// <summary>
/// Mô tả đường tròn bằng tâm và bán kính.
/// </summary>
public readonly record struct Circle(Point2d Center, double Radius);

/// <summary>
/// Mô tả đoạn thẳng nối giữa hai điểm.
/// </summary>
public readonly record struct LineSegment(Point2d P1, Point2d P2);

/// <summary>
/// Vùng hình chữ nhật ROI (Region Of Interest) thẳng đứng, định bởi góc trên bên trái, chiều rộng và chiều cao.
/// </summary>
public readonly record struct RectRegion(double X, double Y, double Width, double Height);

/// <summary>
/// Sai lệch căn chỉnh (alignment), gồm lượng dịch chuyển theo trục X, Y và góc xoay theta.
/// </summary>
public readonly record struct XYThetaOffset(double X, double Y, double Theta);