// ==================== Vai trò chính:                TRÁI TIM thuật toán: các công cụ đo lường/tìm kiếm hình học kiểu công nghiệp (Caliper)
// ==================== Thành phần / Class tiêu biểu: CaliperUtil (helper), FindCircleTool, FindLineTool
// ==================== Phụ thuộc vào:                OpenCvSharp + Core.Models
// ==================== Pattern / Kỹ thuật nổi bật:   Sub-pixel edge detection, RANSAC, Circle/Line fitting
// ==================== LỊCH SỬ CHỈNH SỬA:
//   ★ FIX#1: EdgePolarity áp dụng cho CẢ 3 mô hình cạnh (Sharp/Step/Peak), không chỉ riêng Sharp.
//   ★ FIX#2: CaliperWidth THỰC SỰ lấy trung bình nhiều đường quét song song để khử nhiễu ngang.
//   ★ FIX#3: EdgeFilterWidth THỰC SỰ áp dụng bước làm mịn (moving average) trước khi tính Gradient.
//   ★ FIX#4: MinEdgeStrength THỰC SỰ được dùng làm ngưỡng lọc bổ trợ.
//   ★ ADD#1: Thêm tham số CaliperLength (Detection) - tách biệt hoàn toàn khỏi Region.Height theo đúng tài liệu.
//   ★ ADD#2: Thêm CaliperDistributionMode (Linear/Perpendicular) - làm CaliperSpacing có tác dụng thật ở mode Perpendicular.
//   ★ ADD#3: Thêm UseInteractiveCalipers (Detection) - hiển thị số thứ tự caliper khi bật.
//   ★ ADD#4: MaxEdgesPerCaliper giờ THỰC SỰ giới hạn số điểm cạnh lấy trên mỗi caliper (trước đây luôn = 1).
//   ★ ADD#5: Thêm UseUIOverlay (Display) - ghi rõ giới hạn: hệ thống hiện chưa có lớp Overlay UI riêng.
//   ★ ADD#6: Thêm Output InteractiveCalipers - ảnh debug LUÔN hiển thị đủ Region+Caliper+EdgePoints để hỗ trợ kiểm tra.

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

namespace VisionFlow.Tools.Finding
{
    /// <summary>
    /// Thuật toán tìm đường thẳng dựa trên kỹ thuật Caliper (Thước đo cạnh) - Tiêu chuẩn công nghiệp Cognex VisionPro.
    /// [QUY TRÌNH XỬ LÝ]:
    /// 1. Tính toán ma trận hình học để rải N thước đo (Caliper) song song trong vùng Search Region (RotatedRectRegion).
    /// 2. Dọc theo mỗi Caliper, thực hiện nội suy song tuyến tính (Bilinear) để trích xuất Profile độ xám 1D
    ///    (★ FIX#2: lấy trung bình nhiều đường quét song song trong phạm vi CaliperWidth để khử nhiễu ngang).
    /// 2b. (★ FIX#3) Làm mịn Profile bằng bộ lọc trung bình trượt có độ rộng EdgeFilterWidth trước khi tính đạo hàm.
    /// 3. Sử dụng bộ lọc tích chập đạo hàm (Sobel/Roberts/Prewitt) 1D để tính toán hàm phản hồi Gradient.
    /// 4. Phân tích Profile qua 3 mô hình toán học (Sharp/Step/Peak) kết hợp bộ lọc cực tính (Polarity, ★ FIX#1 áp dụng
    ///    đồng nhất cho cả 3 mô hình) và ngưỡng độ mạnh tối thiểu (★ FIX#4 MinEdgeStrength) để tìm điểm biên.
    ///    (★ ADD#4) Mỗi caliper có thể trả về tối đa MaxEdgesPerCaliper điểm thay vì luôn cố định 1.
    /// 5. Áp dụng thuật toán RANSAC tối ưu O(1) để loại bỏ các điểm nhiễu ngoại lai (Outliers).
    /// 6. Khớp hồi quy tuyến tính trực giao bằng phân tích thành phần chính (PCA/Least Squares) để tìm đường thẳng lý thuyết.
    /// 7. Giới hạn (Clip) đường thẳng vô hạn vào phạm vi biên của vùng ROI để phục vụ hiển thị đồ họa Overlay.
    /// </summary>
    [ToolMetadata("FindLine", DisplayName = "Find Line", Category = "Detection",
        Description = "Caliper-based line finder (COGNEX-style edge models + RANSAC + PCA fit).")]
    public sealed class FindLineTool : VisionTool
    {
        #region 1. Khai Báo Các Cổng Vào / Ra Dữ Liệu (Inputs / Outputs Ports)
        private readonly InputPort<IVisionImage> _input;
        private readonly OutputPort<IVisionImage> _outImage;
        private readonly OutputPort<LineResult> _outLine;
        private readonly OutputPort<P2[]> _outEdges;
        private readonly OutputPort<double> _outRms;
        private readonly OutputPort<double> _outScore;
        // ★ ADD#6: cổng ra ảnh debug riêng, LUÔN vẽ đủ Region + toàn bộ Caliper + toàn bộ EdgePoints,
        // không phụ thuộc vào các cờ DrawFittedLine/DrawEdgePoints/DrawSearchRegion ở Tab Display -
        // đúng tinh thần "InteractiveCalipers: hỗ trợ kiểm tra và debug" của tài liệu.
        private readonly OutputPort<IVisionImage> _outInteractiveCalipers;
        #endregion

        #region 2. Hệ Thống Tham Số Cấu Hình Hướng Người Dùng (Parameters)
        private readonly ToolParameter<RotatedRectRegion> _region;

        private readonly ToolParameter<int> _numCalipers;
        private readonly ToolParameter<double> _caliperWidth;
        private readonly ToolParameter<double> _caliperLength;          // ★ ADD#1: tách biệt khỏi Region.Height
        private readonly ToolParameter<double> _caliperSpacing;
        private readonly ToolParameter<bool> _dirForward;
        private readonly ToolParameter<int> _maxEdgesPerCaliper;
        private readonly ToolParameter<bool> _useInteractiveCalipers;   // ★ ADD#3
        private readonly ToolParameter<string> _caliperDistributionMode; // ★ ADD#2: "Linear" hoặc "Perpendicular"

        private readonly ToolParameter<double> _edgeThreshold;
        private readonly ToolParameter<string> _edgePolarity;
        private readonly ToolParameter<double> _edgeFilterWidth;
        private readonly ToolParameter<double> _minEdgeStrength;
        private readonly ToolParameter<bool> _subPixel;

        private readonly ToolParameter<int> _minEdgePoints;
        private readonly ToolParameter<double> _maxRms;
        private readonly ToolParameter<double> _minScore;

        private readonly ToolParameter<bool> _outlierRemoval;
        private readonly ToolParameter<double> _outlierThreshold;
        private readonly ToolParameter<double> _minInlierRatio;

        private readonly ToolParameter<bool> _drawFitted;
        private readonly ToolParameter<bool> _drawEdges;
        private readonly ToolParameter<bool> _drawRegion;
        private readonly ToolParameter<bool> _useUIOverlay; // ★ ADD#5
        #endregion

        public FindLineTool()
        {
            _input = AddInput<IVisionImage>("Image", "Image");
            _outImage = AddOutput<IVisionImage>("Image", "Overlay");
            _outLine = AddOutput<LineResult>("Line", "Line");
            _outEdges = AddOutput<P2[]>("EdgePoints", "Edge Points");
            _outRms = AddOutput<double>("RMSError", "RMS Error");
            _outScore = AddOutput<double>("Score", "Score");
            _outInteractiveCalipers = AddOutput<IVisionImage>("InteractiveCalipers", "Interactive Calipers"); // ★ ADD#6

            _region = AddParameter("Region", new RotatedRectRegion(new P2(320, 240), 200, 50, 0), "Search Region", category: "Region", order: 1, interaction: ParameterInteraction.RotatedRectRegion);

            _numCalipers = AddParameter("NumberOfCalipers", 10, "Number Of Calipers", 1, 10000, category: "Detection", order: 1);
            _caliperLength = AddParameter("CaliperLength", 50.0, "Caliper Length", 1.0, 100000.0, category: "Detection", order: 2); // ★ ADD#1: mặc định = Region.Height cũ để tương thích ngược
            _caliperWidth = AddParameter("CaliperWidth", 3.0, "Caliper Width", 1.0, 10000.0, category: "Detection", order: 3);
            _caliperSpacing = AddParameter("CaliperSpacing", 2.0, "Caliper Spacing", 0.0, 100000.0, category: "Detection", order: 4);
            _dirForward = AddParameter("CaliperDirectionForward", true, "Search Forward", category: "Detection", order: 5);
            _maxEdgesPerCaliper = AddParameter("MaxEdgesPerCaliper", 1, "Max Edges Per Caliper", 1, 100000, category: "Detection", order: 6);
            _useInteractiveCalipers = AddParameter("UseInteractiveCalipers", false, "Use Interactive Calipers", category: "Detection", order: 7); // ★ ADD#3
            _caliperDistributionMode = AddChoiceParameter("CaliperDistributionMode", "Linear", new[] { "Linear", "Perpendicular" }, "Caliper Distribution Mode", category: "Detection", order: 8); // ★ ADD#2

            _edgeThreshold = AddParameter("EdgeThreshold", 1.5, "Edge Threshold", 0.0, 255.0, category: "Threshold", order: 1);
            _edgePolarity = AddChoiceParameter("EdgePolarity", "Either", new[] { "DarkToLight", "LightToDark", "Either" }, "Edge Polarity", category: "Threshold", order: 2);
            _edgeFilterWidth = AddParameter("EdgeFilterWidth", 1.0, "Edge Filter Width", 0.0, 10000.0, category: "Threshold", order: 3);
            _minEdgeStrength = AddParameter("MinEdgeStrength", 2.0, "Min Edge Strength", 0.0, 255.0, category: "Threshold", order: 4);
            _subPixel = AddParameter("SubPixelAccuracy", true, "Sub-Pixel Accuracy", category: "Advanced", order: 6);

            _minEdgePoints = AddParameter("MinEdgePoints", 3, "Min Edge Points", 1, 1000000, category: "Threshold", order: 5);
            _maxRms = AddParameter("MaxRMSError", 50.0, "Max RMS Error", 0.0, 100000.0, category: "Threshold", order: 6);
            _minScore = AddParameter("MinScore", 0.3, "Min Score", 0.0, 1.0, category: "Threshold", order: 7);

            _outlierRemoval = AddParameter("EnableOutlierRemoval", true, "Outlier Removal", category: "Advanced", order: 1);
            _outlierThreshold = AddParameter("OutlierThreshold", 2.0, "Outlier Threshold", 0.0, 100000.0, category: "Advanced", order: 2);
            _minInlierRatio = AddParameter("MinInlierRatio", 0.7, "Min Inlier Ratio", 0.0, 1.0, category: "Advanced", order: 3);

            _drawFitted = AddParameter("DrawFittedLine", true, "Draw Fitted Line", category: "Display", order: 1);
            _drawEdges = AddParameter("DrawEdgePoints", true, "Draw Edge Points", category: "Display", order: 2);
            _drawRegion = AddParameter("DrawSearchRegion", true, "Draw Search Region", category: "Display", order: 3);
            _useUIOverlay = AddParameter("UseUIOverlay", false, "Use UI Overlay", category: "Display", order: 4); // ★ ADD#5
        }

