// ==================== Vai trò chính:                Gộp 6 phép toán hình thái học nâng cao (Open/Close/Gradient/TopHat/BlackHat/HitMiss) vào 1 node cấu hình linh hoạt
// ==================== Thành phần / Class tiêu biểu: MorphologyExTool
// ==================== Phụ thuộc vào:                OpenCvSharp
// ==================== Pattern / Kỹ thuật nổi bật:   Kernel động (chuẩn hoặc tự định nghĩa), Strategy chọn phép toán qua enum, Anchor/Border tùy chỉnh

using System;
using System.Diagnostics;        // Stopwatch: đo ProcessingTime
using System.Linq;
using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;

namespace VisionFlow.Tools.Preprocess; // Cùng nhóm "xử lý mức pixel" với DilateTool/ErodeTool, Category hiển thị UI là "Morphological"

/// <summary>
/// Tool hình thái học nâng cao: đóng gói 6 phép toán tổ hợp từ Erode/Dilate.
/// - Opening  = Erode rồi Dilate  -> xóa nhiễu nhỏ, giữ nguyên kích thước vật thể chính.
/// - Closing  = Dilate rồi Erode  -> lấp lỗ nhỏ bên trong vật, nối chi tiết đứt đoạn.
/// - Gradient = Dilate - Erode    -> chỉ giữ lại đường viền (biên) của vật thể.
/// - TopHat   = Ảnh gốc - Opening -> làm nổi bật chi tiết SÁNG nhỏ trên nền tối (VD: vết xước sáng).
/// - BlackHat = Closing - Ảnh gốc -> làm nổi bật chi tiết TỐI nhỏ trên nền sáng (VD: vết nứt/lỗ đen).
/// - HitMiss  = So khớp đồng thời hình dạng tiền cảnh VÀ nền -> tìm 1 hình dạng cụ thể chính xác cao
///   (chỉ có ý nghĩa đúng khi dùng Custom Kernel với giá trị -1 = "không quan tâm").
/// </summary>
[ToolMetadata("MorphologyEx", DisplayName = "Morphology Ex", Category = "Morphological",
    Description = "Advanced morphological operations: Opening, Closing, Gradient, TopHat, BlackHat, HitMiss")]
public sealed class MorphologyExTool : VisionTool
{
    #region 1. Khai báo Port (In/Out)
    private readonly InputPort<IVisionImage> _input;              // Cổng vào: ảnh nhị phân (thường sau Threshold) hoặc ảnh xám
    private readonly OutputPort<IVisionImage> _output;            // Cổng ra: ảnh sau khi xử lý (hoặc ảnh ghép gốc+kết quả nếu bật ShowOriginalAndResult)
    private readonly OutputPort<string> _outOperationInfo;        // Cổng ra: tên/mô tả phép toán vừa chạy
    private readonly OutputPort<int> _outKernelSize;              // Cổng ra: kích thước kernel THỰC TẾ đã dùng
    private readonly OutputPort<double> _outProcessingTime;       // Cổng ra: thời gian xử lý (mili-giây)
    #endregion

    #region 2. Khai báo Parameter theo từng Tab
    // ----- Tab Operation -----
    private readonly ToolParameter<MorphTypes> _operation;

    // ----- Tab Kernel -----
    private readonly ToolParameter<int> _kernelSize;
    private readonly ToolParameter<int> _kernelSizeX;
    private readonly ToolParameter<int> _kernelSizeY;
    private readonly ToolParameter<bool> _useCustomKernelSize;
    private readonly ToolParameter<MorphShapes> _kernelShape;

    // ----- Tab Advanced -----
    private readonly ToolParameter<int> _iterations;
    private readonly ToolParameter<int> _anchorX;
    private readonly ToolParameter<int> _anchorY;
    private readonly ToolParameter<bool> _useCustomAnchor;
    private readonly ToolParameter<BorderTypes> _borderType;
    private readonly ToolParameter<double> _borderValue;

    // ----- Tab Custom Kernel -----
    private readonly ToolParameter<bool> _useCustomKernel;
    private readonly ToolParameter<string> _customKernelData;

    // ----- Tab Output -----
    private readonly ToolParameter<bool> _showOriginalAndResult;
    private readonly ToolParameter<bool> _outputAsColorImage;
    #endregion

