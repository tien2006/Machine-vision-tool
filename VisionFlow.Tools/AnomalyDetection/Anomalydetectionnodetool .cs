// ==================== Vai trò chính:                Phát hiện + KHOANH VÙNG bất thường trên ảnh mới, dựa vào model đã học từ AnomalyTrainingNode
// ==================== Thành phần / Class tiêu biểu: AnomalyDetectionNodeTool
// ==================== Phụ thuộc vào:                System.Text.Json (đọc model), OpenCvSharp
// ==================== Pattern / Kỹ thuật nổi bật:   Với MỖI ô lưới, tính khoảng cách Mahalanobis giữa đặc
//                       trưng ảnh hiện tại và phân phối Gaussian "bình thường" đã học riêng cho đúng vị trí
//                       đó -> ghép thành bản đồ nhiệt (heat map) bất thường theo không gian -> threshold +
//                       FindContours để ra DefectCount/BoundingBoxes, thay vì chỉ 1 điểm số chung chung.
//
// GHI CHÚ: BackbonePath/Device hiện CHƯA được dùng trong logic bên dưới (dự phòng cho khi nâng cấp sang
// backbone CNN qua ONNX Runtime - xem ghi chú ở đầu AnomalyTrainingNodeTool.cs). Thuật toán hiện dùng đặc
// trưng thống kê patch (mean/std độ sáng), không cần GPU nên tham số Device không ảnh hưởng hiệu năng.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;

namespace VisionFlow.Tools.AnomalyDetection;

/// <summary>Thiết bị tính toán mong muốn - hiện KHÔNG ảnh hưởng thuật toán (chỉ dùng thống kê, không cần GPU), giữ tham số để tương thích khi nâng cấp backbone CNN.</summary>
public enum AnomalyComputeDevice { Auto, Cpu, Gpu }

/// <summary>
/// AnomalyDetectionNode: nạp model đã huấn luyện từ <see cref="AnomalyTrainingNodeTool"/> (mean + hiệp
/// phương sai nghịch đảo RIÊNG cho từng ô lưới không gian), rồi so sánh ảnh mới với phân phối "bình thường"
/// tại TỪNG vị trí bằng khoảng cách Mahalanobis - vị trí nào lệch xa phân phối học được thì bị đánh dấu là
/// vùng bất thường. Khác phương pháp Threshold/BlobAnalysis truyền thống, tool này KHÔNG CẦN biết trước
/// hình dạng lỗi cụ thể - chỉ cần lỗi làm vùng đó "khác" so với các mẫu sạch đã học.
/// </summary>
[ToolMetadata(
    "AnomalyDetectionNode",
    DisplayName = "Anomaly Detection Node",
    Category = "AnomalyDetection",
    Description = "Detect and localize anomalies against a model trained by AnomalyTrainingNode using per-region Mahalanobis distance")]
public sealed class AnomalyDetectionNodeTool : VisionTool
{
    #region 1. Khai báo Port (In/Out)
    private readonly InputPort<IVisionImage> _imageMatrix;

    private readonly OutputPort<bool> _outIsAnomaly;
    private readonly OutputPort<double> _outAnomalyScore;
    private readonly OutputPort<double> _outThreshold;
    private readonly OutputPort<double> _outConfidence;
    private readonly OutputPort<int> _outDefectCount;
    private readonly OutputPort<IReadOnlyList<Rect>> _outBoundingBoxes;
    private readonly OutputPort<bool> _outHasVisualization;
    private readonly OutputPort<IVisionImage> _outVisualizationImage;
    #endregion

    #region 2. Khai báo Parameter (đúng 4 tham số tài liệu liệt kê)
    private readonly ToolParameter<string> _modelPath;
    private readonly ToolParameter<string> _backbonePath;      // Hiện chưa dùng - xem ghi chú đầu file
    private readonly ToolParameter<float> _customThreshold;
    private readonly ToolParameter<AnomalyComputeDevice> _device; // Hiện chưa dùng - xem ghi chú đầu file
    #endregion

    // Cache model giữa các lần Execute liên tiếp - đọc/parse JSON mỗi frame sẽ rất tốn nếu chạy realtime,
    // giống pattern _cachedPredictor đã dùng ở ObjectDetectionEngineTool.
    private AnomalyModelData? _cachedModel;
    private string _cachedModelPath = string.Empty;

