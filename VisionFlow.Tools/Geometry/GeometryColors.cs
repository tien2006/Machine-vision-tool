// ==================== Vai trò chính:                Helper dùng chung: chuyển tên màu (string) sang Scalar OpenCV cho các tham số hiển thị (Display) của nhóm Geometry/Measurement
// ==================== Thành phần / Class tiêu biểu: GeometryColors
// ==================== Phụ thuộc vào:                OpenCvSharp
// ==================== Pattern / Kỹ thuật nổi bật:   Static helper, dùng chung giữa nhiều file cùng namespace (giống cách CaliperUtil được Finding dùng chung)

using OpenCvSharp;

namespace VisionFlow.Tools.Geometry; // internal -> chỉ nhìn thấy trong cùng assembly, nhưng dùng được ở cả namespace Measurement vì cùng project VisionFlow.Tools

internal static class GeometryColors
{
    /// <summary>Danh sách tên màu hợp lệ, dùng làm choices cho AddChoiceParameter.</summary>
    public static readonly string[] Names = { "Red", "Green", "Blue", "Yellow", "Cyan", "Magenta", "White", "Orange" };

    /// <summary>Chuyển tên màu (string) thành Scalar OpenCV (thứ tự kênh BGR).</summary>
    public static Scalar FromName(string name) => name switch
    {
        "Red" => Scalar.Red,
        "Green" => Scalar.LimeGreen,
        "Blue" => Scalar.Blue,
        "Yellow" => Scalar.Yellow,
        "Cyan" => Scalar.Cyan,
        "Magenta" => Scalar.Magenta,
        "White" => Scalar.White,
        "Orange" => Scalar.Orange,
        _ => Scalar.Yellow, // Mặc định an toàn nếu tên màu không khớp danh sách trên
    };
}