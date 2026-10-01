// ==================== Vai trò chính:                So khớp mẫu bất biến xoay + tỷ lệ bằng đặc trưng ORB, tìm được nhiều bản sao cùng lúc
// ==================== Thành phần / Class tiêu biểu: ORBTemplateMatchingTool
// ==================== Phụ thuộc vào:                OpenCvSharp (ORB, BFMatcher, FindHomography)
// ==================== Pattern / Kỹ thuật nổi bật:   Feature-based matching + RANSAC Homography + kỹ thuật
//                       "lột dần" (peel-off) để tìm nhiều instance - ĐÃ SỬA lỗi đánh dấu keypoint "đã dùng"
//                       quá sớm (xem ghi chú "SỬA LỖI" trong OnExecute)

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
using P2 = VisionFlow.Core.Models.Point2d;

namespace VisionFlow.Tools.Detection;

/// <summary>
/// So khớp mẫu bằng đặc trưng ORB (Oriented FAST and Rotated BRIEF) - khác hẳn TemplateMatchingNCC (dựa pixel)
/// và ContourTemplateMatching (dựa hướng gradient): ORB trích các "điểm đặc trưng" (góc, cạnh đặc biệt) rồi mô tả
/// bằng vector nhị phân, nên BẤT BIẾN với xoay VÀ tỷ lệ (scale) - mạnh hơn 2 tool kia khi vật thể vừa xoay vừa
/// đổi khoảng cách camera. Có thể tìm NHIỀU bản sao cùng lúc bằng kỹ thuật "lột dần" (peel-off):
/// mỗi vòng lặp tìm ra 1 bản sao thì đánh dấu các keypoint đã dùng để vòng lặp sau không tìm trùng lại.
/// Quy trình:
/// 1. Trích ORB keypoints + descriptors cho cả Template và ảnh Scene (chỉ 1 lần duy nhất).
/// 2. Lặp tối đa MaxInstances*3 lần, mỗi lần:
///    a. Ghép cặp đặc trưng Template vs các keypoint Scene CHƯA bị đánh dấu "đã dùng" (BFMatcher + Lowe's Ratio Test).
///    b. Nếu đủ MinMatchCount cặp ghép tốt -> chạy RANSAC ước lượng Homography.
///    c. Đếm inlier, tính MatchScore = inlier / goodMatches.
///    d. Biến đổi 4 góc Template qua Homography ra toạ độ Scene -> lọc theo Area/Aspect Ratio (Tab Validation).
///    e. Nếu hợp lệ và đủ xa các bản sao đã nhận trước đó (MinSeparationRatio) -> ghi nhận là 1 instance mới.
///    f. CHỈ đánh dấu các keypoint Scene là inlier là "đã dùng" khi bản sao đó ĐƯỢC CHẤP NHẬN hoàn toàn
///       (đã sửa lỗi so với bản trước: bản cũ đánh dấu "đã dùng" ngay sau RANSAC dù bản sao bị loại ở bước
///       Validation phía sau - khiến các keypoint đúng bị "đốt" oan, làm mất luôn cơ hội tìm lại object ở
///       vòng lặp kế tiếp trên các vật thể ít đặc trưng/gần ngưỡng MinMatchCount). Vòng lặp vẫn được chặn
///       trần an toàn bởi maxLoops, không lo lặp vô hạn dù không còn đánh dấu sớm.
/// 3. Vẽ overlay (khung đa giác + tâm + score, tuỳ chọn vẽ keypoints/matches debug) và xuất kết quả.
/// </summary>
[ToolMetadata("ORBTemplateMatching", DisplayName = "ORB Template Matching", Category = "Detection",
    Description = "Rotation & scale invariant multi-instance object detection using ORB features + RANSAC Homography.")]
public sealed class ORBTemplateMatchingTool : VisionTool
{
    #region 1. Ports
    private readonly InputPort<IVisionImage> _input;         // Ảnh nguồn (Scene) cần tìm kiếm
    private readonly InputPort<IVisionImage> _templateInput; // Ảnh mẫu (Template)