    public AnomalyDetectionNodeTool()
    {
        _imageMatrix = AddInput<IVisionImage>("ImageMatrix", "Image Matrix");

        _outIsAnomaly = AddOutput<bool>("IsAnomaly", "Is Anomaly");
        _outAnomalyScore = AddOutput<double>("AnomalyScore", "Anomaly Score");
        _outThreshold = AddOutput<double>("Threshold", "Threshold");
        _outConfidence = AddOutput<double>("Confidence", "Confidence");
        _outDefectCount = AddOutput<int>("DefectCount", "Defect Count");
        _outBoundingBoxes = AddOutput<IReadOnlyList<Rect>>("BoundingBoxes", "Bounding Boxes");
        _outHasVisualization = AddOutput<bool>("HasVisualization", "Has Visualization");
        _outVisualizationImage = AddOutput<IVisionImage>("VisualizationImage", "Visualization Image");

        _modelPath = AddParameter("ModelPath", string.Empty, "Model Path", category: "Detection", order: 1);
        _backbonePath = AddParameter("BackbonePath", string.Empty, "Backbone Path", category: "Detection", order: 2);
        _customThreshold = AddParameter("CustomThreshold", 3.0f, "Custom Threshold", min: 0.1f, max: 50f, category: "Detection", order: 3);
        _device = AddParameter("Device", AnomalyComputeDevice.Auto, "Device", category: "Detection", order: 4);
    }

