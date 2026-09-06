// ==================== Vai trò chính:                Phát hiện biên Canny — ảnh nhị phân cạnh mảnh, ít nhiễu giả
// ==================== Thành phần / Class tiêu biểu: CannyEdgeDetectionTool
// ==================== Phụ thuộc vào:                OpenCvSharp (Cv2.Canny, GaussianBlur, Threshold Otsu)
// ==================== Pattern / Kỹ thuật nổi bật:   Canny hysteresis + (tuỳ chọn) Gauss trước + Otsu auto-threshold

using System;
using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;

namespace VisionFlow.Tools.Preprocess;

/// <summary>
/// Canny: tìm biên theo gradient, giữ cạnh mạnh, nối cạnh yếu, loại nhiễu.
/// Nên nối ảnh xám đã Smooth/Bilateral. Xuất ảnh biên + EdgeCount + EdgePercentage (nối CompareTool OK/NG).
/// </summary>
[ToolMetadata(
    "CannyEdgeDetection",
    DisplayName = "Canny Edge Detection",
    Category = "Preprocessing",
    Description = "Detect thin object edges with Canny; outputs edge image, count and percentage")]
public sealed class CannyEdgeDetectionTool : VisionTool
{
    #region 1. Port
    private readonly InputPort<IVisionImage> _input;
    private readonly OutputPort<IVisionImage> _output;          // Ảnh biên (binary hoặc BGR nếu bật OutputAsColorImage)
    private readonly OutputPort<int> _edgeCount;                // Số pixel biên
    private readonly OutputPort<double> _edgePercentage;        // % diện tích ảnh là biên
    #endregion

    #region 2. Parameter — khớp tài liệu thuật toán
    private readonly ToolParameter<double> _lowThreshold;       // Ngưỡng thấp: cạnh yếu hơn mức này bị loại
    private readonly ToolParameter<double> _highThreshold;      // Ngưỡng cao: cạnh mạnh được giữ
    private readonly ToolParameter<bool> _useAutoThreshold;     // true = tự lấy ngưỡng bằng Otsu
    private readonly ToolParameter<bool> _enableGaussianBlur;   // Làm mờ trước Canny
    private readonly ToolParameter<int> _gaussianKernelSize;    // Kernel Gauss (số lẻ)
    private readonly ToolParameter<double> _gaussianSigma;      // 0 = OpenCV tự tính
    private readonly ToolParameter<int> _sobelKernelSize;       // Aperture Sobel trong Canny: 3 / 5 / 7
    private readonly ToolParameter<bool> _l2Gradient;           // true = magnitude Euclidean (chính xác hơn)
    private readonly ToolParameter<bool> _outputAsColorImage;   // Binary → BGR để overlay
    private readonly ToolParameter<bool> _invertEdges;          // Đảo: cạnh đen, nền trắng
    #endregion

    public CannyEdgeDetectionTool()
    {
        _input = AddInput<IVisionImage>("Image");
        _output = AddOutput<IVisionImage>("Image");
        _edgeCount = AddOutput<int>("EdgeCount");
        _edgePercentage = AddOutput<double>("EdgePercentage");

        _lowThreshold = AddParameter("LowThreshold", 50.0, "Low Threshold", 0.0, 255.0, category: "Thresholds", order: 1);
        _highThreshold = AddParameter("HighThreshold", 150.0, "High Threshold", 0.0, 255.0, category: "Thresholds", order: 2);
        _useAutoThreshold = AddParameter("UseAutoThreshold", false, "Use Auto Threshold", category: "Thresholds", order: 3);

        _enableGaussianBlur = AddParameter("EnableGaussianBlur", true, "Enable Gaussian Blur", category: "Preprocessing", order: 1);
        _gaussianKernelSize = AddParameter("GaussianKernelSize", 5, "Gaussian Kernel Size", 1, 31, category: "Preprocessing", order: 2);
        _gaussianSigma = AddParameter("GaussianSigma", 0.0, "Gaussian Sigma", 0.0, 20.0, category: "Preprocessing", order: 3);

        _sobelKernelSize = AddParameter("SobelKernelSize", 3, "Sobel Kernel Size", 3, 7, category: "Gradient", order: 1);
        _l2Gradient = AddParameter("L2Gradient", false, "L2 Gradient", category: "Gradient", order: 2);

        _outputAsColorImage = AddParameter("OutputAsColorImage", false, "Output As Color Image", category: "Output", order: 1);
        _invertEdges = AddParameter("InvertEdges", false, "Invert Edges", category: "Output", order: 2);
    }

