// ==================== Vai trò chính:                Phân tích kết cấu bề mặt (texture) của 1 vùng ROI bằng GLCM (Gray-Level Co-occurrence Matrix)
// ==================== Thành phần / Class tiêu biểu: TextureAnalysisTool
// ==================== Phụ thuộc vào:                OpenCvSharp (GetRotationMatrix2D, WarpAffine, GetRectSubPix) + Core.Models (TextureAnalysisResult) + Core.Ports + Core.Tools
// ==================== Pattern / Kỹ thuật nổi bật:   GLCM tự triển khai (Contrast/Correlation/Energy/Homogeneity/Entropy) + tái sử dụng kỹ thuật cắt ROI xoay đã dùng ở PMAlignTool/RegionSelectorTool

using System;
using System.Diagnostics;
using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Models;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;

namespace VisionFlow.Tools.Detection;

/// <summary>
/// Phân tích kết cấu bề mặt (texture) bằng GLCM - KHÔNG trả lời "vật ở đâu" (như Blob/Caliper/Template Matching)
/// mà trả lời "bề mặt này có đặc trưng thế nào": mịn hay thô, đồng đều hay loang lổ, có cấu trúc hay hỗn loạn.
/// Quy trình:
/// 1. Cắt vùng ROI (có thể xoay góc RegionAngle) từ ảnh nguồn - tái dùng đúng kỹ thuật GetRotationMatrix2D +
///    WarpAffine + GetRectSubPix đã dùng ở PMAlignTool/RegionSelectorTool.
/// 2. Lượng tử hoá mức xám ROI từ 256 mức xuống GrayLevels mức (pixel * GrayLevels / 256).
/// 3. Xây ma trận đồng hiện GLCM: đếm số lần cặp pixela (i,j) cách nhau đúng Distance pixel theo hướng Angle
///    (0/45/90/135 độ) xuất hiện cạnh nhau, dùng phiên bản đối xứng (cộng cả (i,j) và (j,i)) rồi chuẩn hoá thành xác suất.
/// 4. Tính 5 đặc trưng kinh điển từ GLCM: Contrast, Correlation, Energy (ASM), Homogeneity (IDM), Entropy.
/// 5. Tính thêm thống kê cơ bản (MeanGrayLevel, StandardDeviation) trực tiếp trên ROI gốc (chưa lượng tử hoá).
/// 6. Vẽ khung ROI (thẳng hoặc xoay tuỳ RegionAngle) lên ảnh kết quả.
/// </summary>
[ToolMetadata("TextureAnalysis", DisplayName = "Texture Analysis (GLCM)", Category = "Detection",
    Description = "Analyze surface texture (smoothness, uniformity, structure) using Gray-Level Co-occurrence Matrix (GLCM).")]
public sealed class TextureAnalysisTool : VisionTool
{
    #region 1. Ports
    private readonly InputPort<IVisionImage> _input; // Ảnh đầu vào (tự convert grayscale nội bộ nếu là ảnh màu)

    private readonly OutputPort<IVisionImage> _outImage;              // Ảnh kết quả (đã vẽ khung ROI nếu DrawRegion=true)
    private readonly OutputPort<TextureAnalysisResult> _outResult;    // Kết quả tổng hợp (5 đặc trưng GLCM + thống kê + info vùng)
    #endregion

    #region 2. Parameters
    // --- Tab Region ---
    private readonly ToolParameter<double> _regionCenterX; // Toạ độ X tâm vùng phân tích
    private readonly ToolParameter<double> _regionCenterY; // Toạ độ Y tâm vùng phân tích
    private readonly ToolParameter<int> _regionWidth;       // Chiều rộng ROI
    private readonly ToolParameter<int> _regionHeight;      // Chiều cao ROI
    private readonly ToolParameter<double> _regionAngle;    // Góc xoay ROI (độ)

    // --- Tab Analysis ---
    private readonly ToolParameter<int> _distance;          // Khoảng cách (pixel) giữa cặp pixel khi xây GLCM
    private readonly ToolParameter<string> _angle;           // Hướng quan hệ cặp pixel: chỉ nhận 0/45/90/135
    private readonly ToolParameter<int> _grayLevels;         // Số mức xám dùng khi xây GLCM (1-256)

