// ==================== Vai trò chính:                Tìm và đếm các "vật" (blob) trong ảnh bằng phân ngưỡng sáng-tối + phân tích contour/hierarchy, hỗ trợ vùng Inclusion/Exclusion và chế độ Blob/Hole/All
// ==================== Thành phần / Class tiêu biểu: BlobAnalysisTool
// ==================== Phụ thuộc vào:                OpenCvSharp (FindContours + Hierarchy, FitEllipse, ConvexHull) + Core.Models (VisionBlob, BlobAnalysisResult, ExclusionPolygonInfo) + System.Text.Json
// ==================== Pattern / Kỹ thuật nổi bật:   Contour Hierarchy (RetrievalModes.CComp) để tách Blob/Hole tự nhiên + Inclusion/Exclusion Region Masking + Sub-pixel qua Upscale-rồi-Rescale-ngược
//                                                     + ROI vẽ chuột (RotatedRectRegion, enabledWhen="UseROI") được gộp runtime vào ExclusionRegions mỗi lần chạy
//                                                     + Tự giới hạn ngân sách pixel khi Sub-Pixel bật quá cao, tránh treo máy (bổ sung theo đúng tinh thần ProcessingTimeout trong tài liệu)

using log4net.Core;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Models;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;
using static OpenCvSharp.LineIterator;
using P2 = VisionFlow.Core.Models.Point2d;

namespace VisionFlow.Tools.Detection;

/// <summary>
/// Công cụ phân tích Blob (Connected-Component Analysis) - phiên bản đầy đủ theo đúng Tài liệu thuật toán mục tiêu:
/// 1. (Tuỳ chọn) Tiền xử lý sub-pixel: làm mịn biên (EdgeRefinementMethod) rồi phóng to ảnh InterpolationFactor lần
///    - CÓ TỰ ĐỘNG HẠ scaleFactor nếu ngân sách pixel (Width*Height*scale²) vượt mức an toàn, tránh treo/OOM.
/// 2. Nhị phân hoá bằng BinaryThreshold đơn giản (KHÔNG có Otsu built-in - cần Otsu/Adaptive thì dùng Threshold
///    tool riêng rồi đưa ảnh binary vào, đúng như tài liệu ghi chú).
/// 3. (Tuỳ chọn) NoiseReduction: Open rồi Close morphology nhẹ để gỡ nhiễu chấm pixel.
/// 3.5. ROI vẽ chuột (tab "Region", chỉ áp dụng khi UseROI=true) được gộp (Insert đầu danh sách) vào tập hợp Region
///      TRƯỚC khi tách Inclusion/Exclusion - xem đoạn "Gộp ROI vẽ chuột" trong OnExecute. UseROI=false -> bỏ qua
///      hoàn toàn, không mask, không vẽ, không xuất ra ExclusionPolygons (= quét full ảnh, đúng tinh thần "Off = Full screen").
/// 4. Áp Inclusion Region (nếu có: AND với binary) rồi Exclusion Region (tô đen đè lên binary).
/// 5. Tách Blob/Hole tự nhiên bằng Cv2.FindContours với RetrievalModes.CComp (2 tầng hierarchy):
///    contour có Parent = -1 là Blob (vùng sáng/vật), contour có Parent != -1 là Hole (lỗ bên trong vật).
///    ConnectivityLabel quyết định giữ Blob-only / Hole-only / All (cả 2).
/// 6. Tính đầy đủ đặc trưng hình học từng contour rồi RESCALE NGƯỢC về đúng toạ độ ảnh gốc (nếu có dùng sub-pixel).
/// 7. Lọc MinArea/MaxArea, sắp xếp SortBy, giới hạn MaxBlobCount.
/// 8. Vẽ 2 lớp overlay riêng: ImageMatrix (đè lên ảnh gốc) và InteractiveVisualization (đè lên nền ĐEN - để
///    OverlayRendererTool ghép lại sau này bằng kỹ thuật "đen = trong suốt").
///
/// LƯU Ý QUAN TRỌNG VỀ UI: tham số _roi khai báo enabledWhen: "UseROI" - đây là quy ước MỚI dùng chung của
/// framework (xem patch ParametersWindowViewModel.HasRoi) để khung ROI tương tác trên Viewport tự ẩn khi
/// UseROI=false. Nếu môi trường build của bạn CHƯA áp patch đó, tham số enabledWhen sẽ bị bỏ qua an toàn
/// (không lỗi build) nhưng khung ROI vẫn hiện bất kể UseROI - đó là do phần UI dùng chung, không phải do Tool này.
/// </summary>
[ToolMetadata("BlobAnalysis", DisplayName = "Blob Analysis", Category = "Detection",
    Description = "Connected-component blob analysis with Inclusion/Exclusion regions and Blob/Hole/All labeling.")]
public sealed class BlobAnalysisTool : VisionTool
{
    #region 1. Ports
    private readonly InputPort<IVisionImage> _input; // ImageMatrix: ảnh gốc (xám hoặc màu, tự convert + tự threshold theo BinaryThreshold)

    private readonly OutputPort<IVisionImage> _outImage;                            // ImageMatrix: ảnh gốc + overlay (số thứ tự, hộp bao, đường viền)
    private readonly OutputPort<int> _outBlobCount;                                 // BlobCount: số blob sau khi đã lọc + giới hạn
    private readonly OutputPort<IReadOnlyList<VisionBlob>> _outBlobs;               // Blobs: danh sách chi tiết từng blob
    private readonly OutputPort<P2[]> _outExclusionPolygonPoints;                   // ExclusionPolygonPoints: gộp toàn bộ điểm của mọi Exclusion/Inclusion Region
    private readonly OutputPort<IReadOnlyList<ExclusionPolygonInfo>> _outExclusionPolygons; // ExclusionPolygons: thông tin từng vùng riêng biệt
    private readonly OutputPort<IVisionImage> _outInteractiveVisualization;         // InteractiveVisualization: overlay trên nền ĐEN, dùng cho OverlayRenderer

    // Bonus tiện dụng (không có trong tài liệu nhưng giữ lại để tương thích các pipeline đã nối CompareTool/OverlayRenderer từ trước)
    private readonly OutputPort<BlobAnalysisResult> _outResult;
    private readonly OutputPort<double> _outTotalArea;
    #endregion

    #region 2. Parameters
    // --- Tab Region (ROI vẽ chuột trực tiếp trên Viewport - 1 vùng duy nhất) ---
    private readonly ToolParameter<bool> _useRoi;             // Bật/tắt dùng ROI (true = dùng ROI, false = quét full ảnh, KHÔNG vẽ/mask gì cả)
    private readonly ToolParameter<RotatedRectRegion> _roi;   // interaction: RotatedRectRegion -> HasRoi = true, cho phép kéo/vẽ/xoay bằng chuột; enabledWhen="UseROI" -> UI tự ẩn khung khi tắt (cần patch dùng chung, xem ghi chú đầu file)