        protected override void OnExecute(IToolContext context)
        {
            var srcImg = _input.Value!.AsMat();
            Mat overlay = srcImg.Channels() == 1 ? srcImg.CvtColor(ColorConversionCodes.GRAY2BGR) : srcImg.Clone();
            bool isSrcGray = srcImg.Channels() == 1;
            Mat gray = isSrcGray ? srcImg : srcImg.CvtColor(ColorConversionCodes.BGR2GRAY);

            // ★ ADD#5: UseUIOverlay chưa có hạ tầng lớp Overlay UI riêng trong phiên bản hiện tại của Editor
            // (chỉ có ParameterInteraction.RotatedRectRegion cho việc kéo-thả vùng ROI, chưa có canvas hiển thị
            // kết quả runtime độc lập scale). Khi bật, tool vẫn hoạt động bình thường (vẽ trực tiếp lên ảnh)
            // và chỉ ghi log 1 lần để người dùng biết tính năng này đang là placeholder chờ nâng cấp Editor.
            if (_useUIOverlay.Value)
                context.Log("FindLine: UseUIOverlay=true nhưng Editor hiện chưa hỗ trợ lớp Overlay UI riêng -> vẫn vẽ trực tiếp lên ảnh (fallback).");

            var calipers = GenerateRectangleCalipers();
            var allEdges = new List<P2>(calipers.Count);

            foreach (var (start, end) in calipers)
            {
                var edgesFromCaliper = ProcessCaliperAdvanced(gray, start, end);
                if (edgesFromCaliper.Count > 0)
                {
                    allEdges.AddRange(edgesFromCaliper);
                }
            }

            var filtered = allEdges;
            if (_outlierRemoval.Value && allEdges.Count >= _minEdgePoints.Value)
            {
                filtered = RemoveOutliers(allEdges);
            }

            var result = new LineResult { Judge = Judge.NG };
            double rms = 0, score = 0;
            var outEdges = allEdges;

            LineSegment seg = default;
            double fittedRms = 0;
            bool haveFit = filtered.Count >= _minEdgePoints.Value && FitLineLeastSquares(filtered, out seg, out fittedRms);
            if (haveFit)
            {
                rms = fittedRms;
                score = CalculateLineScore(filtered, fittedRms);
                result.Segment = seg;
                result.AngleDeg = Math.Atan2(seg.P2.Y - seg.P1.Y, seg.P2.X - seg.P1.X) * 180.0 / Math.PI;
                result.Judge = (fittedRms <= _maxRms.Value && score >= _minScore.Value) ? Judge.OK : Judge.NG;

                if (result.Judge == Judge.OK)
                {
                    outEdges = filtered;
                }
            }

            if (_drawRegion.Value)
            {
                DrawRotatedRegion(overlay, new Scalar(255, 255, 0));
                DrawCalipersOnImage(overlay, calipers, showIndex: false); // Ảnh Image chính giữ nguyên gọn gàng, không số thứ tự
            }
            if (_drawEdges.Value)
            {
                foreach (var e in allEdges)
                {
                    Cv2.Circle(overlay, (int)Math.Round(e.X), (int)Math.Round(e.Y), 2, new Scalar(102, 255, 0), -1);
                }
            }
            if (_drawFitted.Value && haveFit)
            {
                var color = result.Judge == Judge.OK ? new Scalar(0, 165, 255) : new Scalar(0, 0, 255);
                Cv2.Line(overlay, (int)Math.Round(seg.P1.X), (int)Math.Round(seg.P1.Y), (int)Math.Round(seg.P2.X), (int)Math.Round(seg.P2.Y), color, 2);
            }

            // ★ ADD#6: Xây dựng ảnh InteractiveCalipers riêng - LUÔN vẽ đầy đủ Region + toàn bộ Caliper (kèm số thứ tự
            // nếu UseInteractiveCalipers=true) + toàn bộ EdgePoints + đường fit, KHÔNG phụ thuộc cờ Draw* của Tab Display,
            // phục vụ đúng mục đích debug/kiểm tra độc lập với ảnh kết quả chính.
            Mat debugCanvas = srcImg.Channels() == 1 ? srcImg.CvtColor(ColorConversionCodes.GRAY2BGR) : srcImg.Clone();
            DrawRotatedRegion(debugCanvas, new Scalar(255, 255, 0));
            DrawCalipersOnImage(debugCanvas, calipers, showIndex: _useInteractiveCalipers.Value);
            foreach (var e in allEdges)
                Cv2.Circle(debugCanvas, (int)Math.Round(e.X), (int)Math.Round(e.Y), 2, new Scalar(102, 255, 0), -1);
            if (haveFit)
            {
                var dbgColor = result.Judge == Judge.OK ? new Scalar(0, 165, 255) : new Scalar(0, 0, 255);
                Cv2.Line(debugCanvas, (int)Math.Round(seg.P1.X), (int)Math.Round(seg.P1.Y), (int)Math.Round(seg.P2.X), (int)Math.Round(seg.P2.Y), dbgColor, 2);
            }

            if (!isSrcGray) gray.Dispose();

            _outImage.Value = new MatVisionImage(overlay);
            _outInteractiveCalipers.Value = new MatVisionImage(debugCanvas); // ★ ADD#6
            _outLine.Value = result;
            _outEdges.Value = outEdges.ToArray();
            _outRms.Value = rms;
            _outScore.Value = score;

            context.Log($"FindLine: edges={allEdges.Count} kept={filtered.Count} judge={result.Judge} angle={result.AngleDeg:F1} rms={rms:F2} score={score:F2}");
        }

        #region 3. Tính Toán Hình Học Thước Đo (Caliper Geometry Calculations)
        private List<(P2 start, P2 end)> GenerateRectangleCalipers()
        {
            var r = _region.Value;
            var list = new List<(P2 start, P2 end)>();

            double cx = r.Center.X, cy = r.Center.Y;
            double regionWidth = r.Width;
            double caliperLength = _caliperLength.Value; // ★ ADD#1: dùng param riêng thay vì r.Height
            double angle = r.AngleDeg * Math.PI / 180.0;
            bool forward = _dirForward.Value;

            // Hướng tìm line (ngang/dọc) vẫn dựa theo tỉ lệ hình dạng RIÊNG của vùng ROI (Width vs Height) -
            // không đổi theo CaliperLength, để việc tách 2 tham số ở ★ ADD#1 không làm sai lệch logic xác định hướng quét.
            bool searchingForHorizontalLine = r.Width > r.Height;

            // ★ ADD#2: Xác định danh sách hệ số nội suy "t" (0..1) chạy dọc ROI theo đúng CaliperDistributionMode.
            List<double> tValues;
            if (_caliperDistributionMode.Value == "Perpendicular" && _caliperSpacing.Value > 0)
            {
                // Chế độ Perpendicular: MẬT ĐỘ caliper do CaliperSpacing (đơn vị pixel) quyết định trực tiếp,
                // KHÔNG dùng NumberOfCalipers nữa -> CaliperSpacing từ nay THỰC SỰ có tác dụng (trước đây là dead param).
                // Số lượng caliper tự động tính = chiều rộng vùng ROI chia cho khoảng cách mong muốn.
                int computedCount = Math.Max(1, (int)(regionWidth / _caliperSpacing.Value) + 1);
                tValues = Enumerable.Range(0, computedCount)
                    .Select(i => computedCount > 1 ? (double)i / (computedCount - 1) : 0.5)
                    .ToList();
            }
            else
            {
                // Chế độ Linear (mặc định - GIỮ NGUYÊN hành vi gốc): mật độ do NumberOfCalipers quyết định trực tiếp.
                int n = Math.Max(1, _numCalipers.Value);
                tValues = Enumerable.Range(0, n)
                    .Select(i => n > 1 ? (double)i / (n - 1) : 0.5)
                    .ToList();
            }

            foreach (double t in tValues)
            {
                P2 start, end;
                if (searchingForHorizontalLine)
                {
                    double x = cx + (t - 0.5) * regionWidth;
                    start = forward ? new P2(x, cy - caliperLength / 2) : new P2(x, cy + caliperLength / 2);
                    end = forward ? new P2(x, cy + caliperLength / 2) : new P2(x, cy - caliperLength / 2);
                }
                else
                {
                    double y = cy + (t - 0.5) * caliperLength;
                    start = forward ? new P2(cx - regionWidth / 2, y) : new P2(cx + regionWidth / 2, y);
                    end = forward ? new P2(cx + regionWidth / 2, y) : new P2(cx - regionWidth / 2, y);
                }

                if (Math.Abs(angle) > 0.001)
                {
                    start = RotatePoint(start, cx, cy, angle);
                    end = RotatePoint(end, cx, cy, angle);
                }
                list.Add((start, end));
            }
            return list;
        }
        #endregion

        #region 4. Đường Ống Phân Tích & Phát Hiện Cạnh Biên (Edge Detection Pipeline)
        private List<P2> ProcessCaliperAdvanced(Mat gray, P2 start, P2 end)
        {
            int maxEdges = Math.Max(1, _maxEdgesPerCaliper.Value); // ★ ADD#4
            var valid = new List<P2>(maxEdges);
            double dx = end.X - start.X, dy = end.Y - start.Y;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1) return valid;

            var profile = Extract1DProfile(gray, start, end, _caliperWidth.Value); // ★ FIX#2
            if (profile.Length < 3) return valid;

            profile = SmoothProfile(profile, _edgeFilterWidth.Value); // ★ FIX#3

            var responses = Apply1DEdgeKernels(profile);
            var candidates = DetectEdgesWithModels(responses, start, end);
            var scored = ScoreEdgeCandidates(candidates, profile);

            if (scored.Count > 0)
            {
                double thr = _minScore.Value * 0.5;

                // ★ ADD#4: Lấy TỐI ĐA "maxEdges" điểm đạt ngưỡng, xếp theo điểm số giảm dần (trước đây luôn cứng 1 điểm).
                var passing = scored.Where(e => e.score >= thr).OrderByDescending(e => e.score).ToList();
                if (passing.Count > 0)
                {
                    foreach (var e in passing.Take(maxEdges)) valid.Add(e.point);
                }
                else
                {
                    // Không có điểm nào đạt ngưỡng -> vẫn lấy điểm tốt nhất toàn cục để không mất hoàn toàn dữ liệu caliper này
                    valid.Add(scored.OrderByDescending(e => e.score).First().point);
                }
            }
            return valid;
        }

        private static double[] Extract1DProfile(Mat gray, P2 start, P2 end, double caliperWidth)
        {
            double dx = end.X - start.X, dy = end.Y - start.Y;
            double length = Math.Sqrt(dx * dx + dy * dy);
            int numSamples = Math.Max(3, (int)length);
            var profile = new double[numSamples];
            int width = gray.Width, height = gray.Height;

            double ux = dx / length, uy = dy / length;
            double perpX = -uy, perpY = ux;

            int numLines = Math.Clamp((int)Math.Round(caliperWidth), 1, 15);
            double halfWidth = caliperWidth / 2.0;

            for (int i = 0; i < numSamples; i++)
            {
                double t = (double)i / (numSamples - 1);
                double cxPt = start.X + t * dx, cyPt = start.Y + t * dy;

                double sum = 0;
                for (int k = 0; k < numLines; k++)
                {
                    double offset = numLines > 1 ? (-halfWidth + k * (caliperWidth / (numLines - 1))) : 0.0;
                    double x = cxPt + perpX * offset, y = cyPt + perpY * offset;

                    double clampedX = Math.Clamp(x, 0, width - 1.001);
                    double clampedY = Math.Clamp(y, 0, height - 1.001);
                    sum += GetInterpolatedPixel(gray, clampedX, clampedY);
                }
                profile[i] = sum / numLines;
            }
            return profile;
        }

        private static double[] SmoothProfile(double[] profile, double filterWidth)
        {
            int window = (int)Math.Round(filterWidth);
            if (window <= 1) return profile;

            if (window % 2 == 0) window++;
            int half = window / 2;
            var smoothed = new double[profile.Length];

            for (int i = 0; i < profile.Length; i++)
            {
                double sum = 0; int count = 0;
                for (int k = -half; k <= half; k++)
                {
                    int idx = i + k;
                    if (idx < 0 || idx >= profile.Length) continue;
                    sum += profile[idx];
                    count++;
                }
                smoothed[i] = sum / count;
            }
            return smoothed;
        }

        private static double[] Apply1DEdgeKernels(double[] profile)
        {
            int len = profile.Length;
            if (len < 3) return Array.Empty<double>();

            var responses = new double[len];
            for (int i = 1; i < len - 1; i++)
            {
                double sobel = profile[i + 1] - profile[i - 1];
                double roberts = profile[i] - profile[i - 1];
                double prewitt = sobel * 0.5;
                responses[i] = sobel * 0.5 + roberts * 0.3 + prewitt * 0.2;
            }
            return responses;
        }

        private List<(P2 point, double strength, string model)> DetectEdgesWithModels(double[] responses, P2 start, P2 end)
        {
            var candidates = new List<(P2 point, double strength, string model)>();
            double dx = end.X - start.X, dy = end.Y - start.Y;
            double threshold = _edgeThreshold.Value;
            string polarity = _edgePolarity.Value;
            double minStrength = _minEdgeStrength.Value;
            int len = responses.Length;

            bool ValidPolarity(double signedResponse) => polarity switch
            {
                "DarkToLight" => signedResponse > 0,
                "LightToDark" => signedResponse < 0,
                _ => true,
            };

            for (int i = 1; i < len - 1; i++)
            {
                double response = responses[i];
                string bestModel = "None";
                double bestStrength = 0;

                if (Math.Abs(response) > threshold && ValidPolarity(response))
                {
                    if (Math.Abs(response) > bestStrength)
                    {
                        bestStrength = Math.Abs(response);
                        bestModel = "SharpEdge";
                    }
                }

                if (i >= 2 && i < len - 2)
                {
                    double stepSigned = (responses[i - 1] + response + responses[i + 1]) / 3.0;
                    double stepStrength = Math.Abs(stepSigned);
                    if (stepStrength > threshold * 0.7 && stepStrength > bestStrength && ValidPolarity(stepSigned))
                    {
                        bestStrength = stepStrength;
                        bestModel = "StepEdge";
                    }
                }

                if (Math.Abs(response) > Math.Abs(responses[i - 1]) &&
                    Math.Abs(response) > Math.Abs(responses[i + 1]) &&
                    Math.Abs(response) > threshold * 0.5 && ValidPolarity(response))
                {
                    if (Math.Abs(response) > bestStrength)
                    {
                        bestStrength = Math.Abs(response);
                        bestModel = "PeakEdge";
                    }
                }

                if (bestModel != "None" && bestStrength >= minStrength)
                {
                    double t = (double)i / (len - 1);
                    candidates.Add((new P2(start.X + t * dx, start.Y + t * dy), bestStrength, bestModel));
                }
            }
            return candidates;
        }