    public MorphologyExTool()
    {
        // ----- Port -----
        _input = AddInput<IVisionImage>("Image");
        _output = AddOutput<IVisionImage>("Image");
        _outOperationInfo = AddOutput<string>("OperationInfo");
        _outKernelSize = AddOutput<int>("KernelSize");
        _outProcessingTime = AddOutput<double>("ProcessingTime");

        // ----- Tab Operation -----
        _operation = AddParameter("Operation", MorphTypes.Open, "Operation", category: "Operation", order: 1);

        // ----- Tab Kernel -----
        _kernelSize = AddParameter("KernelSize", 3, "Kernel Size", 1, 99, category: "Kernel", order: 1);
        _kernelSizeX = AddParameter("KernelSizeX", 3, "Kernel Size X", 1, 99, category: "Kernel", order: 2);
        _kernelSizeY = AddParameter("KernelSizeY", 3, "Kernel Size Y", 1, 99, category: "Kernel", order: 3);
        _useCustomKernelSize = AddParameter("UseCustomKernelSize", false, "Use Custom Kernel Size", category: "Kernel", order: 4);
        _kernelShape = AddParameter("KernelShape", MorphShapes.Rect, "Kernel Shape", category: "Kernel", order: 5);

        // ----- Tab Advanced -----
        _iterations = AddParameter("Iterations", 1, "Iterations", 1, 50, category: "Advanced", order: 1);
        _anchorX = AddParameter("AnchorX", 0, "Anchor X", category: "Advanced", order: 2);
        _anchorY = AddParameter("AnchorY", 0, "Anchor Y", category: "Advanced", order: 3);
        _useCustomAnchor = AddParameter("UseCustomAnchor", false, "Use Custom Anchor", category: "Advanced", order: 4);
        _borderType = AddParameter("BorderType", BorderTypes.Constant, "Border Type", category: "Advanced", order: 5);
        _borderValue = AddParameter("BorderValue", 0.0, "Border Value", 0.0, 255.0, category: "Advanced", order: 6);

        // ----- Tab Custom Kernel -----
        _useCustomKernel = AddParameter("UseCustomKernel", false, "Use Custom Kernel", category: "Custom Kernel", order: 1);
        _customKernelData = AddParameter("CustomKernelData", "1,1,1;1,1,1;1,1,1", "Custom Kernel Data", category: "Custom Kernel", order: 2);

        // ----- Tab Output -----
        _showOriginalAndResult = AddParameter("ShowOriginalAndResult", false, "Show Original And Result", category: "Output", order: 1);
        _outputAsColorImage = AddParameter("OutputAsColorImage", false, "Output As Color Image", category: "Output", order: 2);
    }

    protected override void OnExecute(IToolContext context)
    {
        var stopwatch = Stopwatch.StartNew(); // Bắt đầu bấm giờ để tính ProcessingTime

        Mat src = _input.Value!.AsMat(); // Lấy ma trận ảnh gốc từ cổng vào

        // ----- Bước 1: Xây dựng kernel (nhân cấu trúc) theo cấu hình đang chọn -----
        Mat kernel = BuildKernel(out int reportedKernelSize);

        // ----- Bước 2: Xác định Anchor (tâm kernel) -----
        // Point(-1,-1) là quy ước đặc biệt của OpenCV nghĩa là "tự đặt tâm ở chính giữa kernel"
        Point anchor = _useCustomAnchor.Value ? new Point(_anchorX.Value, _anchorY.Value) : new Point(-1, -1);

        // ----- Bước 3: Chạy phép toán hình thái học đã chọn -----
        Mat dst = new Mat(); // Ma trận đích chứa kết quả
        Cv2.MorphologyEx(
            src, dst, _operation.Value, kernel,
            anchor: anchor,
            iterations: Math.Max(1, _iterations.Value),
            borderType: _borderType.Value,
            borderValue: Scalar.All(_borderValue.Value)); // Chỉ thực sự có tác dụng khi BorderType = Constant

        kernel.Dispose(); // Giải phóng kernel ngay sau khi dùng xong (không dùng using vì kernel được tạo trong helper riêng)

        // ----- Bước 4 (tùy chọn): Ghép ảnh gốc + ảnh kết quả nằm cạnh nhau để tiện so sánh/debug -----
        Mat finalImage;
        if (_showOriginalAndResult.Value)
        {
            finalImage = new Mat();
            // HConcat yêu cầu 2 ảnh cùng số hàng (Rows) và cùng kiểu dữ liệu -> MorphologyEx luôn giữ nguyên
            // kích thước và số kênh của ảnh gốc nên src và dst chắc chắn tương thích, ghép được trực tiếp
            Cv2.HConcat(new[] { src, dst }, finalImage);
            dst.Dispose(); // dst đã được "nhân bản" vào finalImage nên giải phóng bản gốc
        }
        else
        {
            finalImage = dst; // Không ghép -> dùng thẳng ảnh kết quả
        }

        // ----- Bước 5 (tùy chọn): Chuyển output sang ảnh màu (BGR) nếu đang là ảnh xám 1 kênh -----
        if (_outputAsColorImage.Value && finalImage.Channels() == 1)
        {
            Mat colorImage = new Mat();
            Cv2.CvtColor(finalImage, colorImage, ColorConversionCodes.GRAY2BGR);
            finalImage.Dispose();
            finalImage = colorImage;
        }

        stopwatch.Stop(); // Dừng bấm giờ

        // ----- Ghi kết quả ra các cổng output -----
        _output.Value = new MatVisionImage(finalImage);
        _outOperationInfo.Value = DescribeOperation(_operation.Value);
        _outKernelSize.Value = reportedKernelSize;
        _outProcessingTime.Value = stopwatch.Elapsed.TotalMilliseconds;

        context.Log($"MorphologyEx [{_operation.Value}]: kernel={reportedKernelSize}, time={_outProcessingTime.Value:F2}ms");
    }

