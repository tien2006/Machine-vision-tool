// ==================== Vai trò chính:                Sửa méo ảnh (undistort) dùng Camera Matrix + Distortion Coefficients đọc từ file JSON calibration
// ==================== Thành phần / Class tiêu biểu: CalibImageCorrectorTool
// ==================== Phụ thuộc vào:                OpenCvSharp (GetOptimalNewCameraMatrix, InitUndistortRectifyMap, Remap) + Core.Ports + Core.Tools + System.Text.Json
// ==================== Pattern / Kỹ thuật nổi bật:   Đọc JSON tương thích 2 convention (PascalCase/snake_case) + Cache UndistortRectifyMap (tránh tính lại mỗi lần Execute nếu tham số không đổi)

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;

namespace VisionFlow.Tools.Calibration;

/// <summary>
/// Sửa méo ảnh (undistort) bằng dữ liệu calibration đã tính từ CalibCheckerboardTool (hoặc file JSON calibration
/// Python OpenCV tương thích). Mọi ống kính đều có méo quang học (barrel ở góc rộng, pincushion ở tele) - Tool này
/// áp dụng phép biến đổi ngược (inverse warp) dựa trên Camera Matrix + Distortion Coefficients để trả về ảnh thẳng
/// đúng tỷ lệ thật, cần thiết trước khi đo lường chính xác bằng pixel.
/// Quy trình:
/// 1. Đọc file JSON tại CalibrationFilePath - hỗ trợ cả 2 convention đặt tên field (PascalCase của Tool này VÀ
///    snake_case của calibration Python OpenCV) để tương thích cả 2 nguồn.
/// 2. Tính Camera Matrix "mới" (Optimal New Camera Matrix) theo Alpha - quyết định giữ toàn bộ pixel gốc (Alpha=1,
///    có viền đen) hay chỉ giữ vùng pixel hợp lệ hoàn toàn (Alpha=0, ảnh nhỏ hơn).
/// 3. Tính sẵn bản đồ remap (map1, map2) 1 LẦN rồi CACHE lại - chỉ tính lại khi CalibrationFilePath/Alpha/kích thước
///    ảnh thay đổi, tránh lãng phí CPU khi chạy realtime nhiều khung hình liên tiếp với cùng 1 calibration.
/// 4. Remap ảnh đầu vào theo bản đồ đã tính, dùng phương pháp nội suy theo InterpolationMethod.
/// 5. (Tuỳ chọn) Crop ảnh kết quả về đúng vùng pixel hợp lệ (validPixROI) nếu CropResult = true.
/// </summary>
[ToolMetadata("CalibImageCorrector", DisplayName = "Calibration Image Corrector", Category = "Calibration",
    Description = "Undistort image using camera calibration data (camera matrix + distortion coefficients) loaded from JSON.")]
public sealed class CalibImageCorrectorTool : VisionTool
{
    #region 1. Ports
    private readonly InputPort<IVisionImage> _input;          // Ảnh gốc còn méo, thường nối từ CameraCapture/ImageLoader
    private readonly OutputPort<IVisionImage> _outCorrected;  // CorrectedImage: ảnh đã sửa méo
    #endregion

    #region 2. Parameters
    // --- Tab Calibration ---
    private readonly ToolParameter<double> _alpha;                    // 0 = crop vùng hợp lệ hoàn toàn, 1 = giữ hết pixel gốc (có viền đen)
    private readonly ToolParameter<string> _calibrationFilePath;      // Đường dẫn tuyệt đối tới file JSON calibration

    // --- Tab Output ---
    private readonly ToolParameter<bool> _cropResult; // Cắt ảnh kết quả về đúng validPixROI hay giữ nguyên kích thước gốc

    // --- Tab Processing ---
    private readonly ToolParameter<string> _interpolationMethod; // "Linear" | "Cubic" | "Nearest"
    #endregion

    // ----- Cache bản đồ remap giữa các lần Execute liên tiếp, tránh tính lại khi tham số không đổi -----
    private Mat? _cachedMap1;
    private Mat? _cachedMap2;
    private Rect _cachedValidRoi;
    private string? _cachedFilePath;
    private double _cachedAlpha = double.NaN;
    private Size _cachedImageSize;

