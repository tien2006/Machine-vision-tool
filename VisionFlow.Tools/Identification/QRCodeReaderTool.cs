// ==================== Vai trò chính:                Tìm kiếm, phát hiện và giải mã QR code bằng OpenCV QRCodeDetector (finder patterns)
// ==================== Thành phần / Class tiêu biểu: QRCodeReaderTool
// ==================== Phụ thuộc vào:                OpenCvSharp (QRCodeDetector) + Core.Models (QrReadResult) + Core.Ports + Core.Tools - KHÔNG cần thư viện ngoài
// ==================== Pattern / Kỹ thuật nổi bật:   Multi-attempt fallback (thử nhiều góc xoay/tỷ lệ/tăng tương phản) khi detect trực tiếp thất bại

using System;
using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Models;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;
using P2 = VisionFlow.Core.Models.Point2d;

namespace VisionFlow.Tools.Identification;

/// <summary>
/// Tìm và giải mã QR code bằng <see cref="OpenCvSharp.QRCodeDetector"/> - dùng thuần OpenCV, KHÔNG cần thư viện
/// ngoài (khác OCREngine/BarcodeReader cần Tesseract/ZXing). Thuật toán dò 3 Finder Pattern (hình vuông lồng nhau
/// ở 3 góc QR) để xác định sự hiện diện + hướng, rồi giải mã dữ liệu module.
/// Quy trình:
/// 1. Xác định vùng scan: UseROI=false -> toàn ảnh; true -> cắt ROI thẳng (không hỗ trợ xoay ROI, theo đúng tài liệu).
/// 2. (Tuỳ chọn) EnablePreprocessing: CLAHE tăng tương phản trước khi thử detect lần đầu.
/// 3. Thử detect trực tiếp bằng DetectAndDecode(). Nếu thất bại và các cờ tương ứng được bật, thử lần lượt:
///    a. TryRotations: xoay ảnh ở nhiều góc (0/90/180/270 + vài góc lẻ), detect lại, quy đổi góc ngược lại.
///    b. TryScaling: resize ảnh ở vài tỷ lệ khác nhau, detect lại, quy đổi tỷ lệ ngược lại.
///    c. UseEnhancedDetection: áp CLAHE + tăng tương phản mạnh hơn, thử detect lần cuối.
/// 4. Validation: kiểm tra MinTextLength/RequiredLength, không đạt -> coi như Found = false.
/// 5. Quality: ước lượng bằng độ nét (variance of Laplacian) trong vùng tứ giác QR, chuẩn hoá 0-100.
/// 6. Vẽ overlay theo Tab Display.
/// </summary>
[ToolMetadata("QRCodeReader", DisplayName = "QR Code Reader", Category = "Identification",
    Description = "Detect and decode QR codes using OpenCV's built-in QRCodeDetector (finder pattern based).")]
public sealed class QRCodeReaderTool : VisionTool
{
    #region 1. Ports
    private readonly InputPort<IVisionImage> _input; // Ảnh đầu vào

    private readonly OutputPort<IVisionImage> _outImage;      // Ảnh chuyển tiếp (có overlay nếu bật)
    private readonly OutputPort<bool> _outFound;               // Found
    private readonly OutputPort<string> _outText;              // Text
    private readonly OutputPort<double> _outQuality;           // Quality (0-100)
    private readonly OutputPort<P2> _outPosition;               // Position (tâm QR)
    private readonly OutputPort<P2[]> _outCorners;              // Corners (4 góc)
    private readonly OutputPort<QrReadResult> _outResult;       // Kết quả tổng hợp
    #endregion

    #region 2. Parameters
    // --- Tab Region ---
    private readonly ToolParameter<bool> _useROI;
    private readonly ToolParameter<bool> _useImageCenter;
    private readonly ToolParameter<double> _regionCenterX;
    private readonly ToolParameter<double> _regionCenterY;
    private readonly ToolParameter<int> _regionWidth;
    private readonly ToolParameter<int> _regionHeight;

    // --- Tab Detection ---
    private readonly ToolParameter<bool> _tryRotations;
    private readonly ToolParameter<bool> _tryScaling;
    private readonly ToolParameter<bool> _useEnhancedDetection;

    // --- Tab Preprocessing ---
    private readonly ToolParameter<bool> _enablePreprocessing;