    protected override void OnExecute(IToolContext context)
    {
        var model = LoadModel(_modelPath.Value);
        Mat src = _imageMatrix.Value!.AsMat();

        int gridN = model.FeaturePatchGrid;
        int imgSize = model.ImageSize;
        int cellCount = gridN * gridN;

        if (model.Cells.Length != cellCount)
            throw new ToolExecutionException($"AnomalyDetectionNode: corrupted model - expected {cellCount} cells, found {model.Cells.Length}.");

        // ----- Bước 1: trích đặc trưng từng ô lưới - DÙNG CHUNG hàm với AnomalyTrainingNode để đảm bảo nhất quán -----
        double[][] cellFeatures = AnomalyTrainingNodeTool.ExtractPerCellFeatures(src, imgSize, gridN);

        // ----- Bước 2: tính khoảng cách Mahalanobis cho TỪNG ô, ghép thành bản đồ nhiệt (heat map) kích thước gridN x gridN -----
        using Mat heatSmall = new Mat(gridN, gridN, MatType.CV_32F);
        double maxDistance = 0;
        for (int gy = 0; gy < gridN; gy++)
            for (int gx = 0; gx < gridN; gx++)
            {
                int c = gy * gridN + gx;
                double distance = MahalanobisDistance(cellFeatures[c], model.Cells[c]);
                heatSmall.Set(gy, gx, (float)distance);
                if (distance > maxDistance) maxDistance = distance;
            }

        double threshold = _customThreshold.Value;
        double anomalyScore = maxDistance; // Lấy giá trị LỆCH XA NHẤT trong toàn ảnh làm điểm số tổng - 1 vùng lỗi nhỏ vẫn không bị "trung bình hoá" làm mất dấu
        bool isAnomaly = anomalyScore > threshold;

        // Confidence là chỉ số HEURISTIC (không phải xác suất thống kê chuẩn) thể hiện độ chắc chắn của phán
        // định IsAnomaly - càng lệch xa ngưỡng theo đúng hướng phán định thì Confidence càng gần 1.
        double ratio = anomalyScore / Math.Max(threshold, 1e-6);
        double confidence = isAnomaly ? Math.Clamp(1.0 - 1.0 / ratio, 0.0, 1.0) : Math.Clamp(1.0 - ratio, 0.0, 1.0);

        // ----- Bước 3: phóng to bản đồ nhiệt lên kích thước ImageSize để khoanh vùng lỗi mức pixel -----
        using Mat heatFull = new Mat();
        Cv2.Resize(heatSmall, heatFull, new Size(imgSize, imgSize), interpolation: InterpolationFlags.Linear);

        using Mat defectMask = new Mat();
        Cv2.Threshold(heatFull, defectMask, threshold, 255, ThresholdTypes.Binary);
        defectMask.ConvertTo(defectMask, MatType.CV_8U);

        Cv2.FindContours(defectMask, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

        // Quy đổi bounding box từ không gian ImageSize x ImageSize về đúng kích thước ảnh gốc đầu vào
        double scaleX = (double)src.Width / imgSize, scaleY = (double)src.Height / imgSize;
        var boundingBoxes = contours
            .Select(c => Cv2.BoundingRect(c))
            .Select(r => new Rect((int)(r.X * scaleX), (int)(r.Y * scaleY), Math.Max(1, (int)(r.Width * scaleX)), Math.Max(1, (int)(r.Height * scaleY))))
            .ToList();

        // ----- Bước 4: dựng ảnh trực quan hoá - heat map màu (JET) chồng lên ảnh gốc + khung đỏ quanh từng vùng lỗi -----
        Mat visualization = BuildVisualization(src, heatFull, boundingBoxes, threshold);

        _outIsAnomaly.Value = isAnomaly;
        _outAnomalyScore.Value = anomalyScore;
        _outThreshold.Value = threshold;
        _outConfidence.Value = confidence;
        _outDefectCount.Value = boundingBoxes.Count;
        _outBoundingBoxes.Value = boundingBoxes;
        _outHasVisualization.Value = true;
        _outVisualizationImage.Value = new MatVisionImage(visualization);

        context.Log($"AnomalyDetectionNode: score={anomalyScore:F2} (threshold={threshold:F2}) -> {(isAnomaly ? "ANOMALY" : "OK")}, defects={boundingBoxes.Count}, confidence={confidence:F2}");
    }

    /// <summary>Nạp model từ ModelPath, cache lại nếu đường dẫn không đổi so với lần Execute() trước - tránh đọc/parse JSON lại mỗi frame.</summary>
    private AnomalyModelData LoadModel(string path)
    {
        if (_cachedModel != null && string.Equals(_cachedModelPath, path, StringComparison.OrdinalIgnoreCase))
            return _cachedModel;

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new ToolExecutionException($"AnomalyDetectionNode: model file not found at '{path}'. Train a model first with AnomalyTrainingNode.");

        AnomalyModelData? model;
        try
        {
            model = JsonSerializer.Deserialize<AnomalyModelData>(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            throw new ToolExecutionException($"AnomalyDetectionNode: failed to parse model file '{path}': {ex.Message}");
        }

        if (model == null || model.Cells.Length == 0)
            throw new ToolExecutionException($"AnomalyDetectionNode: model file '{path}' is empty or invalid.");

        _cachedModel = model;
        _cachedModelPath = path;
        return model;
    }

    /// <summary>Khoảng cách Mahalanobis: d² = (x-mean)^T * InvCov * (x-mean), tính trực tiếp cho vector 2 chiều (mean, std).</summary>
    private static double MahalanobisDistance(double[] feature, CellGaussian cell)
    {
        double dx0 = feature[0] - cell.Mean[0];
        double dx1 = feature[1] - cell.Mean[1];
        double[][] inv = cell.InverseCovariance;

        double dSquared = dx0 * (inv[0][0] * dx0 + inv[0][1] * dx1) + dx1 * (inv[1][0] * dx0 + inv[1][1] * dx1);
        return Math.Sqrt(Math.Max(0, dSquared)); // Kẹp về 0 phòng sai số dấu phẩy động khiến giá trị âm rất nhỏ
    }

    private static Mat BuildVisualization(Mat src, Mat heatFull, List<Rect> boundingBoxes, double threshold)
    {
        using Mat srcColor = src.Channels() == 3 ? src.Clone() : ToColor(src);
        using Mat srcResized = new Mat();
        Cv2.Resize(srcColor, srcResized, new Size(heatFull.Cols, heatFull.Rows));

        // Chuẩn hóa heat map về 0-255 rồi tô màu JET (xanh=bình thường, đỏ=bất thường mạnh) - quy ước màu phổ biến cho heat map
        using Mat heatNorm = new Mat();
        Cv2.Normalize(heatFull, heatNorm, 0, 255, NormTypes.MinMax);
        heatNorm.ConvertTo(heatNorm, MatType.CV_8U);
        using Mat heatColor = new Mat();
        Cv2.ApplyColorMap(heatNorm, heatColor, ColormapTypes.Jet);

        Mat blended = new Mat();
        Cv2.AddWeighted(srcResized, 0.6, heatColor, 0.4, 0, blended);

        // Scale khung vẽ từ không gian ảnh gốc về đúng không gian đã resize để hiển thị (heatFull.Size)
        double scaleX = (double)heatFull.Cols / src.Width, scaleY = (double)heatFull.Rows / src.Height;
        foreach (var box in boundingBoxes)
        {
            var scaled = new Rect((int)(box.X * scaleX), (int)(box.Y * scaleY), (int)(box.Width * scaleX), (int)(box.Height * scaleY));
            Cv2.Rectangle(blended, scaled, new Scalar(0, 0, 255), thickness: 2);
        }

        return blended;
    }

    private static Mat ToColor(Mat src)
    {
        var color = new Mat();
        Cv2.CvtColor(src, color, src.Channels() == 4 ? ColorConversionCodes.BGRA2BGR : ColorConversionCodes.GRAY2BGR);
        return color;
    }
}