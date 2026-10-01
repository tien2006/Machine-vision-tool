// ==================== Vai trò chính:                Học "thế nào là sản phẩm BÌNH THƯỜNG" từ ảnh sạch (không lỗi), lưu thành model để AnomalyDetectionNode dùng sau
// ==================== Thành phần / Class tiêu biểu: AnomalyTrainingNodeTool
// ==================== Phụ thuộc vào:                System.Text.Json (lưu model) - KHÔNG cần thư viện Deep Learning ngoài
// ==================== Pattern / Kỹ thuật nổi bật:   PaDiM-lite THEO TỪNG Ô LƯỚI - khác bản đầu tiên (1 Gaussian
//                       chung cho toàn ảnh, chỉ ra được điểm bất thường TỔNG, không định vị được lỗi), bản này
//                       fit 1 Gaussian 2D RIÊNG cho mỗi ô lưới không gian -> AnomalyDetectionNode tính được
//                       bản đồ bất thường theo từng vùng -> khoanh vùng lỗi (BoundingBoxes) thay vì chỉ ra 1 số duy nhất.
//
// GHI CHÚ QUAN TRỌNG: tài liệu mô tả kiến trúc dùng backbone CNN (EfficientNet qua BackbonePath) để trích đặc
// trưng sâu - đây là cách làm chuẩn công nghiệp (PaDiM/PatchCore) nhưng cần thêm NuGet Microsoft.ML.OnnxRuntime
// + 1 file backbone .onnx đã export sẵn intermediate feature layer, hiện KHÔNG có trong dự án. Bản dưới đây
// dùng đặc trưng THỐNG KÊ CỔ ĐIỂN (mean/std độ sáng mỗi ô) thay cho đặc trưng CNN sâu - cùng bản chất thuật
// toán PaDiM (Gaussian theo từng vị trí không gian + khoảng cách Mahalanobis) nhưng nhẹ, chạy được ngay.

using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;

namespace VisionFlow.Tools.AnomalyDetection;

/// <summary>Kết quả tổng thể của 1 lần huấn luyện.</summary>
public enum AnomalyTrainingStatus
{
    Success,             // Huấn luyện thành công, model đã lưu
    InsufficientImages,  // Không đủ MinTrainingImages ảnh sạch để bắt đầu học
    TimedOut,            // Vượt quá TrainingTimeoutMs trước khi xử lý xong toàn bộ ảnh
    Cancelled            // Người dùng bấm hủy (CancellationToken) giữa chừng
}

/// <summary>Phân phối Gaussian 2D (mean độ sáng, std độ sáng) học được cho 1 ô lưới không gian cụ thể.</summary>
public sealed class CellGaussian
{
    public double[] Mean { get; set; } = new double[2];             // [meanOfMean, meanOfStd] qua các ảnh train
    public double[][] InverseCovariance { get; set; } = Array.Empty<double[]>(); // Ma trận 2x2 nghịch đảo
}

/// <summary>Cấu trúc dữ liệu model lưu ra file JSON - AnomalyDetectionNode đọc lại đúng cấu trúc này.</summary>
public sealed class AnomalyModelData
{
    public int ImageSize { get; set; }
    public int FeaturePatchGrid { get; set; }
    public CellGaussian[] Cells { get; set; } = Array.Empty<CellGaussian>(); // Độ dài = FeaturePatchGrid^2, thứ tự row-major (gy*grid+gx)
    public int TrainedImageCount { get; set; }
    public DateTime TrainedAtUtc { get; set; }
}