    // --- Tab Validation ---
    private readonly ToolParameter<int> _minTextLength;
    private readonly ToolParameter<int> _requiredLength;

    // --- Tab Display ---
    private readonly ToolParameter<bool> _drawSearchRegion;
    private readonly ToolParameter<bool> _drawResults;
    #endregion

    public QRCodeReaderTool()
    {
        _input = AddInput<IVisionImage>("Image", "Image");

        _outImage = AddOutput<IVisionImage>("Image", "Image");
        _outFound = AddOutput<bool>("Found", "Found");
        _outText = AddOutput<string>("Text", "Text");
        _outQuality = AddOutput<double>("Quality", "Quality");
        _outPosition = AddOutput<P2>("Position", "Position");
        _outCorners = AddOutput<P2[]>("Corners", "Corners");
        _outResult = AddOutput<QrReadResult>("Result", "QR Result");

        _useROI = AddParameter<bool>("UseROI", false, "Use ROI", category: "Region", order: 1);
        _useImageCenter = AddParameter<bool>("UseImageCenter", true, "Use Image Center", category: "Region", order: 2);
        _regionCenterX = AddParameter<double>("RegionCenterX", 0.0, "Region Center X", min: 0.0, max: 100000.0, category: "Region", order: 3);
        _regionCenterY = AddParameter<double>("RegionCenterY", 0.0, "Region Center Y", min: 0.0, max: 100000.0, category: "Region", order: 4);
        _regionWidth = AddParameter<int>("RegionWidth", 300, "Region Width", min: 1, max: 10000, category: "Region", order: 5);
        _regionHeight = AddParameter<int>("RegionHeight", 300, "Region Height", min: 1, max: 10000, category: "Region", order: 6);

        _tryRotations = AddParameter<bool>("TryRotations", false, "Try Rotations", category: "Detection", order: 1);
        _tryScaling = AddParameter<bool>("TryScaling", false, "Try Scaling", category: "Detection", order: 2);
        _useEnhancedDetection = AddParameter<bool>("UseEnhancedDetection", false, "Use Enhanced Detection", category: "Detection", order: 3);

        _enablePreprocessing = AddParameter<bool>("EnablePreprocessing", false, "Enable Preprocessing", category: "Preprocessing", order: 1);

        _minTextLength = AddParameter<int>("MinTextLength", 0, "Min Text Length", min: 0, max: 10000, category: "Validation", order: 1);
        _requiredLength = AddParameter<int>("RequiredLength", 0, "Required Length (0 = no check)", min: 0, max: 10000, category: "Validation", order: 2);

        _drawSearchRegion = AddParameter<bool>("DrawSearchRegion", false, "Draw Search Region", category: "Display", order: 1);
        _drawResults = AddParameter<bool>("DrawResults", true, "Draw Results", category: "Display", order: 2);
    }

