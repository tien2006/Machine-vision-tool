// ==================== Vai trò chính:                Trích xuất texture/cạnh theo 1 hướng + 1 tần số cụ thể - xuất riêng phần thực (cosine), phần ảo (sine) và biên độ tổng hợp
// ==================== Thành phần / Class tiêu biểu: GaborFilterTool
// ==================== Phụ thuộc vào:                OpenCvSharp (Cv2.GetGaborKernel, Cv2.Filter2D, Cv2.Magnitude)
// ==================== Pattern / Kỹ thuật nổi bật:   Gabor Wavelet dạng số phức - RealPart dùng kernel Cosine
//                       (Cv2.GetGaborKernel chuẩn), ImaginaryPart dùng CÙNG kernel nhưng lệch pha thêm -90°
//                       (vì cos(θ-90°) = sin(θ), không cần code lại công thức Gabor riêng cho phần Sine)

using System;
using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;

namespace VisionFlow.Tools.Preprocess; // Cùng thư mục với SobelFilterTool / LaplacianFilterTool

/// <summary>
/// Gabor Filter: convolve ảnh với 1 kernel dạng sóng có hướng (Theta) và tần số (Lambda) cụ thể, làm nổi
/// bật vùng TEXTURE khớp với hướng/tần số đó - dùng cho vân vải, đường vân kim loại (brushed metal),
/// kiểm tra bề mặt CNC. Về bản chất toán học, kernel Gabor là số PHỨC: <c>RealPart</c> là phản hồi với
/// thành phần Cosine (thường dùng làm nổi bật cường độ vạch/cạnh), <c>ImaginaryPart</c> là phản hồi với
/// thành phần Sine (thường dùng phát hiện bước chuyển tiếp pha/cạnh). <c>ImageMatrix</c> là biên độ tổng
/// hợp <c>sqrt(Real² + Imag²)</c> - không phụ thuộc pha, cho thấy TOÀN BỘ vùng khớp đặc trưng bất kể vị
/// trí cạnh rơi vào pha nào của sóng.
/// </summary>
[ToolMetadata(
    "GaborFilter",
    DisplayName = "Gabor Filter",
    Category = "Preprocessing",
    Description = "Directional texture filter using a complex Gabor wavelet kernel - outputs RealPart, ImaginaryPart and combined magnitude")]
public sealed class GaborFilterTool : VisionTool
{
    #region 1. Khai báo Port (In/Out)
    private readonly InputPort<IVisionImage> _imageMatrix; // Ảnh gốc (thường là ảnh xám) cần phân tích bề mặt/tìm họa tiết lặp lại

    private readonly OutputPort<IVisionImage> _outImageMatrix;   // Kết quả tổng hợp - biên độ Gabor, không phụ thuộc pha
    private readonly OutputPort<IVisionImage> _outRealPart;      // Phản hồi với hàm Cosine
    private readonly OutputPort<IVisionImage> _outImaginaryPart; // Phản hồi với hàm Sine
    #endregion

    #region 2. Khai báo Parameter
    // --- Tab Advanced ---
    private readonly ToolParameter<double> _psi; // Độ lệch pha (độ) của sóng cosine trong kernel - đổi vị trí vùng sáng/tối trong pattern lọc

    // --- Tab Filter ---
    private readonly ToolParameter<double> _gamma;   // Tỷ lệ kéo dài Gaussian envelope: 1=tròn, <1 kéo dài theo 1 hướng -> nhạy hơn với line/stripe
    private readonly ToolParameter<int> _kernelSize; // Kích thước kernel NxN - lớn hơn bắt pattern to hơn, mượt hơn nhưng chậm hơn
    private readonly ToolParameter<double> _lambda;  // Bước sóng (chu kỳ stripe) - phải khớp gần đúng chu kỳ vân thật mới bắt rõ
    private readonly ToolParameter<double> _sigma;   // Độ lan rộng Gaussian envelope - lớn hơn mượt hơn/giảm nhiễu nhưng giảm sắc nét chi tiết nhỏ
    private readonly ToolParameter<double> _theta;   // Hướng kernel (độ) - Theta=0 nhạy chi tiết dọc/pattern vuông góc hướng ngang
    #endregion