    // --- Tab Region ---
    private readonly ToolParameter<bool> _enableExclusionRoi;     // Vai trò MẶC ĐỊNH cho ROI vẽ chuột: true = _roi được gộp làm Exclusion, false = Inclusion
    private readonly ToolParameter<string> _exclusionRegionsJson; // Danh sách vùng ROI bổ sung (Rectangle/Circle/Polygon) nhập tay/nối từ tool khác, lưu dạng JSON

    // --- Tab Detection ---
    private readonly ToolParameter<string> _objectType;        // "WhiteObjects" (mặc định) | "BlackObjects"
    private readonly ToolParameter<double> _binaryThreshold;   // Ngưỡng sáng-tối đơn giản (0-255) - KHÔNG có Otsu built-in
    private readonly ToolParameter<double> _minArea;
    private readonly ToolParameter<double> _maxArea;
    private readonly ToolParameter<string> _connectivityLabel; // "Blob" (mặc định) | "Hole" | "All"

    // --- Tab Advanced ---
    private readonly ToolParameter<bool> _enableDebugMode;
    private readonly ToolParameter<string> _sortBy;                  // "None" | "Area" | "CenterX" | "CenterY"
    private readonly ToolParameter<bool> _useSubPixelAccuracy;
    private readonly ToolParameter<string> _edgeRefinementMethod;    // "None" | "Gaussian" | "Bilateral" | "AnisotropicDiffusion"
    private readonly ToolParameter<int> _processingTimeout;          // ms
    private readonly ToolParameter<int> _interpolationFactor;        // 1-10
    private readonly ToolParameter<bool> _noiseReduction;

    // --- Tab Results ---
    private readonly ToolParameter<int> _maxBlobCount; // 1-500
    #endregion

    /// <summary>Id cố định dùng để nhận diện entry được sinh ra từ ROI vẽ chuột trong tập hợp Region runtime (không lưu ngược vào JSON).</summary>
    private const string RoiFromCanvasId = "__RoiFromCanvas";

    /// <summary>
    /// Ngân sách pixel tối đa cho phép khi xử lý Sub-Pixel (Width*Height*scaleFactor²). ~64 triệu pixel (VD ảnh 8MP x8,
    /// hoặc ảnh 2MP x32) là mức CPU/RAM còn kiểm soát được trong vài giây - vượt mức này sẽ tự động HẠ scaleFactor
    /// xuống thay vì để Resize/FindContours treo máy hoặc OutOfMemory (ProcessingTimeout không kịp cứu vì nó chỉ
    /// được kiểm tra ở vòng lặp SAU khi các bước Resize/Threshold/FindContours đã chạy xong).
    /// </summary>
    private const long MaxSubPixelPixelBudget = 64_000_000L;
    // Đây là giới hạn phần cứng an toàn mà CPU/RAM có thể xử lý mượt mà trong vài giây khi thực hiện các phép toán nặng như Cv2.Resize và Cv2.FindContours.

    public BlobAnalysisTool()
    {
        _input = AddInput<IVisionImage>("Image", "Image Matrix");

        _outImage = AddOutput<IVisionImage>("Image", "Image Matrix");
        _outBlobCount = AddOutput<int>("BlobCount", "Blob Count");
        _outBlobs = AddOutput<IReadOnlyList<VisionBlob>>("Blobs", "Blobs");
        _outExclusionPolygonPoints = AddOutput<P2[]>("ExclusionPolygonPoints", "Exclusion Polygon Points");
        _outExclusionPolygons = AddOutput<IReadOnlyList<ExclusionPolygonInfo>>("ExclusionPolygons", "Exclusion Polygons");
        _outInteractiveVisualization = AddOutput<IVisionImage>("InteractiveVisualization", "Interactive Visualization");

        _outResult = AddOutput<BlobAnalysisResult>("Result", "Blob Result (bonus)");
        _outTotalArea = AddOutput<double>("TotalArea", "Total Area (bonus)");

        _useRoi = AddParameter<bool>("UseROI", false, "Use ROI (Off = Full screen)", category: "Region", order: 1);
        _roi = AddParameter("Roi", new RotatedRectRegion(new P2(320, 240), 400, 300, 0),
            "ROI (vẽ chuột)", category: "Region", order: 2, interaction: ParameterInteraction.RotatedRectRegion, enabledWhen: "UseROI");
        _enableExclusionRoi = AddParameter<bool>("EnableExclusionROI", false, "Enable Exclusion ROI (default role)", category: "Region", order: 3);
        _exclusionRegionsJson = AddParameter<string>("ExclusionRegions", "[]", "Exclusion Regions (JSON)", category: "Region", order: 4);

        _objectType = AddChoiceParameter("ObjectType", "BlackObjects", new[] { "WhiteObjects", "BlackObjects" }, "Object Type", category: "Detection", order: 1);
        _binaryThreshold = AddParameter<double>("BinaryThreshold", 128.0, "Binary Threshold", min: 0.0, max: 255.0, category: "Detection", order: 2);
        _minArea = AddParameter<double>("MinArea", 50.0, "Min Area", min: 0.0, max: 10_000_000.0, category: "Detection", order: 3);
        _maxArea = AddParameter<double>("MaxArea", 1_000_000.0, "Max Area", min: 0.0, max: 10_000_000.0, category: "Detection", order: 4);
        _connectivityLabel = AddChoiceParameter("ConnectivityLabel", "Blob", new[] { "Blob", "Hole", "All" }, "Connectivity Label", category: "Detection", order: 5);

        _enableDebugMode = AddParameter<bool>("EnableDebugMode", false, "Enable Debug Mode", category: "Advanced", order: 1);
        _sortBy = AddChoiceParameter("SortBy", "Area", new[] { "None", "Area", "CenterX", "CenterY" }, "Sort By", category: "Advanced", order: 2);
        _useSubPixelAccuracy = AddParameter<bool>("UseSubPixelAccuracy", false, "Use Sub-Pixel Accuracy", category: "Advanced", order: 3);
        _edgeRefinementMethod = AddChoiceParameter("EdgeRefinementMethod", "Gaussian",
            new[] { "None", "Gaussian", "Bilateral", "AnisotropicDiffusion" }, "Edge Refinement Method", category: "Advanced", order: 4);
        _processingTimeout = AddParameter<int>("ProcessingTimeout", 5000, "Processing Timeout (ms)", min: 100, max: 60000, category: "Advanced", order: 5);
        _interpolationFactor = AddParameter<int>("InterpolationFactor", 2, "Interpolation Factor", min: 1, max: 10, category: "Advanced", order: 6);
        _noiseReduction = AddParameter<bool>("NoiseReduction", true, "Noise Reduction", category: "Advanced", order: 7);

        _maxBlobCount = AddParameter<int>("MaxBlobCount", 200, "Max Blob Count", min: 1, max: 500, category: "Results", order: 1);
    }

