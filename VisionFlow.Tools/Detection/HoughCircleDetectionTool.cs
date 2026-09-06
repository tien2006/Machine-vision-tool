// ==================== Vai trò chính:                Phát hiện TỰ ĐỘNG nhiều hình tròn trong toàn bộ ảnh bằng Hough Transform
// ==================== Thành phần / Class tiêu biểu: HoughCircleDetectionTool
// ==================== Phụ thuộc vào:                OpenCvSharp (Cv2.HoughCircles, Cv2.Canny) + Core.Models + Core.Ports + Core.Tools + Tools.Imaging
// ==================== Pattern / Kỹ thuật nổi bật:   Wrapper hoá thuật toán Hough Circle Transform có sẵn của OpenCV + hậu xử lý (lọc độ tròn, NMS theo Sort, sub-pixel refine)

using System;
using System.Collections.Generic;
using System.Diagnostics; // Dùng Stopwatch để đo ProcessingTime (ms) - đúng như yêu cầu Output trong tài liệu mục tiêu
using System.Linq;
using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Models;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging; // Chứa extension method .AsMat() để mở hộp ảnh sang Mat của OpenCV
using P2 = VisionFlow.Core.Models.Point2d; // Định danh ngắn gọn cho Point2D, đồng bộ style với FindCircleTool/FindLineTool/BlobAnalysisTool

namespace VisionFlow.Tools.Detection;

/// <summary>
/// Công cụ phát hiện hình tròn bằng thuật toán Hough Transform (Cv2.HoughCircles của OpenCV).
/// Khác với FindCircleTool (phải khoanh 1 vùng ROI tròn kỳ vọng rồi rải Caliper dò biên thủ công),
/// HoughCircleDetectionTool quét TOÀN BỘ ảnh và có thể tìm ra NHIỀU hình tròn cùng lúc một cách tự động,
/// không cần biết trước vị trí gần đúng của vật thể.
/// Quy trình xử lý:
/// 1. Tiền xử lý ảnh (Gaussian Blur + Morphology) để giảm nhiễu, giúp Hough ổn định hơn.
/// 2. Gọi Cv2.HoughCircles để dò toàn bộ ứng viên hình tròn thô.
/// 3. Lọc theo bán kính min/max và độ tròn (circularity - dựa trên bản đồ cạnh Canny nội bộ).
/// 4. Sắp xếp theo độ tin cậy (tùy chọn), giới hạn số lượng kết quả tối đa.
/// 5. Tinh chỉnh sub-pixel (tùy chọn) bằng nội suy Parabol quanh đỉnh biên dạng bán kính.
/// 6. Vẽ overlay và xuất toàn bộ kết quả ra các cổng Output.
/// </summary>
[ToolMetadata("HoughCircleDetection", DisplayName = "Hough Circle Detection", Category = "Detection",
    Description = "Auto-detect multiple circles in the whole image using OpenCV Hough Circle Transform.")]
public sealed class HoughCircleDetectionTool : VisionTool // Lớp kín thực thi công cụ dò hình tròn tự động, kế thừa từ VisionTool
{
    #region 1. Khai báo các Cổng truyền nhận dữ liệu (Ports)
    private readonly InputPort<IVisionImage> _input; // Cổng vào: ảnh gốc (xám hoặc màu, tool tự convert sang xám nội bộ)

    private readonly OutputPort<IVisionImage> _outImage; // Cổng ra: ảnh kết quả có vẽ đè các hình tròn phát hiện được
    private readonly OutputPort<Circle[]> _outCircles; // Cổng ra: toàn bộ danh sách hình tròn (Center + Radius) đã lọc/sắp xếp
    private readonly OutputPort<int> _outCount; // Cổng ra: tổng số hình tròn tìm được (CircleCount)
    private readonly OutputPort<P2[]> _outCenters; // Cổng ra: mảng riêng các tâm hình tròn (CircleCenters) - tiện nối thẳng vào tool khác
    private readonly OutputPort<double[]> _outRadii; // Cổng ra: mảng riêng các bán kính (CircleRadii)
    private readonly OutputPort<double> _outProcessingTime; // Cổng ra: thời gian xử lý thuật toán (ms)
    private readonly OutputPort<string> _outInfo; // Cổng ra: chuỗi tóm tắt kết quả (DetectionInfo), tiện hiển thị debug/log
    #endregion

