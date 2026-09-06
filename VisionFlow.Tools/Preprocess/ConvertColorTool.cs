// ==================== Vai trò chính:                Các bước tiền xử lý ảnh trước khi đo/tìm kiếm
// ==================== Thành phần / Class tiêu biểu: ConvertColorTool, ThresholdTool
// ==================== Phụ thuộc vào:                OpenCvSharp
// ==================== Pattern / Kỹ thuật nổi bật:   Pipeline tiền xử lý (Color space, Binary threshold)

using System;
using OpenCvSharp; // Sử dụng để gọi hàm biến đổi không gian màu Cv2.CvtColor[cite: 4]
using VisionFlow.Core.Imaging; // Sử dụng interface định dạng ảnh toàn hệ thống[cite: 4]
using VisionFlow.Core.Ports; // Quản lý cấu trúc cổng vào ra dữ liệu Generic[cite: 4]
using VisionFlow.Core.Tools; // Kéo vào cấu trúc lớp cơ sở VisionTool[cite: 4]
using VisionFlow.Tools.Imaging; // Sử dụng phương thức mở rộng mở hộp .AsMat()[cite: 4]

namespace VisionFlow.Tools.Preprocess; // Thuộc phân hệ tiền xử lý hình ảnh nâng cao[cite: 4]

/// <summary>
/// Tool xử lý chuyển đổi không gian màu của ảnh đầu vào (ví dụ: chuyển từ ảnh màu BGR sang ảnh xám Grayscale)[cite: 4].
/// </summary>
[ToolMetadata("ConvertColor", DisplayName = "Convert Color", Category = "Preprocessing", Description = "Color space conversion")] // Gắn nhãn phân loại thuật toán[cite: 4]
public sealed class ConvertColorTool : VisionTool
{
    private readonly InputPort<IVisionImage> _input; // Cổng vào nhận ảnh gốc cần biến đổi[cite: 4]
    private readonly ToolParameter<ColorConversionCodes> _conversionCode; // Tham số cấu hình mã chuyển đổi không gian màu[cite: 4]
    private readonly OutputPort<IVisionImage> _output; // Cổng xuất ảnh sau khi chuyển đổi thành công[cite: 4]

    public ConvertColorTool()
    {
        _input = AddInput<IVisionImage>("Image"); // Tạo cổng vào nhận ảnh[cite: 4]
        _conversionCode = AddParameter("Conversion", ColorConversionCodes.BGR2GRAY, "Conversion Code",category: "Processing",order: 1); // Mặc định cấu hình chuyển sang ảnh xám[cite: 4]
        _output = AddOutput<IVisionImage>("Output"); // Tạo cổng ra xuất ảnh[cite: 4]
    }

    protected override void OnExecute(IToolContext context)
    {
        Mat src = _input.Value!.AsMat(); // Mở hộp lấy ma trận ảnh OpenCV, dấu '!' cam kết dữ liệu đã được pipeline validate[cite: 4]
        Mat dst = new Mat(); // Khởi tạo ma trận kết quả trống[cite: 4]

        Cv2.CvtColor(src, dst, _conversionCode.Value); // Thực thi thuật toán biến đổi không gian màu native của OpenCV[cite: 4]

        _output.Value = new MatVisionImage(dst); // Bọc kết quả vào adapter hệ thống và đẩy ra cổng downstream[cite: 4]
        context.Log($"Color converted using code: {_conversionCode.Value}"); // Log vết chẩn đoán thuật toán[cite: 4]
    }
}