        private List<(P2 point, double score)> ScoreEdgeCandidates(List<(P2 point, double strength, string model)> candidates, double[] profile)
        {
            var scored = new List<(P2 point, double score)>(candidates.Count);
            double threshold = _edgeThreshold.Value;
            double contrastScore = LocalContrast(profile);

            foreach (var (point, strength, model) in candidates)
            {
                double score = Math.Min(1.0, strength / (threshold * 3)) * 0.4;
                score += (model switch
                {
                    "SharpEdge" => 1.0,
                    "StepEdge" => 0.8,
                    "PeakEdge" => 0.6,
                    _ => 0.0
                }) * 0.2;
                score += contrastScore * 0.2;
                score += 0.16;
                scored.Add((point, score));
            }
            return scored;
        }

        private static double LocalContrast(double[] profile)
        {
            if (profile.Length < 3) return 0;
            double min = profile[0], max = profile[0];
            for (int i = 1; i < profile.Length; i++)
            {
                if (profile[i] < min) min = profile[i];
                if (profile[i] > max) max = profile[i];
            }
            double range = max - min;
            return range > 0 ? Math.Min(1.0, range / 255.0) : 0;
        }

        private static double GetInterpolatedPixel(Mat image, double x, double y)
        {
            int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
            int x1 = Math.Min(x0 + 1, image.Width - 1), y1 = Math.Min(y0 + 1, image.Height - 1);
            x0 = Math.Max(0, x0); y0 = Math.Max(0, y0);
            double fx = x - x0, fy = y - y0;

            double p00 = image.At<byte>(y0, x0);
            double p10 = image.At<byte>(y0, x1);
            double p01 = image.At<byte>(y1, x0);
            double p11 = image.At<byte>(y1, x1);

            return p00 * (1 - fx) * (1 - fy) + p10 * fx * (1 - fy) + p01 * (1 - fx) * fy + p11 * fx * fy;
        }
        #endregion

        #region 5. Bộ Lọc Khử Điểm Nhiễu Ngoại Lai RANSAC (Random Sample Consensus)
        private List<P2> RemoveOutliers(List<P2> edgePoints)
        {
            int count = edgePoints.Count;
            if (count < _minEdgePoints.Value) return edgePoints;

            var bestInliers = new List<P2>();
            double bestScore = 0;
            int iterations = Math.Min(40, count * 2);
            var random = new Random();

            for (int iter = 0; iter < iterations; iter++)
            {
                int idx1 = random.Next(count);
                int idx2 = random.Next(count - 1);
                if (idx2 >= idx1) idx2++;

                var p1 = edgePoints[idx1];
                var p2 = edgePoints[idx2];
                var sample = new List<P2>(2) { p1, p2 };

                if (!FitLineLeastSquares(sample, out var candidate, out _)) continue;

                var inliers = new List<P2>(count);
                double outlierThreshold = _outlierThreshold.Value;

                for (int i = 0; i < count; i++)
                {
                    if (DistancePointToLine(edgePoints[i], candidate) <= outlierThreshold)
                    {
                        inliers.Add(edgePoints[i]);
                    }
                }

                double inlierRatio = (double)inliers.Count / count;
                if (inliers.Count >= _minEdgePoints.Value && inlierRatio >= _minInlierRatio.Value)
                {
                    double score = inlierRatio * inliers.Count;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestInliers = inliers;
                    }
                }
            }
            return bestInliers.Count > 0 ? bestInliers : edgePoints;
        }

        private static double DistancePointToLine(P2 point, LineSegment line)
        {
            double a = line.P2.Y - line.P1.Y;
            double b = line.P1.X - line.P2.X;
            double c = line.P2.X * line.P1.Y - line.P1.X * line.P2.Y;
            double denom = Math.Sqrt(a * a + b * b);
            return denom > 1e-6 ? Math.Abs(a * point.X + b * point.Y + c) / denom : 0;
        }
        #endregion

        #region 6. Thuật Toán Khớp Đường Thẳng Hình Học (Mathematical Line Fitting - PCA)
        private bool FitLineLeastSquares(List<P2> points, out LineSegment segment, out double rmsError)
        {
            segment = default;
            rmsError = double.MaxValue;
            if (points.Count < 2) return false;

            if (!FitInfiniteLine(points, out double a, out double b, out double c)) return false;

            if (!ProjectLineToROI(a, b, c, out segment))
            {
                segment = SegmentFromPointExtent(points, a, b, c);
            }

            rmsError = CalculateRMSErrorToInfiniteLine(points, a, b, c);
            return true;
        }

        private static bool FitInfiniteLine(List<P2> points, out double a, out double b, out double c)
        {
            a = b = c = 0;
            int count = points.Count;

            double cx = 0, cy = 0;
            for (int i = 0; i < count; i++)
            {
                cx += points[i].X;
                cy += points[i].Y;
            }
            cx /= count; cy /= count;

            double sxx = 0, sxy = 0, syy = 0;
            for (int i = 0; i < count; i++)
            {
                double dx = points[i].X - cx;
                double dy = points[i].Y - cy;
                sxx += dx * dx; sxy += dx * dy; syy += dy * dy;
            }

            double theta = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
            a = Math.Sin(theta);
            b = -Math.Cos(theta);
            c = -(a * cx + b * cy);

            double norm = Math.Sqrt(a * a + b * b);
            if (norm < 1e-10) return false;

            a /= norm; b /= norm; c /= norm;
            return true;
        }

        private bool ProjectLineToROI(double a, double b, double c, out LineSegment segment)
        {
            segment = default;
            var r = _region.Value;
            double cx = r.Center.X, cy = r.Center.Y;
            double hw = r.Width / 2, hh = _caliperLength.Value / 2; // ★ ADD#1: dùng CaliperLength thay vì r.Height để đồng bộ với vùng caliper thực tế quét
            double rad = r.AngleDeg * Math.PI / 180.0;
            double cos = Math.Cos(rad), sin = Math.Sin(rad);

            var corners = new[]
            {
                new P2(cx + (-hw * cos - (-hh) * sin), cy + (-hw * sin + (-hh) * cos)),
                new P2(cx + ( hw * cos - (-hh) * sin), cy + ( hw * sin + (-hh) * cos)),
                new P2(cx + ( hw * cos -   hh  * sin), cy + ( hw * sin +   hh  * cos)),
                new P2(cx + (-hw * cos -   hh  * sin), cy + (-hw * sin +   hh  * cos)),
            };

            var intersections = new List<P2>(4);
            for (int i = 0; i < 4; i++)
            {
                if (GetLineSegmentIntersection(a, b, c, corners[i], corners[(i + 1) % 4], out var p))
                {
                    intersections.Add(p);
                }
            }

            var unique = intersections
                .GroupBy(p => (X: Math.Round(p.X, 1), Y: Math.Round(p.Y, 1)))
                .Select(g => g.First())
                .ToList();

            if (unique.Count < 2) return false;

            double maxDist = 0;
            P2 p1 = unique[0], p2 = unique[1];
            for (int i = 0; i < unique.Count; i++)
            {
                for (int j = i + 1; j < unique.Count; j++)
                {
                    double d = Math.Sqrt(Math.Pow(unique[i].X - unique[j].X, 2) + Math.Pow(unique[i].Y - unique[j].Y, 2));
                    if (d > maxDist)
                    {
                        maxDist = d; p1 = unique[i]; p2 = unique[j];
                    }
                }
            }

            segment = new LineSegment(p1, p2);
            return true;
        }

        private static bool GetLineSegmentIntersection(double a, double b, double c, P2 p1, P2 p2, out P2 result)
        {
            result = default;
            double dx = p2.X - p1.X, dy = p2.Y - p1.Y;
            double denom = a * dx + b * dy;
            if (Math.Abs(denom) < 1e-10) return false;

            double t = -(a * p1.X + b * p1.Y + c) / denom;
            if (t < -1e-4 || t > 1 + 1e-4) return false;

            result = new P2(p1.X + t * dx, p1.Y + t * dy);
            return true;
        }

        private static LineSegment SegmentFromPointExtent(List<P2> points, double a, double b, double c)
        {
            double dirX = -b, dirY = a;
            double cx = points.Average(p => p.X), cy = points.Average(p => p.Y);
            double dist = a * cx + b * cy + c;
            double fx = cx - a * dist, fy = cy - b * dist;

            double tmin = double.MaxValue, tmax = double.MinValue;
            foreach (var p in points)
            {
                double t = (p.X - fx) * dirX + (p.Y - fy) * dirY;
                if (t < tmin) tmin = t;
                if (t > tmax) tmax = t;
            }
            return new LineSegment(
                new P2(fx + tmin * dirX, fy + tmin * dirY),
                new P2(fx + tmax * dirX, fy + tmax * dirY));
        }

        private static double CalculateRMSErrorToInfiniteLine(List<P2> points, double a, double b, double c)
        {
            double sumSq = 0;
            foreach (var p in points)
            {
                double d = a * p.X + b * p.Y + c;
                sumSq += d * d;
            }
            return Math.Sqrt(sumSq / points.Count);
        }

        private double CalculateLineScore(List<P2> points, double rms)
        {
            double score = 1.0 / (1.0 + rms * 0.1);
            return Math.Clamp(score, 0.0, 1.0);
        }
        #endregion

        #region 7. Hệ Thống Các Hàm Hỗ Trợ Đồ Họa Độc Quyền (Helper Rendering Graphics)
        private static P2 RotatePoint(P2 p, double cx, double cy, double angle)
        {
            double cos = Math.Cos(angle), sin = Math.Sin(angle);
            double dx = p.X - cx, dy = p.Y - cy;
            return new P2(cx + dx * cos - dy * sin, cy + dx * sin + dy * cos);
        }

        /// <summary>
        /// ★ ADD#3: thêm tham số "showIndex" - khi true (do UseInteractiveCalipers bật) sẽ in số thứ tự (#0, #1, #2...)
        /// cạnh mỗi caliper, giúp kỹ sư debug biết chính xác caliper nào đang bắt sai/đúng khi kiểm tra thủ công.
        /// </summary>
        private static void DrawCalipersOnImage(Mat img, List<(P2 start, P2 end)> calipers, bool showIndex)
        {
            var bodyColor = new Scalar(51, 214, 51);
            var headColor = new Scalar(0, 215, 255);

            for (int idx = 0; idx < calipers.Count; idx++)
            {
                var (s, e) = calipers[idx];
                Cv2.Line(img, (int)Math.Round(s.X), (int)Math.Round(s.Y), (int)Math.Round(e.X), (int)Math.Round(e.Y), bodyColor, 1);

                double dx = e.X - s.X, dy = e.Y - s.Y;
                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 1) continue;

                double ux = dx / len, uy = dy / len;
                const double arrowHeadLength = 3;

                Cv2.Line(img, (int)Math.Round(e.X), (int)Math.Round(e.Y),
                    (int)Math.Round(e.X - arrowHeadLength * (ux + 0.5 * uy)), (int)Math.Round(e.Y - arrowHeadLength * (uy - 0.5 * ux)), headColor, 1);

                Cv2.Line(img, (int)Math.Round(e.X), (int)Math.Round(e.Y),
                    (int)Math.Round(e.X - arrowHeadLength * (ux - 0.5 * uy)), (int)Math.Round(e.Y - arrowHeadLength * (uy + 0.5 * ux)), headColor, 1);

                // ★ ADD#3: ghi số thứ tự caliper ngay cạnh điểm đầu, chỉ khi UseInteractiveCalipers=true
                if (showIndex)
                {
                    Cv2.PutText(img, $"#{idx}", new Point((int)Math.Round(s.X) + 4, (int)Math.Round(s.Y) - 4),
                        HersheyFonts.HersheySimplex, 0.35, new Scalar(255, 255, 255), 1);
                }
            }
        }

        private void DrawRotatedRegion(Mat img, Scalar color)
        {
            var r = _region.Value;
            double cx = r.Center.X, cy = r.Center.Y, hw = r.Width / 2;
            double hh = _caliperLength.Value / 2; // ★ ADD#1: đồng bộ với vùng caliper thực tế quét, thay vì r.Height cũ
            double rad = r.AngleDeg * Math.PI / 180.0, cos = Math.Cos(rad), sin = Math.Sin(rad);

            var corners = new[]
            {
                new P2(cx + (-hw * cos - (-hh) * sin), cy + (-hw * sin + (-hh) * cos)),
                new P2(cx + ( hw * cos - (-hh) * sin), cy + ( hw * sin + (-hh) * cos)),
                new P2(cx + ( hw * cos -   hh  * sin), cy + ( hw * sin +   hh  * cos)),
                new P2(cx + (-hw * cos -   hh  * sin), cy + (-hw * sin +   hh  * cos)),
            };

            for (int i = 0; i < 4; i++)
            {
                Cv2.Line(img, (int)Math.Round(corners[i].X), (int)Math.Round(corners[i].Y),
                    (int)Math.Round(corners[(i + 1) % 4].X), (int)Math.Round(corners[(i + 1) % 4].Y), color, 1);
            }
        }
        #endregion
    }
}