    private readonly OutputPort<IVisionImage> _outImage;                  // Ảnh overlay kết quả
    private readonly OutputPort<TemplateMatchingNCCResult> _outResult;    // Kết quả tổng hợp (dùng chung model)
    private readonly OutputPort<int> _outMatchesCount;                    // Tổng số bản sao tìm được
    #endregion

    #region 2. Parameters
    // --- Tab ORB Detection ---
    private readonly ToolParameter<int> _maxFeatures;      // Số lượng đặc trưng tối đa ORB trích xuất mỗi ảnh
    private readonly ToolParameter<double> _scaleFactor;   // Tỷ lệ giảm kích thước giữa các tầng pyramid
    private readonly ToolParameter<int> _nlevels;          // Số tầng pyramid
    private readonly ToolParameter<int> _edgeThreshold;    // Khoảng đệm gần biên ảnh không trích feature - LƯU Ý: nếu Template nhỏ hơn ~2xEdgeThreshold, ORB có thể không tìm được keypoint nào
    private readonly ToolParameter<int> _patchSize;        // Kích thước patch tính mô tả BRIEF
    private readonly ToolParameter<int> _fastThreshold;    // Ngưỡng tương phản tối thiểu của FAST detector

    // --- Tab Matching ---
    private readonly ToolParameter<double> _loweRatio;          // Ngưỡng Lowe's Ratio Test
    private readonly ToolParameter<int> _minMatchCount;         // Số cặp match tối thiểu để công nhận 1 bản sao
    private readonly ToolParameter<int> _maxInstances;          // Số bản sao tối đa tìm trong ảnh
    private readonly ToolParameter<double> _minSeparationRatio; // Tỷ lệ khoảng cách tối thiểu giữa 2 bản sao (theo chiều rộng Template)

    // --- Tab RANSAC ---
    private readonly ToolParameter<double> _ransacThreshold;      // Ngưỡng khoảng cách (px) để 1 điểm được tính là inlier
    private readonly ToolParameter<double> _ransacConfidence;     // Độ tin cậy mong muốn của RANSAC (0-1)
    private readonly ToolParameter<int> _ransacMaxIterations;     // Số vòng lặp RANSAC tối đa

    // --- Tab Validation ---
    private readonly ToolParameter<double> _minAreaRatio;    // Tỷ lệ diện tích tối thiểu (so với ảnh scene) để chấp nhận
    private readonly ToolParameter<double> _maxAreaRatio;    // Tỷ lệ diện tích tối đa
    private readonly ToolParameter<double> _minAspectRatio;  // Tỷ lệ width/height tối thiểu của bounding box kết quả
    private readonly ToolParameter<double> _maxAspectRatio;  // Tỷ lệ width/height tối đa

    // --- Tab Visualization ---
    private readonly ToolParameter<bool> _drawBoundingBoxes; // Vẽ khung đa giác + tâm + score cho từng bản sao
    private readonly ToolParameter<bool> _drawKeypoints;     // Vẽ toàn bộ keypoint ORB phát hiện được (debug)
    private readonly ToolParameter<bool> _drawMatches;       // Vẽ các điểm match đã dùng để tính từng bản sao (debug)
    #endregion

