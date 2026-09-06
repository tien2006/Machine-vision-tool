// ==================== Vai trò chính:                Tìm kiếm, phát hiện và giải mã mã vạch (1D barcode: Code128, EAN, UPC...) bằng ZXing.Net
// ==================== Thành phần / Class tiêu biểu: BarcodeReaderTool
// ==================== Phụ thuộc vào:                ZXing.Net (NuGet) + OpenCvSharp + Core.Models (BarcodeReadResult, DecodedCode) + Core.Ports + Core.Tools
// ==================== Pattern / Kỹ thuật nổi bật:   Adapter Pattern cô lập lời gọi thư viện ngoài (RunZXingDecode) + tái sử dụng kỹ thuật cắt ROI xoay đã dùng ở PMAlignTool/TextureAnalysisTool

using System;
using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Models;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;

namespace VisionFlow.Tools.Identification;

/// <summary>
/// Tìm kiếm và giải mã mã vạch 1D (Code128, EAN-13, UPC-A, Code39...) trong ảnh bằng ZXing.Net.
///
/// QUAN TRỌNG - PHỤ THUỘC THƯ VIỆN NGOÀI:
/// Lời gọi thực tế vào ZXing.Net được cô lập trong hàm <see cref="RunZXingDecode"/> ở cuối file.
/// Nếu build lỗi tại đó (API lệch version ZXing.Net), CHỈ SỬA BÊN TRONG HÀM ĐÓ.
///
/// Quy trình:
/// 1. Xác định vùng scan: UseROI=false -> quét toàn ảnh; true -> cắt ROI (hỗ trợ xoay RegionAngle).
/// 2. (Tuỳ chọn) Tiền xử lý: CLAHE tăng tương phản + khử nhiễu nhẹ nếu EnablePreprocessing.
/// 3. Gọi ZXing.Net decode ĐA MÃ (DecodeMultiple) với TryHarder/TryInverted theo cấu hình.
/// 4. Quy đổi toạ độ kết quả (đang tính trên ROI cục bộ) về toạ độ ảnh gốc.
/// 5. Vẽ overlay (vùng search + khung/text từng mã) theo Tab Display.
/// </summary>
[ToolMetadata("BarcodeReader", DisplayName = "Barcode Reader", Category = "Identification",
    Description = "Detect and decode 1D barcodes (Code128, EAN, UPC...) using ZXing.Net.")]
public sealed class BarcodeReaderTool : VisionTool
{
    #region 1. Ports
    private readonly InputPort<IVisionImage> _input; // Ảnh đầu vào chứa barcode cần đọc

    private readonly OutputPort<IVisionImage> _outImage;             // Ảnh output (có overlay nếu bật)
    private readonly OutputPort<bool> _outFound;                     // Found
    private readonly OutputPort<string> _outText;                    // Text - mã đầu tiên/tốt nhất tìm được (rỗng nếu Found=false)
    private readonly OutputPort<int> _outCount;                      // Count
    private readonly OutputPort<BarcodeReadResult> _outResult;       // Toàn bộ danh sách mã tìm được (đa mã)
    #endregion

    #region 2. Parameters
    // --- Tab Region ---
    private readonly ToolParameter<bool> _useROI;
    private readonly ToolParameter<bool> _useImageCenter;
    private readonly ToolParameter<double> _regionCenterX;
    private readonly ToolParameter<double> _regionCenterY;
    private readonly ToolParameter<int> _regionWidth;
    private readonly ToolParameter<int> _regionHeight;
    private readonly ToolParameter<double> _regionAngle;

    // --- Tab Detection ---
    private readonly ToolParameter<bool> _tryHarder;
    private readonly ToolParameter<bool> _tryInverted;

    // --- Tab Preprocessing ---
    private readonly ToolParameter<bool> _enablePreprocessing;

    // --- Tab Display ---
    private readonly ToolParameter<bool> _drawSearchRegion;
    private readonly ToolParameter<bool> _drawResults;