    protected override void OnExecute(IToolContext context)
    {
        var stopwatch = Stopwatch.StartNew();
        // Khởi động một đồng hồ bấm giờ để đo thời gian thực thi của toàn bộ quá trình xử lý ảnh (giúp ghi log debug hoặc kiểm soát timeout).

        Mat src = _input.Value!.AsMat();
        Mat gray = ToGray(src, out bool grayOwned);

        // ----- Bước 1: Tiền xử lý sub-pixel (tuỳ chọn) - CÓ TỰ ĐỘNG GIỚI HẠN NGÂN SÁCH PIXEL -----
        double scaleFactor = 1.0;
        Mat working = gray;
        bool workingOwned = false;
        if (_useSubPixelAccuracy.Value)
        {
            int requestedFactor = Math.Max(1, _interpolationFactor.Value);
            scaleFactor = ClampScaleFactorToBudget(gray.Width, gray.Height, requestedFactor, out bool wasClamped);
            // Mục đích: tính toán và giới hạn hệ số phóng to (scaleFactor) cho quá trình xử lý Sub-Pixel, đảm bảo không bị quá tải bộ nhớ hoặc treo máy.
            // scaleFactor: Biến kiểu double lưu giá trị hệ số scale cuối cùng sẽ dùng cho hàm Cv2.Resize ở bước tiền xử lý sub-pixel.

            if (wasClamped && _enableDebugMode.Value)
                context.Log($"BlobAnalysis [DEBUG]: InterpolationFactor={requestedFactor} trên ảnh {gray.Width}x{gray.Height} " +
                    $"vượt ngân sách pixel an toàn ({MaxSubPixelPixelBudget:N0}px) - tự động HẠ xuống scaleFactor={scaleFactor:F2} để tránh treo/OOM.");

            using Mat refined = ApplyEdgeRefinement(gray, _edgeRefinementMethod.Value);
            working = new Mat();
            Cv2.Resize(refined, working, new Size(0, 0), scaleFactor, scaleFactor, InterpolationFlags.Cubic);
            // - new Size(0, 0) (Kích thước đích tường minh):
            //   Khi truyền new Size(0, 0), bạn đang ra lệnh cho OpenCV: "Hãy tự động tính toán kích thước mới dựa vào hệ số scale,
            //   không cần ép buộc một chiều rộng/chiều cao cố định nào cả".
            // - scaleFactor, scaleFactor (Hệ số scale theo trục X và Y):, quy định tỉ lệ phóng to theo chiều ngang (Width) và dọc (Height).
            // - InterpolationFlags.Cubic (Thuật toán nội suy Bicubic):
            //   * Đây là thuật toán nội suy bậc cao (dựa trên lưới 4x4 pixel lân cận để tính toán giá trị cho pixel mới).
            //   * Tại sao lại dùng Bicubic thay vì Nearest Neighbor (gần nhất) hay Linear (tuyến tính)? Khi phóng to ảnh để làm sub-pixel, các đường biên
            //     của vật thể sẽ bị kéo giãn. Thuật toán Bicubic giúp các đường biên này mượt mà, ít bị vỡ nét (răng cưa) nhất có thể, tạo bàn đạp cực kỳ chính xác
            //     để hàm tìm contour (FindContours) ở bước sau xác định biên giới vật thể đến từng phần nhỏ của pixel.

            workingOwned = true;
        }

        // ----- Bước 2: Nhị phân hoá đơn giản theo BinaryThreshold (KHÔNG Otsu) -----
        Mat binary = new Mat();
        bool darkObjects = _objectType.Value == "BlackObjects";
        var thresholdFlags = darkObjects ? ThresholdTypes.BinaryInv : ThresholdTypes.Binary;
        Cv2.Threshold(working, binary, _binaryThreshold.Value, 255, thresholdFlags);

        // ----- Bước 3: Khử nhiễu nhẹ (tuỳ chọn) -----
        if (_noiseReduction.Value)
        {
            // Tạo phần tử cấu trúc (Kernel) hình elip với kích thước 3x3 pixel. 
            // Khung 3x3 này sẽ quét qua toàn bộ ảnh để thực hiện các phép toán hình thái học xung quanh mỗi pixel.
            // Dùng lệnh 'using' để tự động giải phóng bộ nhớ (Mat) ngay sau khi khối code kết thúc, tránh rò rỉ RAM.
            using Mat kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(3, 3));

            // 1. Phép mở (Morphological Open = Erode rồi Dilate):
            // - Bản chất: Co lại trước để bào mòn các điểm sáng đơn lẻ quá nhỏ (nhiễu muối tiêu), sau đó phình ra lại kích thước cũ.
            // - Tác dụng: Xóa sạch các đốm nhiễu li ti (nhiễu hạt pixel) nằm bên ngoài vật thể mà không làm thay đổi đáng kể kích thước vật chính.
            Cv2.MorphologyEx(binary, binary, MorphTypes.Open, kernel);

            // 2. Phép đóng (Morphological Close = Dilate rồi Erode):
            // - Bản chất: Phình ra trước để lấp đầy các khoảng trống nhỏ, sau đó co lại về kích thước ban đầu.
            // - Tác dụng: Vá lại các "lỗ thủng" nhỏ bị đứt gãy hoặc lấp các vết nứt nhỏ nằm bên trong lòng blob, giúp đồng nhất khối vật thể.
            Cv2.MorphologyEx(binary, binary, MorphTypes.Close, kernel);
        }

        // ----- Bước 4: Gộp ROI vẽ chuột (nếu UseROI=true) + Parse + áp dụng Inclusion/Exclusion Region -----
        List<ExclusionRegionDef> regions = ParseExclusionRegions(_exclusionRegionsJson.Value);