    // --- Tab Display ---
    private readonly ToolParameter<bool> _drawRegion; // Vẽ khung ROI + tâm + nhãn lên ảnh kết quả
    #endregion

    public TextureAnalysisTool()
    {
        _input = AddInput<IVisionImage>("Image", "Image");

        _outImage = AddOutput<IVisionImage>("Image", "Image (Result)");
        _outResult = AddOutput<TextureAnalysisResult>("Result", "TextureAnalysisOutputModel");

        _regionCenterX = AddParameter<double>("RegionCenterX", 200.0, "Region Center X", min: 0.0, max: 100000.0, category: "Region", order: 1);
        _regionCenterY = AddParameter<double>("RegionCenterY", 200.0, "Region Center Y", min: 0.0, max: 100000.0, category: "Region", order: 2);
        _regionWidth = AddParameter<int>("RegionWidth", 150, "Region Width", min: 1, max: 10000, category: "Region", order: 3);
        _regionHeight = AddParameter<int>("RegionHeight", 150, "Region Height", min: 1, max: 10000, category: "Region", order: 4);
        _regionAngle = AddParameter<double>("RegionAngle", 0.0, "Region Angle (deg)", min: -180.0, max: 180.0, category: "Region", order: 5);

        _distance = AddParameter<int>("Distance", 1, "Distance", min: 1, max: 32, category: "Analysis", order: 1);
        _angle = AddChoiceParameter("Angle", "0", new[] { "0", "45", "90", "135" }, "Angle", category: "Analysis", order: 2);
        _grayLevels = AddParameter<int>("GrayLevels", 64, "Gray Levels", min: 1, max: 256, category: "Analysis", order: 3);

        _drawRegion = AddParameter<bool>("DrawRegion", true, "Draw Region", category: "Display", order: 1);
    }