    // --- Tab Debug ---
    private readonly ToolParameter<bool> _debugMode;
    #endregion

    public BarcodeReaderTool()
    {
        _input = AddInput<IVisionImage>("Image", "Image");

        _outImage = AddOutput<IVisionImage>("Image", "Image");
        _outFound = AddOutput<bool>("Found", "Found");
        _outText = AddOutput<string>("Text", "Text");
        _outCount = AddOutput<int>("Count", "Count");
        _outResult = AddOutput<BarcodeReadResult>("Result", "Barcode Result");

        _useROI = AddParameter<bool>("UseROI", false, "Use ROI", category: "Region", order: 1);
        _useImageCenter = AddParameter<bool>("UseImageCenter", true, "Use Image Center", category: "Region", order: 2);
        _regionCenterX = AddParameter<double>("RegionCenterX", 0.0, "Region Center X", min: 0.0, max: 100000.0, category: "Region", order: 3);
        _regionCenterY = AddParameter<double>("RegionCenterY", 0.0, "Region Center Y", min: 0.0, max: 100000.0, category: "Region", order: 4);
        _regionWidth = AddParameter<int>("RegionWidth", 300, "Region Width", min: 1, max: 10000, category: "Region", order: 5);
        _regionHeight = AddParameter<int>("RegionHeight", 150, "Region Height", min: 1, max: 10000, category: "Region", order: 6);
        _regionAngle = AddParameter<double>("RegionAngle", 0.0, "Region Angle (deg)", min: -180.0, max: 180.0, category: "Region", order: 7);

        _tryHarder = AddParameter<bool>("TryHarder", true, "Try Harder", category: "Detection", order: 1);
        _tryInverted = AddParameter<bool>("TryInverted", false, "Try Inverted", category: "Detection", order: 2);

        _enablePreprocessing = AddParameter<bool>("EnablePreprocessing", false, "Enable Preprocessing", category: "Preprocessing", order: 1);

        _drawSearchRegion = AddParameter<bool>("DrawSearchRegion", false, "Draw Search Region", category: "Display", order: 1);
        _drawResults = AddParameter<bool>("DrawResults", true, "Draw Results", category: "Display", order: 2);

        _debugMode = AddParameter<bool>("DebugMode", false, "Debug Mode", category: "Debug", order: 1);
    }

    protected override void OnExecute(IToolContext context)
    {
        Mat src = _input.Value!.AsMat();

        // ----- Bước 1: Xác định vùng scan (toàn ảnh hoặc ROI có xoay) -----
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

            scanArea = CropRotatedRoi(src, cx, cy, w, h, _regionAngle.Value);
            scanAreaOwned = true;
        }

        // ----- Bước 2: Tiền xử lý (tuỳ chọn) -----
        Mat processed = scanArea;
        bool processedOwned = false;
        if (_enablePreprocessing.Value)
        {
            processed = PreprocessForBarcode(scanArea);
            processedOwned = true;
        }

        // ----- Bước 3: Chạy ZXing.Net decode đa mã -----
        List<(string Text, Rect Box)> raw = RunZXingDecode(processed, _tryHarder.Value, _tryInverted.Value);

        if (_debugMode.Value)
            context.Log($"BarcodeReader [DEBUG]: quét vùng {regionW}x{regionH} tại ({regionX:F0},{regionY:F0}), tìm thấy {raw.Count} candidate thô.");

        // ----- Bước 4: Quy đổi toạ độ ROI cục bộ -> ảnh gốc -----
        var codes = raw.Select(r => new DecodedCode(r.Text,
            new RectRegion(regionX + r.Box.X, regionY + r.Box.Y, r.Box.Width, r.Box.Height))).ToList();

        bool found = codes.Count > 0;
        string primaryText = found ? codes[0].Text : "";

        var result = new BarcodeReadResult { Codes = codes, Found = found, Judge = found ? Judge.OK : Judge.NG };

