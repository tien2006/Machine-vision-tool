// ==================== Vai trò chính:                Phân rã ảnh thành các thành phần TẦN SỐ THẤP (cấu trúc tổng thể) và TẦN SỐ CAO (chi tiết/cạnh/nhiễu) qua nhiều mức phân giải
// ==================== Thành phần / Class tiêu biểu: WaveletTransformTool
// ==================== Phụ thuộc vào:                Không phụ thuộc OpenCV cho phần lõi thuật toán - tự cài Haar DWT bằng C# thuần
// ==================== Pattern / Kỹ thuật nổi bật:   Haar Wavelet (dạng đơn giản nhất, chính xác tuyệt đối,
//                       không cần nội suy) - mỗi mức tách ảnh thành 4 góc phần tư LL (trung bình)/LH/HL/HH
//                       (chi tiết theo 3 hướng), rồi tiếp tục phân rã đệ quy trên đúng góc LL cho mức tiếp theo
//
// GHI CHÚ QUAN TRỌNG: mục Wavelet Transform trong tài liệu thuật toán chỉ mô tả đúng 1 tham số (Levels) và
// phần Output bị lỗi nội dung (mô tả "Pass/Fail" rõ ràng copy nhầm từ 1 tool khác, không liên quan Wavelet).
// Do tài liệu không đủ chi tiết, Input/Output/Parameter dưới đây được thiết kế theo đúng thực hành chuẩn
// của Wavelet Transform trong xử lý ảnh công nghiệp (phân tích kết cấu bề mặt, tách nhiễu tần số cao) -
// nếu bạn có bản tài liệu đầy đủ hơn, gửi lại để tôi chỉnh cho khớp chính xác.

using System;
using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;

namespace VisionFlow.Tools.Transform;

/// <summary>Chế độ hiển thị ảnh output.</summary>
public enum WaveletOutputMode
{
    Composite,        // Bố cục lưới kinh điển: LL (trên-trái, phân rã tiếp đệ quy) | LH (trên-phải) | HL (dưới-trái) | HH (dưới-phải)
    ApproximationOnly,// Chỉ hiển thị thành phần xấp xỉ LL ở mức sâu nhất - giống ảnh đã giảm nhiễu/giảm phân giải
    DetailOnly        // Chỉ hiển thị tổng hợp 3 thành phần chi tiết (LH+HL+HH) ở mức 1 - làm nổi bật cạnh/nhiễu/kết cấu bề mặt
}

/// <summary>
/// WaveletTransform: phân tích ảnh ở NHIỀU ĐỘ PHÂN GIẢI cùng lúc bằng phép biến đổi Haar Wavelet 2D.
/// Mỗi mức phân rã tách ảnh thành 4 phần: LL (Low-Low, ảnh thu nhỏ/làm mượt - "cấu trúc tổng thể"),
/// LH/HL (chi tiết theo hướng ngang/dọc - cạnh), HH (chi tiết theo đường chéo - nhiễu/kết cấu mịn).
/// Tăng <c>Levels</c> tiếp tục phân rã sâu hơn NGAY TRÊN góc LL của mức trước, giống việc "phóng to dần"
/// vào phần thông tin tổng thể còn lại. Ứng dụng thực tế: tách nhiễu tần số cao khỏi ảnh (giữ lại LL),
/// làm nổi bật khuyết điểm bề mặt mịn (xem riêng HH), nén/giảm dữ liệu ảnh trước khi lưu trữ.
/// </summary>
[ToolMetadata(
    "WaveletTransform",
    DisplayName = "Wavelet Transform",
    Category = "Transform",
    Description = "Multi-resolution image decomposition using 2D Haar Discrete Wavelet Transform")]
public sealed class WaveletTransformTool : VisionTool
{
    #region 1. Khai báo Port (In/Out)
    private readonly InputPort<IVisionImage> _imageMatrix;

    private readonly OutputPort<IVisionImage> _outImageMatrix;         // Ảnh hiển thị theo OutputMode
    private readonly OutputPort<IVisionImage> _outApproximation;       // Thành phần LL ở mức sâu nhất - luôn xuất kèm dù OutputMode là gì
    private readonly OutputPort<double> _outDetailEnergy;              // Tổng năng lượng thành phần chi tiết (|LH|+|HL|+|HH|) - chỉ số hữu ích để phát hiện bất thường/kết cấu bề mặt
    #endregion

    #region 2. Khai báo Parameter (Tab Wavelet - đúng tham số duy nhất tài liệu có đề cập: Levels)
    private readonly ToolParameter<int> _levels;             // Số mức phân rã - càng cao càng "nhìn sâu" vào cấu trúc tổng thể, nhưng kích thước LL giảm 1 nửa mỗi mức
    private readonly ToolParameter<WaveletOutputMode> _outputMode;
    private readonly ToolParameter<bool> _normalizeDetailForDisplay; // Hệ số chi tiết có giá trị âm/rất nhỏ - cần chuẩn hóa mới NHÌN THẤY được trên ảnh 8-bit
    #endregion