    #region 2. Khai báo các Tham số cấu hình (Parameters)
    // --- Tab Detection: 4 tham số cốt lõi của thuật toán Hough ---
    private readonly ToolParameter<string> _method; // Biến thể thuật toán: "Gradient" (mặc định) hoặc "GradientAlt"
    private readonly ToolParameter<double> _dp; // Tỷ lệ độ phân giải nghịch giữa accumulator và ảnh gốc (1.0 = chính xác nhất)
    private readonly ToolParameter<double> _minDist; // Khoảng cách tối thiểu (pixel) giữa tâm 2 hình tròn liền nhau
    private readonly ToolParameter<double> _param1; // Ngưỡng trên của Canny edge chạy ngầm bên trong Hough
    private readonly ToolParameter<double> _param2; // Ngưỡng tích lũy (accumulator threshold) - càng nhỏ càng nhiều kết quả (kể cả giả)

    // --- Tab Region: giới hạn khoảng bán kính hợp lệ ---
    private readonly ToolParameter<bool> _enableRadiusRange; // Bật/tắt cơ chế giới hạn bán kính Min/Max
    private readonly ToolParameter<double> _minRadius; // Bán kính tối thiểu được giữ lại (pixel)
    private readonly ToolParameter<double> _maxRadius; // Bán kính tối đa được giữ lại (pixel)

    // --- Tab Threshold: hậu lọc theo độ tròn thực tế (circularity) ---
    private readonly ToolParameter<bool> _enableFiltering; // Bật bộ lọc dựa trên độ tròn (quét chu vi kiểm tra tỉ lệ pixel là cạnh)
    private readonly ToolParameter<double> _minCircularity; // Độ tròn tối thiểu (0-1) để chấp nhận 1 ứng viên

    // --- Tab Advanced / Phần 1: Tiền xử lý ---
    private readonly ToolParameter<bool> _enablePreprocessing; // Bật khâu làm mượt ảnh trước khi đưa vào Hough
    private readonly ToolParameter<int> _blurKernelSize; // Kích thước kernel Gaussian Blur (bắt buộc số lẻ: 3,5,7,9...)
    private readonly ToolParameter<double> _blurSigma; // Hệ số sigma của Gaussian Blur
    private readonly ToolParameter<bool> _enableMorphology; // Bật phép Erosion + Dilation (Morphology Open) làm sạch nhiễu hạt
    private readonly ToolParameter<int> _morphKernelSize; // Kích thước kernel hình ellipse dùng cho Morphology
    private readonly ToolParameter<int> _morphIterations; // Số lần lặp lại phép Morphology

    // --- Tab Advanced / Phần 2: Hiển thị ---
    private readonly ToolParameter<bool> _drawCircles; // Vẽ đường viền các hình tròn phát hiện được
    private readonly ToolParameter<bool> _drawCenters; // Vẽ chấm nhỏ tại tâm mỗi hình tròn
    private readonly ToolParameter<bool> _drawNumbers; // Đánh số thứ tự (1,2,3...) cho từng hình tròn
    private readonly ToolParameter<int> _circleThickness; // Độ dày nét vẽ đường viền (pixel)
    private readonly ToolParameter<string> _circleColor; // Màu đường viền, định dạng chuỗi "B,G,R" (VD: "0,255,255" = vàng)
    private readonly ToolParameter<string> _centerColor; // Màu chấm tâm, định dạng chuỗi "B,G,R" (VD: "0,255,0" = xanh lá)
    private readonly ToolParameter<bool> _outputAsColorImage; // Xuất ảnh kết quả ở dạng màu (BGR) thay vì ảnh xám

    // --- Tab Advanced / Phần 3: Tinh chỉnh nâng cao ---
    private readonly ToolParameter<int> _maxCircles; // Giới hạn số lượng hình tròn tối đa trong kết quả đầu ra
    private readonly ToolParameter<bool> _sortByConfidence; // Sắp xếp kết quả theo độ tin cậy (circularity) giảm dần
    private readonly ToolParameter<bool> _enableSubPixel; // Bật tinh chỉnh bán kính độ chính xác dưới 1 pixel (nội suy Parabol)
    #endregion