    protected override void OnExecute(IToolContext context)
    {
        Mat src = _input.Value!.AsMat();

        // ----- Bước 1: Xác định vùng scan -----
        double regionX = 0, regionY = 0;
        int regionW = src.Width, regionH = src.Height;
        Mat scanArea = src;
        bool scanAreaOwned = false;

        if (_useROI.Value)
        {
            double cx = _useImageCenter.Value ? src.Width / 2.0 : _regionCenterX.Value;
            double cy = _useImageCenter.Value ? src.Height / 2.0 : _regionCenterY.Value;
            int w = Math.Max(1, _regionWidth.Value), h = Math.Max(1, _regionHeight.Value);
            regionX = cx - w / 2.0; regionY = cy - h / 2.0; regionW = w; regionH = h;

            scanArea = new Mat();
            Cv2.GetRectSubPix(src, new Size(w, h), new Point2f((float)cx, (float)cy), scanArea);
            scanAreaOwned = true;
        }

        Mat working = scanArea;
        bool workingOwned = false;
        if (_enablePreprocessing.Value)
        {
            working = ApplyClahe(scanArea);
            workingOwned = true;
        }

        // ----- Bước 3: Thử detect (trực tiếp -> xoay -> scale -> enhanced) -----
        using var detector = new QRCodeDetector();
        (bool detected, string text, Point2f[]? points) = TryDetect(detector, working);

        if (!detected && _tryRotations.Value)
            (detected, text, points) = TryWithRotations(detector, working);

        if (!detected && _tryScaling.Value)
            (detected, text, points) = TryWithScaling(detector, working);

        if (!detected && _useEnhancedDetection.Value)
        {
            using Mat enhanced = ApplyClahe(working, strong: true);
            (detected, text, points) = TryDetect(detector, enhanced);
        }

        // ----- Bước 4: Validation -----
        bool valid = detected && !string.IsNullOrEmpty(text);
        if (valid && _minTextLength.Value > 0) valid = text.Length >= _minTextLength.Value;
        if (valid && _requiredLength.Value > 0) valid = text.Length == _requiredLength.Value;

        P2[] cornersGlobal = Array.Empty<P2>();
        P2 position = default;
        double quality = 0.0;

        if (valid && points is { Length: >= 4 })
        {
            // Quy đổi 4 góc từ toạ độ vùng scan cục bộ -> toạ độ ảnh gốc (cộng offset ROI)
            cornersGlobal = points.Take(4).Select(p => new P2(regionX + p.X, regionY + p.Y)).ToArray();
            position = new P2(cornersGlobal.Average(p => p.X), cornersGlobal.Average(p => p.Y));
            quality = EstimateQuality(scanArea, points);
        }
        else
        {
            valid = false; // Không đủ 4 góc -> coi như không hợp lệ
            text = "";
        }

        var result = new QrReadResult
        {
            Found = valid,
            Text = valid ? text : "",
            Quality = quality,
            Position = position,
            Corners = cornersGlobal,
            Judge = valid ? Judge.OK : Judge.NG,
        };

        // ----- Bước 6: Vẽ overlay -----
        Mat overlay = new Mat();
        if (src.Channels() == 1) Cv2.CvtColor(src, overlay, ColorConversionCodes.GRAY2BGR); else src.CopyTo(overlay);

        if (_drawSearchRegion.Value && _useROI.Value)
            Cv2.Rectangle(overlay, new Point((int)regionX, (int)regionY), new Point((int)(regionX + regionW), (int)(regionY + regionH)),
                new Scalar(200, 200, 0), 5, LineTypes.AntiAlias);

        if (_drawResults.Value && valid)
        {
            var pts = cornersGlobal.Select(p => new Point((int)p.X, (int)p.Y)).ToArray();
            Cv2.Polylines(overlay, new[] { pts }, true, new Scalar(0, 255, 0), 2, LineTypes.AntiAlias);
            Cv2.Circle(overlay, (int)position.X, (int)position.Y, 3, new Scalar(0, 0, 255), -1, LineTypes.AntiAlias);
            Cv2.PutText(overlay, $"{text} (Q={quality:F0})", pts[0], HersheyFonts.HersheySimplex, 0.5, new Scalar(0, 255, 0), 1, LineTypes.AntiAlias);
        }

        // ----- Bước 7: Xuất kết quả -----
        // overlay "cho đi" thẳng vào Output -> KHÔNG Dispose(overlay) sau đây
        _outImage.Value = new MatVisionImage(overlay);
        _outFound.Value = valid;
        _outText.Value = valid ? text : "";
        _outQuality.Value = quality;
        _outPosition.Value = position;
        _outCorners.Value = cornersGlobal;
        _outResult.Value = result;

        if (workingOwned) working.Dispose();
        if (scanAreaOwned) scanArea.Dispose();

        context.Log($"QRCodeReader: Found={valid}, Text=\"{(valid ? text : "")}\", Quality={quality:F1}.");
    }

    #region 3. Helpers

    private static (bool Detected, string Text, Point2f[]? Points) TryDetect(QRCodeDetector detector, Mat image)
    {
        bool ok = detector.Detect(image, out Point2f[] points);
        if (!ok || points.Length < 4) return (false, "", null);
        string text = detector.Decode(image, points, new Mat());
        return (!string.IsNullOrEmpty(text), text, points);
    }