    protected override void OnExecute(IToolContext context)
    {
        var stopwatch = Stopwatch.StartNew();

        Mat src = _input.Value!.AsMat();
        Mat srcGray = ToGray(src, out bool srcOwned);

        double cx = _regionCenterX.Value, cy = _regionCenterY.Value;
        int w = Math.Max(1, _regionWidth.Value), h = Math.Max(1, _regionHeight.Value);
        double regionAngle = _regionAngle.Value;
        double regionX = cx - w / 2.0, regionY = cy - h / 2.0; // Đúng công thức tài liệu: x = RegionCenterX - RegionWidth/2

        // ----- Bước 1: Cắt ROI (có xoay góc nếu RegionAngle != 0) - tái dùng kỹ thuật đã có ở PMAlignTool -----
        var center = new Point2f((float)cx, (float)cy);
        var size = new Size(w, h);
        Mat rotSource = srcGray;
        bool rotated = false;
        if (Math.Abs(regionAngle) > 1e-5)
        {
            using Mat rotMat = Cv2.GetRotationMatrix2D(center, regionAngle, 1.0);
            rotSource = new Mat();
            Cv2.WarpAffine(srcGray, rotSource, rotMat, srcGray.Size(), InterpolationFlags.Linear, BorderTypes.Replicate);
            rotated = true;
        }

        using Mat roi = new Mat();
        Cv2.GetRectSubPix(rotSource, size, center, roi);
        if (rotated) rotSource.Dispose();

        bool valid = roi.Width > 1 && roi.Height > 1;

        TextureAnalysisResult result;
        if (!valid)
        {
            // ROI suy biến (kích thước quá nhỏ hoặc nằm ngoài ảnh) -> trả về kết quả Fail thay vì crash
            result = new TextureAnalysisResult
            {
                Success = false,
                Judge = Judge.NG,
                RegionX = regionX,
                RegionY = regionY,
                RegionWidth = w,
                RegionHeight = h,
                ExecutionTimeMs = stopwatch.Elapsed.TotalMilliseconds,
            };
        }
        else
        {
            // ----- Bước 2: Lượng tử hoá mức xám ROI xuống GrayLevels mức -----
            int levels = Math.Clamp(_grayLevels.Value, 1, 256);
            byte[,] quantized = new byte[roi.Height, roi.Width];
            for (int y = 0; y < roi.Height; y++)
                for (int x = 0; x < roi.Width; x++)
                    quantized[y, x] = (byte)Math.Min(levels - 1, roi.At<byte>(y, x) * levels / 256);

            // ----- Bước 3: Xác định offset (dx,dy) theo Angle (chỉ nhận 0/45/90/135, sai -> mặc định 0 theo đúng tài liệu) -----
            int distance = Math.Max(1, _distance.Value);
            (int dx, int dy) = _angle.Value switch
            {
                "45" => (distance, -distance),   // Đường chéo lên-phải
                "90" => (0, distance),           // Vertical - so với pixel bên dưới
                "135" => (-distance, -distance), // Đường chéo lên-trái
                _ => (distance, 0),               // "0" hoặc giá trị lạ -> Horizontal (đúng quy ước fallback trong tài liệu)
            };

            // ----- Bước 4: Xây GLCM đối xứng, chuẩn hoá thành ma trận xác suất -----
            double[,] glcm = BuildSymmetricGlcm(quantized, roi.Width, roi.Height, dx, dy, levels);

            // ----- Bước 5: Tính 5 đặc trưng texture kinh điển từ GLCM -----
            ComputeGlcmFeatures(glcm, levels, out double contrast, out double correlation,
                out double energy, out double homogeneity, out double entropy);

            // ----- Bước 6: Thống kê cơ bản trên ROI gốc (mức xám thật 0-255, KHÔNG dùng bản đã lượng tử hoá) -----
            Cv2.MeanStdDev(roi, out Scalar meanScalar, out Scalar stdScalar);

            result = new TextureAnalysisResult
            {
                Contrast = contrast,
                Correlation = correlation,
                Energy = energy,
                Homogeneity = homogeneity,
                Entropy = entropy,
                MeanGrayLevel = meanScalar.Val0,
                StandardDeviation = stdScalar.Val0,
                RegionX = regionX,
                RegionY = regionY,
                RegionWidth = w,
                RegionHeight = h,
                Success = true,
                Judge = Judge.OK,
                ExecutionTimeMs = stopwatch.Elapsed.TotalMilliseconds,
            };
        }

        // ----- Bước 7: Vẽ khung ROI lên ảnh kết quả -----
        Mat overlay = new Mat();
        Cv2.CvtColor(srcGray, overlay, ColorConversionCodes.GRAY2BGR);

        if (_drawRegion.Value)
        {
            Scalar green = new Scalar(0, 255, 0);
            if (Math.Abs(regionAngle) > 1e-5)
            {
                // RegionAngle != 0 -> vẽ polygon 4 đỉnh (hình chữ nhật xoay) thay vì rectangle thẳng, đúng theo tài liệu
                double rad = regionAngle * Math.PI / 180.0;
                double cosA = Math.Cos(rad), sinA = Math.Sin(rad);
                Point2f Rot(double ddx, double ddy) => new Point2f(
                    (float)(cx + ddx * cosA - ddy * sinA), (float)(cy + ddx * sinA + ddy * cosA));
                var poly = new[]
                {
                    (Point)Rot(-w / 2.0, -h / 2.0), (Point)Rot(w / 2.0, -h / 2.0),
                    (Point)Rot(w / 2.0, h / 2.0), (Point)Rot(-w / 2.0, h / 2.0),
                };
                Cv2.Polylines(overlay, new[] { poly }, true, green, 2, LineTypes.AntiAlias);
            }
            else
            {
                Cv2.Rectangle(overlay, new Point((int)regionX, (int)regionY), new Point((int)(regionX + w), (int)(regionY + h)), green, 2, LineTypes.AntiAlias);
            }

            Cv2.Circle(overlay, (int)cx, (int)cy, 3, green, -1, LineTypes.AntiAlias);
            Cv2.PutText(overlay, $"Texture ROI ({regionAngle:F0} deg)", new Point((int)(cx - w / 2.0), (int)(cy - h / 2.0) - 8),
                HersheyFonts.HersheySimplex, 0.5, green, 1, LineTypes.AntiAlias);
        }

        stopwatch.Stop();
        result.ExecutionTimeMs = stopwatch.Elapsed.TotalMilliseconds; // Cập nhật lại thời gian thực thi TOÀN BỘ (bao gồm cả vẽ overlay)

        // ----- Bước 8: Xuất kết quả -----
        // overlay "cho đi" thẳng vào Output -> KHÔNG Dispose(overlay) sau đây (bài học từ HoughCircleDetectionTool)
        _outImage.Value = new MatVisionImage(overlay);
        _outResult.Value = result;

        if (srcOwned) srcGray.Dispose();

        context.Log($"TextureAnalysis: Success={result.Success}, Contrast={result.Contrast:F3}, Energy={result.Energy:F3}, Entropy={result.Entropy:F3}, {stopwatch.Elapsed.TotalMilliseconds:F1}ms.");
    }