/// <summary>
/// AnomalyTrainingNode: nhận 1 danh sách ảnh "sạch" (chỉ chứa sản phẩm BÌNH THƯỜNG, không cần ảnh lỗi mẫu),
/// chia mỗi ảnh thành lưới FeaturePatchGrid x FeaturePatchGrid ô, và với MỖI VỊ TRÍ Ô riêng biệt, fit 1 phân
/// phối Gaussian 2D (mean, std độ sáng) qua toàn bộ ảnh training. Nhờ mô hình hoá theo TỪNG VỊ TRÍ KHÔNG GIAN
/// thay vì gộp chung toàn ảnh, AnomalyDetectionNode sau này có thể tính "mức độ bất thường" cho TỪNG VÙNG
/// riêng lẻ trên ảnh mới, từ đó khoanh vùng lỗi cụ thể (BoundingBoxes) chứ không chỉ ra 1 điểm số chung chung.
/// </summary>
[ToolMetadata(
    "AnomalyTrainingNode",
    DisplayName = "Anomaly Training Node",
    Category = "AnomalyDetection",
    Description = "Fit a per-region statistical 'normal' model (PaDiM-lite) from clean sample images")]
public sealed class AnomalyTrainingNodeTool : VisionTool
{
    #region 1. Khai báo Port (In/Out)
    private readonly InputPort<IReadOnlyList<IVisionImage>> _imageList;

    private readonly OutputPort<string> _outModelPath;
    private readonly OutputPort<AnomalyTrainingStatus> _outTrainingStatus;
    private readonly OutputPort<double> _outTrainingProgress; // 0-100, XEM GHI CHÚ trong OnExecute về giới hạn tiến trình "tức thời"
    private readonly OutputPort<string> _outConfigPath;
    private readonly OutputPort<bool> _outIsTrainingComplete;
    private readonly OutputPort<int> _outTrainedImageCount;
    #endregion

    #region 2. Khai báo Parameter (đúng 4 tham số tài liệu liệt kê)
    private readonly ToolParameter<int> _imageSize;
    private readonly ToolParameter<int> _minTrainingImages;
    private readonly ToolParameter<string> _modelOutputPath;
    private readonly ToolParameter<int> _trainingTimeoutMs;

    // --- Tham số BỔ SUNG (không có trong tài liệu, cần thiết vì tài liệu không đặc tả chi tiết thuật toán trích đặc trưng) ---
    private readonly ToolParameter<int> _featurePatchGrid;
    #endregion

    public AnomalyTrainingNodeTool()
    {
        _imageList = AddInput<IReadOnlyList<IVisionImage>>("ImageList", "Image List");

        _outModelPath = AddOutput<string>("ModelPath", "Model Path");
        _outTrainingStatus = AddOutput<AnomalyTrainingStatus>("TrainingStatus", "Training Status");
        _outTrainingProgress = AddOutput<double>("TrainingProgress", "Training Progress");
        _outConfigPath = AddOutput<string>("ConfigPath", "Config Path");
        _outIsTrainingComplete = AddOutput<bool>("IsTrainingComplete", "Is Training Complete");
        _outTrainedImageCount = AddOutput<int>("TrainedImageCount", "Trained Image Count");

        _imageSize = AddParameter("ImageSize", 224, "Image Size", min: 32, max: 1024, category: "Training", order: 1);
        _minTrainingImages = AddParameter("MinTrainingImages", 10, "Min Training Images", min: 1, max: 100000, category: "Training", order: 2);
        _modelOutputPath = AddParameter("ModelOutputPath", @"D:\MVA\25\model.json", "Model Output Path", category: "Training", order: 3);
        _trainingTimeoutMs = AddParameter("TrainingTimeoutMs", 30 * 60 * 1000, "Training Timeout (ms)", min: 1000, max: 3 * 60 * 60 * 1000, category: "Training", order: 4);

        _featurePatchGrid = AddParameter("FeaturePatchGrid", 8, "Feature Patch Grid", min: 2, max: 32, category: "Training", order: 5);
    }

