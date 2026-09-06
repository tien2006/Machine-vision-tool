// ==================== Vai trò chính:                Tự động tính ngưỡng nhị phân hóa tối ưu bằng thuật toán Otsu (không cần dò tay như ThresholdTool)
// ==================== Thành phần / Class tiêu biểu: OtsuThresholdTool
// ==================== Phụ thuộc vào:                OpenCvSharp
// ==================== Pattern / Kỹ thuật nổi bật:   Otsu's method — tối đa hoá phương sai liên lớp (inter-class variance) trên histogram ảnh

using System;
using OpenCvSharp;
using VisionFlow.Core.Imaging;   // Làm việc với kiểu dữ liệu ảnh trừu tượng IVisionImage
using VisionFlow.Core.Ports;     // InputPort<T>, OutputPort<T>
using VisionFlow.Core.Tools;     // VisionTool, ToolMetadataAttribute, IToolContext
using VisionFlow.Tools.Imaging;  // Extension method .AsMat() để mở hộp IVisionImage ra Mat của OpenCV

namespace VisionFlow.Tools.Preprocess; // Cùng nhóm "Filtering" với ThresholdTool, đặt chung thư mục Preprocess

/// <summary>
/// Tool nhị phân hóa ảnh TỰ ĐỘNG bằng thuật toán Otsu.
/// Khác với ThresholdTool (người dùng phải tự dò ThresholdValue bằng tay), Otsu tự động phân tích
/// histogram của ảnh và tìm ra ngưỡng làm phương sai giữa 2 lớp pixel "nền" và "vật" là lớn nhất.
/// Chỉ chính xác khi histogram ảnh có 2 đỉnh rõ ràng (bimodal) — nếu ánh sáng không đều, nên dùng
/// AdaptiveThreshold (sẽ làm ở bước tiếp theo) thay vì Otsu.
/// </summary>
[ToolMetadata("OtsuThreshold", DisplayName = "Otsu Threshold", Category = "Filtering",
    Description = "Automatic binary thresholding using Otsu's method")] // Gắn nhãn để Registry tự quét và đăng ký node này lên UI Palette
public sealed class OtsuThresholdTool : VisionTool // Lớp kín thực thi công cụ threshold tự động, kế thừa từ VisionTool
{
    // ==================== KHAI BÁO PORT & PARAMETER ====================
    private readonly InputPort<IVisionImage> _input;        // Cổng vào: nhận ảnh gốc (xám hoặc màu) từ tool phía trước
    private readonly ToolParameter<bool> _binaryInvert;      // Tham số: có đảo cực kết quả hay không
    private readonly OutputPort<IVisionImage> _output;       // Cổng ra: ảnh nhị phân kết quả
    private readonly OutputPort<double> _thresholdValue;     // Cổng ra: giá trị ngưỡng mà Otsu tự tính được (để log/debug/so sánh)

    public OtsuThresholdTool()
    {
        // ----- Khai báo cổng vào -----
        _input = AddInput<IVisionImage>("Image"); // Nhận ảnh từ tool phía trước (ví dụ GrabImage, ConvertColor)

        // ----- Khai báo tham số cấu hình (hiển thị lên UI Properties Panel) -----
        _binaryInvert = AddParameter("BinaryInvert", false, "Binary Invert",
            category: "Threshold", order: 1);
        // Mặc định false: pixel sáng hơn ngưỡng -> trắng (giống ThresholdTypes.Binary).
        // Bật true khi vật tối trên nền sáng và muốn vật ra trắng (giống ThresholdTypes.BinaryInv).

        // ----- Khai báo cổng ra -----
        _output = AddOutput<IVisionImage>("Image"); // Ảnh nhị phân kết quả, đặt tên "Image" đồng bộ input để dễ nối dây sang tool sau
        _thresholdValue = AddOutput<double>("ThresholdValue"); // Ngưỡng Otsu tính được, hữu ích để log hoặc đưa vào CompareTool kiểm tra chất lượng ánh sáng
    }

    protected override void OnExecute(IToolContext context)
    {
        Mat src = _input.Value!.AsMat(); // Mở hộp lấy ma trận ảnh gốc từ cổng vào (dấu '!' vì ValidateInputs() đã đảm bảo không null)

        // Otsu chỉ hoạt động đúng trên ảnh 1 kênh (grayscale) -> nếu ảnh đầu vào là ảnh màu (3 kênh) thì tự convert sang xám trước
        Mat gray;
        bool grayIsTemporary = false; // Cờ đánh dấu ảnh xám có phải tạo mới hay không, để biết có cần Dispose thủ công hay không (tránh rò rỉ bộ nhớ unmanaged)

        if (src.Channels() == 1)
        {
            gray = src; // Ảnh gốc đã là ảnh xám sẵn -> dùng thẳng, không tạo bản sao thừa
        }
        else
        {
            gray = new Mat(); // Tạo ma trận tạm để chứa kết quả chuyển đổi
            Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY); // Chuyển ảnh màu BGR sang ảnh xám 1 kênh
            grayIsTemporary = true; // Đánh dấu để nhớ giải phóng sau khi dùng xong
        }

        Mat dst = new Mat(); // Ma trận đích chứa ảnh nhị phân kết quả (sẽ được OpenCV cấp phát bộ nhớ bên trong hàm Threshold)

        // Xác định kiểu ngưỡng cơ bản: Binary (vật sáng -> trắng) hoặc BinaryInv (vật tối -> trắng)
        ThresholdTypes baseType = _binaryInvert.Value ? ThresholdTypes.BinaryInv : ThresholdTypes.Binary;

        // Gọi Cv2.Threshold với cờ OR thêm ThresholdTypes.Otsu:
        // - Tham số ThresholdValue truyền vào (giá trị 0 ở đây) sẽ BỊ BỎ QUA hoàn toàn.
        // - OpenCV tự phân tích histogram của "gray", quét toàn bộ 256 mức xám để tìm ngưỡng T
        //   sao cho phương sai liên lớp (giữa nhóm pixel nền và nhóm pixel vật) là LỚN NHẤT.
        // - Hàm trả về (return) chính là giá trị ngưỡng T mà nó vừa tự tính được.
        double otsuThresh = Cv2.Threshold(gray, dst, 0, 255, baseType | ThresholdTypes.Otsu);

        if (grayIsTemporary) gray.Dispose(); // Giải phóng vùng nhớ unmanaged của ảnh xám tạm nếu có tạo ra ở bước convert phía trên

        _output.Value = new MatVisionImage(dst); // Đóng gói ảnh nhị phân kết quả vào adapter hệ thống và đẩy ra cổng downstream
        _thresholdValue.Value = otsuThresh; // Đẩy giá trị ngưỡng Otsu tự tính được ra cổng ra để tool sau (hoặc log) sử dụng

        context.Log($"Otsu threshold calculated: {otsuThresh:F1}"); // Ghi nhật ký giá trị ngưỡng tự động tìm được, phục vụ debug/chẩn đoán
    }
}