        // ----- Bước 5: Vẽ overlay -----
        Mat overlay = new Mat();
        if (src.Channels() == 1) Cv2.CvtColor(src, overlay, ColorConversionCodes.GRAY2BGR); else src.CopyTo(overlay);

        if (_drawSearchRegion.Value && _useROI.Value)
            Cv2.Rectangle(overlay, new Point((int)regionX, (int)regionY), new Point((int)(regionX + regionW), (int)(regionY + regionH)),
                new Scalar(200, 200, 0), 5, LineTypes.AntiAlias);

        if (_drawResults.Value)
        {
            foreach (var code in codes)
            {
                var pt1 = new Point((int)code.BoundingBox.X, (int)code.BoundingBox.Y);
                var pt2 = new Point((int)(code.BoundingBox.X + code.BoundingBox.Width), (int)(code.BoundingBox.Y + code.BoundingBox.Height));
                Cv2.Rectangle(overlay, pt1, pt2, new Scalar(0, 255, 0), 2, LineTypes.AntiAlias);
                Cv2.PutText(overlay, code.Text, new Point(pt1.X, Math.Max(12, pt1.Y - 6)),
                    HersheyFonts.HersheySimplex, 0.5, new Scalar(0, 255, 0), 1, LineTypes.AntiAlias);
            }
        }

        // ----- Bước 6: Xuất kết quả -----
        // overlay "cho đi" thẳng vào Output -> KHÔNG Dispose(overlay) sau đây
        _outImage.Value = new MatVisionImage(overlay);
        _outFound.Value = found;
        _outText.Value = primaryText;
        _outCount.Value = codes.Count;
        _outResult.Value = result;

        if (processedOwned) processed.Dispose();
        if (scanAreaOwned) scanArea.Dispose();