    protected override void OnExecute(IToolContext context)
    {
        var stopwatch = Stopwatch.StartNew();
        var images = _imageList.Value;
        int totalAvailable = images?.Count ?? 0;

        if (totalAvailable < _minTrainingImages.Value)
        {
            EmitResult(AnomalyTrainingStatus.InsufficientImages, string.Empty, string.Empty, 0, 0);
            context.Log($"AnomalyTrainingNode: need at least {_minTrainingImages.Value} clean images, only {totalAvailable} provided.");
            return;
        }

        int gridN = _featurePatchGrid.Value;
        int cellCount = gridN * gridN;
        int imgSize = _imageSize.Value;

        // Mỗi ô lưới có DANH SÁCH RIÊNG các mẫu (mean,std) thu thập được qua từng ảnh training
        var perCellSamples = new List<double[]>[cellCount];
        for (int c = 0; c < cellCount; c++) perCellSamples[c] = new List<double[]>();

        int processed = 0;
        var stopStatus = AnomalyTrainingStatus.Success;
        foreach (var img in images!)
        {
            if (context.CancellationToken.IsCancellationRequested) { stopStatus = AnomalyTrainingStatus.Cancelled; break; }
            if (stopwatch.Elapsed.TotalMilliseconds > _trainingTimeoutMs.Value) { stopStatus = AnomalyTrainingStatus.TimedOut; break; }

            double[][] cellFeatures = ExtractPerCellFeatures(img.AsMat(), imgSize, gridN);
            for (int c = 0; c < cellCount; c++) perCellSamples[c].Add(cellFeatures[c]);
            processed++;
        }

        double progress = totalAvailable > 0 ? processed * 100.0 / totalAvailable : 0;

        // LƯU Ý VỀ TrainingProgress: IToolContext hiện không có cơ chế báo tiến trình "trực tuyến" trong lúc
        // Execute() đang chạy - UI chỉ nhận Output SAU KHI Execute() trả về hoàn toàn. TrainingProgress ở đây
        // là ảnh chụp CUỐI CÙNG (100% nếu học xong hết), không phải thanh tiến trình cập nhật thời gian thực.

        if (processed < _minTrainingImages.Value)
        {
            EmitResult(stopStatus, string.Empty, string.Empty, progress, processed);
            context.Log($"AnomalyTrainingNode: training stopped early ({stopStatus}) after {processed}/{totalAvailable} images - not enough to fit a model.");
            return;
        }

        // ----- Fit 1 Gaussian 2D RIÊNG cho từng ô lưới -----
        var cells = new CellGaussian[cellCount];
        for (int c = 0; c < cellCount; c++)
        {
            double[] mean = ComputeMean2D(perCellSamples[c]);
            double[,] cov = ComputeCovariance2D(perCellSamples[c], mean);
            cov[0, 0] += 1e-3; cov[1, 1] += 1e-3; // Regularization nhẹ - tránh suy biến khi std gần như không đổi giữa các ảnh sạch
            double[,] inv = Invert2x2(cov);

            cells[c] = new CellGaussian { Mean = mean, InverseCovariance = new[] { new[] { inv[0, 0], inv[0, 1] }, new[] { inv[1, 0], inv[1, 1] } } };
        }

        string modelPath = _modelOutputPath.Value;
        string? dir = Path.GetDirectoryName(modelPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var modelData = new AnomalyModelData
        {
            ImageSize = imgSize,
            FeaturePatchGrid = gridN,
            Cells = cells,
            TrainedImageCount = processed,
            TrainedAtUtc = DateTime.UtcNow
        };
        File.WriteAllText(modelPath, JsonSerializer.Serialize(modelData));

        string configPath = Path.Combine(dir ?? ".", Path.GetFileNameWithoutExtension(modelPath) + ".config.json");
        var configData = new
        {
            ImageSize = imgSize,
            MinTrainingImages = _minTrainingImages.Value,
            FeaturePatchGrid = gridN,
            TrainingTimeoutMs = _trainingTimeoutMs.Value,
            TrainedImageCount = processed,
            TrainedAtUtc = DateTime.UtcNow,
            Status = nameof(AnomalyTrainingStatus.Success)
        };
        File.WriteAllText(configPath, JsonSerializer.Serialize(configData));

        EmitResult(AnomalyTrainingStatus.Success, modelPath, configPath, 100.0, processed);
        context.Log($"AnomalyTrainingNode: trained on {processed} image(s), grid={gridN}x{gridN} ({cellCount} independent cells), saved to '{modelPath}' in {stopwatch.Elapsed.TotalSeconds:F1}s");
    }

    private void EmitResult(AnomalyTrainingStatus status, string modelPath, string configPath, double progress, int trainedCount)
    {
        _outModelPath.Value = modelPath;
        _outTrainingStatus.Value = status;
        _outTrainingProgress.Value = progress;
        _outConfigPath.Value = configPath;
        _outIsTrainingComplete.Value = status == AnomalyTrainingStatus.Success;
        _outTrainedImageCount.Value = trainedCount;
    }

    #region Trích đặc trưng theo từng ô lưới (dùng chung với AnomalyDetectionNode để đảm bảo nhất quán)
    /// <summary>Chia ảnh thành lưới gridN x gridN ô, trả về mảng [cellCount][2] = (mean, std) độ sáng của từng ô, thứ tự row-major.</summary>
    internal static double[][] ExtractPerCellFeatures(Mat src, int imageSize, int gridN)
    {
        using Mat resized = new Mat();
        Cv2.Resize(src, resized, new Size(imageSize, imageSize));
        using Mat gray = resized.Channels() == 1 ? resized.Clone() : ToGray(resized);

        int cellW = imageSize / gridN, cellH = imageSize / gridN;
        var result = new double[gridN * gridN][];
        int idx = 0;
        for (int gy = 0; gy < gridN; gy++)
            for (int gx = 0; gx < gridN; gx++)
            {
                var roi = new Rect(gx * cellW, gy * cellH, cellW, cellH);
                using Mat cell = new Mat(gray, roi);
                Cv2.MeanStdDev(cell, out Scalar meanVal, out Scalar stdVal);
                result[idx++] = new[] { meanVal.Val0, stdVal.Val0 };
            }
        return result;
    }

    internal static Mat ToGray(Mat src)
    {
        var gray = new Mat();
        Cv2.CvtColor(src, gray, src.Channels() == 4 ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY);
        return gray;
    }
    #endregion

    #region Thống kê Gaussian 2D (thuần C#, không phụ thuộc thư viện toán ngoài)
    private static double[] ComputeMean2D(List<double[]> samples)
    {
        double m0 = 0, m1 = 0;
        foreach (var s in samples) { m0 += s[0]; m1 += s[1]; }
        return new[] { m0 / samples.Count, m1 / samples.Count };
    }

    private static double[,] ComputeCovariance2D(List<double[]> samples, double[] mean)
    {
        double c00 = 0, c01 = 0, c11 = 0;
        foreach (var s in samples)
        {
            double d0 = s[0] - mean[0], d1 = s[1] - mean[1];
            c00 += d0 * d0; c01 += d0 * d1; c11 += d1 * d1;
        }
        int n = Math.Max(1, samples.Count - 1); // Hiệu chỉnh mẫu (Bessel's correction)
        return new[,] { { c00 / n, c01 / n }, { c01 / n, c11 / n } };
    }

    /// <summary>Nghịch đảo ma trận 2x2 bằng công thức đóng (closed-form) - nhanh và chính xác tuyệt đối, không cần Gauss-Jordan tổng quát vì mỗi ô chỉ có 2 chiều (mean, std).</summary>
    internal static double[,] Invert2x2(double[,] m)
    {
        double det = m[0, 0] * m[1, 1] - m[0, 1] * m[1, 0];
        if (Math.Abs(det) < 1e-12) det = 1e-12; // Phòng hờ ma trận vẫn suy biến sau regularization
        double invDet = 1.0 / det;
        return new[,] { { m[1, 1] * invDet, -m[0, 1] * invDet }, { -m[1, 0] * invDet, m[0, 0] * invDet } };
    }
    #endregion
}