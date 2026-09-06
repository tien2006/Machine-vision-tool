// ==================== Vai trò chính:                 Hiệu chỉnh camera (Camera Calibration) bằng tấm bàn cờ - tính Camera Matrix + Distortion Coefficients, xuất file JSON
// ==================== Thành phần / Class tiêu biểu: CalibCheckerboardTool
// ==================== Phụ thuộc vào:                 OpenCvSharp (FindChessboardCorners, CornerSubPix, CalibrateCamera) + Core.Models (CalibrationResult) + Core.Ports + Core.Tools + System.Text.Json
// ==================== Pattern / Kỹ thuật nổi bật:    Zhang's Method (OpenCV CalibrateCamera) + Sub-pixel Corner Refinement + JSON Export

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Models;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;

namespace VisionFlow.Tools.Calibration;

/// <summary>
/// Hiệu chỉnh camera bằng tấm bàn cờ (checkerboard) - bước NỀN TẢNG trước khi đo lường chính xác bằng vision:
/// ảnh từ camera luôn có méo quang học (barrel/pincushion), calibrate giúp tính ra camera matrix + hệ số méo
/// để các Tool sau (CalibImageCorrector) sửa méo, đảm bảo tỉ lệ pixel-to-mm chính xác trên toàn bộ khung hình.
/// Quy trình:
/// 1. Với mỗi ảnh trong ImageList: dò 4 góc trong (inner corners) của bàn cờ bằng Cv2.FindChessboardCorners,
///    tinh chỉnh sub-pixel bằng Cv2.CornerSubPix.
/// 2. Ảnh nào dò đủ góc -> ghi nhận cặp (objectPoints 3D lý tưởng, imagePoints 2D thực tế tìm được).
/// 3. Nếu đủ MinImages ảnh hợp lệ -> chạy Cv2.CalibrateCamera (phương pháp Zhang's) ra Camera Matrix + Distortion Coefficients + Reprojection Error.
/// 4. Đánh giá thành công khi ReprojectionError &lt; 1.0 pixel.
/// 5. (Tuỳ chọn) Lưu kết quả ra file JSON để CalibImageCorrector dùng lại.
/// </summary>
[ToolMetadata("CalibCheckerboard", DisplayName = "Calibrate Checkerboard", Category = "Calibration",
    Description = "Camera calibration using checkerboard pattern - computes intrinsic matrix and distortion coefficients.")]
public sealed class CalibCheckerboardTool : VisionTool
{
    #region 1. Ports
    private readonly InputPort<IReadOnlyList<IVisionImage>> _imageList; // Danh sách ảnh checkerboard, thường nối từ FolderImageLoader.ImageList

    private readonly OutputPort<CalibrationResult> _outResult;      // CalibrationResult tổng hợp
    private readonly OutputPort<double> _outReprojectionError;      // ReprojectionError (pixel)
    private readonly OutputPort<bool> _outSuccess;                  // CalibrationSuccess
    private readonly OutputPort<int> _outCornersDetected;           // Số ảnh đã phát hiện đủ góc
    private readonly OutputPort<string> _outSavedFilePath;          // SavedFilePath (rỗng nếu không lưu)
    #endregion

    #region 2. Parameters
    // --- Tab Pattern ---
    private readonly ToolParameter<int> _chessboardWidth;   // Số góc trong theo chiều ngang (KHÔNG phải số ô)
    private readonly ToolParameter<int> _chessboardHeight;  // Số góc trong theo chiều dọc
    private readonly ToolParameter<double> _squareSize;     // Kích thước cạnh ô vuông thật (mm)

    // --- Tab Calibration ---
    private readonly ToolParameter<int> _minImages;         // Số ảnh tối thiểu đã dò được góc để chạy calibrate (>=3)
    private readonly ToolParameter<bool> _fixAspectRatio;   // Giả định fx == fy (pixel vuông)

    // --- Tab Output ---
    private readonly ToolParameter<string> _outputPath;         // Thư mục lưu file JSON (rỗng = không lưu)
    private readonly ToolParameter<string> _fileName;           // Tên file (không cần .json)
    private readonly ToolParameter<bool> _overwriteExisting;    // Cho phép ghi đè file trùng tên

    // --- Tab Advanced ---
    private readonly ToolParameter<bool> _zeroTangentDistortion; // Buộc p1, p2 = 0
    private readonly ToolParameter<bool> _fixPrincipalPoint;     // Buộc (cx, cy) cố định giữa ảnh
    #endregion

