// ==================== Vai trò chính:                Các bước tiền xử lý ảnh trước khi đo/tìm kiếm
// ==================== Thành phần / Class tiêu biểu: ConvertColorTool, ThresholdTool
// ==================== Phụ thuộc vào:                OpenCvSharp
// ==================== Pattern / Kỹ thuật nổi bật:   Pipeline tiền xử lý (Color space, Binary threshold)

using System;
using OpenCvSharp; // Sử dụng thuật toán phân ngưỡng nhị phân Cv2.Threshold[cite: 4]
using VisionFlow.Core.Imaging; // Làm việc với kiểu dữ liệu hình ảnh trừu tượng Core[cite: 4]
using VisionFlow.Core.Ports; // Quản lý cấu trúc cổng dữ liệu đầu vào đầu ra[cite: 4]
using VisionFlow.Core.Tools; // Kế thừa cấu trúc xử lý luồng của lớp VisionTool[cite: 4]
using VisionFlow.Tools.Imaging; // Sử dụng extension method mở hộp .AsMat()[cite: 4]

namespace VisionFlow.Tools.Preprocess; // Định vị thuộc nhóm tiền xử lý và lọc ảnh nhị phân[cite: 4]

/// <summary>
/// Tool xử lý ảnh thực hiện phân ngưỡng nhị phân hóa (Binary Thresholding) từng pixel dựa trên giá trị cấu hình[cite: 4].
/// </summary>
[ToolMetadata("Threshold", DisplayName = "Threshold", Category = "Filtering", Description = "Binary thresholding of an image")] // Gắn nhãn định danh cấu trúc node trên UI Palette[cite: 4]
public sealed class ThresholdTool : VisionTool
{
    private readonly InputPort<IVisionImage> _input; // Cổng vào tiếp nhận ảnh nguồn xử lý[cite: 4]
    private readonly ToolParameter<double> _threshold; // Tham số cấu hình giá trị ngưỡng phân tách[cite: 4]
    private readonly ToolParameter<double> _maxValue; // Giá trị gán tối đa cho pixel thỏa mãn điều kiện phân ngưỡng[cite: 4]
    private readonly ToolParameter<ThresholdTypes> _thresholdType; // Kiểu thuật toán nhị phân hóa của OpenCV (Binary, Invert...)[cite: 4]
    private readonly OutputPort<IVisionImage> _output; // Cổng ra xuất ảnh nhị phân thu được[cite: 4]

    public ThresholdTool()
    {
        _input = AddInput<IVisionImage>("Image"); // Đăng ký cổng nhập ảnh[cite: 4]
        _threshold = AddParameter("ThresholdValue", 128.0, "Threshold", 0.0, 255.0,category: "Filtering",order: 1 ); // Giới hạn biên giá trị pixel 8-bit từ 0 đến 255[cite: 4]
        _maxValue = AddParameter("MaxValue", 255.0, "Max Value", 0.0, 255.0,category: "Filtering",order: 2); // Cấu hình mặc định gán mức trắng tối đa 255[cite: 4]
        _thresholdType = AddParameter("Type", ThresholdTypes.Binary, "Threshold Type",category: "Filtering",order: 3); // Mặc định chọn thuật toán cắt ngưỡng nhị phân cơ bản[cite: 4]
        _output = AddOutput<IVisionImage>("Output"); // Đăng ký cổng xuất ảnh kết quả[cite: 4]
    }

    protected override void OnExecute(IToolContext context)
    {
        Mat src = _input.Value!.AsMat(); // Trích xuất an toàn ma trận ảnh OpenCV từ cổng vào[cite: 4]
        Mat dst = new Mat(); // Khởi tạo ma trận đích trống chứa ảnh nhị phân[cite: 4]

        Cv2.Threshold(src, dst, _threshold.Value, _maxValue.Value, _thresholdType.Value); // Thực thi hàm toán học phân ngưỡng ảnh của OpenCV[cite: 4]

        _output.Value = new MatVisionImage(dst); // Đẩy thực thể ảnh bọc wrapper ra cổng downstream[cite: 4]
        context.Log($"Image thresholded. Type: {_thresholdType.Value}, Thresh: {_threshold.Value}"); // Nhật ký chẩn đoán luồng chạy[cite: 4]
    }
}