    public HoughCircleDetectionTool()
    {
        // ----- Khởi tạo cổng vào/ra -----
        _input = AddInput<IVisionImage>("Image", "Image");

        _outImage = AddOutput<IVisionImage>("Image", "Image");
        _outCircles = AddOutput<Circle[]>("Circles", "Circles");
        _outCount = AddOutput<int>("CircleCount", "Circle Count");
        _outCenters = AddOutput<P2[]>("CircleCenters", "Circle Centers");
        _outRadii = AddOutput<double[]>("CircleRadii", "Circle Radii");
        _outProcessingTime = AddOutput<double>("ProcessingTime", "Processing Time (ms)");
        _outInfo = AddOutput<string>("DetectionInfo", "Detection Info");

        // ----- Tab Detection -----
        _method = AddChoiceParameter("Method", "Gradient", new[] { "Gradient", "GradientAlt" }, "Method", category: "Detection", order: 1); // Mặc định dùng biến thể cổ điển, cân bằng tốc độ/độ chính xác
        _dp = AddParameter<double>("DP", 1.0, "DP", min: 1.0, max: 4.0, category: "Detection", order: 2); // DP=1: accumulator cùng độ phân giải ảnh gốc -> chính xác nhất
        _minDist = AddParameter<double>("MinDist", 30.0, "Min Distance", min: 1.0, max: 2000.0, category: "Detection", order: 3); // Nên đặt xấp xỉ bán kính nhỏ nhất mong đợi
        _param1 = AddParameter<double>("Param1", 100.0, "Param1 (Canny High)", min: 1.0, max: 500.0, category: "Detection", order: 4); // Ngưỡng cao của Canny nội bộ trong Hough
        _param2 = AddParameter<double>("Param2", 40.0, "Param2 (Accumulator)", min: 1.0, max: 500.0, category: "Detection", order: 5); // Tham số hay chỉnh nhất: nhỏ -> nhiều kết quả hơn (kể cả giả)

        // ----- Tab Region -----
        _enableRadiusRange = AddParameter<bool>("EnableRadiusRange", true, "Enable Radius Range", category: "Region", order: 1);
        _minRadius = AddParameter<double>("MinRadius", 5.0, "Min Radius", min: 0.0, max: 5000.0, category: "Region", order: 2);
        _maxRadius = AddParameter<double>("MaxRadius", 200.0, "Max Radius", min: 0.0, max: 5000.0, category: "Region", order: 3);

        // ----- Tab Threshold -----
        _enableFiltering = AddParameter<bool>("EnableFiltering", true, "Enable Circularity Filter", category: "Threshold", order: 1);
        _minCircularity = AddParameter<double>("MinCircularity", 0.7, "Min Circularity", min: 0.0, max: 1.0, category: "Threshold", order: 2); // >=70% chu vi phải là cạnh rõ ràng

        // ----- Tab Advanced: Preprocessing -----
        _enablePreprocessing = AddParameter<bool>("EnablePreprocessing", true, "Enable Preprocessing", category: "Advanced.Preprocessing", order: 1);
        _blurKernelSize = AddParameter<int>("BlurKernelSize", 5, "Blur Kernel Size", min: 3, max: 31, category: "Advanced.Preprocessing", order: 2); // Sẽ tự ép về số lẻ khi chạy
        _blurSigma = AddParameter<double>("BlurSigma", 1.0, "Blur Sigma", min: 0.1, max: 10.0, category: "Advanced.Preprocessing", order: 3);
        _enableMorphology = AddParameter<bool>("EnableMorphology", false, "Enable Morphology", category: "Advanced.Preprocessing", order: 4);
        _morphKernelSize = AddParameter<int>("MorphKernelSize", 3, "Morph Kernel Size", min: 1, max: 21, category: "Advanced.Preprocessing", order: 5);
        _morphIterations = AddParameter<int>("MorphIterations", 1, "Morph Iterations", min: 1, max: 10, category: "Advanced.Preprocessing", order: 6);

        // ----- Tab Advanced: Display -----
        _drawCircles = AddParameter<bool>("DrawCircles", true, "Draw Circles", category: "Advanced.Display", order: 1);
        _drawCenters = AddParameter<bool>("DrawCenters", true, "Draw Centers", category: "Advanced.Display", order: 2);
        _drawNumbers = AddParameter<bool>("DrawNumbers", false, "Draw Numbers", category: "Advanced.Display", order: 3);
        _circleThickness = AddParameter<int>("CircleThickness", 2, "Circle Thickness", min: 1, max: 20, category: "Advanced.Display", order: 4);
        _circleColor = AddParameter<string>("CircleColor", "0,255,255", "Circle Color (B,G,R)", category: "Advanced.Display", order: 5); // Mặc định vàng
        _centerColor = AddParameter<string>("CenterColor", "0,255,0", "Center Color (B,G,R)", category: "Advanced.Display", order: 6); // Mặc định xanh lá
        _outputAsColorImage = AddParameter<bool>("OutputAsColorImage", true, "Output As Color Image", category: "Advanced.Display", order: 7);

        // ----- Tab Advanced: Refine -----
        _maxCircles = AddParameter<int>("MaxCircles", 50, "Max Circles", min: 1, max: 1000, category: "Advanced.Refine", order: 1);
        _sortByConfidence = AddParameter<bool>("SortByConfidence", true, "Sort By Confidence", category: "Advanced.Refine", order: 2);
        _enableSubPixel = AddParameter<bool>("EnableSubPixel", false, "Enable Sub-Pixel", category: "Advanced.Refine", order: 3);
    }