        // Chỉ thêm và xử lý ROI vẽ từ canvas khi UseROI=true - tắt thì bỏ qua HOÀN TOÀN (không mask, không vẽ,
        // không xuất ExclusionPolygons cho vùng này) = đúng nghĩa "Off = Full screen".
        if (_useRoi.Value)
        {
            RotatedRectRegion roi = _roi.Value;
            if (roi.Width > 0 && roi.Height > 0)
            {
                // Chèn vùng ROI do người dùng vẽ trực tiếp trên giao diện vào VỊ TRÍ ĐẦU TIÊN (index = 0) 
                // của danh sách regions để ưu tiên xử lý trước các vùng phụ trợ khác.
                regions.Insert(0, new ExclusionRegionDef
                {
                    Id = RoiFromCanvasId,                  // Gắn định danh đặc biệt để hệ thống nhận diện đây là ROI tương tác từ Canvas
                    Name = "ROI (canvas)",                 // Tên hiển thị trực quan của vùng trên giao diện
                    Type = "Rectangle",                    // Kiểu hình học của vùng là hình chữ nhật xoay (RotatedRect)
                    Enabled = true,                        // Bật kích hoạt trạng thái hoạt động cho vùng này
                    IsExclusion = _enableExclusionRoi.Value, // Xác định vai trò: true = vùng cấm (loại trừ), false = vùng giữ lại (bao gồm)
                    CenterX = roi.Center.X,                // Tọa độ tâm X của khung hình chữ nhật do người dùng vẽ
                    CenterY = roi.Center.Y,                // Tọa độ tâm Y của khung hình chữ nhật do người dùng vẽ
                    Width = roi.Width,                     // Chiều rộng (Width) của vùng ROI
                    Height = roi.Height,                   // Chiều cao (Height) của vùng ROI
                    Angle = roi.AngleDeg,                  // Góc xoay của khung ROI tính theo độ (Deg)
                });
            }
            else if (_enableDebugMode.Value)
            {
                context.Log("BlobAnalysis [DEBUG]: UseROI=true nhưng ROI (vẽ chuột) đang có Width/Height = 0 - " +
                    "bỏ qua, coi như chưa vẽ ROI (kết quả sẽ như quét full ảnh).");
            }
        }

        // Lọc từ danh sách tổng (regions) ra các vùng mang tính chất GIỮ LẠI (Inclusion):
        // Điều kiện: Vùng phải đang được bật (r.Enabled == true) VÀ KHÔNG PHẢI là vùng loại trừ (!r.IsExclusion), sau đó gom lại thành một danh sách (List).
        var inclusionRegions = regions.Where(r => r.Enabled && !r.IsExclusion).ToList();

        // Lọc từ danh sách tổng (regions) ra các vùng mang tính chất LOẠI TRỪ / VÙNG CẤM (Exclusion):
        // Điều kiện: Vùng phải đang được bật (r.Enabled == true) VÀ ĐÚNG LÀ vùng loại trừ (r.IsExclusion == true), sau đó gom lại thành một danh sách (List).
        var exclusionRegions = regions.Where(r => r.Enabled && r.IsExclusion).ToList();

        // --- XỬ LÝ CÁC VÙNG INCLUSION (VÙNG GIỮ LẠI) ---
        // Nếu danh sách các vùng cần giữ lại có phần tử (lớn hơn 0):
        if (inclusionRegions.Count > 0)
        {
            // Tạo một ma trận mặt nạ (mask) toàn màu đen có kích thước bằng ảnh nhị phân hiện tại (CV_8UC1: ảnh xám 8-bit 1 kênh)
            using Mat inclusionMask = Mat.Zeros(binary.Size(), MatType.CV_8UC1);

            // Duyệt qua từng định nghĩa vùng inclusion và vẽ chúng lên ma trận mask bằng màu TRẮNG (Scalar.White),
            // có nhân với hệ số phóng to (scaleFactor) để đồng bộ với kích thước ảnh đang xử lý sub-pixel.
            foreach (var r in inclusionRegions)
                FillRegionScaled(inclusionMask, r, scaleFactor, Scalar.White);

            // Thực hiện phép toán Bitwise AND giữa ảnh nhị phân gốc và tổng hợp các vùng Inclusion vừa vẽ (inclusionMask).
            // Tác dụng: Chỉ giữ lại những gì nằm BÊN TRONG các vùng inclusion, toàn bộ khu vực bên ngoài bị ép thành màu đen (bị loại bỏ).
            Cv2.BitwiseAnd(binary, inclusionMask, binary);
        }

        // --- XỬ LÝ CÁC VÙNG EXCLUSION (VÙNG LOẠI TRỪ / VÙNG CẤM) ---
        // Duyệt qua từng định nghĩa vùng exclusion trong danh sách và tô ĐEN (Scalar.Black) đè lên ảnh nhị phân,
        // có áp dụng hệ số scale tương ứng. 
        // Tác dụng: Xóa sạch (vô hiệu hóa) mọi pixel nằm bên trong các vùng cấm này, khiến thuật toán tìm blob bỏ qua chúng hoàn toàn.
        foreach (var r in exclusionRegions)
            FillRegionScaled(binary, r, scaleFactor, Scalar.Black);

        // ----- Bước 5: Tách Blob/Hole bằng Contour Hierarchy (CComp = 2 tầng: outer + hole) -----
        Cv2.FindContours(binary, out Point[][] contours, out HierarchyIndex[] hierarchy,
            RetrievalModes.CComp, ContourApproximationModes.ApproxSimple);
        // - dò tìm và trích xuất đường viền (contours) của các vật thể (vùng màu trắng) nằm trên nền đen.
        // - out Point[][] contours (Danh sách đường viền - Đầu ra số 1):
        //   Bản chất: Kiểu dữ liệu là một mảng chứa các mảng tọa độ điểm (Point[][]).
        //   Ý nghĩa:
        //    * Mỗi phần tử trong mảng lớn contours ứng với đường viền của một vật thể (blob) tìm được trên ảnh.
        //    * Bản thân mỗi đường viền lại là một tập hợp các điểm tọa độ Point[] nối liền nhau (giống như các nét vẽ khép kín ôm lấy mép của vật thể).
        // - out HierarchyIndex[] hierarchy (Cấu trúc phân cấp - Đầu ra số 2):
        //   Bản chất: Mảng chứa thông tin về mối quan hệ "cha - con" giữa các đường viền (ví dụ: cái nào nằm bên trong cái nào).
        //   Ý nghĩa: Giúp hệ thống phân biệt đâu là đường viền ngoài của vật thể, đâu là đường viền của các "lỗ thủng" nằm lọt thỏm bên trong vật thể đó.
        // - RetrievalModes.CComp (Chế độ trích xuất đường viền):
        //   CComp là viết tắt của Connected Components (Các thành phần liên kết).
        //   Cách hoạt động: Chế độ này tổ chức các đường viền tìm được thành 2 cấp độ (2 levels) rõ ràng:
        //    * Cấp 1 (Level 1): Đường viền bên ngoài của vật thể.
        //    * Cấp 2(Level 2): Đường viền của các khoảng trống / lỗ thủng nằm bên trong vật thể đó(nếu có).
        //      -> Ứng dụng thực tế: Rất phù hợp cho các bài toán kiểm tra linh kiện cơ khí có lỗ định vị, hoặc kiểm tra các bo mạch điện tử có các vùng trống bên trong.
        // - ContourApproximationModes.ApproxSimple (Chế độ nén/xấp xỉ đường viền):
        //   ApproxSimple là chế độ nén dữ liệu hình học cực kỳ thông minh và phổ biến.
        //   Cách hoạt động:
        //    * Hãy tưởng tượng bạn có một hình chữ nhật màu trắng. Biên của nó vốn có hàng trăm pixel nằm trên 4 cạnh.
        //    * Nếu dùng chế độ giữ nguyên, OpenCV sẽ lưu trữ toàn bộ hàng trăm điểm đó. Nhưng khi dùng ApproxSimple,
        //      OpenCV sẽ tự động loại bỏ các điểm thừa nằm trên đường thẳng, và chỉ giữ lại các đỉnh chốt (ví dụ: đúng 4 góc của hình chữ nhật).
        //    -> Lợi ích: Giúp giảm dung lượng bộ nhớ lưu trữ đường viền xuống mức tối đa và tăng tốc độ xử lý cho các bước tính toán hình học tiếp theo
        //       (như tính diện tích, tâm, góc xoay).

