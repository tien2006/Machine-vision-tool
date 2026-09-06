// ==================== Vai trò chính:                Nhận dạng ký tự quang học (OCR) trong 1 vùng ROI bằng Tesseract OCR Engine
// ==================== Thành phần / Class tiêu biểu: OCREngineTool
// ==================== Phụ thuộc vào:                Tesseract (NuGet) + OpenCvSharp + Core.Models (OcrResult) + Core.Ports + Core.Tools
// ==================== Pattern / Kỹ thuật nổi bật:   Adapter Pattern cô lập lời gọi thư viện ngoài (RunTesseractOcr) + hợp khung các từ detect được thành 1 BoundingBox tổng

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Models;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;

namespace VisionFlow.Tools.Identification;

/// <summary>
/// Nhận dạng ký tự quang học (OCR): chuyển vùng ảnh chứa chữ/số thành chuỗi text máy tính đọc được, kèm độ tin
/// cậy (Confidence) và khung bao (BoundingBox) vùng text thực sự detect được.
///
/// QUAN TRỌNG - PHỤ THUỘC THƯ VIỆN NGOÀI:
/// Lời gọi thực tế vào thư viện Tesseract được cô lập trong hàm <see cref="RunTesseractOcr"/> ở cuối file.
/// Nếu build lỗi tại đó (API lệch version Tesseract .NET wrapper bạn cài), CHỈ SỬA BÊN TRONG HÀM ĐÓ.
///
/// Quy trình:
/// 1. Xác định vùng ROI (UseImageCenter hoặc RegionCenterX/Y + RegionWidth/Height) rồi cắt ra khỏi ảnh gốc.
/// 2. Chạy Tesseract trên vùng ROI theo Language + PageSegmentationMode đã cấu hình.
/// 3. Lấy danh sách từ (word) kèm khung + confidence riêng từng từ, hợp (union) toàn bộ khung từ thành 1 BoundingBox tổng.
/// 4. Ghép toàn bộ text các từ lại, tính Confidence trung bình.
/// 5. So Confidence với MinConfidence: dưới ngưỡng -> NeedsLabeling = true, coi như kết quả không đạt.
/// 6. Vẽ overlay (vùng search + khung text detect được) theo Tab Display.
/// </summary>
[ToolMetadata("OCREngine", DisplayName = "OCR Engine", Category = "Identification",
    Description = "Optical Character Recognition (OCR) on a region of interest using Tesseract.")]
public sealed class OCREngineTool : VisionTool
{
    #region 1. Ports
    private readonly InputPort<IVisionImage> _input; // Ảnh đầu vào

    private readonly OutputPort<IVisionImage> _outImage;         // Ảnh chuyển tiếp (có vẽ overlay nếu bật)
    private readonly OutputPort<string> _outText;                // RecognizedText
    private readonly OutputPort<double> _outConfidence;          // Confidence (0-1)
    private readonly OutputPort<RectRegion> _outBoundingBox;     // BoundingBox vùng text detect được
    private readonly OutputPort<double> _outProcessingTime;      // ProcessingTime (ms)
    private readonly OutputPort<bool> _outNeedsLabeling;         // NeedsLabeling
    private readonly OutputPort<OcrResult> _outResult;           // Kết quả tổng hợp (tiện nối 1 cổng duy nhất)
    #endregion

    #region 2. Parameters
    // --- Tab Region ---
    private readonly ToolParameter<bool> _useImageCenter;   // true -> tự đặt tâm ROI = tâm ảnh
    private readonly ToolParameter<double> _regionCenterX;
    private readonly ToolParameter<double> _regionCenterY;
    private readonly ToolParameter<int> _regionWidth;
    private readonly ToolParameter<int> _regionHeight;

    // --- Tab Setting ---
    private readonly ToolParameter<string> _language;              // Mã ngôn ngữ Tesseract: eng, vie, vie+eng, chi_sim...
    private readonly ToolParameter<string> _pageSegmentationMode;   // "3".."13" theo PSM Tesseract
    private readonly ToolParameter<double> _minConfidence;          // Ngưỡng confidence tối thiểu để chấp nhận kết quả
    private readonly ToolParameter<string> _tessDataPath;           // Đường dẫn thư mục chứa file .traineddata (yêu cầu thực tế của Tesseract, tài liệu không nêu tên tham số cụ thể nên đặt tên rõ nghĩa)

    // --- Tab Display ---
    private readonly ToolParameter<bool> _drawSearchRegion;
    private readonly ToolParameter<bool> _drawBoundingBox;
    #endregion