    public ORBTemplateMatchingTool()
    {
        _input = AddInput<IVisionImage>("Image", "Image (Source)");
        _templateInput = AddInput<IVisionImage>("Template", "Template Image");

        _outImage = AddOutput<IVisionImage>("Image", "Image (Result)");
        _outResult = AddOutput<TemplateMatchingNCCResult>("Result", "TemplateMatchingNCCOutputModel");
        _outMatchesCount = AddOutput<int>("MatchesCount", "Matches Count");

        _maxFeatures = AddParameter<int>("MaxFeatures", 5000, "Max Features", min: 100, max: 20000, category: "ORB Detection", order: 1);
        _scaleFactor = AddParameter<double>("ScaleFactor", 1.2, "Scale Factor", min: 1.01, max: 2.0, category: "ORB Detection", order: 2);
        _nlevels = AddParameter<int>("Nlevels", 8, "N Levels", min: 1, max: 16, category: "ORB Detection", order: 3);
        _edgeThreshold = AddParameter<int>("EdgeThreshold", 15, "Edge Threshold", min: 0, max: 100, category: "ORB Detection", order: 4);
        _patchSize = AddParameter<int>("PatchSize", 31, "Patch Size", min: 2, max: 100, category: "ORB Detection", order: 5);
        _fastThreshold = AddParameter<int>("FastThreshold", 10, "Fast Threshold", min: 1, max: 100, category: "ORB Detection", order: 6);

        _loweRatio = AddParameter<double>("LoweRatio", 0.8, "Lowe Ratio", min: 0.1, max: 1.0, category: "Matching", order: 1);
        _minMatchCount = AddParameter<int>("MinMatchCount", 8, "Min Match Count", min: 4, max: 500, category: "Matching", order: 2);
        _maxInstances = AddParameter<int>("MaxInstances", 10, "Max Instances", min: 1, max: 200, category: "Matching", order: 3);
        _minSeparationRatio = AddParameter<double>("MinSeparationRatio", 0.2, "Min Separation Ratio", min: 0.0, max: 2.0, category: "Matching", order: 4);

        _ransacThreshold = AddParameter<double>("RANSACThreshold", 8.0, "RANSAC Threshold", min: 0.1, max: 100.0, category: "RANSAC", order: 1);
        _ransacConfidence = AddParameter<double>("RANSACConfidence", 0.99, "RANSAC Confidence", min: 0.5, max: 0.999, category: "RANSAC", order: 2);
        _ransacMaxIterations = AddParameter<int>("RANSACMaxIterations", 2000, "RANSAC Max Iterations", min: 100, max: 20000, category: "RANSAC", order: 3);

        _minAreaRatio = AddParameter<double>("MinAreaRatio", 0.0005, "Min Area Ratio", min: 0.0, max: 1.0, category: "Validation", order: 1);
        _maxAreaRatio = AddParameter<double>("MaxAreaRatio", 0.95, "Max Area Ratio", min: 0.0, max: 1.0, category: "Validation", order: 2);
        _minAspectRatio = AddParameter<double>("MinAspectRatio", 0.1, "Min Aspect Ratio", min: 0.01, max: 100.0, category: "Validation", order: 3);
        _maxAspectRatio = AddParameter<double>("MaxAspectRatio", 10.0, "Max Aspect Ratio", min: 0.01, max: 100.0, category: "Validation", order: 4);

        _drawBoundingBoxes = AddParameter<bool>("DrawBoundingBoxes", true, "Draw Bounding Boxes", category: "Visualization", order: 1);
        _drawKeypoints = AddParameter<bool>("DrawKeypoints", false, "Draw Keypoints", category: "Visualization", order: 2);
        _drawMatches = AddParameter<bool>("DrawMatches", false, "Draw Matches", category: "Visualization", order: 3);
    }