        string mode = _connectivityLabel.Value;
        double inv = 1.0 / scaleFactor; // Hệ số quy đổi ngược từ toạ độ "working" (có thể đã phóng to) về toạ độ ảnh gốc
        var rawBlobs = new List<VisionBlob>();
        bool timedOut = false;

        for (int i = 0; i < contours.Length; i++)
        {
            if (stopwatch.ElapsedMilliseconds > _processingTimeout.Value) { timedOut = true; break; } // ----- ProcessingTimeout -----

            bool isHole = hierarchy[i].Parent != -1; // Contour có "cha" -> nó nằm bên trong 1 contour khác -> đó là Hole
            bool include = mode switch { "Hole" => isHole, "All" => true, _ => !isHole }; // "Blob" (mặc định): chỉ contour ngoài cùng
            if (!include) continue;

            var contour = contours[i];
            double areaScaled = Math.Abs(Cv2.ContourArea(contour));
            if (areaScaled < 1e-6) continue;

            double perimeterScaled = Cv2.ArcLength(contour, true);
            Moments m = Cv2.Moments(contour);
            // Moments là một tập hợp các giá trị thống kê mô tả hình dáng, diện tích và sự phân bố pixel của vật thể.

            if (Math.Abs(m.M00) < 1e-9) continue;
            double cxScaled = m.M10 / m.M00, cyScaled = m.M01 / m.M00;
            // dùng để tính toán tọa độ Trọng tâm (Center of Mass / Centroid) của vật thể (blob) đang được xét.
            // Các ký hiệu như M00, M10, M01 (và rộng hơn là $M_{20}, M_{11}, M_{02}, \dots$) là tên các hệ số Moments chuẩn được quy định sẵn bởi thư viện OpenCV
            //  m.M00: Đại diện cho diện tích (Area) của vật thể (tổng số pixel nằm bên trong đường viền).
            //  m.M10: Tổng tọa độ $X$ của tất cả các pixel trong vật thể.
            //  m.M01: Tổng tọa độ $Y$ của tất cả các pixel trong vật thể.
            // -> (Nói một cách dễ hiểu: Lấy tổng tọa độ các điểm chia cho tổng diện tích/số lượng điểm, ta ra được điểm trung tâm cân bằng của vật thể).

            Rect bboxScaled = Cv2.BoundingRect(contour);

            double orientation = 0, majorScaled = 0, minorScaled = 0;

            // Kiểm tra xem đường viền có đủ từ 5 điểm trở lên hay không 
            // (Đây là điều kiện toán học bắt buộc của OpenCV để có thể vẽ/fit được một hình elip chính xác).
            if (contour.Length >= 5)
            {
                // Khớp một hình elip xoay (RotatedRect) ôm sát vào các điểm của đường viền (contour).
                RotatedRect ellipse = Cv2.FitEllipse(contour);

                // Lấy góc xoay của hình elip so với trục hoành (tính bằng độ).
                orientation = ellipse.Angle;

                // Lấy chiều dài trục lớn (Major Axis): So sánh chiều rộng và cao của elip để lấy giá trị lớn hơn.
                majorScaled = Math.Max(ellipse.Size.Width, ellipse.Size.Height);

                // Lấy chiều dài trục nhỏ (Minor Axis): So sánh để lấy giá trị nhỏ hơn.
                minorScaled = Math.Min(ellipse.Size.Width, ellipse.Size.Height);
            }
            // Tính tỷ lệ khung hình (Aspect Ratio): Lấy chiều dài trục lớn chia cho trục nhỏ của elip.
            // Kiểm tra trục nhỏ > 1e-6 để tránh lỗi chia cho 0.
            double aspectRatio = minorScaled > 1e-6 ? majorScaled / minorScaled : 0;

            // Tính độ tròn (Roundness / Circularity) của vật thể:
            // - Công thức chuẩn: (4 * pi * Diện tích) / (Chu vi^2). Đối với hình tròn hoàn hảo, giá trị này bằng 1.0.
            // - Kiểm tra chu vi > 1e-6 để tránh lỗi chia cho 0 (Division by zero) nếu đối tượng quá nhỏ hoặc bị lỗi.
            // - Math.Clamp dùng để ép kết quả luôn nằm chắc chắn trong khoảng từ 0.0 đến 1.0, phòng hờ sai số số thực (floating-point).
            double roundness = perimeterScaled > 1e-6 ? Math.Clamp(4.0 * Math.PI * areaScaled / (perimeterScaled * perimeterScaled), 0.0, 1.0) : 0.0;

            // Tìm đường bao lồi (Convex Hull) của vật thể:
            // Giống như việc bạn dùng một sợi dây thun căng vòng quanh các điểm ngoài cùng của vật thể để tạo ra một hình bao bọc nhẵn không có các phần lõm vào.
            Point[] hull = Cv2.ConvexHull(contour);

            // Tính diện tích của cái khung bao lồi (hull) vừa tìm được ở trên.
            double hullAreaScaled = Cv2.ContourArea(hull);

            // Tính độ đặc / tính nguyên khối (Solidity):
            // - Công thức: Diện tích thực tế của vật thể (areaScaled) chia cho Diện tích phần bao lồi (hullAreaScaled).
            // - Ý nghĩa: Giúp đánh giá xem vật thể có bị khuyết tật, lõm góc hay sứt mẻ hay không (vật thể đặc hoàn toàn thì tỷ lệ này gần bằng 1.0).
            // - Kiểm tra diện tích hull > 1e-6 để tránh lỗi chia cho 0, kết quả được ép an toàn trong khoảng [0.0, 1.0].
            double solidity = hullAreaScaled > 1e-6 ? Math.Clamp(areaScaled / hullAreaScaled, 0.0, 1.0) : 0.0;

            // ----- Rescale NGƯỢC toàn bộ số đo về đúng hệ toạ độ ảnh GỐC (quan trọng khi có dùng Sub-Pixel/InterpolationFactor) -----
            rawBlobs.Add(new VisionBlob
            {
                Id = i,
                Area = areaScaled * inv * inv,
                Perimeter = perimeterScaled * inv,
                CenterOfMass = new P2(cxScaled * inv, cyScaled * inv),
                BoundingBox = new RectRegion(bboxScaled.X * inv, bboxScaled.Y * inv, bboxScaled.Width * inv, bboxScaled.Height * inv),
                Orientation = orientation,
                MajorAxisLength = majorScaled * inv,
                MinorAxisLength = minorScaled * inv,
                AspectRatio = aspectRatio,
                Roundness = roundness,
                Solidity = solidity,
                Contour = contour.Select(p => new P2(p.X * inv, p.Y * inv)).ToList(),
            });
        }