    public CalibCheckerboardTool()
    {
        _imageList = AddInput<IReadOnlyList<IVisionImage>>("ImageList", "Image List");

        _outResult = AddOutput<CalibrationResult>("CalibrationResult", "Calibration Result");
        _outReprojectionError = AddOutput<double>("ReprojectionError", "Reprojection Error");
        _outSuccess = AddOutput<bool>("CalibrationSuccess", "Calibration Success");
        _outCornersDetected = AddOutput<int>("CornersDetected", "Corners Detected");
        _outSavedFilePath = AddOutput<string>("SavedFilePath", "Saved File Path");

        _chessboardWidth = AddParameter<int>("ChessboardWidth", 9, "Chessboard Width (inner corners)", min: 2, max: 50, category: "Pattern", order: 1);
        _chessboardHeight = AddParameter<int>("ChessboardHeight", 6, "Chessboard Height (inner corners)", min: 2, max: 50, category: "Pattern", order: 2);
        _squareSize = AddParameter<double>("SquareSize", 25.0, "Square Size (mm)", min: 0.001, max: 10000.0, category: "Pattern", order: 3);

        _minImages = AddParameter<int>("MinImages", 10, "Min Images", min: 3, max: 200, category: "Calibration", order: 1);
        _fixAspectRatio = AddParameter<bool>("FixAspectRatio", true, "Fix Aspect Ratio", category: "Calibration", order: 2);

        _outputPath = AddParameter<string>("OutputPath", "", "Output Path (folder)", category: "Output", order: 1);
        _fileName = AddParameter<string>("FileName", "calibration", "File Name", category: "Output", order: 2);
        _overwriteExisting = AddParameter<bool>("OverwriteExisting", true, "Overwrite Existing", category: "Output", order: 3);

        _zeroTangentDistortion = AddParameter<bool>("ZeroTangentDistortion", false, "Zero Tangent Distortion", category: "Advanced", order: 1);
        _fixPrincipalPoint = AddParameter<bool>("FixPrincipalPoint", false, "Fix Principal Point", category: "Advanced", order: 2);
    }

    protected override void OnExecute(IToolContext context)
    {
        var images = _imageList.Value;
        if (images == null || images.Count == 0)
            throw new ToolExecutionException("CalibCheckerboard: ImageList rỗng - cần nối danh sách ảnh checkerboard.");

        int patternW = _chessboardWidth.Value, patternH = _chessboardHeight.Value;
        var patternSize = new Size(patternW, patternH);
        double squareSize = _squareSize.Value;

        var allObjectPoints = new List<Mat>();
        var allImagePoints = new List<Mat>();
        int imageWidth = 0, imageHeight = 0;
        int cornersDetected = 0;

        // Tạo sẵn mảng template 3D dùng chung cho tất cả các ảnh hợp lệ
        var objectPointsTemplateArray = new Point3f[patternW * patternH];
        int idxTemp = 0;
        for (int y = 0; y < patternH; y++)
            for (int x = 0; x < patternW; x++)
                objectPointsTemplateArray[idxTemp++] = new Point3f((float)(x * squareSize), (float)(y * squareSize), 0f);

        try
        {
            // ----- Bước 1+2: Dò góc bàn cờ trên từng ảnh -----
            foreach (var visionImage in images)
            {
                Mat mat = visionImage.AsMat();
                Mat gray = mat.Channels() == 1 ? mat : ToGrayClone(mat);

                if (imageWidth == 0) { imageWidth = gray.Width; imageHeight = gray.Height; }

                bool found = Cv2.FindChessboardCorners(gray, patternSize, out Point2f[] corners,
                    ChessboardFlags.AdaptiveThresh | ChessboardFlags.NormalizeImage | ChessboardFlags.FastCheck);

                if (found)
                {
                    // Tinh chỉnh sub-pixel: độ chính xác góc ảnh hưởng trực tiếp tới độ chính xác calibration
                    Cv2.CornerSubPix(gray, corners, new Size(11, 11), new Size(-1, -1),
                        new TermCriteria(CriteriaTypes.Eps | CriteriaTypes.MaxIter, 30, 0.001));

                    // Khởi tạo Mat và dùng SetArray để nạp dữ liệu mảng an toàn, không vi phạm access modifier
                    var objMat = new Mat(objectPointsTemplateArray.Length, 1, MatType.CV_32FC3);
                    objMat.SetArray(objectPointsTemplateArray);
                    allObjectPoints.Add(objMat);

                    var imgMat = new Mat(corners.Length, 1, MatType.CV_32FC2);
                    imgMat.SetArray(corners);
                    allImagePoints.Add(imgMat);
                    cornersDetected++;
                }

                if (!ReferenceEquals(gray, mat)) gray.Dispose(); // Chỉ Dispose bản gray tự tạo, không đụng ảnh gốc của Input port
            }

            CalibrationResult result;

            // ----- Bước 3: Kiểm tra đủ số ảnh tối thiểu -----
            if (cornersDetected < Math.Max(3, _minImages.Value))
            {
                result = new CalibrationResult
                {
                    Success = false,
                    Judge = Judge.NG,
                    ImageWidth = imageWidth,
                    ImageHeight = imageHeight,
                    ReprojectionError = double.NaN,
                    CalibrationDate = DateTime.Now,
                };

                _outResult.Value = result;
                _outReprojectionError.Value = double.NaN;
                _outSuccess.Value = false;
                _outCornersDetected.Value = cornersDetected;
                _outSavedFilePath.Value = "";
                context.Log($"CalibCheckerboard: chỉ dò được {cornersDetected}/{images.Count} ảnh đủ góc, cần tối thiểu {_minImages.Value} - KHÔNG chạy calibrate.");
                return;
            }

            // ----- Bước 3b: Chạy CalibrateCamera (phương pháp Zhang's) -----
            CalibrationFlags flags = 0;
            if (_fixAspectRatio.Value) flags |= CalibrationFlags.FixAspectRatio;
            if (_zeroTangentDistortion.Value) flags |= CalibrationFlags.ZeroTangentDist;
            if (_fixPrincipalPoint.Value) flags |= CalibrationFlags.FixPrincipalPoint;

            using Mat cameraMatrix = new Mat();
            using Mat distCoeffs = new Mat();

            double reprojError = Cv2.CalibrateCamera(
                allObjectPoints, allImagePoints, new Size(imageWidth, imageHeight),
                cameraMatrix, distCoeffs, out Mat[] rvecs, out Mat[] tvecs, flags);

            foreach (var v in rvecs) v.Dispose();
            foreach (var v in tvecs) v.Dispose();

            bool success = reprojError < 1.0;

            result = new CalibrationResult
            {
                CameraMatrix = MatToJaggedArray3x3(cameraMatrix),
                DistCoeffs = MatToFlatArray(distCoeffs),
                ImageWidth = imageWidth,
                ImageHeight = imageHeight,
                ReprojectionError = reprojError,
                CalibrationDate = DateTime.Now,
                Success = success,
                Judge = success ? Judge.OK : Judge.NG,
            };

            // ----- Bước 4: Lưu file JSON (nếu OutputPath khác rỗng) -----
            string savedPath = "";
            if (!string.IsNullOrWhiteSpace(_outputPath.Value))
            {
                savedPath = SaveCalibrationJson(result, _outputPath.Value, _fileName.Value, _overwriteExisting.Value, context);
            }

            // ----- Bước 5: Xuất kết quả -----
            _outResult.Value = result;
            _outReprojectionError.Value = reprojError;
            _outSuccess.Value = success;
            _outCornersDetected.Value = cornersDetected;
            _outSavedFilePath.Value = savedPath;

            context.Log($"CalibCheckerboard: {cornersDetected}/{images.Count} ảnh hợp lệ, ReprojectionError={reprojError:F4}px, Success={success}.");
        }
        finally
        {
            // BẢO ĐẢM GIẢI PHÓNG BỘ NHỚ MAT TRONG MỌI TRƯỜNG HỢP (KỂ CẢ KHI RETURN SỚM HOẶC GẶP NGOẠI LỆ)
            foreach (var obj in allObjectPoints) obj.Dispose();
            foreach (var img in allImagePoints) img.Dispose();
        }
    }