    public OCREngineTool()
    {
        _input = AddInput<IVisionImage>("Image", "Image");

        _outImage = AddOutput<IVisionImage>("Image", "Image");
        _outText = AddOutput<string>("RecognizedText", "Recognized Text");
        _outConfidence = AddOutput<double>("Confidence", "Confidence");
        _outBoundingBox = AddOutput<RectRegion>("BoundingBox", "Bounding Box");
        _outProcessingTime = AddOutput<double>("ProcessingTime", "Processing Time (ms)");
        _outNeedsLabeling = AddOutput<bool>("NeedsLabeling", "Needs Labeling");
        _outResult = AddOutput<OcrResult>("Result", "OCR Result");

        _useImageCenter = AddParameter<bool>("UseImageCenter", true, "Use Image Center", category: "Region", order: 1);
        _regionCenterX = AddParameter<double>("RegionCenterX", 0.0, "Region Center X", min: 0.0, max: 100000.0, category: "Region", order: 2);
        _regionCenterY = AddParameter<double>("RegionCenterY", 0.0, "Region Center Y", min: 0.0, max: 100000.0, category: "Region", order: 3);
        _regionWidth = AddParameter<int>("RegionWidth", 300, "Region Width", min: 1, max: 10000, category: "Region", order: 4);
        _regionHeight = AddParameter<int>("RegionHeight", 100, "Region Height", min: 1, max: 10000, category: "Region", order: 5);

        _language = AddChoiceParameter("Language", "eng",
            new[] { "eng", "vie", "vie+eng", "chi_sim", "chi_tra", "jpn", "kor", "fra", "deu", "spa" },
            "Language", category: "Setting", order: 1);
        _pageSegmentationMode = AddChoiceParameter("PageSegmentationMode", "6",
            new[] { "3", "6", "7", "8", "9", "10", "11", "12", "13" }, "Page Segmentation Mode", category: "Setting", order: 2);
        _minConfidence = AddParameter<double>("MinConfidence", 0.5, "Min Confidence", min: 0.0, max: 1.0, category: "Setting", order: 3);
        _tessDataPath = AddParameter<string>("TessDataPath", "./tessdata", "TessData Path", category: "Setting", order: 4);

        _drawSearchRegion = AddParameter<bool>("DrawSearchRegion", true, "Draw Search Region", category: "Display", order: 1);
        _drawBoundingBox = AddParameter<bool>("DrawBoundingBox", true, "Draw Bounding Box", category: "Display", order: 2);
    }

    protected override void OnExecute(IToolContext context)
    {
        var stopwatch = Stopwatch.StartNew();

        Mat src = _input.Value!.AsMat();

        // ----- Bước 1: Xác định + cắt vùng ROI -----
        double cx = _useImageCenter.Value ? src.Width / 2.0 : _regionCenterX.Value;
        double cy = _useImageCenter.Value ? src.Height / 2.0 : _regionCenterY.Value;
        int w = Math.Max(1, _regionWidth.Value), h = Math.Max(1, _regionHeight.Value);
        double regionX = cx - w / 2.0, regionY = cy - h / 2.0;

        using Mat roi = new Mat();
        Cv2.GetRectSubPix(src, new Size(w, h), new Point2f((float)cx, (float)cy), roi);

        // ----- Bước 2+3: Chạy Tesseract, lấy text + confidence + khung từng từ -----
        (string fullText, double avgConfidence, List<Rect> wordBoxes) = RunTesseractOcr(
            roi, _language.Value, int.Parse(_pageSegmentationMode.Value), _tessDataPath.Value);

        // ----- Bước 3b: Hợp toàn bộ khung từ thành 1 BoundingBox tổng, quy đổi về toạ độ ảnh gốc (cộng offset ROI) -----
        RectRegion boundingBox;
        if (wordBoxes.Count > 0)
        {
            int minX = wordBoxes.Min(r => r.X), minY = wordBoxes.Min(r => r.Y);
            int maxX = wordBoxes.Max(r => r.X + r.Width), maxY = wordBoxes.Max(r => r.Y + r.Height);
            boundingBox = new RectRegion(regionX + minX, regionY + minY, maxX - minX, maxY - minY);
        }
        else
        {
            boundingBox = new RectRegion(regionX, regionY, w, h); // Không detect được từ nào -> trả về nguyên vùng ROI đã search
        }

        // ----- Bước 5: Kiểm tra MinConfidence -----
        bool needsLabeling = string.IsNullOrWhiteSpace(fullText) || avgConfidence < _minConfidence.Value;
        string finalText = needsLabeling ? "" : fullText.Trim();

        stopwatch.Stop();

        var result = new OcrResult
        {
            RecognizedText = finalText,
            Confidence = avgConfidence,
            BoundingBox = boundingBox,
            ProcessingTimeMs = stopwatch.Elapsed.TotalMilliseconds,
            NeedsLabeling = needsLabeling,
            Judge = needsLabeling ? Judge.NG : Judge.OK,
        };

        // ----- Bước 6: Vẽ overlay -----
        Mat overlay = new Mat();
        if (src.Channels() == 1) Cv2.CvtColor(src, overlay, ColorConversionCodes.GRAY2BGR); else src.CopyTo(overlay);

        if (_drawSearchRegion.Value)
            Cv2.Rectangle(overlay, new Point((int)regionX, (int)regionY), new Point((int)(regionX + w), (int)(regionY + h)),
                new Scalar(200, 200, 0), 1, LineTypes.AntiAlias);

        if (_drawBoundingBox.Value && !needsLabeling)
        {
            var bboxPt1 = new Point((int)boundingBox.X, (int)boundingBox.Y);
            var bboxPt2 = new Point((int)(boundingBox.X + boundingBox.Width), (int)(boundingBox.Y + boundingBox.Height));
            Cv2.Rectangle(overlay, bboxPt1, bboxPt2, new Scalar(0, 255, 0), 2, LineTypes.AntiAlias);
            Cv2.PutText(overlay, $"{finalText} ({avgConfidence * 100:F0}%)", new Point(bboxPt1.X, Math.Max(12, bboxPt1.Y - 6)),
                HersheyFonts.HersheySimplex, 0.5, new Scalar(0, 255, 0), 1, LineTypes.AntiAlias);
        }

        // ----- Bước 7: Xuất kết quả -----
        // overlay "cho đi" thẳng vào Output -> KHÔNG Dispose(overlay) sau đây
        _outImage.Value = new MatVisionImage(overlay);
        _outText.Value = finalText;
        _outConfidence.Value = avgConfidence;
        _outBoundingBox.Value = boundingBox;
        _outProcessingTime.Value = stopwatch.Elapsed.TotalMilliseconds;
        _outNeedsLabeling.Value = needsLabeling;
        _outResult.Value = result;

        context.Log($"OCREngine: \"{finalText}\" Confidence={avgConfidence:F2} NeedsLabeling={needsLabeling} {stopwatch.Elapsed.TotalMilliseconds:F1}ms.");
    }