    #region 3. Helpers

    /// <summary>Xây GLCM đối xứng: với mỗi pixel (x,y) và pixel lân cận (x+dx,y+dy), cộng cả (i,j) và (j,i) rồi chuẩn hoá thành xác suất.</summary>
    private static double[,] BuildSymmetricGlcm(byte[,] quantized, int width, int height, int dx, int dy, int levels)
    {
        var glcm = new double[levels, levels];
        long pairCount = 0;

        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || nx >= width || ny < 0 || ny >= height) continue; // Pixel lân cận nằm ngoài ROI -> bỏ qua cặp này

                int i = quantized[y, x];
                int j = quantized[ny, nx];
                glcm[i, j] += 1.0;
                glcm[j, i] += 1.0; // Đối xứng: không quan tâm hướng đi hay về, đúng chuẩn GLCM symmetric phổ biến
                pairCount += 2;
            }

        if (pairCount > 0)
        {
            for (int i = 0; i < levels; i++)
                for (int j = 0; j < levels; j++)
                    glcm[i, j] /= pairCount; // Chuẩn hoá thành ma trận xác suất, tổng toàn bộ = 1.0
        }

        return glcm;
    }

    /// <summary>Tính 5 đặc trưng texture kinh điển (Haralick) từ ma trận GLCM đã chuẩn hoá.</summary>
    private static void ComputeGlcmFeatures(double[,] glcm, int levels,
        out double contrast, out double correlation, out double energy, out double homogeneity, out double entropy)
    {
        // Tính phân bố biên (marginal) P_i = Sigma_j glcm[i,j], dùng cho Correlation
        var marginal = new double[levels];
        for (int i = 0; i < levels; i++)
        {
            double sum = 0;
            for (int j = 0; j < levels; j++) sum += glcm[i, j];
            marginal[i] = sum;
        }

        double mu = 0;
        for (int i = 0; i < levels; i++) mu += i * marginal[i]; // Trung bình mức xám theo phân bố GLCM (đối xứng nên mu hàng = mu cột)

        double sigma2 = 0;
        for (int i = 0; i < levels; i++) sigma2 += (i - mu) * (i - mu) * marginal[i];
        double sigma = Math.Sqrt(sigma2);

        contrast = 0; energy = 0; homogeneity = 0; entropy = 0;
        double correlationSum = 0;

        for (int i = 0; i < levels; i++)
            for (int j = 0; j < levels; j++)
            {
                double p = glcm[i, j];
                if (p <= 0) continue;

                double diff = i - j;
                contrast += p * diff * diff;                          // Contrast = Sigma p(i,j) * (i-j)^2
                energy += p * p;                                      // Energy (ASM) = Sigma p(i,j)^2
                homogeneity += p / (1.0 + diff * diff);                // Homogeneity (IDM) = Sigma p(i,j) / (1+(i-j)^2)
                entropy += -p * Math.Log(p, 2);                       // Entropy = -Sigma p(i,j)*log2(p(i,j))
                correlationSum += p * (i - mu) * (j - mu);             // Tử số của Correlation
            }

        correlation = sigma > 1e-9 ? correlationSum / (sigma * sigma) : 0.0; // Correlation = Sigma p(i,j)(i-mu)(j-mu) / (sigma_i*sigma_j), đối xứng nên sigma_i=sigma_j=sigma
    }

    private static Mat ToGray(Mat src, out bool owned)
    {
        if (src.Channels() == 1) { owned = false; return src; }
        Mat gray = new Mat();
        Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
        owned = true;
        return gray;
    }

    #endregion
}