    public CalibImageCorrectorTool()
    {
        _input = AddInput<IVisionImage>("Image", "Image");
        _outCorrected = AddOutput<IVisionImage>("CorrectedImage", "Corrected Image");

        _alpha = AddParameter<double>("Alpha", 1.0, "Alpha", min: 0.0, max: 1.0, category: "Calibration", order: 1);
        _calibrationFilePath = AddParameter<string>("CalibrationFilePath", "", "Calibration File Path", category: "Calibration", order: 2);

        _cropResult = AddParameter<bool>("CropResult", false, "Crop Result", category: "Output", order: 1);

        _interpolationMethod = AddChoiceParameter("InterpolationMethod", "Linear", new[] { "Linear", "Cubic", "Nearest" }, "Interpolation Method", category: "Processing", order: 1);
    }

    protected override void OnExecute(IToolContext context)
    {
        Mat src = _input.Value!.AsMat();

        // ----- Bước 1: Đọc file JSON calibration (hỗ trợ cả 2 convention tên field) -----
        (double[,] cameraMatrix, double[] distCoeffs) = LoadCalibrationJson(_calibrationFilePath.Value);

        using Mat camMat = ToMat3x3(cameraMatrix);
        using Mat dist = ToDistMat(distCoeffs);

        var imageSize = new Size(src.Width, src.Height);
        double alpha = _alpha.Value;

        // ----- Bước 2+3: Tính (hoặc lấy từ cache) Optimal New Camera Matrix + bản đồ remap -----
        bool cacheValid = _cachedMap1 != null && _cachedMap2 != null
            && _cachedFilePath == _calibrationFilePath.Value
            && Math.Abs(_cachedAlpha - alpha) < 1e-9
            && _cachedImageSize == imageSize;

        if (!cacheValid)
        {
            RebuildUndistortCache(camMat, dist, imageSize, alpha);
        }

        // ----- Bước 4: Remap ảnh theo bản đồ đã tính -----
        InterpolationFlags interp = _interpolationMethod.Value switch
        {
            "Cubic" => InterpolationFlags.Cubic,
            "Nearest" => InterpolationFlags.Nearest,
            _ => InterpolationFlags.Linear,
        };

        Mat corrected = new Mat();
        Cv2.Remap(src, corrected, _cachedMap1!, _cachedMap2!, interp, BorderTypes.Constant);

        // ----- Bước 5: Crop về validPixROI nếu CropResult = true -----
        Mat finalImage = corrected;
        bool cropped = false;
        if (_cropResult.Value && _cachedValidRoi.Width > 0 && _cachedValidRoi.Height > 0)
        {
            Rect safeRoi = _cachedValidRoi.Intersect(new Rect(0, 0, corrected.Width, corrected.Height));
            if (safeRoi.Width > 0 && safeRoi.Height > 0)
            {
                finalImage = new Mat(corrected, safeRoi).Clone(); // Clone để tách khỏi bộ nhớ của "corrected" gốc trước khi Dispose nó
                cropped = true;
            }
        }

        // ----- Bước 6: Xuất kết quả -----
        // finalImage "cho đi" thẳng vào Output -> KHÔNG Dispose(finalImage) sau đây
        _outCorrected.Value = new MatVisionImage(finalImage);

        if (cropped) corrected.Dispose(); // "corrected" gốc không còn dùng nữa sau khi đã Clone crop ra finalImage -> Dispose để tránh rò rỉ bộ nhớ

        context.Log($"CalibImageCorrector: Alpha={alpha:F2}, Cropped={cropped}, Interpolation={_interpolationMethod.Value}.");
    }

    #region 3. Helpers

