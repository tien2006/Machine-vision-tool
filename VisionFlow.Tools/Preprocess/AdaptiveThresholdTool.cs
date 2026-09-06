// ==================== Vai trò chính:                Node threshold đa năng: gộp 4 phương pháp Manual/Otsu/Triangle/Adaptive trong 1 tool
// ==================== Thành phần / Class tiêu biểu: AdaptiveThresholdTool
// ==================== Phụ thuộc vào:                OpenCvSharp
// ==================== Pattern / Kỹ thuật nổi bật:   Strategy chọn thuật toán qua tham số (switch theo ThresholdMethod), Adaptive threshold cục bộ theo vùng (BlockSize x BlockSize)

using System;
using System.Diagnostics;        // Stopwatch: đo thời gian xử lý (ProcessingTime)
using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;  // Extension method .AsMat()

namespace VisionFlow.Tools.Preprocess; // Cùng nhóm "Filtering" với ThresholdTool/OtsuThresholdTool

/// <summary>
/// Tool threshold đa năng: kết hợp 4 phương pháp trong 1 node.
/// - Manual: ngưỡng cố định do người dùng đặt (giống ThresholdTool).
/// - Otsu: tự tính ngưỡng tối ưu toàn cục (giống OtsuThresholdTool).
/// - Triangle: biến thể của Otsu, tốt khi histogram lệch (1 đỉnh chính + đuôi dài).
/// - Adaptive: tính ngưỡng RIÊNG cho từng vùng nhỏ (BlockSize x BlockSize) -> xử lý tốt ảnh
///   ánh sáng không đều trong cùng 1 frame (vật bên trái sáng, bên phải tối) - điều mà
///   3 phương pháp còn lại (ngưỡng toàn cục) không xử lý được.
/// </summary>
[ToolMetadata("AdaptiveThreshold", DisplayName = "Adaptive Threshold", Category = "Filtering",
    Description = "Multi-method thresholding: Manual, Otsu, Triangle, or local Adaptive")]
public sealed class AdaptiveThresholdTool : VisionTool
{
    #region 1. Khai báo Port (In/Out)
    private readonly InputPort<IVisionImage> _input;              // Cổng vào: ảnh gốc (xám hoặc màu)
    private readonly OutputPort<IVisionImage> _output;             // Cổng ra: ảnh nhị phân kết quả
    private readonly OutputPort<double> _outCalculatedThreshold;   // Cổng ra: ngưỡng đã dùng (Manual/Otsu/Triangle); Adaptive luôn trả về 0
    private readonly OutputPort<string> _outMethodUsed;            // Cổng ra: tên phương pháp đã chạy (text, để log/hiển thị)
    private readonly OutputPort<double> _outProcessingTime;        // Cổng ra: thời gian xử lý tính bằng mili-giây
    #endregion

    #region 2. Khai báo Parameter theo từng Tab
    // ----- Tab Method: chọn phương pháp - quan trọng nhất -----
    private readonly ToolParameter<string> _thresholdMethod;

    // ----- Tab Manual Threshold (dùng khi ThresholdMethod = Manual/Otsu/Triangle) -----
    private readonly ToolParameter<double> _thresholdValue;
    private readonly ToolParameter<double> _maxValue;
    private readonly ToolParameter<ThresholdTypes> _thresholdType;

    // ----- Tab Adaptive Threshold (dùng khi ThresholdMethod = Adaptive) -----
    private readonly ToolParameter<AdaptiveThresholdTypes> _adaptiveMethod; // Mean (MeanC) hoặc Gaussian (GaussianC)
    private readonly ToolParameter<ThresholdTypes> _adaptiveType;          // OpenCV chỉ cho phép Binary hoặc BinaryInv ở chế độ Adaptive
    private readonly ToolParameter<int> _blockSize;                       // Kích thước vùng tính ngưỡng cục bộ, phải là số lẻ >= 3
    private readonly ToolParameter<double> _c;                             // Hằng số trừ vào ngưỡng cục bộ tính được (ngưỡng = mean - C)

    // ----- Tab Output: hiển thị -----
    private readonly ToolParameter<bool> _showThresholdValue; // In số ngưỡng lên ảnh output
    private readonly ToolParameter<bool> _outputAsColorImage; // Output thành BGR 3 kênh thay vì grayscale 1 kênh

    // ----- Tab Advanced: tiền xử lý + đảo kết quả -----
    private readonly ToolParameter<bool> _preprocessWithBlur; // Áp Gaussian blur trước khi threshold để giảm nhiễu hạt
    private readonly ToolParameter<int> _blurKernelSize;       // Kích thước kernel Gaussian, số lẻ >= 3
    private readonly ToolParameter<bool> _invertResult;        // Đảo kết quả CUỐI CÙNG (255 <-> 0), khác với AdaptiveType=BinaryInv (đảo NGAY LÚC threshold)
    #endregion