    protected override void OnExecute(IToolContext context)
    {
        Mat src = _input.Value!.AsMat();

        // ----- 1. Canny cần ảnh xám 8-bit -----
        using Mat gray = new Mat();
        if (src.Channels() == 1)
            src.CopyTo(gray);
        else
            Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);

        // ----- 2. (Tuỳ chọn) GaussianBlur giảm nhiễu trước khi tính gradient -----
        using Mat work = new Mat();
        if (_enableGaussianBlur.Value)
        {
            int gk = Math.Max(1, _gaussianKernelSize.Value);
            if (gk % 2 == 0) gk += 1;           // Kernel Gauss phải lẻ
            Cv2.GaussianBlur(gray, work, new Size(gk, gk), _gaussianSigma.Value, _gaussianSigma.Value, BorderTypes.Reflect101);
        }
        else
        {
            gray.CopyTo(work);
        }

        // ----- 3. Ngưỡng hysteresis: tay hoặc Otsu (high = otsu, low = 0.5 * otsu) -----
        double low = _lowThreshold.Value;
        double high = _highThreshold.Value;
        if (_useAutoThreshold.Value)
        {
            using Mat dummy = new Mat();        // Threshold Otsu bắt buộc có Mat đích, không dùng kết quả nhị phân này
            double otsu = Cv2.Threshold(work, dummy, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
            high = otsu;
            low = otsu * 0.5;
        }
        if (high < low)                         // OpenCV yêu cầu threshold2 >= threshold1 về mặt hysteresis
        {
            (low, high) = (high, low);
        }

        // ----- 4. Aperture Sobel chỉ nhận 3, 5, 7 -----
        int sobel = _sobelKernelSize.Value;
        if (sobel < 3) sobel = 3;
        if (sobel > 7) sobel = 7;
        if (sobel % 2 == 0) sobel += 1;         // 4 → 5, 6 → 7

        Mat edges = new Mat();
        Cv2.Canny(work, edges, low, high, sobel, _l2Gradient.Value);

        // ----- 5. Đảo màu biên nếu cần -----
        if (_invertEdges.Value)
            Cv2.BitwiseNot(edges, edges);       // Trắng↔đen tại chỗ

        // ----- 6. Thống kê pixel biên (đếm trên ảnh binary trước khi convert màu) -----
        // Invert rồi thì CountNonZero đếm nền trắng — đo trên bản chưa invert thì ổn định hơn.
        // Ở đây đếm SAU invert vì đó là ảnh user nhìn thấy; nếu Invert=true, EdgeCount = pixel nền trắng.
        int count = Cv2.CountNonZero(edges);
        double percent = 100.0 * count / (edges.Rows * edges.Cols);

        Mat dst;
        if (_outputAsColorImage.Value)
        {
            dst = new Mat();
            Cv2.CvtColor(edges, dst, ColorConversionCodes.GRAY2BGR);
            edges.Dispose();                    // Không giữ Mat binary nữa
        }
        else
        {
            dst = edges;
        }

        _output.Value = new MatVisionImage(dst);
        _edgeCount.Value = count;
        _edgePercentage.Value = percent;

        context.Log($"Canny: low={low:F1}, high={high:F1}, auto={_useAutoThreshold.Value}, edges={count} ({percent:F2}%)");
    }
}