    public WaveletTransformTool()
    {
        _imageMatrix = AddInput<IVisionImage>("ImageMatrix", "Image Matrix");

        _outImageMatrix = AddOutput<IVisionImage>("ImageMatrix", "Image Matrix");
        _outApproximation = AddOutput<IVisionImage>("ApproximationImage", "Approximation Image");
        _outDetailEnergy = AddOutput<double>("DetailEnergy", "Detail Energy");

        // Levels=2: đủ để thấy rõ cấu trúc phân cấp mà không làm ảnh LL cuối cùng quá nhỏ để còn ý nghĩa
        _levels = AddParameter("Levels", 2, "Levels", min: 1, max: 6, category: "Wavelet", order: 1);
        _outputMode = AddParameter("OutputMode", WaveletOutputMode.Composite, "Output Mode", category: "Wavelet", order: 2);
        _normalizeDetailForDisplay = AddParameter("NormalizeDetailForDisplay", true, "Normalize Detail For Display", category: "Wavelet", order: 3);
    }

    protected override void OnExecute(IToolContext context)
    {
        Mat original = _imageMatrix.Value!.AsMat();

        // ----- Bước 1: chuyển ảnh xám float32 - Wavelet Transform hoạt động trên độ sáng, không cần màu -----
        using Mat grayByte = original.Channels() == 1 ? original.Clone() : ToGray(original);
        using Mat working = new Mat();
        grayByte.ConvertTo(working, MatType.CV_32F);

        // Canvas hiển thị dùng đúng kích thước ảnh gốc (đã pad chẵn nếu cần) để đặt các góc phần tư vào
        Mat canvas = working.Clone();

        Mat currentLL = working;
        bool currentLLIsWorking = true;
        double detailEnergySum = 0;

        int actualLevels = 0;
        for (int level = 0; level < _levels.Value; level++)
        {
            // Dừng sớm nếu ảnh còn lại quá nhỏ để phân rã tiếp có ý nghĩa (dưới 4x4 pixel)
            if (currentLL.Rows < 4 || currentLL.Cols < 4) break;

            var (ll, lh, hl, hh) = HaarDwtStep(currentLL);

            detailEnergySum += SumAbs(lh) + SumAbs(hl) + SumAbs(hh);

            // ----- Ghi 4 góc phần tư vào đúng vị trí tương ứng trên canvas hiển thị -----
            int offsetRow = 0, offsetCol = 0; // Góc phần tư của mức này luôn nằm ở góc trên-trái của canvas hiện tại (đệ quy dần vào trong)
            WriteQuadrant(canvas, ll, offsetRow, offsetCol, _normalizeDetailForDisplay.Value, isApproximation: true);
            WriteQuadrant(canvas, lh, offsetRow, offsetCol + ll.Cols, _normalizeDetailForDisplay.Value, isApproximation: false);
            WriteQuadrant(canvas, hl, offsetRow + ll.Rows, offsetCol, _normalizeDetailForDisplay.Value, isApproximation: false);
            WriteQuadrant(canvas, hh, offsetRow + ll.Rows, offsetCol + ll.Cols, _normalizeDetailForDisplay.Value, isApproximation: false);

            if (!currentLLIsWorking) currentLL.Dispose(); // Giải phóng LL của vòng lặp trước (không phải working gốc)
            lh.Dispose(); hl.Dispose(); hh.Dispose();

            currentLL = ll;
            currentLLIsWorking = false;
            actualLevels++;
        }

        if (actualLevels == 0)
            throw new ToolExecutionException("WaveletTransform: image is too small to decompose even 1 level (minimum ~4x4 pixels).");

        // ----- Bước 2: dựng ảnh Approximation (LL mức sâu nhất) - luôn xuất kèm bất kể OutputMode -----
        Mat approxOut = new Mat();
        currentLL.ConvertTo(approxOut, MatType.CV_8U);

        // ----- Bước 3: dựng ImageMatrix theo OutputMode -----
        Mat resultImage;
        switch (_outputMode.Value)
        {
            case WaveletOutputMode.ApproximationOnly:
                resultImage = approxOut.Clone();
                break;

            case WaveletOutputMode.DetailOnly:
                {
                    // Chỉ tính lại 1 mức LH/HL/HH từ ẢNH GỐC (không phải từ LL mức sâu) để giữ đúng độ phân giải ban đầu, dễ quan sát chi tiết/nhiễu bề mặt
                    var (_, lh1, hl1, hh1) = HaarDwtStep(working);
                    using Mat absLh1 = AbsF(lh1);
                    using Mat absHl1 = AbsF(hl1);
                    using Mat absHh1 = AbsF(hh1);
                    using Mat combined = new Mat();
                    Cv2.Add(absLh1, absHl1, combined);
                    Cv2.Add(combined, absHh1, combined);
                    resultImage = new Mat();
                    Cv2.Normalize(combined, resultImage, 0, 255, NormTypes.MinMax);
                    resultImage.ConvertTo(resultImage, MatType.CV_8U);
                    lh1.Dispose(); hl1.Dispose(); hh1.Dispose();
                    break;
                }

            default: // Composite
                resultImage = new Mat();
                canvas.ConvertTo(resultImage, MatType.CV_8U);
                break;
        }

        canvas.Dispose();
        if (!currentLLIsWorking) currentLL.Dispose();

        _outImageMatrix.Value = new MatVisionImage(resultImage);
        _outApproximation.Value = new MatVisionImage(approxOut);
        _outDetailEnergy.Value = detailEnergySum;

        context.Log($"WaveletTransform: requested levels={_levels.Value}, actual levels={actualLevels}, detailEnergy={detailEnergySum:F0}");
    }