        // ----- Bước 6: Lọc MinArea/MaxArea (đơn vị pixel² ảnh GỐC, không phụ thuộc sub-pixel) -----
        var filtered = rawBlobs.Where(b => b.Area >= _minArea.Value && b.Area <= _maxArea.Value).ToList();

        // ----- Bước 7: Sắp xếp theo SortBy -----
        filtered = _sortBy.Value switch
        {
            "Area" => filtered.OrderByDescending(b => b.Area).ToList(),
            "CenterX" => filtered.OrderBy(b => b.CenterOfMass.X).ToList(),
            "CenterY" => filtered.OrderBy(b => b.CenterOfMass.Y).ToList(),
            _ => filtered, // "None": giữ nguyên thứ tự phát hiện
        };

        // ----- Bước 8: Giới hạn MaxBlobCount -----
        if (filtered.Count > _maxBlobCount.Value)
            // Giới hạn số lượng đốm (blobs) tối đa được giữ lại dựa trên cấu hình _maxBlobCount, giúp loại bỏ bớt các đối tượng thừa.
            filtered = filtered.Take(_maxBlobCount.Value).ToList();

        int count = filtered.Count;
        double totalArea = filtered.Sum(b => b.Area);
        Judge judge = count > 0 ? Judge.OK : Judge.NG; // Mặc định đơn giản: có ít nhất 1 blob = OK. Muốn kiểm tra số lượng chính xác (VD phải đúng 5), nối BlobCount sang CompareTool phía sau.

        var result = new BlobAnalysisResult { Blobs = filtered, Count = count, TotalArea = totalArea, Judge = judge };

        // ----- Bước 9: Xây ExclusionPolygons/ExclusionPolygonPoints (LUÔN theo toạ độ ảnh GỐC, không scale) -----

        // Chuyển đổi danh sách vùng thô (regions) thành danh sách thông tin vùng loại trừ chi tiết (ExclusionPolygonInfo).
        // Tiến hành map các thuộc tính ID, Tên, cờ IsExclusion và gọi hàm RegionToPoints để quy đổi hình học vùng thành mảng các điểm tọa độ (Points).
        var exclusionPolygons = regions.Select(r => new ExclusionPolygonInfo
        {
            Id = r.Id ?? "",
            Name = r.Name ?? "",
            IsExclusion = r.IsExclusion,
            Points = RegionToPoints(r),
        }).ToList();

        // Gom nhóm và làm phẳng (flatten) toàn bộ các điểm tọa độ từ tất cả các vùng loại trừ lại thành một mảng duy nhất (P2[]).
        // Thao tác này thường dùng để tính toán giới hạn bao ngoài (bounding box chung) hoặc phục vụ cho các bước kiểm tra hình học tiếp theo.
        P2[] allPolygonPoints = exclusionPolygons.SelectMany(p => p.Points).ToArray();

        // ----- Bước 10: Vẽ 2 lớp overlay: ImageMatrix (nền ảnh gốc) + InteractiveVisualization (nền ĐEN) -----
        // 1. Tạo bức ảnh hiển thị 1: Vẽ đè trực tiếp lên nền ảnh xám gốc (đã được chuyển sang dạng màu BGR để có thể hiển thị màu sắc rực rỡ).
        Mat overlayOnBackground = new Mat();
        Cv2.CvtColor(gray, overlayOnBackground, ColorConversionCodes.GRAY2BGR);

        // 2. Tạo bức ảnh hiển thị 2: Tạo một khung canvas trống hoàn toàn màu đen (cùng kích thước với ảnh gốc).
        Mat overlayOnBlack = Mat.Zeros(gray.Size(), MatType.CV_8UC3);

        // 3. Vẽ toàn bộ các vùng ROI (Inclusion & Exclusion regions) lên cả hai bức ảnh trên.
        DrawRegions(overlayOnBackground, inclusionRegions, exclusionRegions);
        DrawRegions(overlayOnBlack, inclusionRegions, exclusionRegions);

        // 4. Vẽ toàn bộ danh sách các đốm vật thể đã được lọc thành công (filtered blobs - bounding box, tâm, thông số...) lên cả hai bức ảnh.
        DrawBlobs(overlayOnBackground, filtered);
        DrawBlobs(overlayOnBlack, filtered);

        // ----- Bước 11: Xuất kết quả -----
        // overlayOnBackground/overlayOnBlack "cho đi" thẳng vào Output -> KHÔNG Dispose(...) chúng sau đây
        _outImage.Value = new MatVisionImage(overlayOnBackground);
        _outInteractiveVisualization.Value = new MatVisionImage(overlayOnBlack);
        _outBlobCount.Value = count;
        _outBlobs.Value = filtered;
        _outExclusionPolygonPoints.Value = allPolygonPoints;
        _outExclusionPolygons.Value = exclusionPolygons;
        _outResult.Value = result;
        _outTotalArea.Value = totalArea;

        // ----- Bước 12: Dọn dẹp tài nguyên Mat trung gian -----
        binary.Dispose();
        if (workingOwned) working.Dispose();
        if (grayOwned) gray.Dispose();

        if (timedOut)
            context.Log($"BlobAnalysis: CẢNH BÁO vượt ProcessingTimeout ({_processingTimeout.Value}ms) - kết quả có thể THIẾU blob, dừng sớm ở contour đang xử lý dở.");

        if (_enableDebugMode.Value)
            context.Log($"BlobAnalysis [DEBUG]: UseROI={_useRoi.Value}, ObjectType={_objectType.Value}, BinaryThreshold={_binaryThreshold.Value}, " +
                $"ConnectivityLabel={mode}, SubPixel={_useSubPixelAccuracy.Value}(x{scaleFactor:F2}), " +
                $"InclusionRegions={inclusionRegions.Count}, ExclusionRegions={exclusionRegions.Count}, " +
                $"RawContours={contours.Length}, AfterFilter={rawBlobs.Count}, AfterSort/Limit={count}.");