    protected override void OnExecute(IToolContext context)
    {
        var stopwatch = Stopwatch.StartNew(); // Bắt đầu đo thời gian xử lý ngay từ đầu, đúng như Output "ProcessingTime" yêu cầu

        Mat src = _input.Value!.AsMat(); // Mở hộp lấy ma trận ảnh OpenCV từ ảnh đầu vào

        // Bước 1: Chuẩn hoá ảnh về hệ xám (Hough chỉ hoạt động trên ảnh 1 kênh)
        Mat gray;
        bool grayOwned = false; // Cờ đánh dấu ma trận xám này có phải do hàm tự tạo ra hay không, để biết có cần Dispose hay không
        if (src.Channels() == 1)
        {
            gray = src;
        }
        else
        {
            gray = new Mat();
            Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
            grayOwned = true;
        }

        // Bước 2: Tiền xử lý - làm mượt + morphology để Hough ổn định hơn trên ảnh nhiễu
        Mat processed = gray.Clone(); // Luôn làm việc trên 1 bản sao để không đụng vào ảnh gray gốc (còn dùng để vẽ overlay/tính circularity)
        if (_enablePreprocessing.Value)
        {
            int k = _blurKernelSize.Value;
            if (k % 2 == 0) k += 1; // Kernel Gaussian Blur bắt buộc là số lẻ, tự động ép nếu người dùng lỡ nhập số chẵn
            Cv2.GaussianBlur(processed, processed, new Size(k, k), _blurSigma.Value);

            if (_enableMorphology.Value)
            {
                using Mat kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse,
                    new Size(_morphKernelSize.Value, _morphKernelSize.Value)); // Kernel hình ellipse, tự nhiên hơn kernel vuông cho vật thể tròn
                Cv2.MorphologyEx(processed, processed, MorphTypes.Open, kernel, iterations: _morphIterations.Value); // Open = Erode rồi Dilate, giúp xoá các đốm nhiễu nhỏ trước khi dò
            }
        }

        // Bước 3: Xây dựng bản đồ cạnh Canny riêng (dùng lại Param1 làm ngưỡng cao, đồng bộ cảm giác với Canny nội bộ của Hough)
        // Bản đồ này KHÔNG dùng để tìm tròn (việc đó do chính Cv2.HoughCircles đảm nhiệm) mà chỉ dùng để tính độ tròn (circularity) ở Bước 5.
        using Mat edgeMap = new Mat();
        Cv2.Canny(processed, edgeMap, _param1.Value / 2.0, _param1.Value);

