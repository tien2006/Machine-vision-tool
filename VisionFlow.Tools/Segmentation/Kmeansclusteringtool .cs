// ==================== Vai trò chính:                Giảm số lượng màu trong ảnh xuống còn K màu đại diện (color quantization) - tiền xử lý mạnh cho segmentation
// ==================== Thành phần / Class tiêu biểu: KmeansClusteringTool
// ==================== Phụ thuộc vào:                OpenCvSharp (Cv2.Kmeans)
// ==================== Pattern / Kỹ thuật nổi bật:   Reshape ảnh 3 kênh thành ma trận (H*W) hàng x 3 cột
//                       (mỗi hàng = 1 pixel trong không gian màu BGR) để đưa vào Cv2.Kmeans như bài toán
//                       phân cụm điểm dữ liệu thông thường, sau đó reshape ngược lại đúng kích thước ảnh gốc

using System;
using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;

namespace VisionFlow.Tools.Segmentation; // Cùng thư mục với GrabCutAlgorithmTool / WatershedAlgorithmTool

/// <summary>Cách khởi tạo K centroid ban đầu cho K-Means.</summary>
public enum KmeansInitialization
{
    PpCenters,     // K-Means++ (mặc định, khuyến nghị) - chọn centroid ban đầu cách xa nhau, hội tụ nhanh và ổn định
    RandomCenters  // Chọn centroid hoàn toàn ngẫu nhiên - nhanh hơn 1 chút nhưng kém ổn định giữa các lần chạy
}

/// <summary>
/// KmeansClustering: giảm hàng triệu màu của ảnh xuống còn đúng K màu đại diện - mỗi pixel được thay bằng
/// màu centroid của cụm gần nhất. Không cần đặt ngưỡng thủ công như Threshold, tool TỰ "gom" các pixel màu
/// giống nhau lại. Thường dùng làm bước đơn giản hoá ảnh trước khi Threshold/BlobAnalysis tách từng "lớp màu".
/// </summary>
[ToolMetadata(
    "KmeansClustering",
    DisplayName = "K-Means Clustering",
    Category = "Segmentation",
    Description = "Reduce the image to K representative colors via K-Means clustering (color quantization)")]
public sealed class KmeansClusteringTool : VisionTool
{
    #region 1. Khai báo Port (In/Out)
    private readonly InputPort<IVisionImage> _imageMatrix;

    private readonly OutputPort<IVisionImage> _outImageMatrix;
    private readonly OutputPort<IVisionImage> _outLabelMatrix; // CV_32SC1 - chỉ số cụm (0..K-1) của từng pixel, bọc trong IVisionImage giống quy ước ở WatershedAlgorithmTool
    private readonly OutputPort<IReadOnlyList<Vec3b>> _outClusterCenters;
    private readonly OutputPort<IReadOnlyList<int>> _outClusterPixelCounts;
    private readonly OutputPort<double> _outCompactness;
    private readonly OutputPort<IVisionImage> _outInteractiveVisualization; // Luôn là canvas đen trống - K-Means không có yếu tố hình học để vẽ overlay
    #endregion

    #region 2. Khai báo Parameter
    // --- Tab Clustering ---
    private readonly ToolParameter<int> _k;
    private readonly ToolParameter<int> _attempts;
    private readonly ToolParameter<int> _maxIterations;
    private readonly ToolParameter<double> _epsilon;
    private readonly ToolParameter<KmeansInitialization> _initialization;

    // --- Tab Output ---
    private readonly ToolParameter<bool> _outputAsColorImage;
    #endregion

    public KmeansClusteringTool()
    {
        _imageMatrix = AddInput<IVisionImage>("ImageMatrix", "Image Matrix");

        _outImageMatrix = AddOutput<IVisionImage>("ImageMatrix", "Image Matrix");
        _outLabelMatrix = AddOutput<IVisionImage>("LabelMatrix", "Label Matrix");
        _outClusterCenters = AddOutput<IReadOnlyList<Vec3b>>("ClusterCenters", "Cluster Centers");
        _outClusterPixelCounts = AddOutput<IReadOnlyList<int>>("ClusterPixelCounts", "Cluster Pixel Counts");
        _outCompactness = AddOutput<double>("Compactness", "Compactness");
        _outInteractiveVisualization = AddOutput<IVisionImage>("InteractiveVisualization", "Interactive Visualization");

        // K=3: mặc định theo tài liệu - phù hợp 90% bài toán (nền - vật thể - bóng đổ)
        _k = AddParameter("K", 3, "K", min: 1, max: 256, category: "Clustering", order: 1);
        _attempts = AddParameter("Attempts", 5, "Attempts", min: 1, max: 50, category: "Clustering", order: 2);
        _maxIterations = AddParameter("MaxIterations", 100, "Max Iterations", min: 1, max: 1000, category: "Clustering", order: 3);
        _epsilon = AddParameter("Epsilon", 1.0, "Epsilon", min: 0.01, max: 10.0, category: "Clustering", order: 4);
        _initialization = AddParameter("Initialization", KmeansInitialization.PpCenters, "Initialization", category: "Clustering", order: 5);

        _outputAsColorImage = AddParameter("OutputAsColorImage", true, "Output As Color Image", category: "Output", order: 1);
    }

