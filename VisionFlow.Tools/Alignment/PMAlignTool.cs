// ==================== Vai trò chính:                Định vị vị trí + góc xoay của vật thể trong ảnh dựa trên 1 Template đã dạy (Pattern Match Alignment - ORB + RANSAC)
// ==================== Thành phần / Class tiêu biểu: PMAlignTool
// ==================== Phụ thuộc vào:                OpenCvSharp (ORB, BFMatcher, EstimateAffinePartial2D) + Core.Models (AlignResult, TemplateImageRef) + Core.Ports + Core.Tools
// ==================== Pattern / Kỹ thuật nổi bật:   ORB Feature Matching + RANSAC Similarity Transform + Validation theo AngleTolerance/Scale (giả lập "dung sai biến dạng" của PatMax)

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Models;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;
using P2 = VisionFlow.Core.Models.Point2d;

namespace VisionFlow.Tools.Alignment;

/// <summary>
/// Công cụ định vị vị trí phôi (Pattern Match Alignment — tương đương PatMax của Cognex ở mức cơ bản):
/// 1. Nạp ảnh mẫu (Template) đã dạy trước — cắt từ 1 vùng trên ảnh tham chiếu (Golden Image, đúng thao tác
///    "kéo khung dạy mẫu" theo tài liệu Tab Template) hoặc nạp từ file.
/// 2. (Tuỳ chọn) Giới hạn vùng tìm kiếm (Tab Search) thay vì luôn quét toàn ảnh — tăng tốc + giảm nhận nhầm.
/// 3. Trích đặc trưng ORB (bất biến xoay, chịu được thay đổi tỉ lệ nhẹ) cho cả Template và vùng tìm kiếm.
/// 4. Ghép cặp đặc trưng bằng BFMatcher + Lowe's Ratio Test để loại các cặp ghép mơ hồ.
/// 5. Ước lượng phép biến đổi Similarity (xoay + tịnh tiến + scale đều) bằng RANSAC — chịu được nhiễu/ghép sai cục bộ.
/// 6. Kiểm tra kết quả có nằm trong dung sai AngleTolerance/MinScale-MaxScale hay không (đúng vai trò "Tab Search"
///    trong tài liệu: giới hạn góc xoay/tỉ lệ chấp nhận được) — vượt dung sai thì coi như KHÔNG tìm thấy dù RANSAC
///    numerically thành công.
/// 7. (Tuỳ chọn) SubPixel: giữ nguyên toạ độ thập phân chính xác; tắt thì làm tròn về số nguyên pixel.
/// 8. Trả về đầy đủ BestMatchPoint/MatchScore/MatchAngle/Isfound/4 góc bounding box + AlignResult (Offset delta,
///    dùng cho FixtureTool phía sau — GIỮ NGUYÊN cấu trúc để không phá vỡ pipeline Fixture đã có).
/// </summary>
[ToolMetadata("PMAlignt", DisplayName = "PMAlign", Category = "Alignment",
    Description = "Feature-based geometric pattern matching alignment (ORB + RANSAC), rotation/scale invariant, with search region and angle/scale tolerance.")]
public sealed class PMAlignTool : VisionTool
{
    #region 1. Ports
    private readonly InputPort<IVisionImage> _input; // ImageMatrix: ảnh runtime cần tìm vị trí phôi trong đó
    private readonly InputPort<IVisionImage> _templateSource; // Ảnh tham chiếu (Golden Image) để cắt Template từ SourceRegion - tương đương "TemplateImage" trong tài liệu

    private readonly OutputPort<IVisionImage> _outResultImage; // ResultImage: ảnh Overlay có vẽ khung Template đã tìm thấy
    private readonly OutputPort<P2> _outBestMatchPoint;         // BestMatchPoint: toạ độ tâm vật thể khớp nhất
    private readonly OutputPort<double> _outMatchScore;         // MatchScore: độ giống nhau (0..1)
    private readonly OutputPort<double> _outMatchAngle;         // MatchAngle: góc xoay TUYỆT ĐỐI (θ) tại BestMatchPoint
    private readonly OutputPort<bool> _outIsFound;              // Isfound: đã tìm thấy đối tượng hợp lệ hay chưa
    private readonly OutputPort<P2> _outLeftTop;                // LeftTopCorner
    private readonly OutputPort<P2> _outRightTop;               // RightTopCorner
    private readonly OutputPort<P2> _outRightBottom;            // RightBottomCorner
    private readonly OutputPort<P2> _outLeftBottom;             // LeftBottomCorner

    // Bonus: giữ nguyên cổng Result (AlignResult chứa Offset delta) để KHÔNG phá vỡ FixtureTool đang tiêu thụ nó
    private readonly OutputPort<AlignResult> _outResult;
    #endregion

    #region 2. Parameters
    // --- Tab Template ---
    private readonly ToolParameter<TemplateImageRef> _template; // Cắt vùng trên ảnh tham chiếu HOẶC nạp từ file
    private readonly ToolParameter<double> _matchThreshold;     // MatchThreshold: ngưỡng khớp tối thiểu (mặc định 0.8 đúng tài liệu)

    // --- Tab Search ---
    private readonly ToolParameter<bool> _useSearchRegion;        // Bật giới hạn vùng tìm kiếm thay vì quét toàn ảnh
    private readonly ToolParameter<double> _searchRegionCenterX;
    private readonly ToolParameter<double> _searchRegionCenterY;
    private readonly ToolParameter<int> _searchRegionWidth;
    private readonly ToolParameter<int> _searchRegionHeight;
    private readonly ToolParameter<double> _angleTolerance;       // AngleTolerance: dung sai góc xoay chấp nhận được (độ), mặc định 10
    private readonly ToolParameter<double> _minScale;             // Giới hạn tỉ lệ (Scale) tối thiểu chấp nhận được
    private readonly ToolParameter<double> _maxScale;             // Giới hạn tỉ lệ (Scale) tối đa chấp nhận được

    // --- Tab Advanced ---
    private readonly ToolParameter<bool> _subPixel;                 // SubPixel: bật = giữ toạ độ thập phân; tắt = làm tròn số nguyên
    private readonly ToolParameter<int> _maxFeatures;               // Số lượng đặc trưng ORB tối đa - "Tối ưu tốc độ"
    private readonly ToolParameter<double> _ratioTestThreshold;     // Ngưỡng Lowe's Ratio Test - "Kiểm soát độ nhạy"
    private readonly ToolParameter<double> _ransacReprojThreshold;  // Sai số tái chiếu RANSAC (px) - "Xử lý biến dạng"
    private readonly ToolParameter<int> _minInlierCount;            // Số inlier tối thiểu - "Xử lý che khuất 1 phần"

    // --- Tab Display ---
    private readonly ToolParameter<bool> _drawBoundingBox;
    private readonly ToolParameter<bool> _drawAxis;
    private readonly ToolParameter<bool> _drawMatches;
    #endregion

    public PMAlignTool()
    {
        _input = AddInput<IVisionImage>("Image", "Image Matrix");
        _templateSource = AddInput<IVisionImage>("TemplateSource", "Template Image", optional: true);

        _outResultImage = AddOutput<IVisionImage>("ResultImage", "Result Image");
        _outBestMatchPoint = AddOutput<P2>("BestMatchPoint", "Best Match Point");
        _outMatchScore = AddOutput<double>("MatchScore", "Match Score");
        _outMatchAngle = AddOutput<double>("MatchAngle", "Match Angle");
        _outIsFound = AddOutput<bool>("Isfound", "Is Found");
        _outLeftTop = AddOutput<P2>("LeftTopCorner", "Left Top Corner");
        _outRightTop = AddOutput<P2>("RightTopCorner", "Right Top Corner");
        _outRightBottom = AddOutput<P2>("RightBottomCorner", "Right Bottom Corner");
        _outLeftBottom = AddOutput<P2>("LeftBottomCorner", "Left Bottom Corner");
        _outResult = AddOutput<AlignResult>("Result", "Align Result (for Fixture)");

        _template = AddParameter(
            "Template",
            new TemplateImageRef(SourceRegion: new RotatedRectRegion(new P2(150, 120), 200, 150, 0)),
            displayName: "Template", category: "Template", order: 1,
            interaction: ParameterInteraction.Template);
        _matchThreshold = AddParameter<double>("MatchThreshold", 0.8, "Match Threshold", min: 0.0, max: 1.0, category: "Template", order: 2);

        _useSearchRegion = AddParameter<bool>("UseSearchRegion", false, "Use Search Region", category: "Search", order: 1);
        _searchRegionCenterX = AddParameter<double>("SearchRegionCenterX", 0.0, "Search Region Center X", min: 0.0, max: 100000.0, category: "Search", order: 2);
        _searchRegionCenterY = AddParameter<double>("SearchRegionCenterY", 0.0, "Search Region Center Y", min: 0.0, max: 100000.0, category: "Search", order: 3);
        _searchRegionWidth = AddParameter<int>("SearchRegionWidth", 400, "Search Region Width", min: 1, max: 20000, category: "Search", order: 4);
        _searchRegionHeight = AddParameter<int>("SearchRegionHeight", 400, "Search Region Height", min: 1, max: 20000, category: "Search", order: 5);
        _angleTolerance = AddParameter<double>("AngleTolerance", 10.0, "Angle Tolerance (deg)", min: 0.0, max: 180.0, category: "Search", order: 6);
        _minScale = AddParameter<double>("MinScale", 0.8, "Min Scale", min: 0.01, max: 10.0, category: "Search", order: 7);
        _maxScale = AddParameter<double>("MaxScale", 1.2, "Max Scale", min: 0.01, max: 10.0, category: "Search", order: 8);

        _subPixel = AddParameter<bool>("SubPixel", false, "Sub-Pixel Accuracy", category: "Advanced", order: 1);
        _maxFeatures = AddParameter<int>("MaxFeatures", 500, "Max Features", min: 50, max: 5000, category: "Advanced", order: 2);
        _ratioTestThreshold = AddParameter<double>("RatioTestThreshold", 0.75, "Ratio Test Threshold", min: 0.5, max: 0.95, category: "Advanced", order: 3);
        _ransacReprojThreshold = AddParameter<double>("RansacReprojThreshold", 3.0, "RANSAC Reproj. Threshold (px)", min: 0.5, max: 20.0, category: "Advanced", order: 4);
        _minInlierCount = AddParameter<int>("MinInlierCount", 8, "Min Inlier Count", min: 3, max: 1000, category: "Advanced", order: 5);

        _drawBoundingBox = AddParameter<bool>("DrawBoundingBox", true, "Draw Bounding Box", category: "Display", order: 1);
        _drawAxis = AddParameter<bool>("DrawAxis", true, "Draw Axis", category: "Display", order: 2);
        _drawMatches = AddParameter<bool>("DrawMatches", false, "Draw Feature Matches", category: "Display", order: 3);
    }