    #region 3. Helpers

    /// <summary>
    /// ĐIỂM TÍCH HỢP DUY NHẤT với thư viện Tesseract .NET wrapper. Nếu build lỗi ở đây (API lệch version),
    /// CHỈ SỬA BÊN TRONG HÀM NÀY - phần Ports/Parameters/luồng xử lý phía trên KHÔNG cần đổi.
    /// Trả về: (toàn bộ text ghép từ các từ detect được, confidence trung bình 0-1, danh sách khung từng từ theo toạ độ ROI cục bộ).
    /// </summary>
    private static (string Text, double Confidence, List<Rect> WordBoxes) RunTesseractOcr(Mat roi, string language, int psm, string tessDataPath)
    {
        using var engine = new Tesseract.TesseractEngine(tessDataPath, language, Tesseract.EngineMode.Default);
        engine.SetVariable("tessedit_pageseg_mode", psm.ToString());

        // Chuyển OpenCvSharp Mat sang mảng byte PNG trực tiếp, không cần System.Drawing hay Extensions
        byte[] imageBytes = roi.ImEncode(".png");
        using var pix = Tesseract.Pix.LoadFromMemory(imageBytes);
        using var page = engine.Process(pix, (Tesseract.PageSegMode)psm);

        var sb = new System.Text.StringBuilder();
        var boxes = new List<Rect>();
        var confidences = new List<double>();

        using var iter = page.GetIterator();
        iter.Begin();
        do
        {
            // Dùng GetText thay vì TryGetText
            string word = iter.GetText(Tesseract.PageIteratorLevel.Word);
            if (!string.IsNullOrWhiteSpace(word))
            {
                sb.Append(word);

                // Tesseract trả về confidence từ 0 đến 100, quy đổi về 0.0 - 1.0
                double conf = iter.GetConfidence(Tesseract.PageIteratorLevel.Word) / 100.0;
                confidences.Add(conf);

                if (iter.TryGetBoundingBox(Tesseract.PageIteratorLevel.Word, out Tesseract.Rect r))
                {
                    boxes.Add(new Rect(r.X1, r.Y1, r.Width, r.Height));
                }
            }
        } while (iter.Next(Tesseract.PageIteratorLevel.Word));

        double avgConf = confidences.Count > 0 ? confidences.Average() : 0.0;
        return (sb.ToString().Trim(), avgConf, boxes);

        //throw new ToolExecutionException(
        //    "OCREngine: chưa cấu hình lời gọi Tesseract thật. Mở hàm RunTesseractOcr() trong OCREngineTool.cs, " +
        //    "bỏ dòng throw này và điền đúng API của bản Tesseract .NET wrapper bạn đang cài (xem comment mẫu ngay phía trên). " +
        //    "Nhớ cài NuGet 'Tesseract' + đặt file .traineddata (VD: eng.traineddata) vào đúng TessDataPath.");
    }

    #endregion
}