        // Bước 4: Gọi thẳng thuật toán Hough Circle Transform có sẵn của OpenCV
        HoughModes method = _method.Value == "GradientAlt" ? HoughModes.GradientAlt : HoughModes.Gradient;
        double minR = _enableRadiusRange.Value ? _minRadius.Value : 0.0; // Khi tắt EnableRadiusRange -> chạy tự do (0,0) đúng như tài liệu mô tả
        double maxR = _enableRadiusRange.Value ? _maxRadius.Value : 0.0;

        CircleSegment[] raw = Cv2.HoughCircles(
            processed,
            method,
            _dp.Value,
            _minDist.Value,
            _param1.Value,
            _param2.Value,
            (int)minR,
            (int)maxR); // Trả về mảng CircleSegment { Center (Point2f), Radius (float) }, đã được OpenCV sắp theo độ mạnh accumulator giảm dần

        // Bước 5: Hậu xử lý - lọc theo circularity thực tế + gán điểm tin cậy cho từng ứng viên
        var candidates = new List<(Circle Circle, double Confidence)>();
        foreach (var c in raw)
        {
            double confidence = _enableFiltering.Value
                ? ComputeCircularity(edgeMap, c.Center.X, c.Center.Y, c.Radius)
                : 1.0; // Nếu không bật lọc, coi như mọi ứng viên đều đạt tối đa

            if (_enableFiltering.Value && confidence < _minCircularity.Value)
                continue; // Loại bỏ ứng viên có độ tròn thực tế quá thấp (khả năng cao là nhiễu/cạnh giả)

            double radius = c.Radius;
            var center = new P2(c.Center.X, c.Center.Y);

            // Bước 5b (tuỳ chọn): tinh chỉnh bán kính sub-pixel bằng nội suy Parabol quanh 3 mức bán kính lân cận
            if (_enableSubPixel.Value)
            {
                radius = RefineRadiusSubPixel(edgeMap, center, radius);
            }

            candidates.Add((new Circle(center, radius), confidence));
        }

        // Bước 6: Sắp xếp theo độ tin cậy nếu được yêu cầu, rồi giới hạn số lượng tối đa (MaxCircles)
        IEnumerable<(Circle Circle, double Confidence)> ordered = _sortByConfidence.Value
            ? candidates.OrderByDescending(x => x.Confidence)
            : candidates; // Nếu không sắp xếp lại, giữ nguyên thứ tự accumulator gốc của OpenCV (đã mạnh -> yếu)

        var finalList = ordered.Take(Math.Max(1, _maxCircles.Value)).ToList();

        // Bước 7: Đóng gói kết quả ra các mảng Output
        Circle[] circlesOut = finalList.Select(x => x.Circle).ToArray();
        P2[] centersOut = circlesOut.Select(c => c.Center).ToArray();
        double[] radiiOut = circlesOut.Select(c => c.Radius).ToArray();

        // Bước 8: Vẽ overlay kết quả lên ảnh (luôn vẽ trên bản BGR nội bộ cho tiện, cuối cùng mới convert lại theo OutputAsColorImage)
        Mat overlay = new Mat();
        Cv2.CvtColor(gray, overlay, ColorConversionCodes.GRAY2BGR);

        Scalar circleColor = ParseBgrColor(_circleColor.Value, Scalar.Yellow);
        Scalar centerColor = ParseBgrColor(_centerColor.Value, Scalar.LimeGreen);

        for (int i = 0; i < circlesOut.Length; i++)
        {
            var circ = circlesOut[i];
            var centerPt = new Point((int)Math.Round(circ.Center.X), (int)Math.Round(circ.Center.Y));

            if (_drawCircles.Value)
                Cv2.Circle(overlay, centerPt, (int)Math.Round(circ.Radius), circleColor, _circleThickness.Value, LineTypes.AntiAlias);

            if (_drawCenters.Value)
                Cv2.Circle(overlay, centerPt, 3, centerColor, -1, LineTypes.AntiAlias); // thickness = -1 -> tô đặc chấm tâm

            if (_drawNumbers.Value)
                Cv2.PutText(overlay, (i + 1).ToString(), new Point(centerPt.X + 6, centerPt.Y - 6),
                    HersheyFonts.HersheySimplex, 0.5, circleColor, 1, LineTypes.AntiAlias);
        }