    protected override void OnExecute(IToolContext context)
    {
        if (_input.Value == null)
            throw new ArgumentNullException(nameof(_input), "Ảnh runtime đầu vào không được rỗng!");

        var src = _input.Value!.AsMat();

        // ----- Bước 1: Chuyển ảnh runtime sang xám -----
        Mat srcGray;
        bool srcGrayOwned = false;
        if (src.Channels() == 1) { srcGray = src; }
        else { srcGray = new Mat(); Cv2.CvtColor(src, srcGray, ColorConversionCodes.BGR2GRAY); srcGrayOwned = true; }

        // ----- Bước 2: Giới hạn vùng tìm kiếm (Tab Search) nếu được bật, ngược lại quét toàn ảnh -----
        double searchOffsetX = 0, searchOffsetY = 0;
        Mat searchArea = srcGray;
        bool searchAreaOwned = false;
        if (_useSearchRegion.Value)
        {
            double cx = _searchRegionCenterX.Value, cy = _searchRegionCenterY.Value;
            int w = Math.Max(1, _searchRegionWidth.Value), h = Math.Max(1, _searchRegionHeight.Value);
            searchOffsetX = cx - w / 2.0;
            searchOffsetY = cy - h / 2.0;

            searchArea = new Mat();
            Cv2.GetRectSubPix(srcGray, new Size(w, h), new Point2f((float)cx, (float)cy), searchArea);
            searchAreaOwned = true;
        }

        // ----- Bước 3: Nạp/Cắt ảnh Template theo cấu hình TemplateImageRef -----
        var templateRef = _template.Value;
        Mat templateGray;
        P2 nominalCenter;
        double nominalAngle;

        if (!string.IsNullOrWhiteSpace(templateRef.FilePath) && File.Exists(templateRef.FilePath))
        {
            using Mat templateColor = Cv2.ImRead(templateRef.FilePath, ImreadModes.Color);
            templateGray = new Mat();
            Cv2.CvtColor(templateColor, templateGray, ColorConversionCodes.BGR2GRAY);
            nominalCenter = new P2(srcGray.Width / 2.0, srcGray.Height / 2.0);
            nominalAngle = 0.0;
        }
        else if (templateRef.SourceRegion.HasValue)
        {
            if (_templateSource.Value == null)
                throw new InvalidOperationException("Template được cấu hình theo SourceRegion nhưng cổng 'TemplateSource' chưa được nối ảnh tham chiếu!");

            RotatedRectRegion reg = templateRef.SourceRegion.Value;
            Mat refMat = _templateSource.Value!.AsMat();

            Point2f center = new Point2f((float)reg.Center.X, (float)reg.Center.Y);
            Size size = new Size(Math.Max(1, (int)reg.Width), Math.Max(1, (int)reg.Height));
            Mat rotSource = refMat;
            bool rotated = false;
            if (Math.Abs(reg.AngleDeg) > 1e-5)
            {
                using Mat rotMat = Cv2.GetRotationMatrix2D(center, reg.AngleDeg, 1.0);
                rotSource = new Mat();
                Cv2.WarpAffine(refMat, rotSource, rotMat, refMat.Size(), InterpolationFlags.Linear, BorderTypes.Replicate);
                rotated = true;
            }
            using Mat templateColor = new Mat();
            Cv2.GetRectSubPix(rotSource, size, center, templateColor);
            if (rotated) rotSource.Dispose();

            templateGray = new Mat();
            if (templateColor.Channels() == 1) templateColor.CopyTo(templateGray);
            else Cv2.CvtColor(templateColor, templateGray, ColorConversionCodes.BGR2GRAY);

            nominalCenter = reg.Center;
            nominalAngle = reg.AngleDeg;
        }
        else
        {
            throw new InvalidOperationException("Tham số Template chưa được cấu hình (cần SourceRegion hoặc FilePath hợp lệ)!");
        }

        // ----- Bước 4: Trích đặc trưng ORB cho Template và vùng tìm kiếm -----
        using var orb = ORB.Create(_maxFeatures.Value);
        using Mat descTemplate = new Mat();
        orb.DetectAndCompute(templateGray, null, out KeyPoint[] kpTemplate, descTemplate);
        using Mat descScene = new Mat();
        orb.DetectAndCompute(searchArea, null, out KeyPoint[] kpSearchLocal, descScene);

        // Quy đổi ngay toạ độ keypoint về hệ ẢNH GỐC (cộng offset vùng tìm kiếm) - để mọi bước tính toán phía sau
        // (affine, corner, overlay...) đều làm việc thống nhất trên hệ toạ độ ảnh gốc, không phải lo offset rải rác.
        KeyPoint[] kpScene = kpSearchLocal.Select(kp => new KeyPoint(
            new Point2f((float)(kp.Pt.X + searchOffsetX), (float)(kp.Pt.Y + searchOffsetY)),
            kp.Size, kp.Angle, kp.Response, kp.Octave, kp.ClassId)).ToArray();

        List<DMatch> goodMatches = new();
        Mat? affine = null;
        Mat? inliersMask = null;

        // ----- Bước 5: Ghép cặp đặc trưng bằng BFMatcher + Lowe's Ratio Test -----
        if (descTemplate.Rows > 0 && descScene.Rows > 0)
        {
            using var matcher = new BFMatcher(NormTypes.Hamming);
            DMatch[][] knnMatches = matcher.KnnMatch(descTemplate, descScene, 2);
            double ratio = _ratioTestThreshold.Value;
            goodMatches = knnMatches
                .Where(m => m.Length == 2 && m[0].Distance < ratio * m[1].Distance)
                .Select(m => m[0])
                .ToList();
        }

        // ----- Bước 6: Ước lượng phép biến đổi Similarity bằng RANSAC -----
        if (goodMatches.Count >= _minInlierCount.Value)
        {
            Point2f[] srcPts = goodMatches.Select(m => kpTemplate[m.QueryIdx].Pt).ToArray();
            Point2f[] dstPts = goodMatches.Select(m => kpScene[m.TrainIdx].Pt).ToArray();

            inliersMask = new Mat();
            affine = Cv2.EstimateAffinePartial2D(InputArray.Create(srcPts), InputArray.Create(dstPts),
                inliersMask, RobustEstimationAlgorithms.RANSAC, _ransacReprojThreshold.Value);
        }

        // ----- Bước 7: Tính toán kết quả + kiểm tra dung sai Angle/Scale (Tab Search) -----
        Mat overlay = new Mat();
        Cv2.CvtColor(srcGray, overlay, ColorConversionCodes.GRAY2BGR);

        int inlierCount = 0;
        double matchScore = 0.0;
        double thetaDeg = 0.0;
        bool isFound = false;
        P2 matchedCenter = default, lt = default, rt = default, rb = default, lb = default;
        AlignResult result;

        if (affine != null && !affine.Empty())
        {
            if (inliersMask != null)
            {
                int rows = inliersMask.Rows;
                for (int i = 0; i < rows; i++)
                    if (inliersMask.At<byte>(i, 0) != 0) inlierCount++;
            }
            matchScore = goodMatches.Count > 0 ? (double)inlierCount / goodMatches.Count : 0.0;

            double a = affine.At<double>(0, 0), b = affine.At<double>(0, 1);
            double c = affine.At<double>(1, 0), d = affine.At<double>(1, 1);
            double tx = affine.At<double>(0, 2), ty = affine.At<double>(1, 2);
            thetaDeg = Math.Atan2(c, a) * 180.0 / Math.PI;
            double scale = Math.Sqrt(a * a + c * c);

            P2 TemplateToScene(P2 p) => new P2(a * p.X + b * p.Y + tx, c * p.X + d * p.Y + ty);

            P2 templateCenter = new P2(templateGray.Width / 2.0, templateGray.Height / 2.0);
            matchedCenter = TemplateToScene(templateCenter);

            double w = templateGray.Width, h = templateGray.Height;
            lt = TemplateToScene(new P2(0, 0));
            rt = TemplateToScene(new P2(w, 0));
            rb = TemplateToScene(new P2(w, h));
            lb = TemplateToScene(new P2(0, h));

            // ----- Kiểm tra dung sai góc (AngleTolerance) - xử lý wraparound đúng cách (VD 179 vs -179 độ chỉ lệch 2 độ, không phải 358) -----
            double angleDiff = Math.Abs(NormalizeAngleDiff(thetaDeg - nominalAngle));
            bool angleOk = angleDiff <= _angleTolerance.Value;

            // ----- Kiểm tra dung sai tỉ lệ (MinScale/MaxScale) -----
            bool scaleOk = scale >= _minScale.Value && scale <= _maxScale.Value;

            isFound = matchScore >= _matchThreshold.Value && inlierCount >= _minInlierCount.Value && angleOk && scaleOk;

            // ----- SubPixel: tắt thì làm tròn về số nguyên pixel, đúng mô tả hành vi trong tài liệu -----
            if (!_subPixel.Value)
            {
                matchedCenter = Round(matchedCenter);
                lt = Round(lt); rt = Round(rt); rb = Round(rb); lb = Round(lb);
            }

            var offset = new XYThetaOffset(matchedCenter.X - nominalCenter.X, matchedCenter.Y - nominalCenter.Y, thetaDeg - nominalAngle);
            result = new AlignResult { Offset = offset, MatchedCenter = matchedCenter, Judge = isFound ? Judge.OK : Judge.NG };

            if (_drawBoundingBox.Value)
            {
                var pts = new[] { lt, rt, rb, lb }.Select(p => new Point((int)p.X, (int)p.Y)).ToArray();
                Cv2.Polylines(overlay, new[] { pts }, true, isFound ? new Scalar(0, 255, 255) : new Scalar(0, 0, 255), 2, LineTypes.AntiAlias);
            }
            if (_drawAxis.Value)
            {
                double axisLen = Math.Max(templateGray.Width, templateGray.Height) * 0.3;
                double rad = thetaDeg * Math.PI / 180.0;
                var xEnd = new Point((int)(matchedCenter.X + axisLen * Math.Cos(rad)), (int)(matchedCenter.Y + axisLen * Math.Sin(rad)));
                var yEnd = new Point((int)(matchedCenter.X - axisLen * Math.Sin(rad)), (int)(matchedCenter.Y + axisLen * Math.Cos(rad)));
                var centerPt = new Point((int)matchedCenter.X, (int)matchedCenter.Y);
                Cv2.ArrowedLine(overlay, centerPt, xEnd, new Scalar(0, 0, 255), 2, LineTypes.AntiAlias);
                Cv2.ArrowedLine(overlay, centerPt, yEnd, new Scalar(0, 255, 0), 2, LineTypes.AntiAlias);
            }
            if (_drawMatches.Value)
            {
                for (int i = 0; i < goodMatches.Count; i++)
                {
                    bool isInlier = inliersMask != null && inliersMask.At<byte>(i, 0) != 0;
                    var pt = kpScene[goodMatches[i].TrainIdx].Pt;
                    Cv2.Circle(overlay, (int)pt.X, (int)pt.Y, 3, isInlier ? new Scalar(0, 255, 0) : new Scalar(0, 0, 255), -1, LineTypes.AntiAlias);
                }
            }
        }
        else
        {
            result = new AlignResult { Offset = default, MatchedCenter = default, Judge = Judge.NG };
        }

        if (_useSearchRegion.Value && (_drawBoundingBox.Value || _drawAxis.Value))
        {
            Cv2.Rectangle(overlay,
                new Point((int)searchOffsetX, (int)searchOffsetY),
                new Point((int)(searchOffsetX + _searchRegionWidth.Value), (int)(searchOffsetY + _searchRegionHeight.Value)),
                new Scalar(200, 200, 0), 1, LineTypes.AntiAlias);
        }

        // ----- Bước 8: Xuất kết quả -----
        // overlay "cho đi" thẳng vào Output -> KHÔNG Dispose(overlay) sau đây
        _outResultImage.Value = new MatVisionImage(overlay);
        _outBestMatchPoint.Value = matchedCenter;
        _outMatchScore.Value = matchScore;
        _outMatchAngle.Value = thetaDeg;
        _outIsFound.Value = isFound;
        _outLeftTop.Value = lt;
        _outRightTop.Value = rt;
        _outRightBottom.Value = rb;
        _outLeftBottom.Value = lb;
        _outResult.Value = result;

        // ----- Bước 9: Dọn dẹp tài nguyên Mat trung gian -----
        templateGray.Dispose();
        if (searchAreaOwned) searchArea.Dispose();
        if (srcGrayOwned) srcGray.Dispose();

        context.Log($"PMAlign: Isfound={isFound}, MatchScore={matchScore:F3}, MatchAngle={thetaDeg:F1}deg, BestMatchPoint=({matchedCenter.X:F1},{matchedCenter.Y:F1}).");
    }