    /// <summary>Tính lại Optimal New Camera Matrix + bản đồ remap (map1, map2) và lưu vào cache instance-level.</summary>
    private void RebuildUndistortCache(Mat cameraMatrix, Mat distCoeffs, Size imageSize, double alpha)
    {
        _cachedMap1?.Dispose();
        _cachedMap2?.Dispose();

        using Mat newCameraMatrix = Cv2.GetOptimalNewCameraMatrix(cameraMatrix, distCoeffs, imageSize, alpha, imageSize, out Rect validRoi);

        Mat map1 = new Mat();
        Mat map2 = new Mat();
        Cv2.InitUndistortRectifyMap(cameraMatrix, distCoeffs, new Mat(), newCameraMatrix, imageSize, MatType.CV_16SC2, map1, map2);

        _cachedMap1 = map1;
        _cachedMap2 = map2;
        _cachedValidRoi = validRoi;
        _cachedFilePath = _calibrationFilePath.Value;
        _cachedAlpha = alpha;
        _cachedImageSize = imageSize;
    }

    /// <summary>
    /// Đọc file JSON calibration, chấp nhận cả 2 convention: PascalCase (CameraMatrix/DistCoeffs - do
    /// CalibCheckerboardTool xuất ra) và snake_case (camera_matrix/dist_coeffs - convention phổ biến của
    /// calibration Python OpenCV), đúng theo yêu cầu tương thích trong tài liệu.
    /// </summary>
    private static (double[,] CameraMatrix, double[] DistCoeffs) LoadCalibrationJson(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            throw new ToolExecutionException("Failed to load calibration data. Check CalibrationFilePath parameter - file không tồn tại hoặc chưa được nhập.");

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(filePath));
        JsonElement root = doc.RootElement;

        JsonElement cmElement = GetPropertyEitherCase(root, "CameraMatrix", "camera_matrix");
        JsonElement dcElement = GetPropertyEitherCase(root, "DistCoeffs", "dist_coeffs");

        double[,] cameraMatrix = ParseCameraMatrix(cmElement);
        double[] distCoeffs = ParseDistCoeffs(dcElement);

        return (cameraMatrix, distCoeffs);
    }

    private static JsonElement GetPropertyEitherCase(JsonElement root, string pascalName, string snakeName)
    {
        if (root.TryGetProperty(pascalName, out var p1)) return p1;
        if (root.TryGetProperty(snakeName, out var p2)) return p2;
        throw new ToolExecutionException($"Failed to load calibration data. File JSON thiếu trường '{pascalName}'/'{snakeName}'.");
    }

    /// <summary>Chấp nhận CameraMatrix dạng mảng răng cưa 3x3 [[..],[..],[..]] HOẶC mảng phẳng 9 phần tử.</summary>
    private static double[,] ParseCameraMatrix(JsonElement element)
    {
        var result = new double[3, 3];
        if (element.ValueKind != JsonValueKind.Array) throw new ToolExecutionException("CameraMatrix trong file JSON không đúng định dạng mảng.");

        var rows = element.EnumerateArray().ToArray();
        if (rows.Length == 3 && rows[0].ValueKind == JsonValueKind.Array)
        {
            for (int r = 0; r < 3; r++)
            {
                var cols = rows[r].EnumerateArray().ToArray();
                for (int c = 0; c < 3; c++) result[r, c] = cols[c].GetDouble();
            }
        }
        else if (rows.Length == 9) // Trường hợp lưu dạng mảng phẳng 9 phần tử (row-major)
        {
            for (int i = 0; i < 9; i++) result[i / 3, i % 3] = rows[i].GetDouble();
        }
        else
        {
            throw new ToolExecutionException("CameraMatrix trong file JSON phải là mảng 3x3 hoặc mảng phẳng 9 phần tử.");
        }
        return result;
    }

    private static double[] ParseDistCoeffs(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array) throw new ToolExecutionException("DistCoeffs trong file JSON không đúng định dạng mảng.");
        return element.EnumerateArray().Select(e => e.GetDouble()).ToArray();
    }

    private static Mat ToMat3x3(double[,] m)
    {
        Mat mat = new Mat(3, 3, MatType.CV_64F);
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                mat.Set(r, c, m[r, c]);
        return mat;
    }

    private static Mat ToDistMat(double[] d)
    {
        Mat mat = new Mat(1, d.Length, MatType.CV_64F);
        for (int i = 0; i < d.Length; i++) mat.Set(0, i, d[i]);
        return mat;
    }

    #endregion
}