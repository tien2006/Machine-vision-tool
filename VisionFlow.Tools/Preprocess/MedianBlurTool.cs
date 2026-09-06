// ==================== Vai trò chính:                Khử nhiễu muối tiêu (hạt trắng/đen) trước Blob / đo biên
// ==================== Thành phần / Class tiêu biểu: MedianBlurTool
// ==================== Phụ thuộc vào:                OpenCvSharp (Cv2.MedianBlur)
// ==================== Pattern / Kỹ thuật nổi bật:   Lọc trung vị — kernel lẻ, giữ cạnh tốt hơn Gaussian

using System;
using OpenCvSharp;                          // Gọi Cv2.MedianBlur — lấy trung vị trong cửa sổ kernel
using VisionFlow.Core.Imaging;              // Kiểu ảnh trừu tượng IVisionImage
using VisionFlow.Core.Ports;                // InputPort / OutputPort
using VisionFlow.Core.Tools;                // VisionTool + [ToolMetadata]
using VisionFlow.Tools.Imaging;             // .AsMat() và MatVisionImage

namespace VisionFlow.Tools.Preprocess;      // Cùng thư mục với SmoothTool / ThresholdTool

/// <summary>
/// MedianBlur: với mỗi pixel, lấy các giá trị trong cửa sổ NxN, sắp xếp, chọn trung vị.
/// Hạt nhiễu muối tiêu (cực sáng/cực tối) bị loại, mép vật thể giữ rõ hơn Smooth/Gaussian.
/// Thường đặt SAU ConvertColor, TRƯỚC Threshold / BlobAnalysis / đo khoảng cách.
/// </summary>
[ToolMetadata(
    "MedianBlur",
    DisplayName = "Median Blur",
    Category = "Preprocessing",
    Description = "Median filter to remove salt-and-pepper noise while keeping edges")]
public sealed class MedianBlurTool : VisionTool
{
    #region 1. Khai báo Port (In/Out)
    private readonly InputPort<IVisionImage> _input;    // Ảnh nguồn (xám hoặc màu)
    private readonly OutputPort<IVisionImage> _output;  // Ảnh đã khử nhiễu hạt
    #endregion

    #region 2. Khai báo Parameter
    private readonly ToolParameter<int> _kernelSize;    // Cửa sổ NxN; OpenCV bắt buộc số lẻ và >= 3
    #endregion

    public MedianBlurTool()
    {
        _input = AddInput<IVisionImage>("Image");
        _output = AddOutput<IVisionImage>("Image");

        // Mặc định 3x3: đủ ăn nhiễu hạt, ít làm mờ chi tiết
        // min=3 vì MedianBlur OpenCV không chấp nhận ksize=1; max=31 vì kernel lớn rất chậm
        _kernelSize = AddParameter("KernelSize", 3, "Kernel Size", 3, 31, category: "Filter", order: 1);
    }

    protected override void OnExecute(IToolContext context)
    {
        Mat src = _input.Value!.AsMat();    // Mở hộp IVisionImage → Mat OpenCV

        // ----- Ép KernelSize về số lẻ >= 3 (OpenCV ném lỗi nếu chẵn hoặc < 3) -----
        int k = Math.Max(3, _kernelSize.Value);
        if (k % 2 == 0)                     // 4, 6, 8... → 5, 7, 9...
        {
            k += 1;
        }

        // ----- Lọc trung vị: dst cùng kích thước, cùng số kênh với src -----
        Mat dst = new Mat();
        Cv2.MedianBlur(src, dst, k);        // k là cạnh kernel (3, 5, 7, ...)

        _output.Value = new MatVisionImage(dst); // Chuyển quyền sở hữu Mat cho pipeline

        context.Log($"MedianBlur: kernel={k}x{k}");
    }
}