        // Nếu người dùng chọn xuất ảnh xám thay vì ảnh màu -> convert overlay (đã vẽ) ngược lại xám,
        // các nét vẽ vẫn hiển thị nhưng chỉ còn là mức xám (không còn màu sắc trực quan như tài liệu đã lưu ý).
        //
        // QUAN TRỌNG: MatVisionImage KHÔNG clone dữ liệu, nó chỉ "bọc" (wrap) nguyên con trỏ Mat được truyền vào.
        // Vì vậy phải theo dõi CHÍNH XÁC Mat nào đã được "cho đi" (đang sống trong Output port) để KHÔNG BAO GIỜ
        // tự tay Dispose() nó trong cùng hàm này - nếu không, Output port sẽ trỏ tới vùng nhớ đã giải phóng,
        // và bất kỳ ai đọc lại ảnh sau đó (UI Preview, Tool phía sau...) sẽ crash kiểu ExecutionEngineException.
        IVisionImage finalImage;
        if (_outputAsColorImage.Value)
        {
            // overlay được "cho đi" thẳng vào Output port -> từ đây trở đi TUYỆT ĐỐI không được Dispose(overlay) nữa
            finalImage = new MatVisionImage(overlay);
        }
        else
        {
            // ConvertOverlayToGray tạo ra 1 Mat MỚI hoàn toàn (grayResult) rồi mới "cho đi" Mat đó
            // -> overlay gốc lúc này chỉ còn là dữ liệu trung gian không ai dùng nữa -> phải tự Dispose ngay để tránh rò rỉ bộ nhớ
            finalImage = ConvertOverlayToGray(overlay);
            overlay.Dispose();
        }

        // Bước 9: Tính chuỗi tóm tắt DetectionInfo
        string info;
        if (circlesOut.Length == 0)
        {
            info = "Detected 0 circles.";
        }
        else
        {
            double minRad = radiiOut.Min();
            double maxRad = radiiOut.Max();
            double avgRad = radiiOut.Average();
            info = $"Detected {circlesOut.Length} circles. Radius range: {minRad:F1} - {maxRad:F1}, Average: {avgRad:F1}";
        }

        stopwatch.Stop();

        // Bước 10: Đẩy toàn bộ kết quả ra các cổng Output
        _outImage.Value = finalImage;
        _outCircles.Value = circlesOut;
        _outCount.Value = circlesOut.Length;
        _outCenters.Value = centersOut;
        _outRadii.Value = radiiOut;
        _outProcessingTime.Value = stopwatch.Elapsed.TotalMilliseconds;
        _outInfo.Value = info;

        // Bước 11: Dọn dẹp tài nguyên Mat trung gian
        // LƯU Ý: KHÔNG Dispose(overlay) ở đây nữa - overlay đã được xử lý dứt điểm ở khối if/else phía trên
        // (hoặc đã "cho đi" vào Output port, hoặc đã tự Dispose ngay sau khi dùng xong trong nhánh gray).
        processed.Dispose();
        if (grayOwned) gray.Dispose();