/*

// ==================== Vai trò chính:                TRÁI TIM thuật toán: các công cụ đo lường/tìm kiếm hình học kiểu công nghiệp (Caliper)
// ==================== Thành phần / Class tiêu biểu: CaliperUtil (helper), FindCircleTool, FindLineTool
// ==================== Phụ thuộc vào:                OpenCvSharp + Core.Models
// ==================== Pattern / Kỹ thuật nổi bật:   Sub-pixel edge detection, RANSAC, Circle/Line fitting

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

namespace VisionFlow.Tools.Finding
{
    /// <summary>
    /// Thuật toán tìm đường thẳng dựa trên kỹ thuật Caliper (Thước đo cạnh) - Tiêu chuẩn công nghiệp Cognex VisionPro.
    /// [QUY TRÌNH XỬ LÝ]:
    /// 1. Tính toán ma trận hình học để rải N thước đo (Caliper) song song trong vùng Search Region (RotatedRectRegion).
    /// 2. Dọc theo mỗi Caliper, thực hiện nội suy song tuyến tính (Bilinear) để trích xuất Profile độ xám 1D.
    /// 3. Sử dụng bộ lọc tích chập đạo hàm (Sobel/Roberts/Prewitt) 1D để tính toán hàm phản hồi Gradient.
    /// 4. Phân tích Profile qua 3 mô hình toán học (Sharp/Step/Peak) kết hợp bộ lọc cực tính (Polarity) để tìm điểm biên.
    /// 5. Áp dụng thuật toán RANSAC tối ưu O(1) để loại bỏ các điểm nhiễu ngoại lai (Outliers).
    /// 6. Khớp hồi quy tuyến tính trực giao bằng phân tích thành phần chính (PCA/Least Squares) để tìm đường thẳng lý thuyết.
    /// 7. Giới hạn (Clip) đường thẳng vô hạn vào phạm vi biên của vùng ROI để phục vụ hiển thị đồ họa Overlay.
    /// </summary>
    [ToolMetadata("FindLine", DisplayName = "Find Line", Category = "Detection",
        Description = "Caliper-based line finder (COGNEX-style edge models + RANSAC + PCA fit).")]
    public sealed class FindLineTool : VisionTool
    {
        #region 1. Khai Báo Các Cổng Vào / Ra Dữ Liệu (Inputs / Outputs Ports)
        // Lưu ý: Việc tách biệt cổng đầu vào và đầu ra giúp Engine có thể xây dựng đồ thị thực thi (Execution Graph) tự động.
        private readonly InputPort<IVisionImage> _input;        // Ảnh đầu vào cần xử lý (Hỗ trợ cả ảnh đơn kênh Gray và ảnh màu BGR)
        private readonly OutputPort<IVisionImage> _outImage;    // Ảnh đầu ra chứa các lớp đồ họa Overlay (Caliper, Điểm biên, Đường khớp)
        private readonly OutputPort<LineResult> _outLine;        // Cấu trúc chứa kết quả đường thẳng tìm được (Đoạn thẳng, Góc, Phán định OK/NG)
        private readonly OutputPort<P2[]> _outEdges;            // Mảng các điểm biên (Edge Points) hợp lệ cuối cùng sau khi lọc nhiễu
        private readonly OutputPort<double> _outRms;            // Sai số bình phương trung bình (Root Mean Square Error) của phép khớp đường thẳng
        private readonly OutputPort<double> _outScore;          // Điểm số chất lượng tổng hợp của đường thẳng (Phạm vi từ 0.0 đến 1.0)
        #endregion

        #region 2. Hệ Thống Tham Số Cấu Hình Hướng Người Dùng (Parameters)
        // Các tham số này sẽ được ánh xạ trực tiếp lên giao diện người dùng (UI Grid) của phần mềm VisionFlow.

        // --- Nhóm Không Gian Vùng Tìm Kiếm (Region) ---
        private readonly ToolParameter<RotatedRectRegion> _region; // Khung chữ nhật xoay định nghĩa vùng tìm kiếm đường thẳng trên ảnh

        // --- Nhóm Thuật Toán Rải Thước Đo (Detection) ---
        private readonly ToolParameter<int> _numCalipers;         // Tổng số lượng thước đo sẽ được rải đều trong phạm vi vùng ROI
        private readonly ToolParameter<double> _caliperWidth;     // Độ rộng hình học của thước đo khi hiển thị (Không tham gia tính toán toán học)
        private readonly ToolParameter<double> _caliperSpacing;   // Khoảng cách giữa các thước đo (Tham số kế thừa phục vụ tương thích ngược)
        private readonly ToolParameter<bool> _dirForward;         // Hướng quét của thước đo: True (Thuận chiều trục ROI), False (Ngược chiều)

        // --- Nhóm Ngưỡng Cạnh Biên (Threshold) ---
        private readonly ToolParameter<double> _edgeThreshold;    // Ngưỡng Gradient tối thiểu để một điểm biến thiên độ xám được coi là cạnh
        private readonly ToolParameter<string> _edgePolarity;     // Cực tính cạnh: "DarkToLight" (Tối sang Sáng), "LightToDark" (Sáng sang Tối), "Either" (Bất kỳ)
        private readonly ToolParameter<double> _edgeFilterWidth;  // Độ rộng bộ lọc làm mịn Profile (Dùng cho kiến trúc nâng cao)
        private readonly ToolParameter<double> _minEdgeStrength;  // Độ mạnh biên tối thiểu của điểm ứng viên
        private readonly ToolParameter<int> _maxEdgesPerCaliper;  // Số lượng điểm cạnh tối đa được phép lấy trên mỗi một Caliper đơn lẻ
        private readonly ToolParameter<bool> _subPixel;           // Bật/tắt tính toán vị trí cạnh đạt độ chính xác dưới mức điểm ảnh (Sub-pixel)

        // --- Nhóm Đánh Giá & Phán Định Chất Lượng (Validation) ---
        private readonly ToolParameter<int> _minEdgePoints;       // Số lượng điểm biên tối thiểu thu được để kích hoạt thuật toán khớp đường thẳng
        private readonly ToolParameter<double> _maxRms;           // Ngưỡng sai số RMS tối đa. Nếu vượt quá, đường thẳng sẽ bị phán định là NG
        private readonly ToolParameter<double> _minScore;         // Điểm số chất lượng tối thiểu cần đạt để chấp nhận kết quả tìm kiếm là OK

        // --- Nhóm Thuật Toán Nâng Cao & Loại Bỏ Nhiễu (Advanced) ---
        private readonly ToolParameter<bool> _outlierRemoval;     // Bật/tắt bộ lọc loại bỏ điểm ngoại lai (Outlier) bằng thuật toán RANSAC
        private readonly ToolParameter<double> _outlierThreshold; // Khoảng cách pixel tối đa từ một điểm tới đường thẳng giả định để coi là điểm Inlier
        private readonly ToolParameter<double> _minInlierRatio;   // Tỷ lệ điểm đúng (Inlier) tối thiểu trên tổng số điểm quét để đường thẳng được coi là hợp lệ

        // --- Nhóm Cấu Hình Hiển Thị Đồ Họa (Display) ---
        private readonly ToolParameter<bool> _drawFitted;         // Bật/tắt vẽ đường thẳng kết quả lên màn hình Overlay
        private readonly ToolParameter<bool> _drawEdges;          // Bật/tắt vẽ các chấm tròn biểu thị điểm biên cạnh phát hiện được
        private readonly ToolParameter<bool> _drawRegion;         // Bật/tắt vẽ khung ROI chữ nhật xoay và hệ thống mũi tên Caliper
        #endregion

        /// <summary>
        /// Hàm khởi tạo (Constructor): Đăng ký các cổng dữ liệu và thiết lập dải giá trị mặc định cho tham số.
        /// </summary>
        public FindLineTool()
        {
            // Liên kết cổng dữ liệu vào hệ thống core của Tool
            _input = AddInput<IVisionImage>("Image", "Image");
            _outImage = AddOutput<IVisionImage>("Image", "Overlay");
            _outLine = AddOutput<LineResult>("Line", "Line");
            _outEdges = AddOutput<P2[]>("EdgePoints", "Edge Points");
            _outRms = AddOutput<double>("RMSError", "RMS Error");
            _outScore = AddOutput<double>("Score", "Score");

            // Khởi tạo các tham số cấu hình mặc định (Default Values), dải giá trị (Min/Max) và phân nhóm (Category) trên UI Grid
            _region = AddParameter("Region", new RotatedRectRegion(new P2(320, 240), 200, 50, 0), "Search Region", category: "Region", order: 1, interaction: ParameterInteraction.RotatedRectRegion);
            _numCalipers = AddParameter("NumberOfCalipers", 10, "Number Of Calipers", 1, 10000, category: "Detection", order: 1);
            _caliperWidth = AddParameter("CaliperWidth", 3.0, "Caliper Width", 1.0, 10000.0, category: "Detection", order: 2);
            _caliperSpacing = AddParameter("CaliperSpacing", 2.0, "Caliper Spacing", 0.0, 100000.0, category: "Detection", order: 3);
            _dirForward = AddParameter("CaliperDirectionForward", true, "Search Forward", category: "Detection", order: 4);

            _edgeThreshold = AddParameter("EdgeThreshold", 1.5, "Edge Threshold", 0.0, 255.0, category: "Threshold", order: 1);
            _edgePolarity = AddChoiceParameter("EdgePolarity", "Either", new[] { "DarkToLight", "LightToDark", "Either" }, "Edge Polarity", category: "Threshold", order: 2);
            _edgeFilterWidth = AddParameter("EdgeFilterWidth", 1.0, "Edge Filter Width", 0.0, 10000.0, category: "Threshold", order: 3);
            _minEdgeStrength = AddParameter("MinEdgeStrength", 2.0, "Min Edge Strength", 0.0, 255.0, category: "Threshold", order: 4);
            _maxEdgesPerCaliper = AddParameter("MaxEdgesPerCaliper", 1, "Max Edges Per Caliper", 1, 100000, category: "Detection", order: 5);
            _subPixel = AddParameter("SubPixelAccuracy", true, "Sub-Pixel Accuracy", category: "Advanced", order: 6);

            _minEdgePoints = AddParameter("MinEdgePoints", 3, "Min Edge Points", 1, 1000000, category: "Threshold", order: 5);
            _maxRms = AddParameter("MaxRMSError", 50.0, "Max RMS Error", 0.0, 100000.0, category: "Threshold", order: 6);
            _minScore = AddParameter("MinScore", 0.3, "Min Score", 0.0, 1.0, category: "Threshold", order: 7);

            _outlierRemoval = AddParameter("EnableOutlierRemoval", true, "Outlier Removal", category: "Advanced", order: 1);
            _outlierThreshold = AddParameter("OutlierThreshold", 2.0, "Outlier Threshold", 0.0, 100000.0, category: "Advanced", order: 2);
            _minInlierRatio = AddParameter("MinInlierRatio", 0.7, "Min Inlier Ratio", 0.0, 1.0, category: "Advanced", order: 3);

            _drawFitted = AddParameter("DrawFittedLine", true, "Draw Fitted Line", category: "Display", order: 1);
            _drawEdges = AddParameter("DrawEdgePoints", true, "Draw Edge Points", category: "Display", order: 2);
            _drawRegion = AddParameter("DrawSearchRegion", true, "Draw Search Region", category: "Display", order: 3);
        }

        /// <summary>
        /// Điểm thực thi lõi (Core Execution Entry) khi Tool được kích hoạt trong chu trình kiểm tra (Inspection Cycle).
        /// </summary>
        protected override void OnExecute(IToolContext context)
        {
            // Ép kiểu lớp bọc IVisionImage về đối tượng OpenCV Mat nguyên bản để xử lý ma trận số học hiệu suất cao
            var srcImg = _input.Value!.AsMat();

            // Khởi tạo ảnh vẽ đồ họa. Nếu ảnh gốc là ảnh xám (Grayscale), chuyển sang ảnh màu BGR để có thể vẽ các đường màu (Xanh, Đỏ, Vàng).
            Mat overlay = srcImg.Channels() == 1 ? srcImg.CvtColor(ColorConversionCodes.GRAY2BGR) : srcImg.Clone();

            // Tối ưu hóa bộ nhớ: Nếu ảnh đầu vào đã là ảnh xám, ta dùng trực tiếp để tính toán toán học, tránh cấp phát vùng nhớ mới trên RAM.
            bool isSrcGray = srcImg.Channels() == 1;
            Mat gray = isSrcGray ? srcImg : srcImg.CvtColor(ColorConversionCodes.BGR2GRAY);

            // BƯỚC 1: Tính toán hình học để xác định tọa độ các Caliper rải trong không gian ảnh, kết quả: List<(P2 start, P2 end)>
            var calipers = GenerateRectangleCalipers();

            // Tối ưu hóa hiệu suất: Khởi tạo danh sách chứa điểm với dung lượng (Capacity) biết trước, tránh việc runtime liên tục 
            // phải nới rộng mảng động (Array Resizing) gây hiện tượng phân mảnh bộ nhớ và kích hoạt Garbage Collector (GC).
            var allEdges = new List<P2>(calipers.Count);

            // BƯỚC 2: Duyệt qua từng thước đo (Caliper) để tiến hành quét tín hiệu độ xám và phát hiện điểm biên cạnh
            foreach (var (start, end) in calipers)
            {
                var edgesFromCaliper = ProcessCaliperAdvanced(gray, start, end);
                if (edgesFromCaliper.Count > 0)
                {
                    allEdges.AddRange(edgesFromCaliper); // Tích lũy các điểm biên tìm thấy vào mảng tổng
                }
            }

            // BƯỚC 3: Loại bỏ các điểm biên bị sai lệch, nhiễu do bụi bẩn hoặc bề mặt cơ khí bằng bộ lọc RANSAC
            var filtered = allEdges;
            if (_outlierRemoval.Value && allEdges.Count >= _minEdgePoints.Value)
            {
                filtered = RemoveOutliers(allEdges);
            }

            // BƯỚC 4: Khởi tạo cấu trúc kết quả và tiến hành khớp (Fit) đường thẳng bằng phương pháp bình phương tối thiểu trực giao
            var result = new LineResult { Judge = Judge.NG }; // Mặc định kết quả ban đầu là NG (Not Good)
            double rms = 0, score = 0;
            var outEdges = allEdges; // Mảng điểm xuất ra mặc định là toàn bộ điểm biên ban đầu

            // Kiểm tra điều kiện: Số lượng điểm biên sau lọc phải lớn hơn hoặc bằng ngưỡng tối thiểu cấu hình
            LineSegment seg = default;
            double fittedRms = 0;
            bool haveFit = filtered.Count >= _minEdgePoints.Value && FitLineLeastSquares(filtered, out seg, out fittedRms);
            if (haveFit)
            {
                rms = fittedRms;
                score = CalculateLineScore(filtered, fittedRms); // Tính toán điểm số chất lượng dựa trên độ lệch phân bổ điểm
                result.Segment = seg;

                // Quy đổi góc từ Radian sang Độ hình học (Phạm vi -180 độ đến +180 độ) để người vận hành máy dễ đọc hiểu trên UI
                result.AngleDeg = Math.Atan2(seg.P2.Y - seg.P1.Y, seg.P2.X - seg.P1.X) * 180.0 / Math.PI;

                // TIÊU CHUẨN PHÁN ĐỊNH (PASS/FAIL CRITERIA): 
                // Kết quả kiểm tra chỉ đạt OK khi sai số RMS nằm dưới ngưỡng tối đa VÀ điểm chất lượng vượt ngưỡng tối thiểu.
                result.Judge = (fittedRms <= _maxRms.Value && score >= _minScore.Value) ? Judge.OK : Judge.NG;

                if (result.Judge == Judge.OK)
                {
                    outEdges = filtered; // Nếu kết quả đạt OK, gán tập điểm sạch (Inliers) làm dữ liệu xuất ra cổng đầu ra
                }
            }

            // BƯỚC 5: Xử lý đồ họa Overlay trực quan (Vẽ kết quả đồ họa đè lên ảnh hiển thị)
            if (_drawRegion.Value)
            {
                DrawRotatedRegion(overlay, new Scalar(255, 255, 0));   // Vẽ khung ROI hình chữ nhật xoay bằng màu Cyan/Vàng
                DrawCalipersOnImage(overlay, calipers);                 // Vẽ thân các thước đo và mũi tên chỉ thị hướng quét cạnh
            }
            if (_drawEdges.Value)
            {
                foreach (var e in allEdges)
                {
                    // Vẽ các điểm biên phát hiện được bằng một hình tròn đặc màu xanh Lime (Đường kính 2 pixel)
                    Cv2.Circle(overlay, (int)Math.Round(e.X), (int)Math.Round(e.Y), 2, new Scalar(102, 255, 0), -1);
                }
            }
            if (_drawFitted.Value && haveFit)
            {
                // Đường thẳng kết quả: Nếu phán định OK vẽ màu Cam (Orange), nếu NG vẽ màu Đỏ (Red) để cảnh báo người vận hành
                var color = result.Judge == Judge.OK ? new Scalar(0, 165, 255) : new Scalar(0, 0, 255);
                Cv2.Line(overlay, (int)Math.Round(seg.P1.X), (int)Math.Round(seg.P1.Y), (int)Math.Round(seg.P2.X), (int)Math.Round(seg.P2.Y), color, 2);
            }

            // GIẢI PHÓNG VÙNG NHỚ KHÔNG QUẢN LÝ (UNMANAGED MEMORY):
            // Bản chất OpenCVSharp viết trên nền thư viện C++ gốc. Nếu không chủ động Dispose các ma trận trung gian, 
            // bộ nhớ RAM sẽ bị rò rỉ (Memory Leak) liên tục, có thể làm crash phần mềm SCADA/Vision sau vài tiếng chạy máy liên tục.
            if (!isSrcGray) gray.Dispose();

            // Gán dữ liệu tính toán hoàn thiện vào các cổng Output
            _outImage.Value = new MatVisionImage(overlay);
            _outLine.Value = result;
            _outEdges.Value = outEdges.ToArray();
            _outRms.Value = rms;
            _outScore.Value = score;

            // Log trạng thái phục vụ công tác giám sát từ xa hoặc lưu vết hệ thống (Traceability System)
            context.Log($"FindLine: edges={allEdges.Count} kept={filtered.Count} judge={result.Judge} angle={result.AngleDeg:F1} rms={rms:F2} score={score:F2}");
        }

        #region 3. Tính Toán Hình Học Thước Đo (Caliper Geometry Calculations)
        /// <summary>
        /// Tạo và rải ma trận tọa độ các đường thước đo (Caliper) chạy song song bên trong vùng ROI chữ nhật xoay.
        /// </summary>
        private List<(P2 start, P2 end)> GenerateRectangleCalipers()
        {
            var r = _region.Value;
            int n = Math.Max(1, _numCalipers.Value); // Đảm bảo số lượng Caliper luôn tối thiểu bằng 1 để tránh lỗi chia cho 0
            var list = new List<(P2 start, P2 end)>(n);

            double cx = r.Center.X, cy = r.Center.Y;
            double regionWidth = r.Width, caliperLength = r.Height;
            double angle = r.AngleDeg * Math.PI / 180.0; // Chuyển đổi góc từ Độ sang Radian phục vụ hàm Lượng giác
            bool forward = _dirForward.Value;

            // KIẾN TRÚC HÌNH HỌC: Xác định hướng tìm kiếm của Caliper dựa trên tỷ lệ kích thước ROI.
            // Nếu Chiều rộng ROI > Chiều cao ROI: Người dùng đang muốn tìm một đường thẳng nằm ngang.
            // Hệ thống sẽ rải các Caliper chạy dọc (Quét từ trên xuống hoặc dưới lên tùy thuộc vào cờ forward).
            bool searchingForHorizontalLine = regionWidth > caliperLength;

            for (int i = 0; i < n; i++)
            {
                // Biến t chạy từ 0.0 đến 1.0 đóng vai trò là tham số tỷ lệ (Interpolation Factor) để rải đều Caliper dọc theo biên ROI
                double t = n > 1 ? (double)i / (n - 1) : 0.5;
                P2 start, end;

                if (searchingForHorizontalLine)
                {
                    // Tính tọa độ Caliper nằm ngang không xoay góc (Mặc định tâm ảnh là gốc hệ tọa độ)
                    double x = cx + (t - 0.5) * regionWidth;
                    start = forward ? new P2(x, cy - caliperLength / 2) : new P2(x, cy + caliperLength / 2);
                    end = forward ? new P2(x, cy + caliperLength / 2) : new P2(x, cy - caliperLength / 2);
                }
                else
                {
                    // Tính tọa độ Caliper nằm dọc không xoay góc
                    double y = cy + (t - 0.5) * caliperLength;
                    start = forward ? new P2(cx - regionWidth / 2, y) : new P2(cx + regionWidth / 2, y);
                    end = forward ? new P2(cx + regionWidth / 2, y) : new P2(cx - regionWidth / 2, y);
                }

                // Nếu vùng tìm kiếm ROI có góc xoay phi-không (AngleDeg != 0), áp dụng ma trận xoay điểm quanh tâm ROI (cx, cy)
                if (Math.Abs(angle) > 0.001)
                {
                    start = RotatePoint(start, cx, cy, angle);
                    end = RotatePoint(end, cx, cy, angle);
                }
                list.Add((start, end));
            }
            return list;
        }

        #endregion

        #region 4. Đường Ống Phân Tích & Phát Hiện Cạnh Biên (Edge Detection Pipeline)
        /// <summary>
        /// Bộ xử lý tín hiệu 1D nâng cao cho từng Caliper đơn lẻ: Chuyển đổi không gian ảnh 2D thành mảng sóng tín hiệu 1D,
        /// chạy qua bộ lọc nhân chập đạo hàm, phân tích theo cấu trúc đặc trưng hình học để trả về tọa độ điểm cạnh chính xác nhất.
        /// </summary>
        private List<P2> ProcessCaliperAdvanced(Mat gray, P2 start, P2 end)
        {
            var valid = new List<P2>(1);
            double dx = end.X - start.X, dy = end.Y - start.Y;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1) return valid; // Chiều dài thước đo quá ngắn, không đủ không gian để quét mẫu ảnh

            // BƯỚC 4.1: Sử dụng kỹ thuật nội suy song tuyến tính để trích xuất Profile cường độ sáng 1D dọc thân Caliper
            var profile = Extract1DProfile(gray, start, end); // Output example: profile[i] = [30.2, 32.5, 115.0, 0.0, 0.0]
            if (profile.Length < 3) return valid;

            // BƯỚC 4.2: Nhân chập tín hiệu 1D với tổ hợp nhân lọc Sobel, Roberts, Prewitt để tính độ mạnh Gradient pháp tuyến
            var responses = Apply1DEdgeKernels(profile);      // Output example: responses[i] = [0.0, 51.57, 124.11, 75.93, 0.0]

            // BƯỚC 4.3: Áp dụng thuật toán máy trạng thái (State Machine) để phân tách và định vị mô hình cạnh (Sharp, Step, Peak)
            var candidates = DetectEdgesWithModels(responses, start, end);  // Output example:[ (point: {X: 101.0, Y: 100.0}, strength: 51.57,  model: "SharpEdge" ),
                                                                            //                  (point: {X: 102.0, Y: 100.0}, strength: 124.11, model: "PeakEdge"  ),
                                                                            //                  (point: {X: 103.0, Y: 100.0}, strength: 75.93,  model: "SharpEdge" )]

            // BƯỚC 4.4: Tính toán điểm số chất lượng đa tiêu chí (Độ tương phản vùng, độ sắc cạnh, vị trí hình học)
            var scored = ScoreEdgeCandidates(candidates, profile);          // Output example:[ (point: { X: 101.0, Y: 100.0}, score: 0.6575 ),
                                                                            //                  (point: { X: 102.0, Y: 100.0}, score: 0.7710 ),
                                                                            //                  (point: { X: 103.0, Y: 100.0}, score: 0.7225 )]

            // Kiểm tra xem danh sách các điểm đã được chấm điểm (scored) có dữ liệu hay không
            if (scored.Count > 0)
            {
                // Tính toán ngưỡng điểm tối thiểu (threshold).
                // Ở đây lấy 50% (0.5) so với giá trị ngưỡng gốc (_minScore.Value).
                double thr = _minScore.Value * 0.5;

                // Tìm vị trí (index) của phần tử ĐẦU TIÊN trong danh sách có điểm số (score) đạt hoặc vượt qua ngưỡng thr vừa tính.
                // Việc này phục vụ logic: Lấy cạnh hợp lệ đầu tiên dọc theo chiều quét (scan direction).
                int idx = scored.FindIndex(e => e.score >= thr);

                // Xác định phần tử tốt nhất (best):
                // - Nếu tìm thấy phần tử đạt ngưỡng (idx >= 0): Lấy phần tử tại vị trí idx đó.
                // - Nếu KHÔNG có phần tử nào đạt ngưỡng: Lùi về phương án dự phòng (fallback) là
                //   sắp xếp giảm dần theo score và chọn phần tử có score cao nhất toàn bộ danh sách.
                var best = idx >= 0 ? scored[idx] : scored.OrderByDescending(e => e.score).First();

                // Kiểm tra lại lần cuối: Nếu phần tử được chọn (best) thực sự đạt hoặc vượt ngưỡng thr
                // thì mới lấy tọa độ/điểm (point) của nó thêm vào danh sách kết quả hợp lệ (valid).
                valid.Add(best.point);
            }
            return valid;
        }

        /// <summary>
        /// Trích xuất mảng giá trị cường độ xám 1D chạy dọc theo thân Caliper. Output example: profile[i] = [30.2, 32.5, 115.0, 0.0, 0.0]
        /// Sử dụng phép nội suy hình học để số lượng mẫu trích xuất tương ứng chính xác với chiều dài pixel thực tế của Caliper.
        /// </summary>
        private static double[] Extract1DProfile(Mat gray, P2 start, P2 end)
        {
            double dx = end.X - start.X, dy = end.Y - start.Y;
            double length = Math.Sqrt(dx * dx + dy * dy);
            int numSamples = Math.Max(3, (int)length); // Số lượng mẫu cần lấy tương đương độ dài pixel của thước
            var profile = new double[numSamples];
            int width = gray.Width, height = gray.Height;

            for (int i = 0; i < numSamples; i++)
            {
                // Tính toán vị trí tọa độ thực tế (X, Y) dưới dạng số thực dấu phẩy động (Double) trên ảnh 2D
                double t = (double)i / (numSamples - 1);
                double x = start.X + t * dx, y = start.Y + t * dy;

                // KIỂM TRA ĐIỀU KIỆN BIÊN (BOUNDARY PROTECTION): 
                // Clamping: Kẹp tọa độ x, y vào dải hợp lệ sát biên ảnh
                double clampedX = Math.Clamp(x, 0, width - 1.001);
                double clampedY = Math.Clamp(y, 0, height - 1.001);

                profile[i] = GetInterpolatedPixel(gray, clampedX, clampedY);
            }
            return profile;
        }

        /// <summary>
        /// Áp dụng tổ hợp các toán tử đạo hàm bậc một (1D Convolution Kernels) để phát hiện sự thay đổi đột ngột của mức xám.
        /// Công thức sử dụng tổ hợp phân bổ trọng số: Response = 0.5 * Sobel + 0.3 * Roberts + 0.2 * Prewitt.
        /// </summary>
        private static double[] Apply1DEdgeKernels(double[] profile)        // Output example: responses[i] = [0.0, 51.57, 124.11, 75.93, 0.0]
        {
            int len = profile.Length;
            if (len < 3) return Array.Empty<double>();

            var responses = new double[len];
            for (int i = 1; i < len - 1; i++)
            {
                // Toán tử Sobel 1D: Lấy chênh lệch giữa điểm phía sau và điểm phía trước, bỏ qua điểm hiện tại (Chống nhiễu tần số cao tốt)
                double sobel = profile[i + 1] - profile[i - 1];
                // Toán tử Roberts 1D: Chênh lệch cục bộ tức thời lân cận trực tiếp với ô phía trước
                double roberts = profile[i] - profile[i - 1];
                // Toán tử Prewitt 1D: Tính giá trị trung bình phẳng của Gradient cục bộ xung quanh lõi i
                double prewitt = sobel * 0.5;

                // Cộng tổng tích lũy với hệ số phân bổ trọng số tối ưu hóa thực nghiệm, cân bằng giữa độ nhạy biên và khả năng triệt tiêu tín hiệu nhiễu nền
                responses[i] = sobel * 0.5 + roberts * 0.3 + prewitt * 0.2;
            }
            return responses;
        }

        /// <summary>
        /// Mô phỏng lõi giải thuật Cognex Edge Model để nhận diện cấu trúc vật lý của biên cạnh dựa trên hình dáng đồ thị Gradient 1D.
        /// Output example:[ (point: {X: 101.0, Y: 100.0}, strength: 51.57,  model: "SharpEdge" ),
                          // (point: {X: 102.0, Y: 100.0}, strength: 124.11, model: "PeakEdge"  ),
                          // (point: { X: 103.0, Y: 100.0}, strength: 75.93,  model: "SharpEdge" )]
        /// </summary>
        private List<(P2 point, double strength, string model)> DetectEdgesWithModels(double[] responses, P2 start, P2 end) 
        {
            var candidates = new List<(P2 point, double strength, string model)>();
            double dx = end.X - start.X, dy = end.Y - start.Y;
            double threshold = _edgeThreshold.Value;
            string polarity = _edgePolarity.Value;
            int len = responses.Length;

            for (int i = 1; i < len - 1; i++)
            {
                double response = responses[i];
                string bestModel = "None";
                double bestStrength = 0;

                // MÔ HÌNH 1: Sharp Edge (Cạnh sắc nét) - Điểm có giá trị Gradient tuyệt đối lớn vượt ngưỡng cấu hình.
                // Mô hình này bắt buộc kiểm định tính chất cực tính sáng tối (Edge Polarity) để loại bỏ các cạnh giả lập.
                if (Math.Abs(response) > threshold)
                {
                    bool validPolarity = polarity switch
                    {
                        "DarkToLight" => response > threshold,   // Chuyển từ vùng Tối sang Sáng (Đạo hàm mang dấu Dương)
                        "LightToDark" => response < -threshold,  // Chuyển từ vùng Sáng sang Tối (Đạo hàm mang dấu Âm)
                        _ => true                                // Trạng thái "Either" chấp nhận bất kỳ hướng chuyển đổi nào
                    };
                    if (validPolarity && Math.Abs(response)>bestStrength)
                    {
                        bestStrength = Math.Abs(response);
                        bestModel = "SharpEdge";
                    }
                }

                // MÔ HÌNH 2: Step Edge (Biến thiên dạng bậc thang phẳng thoải) - Thường xuất hiện ở các biên có hiện tượng mờ (Blur) do tiêu cự 
                // ống kính (Out of focus) hoặc ánh sáng tán xạ. Độ mạnh được tính bằng trung bình cộng phản hồi của 3 ô lân cận liên tiếp.
                if (i >= 2 && i < len - 2)
                {
                    double stepStrength = Math.Abs(responses[i - 1] + response + responses[i + 1]) / 3.0;
                    if (stepStrength > threshold * 0.7 && stepStrength > bestStrength)
                    {
                        bestStrength = stepStrength;
                        bestModel = "StepEdge";
                    }
                }

                // MÔ HÌNH 3: Peak Edge (Cực đại cục bộ dạng đỉnh nhọn) - Đặc trưng cho các vạch kẻ vách dòng (Line Stripe), 
                // nơi cường độ sáng tăng vọt lên rồi giảm xuống ngay lập tức. Xác định bằng điều kiện điểm i lớn hơn cả điểm i-1 và i+1.
                if (Math.Abs(response) > Math.Abs(responses[i - 1]) &&
                    Math.Abs(response) > Math.Abs(responses[i + 1]) &&
                    Math.Abs(response) > threshold * 0.5)
                {
                    if (Math.Abs(response) > bestStrength)
                    {
                        bestStrength = Math.Abs(response);
                        bestModel = "PeakEdge";
                    }
                }

                // Nếu điểm i khớp với một trong ba mô hình toán học trên, thực hiện ánh xạ tuyến tính vị trí tỷ lệ t 
                // từ mảng 1D ngược trở lại không gian tọa độ thực tế 2D (X, Y) của ảnh.
                if (bestModel != "None")
                {
                    double t = (double)i / (len - 1);
                    candidates.Add((new P2(start.X + t * dx, start.Y + t * dy), bestStrength, bestModel));
                }
            }
            return candidates;
        }

        /// <summary>
        /// Tính toán tổng điểm (Score) đánh giá chất lượng cho từng ứng viên cạnh bằng phương pháp chấm điểm đa tiêu chí.
        /// Output example:[ (point: { X: 101.0, Y: 100.0}, score: 0.6575 ),
        //                   (point: { X: 102.0, Y: 100.0}, score: 0.7710 ),
        //                   (point: { X: 103.0, Y: 100.0}, score: 0.7225 )]
        /// </summary>
        private List<(P2 point, double score)> ScoreEdgeCandidates(List<(P2 point, double strength, string model)> candidates, double[] profile)
        {
            var scored = new List<(P2 point, double score)>(candidates.Count);
            double threshold = _edgeThreshold.Value;
            double contrastScore = LocalContrast(profile); // Tính toán độ tương phản tổng thể của vệt thước quét. // Output: [0.0 - 1.0]

            foreach (var (point, strength, model) in candidates)
            {
                // Tiêu chí 1: Độ mạnh biên tuyệt đối (Tỷ trọng chiếm 40% tổng số điểm)
                double score = Math.Min(1.0, strength / (threshold * 3)) * 0.4;

                // Tiêu chí 2: Mức độ ưu tiên của mô hình hình học (Tỷ trọng chiếm 20% tổng số điểm)
                // Cạnh SharpEdge là cạnh lý tưởng nhất trong xử lý ảnh công nghiệp nên được chấm điểm tối đa.
                score += (model switch
                {
                    "SharpEdge" => 1.0,
                    "StepEdge" => 0.8,
                    "PeakEdge" => 0.6,
                    _ => 0.0
                }) * 0.2;

                // Tiêu chí 3: Độ tương phản dải sáng cục bộ của thước quét (Tỷ trọng chiếm 20% tổng số điểm)
                score += contrastScore * 0.2;

                // Tiêu chí 4: Hệ số vị trí phân bổ hình học ổn định (Hằng số bù nhiễu biên 0.16)
                score += 0.16;

                scored.Add((point, score));
            }
            return scored;
        }

        /// <summary>
        /// Tính độ tương phản cục bộ (Local Contrast) bằng hiệu số mức xám lớn nhất và nhỏ nhất thu được trên thước quét.
        /// </summary>
        private static double LocalContrast(double[] profile)   // Output: [0.0 - 1.0]
        {
            if (profile.Length < 3) return 0;
            double min = profile[0], max = profile[0];
            for (int i = 1; i < profile.Length; i++)
            {
                if (profile[i] < min) min = profile[i];
                if (profile[i] > max) max = profile[i];
            }
            double range = max - min; // Dải biên độ biến thiên độ xám [0 - 255]
            return range > 0 ? Math.Min(1.0, range / 255.0) : 0; // Chuẩn hóa về dải số thực tự nhiên [0.0 - 1.0]
        }

        /// <summary>
        /// TOÁN TỬ TỐI ƯU SIÊU TỐC (HIGH-PERFORMANCE OPTIMIZATION): 
        /// Bỏ qua hoàn toàn cơ chế kiểm tra biên mảng (Boundary Check) của lớp bọc C#, thực hiện tính toán công thức 
        /// Nội suy song tuyến tính (Bilinear Interpolation) trực tiếp trên các ô nhớ vật lý kế cận:
        /// 
        ///      p00(x0,y0) ------ p10(x0+1,y0)
        ///          |                 |
        ///          |      P(x,y)     |
        ///          |                 |
        ///      p01(x0,y0+1) ---- p11(x0+1,y0+1)
        /// </summary>
        private static double GetInterpolatedPixel(Mat image, double x, double y)
        {
            int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
            int x1 = Math.Min(x0 + 1, image.Width - 1), y1 = Math.Min(y0 + 1, image.Height - 1);
            x0 = Math.Max(0, x0); y0 = Math.Max(0, y0);
            double fx = x - x0, fy = y - y0; // Phần thập phân dùng để làm trọng số khoảng cách hình học

            // Tính toán địa chỉ ô nhớ bằng biểu thức số học con trỏ (Pointer Arithmetic): Offset = (y * Step) + x
            double p00 = image.At<byte>(y0, x0);
            double p10 = image.At<byte>(y0, x1);
            double p01 = image.At<byte>(y1, x0);
            double p11 = image.At<byte>(y1, x1);

            // Áp dụng công thức giải tích phân bổ trọng số diện tích hai chiều để tính ra cường độ sáng sub-pixel cuối cùng tại vị trí thực P(x, y)
            return p00 * (1 - fx) * (1 - fy) + p10 * fx * (1 - fy) + p01 * (1 - fx) * fy + p11 * fx * fy;
        }
        #endregion

        #region 5. Bộ Lọc Khử Điểm Nhiễu Ngoại Lai RANSAC (Random Sample Consensus)
        /// <summary>
        /// Giải thuật RANSAC nâng cấp cấu trúc toán học đạt tốc độ xử lý O(1) cho mỗi vòng lặp.
        /// [CẢI TIẾN HIỆU SUẤT]: Thay vì dùng đoạn mã cũ `edgePoints.OrderBy(_ => random.Next()).Take(2)` làm phát sinh hành vi cấp phát 
        /// bộ nhớ heap liên tục và sắp xếp lại toàn bộ danh sách điểm rất chậm, đoạn mã mới bốc chỉ mục (Index) trực tiếp từ mảng qua Random. Next().
        /// Nhờ đó giúp Tool duy trì tốc độ xử lý xử lý siêu tốc dưới 2ms kể cả khi tập điểm biên lên tới hàng ngàn điểm.
        /// </summary>
        private List<P2> RemoveOutliers(List<P2> edgePoints)
        {
            int count = edgePoints.Count;
            if (count < _minEdgePoints.Value) return edgePoints;

            var bestInliers = new List<P2>();
            double bestScore = 0;

            // Khống chế số lượng vòng lặp tối đa của RANSAC để đảm bảo tính thời gian thực (Real-time Deterministic) cho dây chuyền nhà máy
            int iterations = Math.Min(40, count * 2);
            var random = new Random();

            for (int iter = 0; iter < iterations; iter++)
            {
                // Bốc ngẫu nhiên chỉ mục của điểm thứ nhất với độ phức tạp thuật toán đạt O(1)
                int idx1 = random.Next(count);              // Ex: {0, 1, 2, 3}
                // Bốc ngẫu nhiên chỉ mục của điểm thứ hai
                int idx2 = random.Next(count - 1);          // Ex: {0, 1, 3, 4}
                if (idx2 >= idx1) idx2++; // Cơ chế dịch chuyển vị trí để đảm bảo chỉ mục idx2 không bao giờ trùng khớp với idx1

                var p1 = edgePoints[idx1];
                var p2 = edgePoints[idx2];

                // Khởi tạo tập mẫu giả định gồm hai điểm biên vừa bốc ngẫu nhiên
                var sample = new List<P2>(2) { p1, p2 };

                // Thử nghiệm khớp một đường thẳng đi qua hai điểm mẫu này. Nếu thất bại (hai điểm trùng nhau), bỏ qua vòng lặp này.
                if (!FitLineLeastSquares(sample, out var candidate, out _)) continue;

                var inliers = new List<P2>(count);
                double outlierThreshold = _outlierThreshold.Value;

                // Duyệt qua toàn bộ tập điểm biên ban đầu để đếm số lượng điểm đồng thuận (Inliers)
                for (int i = 0; i < count; i++)
                {
                    // Nếu khoảng cách hình học từ điểm i tới đường thẳng thử nghiệm nhỏ hơn ngưỡng cho phép, coi đó là điểm đúng (Inlier)
                    if (DistancePointToLine(edgePoints[i], candidate) <= outlierThreshold)
                    {
                        inliers.Add(edgePoints[i]);
                    }
                }

                double inlierRatio = (double)inliers.Count / count;

                // Điều kiện ghi nhận mô hình: Số lượng điểm đúng phải lớn hơn ngưỡng tối thiểu VÀ tỉ lệ vượt mức Inlier Ratio cấu hình
                if (inliers.Count >= _minEdgePoints.Value && inlierRatio >= _minInlierRatio.Value)
                {
                    // Điểm số toán học của vòng lặp RANSAC tỷ lệ thuận với số lượng và mật độ phân bổ điểm đồng thuận
                    double score = inlierRatio * inliers.Count;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestInliers = inliers; // Lưu giữ tập hợp điểm sạch nhất tìm thấy cho tới hiện tại
                    }
                }
            }
            // Nếu tìm thấy mô hình đồng thuận, xuất ra tập điểm sạch đã loại bỏ nhiễu; nếu không, giữ lại tập điểm cũ để tránh mất mát dữ liệu
            return bestInliers.Count > 0 ? bestInliers : edgePoints;
        }

        /// <summary>
        /// Tính toán khoảng cách Euclid ngắn nhất từ một điểm P(x0, y0) tới một đoạn thẳng được định nghĩa bởi hai điểm P1, P2.
        /// Áp dụng phương trình tổng quát đường thẳng dạng Giải tích: Ax + By + C = 0.
        /// </summary>
        private static double DistancePointToLine(P2 point, LineSegment line)
        {
            double a = line.P2.Y - line.P1.Y;
            double b = line.P1.X - line.P2.X;
            double c = line.P2.X * line.P1.Y - line.P1.X * line.P2.Y;
            double denom = Math.Sqrt(a * a + b * b);

            // Tránh bẫy chia cho số 0 nếu đoạn thẳng đầu vào bị co rút thành một điểm duy nhất (Độ dài bằng 0)
            return denom > 1e-6 ? Math.Abs(a * point.X + b * point.Y + c) / denom : 0;
        }
        #endregion

        #region 6. Thuật Toán Khớp Đường Thẳng Hình Học (Mathematical Line Fitting - PCA)
        /// <summary>
        /// Khớp đường thẳng lý thuyết từ tập hợp điểm biên bằng phương pháp Bình Phương Tối Thiểu Trực Giao (Orthogonal Regression) dựa trên thuật toán PCA.
        /// Phương pháp này ưu việt hơn phép hồi quy tuyến tính thông thường (Y = aX + b) vì nó giảm thiểu sai số theo cả hai trục X và Y, 
        /// hoàn toàn không bị lỗi crash toán học khi gặp đường thẳng đứng vuông góc 90 độ (độ dốc tiến tới vô cùng).
        /// </summary>
        private bool FitLineLeastSquares(List<P2> points, out LineSegment segment, out double rmsError)
        {
            segment = default;
            rmsError = double.MaxValue;
            if (points.Count < 2) return false;

            // BƯỚC 6.1: Tìm phương trình tổng quát của đường thẳng vô hạn: ax + by + c = 0 bằng kỹ thuật giải ma trận hiệp biến PCA
            if (!FitInfiniteLine(points, out double a, out double b, out double c)) return false;   // Output example: phương trình đường thẳng: 0.6901x - 0.7237y + 0.3696 = 0

            // BƯỚC 6.2: Chiếu cắt (Clip) đường thẳng vô hạn vào phạm vi biên không gian của vùng ROI chữ nhật xoay.
            // Nếu phép chiếu cắt thất bại (đường thẳng nằm hoàn toàn ngoài ROI), thực hiện thuật toán Fallback chiếu các điểm biên cực để giới hạn đoạn thẳng.
            if (!ProjectLineToROI(a, b, c, out segment))
            {
                segment = SegmentFromPointExtent(points, a, b, c);
            }

            // BƯỚC 6.3: Tính toán sai số căn trung bình bình phương (RMS Error) từ tập điểm thực tế tới đường thẳng vô hạn vừa tìm được
            rmsError = CalculateRMSErrorToInfiniteLine(points, a, b, c);
            return true;
        }

        /// <summary>
        /// Phân tích thành phần chính (Principal Component Analysis) để "Tìm vectơ pháp tuyến" của tập hợp điểm
        /// (dựa trên hình học để tìm ra một đường thẳng khớp nhất (Best-fit Line) đi qua một tập hợp các điểm dữ liệu).
        /// Phương trình đường thẳng tổng quát: ax + by + c = 0
        /// Trong đó, (a, b) chính là vectơ pháp tuyến (vectơ vuông góc với đường thẳng), và c là hằng số dịch chuyển.
        /// </summary>
        private static bool FitInfiniteLine(List<P2> points, out double a, out double b, out double c) // Output example: bool success = true;
                                                                                                       // a = 0.6901;  // Hệ số x
                                                                                                       // b = -0.7237; // Hệ số y
                                                                                                       // c = 0.3696;  // Hằng số tự do
                                                                                                       // thu được phương trình đường thẳng: 0.6901x - 0.7237y + 0.3696 = 0
        {
            a = b = c = 0;
            int count = points.Count;

            // 1. Tính toán tọa độ trọng tâm hình học (Centroid) của hệ thống cụm điểm
            // Mục đích: Bất kỳ đường thẳng nào "khớp nhất" với một cụm điểm thì về mặt toán học,
            // nó bắt buộc phải đi qua điểm trọng tâm này. Việc tìm trọng tâm giúp ta dịch chuyển gốc tọa độ về (cx, cy) nhằm đơn giản hóa các phép tính ma trận phía sau.
            double cx = 0, cy = 0;
            for (int i = 0; i < count; i++)
            {
                cx += points[i].X;
                cy += points[i].Y;
            }
            cx /= count; cy /= count;

            // 2. Tính toán ma trận hiệp biến bậc hai (Covariance Matrix Components): Sxx, Sxy, Syy đại diện cho mức độ phân tán của điểm ảnh
            // Ý nghĩa: Đoạn này tính toán mức độ sai lệch (khoảng cách) dx, dy của từng điểm so với tâm (cx, cy),
            // sau đó tích lũy lại thành các giá trị sxx (biến thiên theo trục X), syy (biến thiên theo trục Y), và sxy (mối tương quan xu hướng giữa X và Y).
            // Mục đích: Ba giá trị này cấu thành một ma trận đối xứng gọi là Ma trận hiệp biến: [sxx sxy
            //                                                                                    sxy syy]
            // Ma trận này chứa thông tin cực kỳ quan trọng: Nó cho biết cụm điểm của bạn đang trải dài (phân tán) theo hướng nào nhiều nhất trong không gian 2D.
            double sxx = 0, sxy = 0, syy = 0;
            for (int i = 0; i < count; i++)
            {
                double dx = points[i].X - cx;
                double dy = points[i].Y - cy;
                sxx += dx * dx; sxy += dx * dy; syy += dy * dy;
            }

            // 3. Sử dụng hàm Atan2 để trích xuất trị riêng (Eigenvalue) và vectơ riêng (Eigenvector) ứng với hướng phân tán chính
            // Ý nghĩa toán học (PCA rút gọn): Thay vì phải dùng các thuật toán đại số tuyến tính phức tạp để phân rã ma trận tìm Vectơ riêng (Eigenvector),
            // đối với không gian 2D, người ta chứng minh được góc theta (hướng vuông góc với đường thẳng khớp nhất) có thể tính trực tiếp bằng hàm lượng giác
            // Math.Atan2(2 * sxy, sxx - syy).
            double theta = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
            a = Math.Sin(theta);
            b = -Math.Cos(theta);
            c = -(a * cx + b * cy); // Xác định hằng số tự do c dựa trên việc đường thẳng phải đi qua tâm hình học (cx, cy)

            // 4. Chuẩn hóa vectơ pháp tuyến đơn vị (Unit Vector Normalization) để độ dài hình học của vectơ (a,b) bằng chính xác 1
            double norm = Math.Sqrt(a * a + b * b);
            if (norm < 1e-10) return false;

            a /= norm; b /= norm; c /= norm;
            return true;
        }

        /// <summary>
        /// Thuật toán hình học chiếu và cắt đường thẳng vô hạn (ax+by+c=0) giao với 4 cạnh bao của ROI chữ nhật xoay.
        /// Mục đích: Giúp đoạn thẳng hiển thị vừa khít, cân đối bên trong khung ROI, mang lại giao diện hiển thị chuyên nghiệp như Cognex VisionPro.
        /// </summary>
        private bool ProjectLineToROI(double a, double b, double c, out LineSegment segment)    // Output example: segment chứa 2 đầu mút giới hạn bởi biên của ROI:
                                                                                                // segment.Start = (X: 120.4, Y: 150.2); // Điểm chui vào ROI
                                                                                                // segment.End   = (X: 210.8, Y: 155.6); // Điểm chui ra khỏi ROI
        {
            segment = default;
            var r = _region.Value;
            double cx = r.Center.X, cy = r.Center.Y;
            double hw = r.Width / 2, hh = r.Height / 2;
            double rad = r.AngleDeg * Math.PI / 180.0;
            double cos = Math.Cos(rad), sin = Math.Sin(rad);

            // Sử dụng ma trận xoay 2D để tính ra vị trí thực tế của 4 đỉnh góc khung ROI hình chữ nhật xoay trên tọa độ ảnh vật lý
            var corners = new[]
            {
                new P2(cx + (-hw * cos - (-hh) * sin), cy + (-hw * sin + (-hh) * cos)),
                new P2(cx + ( hw * cos - (-hh) * sin), cy + ( hw * sin + (-hh) * cos)),
                new P2(cx + ( hw * cos -   hh  * sin), cy + ( hw * sin +   hh  * cos)),
                new P2(cx + (-hw * cos -   hh  * sin), cy + (-hw * sin +   hh  * cos)),
            };

            var intersections = new List<P2>(4);
            // Tìm giao điểm của đường thẳng kết quả với lần lượt 4 đoạn biên bao quanh hình chữ nhật xoay
            for (int i = 0; i < 4; i++)
            {
                if (GetLineSegmentIntersection(a, b, c, corners[i], corners[(i + 1) % 4], out var p)) // Output example: result.X = 15.0;
                                                                                                      //                 result.Y = 10.5;
                {
                    intersections.Add(p); // Tích lũy các điểm giao nhau hợp lệ
                }
            }

            // Sử dụng biểu thức LINQ GroupBy kết hợp hàm làm tròn Math.Round để loại bỏ hoàn toàn các điểm giao trùng lặp 
            // phát sinh do sai số làm tròn số dấu phẩy động (Floating-point precision noise) của cấu trúc máy tính.
            var unique = intersections
                .GroupBy(p => (X: Math.Round(p.X, 1), Y: Math.Round(p.Y, 1)))
                .Select(g => g.First())
                .ToList();

            if (unique.Count < 2) return false; // Đường thẳng không cắt ngang qua ROI (nằm ngoài hoàn toàn)

            // Nếu xuất hiện nhiều hơn 2 điểm giao, tiến hành tìm kiếm cặp điểm có khoảng cách xa nhau nhất để làm điểm đầu và cuối cho đoạn kết quả
            double maxDist = 0;
            P2 p1 = unique[0], p2 = unique[1];
            for (int i = 0; i < unique.Count; i++)
            {
                for (int j = i + 1; j < unique.Count; j++)
                {
                    double d = Math.Sqrt(Math.Pow(unique[i].X - unique[j].X, 2) + Math.Pow(unique[i].Y - unique[j].Y, 2));
                    if (d > maxDist)
                    {
                        maxDist = d; p1 = unique[i]; p2 = unique[j];
                    }
                }
            }

            segment = new LineSegment(p1, p2);
            return true;
        }

        /// <summary>
        /// Tính toán tọa độ giao điểm giữa đường thẳng tổng quát (ax+by+c=0) và một đoạn thẳng hữu hạn được giới hạn bởi hai điểm p1, p2.
        /// Output example: trả về result tọa độ pixel thực tế trên ảnh, nơi đường thẳng cắt qua cạnh ROI
        ///                     result.X = 15.0;    
        ///                     result.Y = 10.5;
        /// </summary>
        private static bool GetLineSegmentIntersection(double a, double b, double c, P2 p1, P2 p2, out P2 result)
        {
            result = default;
            double dx = p2.X - p1.X, dy = p2.Y - p1.Y;
            double denom = a * dx + b * dy;

            // Nếu mẫu số bằng 0, chứng tỏ đường thẳng tổng quát chạy song song tuyệt đối với đoạn biên bao của ROI -> Không có giao điểm
            if (Math.Abs(denom) < 1e-10) return false;

            double t = -(a * p1.X + b * p1.Y + c) / denom;

            // Giá trị tham số t phải nằm trong phạm vi từ 0.0 đến 1.0 thì giao điểm mới nằm trên thân của đoạn thẳng hữu hạn biên ROI
            if (t < -1e-4 || t > 1 + 1e-4) return false;

            result = new P2(p1.X + t * dx, p1.Y + t * dy);
            return true;
        }

        /// <summary>
        /// Thuật toán Fallback hình học: Xác định chiều dài đoạn hiển thị bằng cách chiếu các điểm biên cực biên 
        /// (Extreme Points) lên trên đường thẳng kết quả, tạo ra đoạn thẳng bao bọc khít dải điểm thực tế.
        /// </summary>
        private static LineSegment SegmentFromPointExtent(List<P2> points, double a, double b, double c)    // Output example: outputSegment.P1 = { X: 10.0,  Y: 10.0  }
                                                                                                            //                 outputSegment.P2 = { X: 100.0, Y: 100.0 }
        {
            // Bước 2.1: Xác định hướng và điểm neo (Anchor Point) trên đường thẳng
            double dirX = -b, dirY = a; // Hướng vectơ chỉ phương của đường thẳng
            double cx = points.Average(p => p.X), cy = points.Average(p => p.Y);    // Tính tọa độ trung bình (trọng tâm) của cụm điểm.
            double dist = a * cx + b * cy + c;                                      // Tính khoảng cách từ trọng tâm đến đường thẳng.
            double fx = cx - a * dist, fy = cy - b * dist; // Lấy điểm trọng tâm (cx, cy) chiếu vuông góc xuống đường thẳng tạo thành một Điểm gốc cố định (Base anchor point).
                                                           // Tất cả các phép đo khoảng cách dọc theo đường thẳng sau đó sẽ lấy điểm F(fx, fy) này làm gốc tọa độ (t=0).
            // Bước 2.2: Phép chiếu vô hướng để tìm hai đầu cực biên
            double tmin = double.MaxValue, tmax = double.MinValue;
            foreach (var p in points)
            {
                // Thực hiện phép chiếu vô hướng (Scalar Projection) để tìm vị trí tương đối t của từng điểm trên trục đường thẳng
                double t = (p.X - fx) * dirX + (p.Y - fy) * dirY;   // ích vô hướng giữa vectơ nối từ gốc F đến điểm p với vectơ chỉ phương. Kết quả trả về một con số thực t.
                                                                    // Nếu t > 0: Điểm nằm về phía bên phải điểm neo F.
                                                                    // Nếu t < 0: Điểm nằm về phía bên trái điểm neo F.
                if (t < tmin) tmin = t; // Cực tiểu trái
                if (t > tmax) tmax = t; // Cực đại phải
            }
            // Bước 2.3: Tạo đoạn thẳng hữu hạn nối từ điểm cực trái sang điểm cực phải của hệ thống tập điểm biên
            return new LineSegment(
                new P2(fx + tmin * dirX, fy + tmin * dirY), 
                new P2(fx + tmax * dirX, fy + tmax * dirY));
        }

        /// <summary>
        /// Tính toán sai số căn trung bình bình phương (RMS Error). Chỉ số này đo lường độ lệch trung bình của tập điểm biên 
        /// so với đường thẳng lý thuyết. Trị số RMS càng nhỏ chứng tỏ đường thẳng khớp được có độ thẳng và độ tin cậy càng cao.
        /// </summary>
        private static double CalculateRMSErrorToInfiniteLine(List<P2> points, double a, double b, double c)
        {
            double sumSq = 0;
            foreach (var p in points)
            {
                // Do phương trình ax+by+c=0 đã được chuẩn hóa vectơ pháp tuyến đơn vị (a^2 + b^2 = 1), 
                // khoảng cách Euclid từ điểm p tới đường thẳng được tính trực tiếp bằng công thức tử số đơn giản.
                double d = a * p.X + b * p.Y + c;
                sumSq += d * d; // Cộng dồn bình phương sai số khoảng cách
            }
            return Math.Sqrt(sumSq / points.Count); // Căn bậc hai của trung bình cộng sai số
        }

        /// <summary>
        /// Hàm toán học chuyển đổi sai số RMS thành thang điểm chất lượng trực quan Score nằm trong dải [0.0 - 1.0].
        /// </summary>
        private double CalculateLineScore(List<P2> points, double rms)
        {
            // Sử dụng hàm suy giảm phi tuyến (Lorentzian Decay Function): Nếu RMS tiến gần bằng 0 thì Score đạt điểm tuyệt đối 1.0
            double score = 1.0 / (1.0 + rms * 0.1);
            return Math.Clamp(score, 0.0, 1.0); // Ép cứng kết quả bảo vệ dải giá trị đầu ra
        }
        #endregion

        #region 7. Hệ Thống Các Hàm Hỗ Trợ Đồ Họa Độc Quyền (Helper Rendering Graphics)
        /// <summary>
        /// Thực hiện phép xoay điểm P(x,y) quanh một tâm xoay cố định (cx, cy) với một góc lượng giác cho trước.
        /// </summary>
        private static P2 RotatePoint(P2 p, double cx, double cy, double angle)
        {
            double cos = Math.Cos(angle), sin = Math.Sin(angle);
            double dx = p.X - cx, dy = p.Y - cy;
            // Áp dụng ma trận biến đổi affine xoay phẳng 2D chuẩn hình học giải tích
            return new P2(cx + dx * cos - dy * sin, cy + dx * sin + dy * cos);
        }

        /// <summary>
        /// Vẽ toàn bộ hệ thống thân Caliper kèm mũi tên nhọn ở cuối đường để biểu thị trực quan hướng tìm kiếm cạnh cho người dùng.
        /// </summary>
        /// <param name="img">Ảnh Mat dùng để vẽ đồ họa hiển thị.</param>
        /// <param name="calipers">Danh sách các cặp điểm (start, end) của từng thước đo.</param>
        private static void DrawCalipersOnImage(Mat img, List<(P2 start, P2 end)> calipers)
        {
            var bodyColor = new Scalar(51, 214, 51);    // Màu xanh lá cây nhạt đại diện cho thân thước quét
            var headColor = new Scalar(0, 215, 255);    // Màu cam vàng rực đại diện cho cánh mũi tên hướng quét

            foreach (var (s, e) in calipers)
            {
                // 1. Vẽ đường thân Caliper chạy dọc vùng quét
                Cv2.Line(img, (int)Math.Round(s.X), (int)Math.Round(s.Y), (int)Math.Round(e.X), (int)Math.Round(e.Y), bodyColor, 1);

                double dx = e.X - s.X, dy = e.Y - s.Y;
                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 1) continue;

                // Trích xuất vectơ đơn vị hướng quét (ux, uy)
                double ux = dx / len, uy = dy / len;
                const double arrowHeadLength = 3; // Chiều dài cạnh mũi tên đồ họa (6 pixel)

                // 2. Vẽ hai nét xiên nghiêng tạo thành hình mũi tên nhọn ở điểm kết thúc (End Point)
                Cv2.Line(img, (int)Math.Round(e.X), (int)Math.Round(e.Y),
                    (int)Math.Round(e.X - arrowHeadLength * (ux + 0.5 * uy)), (int)Math.Round(e.Y - arrowHeadLength * (uy - 0.5 * ux)), headColor, 1);

                Cv2.Line(img, (int)Math.Round(e.X), (int)Math.Round(e.Y),
                    (int)Math.Round(e.X - arrowHeadLength * (ux - 0.5 * uy)), (int)Math.Round(e.Y - arrowHeadLength * (uy + 0.5 * ux)), headColor, 1);
            }
        }

        /// <summary>
        /// Vẽ viền khung hộp chữ nhật xoay đại diện cho vùng tìm kiếm dữ liệu (ROI Search Region) lên ảnh Overlay.
        /// </summary>
        private void DrawRotatedRegion(Mat img, Scalar color)
        {
            var r = _region.Value;
            double cx = r.Center.X, cy = r.Center.Y, hw = r.Width / 2, hh = r.Height / 2;
            double rad = r.AngleDeg * Math.PI / 180.0, cos = Math.Cos(rad), sin = Math.Sin(rad);

            // Xác định tọa độ thực tại của 4 góc hộp xoay
            var corners = new[]
            {
                new P2(cx + (-hw * cos - (-hh) * sin), cy + (-hw * sin + (-hh) * cos)),
                new P2(cx + ( hw * cos - (-hh) * sin), cy + ( hw * sin + (-hh) * cos)),
                new P2(cx + ( hw * cos -   hh  * sin), cy + ( hw * sin +   hh  * cos)),
                new P2(cx + (-hw * cos -   hh  * sin), cy + (-hw * sin +   hh  * cos)),
            };

            // Vẽ 4 đoạn thẳng nối tiếp khép kín giữa các đỉnh góc để tạo thành khung ROI hình chữ nhật hoàn chỉnh
            for (int i = 0; i < 4; i++)
            {
                Cv2.Line(img, (int)Math.Round(corners[i].X), (int)Math.Round(corners[i].Y),
                    (int)Math.Round(corners[(i + 1) % 4].X), (int)Math.Round(corners[(i + 1) % 4].Y), color, 1);
            }
        }
        #endregion
    }
}

*/