    public AdaptiveThresholdTool()
    {
        // ----- Port -----
        _input = AddInput<IVisionImage>("Image");
        _output = AddOutput<IVisionImage>("Image");
        _outCalculatedThreshold = AddOutput<double>("CalculatedThreshold");
        _outMethodUsed = AddOutput<string>("ThresholdMethod");
        _outProcessingTime = AddOutput<double>("ProcessingTime");

        // ----- Tab Method -----
        _thresholdMethod = AddChoiceParameter(
            "ThresholdMethod", "Manual",
            choices: new[] { "Manual", "Otsu", "Triangle", "Adaptive" },
            displayName: "Threshold Method", category: "Method", order: 1);

        // ----- Tab Manual Threshold -----
        _thresholdValue = AddParameter("ThresholdValue", 128.0, "Threshold Value", 0.0, 255.0, category: "Manual", order: 1);
        _maxValue = AddParameter("MaxValue", 255.0, "Max Value", 0.0, 255.0, category: "Manual", order: 2);
        _thresholdType = AddParameter("ThresholdType", ThresholdTypes.Binary, "Threshold Type", category: "Manual", order: 3);

        // ----- Tab Adaptive Threshold -----
        _adaptiveMethod = AddParameter("AdaptiveMethod", AdaptiveThresholdTypes.MeanC, "Adaptive Method", category: "Adaptive", order: 1);
        _adaptiveType = AddParameter("AdaptiveType", ThresholdTypes.Binary, "Adaptive Type", category: "Adaptive", order: 2);
        _blockSize = AddParameter("BlockSize", 11, "Block Size", 3, 999, category: "Adaptive", order: 3);
        _c = AddParameter("C", 2.0, "C", -100.0, 100.0, category: "Adaptive", order: 4);

        // ----- Tab Output -----
        _showThresholdValue = AddParameter("ShowThresholdValue", false, "Show Threshold Value", category: "Output", order: 1);
        _outputAsColorImage = AddParameter("OutputAsColorImage", false, "Output As Color Image", category: "Output", order: 2);

        // ----- Tab Advanced -----
        _preprocessWithBlur = AddParameter("PreprocessWithBlur", false, "Preprocess With Blur", category: "Advanced", order: 1);
        _blurKernelSize = AddParameter("BlurKernelSize", 5, "Blur Kernel Size", 3, 99, category: "Advanced", order: 2);
        _invertResult = AddParameter("InvertResult", false, "Invert Result", category: "Advanced", order: 3);
    }

