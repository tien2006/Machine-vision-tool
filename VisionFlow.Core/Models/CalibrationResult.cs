// ==================== Vai trò chính:                Định nghĩa kiểu dữ liệu kết quả Camera Calibration (dùng bởi CalibCheckerboardTool, đọc lại bởi CalibImageCorrectorTool)
// ==================== Thành phần / Class tiêu biểu: CalibrationResult
// ==================== Phụ thuộc vào:                Core.Models (VisionResult)
// ==================== Pattern / Kỹ thuật nổi bật:   POCO/DTO - dùng double[][] (jagged array) thay vì double[,] vì System.Text.Json KHÔNG serialize được mảng 2 chiều thật (rectangular array)

using System;

namespace VisionFlow.Core.Models;

/// <summary>
/// Kết quả hiệu chỉnh camera (Camera Calibration) bằng phương pháp bàn cờ (checkerboard).
/// CameraMatrix cố tình dùng double[][] (mảng răng cưa 3 hàng x 3 cột) thay vì double[,] vì
/// System.Text.Json không hỗ trợ serialize/deserialize mảng 2 chiều dạng [,] - dùng [][] để lưu JSON được trực tiếp.
/// </summary>
public sealed class CalibrationResult : VisionResult
{
    /// <summary>Ma trận nội tại camera (Intrinsic Matrix) 3x3: [[fx,0,cx],[0,fy,cy],[0,0,1]].</summary>
    public double[][] CameraMatrix { get; set; } = Array.Empty<double[]>();

    /// <summary>Hệ số méo (Distortion Coefficients): thường 5 phần tử [k1, k2, p1, p2, k3].</summary>
    public double[] DistCoeffs { get; set; } = Array.Empty<double>();

    /// <summary>Chiều rộng ảnh dùng để calibrate (pixel).</summary>
    public int ImageWidth { get; set; }

    /// <summary>Chiều cao ảnh dùng để calibrate (pixel).</summary>
    public int ImageHeight { get; set; }

    /// <summary>Sai số tái chiếu trung bình (RMS reprojection error), đơn vị pixel. Càng nhỏ càng tốt, &lt; 1.0 coi là OK.</summary>
    public double ReprojectionError { get; set; }

    /// <summary>Thời điểm thực hiện calibration.</summary>
    public DateTime CalibrationDate { get; set; }

    /// <summary>Cờ thành công: true khi ReprojectionError &lt; 1.0 VÀ đủ số ảnh tối thiểu (MinImages).</summary>
    public bool Success { get; set; }
}