    /// <summary>
    /// Xây dựng kernel (nhân cấu trúc) theo đúng thứ tự ưu tiên cấu hình:
    /// 1) UseCustomKernel=true  -> parse chuỗi CustomKernelData thành ma trận kernel tự định nghĩa.
    /// 2) UseCustomKernelSize=true -> dùng KernelSizeX/KernelSizeY (kernel không vuông, VD 5x3).
    /// 3) Mặc định -> dùng KernelSize vuông (VD 3x3, 5x5).
    /// </summary>
    private Mat BuildKernel(out int reportedKernelSize)
    {
        if (_useCustomKernel.Value)
        {
            Mat customKernel = ParseCustomKernelData(_customKernelData.Value);
            reportedKernelSize = customKernel.Cols; // Báo cáo bề rộng kernel tự định nghĩa (giả định kernel vuông thường gặp)
            return customKernel;
        }

        if (_useCustomKernelSize.Value)
        {
            int w = Math.Max(1, _kernelSizeX.Value);
            int h = Math.Max(1, _kernelSizeY.Value);
            reportedKernelSize = Math.Max(w, h); // Báo cáo cạnh lớn nhất khi kernel không vuông
            return Cv2.GetStructuringElement(_kernelShape.Value, new Size(w, h));
        }

        int size = Math.Max(1, _kernelSize.Value);
        reportedKernelSize = size;
        return Cv2.GetStructuringElement(_kernelShape.Value, new Size(size, size));
    }

    /// <summary>
    /// Parse chuỗi kernel tự định nghĩa dạng "1,1,1;1,1,1;1,1,1" (hàng phân tách bằng ';', giá trị phân tách bằng ',')
    /// thành ma trận kernel kiểu CV_8SC1 (số nguyên có dấu 8-bit).
    /// LƯU Ý: dùng kiểu CÓ DẤU (signed) vì phép HitMiss cần phân biệt 3 trạng thái: 1 = tiền cảnh bắt buộc,
    /// 0 = nền bắt buộc, -1 = "không quan tâm" (don't care). Với các phép Open/Close/Gradient/TopHat/BlackHat
    /// thông thường, chỉ cần dùng giá trị 0/1 như ví dụ trong tài liệu là đủ, kernel CV_8SC1 vẫn hoạt động đúng.
    /// </summary>
    private static Mat ParseCustomKernelData(string data)
    {
        if (string.IsNullOrWhiteSpace(data))
            throw new ToolExecutionException("MorphologyExTool: CustomKernelData is empty.");

        // Tách thành từng hàng theo dấu ';', bỏ hàng rỗng thừa (VD: có dấu ';' dư ở cuối chuỗi)
        string[] rowsText = data.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (rowsText.Length == 0)
            throw new ToolExecutionException("MorphologyExTool: CustomKernelData has no valid rows.");

        // Parse hàng đầu tiên trước để biết số cột chuẩn, các hàng sau phải khớp số cột này
        int[][] rows = rowsText.Select(row =>
            row.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
               .Select(v => int.Parse(v))
               .ToArray()
        ).ToArray();

        int colCount = rows[0].Length;
        if (rows.Any(r => r.Length != colCount))
            throw new ToolExecutionException("MorphologyExTool: CustomKernelData rows must have the same number of columns.");

        Mat kernel = new Mat(rows.Length, colCount, MatType.CV_8SC1); // Tạo ma trận rows x cols kiểu số nguyên có dấu 8-bit
        for (int r = 0; r < rows.Length; r++)
            for (int c = 0; c < colCount; c++)
                kernel.Set(r, c, (sbyte)rows[r][c]); // Ghi từng phần tử vào đúng vị trí [hàng, cột]

        return kernel;
    }

    /// <summary>Trả về chuỗi mô tả dễ hiểu cho phép toán đang chạy, phục vụ log/hiển thị HMI.</summary>
    private static string DescribeOperation(MorphTypes op) => op switch
    {
        MorphTypes.Open => "Opening (Erode -> Dilate) - remove small noise, keep object size",
        MorphTypes.Close => "Closing (Dilate -> Erode) - fill small holes, connect broken parts",
        MorphTypes.Gradient => "Gradient (Dilate - Erode) - highlight object boundary/edge",
        MorphTypes.TopHat => "TopHat (Src - Opening) - highlight bright details on dark background",
        MorphTypes.BlackHat => "BlackHat (Closing - Src) - highlight dark details on bright background",
        MorphTypes.HitMiss => "HitMiss - match exact shape pattern (requires Custom Kernel with -1 don't-care values)",
        _ => op.ToString()
    };
}