    protected override void OnExecute(IToolContext context)
    {
        var stopwatch = Stopwatch.StartNew(); // Bắt đầu bấm giờ để tính ProcessingTime

        Mat src = _input.Value!.AsMat(); // Lấy ma trận ảnh gốc từ cổng vào

        // ----- Bước 1: Đảm bảo ảnh xử lý là ảnh xám (mọi thuật toán threshold đều cần ảnh 1 kênh) -----
        Mat gray;
        bool grayIsTemporary = false; // Đánh dấu để biết có cần Dispose ảnh xám tạm hay không
        if (src.Channels() == 1)
        {
            gray = src;
        }
        else
        {
            gray = new Mat();
            Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
            grayIsTemporary = true;
        }

        // ----- Bước 2 (tùy chọn): Tiền xử lý làm mờ Gaussian để giảm nhiễu hạt trước khi threshold -----
        Mat working = gray; // "working" là ảnh thực sự đem đi threshold (có thể là gray gốc hoặc bản đã blur)
        bool workingIsTemporary = false;
        if (_preprocessWithBlur.Value)
        {
            int blurK = ForceOdd(_blurKernelSize.Value, minValue: 3); // Kernel Gaussian bắt buộc phải là số lẻ >= 3
            working = new Mat();
            Cv2.GaussianBlur(gray, working, new Size(blurK, blurK), 0); // sigma = 0 -> OpenCV tự suy ra từ kích thước kernel
            workingIsTemporary = true;
        }

        // ----- Bước 3: Chạy đúng phương pháp threshold đã chọn ở Tab Method -----
        Mat dst = new Mat();          // Ma trận đích chứa ảnh nhị phân kết quả
        double calculatedThreshold = 0; // Mặc định 0 (áp dụng cho Adaptive - mỗi vùng có ngưỡng riêng, không có 1 con số đại diện)
        string methodUsed = _thresholdMethod.Value;

        switch (methodUsed)
        {
            case "Manual":
                // Ngưỡng cố định do người dùng đặt -> dùng nguyên ThresholdValue/MaxValue/ThresholdType đã cấu hình
                calculatedThreshold = Cv2.Threshold(working, dst, _thresholdValue.Value, _maxValue.Value, _thresholdType.Value);
                break;

            case "Otsu":
                // Otsu chỉ nhận Binary/BinaryInv -> nếu người dùng lỡ chọn Trunc/Tozero ở Tab Manual thì fallback về Binary
                var otsuBase = _thresholdType.Value == ThresholdTypes.BinaryInv ? ThresholdTypes.BinaryInv : ThresholdTypes.Binary;
                // Giá trị 0 truyền vào ThresholdValue sẽ bị OpenCV bỏ qua, nó tự tính ngưỡng tối ưu và trả về qua return
                calculatedThreshold = Cv2.Threshold(working, dst, 0, _maxValue.Value, otsuBase | ThresholdTypes.Otsu);
                break;

            case "Triangle":
                // Triangle cũng chỉ nhận Binary/BinaryInv, cơ chế tính ngưỡng khác Otsu (dựa vào hình tam giác trên histogram)
                var triangleBase = _thresholdType.Value == ThresholdTypes.BinaryInv ? ThresholdTypes.BinaryInv : ThresholdTypes.Binary;
                calculatedThreshold = Cv2.Threshold(working, dst, 0, _maxValue.Value, triangleBase | ThresholdTypes.Triangle);
                break;

            case "Adaptive":
                // Ngưỡng cục bộ: mỗi vùng BlockSize x BlockSize có 1 ngưỡng riêng = mean(vùng) - C (hoặc Gaussian-weighted mean - C)
                int blockSize = ForceOdd(_blockSize.Value, minValue: 3); // BlockSize bắt buộc là số lẻ >= 3
                Cv2.AdaptiveThreshold(working, dst, _maxValue.Value, _adaptiveMethod.Value, _adaptiveType.Value, blockSize, _c.Value);
                calculatedThreshold = 0; // Không có 1 ngưỡng đại diện chung cho toàn ảnh -> để 0 đúng như tài liệu mô tả
                break;

            default:
                // Phòng thủ: nếu vì lý do nào đó ThresholdMethod bị nhập sai tên -> báo lỗi rõ ràng thay vì chạy sai âm thầm
                throw new ToolExecutionException($"AdaptiveThresholdTool: unknown ThresholdMethod '{methodUsed}'.");
        }

        // ----- Bước 4 (tùy chọn): Đảo kết quả CUỐI CÙNG (khác với BinaryInv - cái đó đảo ngay lúc threshold) -----
        if (_invertResult.Value)
        {
            Cv2.BitwiseNot(dst, dst); // 255 <-> 0 toàn bộ ảnh nhị phân
        }

        // ----- Bước 5 (tùy chọn): In giá trị ngưỡng lên góc ảnh để tiện quan sát/debug trực quan -----
        if (_showThresholdValue.Value)
        {
            Cv2.PutText(dst, $"T={calculatedThreshold:F1}", new Point(10, 25),
                HersheyFonts.HersheySimplex, 0.7, Scalar.White, 2); // Vẽ chữ trắng, dày 2px, góc trên-trái ảnh
        }

        // ----- Bước 6 (tùy chọn): Xuất ảnh output dạng màu (BGR) thay vì ảnh xám 1 kênh -----
        if (_outputAsColorImage.Value)
        {
            Mat colorDst = new Mat();
            Cv2.CvtColor(dst, colorDst, ColorConversionCodes.GRAY2BGR); // Nhân bản 1 kênh xám thành 3 kênh B=G=R giống nhau
            dst.Dispose(); // Giải phóng ảnh xám gốc vì đã có bản màu thay thế
            dst = colorDst;
        }

        // ----- Dọn dẹp bộ nhớ unmanaged của các ảnh tạm đã tạo ra trong quá trình xử lý -----
        if (workingIsTemporary) working.Dispose();
        if (grayIsTemporary) gray.Dispose();

        stopwatch.Stop(); // Dừng bấm giờ

        // ----- Ghi kết quả ra các cổng output -----
        _output.Value = new MatVisionImage(dst);
        _outCalculatedThreshold.Value = calculatedThreshold;
        _outMethodUsed.Value = methodUsed;
        _outProcessingTime.Value = stopwatch.Elapsed.TotalMilliseconds;

        context.Log($"AdaptiveThreshold [{methodUsed}]: T={calculatedThreshold:F1}, time={_outProcessingTime.Value:F2}ms");
    }

    /// <summary>
    /// Hàm hỗ trợ: ép một số nguyên về số LẺ và không nhỏ hơn minValue.
    /// Dùng cho BlockSize và BlurKernelSize vì OpenCV bắt buộc các kích thước kernel này phải là số lẻ.
    /// </summary>
    private static int ForceOdd(int value, int minValue)
    {
        if (value < minValue) value = minValue;   // Ép không nhỏ hơn giá trị tối thiểu cho phép
        if (value % 2 == 0) value++;               // Nếu là số chẵn thì cộng thêm 1 để thành số lẻ
        return value;
    }
}