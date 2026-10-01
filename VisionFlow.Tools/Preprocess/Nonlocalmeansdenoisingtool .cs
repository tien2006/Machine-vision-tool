// ==================== Vai trò chính:                Khử nhiễu CHẤT LƯỢNG CAO NHẤT trong nhóm Preprocessing (đánh đổi bằng tốc độ chậm nhất)
// ==================== Thành phần / Class tiêu biểu: NonLocalMeansDenoisingTool
// ==================== Phụ thuộc vào:                OpenCvSharp (Cv2.FastNlMeansDenoising / FastNlMeansDenoisingColored)
// ==================== Pattern / Kỹ thuật nổi bật:   Non-Local Means — thay vì chỉ so N pixel lân cận như
//                       Gaussian/Bilateral, thuật toán so sánh TỪNG VÙNG PATCH nhỏ (TemplateWindowSize) với mọi
//                       patch khác trong 1 vùng tìm kiếm rộng (SearchWindowSize) trong toàn ảnh, rồi lấy trung
//                       bình có trọng số theo độ giống nhau giữa các patch -> khử nhiễu rất mượt mà vẫn giữ
//                       được chi tiết lặp lại (texture), nhưng ĐẮT ĐỎ về mặt tính toán hơn hẳn Bilateral/Gaussian.

using System;
using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;

namespace VisionFlow.Tools.Preprocess; // Cùng thư mục với BilateralFilteringTool / SmoothTool

/// <summary>
/// Non-Local Means Denoising: thuật toán khử nhiễu mạnh nhất trong nhóm Preprocessing, thường dùng cho
/// ảnh chụp ở điều kiện thiếu sáng / phải tăng Gain cao (nhiễu hạt nặng) mà Bilateral/Median chưa đủ sạch.
/// CẢNH BÁO HIỆU NĂNG: chậm hơn Bilateral/Gaussian hàng chục lần với SearchWindowSize mặc định (21x21) -
/// KHÔNG khuyến nghị dùng trong pipeline realtime tốc độ cao (line chạy >5-10 FPS); phù hợp cho các bước
/// kiểm tra offline, phân tích ảnh 1 lần, hoặc line tốc độ chậm chấp nhận được cycle time vài trăm ms.
/// </summary>
[ToolMetadata(
    "NonLocalMeansDenoising",
    DisplayName = "Non-Local Means Denoising",
    Category = "Preprocessing",
    Description = "Highest-quality denoising via patch-based averaging; much slower than Bilateral/Gaussian")]
public sealed class NonLocalMeansDenoisingTool : VisionTool
{
    #region 1. Khai báo Port (In/Out)
    private readonly InputPort<IVisionImage> _input;    // Ảnh nguồn: chỉ hỗ trợ ảnh xám (1 kênh) hoặc màu BGR (3 kênh)
    private readonly OutputPort<IVisionImage> _output;  // Ảnh đã khử nhiễu, giữ nguyên số kênh với đầu vào
    #endregion

    #region 2. Khai báo Parameter
    private readonly ToolParameter<float> _h;                    // Cường độ lọc kênh sáng (Luminance) - lớn hơn = sạch nhiễu hơn nhưng mất chi tiết nhiều hơn
    private readonly ToolParameter<float> _hColor;                // Cường độ lọc kênh màu - chỉ áp dụng khi ảnh đầu vào có 3 kênh (màu)
    private readonly ToolParameter<int> _templateWindowSize;      // Kích thước patch dùng để so sánh độ giống nhau (phải lẻ)
    private readonly ToolParameter<int> _searchWindowSize;        // Bán kính vùng tìm patch tương tự (phải lẻ) - CÀNG LỚN CÀNG CHẬM
    #endregion

    public NonLocalMeansDenoisingTool()
    {
        _input = AddInput<IVisionImage>("Image");
        _output = AddOutput<IVisionImage>("Image");

        // H=10: giá trị khuyến nghị mặc định của OpenCV, phù hợp với hầu hết nhiễu camera công nghiệp bình thường
        _h = AddParameter("H", 10f, "H (Luminance)", min: 1f, max: 30f, category: "Filter", order: 1);

        // HColor=10: chỉ có ý nghĩa khi ảnh màu (3 kênh); bị bỏ qua khi ảnh xám 1 kênh
        _hColor = AddParameter("HColor", 10f, "H (Color)", min: 1f, max: 30f, category: "Filter", order: 2);

        // TemplateWindowSize=7: kích thước patch chuẩn theo khuyến nghị OpenCV, đủ để nhận diện texture cục bộ
        _templateWindowSize = AddParameter("TemplateWindowSize", 7, "Template Window Size", min: 3, max: 21, category: "Filter", order: 3);

        // SearchWindowSize=21: vùng tìm kiếm chuẩn của OpenCV. Đây là tham số ẢNH HƯỞNG TỐC ĐỘ NHIỀU NHẤT -
        // giảm xuống (VD 11) nếu cần chạy nhanh hơn, chấp nhận đổi lấy hiệu quả khử nhiễu thấp hơn 1 chút
        _searchWindowSize = AddParameter("SearchWindowSize", 21, "Search Window Size", min: 3, max: 51, category: "Filter", order: 4);
    }

    protected override void OnExecute(IToolContext context)
    {
        Mat src = _input.Value!.AsMat();

        // ----- Ép 2 window size về số lẻ dương, đúng yêu cầu bắt buộc của OpenCV -----
        int template = ForceOdd(_templateWindowSize.Value, min: 3);
        int search = ForceOdd(_searchWindowSize.Value, min: 3);

        Mat dst = new Mat();
        int channels = src.Channels();

        if (channels == 1)
        {
            // Ảnh xám: dùng biến thể đơn kênh, chỉ cần 1 tham số cường độ lọc H
            Cv2.FastNlMeansDenoising(src, dst, _h.Value, template, search);
        }
        else if (channels == 3)
        {
            // Ảnh màu: dùng biến thể Colored - lọc riêng kênh sáng (H) và kênh màu (HColor) để tránh
            // làm lem màu (color bleeding) giữa các vùng khác màu nhưng độ sáng gần nhau
            Cv2.FastNlMeansDenoisingColored(src, dst, _h.Value, _hColor.Value, template, search);
        }
        else
        {
            // 4 kênh (BGRA) hoặc số kênh lạ khác: OpenCV không hỗ trợ trực tiếp -> báo lỗi rõ ràng
            // thay vì để Cv2 ném exception khó hiểu, đồng thời gợi ý hướng khắc phục ngay trong thông báo lỗi
            dst.Dispose();
            throw new ToolExecutionException(
                $"NonLocalMeansDenoising: unsupported channel count ({channels}). " +
                "Add a ConvertColor node before this tool to convert to Grayscale (1 channel) or BGR (3 channels).");
        }

        _output.Value = new MatVisionImage(dst);

        context.Log($"NonLocalMeansDenoising: channels={channels}, h={_h.Value}, hColor={_hColor.Value}, template={template}, search={search}");
    }

    /// <summary>Ép giá trị về số lẻ dương >= min, giống cách SmoothTool/MedianBlurTool tự sửa KernelSize chẵn do người dùng nhập nhầm.</summary>
    private static int ForceOdd(int value, int min)
    {
        int v = Math.Max(min, value);
        if (v % 2 == 0) v += 1;
        return v;
    }
}