    protected override void OnExecute(IToolContext context)
    {
        Mat original = _imageMatrix.Value!.AsMat();
        int width = original.Width, height = original.Height;

        // ----- Bước 1: chuẩn hóa về BGR 3 kênh (grayscale VÀ BGRA đều tự chuyển về BGR theo tài liệu) -----
        using Mat bgr = ToColor(original);

        // ----- Bước 2: reshape ảnh thành ma trận mẫu (H*W) hàng x 3 cột (BGR), kiểu CV_32F theo yêu cầu Cv2.Kmeans -----
        using Mat floatImg = new Mat();
        bgr.ConvertTo(floatImg, MatType.CV_32F);
        Mat samples = floatImg.Reshape(1, width * height); // Gộp 3 kênh thành 3 cột của 1 kênh duy nhất - mỗi hàng = 1 pixel trong không gian màu BGR

        int k = Math.Min(_k.Value, width * height); // K không thể lớn hơn tổng số pixel có trong ảnh
        var criteria = new TermCriteria(CriteriaTypes.Eps | CriteriaTypes.MaxIter, _maxIterations.Value, _epsilon.Value);
        var flags = _initialization.Value == KmeansInitialization.PpCenters ? KMeansFlags.PpCenters : KMeansFlags.RandomCenters;

        using Mat labels = new Mat();
        using Mat centers = new Mat();

        // ----- Bước 3: chạy K-Means - centers trả về là ma trận K hàng x 3 cột (tâm BGR của mỗi cụm) -----
        double compactness = Cv2.Kmeans(samples, k, labels, criteria, _attempts.Value, flags, centers);

        // ----- Bước 4: chuyển centers (float) về Vec3b[K] để dễ dùng khi tô lại ảnh và xuất ClusterCenters -----
        var clusterColors = new Vec3b[k];
        for (int i = 0; i < k; i++)
        {
            byte b = ClampToByte(centers.At<float>(i, 0));
            byte g = ClampToByte(centers.At<float>(i, 1));
            byte r = ClampToByte(centers.At<float>(i, 2));
            clusterColors[i] = new Vec3b(b, g, r);
        }

        // Gán mỗi cụm 1 mức xám RIÊNG BIỆT cách đều nhau (KHÔNG suy từ màu BGR thật của cụm, để tránh 2 cụm
        // màu khác nhau nhưng độ sáng gần nhau bị trùng mức xám, mất đi ý nghĩa "K lớp phân biệt" khi xuất grayscale).
        var grayLevels = new byte[k];
        for (int i = 0; i < k; i++)
            grayLevels[i] = (byte)(k <= 1 ? 0 : i * 255 / (k - 1));

        // ----- Bước 5: reshape labels (H*W x 1) ngược lại thành ma trận H x W - đúng thứ tự pixel gốc -----
        using Mat labelsInt = new Mat();
        labels.ConvertTo(labelsInt, MatType.CV_32S);
        Mat labelMatrix = labelsInt.Reshape(1, height);

        // ----- Bước 6: dựng ảnh output + đếm số pixel mỗi cụm trong CÙNG 1 lượt quét (tránh quét ảnh 2 lần) -----
        Mat outputImage = _outputAsColorImage.Value
            ? new Mat(height, width, MatType.CV_8UC3)
            : new Mat(height, width, MatType.CV_8UC1);

        var pixelCounts = new int[k];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int label = labelMatrix.At<int>(y, x);
                pixelCounts[label]++;

                if (_outputAsColorImage.Value)
                    outputImage.Set(y, x, clusterColors[label]);
                else
                    outputImage.Set(y, x, grayLevels[label]);
            }
        }

        // ----- Bước 7: InteractiveVisualization luôn là canvas đen trống theo đúng tài liệu (không có overlay để vẽ) -----
        Mat emptyOverlay = new Mat(height, width, MatType.CV_8UC3, Scalar.Black);

        _outImageMatrix.Value = new MatVisionImage(outputImage);
        _outLabelMatrix.Value = new MatVisionImage(labelMatrix);
        _outClusterCenters.Value = clusterColors;
        _outClusterPixelCounts.Value = pixelCounts;
        _outCompactness.Value = compactness;
        _outInteractiveVisualization.Value = new MatVisionImage(emptyOverlay);

        context.Log($"KmeansClustering: K={k}, attempts={_attempts.Value}, compactness={compactness:F1}, " +
                    $"largest cluster={pixelCounts.Max()}px, smallest={pixelCounts.Min()}px");
    }

    private static byte ClampToByte(float value) => (byte)Math.Clamp(Math.Round(value), 0, 255);

    private static Mat ToColor(Mat src)
    {
        if (src.Channels() == 3) return src.Clone();
        var color = new Mat();
        Cv2.CvtColor(src, color, src.Channels() == 1 ? ColorConversionCodes.GRAY2BGR : ColorConversionCodes.BGRA2BGR);
        return color;
    }
}