        context.Log($"BlobAnalysis: found {rawBlobs.Count} region(s) ({mode}), {count} after filter. TotalArea={totalArea:F0}. Judge={judge}. {stopwatch.Elapsed.TotalMilliseconds:F1}ms.");
    }

    #region 3. Helpers - Exclusion Region

    /// <summary>DTO nội bộ để parse JSON tham số ExclusionRegions - KHÔNG public, chỉ dùng riêng trong Tool này.</summary>
    private sealed class ExclusionRegionDef
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string Type { get; set; } = "Rectangle"; // "Rectangle" | "Circle" | "Polygon"
        public bool Enabled { get; set; } = true;
        public bool IsExclusion { get; set; }
        public double CenterX { get; set; }
        public double CenterY { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public double Radius { get; set; }
        public double Angle { get; set; }
        public List<PointDef>? Points { get; set; }
    }

    private sealed class PointDef { public double X { get; set; } public double Y { get; set; } }

    /// <summary>
    /// Mục đích: Giải mã (deserialize) chuỗi dữ liệu JSON chứa danh sách các vùng loại trừ/bao gồm (Exclusion/Inclusion Regions) 
    /// thành các đối tượng C# để tool xử lý, đồng thời xử lý an toàn các trường hợp dữ liệu rỗng hoặc sai cú pháp.
    /// </summary>
    private static List<ExclusionRegionDef> ParseExclusionRegions(string json)
    {
        // Kiểm tra nhanh: nếu chuỗi JSON rỗng, null hoặc chỉ chứa khoảng trắng thì trả về ngay một danh sách trống (không cần parse)
        if (string.IsNullOrWhiteSpace(json)) return new List<ExclusionRegionDef>();

        try
        {
            // Cấu hình tùy chọn cho bộ giải mã JSON: cho phép không phân biệt chữ hoa/chữ thường (PropertyNameCaseInsensitive = true) 
            // giúp tránh lỗi khi tên thuộc tính trong JSON lệch kiểu viết hoa/thường so với C# DTO.
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

            // Thực hiện giải mã (deserialize) chuỗi danh sách các đối tượng ExclusionRegionDef.
            // Nếu kết quả trả về null (dù JSON hợp lệ nhưng rỗng), dùng toán tử ?? để thay bằng danh sách trống mới.
            return JsonSerializer.Deserialize<List<ExclusionRegionDef>>(json, options) ?? new List<ExclusionRegionDef>();
        }
        catch (JsonException ex)
        {
            // Bắt lỗi nếu cú pháp JSON không đúng định dạng (sai dấu ngoặc, thiếu dấu phẩy, v.v.), 
            // sau đó ném ra ngoại lệ chuyên dụng của tool kèm theo chi tiết lỗi để người vận hành dễ debug.
            throw new ToolExecutionException($"BlobAnalysis: tham số ExclusionRegions không phải JSON hợp lệ - {ex.Message}");
        }
    }

    /// <summary>Quy đổi 1 vùng ROI (Rectangle/Circle/Polygon) thành danh sách điểm đa giác, LUÔN theo hệ toạ độ ảnh GỐC (chưa scale).</summary>
    private static List<P2> RegionToPoints(ExclusionRegionDef r)
    {
        switch (r.Type)
        {
            case "Circle":
                {
                    var pts = new List<P2>();
                    const int n = 36; // Xấp xỉ hình tròn bằng đa giác 36 cạnh - đủ mượt để hiển thị/tính toán
                    for (int i = 0; i < n; i++)
                    {
                        double a = i * 2 * Math.PI / n;
                        pts.Add(new P2(r.CenterX + r.Radius * Math.Cos(a), r.CenterY + r.Radius * Math.Sin(a)));
                    }
                    return pts;
                }
            case "Polygon":
                return (r.Points ?? new List<PointDef>()).Select(p => new P2(p.X, p.Y)).ToList();
            default: // "Rectangle"
                {
                    double hw = r.Width / 2.0, hh = r.Height / 2.0;
                    double rad = r.Angle * Math.PI / 180.0;
                    double cos = Math.Cos(rad), sin = Math.Sin(rad);
                    P2 Rot(double dx, double dy) => new P2(r.CenterX + dx * cos - dy * sin, r.CenterY + dx * sin + dy * cos);
                    return new List<P2> { Rot(-hw, -hh), Rot(hw, -hh), Rot(hw, hh), Rot(-hw, hh) };
                }
        }
    }

    /// <summary>Vẽ 1 vùng ROI đã tô đặc (filled) lên mask, có nhân toạ độ với scaleFactor để khớp không gian xử lý sub-pixel.</summary>
    private static void FillRegionScaled(Mat mask, ExclusionRegionDef r, double scaleFactor, Scalar color)
    {
        var pts = RegionToPoints(r)
            .Select(p => new Point((int)Math.Round(p.X * scaleFactor), (int)Math.Round(p.Y * scaleFactor)))
            .ToArray();
        if (pts.Length < 3) return; // Không đủ điểm để tạo đa giác hợp lệ - bỏ qua an toàn
        Cv2.FillPoly(mask, new[] { pts }, color);
    }

    /// <summary>Vẽ viền các vùng Inclusion (xanh lá, nét liền) và Exclusion (đỏ, nét liền) lên overlay để người vận hành thấy rõ vùng đang áp dụng.</summary>
    private static void DrawRegions(Mat overlay, List<ExclusionRegionDef> inclusionRegions, List<ExclusionRegionDef> exclusionRegions)
    {
        foreach (var r in inclusionRegions)
            DrawRegionOutline(overlay, r, new Scalar(0, 200, 0));
        foreach (var r in exclusionRegions)
            DrawRegionOutline(overlay, r, new Scalar(0, 0, 200));
    }

    private static void DrawRegionOutline(Mat overlay, ExclusionRegionDef r, Scalar color)
    {
        var pts = RegionToPoints(r).Select(p => new Point((int)p.X, (int)p.Y)).ToArray();
        if (pts.Length < 3) return;
        Cv2.Polylines(overlay, new[] { pts }, true, color, 1, LineTypes.AntiAlias);
    }

    #endregion

    #region 4. Helpers - Sub-pixel & Overlay Blob

    /// <summary>
    /// Tính scaleFactor thực tế dùng để Resize, đã kẹp theo ngân sách pixel an toàn (MaxSubPixelPixelBudget).
    /// Trả về scaleFactor gốc nếu vẫn nằm trong ngân sách; nếu vượt, hạ dần về mức lớn nhất còn an toàn (tối thiểu 1.0).
    /// </summary>
    private static double ClampScaleFactorToBudget(int width, int height, int requestedFactor, out bool wasClamped)
    {
        // Bước 1: Tính tổng số pixel của ảnh gốc
        long basePixels = (long)width * height;
        if (basePixels <= 0) { wasClamped = false; return requestedFactor; }
        // wasClamped = false: không tiến hành việc hạ thấp hệ số scale (không "clamp") mà chuẩn bị thoát ra luôn.
        // return requestedFactor: Lập tức trả về nguyên vẹn hệ số phóng to mà người dùng đã cài đặt (requestedFactor)
        // và kết thúc hàm ngay lập tức để không chạy tiếp các bước toán học bên dưới.

        // Bước 2: Tính tổng số pixel dự kiến sau khi phóng to (scale)
        // Vì scale ảnh theo cả chiều rộng lẫn chiều cao, số pixel tăng theo bình phương (factor * factor)
        long requestedPixels = basePixels * (long)requestedFactor * requestedFactor;

        // Nếu vẫn nằm trong ngân sách cho phép (<= 64 triệu pixel), giữ nguyên yêu cầu
        if (requestedPixels <= MaxSubPixelPixelBudget) { wasClamped = false; return requestedFactor; }

        // Bước 3: Nếu vượt quá ngân sách, tính toán lại hệ số scale lớn nhất VẪN CÒN AN TOÀN
        // Dùng căn bậc hai (Sqrt) vì diện tích ảnh tăng theo bình phương của hệ số scale
        double maxSafeFactor = Math.Sqrt(MaxSubPixelPixelBudget / (double)basePixels);
        wasClamped = true;
        return Math.Max(1.0, maxSafeFactor); // Đảm bảo scale tối thiểu là 1.0 (không thu nhỏ ảnh)
    }

    /// <summary>Làm mịn biên trước khi phóng to sub-pixel, theo đúng EdgeRefinementMethod đã chọn.</summary>
    private static Mat ApplyEdgeRefinement(Mat gray, string method)
    {
        // Tạo một bản sao (clone) của ảnh xám gốc để xử lý độc lập, tránh làm thay đổi ảnh đầu vào ban đầu
        Mat result = gray.Clone();

        switch (method)
        {
            case "Gaussian":
                // Làm mờ Gaussian tiêu chuẩn (kernel 5x5, sigma = 1.0) để khử nhiễu nhanh
                Cv2.GaussianBlur(result, result, new Size(5, 5), 1.0);
                // Tại mỗi pixel, thuật toán sẽ xét vùng lân cận $5 \times 5$ xung quanh nó để tính toán giá trị trung bình
                // có trọng số theo hình chuông (Gaussian). Kích thước này phải luôn là số lẻ.
                // 1.0: hệ số sigma:
                //  * Khi Sigma nhỏ (ví dụ $\sigma = 0.5$): Hình chóp nhọn: 
                //   - Toàn bộ "quyền lực" (trọng số lớn nhất, ví dụ 90%) dồn cục vào đúng pixel ở chính giữa tâm.
                //   - Các pixel xung quanh cách đó vài bước chân chỉ chiếm một chút xíu trọng số (gần như bằng 0).
                //  * Khi Sigma lớn (ví dụ $\sigma = 3.0$): Hình ngọn đồi thoải:
                //   - Trọng số ở tâm bị giảm xuống, và được "san sẻ" bè rộng ra cho các pixel xung quanh trong ô 5x5 đó.
                //   - Các pixel ở rìa ngoài cũng bắt đầu có trọng số cao đáng kể chứ không bị phớt lờ nữa.

                break;

            case "Bilateral":
                {
                    // Bộ lọc song phương (Bilateral Filter): làm mịn vùng phẳng nhưng vẫn GIỮ NGUYÊN các đường biên sắc nét
                    Mat bf = new Mat();
                    Cv2.BilateralFilter(result, bf, 9, 50, 50);
                    // - 9 (Đường kính vùng lọc - Diameter): Kích thước vùng lân cận được xét xung quanh mỗi pixel (đường kính 9 pixel).
                    // - 50 thứ nhất (Sigma màu - Sigma Color): Quyết định mức độ chênh lệch màu sắc/độ sáng được phép pha trộn.
                    //   * Nếu 2 pixel cạnh nhau có màu sắc tương đồng(trong ngưỡng 50), chúng sẽ bị pha trộn(làm mờ) với nhau để khử nhiễu.
                    //   * Nếu độ chênh lệch màu vượt quá 50(ví dụ đây là đường biên ranh giới giữa vật thể sáng và nền tối), chúng sẽ KHÔNG bị pha trộn.
                    //     Nhờ vậy, đường viền của vật thể được giữ nguyên vẹn, không bị nhòe hay bè rộng ra.
                    // - 50 thứ hai (Sigma không gian - Sigma Space): Tương tự như sigma của Gaussian, quyết định khoảng cách không gian
                    //   (pixel nào ở gần tâm hơn thì có trọng số lớn hơn).

                    result.Dispose(); // Giải phóng ảnh cũ trước khi gán ảnh mới để tránh rò rỉ bộ nhớ (memory leak)
                    result = bf;
                    break;
                }

            case "AnisotropicDiffusion":
                // OpenCV không có sẵn thuật toán Anisotropic Diffusion (Perona-Malik) thực thụ - 
                // ta xấp xỉ bằng cách chạy 3 lượt Bilateral Filter với kernel nhỏ liên tiếp.
                // Kỹ thuật này giúp giữ cạnh tốt hơn Gaussian thuần và tạo hiệu ứng khuếch tán dị hướng chân thực hơn.
                for (int i = 0; i < 3; i++)
                {
                    Mat bf2 = new Mat();
                    Cv2.BilateralFilter(result, bf2, 5, 30, 30);
                    result.Dispose(); // Giải phóng ma trận trung gian sau mỗi vòng lặp
                    result = bf2;
                }
                break;

            case "None":
            default:
                break; // Giữ nguyên ảnh gốc, không thực hiện làm mịn biên
        }

        // Trả về bức ảnh đã được tinh chỉnh biên hoàn thiện
        return result;
    }

    /// <summary>Vẽ contour + bounding box + tâm khối + nhãn số thứ tự cho từng Blob lên overlay (dùng chung cho cả 2 lớp Image/InteractiveVisualization).</summary>
    private static void DrawBlobs(Mat overlay, List<VisionBlob> blobs)
    {
        int idx = 1;
        foreach (var b in blobs)
        {
            var contourPts = b.Contour.Select(p => new Point((int)p.X, (int)p.Y)).ToArray();

            if (contourPts.Length > 0)
                Cv2.Polylines(overlay, new[] { contourPts }, true, Scalar.Yellow, 2, LineTypes.AntiAlias);

            var bb = b.BoundingBox;
            Cv2.Rectangle(overlay, new Rect((int)bb.X, (int)bb.Y, (int)bb.Width, (int)bb.Height), Scalar.Cyan, 1, LineTypes.AntiAlias);

            Cv2.Circle(overlay, new Point((int)b.CenterOfMass.X, (int)b.CenterOfMass.Y), 4, Scalar.Red, -1, LineTypes.AntiAlias);

            string text = $"#{idx} A={b.Area:F0}";
            Cv2.PutText(overlay, text, new Point((int)b.CenterOfMass.X + 6, (int)b.CenterOfMass.Y - 6),
                HersheyFonts.HersheySimplex, 0.4, Scalar.LimeGreen, 1, LineTypes.AntiAlias);
            idx++;
        }
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