    protected override void OnExecute(IToolContext context)
    {
        var stopwatch = Stopwatch.StartNew();

        Mat src = _input.Value!.AsMat();
        Mat template = _templateInput.Value!.AsMat();

        if (template.Width <= 0 || template.Height <= 0)
            throw new ToolExecutionException("ORBTemplateMatching: Template không hợp lệ.");

        Mat srcGray = ToGray(src, out bool srcOwned);
        Mat templateGray = ToGray(template, out bool templateOwned);

        // ----- Bước 1: Trích ORB keypoints + descriptors 1 LẦN DUY NHẤT cho cả Template và Scene -----
        using var orb = ORB.Create(
            _maxFeatures.Value, (float)_scaleFactor.Value, _nlevels.Value,
            _edgeThreshold.Value, firstLevel: 0, wtaK: 2, ORBScoreType.Harris,
            _patchSize.Value, _fastThreshold.Value);

        using Mat descTemplate = new Mat();
        orb.DetectAndCompute(templateGray, null, out KeyPoint[] kpTemplate, descTemplate);
        using Mat descScene = new Mat();
        orb.DetectAndCompute(srcGray, null, out KeyPoint[] kpScene, descScene);

        var matches = new List<TemplateMatchInstance>();
        var acceptedCenters = new List<P2>();
        var usedSceneIdx = new HashSet<int>(); // Các keypoint Scene đã "dùng" (thuộc 1 bản sao ĐÃ ĐƯỢC CHẤP NHẬN) - loại khỏi vòng tìm kiếm sau
        double templateW = templateGray.Width, templateH = templateGray.Height;
        double minSeparationPx = _minSeparationRatio.Value * templateW;
        double sceneArea = (double)srcGray.Width * srcGray.Height;

        Mat overlay = new Mat();
        Cv2.CvtColor(srcGray, overlay, ColorConversionCodes.GRAY2BGR);

        if (_drawKeypoints.Value)
            Cv2.DrawKeypoints(overlay, kpScene, overlay, new Scalar(120, 120, 120), DrawMatchesFlags.DrawRichKeypoints);

        bool canMatch = descTemplate.Rows > 0 && descScene.Rows > 0;

        // ----- SỬA LỖI (chẩn đoán): trước đây không có log nào khi ORB không tìm được keypoint nào -----
        // -> tool "thất bại trong im lặng" (MatchesCount=0 nhưng không rõ lý do). Thêm log tường minh:
        // nguyên nhân phổ biến nhất là Template quá nhỏ so với EdgeThreshold (ORB loại bỏ mọi keypoint
        // nằm trong dải EdgeThreshold pixel quanh biên ảnh), hoặc bề mặt vật thể quá trơn/ít góc cạnh.
        if (!canMatch)
        {
            context.Log($"ORBTemplateMatching: no ORB keypoints found (Template keypoints={descTemplate.Rows}, Scene keypoints={descScene.Rows}). " +
                        $"Check: Template size ({templateGray.Width}x{templateGray.Height}) vs EdgeThreshold ({_edgeThreshold.Value}) - " +
                        "if Template is smaller than ~2x EdgeThreshold, reduce EdgeThreshold or use a larger Template. " +
                        "Also check the object has enough corner-like features - ORB struggles on smooth/plain surfaces.");
        }

        int maxLoops = _maxInstances.Value * 3; // Chặn trần số vòng lặp để tránh lặp vô hạn khi ảnh còn nhiều keypoint nhưng không đủ inlier

        for (int loop = 0; canMatch && loop < maxLoops && matches.Count < _maxInstances.Value; loop++)
        {
            // ----- Bước 2a: Ghép cặp đặc trưng bằng BFMatcher (Hamming - phù hợp mô tả nhị phân ORB) + Lowe's Ratio Test -----
            using var matcher = new BFMatcher(NormTypes.Hamming);
            DMatch[][] knn = matcher.KnnMatch(descTemplate, descScene, 2);

            var goodMatches = knn
                .Where(m => m.Length == 2 && m[0].Distance < _loweRatio.Value * m[1].Distance)
                .Select(m => m[0])
                .Where(m => !usedSceneIdx.Contains(m.TrainIdx)) // Bỏ qua keypoint Scene đã "dùng" ở các bản sao ĐÃ CHẤP NHẬN trước đó
                .ToList();

            if (goodMatches.Count < _minMatchCount.Value) break; // Không còn đủ match tốt -> hết bản sao để tìm, dừng vòng lặp

            // ----- Bước 2b: RANSAC ước lượng Homography (cho phép cả xoay, scale, méo phối cảnh nhẹ) -----
            Point2f[] srcPts = goodMatches.Select(m => kpTemplate[m.QueryIdx].Pt).ToArray();
            Point2f[] dstPts = goodMatches.Select(m => kpScene[m.TrainIdx].Pt).ToArray();

            using Mat mask = new Mat();
            using Mat homography = Cv2.FindHomography(
                InputArray.Create(srcPts), InputArray.Create(dstPts),
                HomographyMethods.Ransac, _ransacThreshold.Value, mask,
                _ransacMaxIterations.Value, _ransacConfidence.Value);

            if (homography.Empty())
            {
                // SỬA LỖI: trước đây có đánh dấu usedSceneIdx cho các match đang xét ở đây, khiến các keypoint
                // hợp lệ bị loại vĩnh viễn dù bản sao chưa hề được xác nhận. Giờ để nguyên, không đánh dấu gì -
                // maxLoops vẫn đảm bảo vòng lặp không chạy vô hạn nếu ảnh liên tục cho homography rỗng.
                continue;
            }

            // SỬA LỖI: chỉ THU THẬP danh sách inlier vào biến tạm, CHƯA ghi vào usedSceneIdx ở đây.
            // Việc ghi thật sự chỉ xảy ra SAU KHI bản sao vượt qua toàn bộ Validation phía dưới (xem cuối vòng lặp).
            var inlierTrainIdx = new List<int>();
            int inlierCount = 0;
            int maskRows = mask.Rows;
            for (int i = 0; i < maskRows; i++)
                if (mask.At<byte>(i, 0) != 0) { inlierCount++; inlierTrainIdx.Add(goodMatches[i].TrainIdx); }

            double matchScore = goodMatches.Count > 0 ? (double)inlierCount / goodMatches.Count : 0.0;

            if (inlierCount < _minMatchCount.Value)
                continue; // Quá ít inlier -> không đủ tin cậy. Các keypoint này KHÔNG bị đánh dấu used, vẫn còn cơ hội dùng lại ở vòng lặp sau với tổ hợp match khác

            // ----- Bước 2d: Biến đổi 4 góc Template qua Homography ra toạ độ Scene -----
            Point2f[] templateCorners = { new Point2f(0, 0), new Point2f((float)templateW, 0), new Point2f((float)templateW, (float)templateH), new Point2f(0, (float)templateH) };
            Point2f[] sceneCorners = Cv2.PerspectiveTransform(templateCorners, homography);

            var cvPoly = sceneCorners.Select(p => new Point((int)p.X, (int)p.Y)).ToArray();
            if (!Cv2.IsContourConvex(cvPoly)) continue; // Homography méo tạo tứ giác lõm/tự cắt -> chắc chắn là kết quả sai, loại bỏ

            double area = Math.Abs(Cv2.ContourArea(cvPoly));
            double areaRatio = sceneArea > 0 ? area / sceneArea : 0.0;
            if (areaRatio < _minAreaRatio.Value || areaRatio > _maxAreaRatio.Value) continue; // Lọc theo Tab Validation

            Rect bbox = Cv2.BoundingRect(cvPoly);
            double aspect = bbox.Height > 0 ? (double)bbox.Width / bbox.Height : 0.0;
            if (aspect < _minAspectRatio.Value || aspect > _maxAspectRatio.Value) continue; // Lọc theo Tab Validation

            P2 center = new P2(sceneCorners.Average(p => p.X), sceneCorners.Average(p => p.Y));

            // ----- Bước 2e: Kiểm tra khoảng cách tối thiểu với các bản sao đã nhận trước đó, tránh đếm trùng 1 vật -----
            bool tooCloseToExisting = acceptedCenters.Any(c => Distance(c.X, c.Y, center.X, center.Y) < minSeparationPx);
            if (tooCloseToExisting) continue;

            // Góc xoay xấp xỉ: lấy hướng vector cạnh trên (RightTop - LeftTop) của tứ giác kết quả
            double angleDeg = Math.Atan2(sceneCorners[1].Y - sceneCorners[0].Y, sceneCorners[1].X - sceneCorners[0].X) * 180.0 / Math.PI;

            var lt = new P2(sceneCorners[0].X, sceneCorners[0].Y);
            var rt = new P2(sceneCorners[1].X, sceneCorners[1].Y);
            var rb = new P2(sceneCorners[2].X, sceneCorners[2].Y);
            var lb = new P2(sceneCorners[3].X, sceneCorners[3].Y);

            // ----- Bản sao ĐƯỢC CHẤP NHẬN hoàn toàn - CHỈ TỪ ĐÂY mới đánh dấu các keypoint là "đã dùng" -----
            matches.Add(new TemplateMatchInstance(lt, rt, rb, lb, center, matchScore, angleDeg, matches.Count));
            acceptedCenters.Add(center);
            foreach (var idx in inlierTrainIdx) usedSceneIdx.Add(idx); // SỬA LỖI: chuyển xuống đây (trước đây nằm ngay sau RANSAC, phía trên)

            if (_drawMatches.Value)
            {
                // Đơn giản hoá: đánh dấu các điểm Scene đã dùng làm inlier cho bản sao này (không vẽ ghép nối 2 ảnh cạnh nhau
                // để giữ nguyên kích thước ảnh Output, khác với Cv2.DrawMatches chuẩn cần ảnh đôi).
                Scalar dbgColor = PickDistinctColor(matches.Count - 1);
                int maskRows1 = mask.Rows;
                for (int i = 0; i < maskRows1; i++)
                    if (mask.At<byte>(i, 0) != 0)
                        Cv2.Circle(overlay, (int)dstPts[i].X, (int)dstPts[i].Y, 2, dbgColor, -1, LineTypes.AntiAlias);
            }
        }

        // ----- Bước 3: Vẽ khung đa giác + tâm + score cho từng bản sao -----
        if (_drawBoundingBoxes.Value)
        {
            for (int i = 0; i < matches.Count; i++)
            {
                var m = matches[i];
                Scalar color = PickDistinctColor(i);
                var pts = new[]
                {
                    new Point((int)m.LeftTop.X, (int)m.LeftTop.Y),
                    new Point((int)m.RightTop.X, (int)m.RightTop.Y),
                    new Point((int)m.RightBottom.X, (int)m.RightBottom.Y),
                    new Point((int)m.LeftBottom.X, (int)m.LeftBottom.Y),
                };
                Cv2.Polylines(overlay, new[] { pts }, true, color, 2, LineTypes.AntiAlias);
                Cv2.Circle(overlay, (int)m.Center.X, (int)m.Center.Y, 3, color, -1, LineTypes.AntiAlias);
                Cv2.PutText(overlay, $"#{i} {m.MatchScore:F2} {m.MatchedAngle:F0}deg",
                    new Point((int)m.LeftTop.X, (int)m.LeftTop.Y - 6),
                    HersheyFonts.HersheySimplex, 0.45, color, 1, LineTypes.AntiAlias);
            }
        }
        else if (matches.Count > 0)
        {
            // SỬA LỖI (chẩn đoán): tránh trường hợp người dùng tưởng thuật toán thất bại trong khi thực ra
            // chỉ là DrawBoundingBoxes đang tắt - MatchesCount vẫn > 0 nhưng ảnh không hiện khung nào.
            context.Log($"ORBTemplateMatching: found {matches.Count} match(es) but DrawBoundingBoxes is OFF - enable it in the Visualization tab to see the boxes.");
        }

        stopwatch.Stop();

        var result = new TemplateMatchingNCCResult
        {
            Matches = matches,
            Success = matches.Count > 0,
            ExecutionTimeMs = stopwatch.Elapsed.TotalMilliseconds,
            Judge = matches.Count > 0 ? Judge.OK : Judge.NG,
        };

        // ----- Bước 4: Xuất kết quả -----
        // overlay "cho đi" thẳng vào Output -> KHÔNG Dispose(overlay) sau đây (bài học từ HoughCircleDetectionTool)
        _outImage.Value = new MatVisionImage(overlay);
        _outResult.Value = result;
        _outMatchesCount.Value = matches.Count;

        // ----- Bước 5: Dọn dẹp tài nguyên Mat trung gian -----
        if (templateOwned) templateGray.Dispose();
        if (srcOwned) srcGray.Dispose();

        context.Log($"ORBTemplateMatching: {matches.Count} instance(s) found in {stopwatch.Elapsed.TotalMilliseconds:F1}ms.");
    }

    #region 3. Helpers

    private static Mat ToGray(Mat src, out bool owned)
    {
        if (src.Channels() == 1) { owned = false; return src; }
        Mat gray = new Mat();
        Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
        owned = true;
        return gray;
    }

    private static double Distance(double x1, double y1, double x2, double y2) => Math.Sqrt((x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2));

    private static Scalar PickDistinctColor(int index)
    {
        Scalar[] palette =
        {
            new Scalar(0, 255, 255), new Scalar(0, 255, 0), new Scalar(255, 0, 0),
            new Scalar(0, 165, 255), new Scalar(255, 0, 255), new Scalar(255, 255, 0),
        };
        return palette[index % palette.Length];
    }

    #endregion
}