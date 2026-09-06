// ==================== Vai trò chính:                Làm mượt ảnh, giảm nhiễu hạt trước Threshold / Edge / Contour
// ==================== Thành phần / Class tiêu biểu: SmoothTool
// ==================== Phụ thuộc vào:                OpenCvSharp (Cv2.GaussianBlur)
// ==================== Pattern / Kỹ thuật nổi bật:   Gaussian smoothing — kernel lẻ, sigma=0 thì OpenCV tự tính

using System;
using OpenCvSharp;                          // Gọi Cv2.GaussianBlur — bộ lọc Gauss trên ma trận ảnh
using VisionFlow.Core.Imaging;              // Kiểu ảnh trừu tượng IVisionImage (Core không phụ thuộc OpenCV)
using VisionFlow.Core.Ports;                // InputPort / OutputPort — cổng nối dây trên canvas
using VisionFlow.Core.Tools;                // VisionTool + [ToolMetadata] + ToolExecutionException
using VisionFlow.Tools.Imaging;             // Extension .AsMat() và wrapper MatVisionImage

namespace VisionFlow.Tools.Preprocess;      // Cùng thư mục Preprocess với ConvertColorTool / ThresholdTool

/// <summary>
/// Smooth (Gaussian Blur): hòa tan nhiễu hạt vào nền, làm mềm răng cưa mép vật thể.
/// Thường đặt SAU ConvertColor (ảnh xám) và TRƯỚC Threshold / FindContours / Canny.
/// Kernel càng lớn → mượt mạnh hơn nhưng chi tiết nhỏ (vết xước, chữ) dễ mất.
/// </summary>
[ToolMetadata(
    "Smooth",                               // TypeKey lưu JSON flow — không đổi sau khi đã có file .flow cũ
    DisplayName = "Smooth",                 // Tên hiện trên palette / node
    Category = "Preprocessing",             // Nhóm palette, cùng Convert Color
    Description = "Gaussian blur to reduce noise before threshold/edge detection")]
public sealed class SmoothTool : VisionTool
{
    #region 1. Khai báo Port (In/Out)
    private readonly InputPort<IVisionImage> _input;    // Ảnh nguồn (màu hoặc xám đều được)
    private readonly OutputPort<IVisionImage> _output;  // Ảnh đã làm mượt, cùng số kênh với đầu vào
    #endregion

    #region 2. Khai báo Parameter
    private readonly ToolParameter<int> _kernelSize;    // Cạnh kernel NxN (phải lẻ khi đưa vào OpenCV)
    private readonly ToolParameter<double> _sigma;      // Độ lệch chuẩn Gauss; 0 = OpenCV tự suy từ KernelSize
    #endregion

    public SmoothTool()
    {
        // ----- Port: tên "Image" để nối dây giống DilateTool / GrabImageTool -----
        _input = AddInput<IVisionImage>("Image");
        _output = AddOutput<IVisionImage>("Image");

        // KernelSize mặc định 5 (5x5): đủ giảm nhiễu camera công nghiệp, chưa làm mất biên quá mạnh
        // min=1, max=31: kernel quá lớn vừa chậm vừa làm vật thể "tan" vào nền
        _kernelSize = AddParameter("KernelSize", 5, "Kernel Size", 1, 31, category: "Filter", order: 1);

        // Sigma=0: OpenCV tính sigma từ kích thước kernel (công thức nội bộ ~ 0.3*((k-1)*0.5-1)+0.8)
        // Chỉ tăng Sigma khi muốn mờ mạnh hơn mà không muốn tăng KernelSize
        _sigma = AddParameter("Sigma", 0.0, "Sigma", 0.0, 20.0, category: "Filter", order: 2);
    }

    protected override void OnExecute(IToolContext context)
    {
        // Dấu '!': ValidateInputs() của VisionTool đã chặn cổng bắt buộc bị null trước khi vào đây
        Mat src = _input.Value!.AsMat();    // Mở hộp IVisionImage → Mat OpenCV thật

        // ----- Bước 1: Ép KernelSize về số lẻ dương -----
        // GaussianBlur của OpenCV yêu cầu ksize.width và ksize.height là số lẻ dương.
        // Nếu user kéo slider ra 4 hoặc 6, ta tự nâng lên 5 hoặc 7 thay vì để OpenCV ném exception.
        int k = Math.Max(1, _kernelSize.Value); // Không cho kernel 0 / âm
        if (k % 2 == 0)                         // Số chẵn → cộng 1 thành số lẻ kế tiếp
        {
            k += 1;
        }

        // ----- Bước 2: Chạy GaussianBlur -----
        // dst là Mat mới: không ghi đè src, để pipeline còn giữ ảnh gốc ở node trước (nếu node đó còn sống)
        Mat dst = new Mat();
        Cv2.GaussianBlur(
            src,                                // Ảnh nguồn
            dst,                                // Ảnh đích (cùng kích thước, cùng số kênh)
            new Size(k, k),                     // Kernel vuông k x k
            _sigma.Value,                       // sigmaX; 0 = tự tính
            _sigma.Value,                       // sigmaY = sigmaX → làm mờ đẳng hướng (không méo theo 1 trục)
            BorderTypes.Reflect101);            // Phản xạ biên kiểu OpenCV mặc định, tránh viền đen giả ở mép ảnh

        // ----- Bước 3: Đẩy kết quả ra cổng — chuyển quyền sở hữu Mat cho MatVisionImage -----
        _output.Value = new MatVisionImage(dst);

        context.Log($"Smooth: GaussianBlur kernel={k}x{k}, sigma={_sigma.Value}");
    }
}