    /// <summary>Thử xoay ảnh ở vài góc cố định, detect lại, rồi quy đổi 4 góc kết quả về hệ toạ độ ảnh GỐC (chưa xoay).</summary>
    private static (bool, string, Point2f[]?) TryWithRotations(QRCodeDetector detector, Mat image)
    {
        double[] angles = { 90, 180, 270, 45, 135, 225, 315 }; // Thử trục chính trước (nhanh), rồi tới góc lẻ

        foreach (double angle in angles)
        {
            var center = new Point2f(image.Width / 2f, image.Height / 2f);
            using Mat rotMat = Cv2.GetRotationMatrix2D(center, angle, 1.0);
            using Mat rotated = new Mat();
            Cv2.WarpAffine(image, rotated, rotMat, image.Size(), InterpolationFlags.Linear, BorderTypes.Replicate);

            var (ok, text, points) = TryDetect(detector, rotated);
            if (ok && points != null)
            {
                // Quy đổi ngược 4 góc từ hệ toạ độ ảnh ĐÃ XOAY về hệ toạ độ ảnh GỐC bằng ma trận nghịch đảo
                using Mat invRot = new Mat();
                Cv2.InvertAffineTransform(rotMat, invRot);
                Point2f[] originalPoints = points.Select(p =>
                {
                    double x = invRot.At<double>(0, 0) * p.X + invRot.At<double>(0, 1) * p.Y + invRot.At<double>(0, 2);
                    double y = invRot.At<double>(1, 0) * p.X + invRot.At<double>(1, 1) * p.Y + invRot.At<double>(1, 2);
                    return new Point2f((float)x, (float)y);
                }).ToArray();
                return (true, text, originalPoints);
            }
        }
        return (false, "", null);
    }

    /// <summary>Thử resize ảnh ở vài tỷ lệ, detect lại, rồi quy đổi 4 góc kết quả về đúng tỷ lệ ảnh GỐC.</summary>
    private static (bool, string, Point2f[]?) TryWithScaling(QRCodeDetector detector, Mat image)
    {
        double[] scales = { 0.5, 1.5, 2.0, 0.75 };

        foreach (double scale in scales)
        {
            using Mat resized = new Mat();
            Cv2.Resize(image, resized, new Size(0, 0), scale, scale, InterpolationFlags.Linear);

            var (ok, text, points) = TryDetect(detector, resized);
            if (ok && points != null)
            {
                Point2f[] originalPoints = points.Select(p => new Point2f((float)(p.X / scale), (float)(p.Y / scale))).ToArray();
                return (true, text, originalPoints);
            }
        }
        return (false, "", null);
    }

    /// <summary>Tăng tương phản bằng CLAHE; strong=true dùng clipLimit cao hơn cho trường hợp UseEnhancedDetection (ảnh khó đọc).</summary>
    private static Mat ApplyClahe(Mat src, bool strong = false)
    {
        Mat gray = src.Channels() == 1 ? src.Clone() : new Mat();
        if (src.Channels() != 1) Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);

        using var clahe = Cv2.CreateCLAHE(clipLimit: strong ? 4.0 : 2.0, tileGridSize: new Size(8, 8));
        Mat enhanced = new Mat();
        clahe.Apply(gray, enhanced);
        gray.Dispose();
        return enhanced;
    }

    /// <summary>Ước lượng "chất lượng" QR bằng độ nét (variance of Laplacian) trong vùng tứ giác - proxy cho độ mờ/nhiễu, chuẩn hoá về 0-100.</summary>
    private static double EstimateQuality(Mat image, Point2f[] points)
    {
        var cvPoints = points.Take(4).Select(p => new Point((int)p.X, (int)p.Y)).ToArray();
        Rect bbox = Cv2.BoundingRect(cvPoints).Intersect(new Rect(0, 0, image.Width, image.Height));
        if (bbox.Width < 3 || bbox.Height < 3) return 0.0;

        using Mat roi = new Mat(image, bbox);
        using Mat gray = roi.Channels() == 1 ? roi.Clone() : roi.CvtColor(ColorConversionCodes.BGR2GRAY);
        using Mat lap = new Mat();
        Cv2.Laplacian(gray, lap, MatType.CV_64F);
        Cv2.MeanStdDev(lap, out _, out Scalar stdDev);

        double variance = stdDev.Val0 * stdDev.Val0;
        // Chuẩn hoá về thang 0-100: mốc 500 được chọn thực nghiệm (variance Laplacian của QR nét rõ thường > 500) - có thể tinh chỉnh theo thực tế camera của bạn
        return Math.Clamp(variance / 5.0, 0.0, 100.0);
    }

    #endregion
}