    #region 3. Helpers

    private static Mat ToGrayClone(Mat src)
    {
        Mat gray = new Mat();
        Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
        return gray;
    }

    /// <summary>Chuyển Mat 3x3 (CV_64F) thành mảng răng cưa double[3][3] để JSON-friendly.</summary>
    private static double[][] MatToJaggedArray3x3(Mat m)
    {
        var result = new double[3][];
        for (int r = 0; r < 3; r++)
        {
            result[r] = new double[3];
            for (int c = 0; c < 3; c++)
                result[r][c] = m.At<double>(r, c);
        }
        return result;
    }

    /// <summary>Chuyển Mat hệ số méo (1xN hoặc Nx1, CV_64F) thành mảng phẳng double[].</summary>
    private static double[] MatToFlatArray(Mat m)
    {
        int n = m.Rows * m.Cols;
        var result = new double[n];
        int idx = 0;
        for (int r = 0; r < m.Rows; r++)
            for (int c = 0; c < m.Cols; c++)
                result[idx++] = m.At<double>(r, c);
        return result;
    }

    /// <summary>Lưu CalibrationResult ra file JSON (PascalCase, đúng convention C#) trong thư mục OutputPath.</summary>
    private static string SaveCalibrationJson(CalibrationResult result, string outputPath, string fileName, bool overwrite, IToolContext context)
    {
        Directory.CreateDirectory(outputPath); // Tự tạo thư mục nếu chưa tồn tại, đúng theo tài liệu

        string safeFileName = fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? fileName : fileName + ".json";
        string fullPath = Path.Combine(outputPath, safeFileName);

        if (File.Exists(fullPath) && !overwrite)
            throw new ToolExecutionException($"CalibCheckerboard: file '{fullPath}' đã tồn tại và OverwriteExisting=false.");

        var options = new JsonSerializerOptions { WriteIndented = true };
        string json = JsonSerializer.Serialize(result, options);
        File.WriteAllText(fullPath, json);

        context.Log($"CalibCheckerboard: đã lưu calibration vào '{fullPath}'.");
        return fullPath;
    }

    #endregion
}