    #region 3. Helpers

    /// <summary>Chuẩn hoá hiệu 2 góc về khoảng [-180, 180] độ, xử lý đúng trường hợp wraparound (VD 179 vs -179 chỉ lệch 2 độ).</summary>
    private static double NormalizeAngleDiff(double diffDeg)
    {
        double d = diffDeg % 360.0;
        if (d > 180.0) d -= 360.0;
        if (d < -180.0) d += 360.0;
        return d;
    }

    private static P2 Round(P2 p) => new P2(Math.Round(p.X), Math.Round(p.Y));

    #endregion
}









/*
using Moq;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Models;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging; // Chứa extension method .AsMat() để mở hộp ảnh sang Mat của OpenCV
using static System.Net.Mime.MediaTypeNames;
using P2 = VisionFlow.Core.Models.Point2d; // Định danh ngắn gọn cho Point2D, đồng bộ style với các Tool khác

namespace VisionFlow.Tools.Alignment;

/// <summary>
/// Công cụ định vị vị trí phôi (Pattern Match Alignment — tương đương PatMax của Cognex ở mức cơ bản):
/// 1. Nạp ảnh mẫu (Template) đã dạy trước, từ file hoặc cắt từ 1 vùng trên ảnh tham chiếu (Golden Image).
/// 2. Trích đặc trưng ORB (bất biến xoay, chịu được thay đổi tỉ lệ nhẹ) cho cả Template và ảnh runtime.
/// 3. Ghép cặp đặc trưng bằng BFMatcher + Lowe's Ratio Test để loại các cặp ghép mơ hồ.
/// 4. Ước lượng phép biến đổi Rigid/Similarity (xoay + tịnh tiến + scale đều) bằng RANSAC — chịu được nhiễu/ghép sai cục bộ.
/// 5. Trả về độ lệch XYTheta so với vị trí đã dạy, để FixtureTool phía sau bù trừ lại ảnh.
/// </summary>
[ToolMetadata("PMAlignt", DisplayName = "PatMax Align", Category = "Alignment",
    Description = "Feature-based geometric pattern matching alignment (ORB + RANSAC), rotation/scale invariant")]
public sealed class PMAlignTool : VisionTool // Lớp kín thực thi công cụ căn chỉnh vị trí phôi, kế thừa từ VisionTool
{
    #region 1. Khai báo các Cổng truyền nhận dữ liệu (Ports)
    private readonly InputPort<IVisionImage> _input; // Cổng vào bắt buộc: ảnh runtime cần tìm vị trí phôi trong đó
    private readonly InputPort<IVisionImage> _templateSource; // Cổng vào tùy chọn: ảnh tham chiếu (Golden Image) để cắt Template từ SourceRegion
    private readonly OutputPort<IVisionImage> _outImage; // Cổng ra: ảnh Overlay có vẽ khung Template đã tìm thấy
    private readonly OutputPort<AlignResult> _outResult; // Cổng ra: kết quả căn chỉnh (Offset + MatchedCenter + Judge)
    private readonly OutputPort<double> _outScore; // Cổng ra tiện lợi: điểm số khớp mẫu (0..1), dễ nối vào CompareTool
    #endregion

    #region 2. Khai báo các Tham số cấu hình (Parameters)
    // Nhóm dạy mẫu (Template)
    private readonly ToolParameter<TemplateImageRef> _template; // Tham số Template: hoặc cắt vùng trên ảnh tham chiếu, hoặc nạp từ file

    // Nhóm thuật toán ghép đặc trưng (Matching)
    private readonly ToolParameter<int> _maxFeatures; // Số lượng đặc trưng ORB tối đa trích ra trên mỗi ảnh
    private readonly ToolParameter<double> _ratioTestThreshold; // Ngưỡng Lowe's Ratio Test (càng nhỏ càng khắt khe, ít cặp ghép nhiễu)
        // Thuật toán KnnMatch(..., 2) sẽ tìm 2 điểm giống $A$ nhất trên ảnh runtime:
        // Ứng viên 1 (m[0]): Điểm giống $A$ nhất (khoảng cách Hamming nhỏ nhất = m[0].Distance).
        // Ứng viên 2 (m[1]): Điểm giống $A$ nhì (khoảng cách Hamming nhỏ nhì = m[1].Distance).
        // Nếu m[0] vượt trội hoàn toàn so với m[1] (khoảng cách m[0] nhỏ hơn nhiều so với m[1]):
        //      -> Điểm A đã tìm thấy đối tượng khớp rất rõ ràng, không bị lẫn lộn -> Giữ lại cặp này.
        // Nếu m[0] và m[1] có khoảng cách gần ngang nhau (ví dụ: m[0].Distance = 20, m[1].Distance = 21): Điểm $A$ trông vừa giống điểm X vừa giống điểm Y
        //      trên ảnh runtime (thường xảy ra ở các vùng có nền lặp lại, sọc caro, nhiễu ảnh) -> Loại bỏ cả 2 vì đây là cặp ghép mơ hồ.
        // _ratioTestThreshold: 
        //      * Giá trị càng thấp: Yêu cầu ứng viên 1 phải vượt trội hẳn ứng viên 2.
        //      * Giá trị càng cao: Nới lỏng hơn (Permissive): Chấp nhận cả những ứng viên 1 chỉ nhỉnh hơn ứng viên 2 một chút.
    
    private readonly ToolParameter<double> _ransacReprojThreshold;
    // Sai số tái chiếu tối đa (pixel) để 1 cặp ghép được coi là inlier trong RANSAC
    // Bản chất của RANSAC và "Sai số tái chiếu" là gì?
    // Sau khi trải qua bước Lowe's Ratio Test, bạn có một danh sách các cặp điểm ghép goodMatches. Tuy nhiên, danh sách này vẫn còn chứa các cặp ghép sai (nhiễu / outliers).
    // Thuật toán RANSAC sẽ nhảy vào để tìm ra mô hình biến đổi hình học (ma trận Affine) đúng nhất bằng cách thử-sai liên tục:
    //  1. RANSAC lấy ngẫu nhiên một tập điểm tối thiểu từ goodMatches để tính thử một ma trận biến đổi M.
    //  2. Dùng ma trận M này để chiếu thử (reproject) điểm gốc trên Template sang tọa độ mới trên ảnh Scene.
    //  3. RANSAC so sánh khoảng cách giữa tọa độ dự đoán (chiếu thử) và tọa độ thực tế thu được từ ORB trên ảnh Scene.
    //      -> Khoảng cách này chính là Sai số tái chiếu (Reprojection Error).
    //  => Sai số = Khoảng cách (Điểm thực tế trên Scene và điểm dự đoán từ Template qua M).
    // _ransacReprojThreshold: chính là khoảng cách dung sai tối đa (tính theo pixel) cho phép.
    //  * Nếu Sai số <= _ransacReprojThreshold: Cặp điểm đó được RANSAC công nhận là Inlier (cặp ghép đúng, hợp lệ).
    //  * Nếu Sai số >= _ransacReprojThreshold: Cặp điểm đó bị gán là Outlier (cặp ghép sai/nhiễu) và bị đánh dấu 0 trong inliersMask.

    private readonly ToolParameter<int> _minInlierCount; // Số lượng inlier tối thiểu để coi là tìm thấy hợp lệ
    private readonly ToolParameter<double> _minMatchScore; // Điểm số khớp mẫu tối thiểu (tỉ lệ inlier / tổng cặp ghép tốt) để phán định OK

    // Nhóm hiển thị đồ họa (Display)
    private readonly ToolParameter<bool> _drawBoundingBox; // Bật/tắt vẽ khung Template đã biến đổi vào đúng vị trí tìm thấy
    private readonly ToolParameter<bool> _drawAxis; // Bật/tắt vẽ trục tọa độ tại tâm đã tìm thấy, thể hiện hướng xoay
    private readonly ToolParameter<bool> _drawMatches; // Bật/tắt vẽ các đường nối cặp đặc trưng ghép được (phục vụ debug)
    #endregion

    public PMAlignTool()
    {
        // Khởi tạo cổng vào/ra
        _input = AddInput<IVisionImage>("Image"); // Đăng ký cổng nhập ảnh runtime
        _templateSource = AddInput<IVisionImage>("TemplateSource", optional: true); // Đăng ký cổng nhập ảnh tham chiếu, không bắt buộc nếu dùng Template từ file
        _outImage = AddOutput<IVisionImage>("Image", "Overlay"); // Đăng ký cổng xuất ảnh vẽ đè kết quả
        _outResult = AddOutput<AlignResult>("Result", "Align Result"); // Đăng ký cổng xuất kết quả căn chỉnh
        _outScore = AddOutput<double>("Score", "Match Score"); // Đăng ký cổng xuất điểm số khớp mẫu

        // --- Nhóm Template ---
        _template = AddParameter(
            "Template",
            new TemplateImageRef(SourceRegion: new RotatedRectRegion(new P2(150, 120), 200, 150, 0)), // Mặc định: cắt vùng 200x150 tại tâm (150,120) trên ảnh tham chiếu
            displayName: "Template",
            category: "Template",
            order: 1,
            interaction: ParameterInteraction.Template // Bật tính năng kéo khung dạy mẫu trực tiếp trên ảnh UI
        );

        // --- Nhóm Matching ---
        _maxFeatures = AddParameter<int>("MaxFeatures", 500, "Max Features", min: 50, max: 5000, category: "Matching", order: 1); // Mặc định 500 đặc trưng, đủ cho hầu hết vật thể có texture rõ
        _ratioTestThreshold = AddParameter<double>("RatioTestThreshold", 0.75, "Ratio Test Threshold", min: 0.5, max: 0.95, category: "Matching", order: 2); // Ngưỡng chuẩn theo khuyến nghị gốc của Lowe (SIFT paper)
        _ransacReprojThreshold = AddParameter<double>("RansacReprojThreshold", 3.0, "RANSAC Reproj. Threshold (px)", min: 0.5, max: 20.0, category: "Matching", order: 3); // Sai số cho phép 3 pixel là hợp lý cho hầu hết ứng dụng
        _minInlierCount = AddParameter<int>("MinInlierCount", 8, "Min Inlier Count", min: 3, max: 1000, category: "Matching", order: 4); // Cần tối thiểu 8 inlier để coi là kết quả đáng tin cậy (an toàn hơn mức tối thiểu lý thuyết là 3)
        _minMatchScore = AddParameter<double>("MinMatchScore", 0.3, "Min Match Score", min: 0.0, max: 1.0, category: "Matching", order: 5); // Mặc định 30% cặp ghép tốt phải là inlier mới coi là OK

        // --- Nhóm Display ---
        _drawBoundingBox = AddParameter<bool>("DrawBoundingBox", true, "Draw Bounding Box", category: "Display", order: 1); // Mặc định bật vẽ khung Template tại vị trí tìm thấy
        _drawAxis = AddParameter<bool>("DrawAxis", true, "Draw Axis", category: "Display", order: 2); // Mặc định bật vẽ trục X/Y tại tâm để thấy rõ hướng xoay
        _drawMatches = AddParameter<bool>("DrawMatches", false, "Draw Feature Matches", category: "Display", order: 3); // Mặc định tắt (chỉ bật khi cần debug vì có thể rối ảnh)
    }

    protected override void OnExecute(IToolContext context)
    {
        if (_input.Value == null) // Kiểm tra an toàn dữ liệu đầu vào
            throw new ArgumentNullException(nameof(_input), "Ảnh runtime đầu vào không được rỗng!");

        var src = _input.Value!.AsMat(); // Mở hộp lấy ma trận ảnh OpenCV của ảnh runtime

        // Bước 1: Chuyển ảnh runtime sang xám để trích đặc trưng
        Mat srcGray;    // Khai báo biến biến ở phạm vi ngoài
        bool srcGrayOwned = false; // Cờ đánh dấu ma trận xám do hàm này tự tạo, cần tự giải phóng
        if (src.Channels() == 1) { srcGray = src; }     // Trường hợp A: Ảnh đã xám sẵn
        else { srcGray = new Mat(); Cv2.CvtColor(src, srcGray, ColorConversionCodes.BGR2GRAY); srcGrayOwned = true; }   // Trường hợp B: Ảnh màu -> Cần cấp phát vùng nhớ mới

        // Bước 2: Nạp/Cắt ảnh Template theo cấu hình TemplateImageRef (ưu tiên FilePath, sau đó tới SourceRegion)
        var templateRef = _template.Value;
        Mat templateGray;
        bool templateOwned = true; // Ma trận Template luôn do hàm này tạo ra, cần tự giải phóng khi xong
        P2 nominalCenter; // Tâm "chuẩn" (nominal) của Template — mốc để tính Offset
        double nominalAngle; // Góc "chuẩn" (nominal) của Template — mốc để tính Offset

        if (!string.IsNullOrWhiteSpace(templateRef.FilePath) && File.Exists(templateRef.FilePath))
        {
            // Trường hợp 1: Template nạp từ file ảnh đã lưu sẵn (không gắn với tọa độ trên 1 ảnh gốc cụ thể)
            using Mat templateColor = Cv2.ImRead(templateRef.FilePath, ImreadModes.Color);
            // templateRef.FilePath: Tham số thứ nhất, truyền vào đường dẫn tệp ảnh trên ổ đĩa (ví dụ: C:\Templates\ProductA.png).
            // Khi dùng ImreadModes.Color (tương đương giá trị 1), OpenCV sẽ ép bức ảnh đầu vào về dạng ảnh màu BGR 3 kênh (Blue, Green, Red), bất kể file
            //      ảnh gốc trên đĩa là ảnh xám (Grayscale), ảnh RGB hay ảnh PNG có kênh alpha trong suốt.
            // => templateColor: Tên biến chứa dữ liệu ảnh mẫu (Template) ở dạng ma trận màu BGR vừa đọc được.
            // Tác dụng của using: Đảm bảo đối tượng templateColor sẽ tự động gọi hàm Dispose() để giải phóng vùng nhớ RAM/VRAM ngay lập tức
            //      khi hàm OnExecute chạy xong (khi thoát khỏi phạm vi/block của lệnh).

            templateGray = new Mat();   // Khai báo ma trận xám
            Cv2.CvtColor(templateColor, templateGray, ColorConversionCodes.BGR2GRAY);   // Chuyển từ BGR (3 kênh) sang GRAY (1 kênh)
            // Không có vị trí "chuẩn" nào khác ngoài tâm khung hình runtime -> quy ước Offset=0 nghĩa là vật thể nằm giữa khung ảnh
            nominalCenter = new P2(srcGray.Width / 2.0, srcGray.Height / 2.0);
            nominalAngle = 0.0;
        }
        else if (templateRef.SourceRegion.HasValue) // Kiểm tra nếu cấu hình Template có chứa thông số vùng chọn xoay (RotatedRectRegion)
        {
            // Trường hợp 2: Template được cắt từ 1 vùng chữ nhật xoay trên ảnh tham chiếu (Golden Image)
            if (_templateSource.Value == null) // Kiểm tra xem cổng nhập 'TemplateSource' đã được nối dây ảnh gốc chưa
                throw new InvalidOperationException("Template được cấu hình theo SourceRegion nhưng cổng 'TemplateSource' chưa được nối ảnh tham chiếu!");

            // 1. Lấy thông số vùng chọn đã dạy/vẽ trên UI (bao gồm Tâm X/Y, Chiều rộng W, Chiều cao H, Góc xoay Theta)
            RotatedRectRegion reg = templateRef.SourceRegion.Value;
            Mat refMat = _templateSource.Value!.AsMat(); // Mở hộp lấy ma trận OpenCV (Mat) từ cổng nhập ảnh tham chiếu

            // Khởi tạo điểm tâm cắt ảnh dạng số thực Point2f (OpenCV yêu cầu số thực float cho các phép tính hình học)
            // Chuyển đổi (ép kiểu) tọa độ tâm của vùng chọn từ số thực kép (double) sang số thực đơn (float) để phù hợp với định dạng
            //    dữ liệu đầu vào mà thư viện OpenCV (OpenCvSharp) yêu cầu.
            Point2f center = new Point2f((float)reg.Center.X, (float)reg.Center.Y);

            // Khởi tạo kích thước vùng chọn (Width x Height), dùng Math.Max để đảm bảo W và H luôn >= 1 pixel, tránh lỗi crash OpenCV
            Size size = new Size(Math.Max(1, (int)reg.Width), Math.Max(1, (int)reg.Height));

            Mat rotSource = refMat; // Biến con trỏ tạm, mặc định trỏ tới ảnh tham chiếu gốc
            bool rotated = false;   // Cờ đánh dấu xem ảnh tham chiếu có vừa trải qua phép xoay hay không

            // 2. Kiểm tra góc xoay của vùng chọn: Nếu khác 0 độ (dùng ngưỡng 1e-5 = 0.00001 để tránh lỗi sai số số thực float)
            if (Math.Abs(reg.AngleDeg) > 1e-5)
            {
                // Tính ma trận biến đổi Affine 2D (2x3) để xoay ảnh xung quanh điểm 'center' đúng một góc 'reg.AngleDeg' với tỉ lệ 1.0 (không thu phóng)
                using Mat rotMat = Cv2.GetRotationMatrix2D(center, reg.AngleDeg, 1.0);

                rotSource = new Mat(); // Cấp phát vùng nhớ mới để chứa bức ảnh sau khi xoay

                // Thực hiện xoay toàn bộ bức ảnh gốc 'refMat' theo ma trận 'rotMat', kết quả ghi vào 'rotSource'.
                // Sử dụng nội suy tuyến tính (Linear) và chế độ lấp đầy viền bằng cách lặp lại pixel mép (Replicate)
                Cv2.WarpAffine(refMat, rotSource, rotMat, refMat.Size(), InterpolationFlags.Linear, BorderTypes.Replicate);
                // Cv2.WarpAffine trong OpenCV: xoay, tịnh tiến hoặc biến dạng hình học toàn bộ bức ảnh dựa trên một ma trận biến đổi Affine (2x3) được tính toán từ trước
                // 1. refMat (Input Matrix): Bức ảnh gốc đầu vào(ảnh tham chiếu / Golden Image) cần thực hiện phép xoay.
                // 2. rotSource (Output Matrix): Bức ảnh kết quả sau khi đã được xoay xong.
                // 3.rotMat (Transformation Matrix):Ma trận Affine kích thước 2x3 chứa các hệ số lượng giác (sin, cos) biểu diễn góc xoay và tọa độ tâm xoay.
                // 4. refMat.Size() (Output Size): Kích thước(Width x Height) của bức ảnh kết quả rotSource.
                //      Ở đây truyền vào đúng bằng kích thước ảnh gốc để khung ảnh không bị thu nhỏ hay thay đổi tỉ lệ.
                // 5. InterpolationFlags.Linear (Thuật toán nội suy):Nội suy tuyến tính (Bilinear Interpolation). Khi xoay ảnh một góc bất kỳ, tọa độ các pixel
                //      mới thường là các số thập phân (không nằm đúng tọa độ lưới nguyên). OpenCV sẽ tự động tính toán màu sắc cho các pixel mới bằng cách
                //      lấy trung bình có trọng số của 4 pixel lân cận.
                //  -> Ý nghĩa: Giúp bức ảnh sau khi xoay giữ được độ mượt mà, không bị răng cưa hay vỡ nét các đường mép chi tiết.
                // 6. BorderTypes.Replicate (Xử lý vùng viền ngoài ảnh):
                //   - Khi xoay ảnh, 4 góc của bức ảnh mới sẽ bị trống (vì không có dữ liệu pixel gốc đắp vào).
                //   - Replicate báo cho OpenCV biết: Hãy lặp lại (kéo dãn) màu sắc của các pixel nằm ở mép ngoài cùng để lấp đầy các khoảng trống góc đen đó,
                //     giúp thuật toán trích xuất đặc trưng phía sau không bị bắt lầm các đường viền đen nhân tạo.

                rotated = true; // Bật cờ đánh dấu đã tạo ra một ma trận xoay mới (cần phải Dispose về sau)
            }

            // 3. Cắt chính xác vùng ảnh chữ nhật thẳng đứng kích thước 'size' tại vị trí tâm 'center' từ ảnh đã được xoay 'rotSource'
            using Mat templateColor = new Mat(); // Khởi tạo vùng nhớ tạm lưu ảnh màu cắt được (dùng 'using' để tự động xả RAM)
            Cv2.GetRectSubPix(rotSource, size, center, templateColor); // Cắt ảnh độ phân giải dưới pixel (Sub-pixel accuracy)

            // Nếu ở bước 2 có tạo ảnh xoay tạm 'rotSource' (khác với ảnh gốc refMat), tiến hành giải phóng RAM C++ lập tức
            if (rotated) rotSource.Dispose();

            templateGray = new Mat(); // Cấp phát vùng nhớ cho ma trận ảnh Template mức xám

            // Kiểm tra số kênh màu của ảnh vừa cắt:
            if (templateColor.Channels() == 1)
                templateColor.CopyTo(templateGray); // Nếu ảnh vốn đã là ảnh xám (1 kênh), chỉ cần copy sang
            else
                Cv2.CvtColor(templateColor, templateGray, ColorConversionCodes.BGR2GRAY); // Nếu là ảnh màu BGR (3 kênh), chuyển đổi sang ảnh xám 1 kênh

            // Lưu lại vị trí tâm và góc xoay "mốc" (Nominal) ban đầu đã dạy trên ảnh tham chiếu -> Dùng làm căn cứ để tính Offset (dX, dY, dTheta) ở các bước sau
            nominalCenter = reg.Center;
            nominalAngle = reg.AngleDeg;
        }
        else
        {
            throw new InvalidOperationException("Tham số Template chưa được cấu hình (cần SourceRegion hoặc FilePath hợp lệ)!");
        }

        // Bước 3: Trích đặc trưng ORB cho cả Template và ảnh runtime
        using var orb = ORB.Create(_maxFeatures.Value); // Khởi tạo đối tượng thuật toán ORB với số lượng điểm đặc trưng tối đa chỉ định (tự động xả bộ nhớ C++ nhờ 'using')
        using Mat descTemplate = new Mat(); // Khởi tạo ma trận chứa chuỗi mô tả nhị phân 256-bit (Descriptor) cho ảnh Template
        orb.DetectAndCompute(templateGray, null, out KeyPoint[] kpTemplate, descTemplate);
        // 2 nhiệm vụ chính: vừa tìm các "điểm đặc trưng" (KeyPoints) trên bức ảnh mẫu, vừa tính toán "mã nhận dạng" (Descriptors) cho từng điểm đó.
        // - templateGray: ảnh xám đầu vào (Template).
        // - null: Báo cho OpenCV biết: "Hãy tìm kiếm điểm đặc trưng trên toàn bộ bức ảnh templateGray". Nếu truyền vào một ảnh
        //      nhị phân (trắng/đen), ORB sẽ chỉ tìm điểm đặc trưng ở những vùng có màu trắng.
        // - out KeyPoint[] kpTemplate: (Output 1: Danh sách điểm đặc trưng):
        //      -> Một mảng các đối tượng KeyPoint trả về danh sách các vị trí góc/mép nổi bật mà thuật toán tìm được.
        //   Mỗi KeyPoint chứa các thông tin quan trọng nào?
        //    * Pt (X, Y): Tọa độ của điểm đặc trưng trên ảnh (độ chính xác dạng số thực float).
        //    * Angle: Góc hướng (Orientation) của điểm góc đó (từ 0 ~ 360 độ). Nhờ có góc này mà thuật toán ORB chịu được phép xoay ảnh.
        //    * Response: Độ "mạnh/nổi bật" của điểm góc (dùng để xếp hạng điểm tốt/dở).
        //    * Octave: Tầng kim tự tháp ảnh (Image Pyramid) nơi điểm đó được phát hiện (giúp thuật toán chịu được phép thu phóng Scale).
        // - descTemplate (Output 2: Ma trận chuỗi mô tả / Descriptors):
        //    * Là gì: Một ma trận Mat chứa các mã đặc trưng nhị phân đại diện cho vùng ảnh xung quanh mỗi KeyPoint.
        //    * Cấu trúc ma trận này như thế nào?
        //      + Số hàng (Rows): Đúng bằng số lượng KeyPoint tìm được (kpTemplate.Length).
        //      + Số cột (Cols): Mặc định là 32 columns (kiểu dữ liệu CV_8U - 8-bit unsigned char).
        //      + Ý nghĩa: 32 bytes x 8 bits = 256 bits. Mỗi KeyPoint sẽ được ORB mã hóa thành một chuỗi 256 bit nhị phân (mã 0 và 1) đại diện hoạ tiết bề mặt xung quanh điểm đó.
        // => Hai bước diễn ra bên trong hàm này (Under the Hood): Khi dòng lệnh chạy, OpenCV thực hiện 2 thuật toán nối tiếp nhau:
        //  Bước A: Detect (Tìm vị trí KeyPoint) — Dùng thuật toán oFAST:
        //    * 1. OpenCV quét qua các pixel trên ảnh và so sánh độ sáng của pixel trung tâm với 16 pixel nằm trên vòng tròn xung quanh nó.
        //    * 2. Nếu có một chuỗi các pixel liên tiếp sáng hơn hoặc tối hơn hẳn pixel trung tâm, điểm đó được coi là một điểm góc (KeyPoint).
        //    * 3. Tính toán trọng tâm độ sáng (Intensity Centroid) của vùng lân cận để tìm ra góc hướng (Angle) cho điểm góc đó.
        //  Bước B: Compute (Tạo chuỗi mô tả Descriptor) — Dùng thuật toán rBRIEF:
        //    * 1. Xoay vùng ảnh xung quanh điểm góc theo góc hướng (Angle) vừa tính ở Bước A (để đảm bảo dù phôi bị xoay thì mã nhị phân tạo ra vẫn giống nhau).
        //    * 2. So sánh độ sáng của 256 cặp điểm ngẫu nhiên đã định sẵn trong vùng ảnh đó:
        //          Nếu điểm A sáng hơn điểm B -> Ghi bit 1.
        //          Nếu điểm A tối hơn điểm B -> Ghi bit 0.
        //    * Kết quả thu được một dãy 256 bit (32 bytes) đóng vai trò như "Dấu vân tay" của điểm đặc trưng đó.
        // => Tóm tắt dòng lệnh:
        //  Dòng lệnh này giúp máy tính "học" các chi tiết nhận diện của phôi mẫu: nó ghi lại ở đâu có điểm góc (kpTemplate) và chi tiết đó trông như thế nào
        //      dưới dạng mã nhị phân (descTemplate) để ở Bước 4 có thể đem so sánh (Match) với ảnh thực tế!

        using Mat descScene = new Mat(); // Khởi tạo ma trận chứa chuỗi mô tả nhị phân (Descriptor) cho ảnh runtime (Scene)
        orb.DetectAndCompute(srcGray, null, out KeyPoint[] kpScene, descScene); // Trích xuất KeyPoint và Descriptor cho ảnh runtime

        AlignResult result; // Khai báo biến lưu kết quả căn chỉnh cuối cùng (Offset, Tâm, OK/NG)
        double matchScore;  // Khai báo biến lưu điểm số khớp mẫu (tỉ lệ 0.0 -> 1.0)
        Mat? affine = null; // Khai báo ma trận Affine 2x3 để chứa kết quả ước lượng hình học (xoay, tịnh tiến, scale)
        List<DMatch> goodMatches = new(); // Khai báo danh sách chứa các cặp điểm ghép thành công sau khi lọc Ratio Test
        Mat? inliersMask = null; // Khai báo mảng mặt nạ đánh dấu điểm nào là Inlier (1) hay Outlier (0) do RANSAC trả về

        // Bước 4: Ghép cặp đặc trưng bằng BFMatcher (khoảng cách Hamming, phù hợp mô tả nhị phân ORB) + Lowe's Ratio Test
        // Bước 4 sẽ đi so sánh từng dấu vân tay của Template với các dấu vân tay của Scene (ảnh runtime) để tìm xem điểm nào trên ảnh thật tương ứng với điểm nào trên ảnh mẫu.
        if (descTemplate.Rows > 0 && descScene.Rows > 0) // Kiểm tra điều kiện: Cả 2 ảnh đều phải trích xuất được ít nhất 1 điểm đặc trưng
        {
            using var matcher = new BFMatcher(NormTypes.Hamming); // Khởi tạo bộ ghép cặp Brute-Force dùng "thước đo" độ giống nhaulà khoảng cách Hamming (chuyên dùng cho mã nhị phân ORB)
            // 1. Khởi tạo BFMatcher với khoảng cách Hamming:
            //  - BFMatcher (Brute-Force Matcher): Thuật toán tìm kiếm theo kiểu "vét cạn". Nó lấy từng chuỗi Descriptor của ảnh Template và so sánh
            //    lần lượt với tất cả các chuỗi Descriptor trên ảnh Scene để tìm ra cặp khớp nhất.
            //  - NormTypes.Hamming (Khoảng cách Hamming):
            //    * Vì đặc trưng của ORB là dãy nhị phân 256 bit (0 và 1), nên việc đo khoảng cách giữa 2 điểm đặc trưng không dùng
            //      khoảng cách Euclid ($\sqrt{\Delta x^2 + \Delta y^2}$) thông thường.
            //    * Khoảng cách Hamming được tính bằng phép toán XOR (⊕) giữa 2 chuỗi bit: nó chỉ đơn giản là đếm số lượng bit khác nhau giữa 2 chuỗi bit:
            //      Ví dụ: Bit chuỗi A: 1 0 1 1 0
            //             Bit chuỗi B: 1 0 0 1 1       -> Số bit khác nhau = 2 -> Khoảng cách Hamming = 2.
            //      -> Ưu điểm: Phép toán XOR trên chuỗi bit được CPU xử lý ở tầng vi lệnh (Bitwise XOR + Popcount) cực kỳ nhanh, phù hợp
            //         cho hệ thống Vision công nghiệp đòi hỏi thời gian tính toán cỡ miligiây (ms).

            DMatch[][] knnMatches = matcher.KnnMatch(descTemplate, descScene, 2);
            // 2. Tìm 2 ứng viên giống nhất (k=2) trên ảnh Scene cho từng đặc trưng của Template
            //  - k-Nearest Neighbors (k-NN) với k=2: Với mỗi một điểm đặc trưng P_{template} trên ảnh Template, thuật toán không chỉ tìm 1 điểm giống nhất,
            //    mà sẽ tìm ra 2 điểm trên ảnh Scene giống nó nhất:
            //    * m[0] (Ứng viên 1 - Best Match): Điểm có khoảng cách Hamming nhỏ nhất (D_1).
            //    * m[1] (Ứng viên 2 - Second Best Match): Điểm có khoảng cách Hamming nhỏ thứ nhì (D_2).
            // -> Kết quả knnMatches trả về là mảng 2 chiều, trong đó mỗi phần tử chứa đúng 2 đối tượng DMatch (đại diện cho ứng viên 1 và 2).
            // Kết quả cho điểm T0 (QueryIdx = 0):
            //      knnMatches[0][0]-> { QueryIdx = 0, TrainIdx = 0, Distance = 1 } // Ứng viên 1: S0 (tốt nhất)
            //      knnMatches[0][1]-> { QueryIdx = 0, TrainIdx = 1, Distance = 2 } // Ứng viên 2: S1 (tốt nhì)
            // Kết quả cho điểm T1 (QueryIdx = 1):
            //      knnMatches[1][0]-> { QueryIdx = 1, TrainIdx = 2, Distance = 5 } // Ứng viên 1
            //      knnMatches[1][1]-> { QueryIdx = 1, TrainIdx = 1, Distance = 12 } // Ứng viên 2

            double ratio = _ratioTestThreshold.Value; // Lấy ngưỡng tỉ lệ Lowe (ví dụ: 0.75)
            goodMatches = knnMatches
                .Where(m => m.Length == 2 && m[0].Distance < ratio * m[1].Distance) // Lọc: Chỉ giữ cặp nếu khoảng cách ứng viên 1 nhỏ hơn (ratio * khoảng cách ứng viên 2)
                .Select(m => m[0]) // Chọn lấy ứng viên 1 (cặp ghép tốt nhất)
                .ToList(); // Chuyển kết quả lọc thành danh sách List<DMatch>
            // Tại sao phải dùng Lowe's Ratio Test?
            //  - Trong thực tế, ảnh công nghiệp có rất nhiều vùng họa tiết lặp lại (ví dụ: các lỗ tròn giống hệt nhau, đường kẻ song song, nền kim loại phay xước...).
            //  - Nếu một điểm đặc trưng nằm trên vùng họa tiết lặp lại, nó sẽ có rất nhiều điểm khác trên ảnh Scene trông "na ná" như nó.
            //    Lúc này D_1 (ứng viên tốt nhất) và D_2 (ứng viên tốt nhì) sẽ có khoảng cách xấp xỉ bằng nhau. Cặp ghép này cực kỳ mơ hồ và dễ sai.
            //  - Nếu điểm đặc trưng đó là duy nhất (độc bản, góc cạnh rõ ràng), ứng viên tốt nhất $D_1$ sẽ vượt trội hoàn toàn so với ứng viên thứ nhì $D_2$
        }

        // Bước 5: Ước lượng phép biến đổi Similarity (xoay + tịnh tiến + scale đều) bằng RANSAC nếu đủ số cặp ghép
        // Sau khi Bước 4 đã cho ra một danh sách các cặp điểm ghép goodMatches, trong danh sách đó vẫn có thể tồn tại các cặp ghép sai (do nhiễu ảnh, họa tiết lặp lại...).
        //  -> Bước 5 dùng thuật toán RANSAC để loại bỏ hoàn toàn các cặp ghép sai đó và tìm ra mối quan hệ không gian thực sự
        //     (tọa độ tịnh tiến $\Delta X, \Delta Y$, góc xoay $\Delta \Theta$) giữa phôi mẫu và phôi thực tế.
        if (goodMatches.Count >= _minInlierCount.Value) // Điều kiện: Số cặp ghép tốt thu được phải đạt hoặc vượt ngưỡng tối thiểu cấu hình
        {
            Point2f[] srcPts = goodMatches.Select(m => kpTemplate[m.QueryIdx].Pt).ToArray(); // Trích xuất danh sách tọa độ (X, Y) các điểm góc trên hệ ảnh Template
            Point2f[] dstPts = goodMatches.Select(m => kpScene[m.TrainIdx].Pt).ToArray();    // Trích xuất danh sách tọa độ (X, Y) tương ứng trên hệ ảnh runtime (Scene)
            // .pt: Thuộc tính này trả về tọa độ $2D$ $(X, Y)$ của điểm đặc trưng trên bức ảnh dưới dạng một điểm số thực Point2f.

            inliersMask = new Mat(); // Cấp phát ma trận chứa kết quả phân loại Inlier/Outlier của RANSAC
            // Chức năng: Khởi tạo một ma trận kiểu byte 1 cột (N x 1) để chứa kết quả phân loại của RANSAC sau khi tính toán xong.
            // Quy ước:
            //   * Giá trị 1 (Inlier): Cặp điểm thứ $i$ khớp đúng với mô hình hình học chung (cặp ghép đúng).
            //   * Giá trị 0 (Outlier): Cặp điểm thứ $i$ nằm lệch khỏi quy luật hình học chung (cặp ghép sai/nhiễu).

            // Chạy RANSAC để tính ma trận biến đổi Affine Partial 2D (Similarity: gồm Tịnh tiến, Xoay và Scale đều)
            // Cv2.EstimateAffinePartial2D: Hàm này nhận vào 2 tập điểm và ước lượng ma trận Similarity Transformation (Phép biến đổi đồng dạng 2D).
            // A. Tại sao lại dùng EstimateAffinePartial2D thay vì EstimateAffine2D chuẩn?
            //    * EstimateAffine2D chuẩn (6 độ do): Cho phép tịnh tiến, xoay, scale X/Y độc lập và kéo xiên (Shear).
            //      Trong thực tế camera cố định nhìn từ trên xuống, sản phẩm không bao giờ bị biến dạng kéo xiên.
            //    * EstimateAffinePartial2D (4 độ tự do - Similarity): Chỉ cho phép Tịnh tiến ($t_x, t_y$), Xoay ($\theta$), và Scale đều ($s$).
            //   -> Ma trận trả về có dạng $2 \times 3$:
            //    [a b tx    = [s.cos(theta)   -s.sin(theta)   tx
            //     d d ty]      s.sin(theta)    s.cos(theta)   ty] 
            //   - Ưu điểm: Cực kỳ ổn định cho bài toán Vision định vị phôi (Alignment/Guidance), không bị biến dạng méo hình do sai số điểm nhiễu.
            // B. Thuật toán RANSAC (RANdom SAmple Consensus) hoạt động thế nào?
            //  RANSAC giải quyết bài toán lọc nhiễu qua các vòng lặp cực nhanh:
            //  1. Lấy mẫu: RANSAC bốc ngẫu nhiên một số lượng điểm tối thiểu (thường là 2-3 cặp điểm trong srcPts/dstPts).
            //  2. Dựng mô hình: Tính thử một ma trận biến đổi dựa trên 2-3 cặp điểm ngẫu nhiên này.
            //  3. Thử nghiệm (Voting): Dùng ma trận vừa tính chiếu thử toàn bộ các điểm srcPts còn lại sang hệ tọa độ mới.
            //  4. Đếm Inliers: So sánh khoảng cách giữa điểm chiếu thử và điểm thật dstPts. Nếu khoảng cách < Threshold (ngưỡng _ransacReprojThreshold.Value),
            //      -> điểm đó bình chọn 1 phiếu (Inlier).
            //  5. Lặp lại: Lặp lại quy trình trên hàng trăm lần. Ma trận nào gom được nhiều phiếu bầu nhất sẽ được chọn làm kết quả cuối cùng (affine), và
            //      danh sách các điểm bỏ phiếu cho nó sẽ được ghi nhận vào inliersMask.
            // C. _ransacReprojThreshold.Value (Sai số tái chiếu / Reprojection Error):
            //  - Ý nghĩa: Là bán kính vùng dung sai (tính bằng pixel, ví dụ: 3.0 px).
            //  - Nếu một điểm sau khi chiếu bằng ma trận thử nghiệm mà nằm cách điểm thật quá 3 px, RANSAC sẽ gạch tên nó ra khỏi danh sách Inlier (đánh dấu bằng 0 trong inliersMask).
            // => Tóm tắt kết quả sau khi Bước 5 chạy xong:
            //  1. affine: Một ma trận 2x3 chứa chính xác góc xoay và tọa độ dịch chuyển của phôi thực tế.
            //     (Nếu RANSAC thất bại do phôi bị che khuất hoặc quá ít điểm chuẩn, affine sẽ trả về null hoặc ma trận rỗng).
            // inliersMask: Mảng đánh dấu chính xác những cặp đặc trưng nào là "chuẩn thật" để Bước 6 tính ra Match Score = (Số Inlier / Tổng số Good Matches).
            affine = Cv2.EstimateAffinePartial2D(
                InputArray.Create(srcPts), // Tập điểm nguồn (Template)
                InputArray.Create(dstPts), // Tập điểm đích (Scene)
                inliersMask,               // Mảng xuất kết quả: 1 nếu là Inlier (đúng), 0 nếu là Outlier (nhiễu)
                RobustEstimationAlgorithms.RANSAC, // Thuật toán lọc nhiễu RANSAC
                _ransacReprojThreshold.Value);      // Sai số tái chiếu cho phép (px)
        }

        // Bước 6: Từ ma trận Affine, tính Offset (X, Y, Theta) và điểm số khớp mẫu
        Mat overlay = new Mat(); // Khai báo ma trận ảnh dùng để vẽ đồ họa đè kết quả (Overlay)
        Cv2.CvtColor(srcGray, overlay, ColorConversionCodes.GRAY2BGR); // Chuyển ảnh xám runtime thành ảnh màu BGR để vẽ các đường nét có màu (vàng, đỏ, xanh...)

        int inlierCount = 0; // Khai báo biến đếm số lượng cặp điểm hợp lệ (Inlier)
        if (affine != null && !affine.Empty()) // Kiểm tra nếu RANSAC tính toán thành công ra ma trận Affine
        {
            if (inliersMask != null) // Nếu có mặt nạ inliersMask trả về
            {
                int rows = inliersMask.Rows; // Lưu lại số lượng hàng để tối ưu hiệu năng vòng lặp
                for (int i = 0; i < rows; i++)
                    if (inliersMask.At<byte>(i, 0) != 0) inlierCount++; // Đếm các ô có giá trị khác 0 (chính là điểm Inlier)
            }

            // Điểm số khớp mẫu = (Số lượng điểm Inlier / Tổng số điểm ghép tốt ban đầu)
            matchScore = goodMatches.Count > 0 ? (double)inlierCount / goodMatches.Count : 0.0;

            // Bóc tách các hệ số trong ma trận Affine 2x3: [ [a, b, tx], [c, d, ty] ]
            double a = affine.At<double>(0, 0), b = affine.At<double>(0, 1);
            double c = affine.At<double>(1, 0), d = affine.At<double>(1, 1);
            double tx = affine.At<double>(0, 2), ty = affine.At<double>(1, 2);

            double thetaDeg = Math.Atan2(c, a) * 180.0 / Math.PI; // Tính góc xoay (đơn vị độ) dùng hàm arctan2(c, a)
            double scale = Math.Sqrt(a * a + c * c); // Tính tỉ lệ phóng to/thu nhỏ (Scale factor = sqrt(a^2 + c^2))

            // Hàm cục bộ (Local Function): Chiếu 1 điểm p(x,y) từ hệ tọa độ Template sang hệ tọa độ Scene nhờ ma trận Affine
            P2 TemplateToScene(P2 p) => new P2(a * p.X + b * p.Y + tx, c * p.X + d * p.Y + ty);

            P2 templateCenter = new P2(templateGray.Width / 2.0, templateGray.Height / 2.0); // Tính vị trí tâm đại số của ảnh Template
            P2 matchedCenter = TemplateToScene(templateCenter); // Chiếu tâm Template sang vị trí thực tế tìm thấy trên ảnh Scene

            // Tính độ lệch Offset (Delta X, Delta Y, Delta Theta) so với vị trí mốc chuẩn đã dạy (nominal)
            var offset = new XYThetaOffset(
                matchedCenter.X - nominalCenter.X,
                matchedCenter.Y - nominalCenter.Y,
                thetaDeg - nominalAngle);

            // Đánh giá kết quả: Phải đạt cả 2 điều kiện (Match Score >= minScore VÀ Số Inliers >= minInliers)
            Judge judge = (matchScore >= _minMatchScore.Value && inlierCount >= _minInlierCount.Value)
                ? Judge.OK
                : Judge.NG;

            result = new AlignResult { Offset = offset, MatchedCenter = matchedCenter, Judge = judge }; // Đóng gói kết quả căn chỉnh

            // Vẽ khung Template đã biến đổi đúng vị trí/góc tìm thấy trên ảnh runtime
            if (_drawBoundingBox.Value) // Nếu tham số bật vẽ Bounding Box = true
            {
                double w = templateGray.Width;
                double h = templateGray.Height;

                // Chiếu 4 góc của khung Template sang hệ tọa độ Scene
                P2[] corners = {
                    TemplateToScene(new P2(0, 0)),
                    TemplateToScene(new P2(w, 0)),
                    TemplateToScene(new P2(w, h)),
                    TemplateToScene(new P2(0, h))
                };

                var pts = corners.Select(p => new Point((int)p.X, (int)p.Y)).ToArray(); // Chuyển các điểm về dạng System.Drawing.Point (số nguyên pixel)
                Cv2.Polylines(overlay, new[] { pts }, true, new Scalar(0, 255, 255), 2); // Vẽ đa giác khép kín 4 cạnh màu vàng (Yellow: B=0, G=255, R=255), độ dày 2px
            }

            if (_drawAxis.Value) // Nếu tham số bật vẽ Trục tọa độ = true
            {
                double axisLen = Math.Max(templateGray.Width, templateGray.Height) * 0.3; // Độ dài trục vẽ = 30% cạnh lớn nhất của Template
                double rad = thetaDeg * Math.PI / 180.0; // Chuyển góc xoay từ Độ sang Radian

                // Tính điểm đầu mút của trục X (hướng theo góc theta) và trục Y (vuông góc với trục X)
                var xEnd = new Point((int)(matchedCenter.X + axisLen * Math.Cos(rad)), (int)(matchedCenter.Y + axisLen * Math.Sin(rad)));
                var yEnd = new Point((int)(matchedCenter.X - axisLen * Math.Sin(rad)), (int)(matchedCenter.Y + axisLen * Math.Cos(rad)));
                var centerPt = new Point((int)matchedCenter.X, (int)matchedCenter.Y);

                Cv2.ArrowedLine(overlay, centerPt, xEnd, new Scalar(0, 0, 255), 2); // Vẽ mũi tên Trục X màu đỏ (Red: B=0, G=0, R=255)
                Cv2.ArrowedLine(overlay, centerPt, yEnd, new Scalar(0, 255, 0), 2); // Vẽ mũi tên Trục Y màu xanh lá (Green: B=0, G=255, R=0)
            }

            if (_drawMatches.Value && inliersMask != null) // Nếu tham số bật vẽ Feature Matches = true và có mặt nạ Inlier
            {
                for (int i = 0; i < goodMatches.Count; i++) // Duyệt qua từng cặp điểm ghép
                {
                    if (inliersMask.At<byte>(i, 0) == 0) continue; // Bỏ qua nếu điểm đó bị RANSAC đánh dấu là nhiễu (Outlier)
                    var pt = kpScene[goodMatches[i].TrainIdx].Pt;  // Lấy tọa độ điểm đặc trưng tương ứng trên ảnh Scene
                    Cv2.Circle(overlay, (int)pt.X, (int)pt.Y, 3, new Scalar(255, 255, 0), -1); // Vẽ chấm tròn đặc màu xanh lơ (Cyan: B=255, G=255, R=0), bán kính 3px
                }
            }

            // Tạo chuỗi văn bản hiển thị các thông số đo đạc trên ảnh
            string label = $"Score={matchScore:F2} Theta={offset.Theta:F1}deg dX={offset.X:F1} dY={offset.Y:F1}";
            Cv2.PutText(overlay, label, new Point(10, 24), HersheyFonts.HersheySimplex, 0.6,
                judge == Judge.OK ? new Scalar(0, 255, 0) : new Scalar(0, 0, 255), 2); // In chữ màu Xanh lá nếu OK, màu Đỏ nếu NG tại tọa độ (10, 24)
        }
        else // Trường hợp thất bại: Không đủ điểm ghép hoặc RANSAC không thể ước lượng được ma trận vị trí
        {
            matchScore = 0.0; // Đặt điểm số = 0
            result = new AlignResult { Offset = default, MatchedCenter = default, Judge = Judge.NG }; // Trả về kết quả mặc định với phán định NG
            Cv2.PutText(overlay, "NOT FOUND", new Point(10, 24), HersheyFonts.HersheySimplex, 0.7, new Scalar(0, 0, 255), 2); // In chữ "NOT FOUND" màu đỏ lên ảnh
        }

        // Bước 7: Đẩy kết quả ra các cổng Output của Tool
        _outImage.Value = new MatVisionImage(overlay); // Xuất ảnh Overlay kèm đồ họa vẽ đè ra cổng 'Image'
        _outResult.Value = result;                     // Xuất cấu trúc AlignResult ra cổng 'Result'
        _outScore.Value = matchScore;                   // Xuất điểm số Score ra cổng 'Score'

        // Bước 8: Giải phóng tài nguyên bộ nhớ C++ của các ma trận OpenCV trung gian
        affine?.Dispose();       // Xả RAM ma trận Affine (nếu khác null)
        inliersMask?.Dispose();  // Xả RAM ma trận mặt nạ Inlier (nếu khác null)
        if (templateOwned) templateGray.Dispose(); // Xả RAM ma trận Template xám nếu do hàm này tự tạo
        if (srcGrayOwned) srcGray.Dispose();       // Xả RAM ma trận Scene xám nếu do hàm này tự tạo

        // Ghi log nhật ký hoạt động của Tool vào hệ thống
        context.Log($"PMAlignt: {goodMatches.Count} good match(es), Score={matchScore:F2}, Judge={result.Judge}.");
    }
}
*/