    public GaborFilterTool()
    {
        _imageMatrix = AddInput<IVisionImage>("ImageMatrix", "Image Matrix");

        _outImageMatrix = AddOutput<IVisionImage>("ImageMatrix", "Image Matrix");
        _outRealPart = AddOutput<IVisionImage>("RealPart", "Real Part");
        _outImaginaryPart = AddOutput<IVisionImage>("ImaginaryPart", "Imaginary Part");

        _psi = AddParameter("Psi", 0.0, "Psi", min: 0.0, max: 360.0, category: "Advanced", order: 1);

        _gamma = AddParameter("Gamma", 0.5, "Gamma", min: 0.1, max: 1.0, category: "Filter", order: 1);
        _kernelSize = AddParameter("KernelSize", 21, "Kernel Size", min: 3, max: 99, category: "Filter", order: 2);
        _lambda = AddParameter("Lambda", 10.0, "Lambda", min: 2.0, max: 100.0, category: "Filter", order: 3);
        _sigma = AddParameter("Sigma", 4.0, "Sigma", min: 0.5, max: 30.0, category: "Filter", order: 4);
        _theta = AddParameter("Theta", 0.0, "Theta", min: 0.0, max: 180.0, category: "Filter", order: 5);
    }

    protected override void OnExecute(IToolContext context)
    {
        Mat src = _imageMatrix.Value!.AsMat();

        // ----- Bước 1: ép KernelSize về số lẻ dương -----
        int k = Math.Max(3, _kernelSize.Value);
        if (k % 2 == 0) k += 1;

        // Theta/Psi trên UI dùng đơn vị độ cho dễ hình dung - Cv2.GetGaborKernel yêu cầu radian
        double thetaRad = _theta.Value * Math.PI / 180.0;
        double psiRad = _psi.Value * Math.PI / 180.0;

        // ----- Bước 2: sinh 2 kernel Cosine (Real) và Sine (Imaginary) -----
        // MẸO TOÁN HỌC: cos(θ - 90°) = sin(θ), nên chỉ cần gọi lại đúng hàm GetGaborKernel với Psi lệch
        // thêm -90° là ra kernel Sine, KHÔNG cần tự viết công thức Gabor riêng cho phần ảo.
        using Mat kernelReal = Cv2.GetGaborKernel(new Size(k, k), _sigma.Value, thetaRad, _lambda.Value, _gamma.Value, psiRad, MatType.CV_32F);
        using Mat kernelImag = Cv2.GetGaborKernel(new Size(k, k), _sigma.Value, thetaRad, _lambda.Value, _gamma.Value, psiRad - Math.PI / 2.0, MatType.CV_32F);

        // ----- Bước 3: convolve ảnh với từng kernel - dùng độ sâu float để không mất giá trị âm của sóng -----
        using Mat realFloat = new Mat();
        using Mat imagFloat = new Mat();
        Cv2.Filter2D(src, realFloat, MatType.CV_32F, kernelReal);
        Cv2.Filter2D(src, imagFloat, MatType.CV_32F, kernelImag);

        // ----- Bước 4: biên độ tổng hợp = sqrt(Real^2 + Imag^2) - tính trên dữ liệu float TRƯỚC khi ép 8-bit để không mất độ chính xác -----
        using Mat magnitudeFloat = new Mat();
        Cv2.Magnitude(realFloat, imagFloat, magnitudeFloat);

        // ----- Bước 5: chuẩn hóa cả 3 kết quả về 8-bit để tương thích các Tool downstream (Threshold, BlobAnalysis...) -----
        Mat realOut = new Mat();
        Mat imagOut = new Mat();
        Mat magnitudeOut = new Mat();
        Cv2.ConvertScaleAbs(realFloat, realOut);
        Cv2.ConvertScaleAbs(imagFloat, imagOut);
        Cv2.ConvertScaleAbs(magnitudeFloat, magnitudeOut);

        _outImageMatrix.Value = new MatVisionImage(magnitudeOut);
        _outRealPart.Value = new MatVisionImage(realOut);
        _outImaginaryPart.Value = new MatVisionImage(imagOut);

        context.Log($"GaborFilter: kernel={k}x{k}, sigma={_sigma.Value}, theta={_theta.Value}°, lambda={_lambda.Value}, gamma={_gamma.Value}, psi={_psi.Value}°");
    }
}