        context.Log($"HoughCircleDetection: {info} (ProcessingTime={stopwatch.Elapsed.TotalMilliseconds:F1}ms)");
    }

    #region 3. Các hàm hỗ trợ thuật toán (Helpers)

    /// <summary>
    /// Ước lượng độ tròn thực tế (circularity) của 1 ứng viên hình tròn: rải N điểm đều quanh chu vi,
    /// kiểm tra tại mỗi điểm có tồn tại pixel cạnh (trong bán kính dung sai nhỏ) trên bản đồ Canny hay không,
    /// rồi trả về tỉ lệ phần trăm số điểm "trúng cạnh" trên tổng số điểm đã rải.
    /// </summary>
    private static double ComputeCircularity(Mat edgeMap, float cx, float cy, float radius)
    {
        if (radius < 1f) return 0.0; // Bán kính quá nhỏ, không đủ ý nghĩa để đánh giá

        int sampleCount = Math.Max(16, (int)(2 * Math.PI * radius / 4.0)); // Rải nhiều điểm hơn với vòng tròn lớn để đánh giá chính xác hơn
        int hit = 0;
        const int tolerance = 2; // Dung sai vài pixel quanh vị trí lý thuyết, vì biên thực tế trên ảnh hiếm khi nằm đúng tuyệt đối

        for (int i = 0; i < sampleCount; i++)
        {
            double angle = i * 2 * Math.PI / sampleCount;
            int px = (int)Math.Round(cx + radius * Math.Cos(angle));
            int py = (int)Math.Round(cy + radius * Math.Sin(angle));

            if (IsEdgeNearby(edgeMap, px, py, tolerance))
                hit++;
        }

        return (double)hit / sampleCount;
    }

    /// <summary>Kiểm tra trong 1 cửa sổ vuông nhỏ quanh (px, py) có tồn tại ít nhất 1 pixel cạnh (giá trị > 0) hay không.</summary>
    private static bool IsEdgeNearby(Mat edgeMap, int px, int py, int tolerance)
    {
        int x0 = Math.Max(0, px - tolerance);
        int x1 = Math.Min(edgeMap.Width - 1, px + tolerance);
        int y0 = Math.Max(0, py - tolerance);
        int y1 = Math.Min(edgeMap.Height - 1, py + tolerance);
        if (x0 > x1 || y0 > y1) return false; // Điểm nằm hẳn ngoài ảnh

        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
                if (edgeMap.At<byte>(y, x) > 0)
                    return true;

        return false;
    }

    /// <summary>
    /// Tinh chỉnh bán kính về độ chính xác sub-pixel: đo độ mạnh cạnh (circularity) tại bán kính (r-1, r, r+1),
    /// rồi dùng công thức nội suy Parabol (giống hệt kỹ thuật đã học ở buổi 100 - Shape-Based Matching)
    /// để tìm đỉnh thực sự nằm giữa các mức nguyên, cho ra bán kính lẻ như 24.37 thay vì chỉ 24.
    /// </summary>
    private static double RefineRadiusSubPixel(Mat edgeMap, P2 center, double radius)
    {
        if (radius < 2) return radius; // Bán kính quá nhỏ, tinh chỉnh sub-pixel không còn ý nghĩa

        double sMinus1 = ComputeCircularity(edgeMap, (float)center.X, (float)center.Y, (float)(radius - 1));
        double sCenter = ComputeCircularity(edgeMap, (float)center.X, (float)center.Y, (float)radius);
        double sPlus1 = ComputeCircularity(edgeMap, (float)center.X, (float)center.Y, (float)(radius + 1));

        double denom = sMinus1 - 2 * sCenter + sPlus1;
        if (Math.Abs(denom) < 1e-6) return radius; // Tránh chia cho 0 khi 3 điểm gần như thẳng hàng (không có đỉnh rõ ràng)

        double offset = 0.5 * (sMinus1 - sPlus1) / denom; // Công thức nội suy Parabol chuẩn (giống buổi 100)
        offset = Math.Clamp(offset, -1.0, 1.0); // Chặn offset trong khoảng hợp lý, tránh nhảy lố do nhiễu

        return radius + offset;
    }

    /// <summary>Parse chuỗi màu định dạng "B,G,R" (ví dụ "0,255,255") thành Scalar OpenCV; trả về màu mặc định nếu parse lỗi.</summary>
    private static Scalar ParseBgrColor(string text, Scalar fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;

        var parts = text.Split(',');
        if (parts.Length != 3) return fallback;

        if (byte.TryParse(parts[0].Trim(), out byte b) &&
            byte.TryParse(parts[1].Trim(), out byte g) &&
            byte.TryParse(parts[2].Trim(), out byte r))
        {
            return new Scalar(b, g, r);
        }

        return fallback; // Chuỗi nhập sai định dạng -> an toàn dùng màu mặc định thay vì crash Tool
    }

    /// <summary>Convert ảnh overlay (đã vẽ màu) về lại ảnh xám 1 kênh, dùng khi OutputAsColorImage = false.</summary>
    private static IVisionImage ConvertOverlayToGray(Mat overlay)
    {
        Mat grayResult = new Mat();
        Cv2.CvtColor(overlay, grayResult, ColorConversionCodes.BGR2GRAY);
        return new MatVisionImage(grayResult);
    }

    #endregion
}