    /// <summary>
    /// 1 bước Haar DWT 2D: tách ảnh (CV_32F, 1 kênh) thành 4 góc phần tư kích thước bằng 1 nửa mỗi chiều.
    /// Công thức lifting scheme cổ điển: L=(a+b)/2 (trung bình - giữ thông tin tổng thể), H=(a-b)/2
    /// (hiệu - giữ thông tin chi tiết/biến thiên). Áp dụng lần lượt theo hàng rồi theo cột.
    /// </summary>
    private static (Mat LL, Mat LH, Mat HL, Mat HH) HaarDwtStep(Mat src)
    {
        // Đảm bảo kích thước chẵn - nhân bản hàng/cột cuối nếu lẻ (edge replicate), tránh mất dữ liệu biên
        int h = src.Rows % 2 == 0 ? src.Rows : src.Rows + 1;
        int w = src.Cols % 2 == 0 ? src.Cols : src.Cols + 1;
        Mat padded = src;
        bool paddedIsCopy = false;
        if (h != src.Rows || w != src.Cols)
        {
            padded = new Mat();
            Cv2.CopyMakeBorder(src, padded, 0, h - src.Rows, 0, w - src.Cols, BorderTypes.Replicate);
            paddedIsCopy = true;
        }

        // ----- Bước theo HÀNG: mỗi cặp cột liền kề (2x, 2x+1) -> 1 giá trị L và 1 giá trị H -----
        Mat rowL = new Mat(h, w / 2, MatType.CV_32F);
        Mat rowH = new Mat(h, w / 2, MatType.CV_32F);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w / 2; x++)
            {
                float a = padded.At<float>(y, 2 * x);
                float b = padded.At<float>(y, 2 * x + 1);
                rowL.Set(y, x, (a + b) / 2f);
                rowH.Set(y, x, (a - b) / 2f);
            }

        // ----- Bước theo CỘT trên cả rowL và rowH -> tách ra 4 góc phần tư cuối cùng -----
        var (ll, hl) = ColumnHaarStep(rowL);
        var (lh, hh) = ColumnHaarStep(rowH);

        rowL.Dispose(); rowH.Dispose();
        if (paddedIsCopy) padded.Dispose();

        return (ll, lh, hl, hh);
    }

    /// <summary>Áp dụng bước Haar (trung bình/hiệu) theo chiều CỘT (giữa 2 hàng liền kề) cho 1 ma trận CV_32F.</summary>
    private static (Mat Low, Mat High) ColumnHaarStep(Mat src)
    {
        int h = src.Rows, w = src.Cols;
        Mat low = new Mat(h / 2, w, MatType.CV_32F);
        Mat high = new Mat(h / 2, w, MatType.CV_32F);
        for (int y = 0; y < h / 2; y++)
            for (int x = 0; x < w; x++)
            {
                float a = src.At<float>(2 * y, x);
                float b = src.At<float>(2 * y + 1, x);
                low.Set(y, x, (a + b) / 2f);
                high.Set(y, x, (a - b) / 2f);
            }
        return (low, high);
    }

    /// <summary>Ghi 1 góc phần tư (đã tùy chọn chuẩn hóa nếu là thành phần chi tiết) vào đúng vị trí trên canvas hiển thị.</summary>
    private static void WriteQuadrant(Mat canvas, Mat quadrant, int rowOffset, int colOffset, bool normalize, bool isApproximation)
    {
        using Mat display = new Mat();
        if (isApproximation || !normalize)
        {
            quadrant.CopyTo(display);
        }
        else
        {
            // Thành phần chi tiết có giá trị quanh 0 (âm/dương) - lấy trị tuyệt đối rồi chuẩn hóa để NHÌN THẤY được,
            // nếu không bật Normalize thì hầu hết sẽ hiện gần như đen tuyền (giá trị quá nhỏ so với thang 0-255).
            using Mat absVal = AbsF(quadrant);
            Cv2.Normalize(absVal, display, 0, 255, NormTypes.MinMax);
        }

        using Mat roi = new Mat(canvas, new Rect(colOffset, rowOffset, quadrant.Cols, quadrant.Rows));
        display.CopyTo(roi);
    }

    private static Mat AbsF(Mat src)
    {
        Mat dst = new Mat();
        Cv2.Absdiff(src, Scalar.All(0), dst);
        return dst;
    }

    private static double SumAbs(Mat src)
    {
        using Mat abs = AbsF(src);
        return Cv2.Sum(abs).Val0;
    }

    private static Mat ToGray(Mat src)
    {
        var gray = new Mat();
        Cv2.CvtColor(src, gray, src.Channels() == 4 ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY);
        return gray;
    }
}