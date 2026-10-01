// ==================== Vai trò chính:                Làm mượt nhiễu NHƯNG giữ cạnh sắc nét (khác Smooth/Gaussian làm mờ luôn cả cạnh)
// ==================== Thành phần / Class tiêu biểu: BilateralFilteringTool
// ==================== Phụ thuộc vào:                OpenCvSharp (Cv2.BilateralFilter)
// ==================== Pattern / Kỹ thuật nổi bật:   Edge-preserving smoothing — trọng số kết hợp cả khoảng cách
//                       không gian (Spatial) LẪN độ chênh màu sắc (Color); pixel khác biệt màu lớn (= cạnh) hầu
//                       như không bị pha trộn vào nhau, còn vùng đồng màu (= nền/nhiễu hạt) thì được hòa trộn mạnh.

using System;
using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;

namespace VisionFlow.Tools.Preprocess; // Cùng thư mục với SmoothTool / MedianBlurTool

/// <summary>
/// Bilateral Filter: giống Gaussian Blur nhưng thêm 1 trọng số phụ theo độ chênh lệch màu (SigmaColor).
/// Nhờ vậy: vùng nền đồng màu bị làm mượt mạnh (giảm nhiễu hạt), còn cạnh vật thể (nơi màu đổi đột ngột)
/// gần như được giữ nguyên sắc nét - phù hợp làm bước tiền xử lý TRƯỚC Caliper/FindLine/FindCircle,
/// nơi cần vừa giảm nhiễu vừa không được làm "trôi" vị trí cạnh cần đo.
/// Đánh đổi: chậm hơn Gaussian/Median đáng kể (thuật toán không tách được thành 2 lần lọc 1 chiều),
/// nên hạn chế Diameter lớn khi cần chạy realtime.
/// </summary>
[ToolMetadata(
    "BilateralFiltering",
    DisplayName = "Bilateral Filtering",
    Category = "Preprocessing",
    Description = "Edge-preserving smoothing: reduces noise while keeping sharp edges, slower than Gaussian/Median")]
public sealed class BilateralFilteringTool : VisionTool
{
    #region 1. Khai báo Port (In/Out)
    private readonly InputPort<IVisionImage> _input;    // Ảnh nguồn (xám hoặc màu 8-bit đều được)
    private readonly OutputPort<IVisionImage> _output;  // Ảnh đã lọc, cùng kích thước/số kênh với đầu vào
    #endregion

    #region 2. Khai báo Parameter
    private readonly ToolParameter<int> _diameter;      // Đường kính vùng lân cận mỗi pixel (d trong Cv2.BilateralFilter)
    private readonly ToolParameter<double> _sigmaColor; // Ngưỡng chênh lệch màu để coi là "cùng vùng" (giá trị càng lớn càng trộn nhiều màu khác nhau)
    private readonly ToolParameter<double> _sigmaSpace; // Ngưỡng khoảng cách không gian để coi là "lân cận" (giá trị càng lớn, vùng ảnh hưởng càng rộng)
    #endregion

    public BilateralFilteringTool()
    {
        _input = AddInput<IVisionImage>("Image");
        _output = AddOutput<IVisionImage>("Image");

        // Diameter=9: cân bằng giữa hiệu quả khử nhiễu và tốc độ. Tài liệu OpenCV khuyến nghị <=9 cho realtime,
        // giá trị lớn hơn (>9) chỉ nên dùng cho xử lý offline (không yêu cầu tốc độ) vì độ phức tạp tăng rất nhanh.
        _diameter = AddParameter("Diameter", 9, "Diameter", min: 1, max: 25, category: "Filter", order: 1);

        // SigmaColor=75: đủ để hòa trộn nhiễu hạt bình thường, không đủ lớn để "nuốt" luôn cạnh có tương phản rõ
        _sigmaColor = AddParameter("SigmaColor", 75.0, "Sigma Color", min: 1.0, max: 200.0, category: "Filter", order: 2);

        // SigmaSpace=75: bán kính không gian tương ứng với Diameter mặc định ở trên
        _sigmaSpace = AddParameter("SigmaSpace", 75.0, "Sigma Space", min: 1.0, max: 200.0, category: "Filter", order: 3);
    }

    protected override void OnExecute(IToolContext context)
    {
        Mat src = _input.Value!.AsMat();

        // Cv2.BilateralFilter KHÔNG cho phép xử lý In-place (dst phải khác vùng nhớ với src) -> luôn tạo Mat mới
        Mat dst = new Mat();
        Cv2.BilateralFilter(
            src,
            dst,
            Math.Max(1, _diameter.Value),
            _sigmaColor.Value,
            _sigmaSpace.Value,
            BorderTypes.Reflect101); // Phản xạ biên, tránh viền đen giả ở mép ảnh - đồng bộ với SmoothTool

        _output.Value = new MatVisionImage(dst);

        context.Log($"BilateralFiltering: d={_diameter.Value}, sigmaColor={_sigmaColor.Value}, sigmaSpace={_sigmaSpace.Value}");
    }
}