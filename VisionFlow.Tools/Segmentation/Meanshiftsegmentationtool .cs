// ==================== Vai trò chính:                Làm PHẲNG ảnh thành các vùng màu đồng nhất (color-flat regions) trong khi vẫn giữ ranh giới sắc nét giữa các vùng khác màu
// ==================== Thành phần / Class tiêu biểu: MeanShiftSegmentationTool
// ==================== Phụ thuộc vào:                OpenCvSharp (Cv2.PyrMeanShiftFiltering)
// ==================== Pattern / Kỹ thuật nổi bật:   Pyramidal Mean Shift Filtering - tự động tìm các "đỉnh
//                       mật độ" trong không gian màu (không cần khai báo trước số vùng như K-Means), xử lý
//                       qua nhiều tầng pyramid (MaxLevel) để tăng tốc trên ảnh lớn mà vẫn giữ ranh giới sắc nét

using System;
using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;

namespace VisionFlow.Tools.Segmentation; // Cùng thư mục với KmeansClusteringTool / GrabCutAlgorithmTool

/// <summary>
/// MeanShiftSegmentation: khác <see cref="KmeansClusteringTool"/> (gom về đúng K màu rời rạc, cần đoán
/// trước số cụm), Mean Shift TỰ ĐỘNG tìm số vùng phù hợp bằng cách gom các pixel quanh mỗi "đỉnh mật độ"
/// trong không gian màu thành 1 vùng phẳng - ranh giới giữa các vùng khác màu vẫn giữ SẮC NÉT (không mờ
/// như Gaussian Blur). Kết quả trông như tranh cartoon shading - lý tưởng làm bước tiền xử lý trước
/// Threshold/FindContours/BlobAnalysis, đặc biệt với vật liệu có gradient mượt (kim loại ánh kim, da) mà
/// K-Means dễ gây hiện tượng dải màu (banding).
/// </summary>
[ToolMetadata(
    "MeanShiftSegmentation",
    DisplayName = "Mean Shift Segmentation",
    Category = "Segmentation",
    Description = "Flatten the image into color-flat regions with sharp boundaries using Pyramidal Mean Shift Filtering")]
public sealed class MeanShiftSegmentationTool : VisionTool
{
    #region 1. Khai báo Port (In/Out)
    private readonly InputPort<IVisionImage> _imageMatrix;

    private readonly OutputPort<IVisionImage> _outImageMatrix;
    private readonly OutputPort<IVisionImage> _outInteractiveVisualization; // Luôn là canvas đen trống - giống KmeansClusteringTool, không có yếu tố hình học để vẽ overlay
    #endregion

    #region 2. Khai báo Parameter (Tab Mean Shift)
    private readonly ToolParameter<double> _spatialWindowRadius; // Bán kính cửa sổ không gian (sp) - pixel trong bán kính này được xét gom cụm cùng nhau
    private readonly ToolParameter<double> _colorWindowRadius;   // Bán kính cửa sổ màu (sr) - chênh lệch màu tối đa (đơn vị BGR) để coi là cùng vùng
    private readonly ToolParameter<int> _maxLevel;               // Số tầng pyramid - lớn hơn xử lý nhanh hơn trên ảnh lớn nhưng giảm chi tiết
    private readonly ToolParameter<int> _maxIterations;
    private readonly ToolParameter<double> _epsilon;

    // --- Tab Output ---
    private readonly ToolParameter<bool> _outputAsColorImage;
    #endregion

    public MeanShiftSegmentationTool()
    {
        _imageMatrix = AddInput<IVisionImage>("ImageMatrix", "Image Matrix");

        _outImageMatrix = AddOutput<IVisionImage>("ImageMatrix", "Image Matrix");
        _outInteractiveVisualization = AddOutput<IVisionImage>("InteractiveVisualization", "Interactive Visualization");

        // SpatialWindowRadius=20, ColorWindowRadius=20: nằm trong dải "cân bằng tốt" theo tài liệu (15-20 và 20-30)
        _spatialWindowRadius = AddParameter("SpatialWindowRadius", 20.0, "Spatial Window Radius", min: 1.0, max: 100.0, category: "Mean Shift", order: 1);
        _colorWindowRadius = AddParameter("ColorWindowRadius", 20.0, "Color Window Radius", min: 1.0, max: 100.0, category: "Mean Shift", order: 2);
        _maxLevel = AddParameter("MaxLevel", 1, "Max Level", min: 0, max: 4, category: "Mean Shift", order: 3);
        _maxIterations = AddParameter("MaxIterations", 5, "Max Iterations", min: 1, max: 50, category: "Mean Shift", order: 4);
        _epsilon = AddParameter("Epsilon", 1.0, "Epsilon", min: 0.1, max: 10.0, category: "Mean Shift", order: 5);

        _outputAsColorImage = AddParameter("OutputAsColorImage", true, "Output As Color Image", category: "Output", order: 1);
    }

    protected override void OnExecute(IToolContext context)
    {
        Mat original = _imageMatrix.Value!.AsMat();

        // ----- Bước 1: chuẩn hóa về BGR 3 kênh - Cv2.PyrMeanShiftFiltering bắt buộc ảnh CV_8UC3 -----
        using Mat bgr = ToColor(original);

        var criteria = new TermCriteria(CriteriaTypes.Eps | CriteriaTypes.MaxIter, _maxIterations.Value, _epsilon.Value);

        // ----- Bước 2: chạy Pyramidal Mean Shift Filtering -----
        Mat filtered = new Mat();
        Cv2.PyrMeanShiftFiltering(bgr, filtered, _spatialWindowRadius.Value, _colorWindowRadius.Value, _maxLevel.Value, criteria);

        // ----- Bước 3: xuất theo đúng OutputAsColorImage -----
        Mat outputImage;
        if (_outputAsColorImage.Value)
        {
            outputImage = filtered;
        }
        else
        {
            outputImage = new Mat();
            Cv2.CvtColor(filtered, outputImage, ColorConversionCodes.BGR2GRAY);
            filtered.Dispose();
        }

        // ----- Bước 4: InteractiveVisualization luôn là canvas đen trống theo đúng tài liệu -----
        Mat emptyOverlay = new Mat(original.Size(), MatType.CV_8UC3, Scalar.Black);

        _outImageMatrix.Value = new MatVisionImage(outputImage);
        _outInteractiveVisualization.Value = new MatVisionImage(emptyOverlay);

        context.Log($"MeanShiftSegmentation: sp={_spatialWindowRadius.Value}, sr={_colorWindowRadius.Value}, maxLevel={_maxLevel.Value}");
    }

    private static Mat ToColor(Mat src)
    {
        if (src.Channels() == 3) return src.Clone();
        var color = new Mat();
        Cv2.CvtColor(src, color, src.Channels() == 1 ? ColorConversionCodes.GRAY2BGR : ColorConversionCodes.BGRA2BGR);
        return color;
    }
}