        context.Log($"BarcodeReader: Found={found}, Count={codes.Count}, Text=\"{primaryText}\".");
    }

    #region 3. Helpers

    /// <summary>Cắt ROI có xoay - tái dùng đúng kỹ thuật GetRotationMatrix2D + WarpAffine + GetRectSubPix đã dùng ở PMAlignTool/TextureAnalysisTool.</summary>
    private static Mat CropRotatedRoi(Mat src, double cx, double cy, int w, int h, double angleDeg)
    {
        var center = new Point2f((float)cx, (float)cy);
        Mat source = src;
        bool rotated = false;

        if (Math.Abs(angleDeg) > 1e-5)
        {
            using Mat rotMat = Cv2.GetRotationMatrix2D(center, angleDeg, 1.0);
            source = new Mat();
            Cv2.WarpAffine(src, source, rotMat, src.Size(), InterpolationFlags.Linear, BorderTypes.Replicate);
            rotated = true;
        }

        Mat roi = new Mat();
        Cv2.GetRectSubPix(source, new Size(w, h), center, roi);
        if (rotated) source.Dispose();
        return roi;
    }

    /// <summary>Tăng tương phản (CLAHE) + khử nhiễu nhẹ để barcode dễ đọc hơn trong điều kiện ảnh xấu.</summary>
    private static Mat PreprocessForBarcode(Mat src)
    {
        Mat gray = src.Channels() == 1 ? src.Clone() : new Mat();
        if (src.Channels() != 1) Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);

        using var clahe = Cv2.CreateCLAHE(clipLimit: 2.0, tileGridSize: new Size(8, 8));
        Mat enhanced = new Mat();
        clahe.Apply(gray, enhanced);
        Cv2.MedianBlur(enhanced, enhanced, 3); // Khử nhiễu hạt nhẹ, không làm mờ mất cạnh vạch

        gray.Dispose();
        return enhanced;
    }

    /// <summary>
    /// ĐIỂM TÍCH HỢP DUY NHẤT với ZXing.Net. Nếu build lỗi ở đây (API lệch version), CHỈ SỬA BÊN TRONG HÀM NÀY.
    /// Trả về danh sách (text giải mã, khung bao) theo toạ độ CỤC BỘ của ảnh truyền vào (chưa cộng offset ROI).
    /// </summary>
    private static List<(string Text, Rect Box)> RunZXingDecode(Mat gray, bool tryHarder, bool tryInverted)
    {
        var results = new List<(string, Rect)>();

        Mat grayMat = gray.Channels() == 1 ? gray : gray.CvtColor(ColorConversionCodes.BGR2GRAY);

        // QUAN TRỌNG: Marshal.Copy bên dưới giả định mỗi hàng pixel nằm LIÊN TỤC trong bộ nhớ (stride == width).
        // Nếu grayMat là 1 sub-view/ROI của Mat khác (VD ảnh đã qua crop/rotate ở tool trước), Mat.IsContinuous()
        // có thể trả về false (mỗi hàng có "đệm" dư) -> copy thẳng byte kiểu này sẽ đọc SAI DẦN từng hàng xuống dưới,
        // khiến ảnh gửi cho ZXing bị nhiễu vụn dù ảnh preview vẫn hiển thị đúng bình thường (preview không đi qua
        // đường copy byte này). Phải ép Clone() ra bản liên tục trước khi Marshal.Copy để tránh lỗi "chạy được,
        // không lỗi, nhưng decode luôn ra rỗng" y hệt trường hợp thực tế đã gặp.
        bool grayMatOwned = grayMat != gray; // true nếu CvtColor ở trên đã tạo Mat mới (cần Dispose sau khi dùng xong)
        if (!grayMat.IsContinuous())
        {
            Mat continuous = grayMat.Clone(); // Clone() luôn trả về Mat liên tục
            if (grayMatOwned) grayMat.Dispose();
            grayMat = continuous;
            grayMatOwned = true;
        }

        byte[] pixelBytes = new byte[grayMat.Width * grayMat.Height];
        System.Runtime.InteropServices.Marshal.Copy(grayMat.Data, pixelBytes, 0, pixelBytes.Length);
        var luminanceSource = new ZXing.RGBLuminanceSource(pixelBytes, grayMat.Width, grayMat.Height, ZXing.RGBLuminanceSource.BitmapFormat.Gray8);

        var reader = new ZXing.BarcodeReaderGeneric
        {
            Options = new ZXing.Common.DecodingOptions
            {
                TryHarder = tryHarder,
                TryInverted = tryInverted,
                PossibleFormats = new[] { ZXing.BarcodeFormat.CODE_128, ZXing.BarcodeFormat.EAN_13, ZXing.BarcodeFormat.EAN_8,
                     ZXing.BarcodeFormat.CODE_39, ZXing.BarcodeFormat.UPC_A, ZXing.BarcodeFormat.UPC_E, ZXing.BarcodeFormat.ITF },
            }
        };

        ZXing.Result[] zxingResults = reader.DecodeMultiple(luminanceSource) ?? Array.Empty<ZXing.Result>();
        foreach (var r in zxingResults)
        {
            var pts = r.ResultPoints;
            if (pts == null || pts.Length == 0) continue;
            float minX = pts.Min(p => p.X), maxX = pts.Max(p => p.X);
            float minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
            var box = new Rect((int)minX, (int)minY, Math.Max(1, (int)(maxX - minX)), Math.Max(1, (int)(maxY - minY)));
            results.Add((r.Text, box));
        }

        if (grayMatOwned) grayMat.Dispose(); // Dọn dẹp Mat trung gian tự tạo (từ CvtColor hoặc Clone) sau khi đã copy xong byte

        return results;

        //throw new ToolExecutionException(
        //    "BarcodeReader: chưa cấu hình lời gọi ZXing.Net thật. Mở hàm RunZXingDecode() trong BarcodeReaderTool.cs, " +
        //    "bỏ dòng throw này và điền đúng API (xem comment mẫu ngay phía trên). Nhớ cài NuGet 'ZXing.Net'.");
    }

    #endregion
}