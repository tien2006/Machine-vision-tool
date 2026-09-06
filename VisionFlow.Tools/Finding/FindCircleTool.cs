// ==================== Vai trò chính:                TRÁI TIM thuật toán: các công cụ đo lường/tìm kiếm hình học kiểu công nghiệp (Caliper)
// ==================== Thành phần / Class tiêu biểu: CaliperUtil (helper), FindCircleTool, FindLineTool
// ==================== Phụ thuộc vào:                OpenCvSharp + Core.Models
// ==================== Pattern / Kỹ thuật nổi bật:   Sub-pixel edge detection, RANSAC, Circle/Line fitting

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Models;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;
using P2 = VisionFlow.Core.Models.Point2d; // Định danh ngắn gọn cho Point2D để tính toán hình học tiện lợi

namespace VisionFlow.Tools.Finding
{
    /// <summary>
    /// Công cụ tìm kiếm đường tròn theo phương pháp Caliper:
    /// 1. Rải N thước đo (Caliper) hướng tâm/ly tâm xung quanh vùng tròn kỳ vọng.
    /// 2. Tìm kiếm điểm cạnh biên dựa trên độ biến thiên mức xám (gradient) và cực tính (polarity).
    /// 3. Lọc bỏ các điểm nhiễu ngoại lai (Outliers) dựa trên thuật toán lọc thống kê.
    /// 4. Khớp (Fit) đường tròn bằng phương pháp đại số Kasa và xuất kết quả.
    /// </summary>
    [ToolMetadata("FindCircle", DisplayName = "Find Circle", Category = "Detection", Description = "Caliper-based circle finder: radial calipers + edge detection + circle fit.")]
    public sealed class FindCircleTool : VisionTool // Lớp kín (sealed) thực thi công cụ tìm đường tròn, kế thừa từ VisionTool
    {
        #region 1. Khai báo các Cổng truyền nhận dữ liệu (Ports)
        private readonly InputPort<IVisionImage> _input; // Cổng vào: Nhận ảnh gốc cần xử lý từ luồng dữ liệu
        private readonly OutputPort<IVisionImage> _outImage; // Cổng ra: Xuất ảnh kết quả có vẽ đè các đồ họa (Overlay)
        private readonly OutputPort<CircleResult> _outCircle; // Cổng ra: Cung cấp thông tin hình học đường tròn tìm được (Tâm, bán kính) và trạng thái Judge
        private readonly OutputPort<P2[]> _outEdges; // Cổng ra: Xuất mảng các tọa độ điểm cạnh sub-pixel tìm thấy
        private readonly OutputPort<double> _outRms; // Cổng ra: Xuất sai số bình phương trung bình (RMS) của phép khớp hình học
        private readonly OutputPort<double> _outScore; // Cổng ra: Xuất điểm số đánh giá chất lượng độ tin cậy của đường tròn (0.0 -> 1.0)
        #endregion

        #region 2. Khai báo các Tham số cấu hình (Parameters)
        // Nhóm vùng tìm kiếm (Region)
        private readonly ToolParameter<CircleRegion> _region; // Tham số lưu trữ thông tin vùng tròn tìm kiếm mẫu do người dùng cấu hình
        private readonly ToolParameter<bool> _useImageCenter; // Tham số cờ bật/tắt: Tự động dùng tâm ảnh làm tâm vùng tìm kiếm
        private readonly ToolParameter<double> _minRadius; // Ngưỡng bán kính tối thiểu hợp lệ của đường tròn kết quả
        private readonly ToolParameter<double> _maxRadius; // Ngưỡng bán kính tối đa hợp lệ của đường tròn kết quả

        // Nhóm thuật toán rải Caliper (Detection)
        private readonly ToolParameter<int> _numCalipers; // Số lượng thước kẹp (đường quét thẳng) rải xung quanh đường tròn kì vọng
        private readonly ToolParameter<double> _caliperLength; // Chiều dài của mỗi thước kẹp quét cắt ngang đường biên kì vọng
        private readonly ToolParameter<bool> _radialOutward; // Hướng quét của Caliper: true là từ trong tâm ra ngoài, false là từ ngoài vào tâm

        // Nhóm ngưỡng lọc và phân tích cạnh (Threshold)
        private readonly ToolParameter<double> _edgeThreshold; // Ngưỡng độ dốc gradient tối thiểu để chấp nhận một điểm là điểm cạnh
        private readonly ToolParameter<string> _edgePolarity; // Cực tính chuyển màu biên mong muốn (Tối sang Sáng, Sáng sang Tối hoặc Mọi hướng)
        private readonly ToolParameter<int> _edgeFilterWidth; // Biên an toàn bỏ qua các pixel sát cạnh rìa ảnh
        private readonly ToolParameter<int> _minEdgePoints; // Số lượng điểm cạnh tối thiểu phải bắt được để đủ điều kiện khớp đường tròn
        private readonly ToolParameter<double> _maxRms; // Ngưỡng sai số RMS tối đa cho phép, nếu vượt quá sẽ đánh giá kết quả là NG
        private readonly ToolParameter<double> _minScore; // Ngưỡng điểm chất lượng tối thiểu để chấp nhận kết quả đường tròn đạt OK

        // Nhóm lọc nâng cao (Advanced)
        private readonly ToolParameter<bool> _outlierRejection; // Bật/tắt thuật toán tự động nhận diện và loại bỏ các điểm cạnh nhiễu ngoại lai
        private readonly ToolParameter<double> _outlierThreshold; // Hệ số nhân khoảng cách RMSE dùng làm bộ lọc dải loại bỏ Outlier

        // Nhóm hiển thị đồ họa (Display)
        private readonly ToolParameter<bool> _drawFitted; // Bật/tắt vẽ đường tròn và tâm kết quả cuối cùng lên hình ảnh
        private readonly ToolParameter<bool> _drawRegion; // Bật/tắt hiển thị vòng tròn xanh chỉ thị vùng tìm kiếm kì vọng ban đầu
        private readonly ToolParameter<bool> _drawEdges; // Bật/tắt vẽ các chấm điểm cạnh màu xanh lá bắt được trên ảnh
        #endregion

        public FindCircleTool()
        {
            // Khởi tạo các cổng vào/ra gọn gàng
            _input = AddInput<IVisionImage>("Image", "Image"); // Tạo cổng vào tên "Image"
            _outImage = AddOutput<IVisionImage>("Image", "Overlay"); // Tạo cổng xuất ảnh vẽ đè đồ họa
            _outCircle = AddOutput<CircleResult>("Circle", "Circle"); // Tạo cổng xuất cấu trúc kết quả đường tròn
            _outEdges = AddOutput<P2[]>("EdgePoints", "Edge Points"); // Tạo cổng xuất mảng tập điểm biên
            _outRms = AddOutput<double>("RMSError", "RMS Error"); // Tạo cổng xuất giá trị lỗi RMS
            _outScore = AddOutput<double>("Score", "Score"); // Tạo cổng xuất giá trị điểm số chất lượng

            // Sử dụng AddParameter và AddChoiceParameter để vừa khởi tạo, vừa đăng ký tham số vào hệ thống
            _region = AddParameter<CircleRegion>("Region", new CircleRegion(new P2(150, 150), 100), "Search Region", category: "Region", order: 1, interaction: ParameterInteraction.CircleRegion); // Đăng ký tham số vùng tìm kiếm trực quan trên UI
            _useImageCenter = AddParameter<bool>("UseImageCenter", false, "Use Image Center", category: "Region", order: 2); // Khởi tạo cờ dùng tâm ảnh mặc định là false
            _minRadius = AddParameter<double>("MinRadius", 5.0, "Min Radius", min: 1.0, max: 10000.0, category: "Region", order: 3); // Cấu hình giới hạn nhập bán kính min từ 1.0 đến 10000.0
            _maxRadius = AddParameter<double>("MaxRadius", 5000.0, "Max Radius", min: 1.0, max: 10000.0, category: "Region", order: 4); // Cấu hình giới hạn nhập bán kính max tương tự

            _numCalipers = AddParameter<int>("NumberOfCalipers", 24, "Number Of Calipers", min: 3, max: 360, category: "Detection", order: 1); // Đặt mặc định rải 24 thước kẹp quanh vòng tròn
            _caliperLength = AddParameter<double>("CaliperLength", 30.0, "Caliper Length", min: 4.0, max: 500.0, category: "Detection", order: 2); // Chiều dài mặc định dải quét là 30 pixel
            _radialOutward = AddParameter<bool>("RadialSearchDirection", true, "Search Outward", category: "Detection", order: 3); // Mặc định hướng quét ly tâm từ trong ra ngoài

            _edgeThreshold = AddParameter<double>("EdgeThreshold", 20.0, "Edge Threshold", min: 1.0, max: 255.0, category: "Threshold", order: 1); // Cấu hình ngưỡng nhạy gradient mặc định là 20.0

            // Sử dụng AddChoiceParameter chuyên dụng cho các tham số kiểu ComboBox thả xuống
            _edgePolarity = AddChoiceParameter("EdgePolarity", "Either", new[] { "DarkToLight", "LightToDark", "Either" }, "Edge Polarity", category: "Threshold", order: 2); // Tạo danh sách lựa chọn cực tính chuyển mức xám

            _edgeFilterWidth = AddParameter<int>("EdgeFilterWidth", 1, "Edge Filter Width", min: 0, max: 20, category: "Threshold", order: 3); // Độ rộng biên an toàn mặc định là 1 pixel
            _minEdgePoints = AddParameter<int>("MinEdgePoints", 6, "Min Edge Points", min: 3, max: 360, category: "Threshold", order: 4); // Yêu cầu tối thiểu phải bắt được 6 điểm cạnh trở lên
            _maxRms = AddParameter<double>("MaxRMSError", 5.0, "Max RMS Error", min: 0.1, max: 100.0, category: "Threshold", order: 5); // Giới hạn sai lệch RMS tối đa cho phép là 5.0 pixel
            _minScore = AddParameter<double>("MinScore", 0.3, "Min Score", min: 0.0, max: 1.0, category: "Threshold", order: 6); // Điểm chất lượng tối thiểu đạt OK là 0.3

            _outlierRejection = AddParameter<bool>("OutlierRejection", true, "Outlier Rejection", category: "Advanced", order: 1); // Mặc định tự động bật bộ lọc nhiễu Outlier
            _outlierThreshold = AddParameter<double>("OutlierThreshold", 2.0, "Outlier Threshold", min: 0.5, max: 10.0, category: "Advanced", order: 2); // Hệ số dải lọc mặc định bằng 2.0 lần RMS

            _drawFitted = AddParameter<bool>("DrawFittedCircle", true, "Draw Fitted Circle", category: "Display", order: 1); // Bật mặc định vẽ đường tròn kết quả màu vàng
            _drawRegion = AddParameter<bool>("DrawSearchRegion", true, "Draw Search Region", category: "Display", order: 2); // Bật mặc định hiển thị đường biên vùng tìm kiếm mẫu
            _drawEdges = AddParameter<bool>("DrawEdgePoints", true, "Draw Edge Points", category: "Display", order: 3); // Bật mặc định vẽ các điểm chấm xanh lục tại vị trí cạnh
        }

        protected override void OnExecute(IToolContext context)
        {
            if (_input.Value == null) // Kiểm tra an toàn dữ liệu đầu vào
                throw new ArgumentNullException(nameof(_input), "Ảnh đầu vào của Tool không được rỗng!"); // Ném lỗi nếu không có dữ liệu ảnh đi vào cổng

            var src = ((MatVisionImage)_input.Value).Mat; // Ép kiểu dữ liệu ảnh từ interface sang thực thể OpenCV Mat cụ thể

            // Bước 1: Chuyển đổi và quản lý ảnh xám (giảm bớt chi phí tính toán)
            Mat gray; // Khai báo đối tượng Mat lưu trữ ảnh xám xử lý thuật toán
            bool grayOwned = false; // Biến cờ đánh dấu quyền sở hữu bộ nhớ ảnh xám để tự giải phóng cuối hàm
            if (src.Channels() == 1) // Nếu ảnh đầu vào bản chất đã là ảnh đơn kênh (Grayscale)
            {
                gray = src; // Dùng trực tiếp ảnh nguồn, không tốn tài nguyên khởi tạo mới
            }
            else
            {
                gray = new Mat(); // Khởi tạo một thực thể ma trận ảnh mới
                Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY); // Chuyển đổi từ không gian màu BGR màu sang ảnh đơn kênh xám
                grayOwned = true; // Kích hoạt cờ đánh dấu ma trận xám này do hàm tạo ra, cần giải phóng thủ công
            }

            // Tạo ảnh Overlay màu để vẽ trực quan hóa đồ họa đầu ra
            Mat overlay = new Mat(); // Khởi tạo ma trận chứa ảnh hiển thị kết quả họa đồ
            Cv2.CvtColor(gray, overlay, ColorConversionCodes.GRAY2BGR); // Nhân bản từ ảnh xám sang ảnh 3 kênh màu để vẽ các màu như vàng, xanh lục, lục lam

            // Bước 2: Xác định tâm và bán kính vùng tìm kiếm ban đầu
            var region = _region.Value; // Lấy dữ liệu cấu hình hình học của vùng tìm kiếm từ tham số
            double cx = region.Center.X; // Lấy tọa độ X của tâm vùng cấu hình
            double cy = region.Center.Y; // Lấy tọa độ Y của tâm vùng cấu hình
            if (_useImageCenter.Value) // Nếu cờ tự động căn giữa ảnh được bật lên
            {
                cx = gray.Width / 2.0; // Tọa độ X dịch về chính giữa chiều rộng ảnh
                cy = gray.Height / 2.0; // Tọa độ Y dịch về chính giữa chiều cao ảnh
            }
            double rr = region.Radius; // Lấy bán kính của vùng tìm kiếm mong muốn

            // Bước 3: Rải các thước đo Caliper hướng tâm và dò tìm điểm cạnh
            var edges = new List<P2>(); // Khởi tạo danh sách động để lưu trữ các tọa độ điểm biên tìm được
            var calipers = new List<(P2 Start, P2 End)>();  // Lưu trữ tọa độ điểm đầu và điểm cuối của từng thước đo
            int n = Math.Max(3, _numCalipers.Value); // Đảm bảo số lượng Caliper tối thiểu phải lớn hơn hoặc bằng 3 để phục vụ tính toán hình học
            double proj = _caliperLength.Value / 2.0; // Khoảng dải quét trải đều về hai phía trong và ngoài đường tròn kì vọng

            for (int i = 0; i < n; i++)
            {
                // Chia góc quét đều 360 độ quanh tâm
                double a = i * 2 * Math.PI / n; // Tính góc quét hướng tâm (Radian) của thước kẹp thứ i
                double inner = Math.Max(0, rr - proj); // Bán kính trong: giới hạn điểm bắt đầu thước quét không nhỏ hơn tâm (0)
                double outer = rr + proj; // Bán kính ngoài: điểm kết thúc thước quét kéo dài ra phía ngoài biên tròn

                var pIn = new P2(cx + inner * Math.Cos(a), cy + inner * Math.Sin(a)); // Tọa độ điểm phía bên trong lòng đường tròn
                var pOut = new P2(cx + outer * Math.Cos(a), cy + outer * Math.Sin(a)); // Tọa độ điểm phía ngoài rìa đường tròn

                // Quyết định hướng quét: Từ trong ra ngoài (Outward) hoặc từ ngoài vào trong
                var (start, end) = _radialOutward.Value ? (pIn, pOut) : (pOut, pIn); // Giải cấu trúc Tuple gán điểm bắt đầu và kết thúc quét dựa trên cấu hình
                // Ghi nhận đoạn quét bằng Tuple (P2, P2)
                calipers.Add((start, end));

                // Dò tìm điểm cạnh bằng phương pháp sub-pixel trên đường quét thẳng
                var idge = FindEdgeAlongLine(gray, start, end, _edgeThreshold.Value, _edgePolarity.Value, _edgeFilterWidth.Value); // Thực thi hàm tìm cạnh trên từng dòng quét đơn lẻ
                if (idge.HasValue) // Nếu tại đường quét này tìm thấy điểm biên hợp lệ
                {
                    edges.Add(idge.Value); // Thêm tọa độ điểm sub-pixel vào tập hợp kết quả điểm cạnh chung
                }
            }

            var result = new CircleResult { Judge = Judge.NG }; // Khởi tạo cấu trúc chứa kết quả cuối cùng với phán quyết mặc định là thất bại (NG)
            double rms = 0, score = 0; // Khởi tạo biến lưu giá trị sai số RMS và điểm số tin cậy bằng không

            // Bước 4: Fit (Khớp) đường tròn lý thuyết từ các điểm cạnh đã tìm thấy
            if (edges.Count >= _minEdgePoints.Value) // Điều kiện tiên quyết: Tập điểm cạnh bắt được phải nhiều hơn số điểm tối thiểu yêu cầu
            {
                var pts = edges; // Gán tham chiếu tập điểm sang biến xử lý tạm thời

                // Lọc bỏ nhiễu ngoại lai (các điểm bắt sai lệch do xước, nhiễu bề mặt)
                if (_outlierRejection.Value) // Nếu cấu hình cho phép lọc bỏ Outlier nâng cao
                {
                    pts = RejectOutliers(edges, _outlierThreshold.Value); // Thực thi bộ lọc thống kê loại bỏ điểm cạnh lỗi lệch chuẩn
                }

                if (pts.Count >= 3 && FitCircleKasa(pts, out var fc, out rms)) // Yêu cầu tối thiểu có 3 điểm còn lại sau lọc và khớp hình học thành công bằng thuật toán Kasa
                {
                    // Tính toán điểm đánh giá chất lượng (Score) dựa trên:
                    // 1. Tỉ lệ điểm bắt thành công (edgeRatio)
                    // 2. Độ lệch chuẩn RMS (rmsScore)
                    // 3. Kích thước bán kính có nằm trong khoảng mong muốn (radiusScore)
                    double edgeRatio = Math.Min(1.0, (double)pts.Count / n); // Tính tỉ lệ số lượng điểm bắt được trên tổng số lượng caliper triển khai
                    double rmsScore = Math.Max(0, 1.0 - rms / _maxRms.Value); // Điểm đánh giá độ lệch: lỗi RMS càng nhỏ thì điểm số càng tiệm cận 1.0
                    double radiusScore = (fc.Radius >= _minRadius.Value && fc.Radius <= _maxRadius.Value) ? 1.0 : 0.0; // Kiểm tra xem bán kính khớp được có nằm trong khoảng min-max không

                    score = Math.Clamp(0.5 * edgeRatio + 0.3 * rmsScore + 0.2 * radiusScore, 0, 1); // Trộn điểm số tổng hợp theo trọng số 5:3:2 và kẹp chặt trong khoảng [0, 1]

                    // Kiểm tra xem đường tròn kết quả có đạt các tiêu chí chất lượng tối thiểu không
                    bool valid = fc.Radius >= _minRadius.Value &&
                                 fc.Radius <= _maxRadius.Value &&
                                 rms <= _maxRms.Value &&
                                 score >= _minScore.Value; // Các ràng buộc đồng thời về bán kính, sai số và điểm số chất lượng tối thiểu

                    result.Circle = fc; // Gán dữ liệu đường tròn hình học vừa khớp được vào cấu trúc kết quả
                    result.Score = score; // Lưu điểm số tin cậy vào kết quả
                    result.Judge = valid ? Judge.OK : Judge.NG; // Quyết định phán quyết OK nếu mọi tiêu chí hợp lệ đều vượt qua, ngược lại là NG
                }
            }

            // Bước 5: Vẽ đồ họa kết quả overlay lên ảnh đầu ra
            if (_drawRegion.Value)
            {
                // Vẽ vùng tròn tìm kiếm (màu lục lam mờ)
                Cv2.Circle(overlay, (int)cx, (int)cy, (int)rr, new Scalar(255, 255, 0), 1); // Vẽ đường tròn kì vọng bằng màu Cyan (BGR: 255, 255, 0) với nét mảnh độ dày bằng 1
                                                                                            // Gọi hàm vẽ Caliper với danh sách Tuple (P2, P2)
                DrawCalipersOnImage(overlay, calipers);
            }

            if (_drawEdges.Value)
            {
                // Vẽ các điểm cạnh tìm kiếm được (màu xanh lá)
                foreach (var e in edges)
                {
                    Cv2.Circle(overlay, (int)e.X, (int)e.Y, 2, new Scalar(0, 255, 0), -1); // Vẽ dấu chấm tròn nhỏ bán kính 2 pixel lấp đầy (-1) bằng màu xanh lục lá cây tại mỗi tọa độ cạnh
                }
            }

            if (_drawFitted.Value && result.Judge == Judge.OK)
            {
                // Vẽ đường tròn kết quả khớp thành công (màu vàng) và vẽ tâm (màu đỏ)
                Cv2.Circle(overlay, (int)result.Circle.Center.X, (int)result.Circle.Center.Y, (int)result.Circle.Radius, new Scalar(0, 255, 255), 2); // Vẽ đường biên tròn kết quả bằng nét dày 2 pixel màu vàng rực
                Cv2.Circle(overlay, (int)result.Circle.Center.X, (int)result.Circle.Center.Y, 4, new Scalar(0, 0, 255), -1); // Vẽ chấm tròn đặc bán kính 4 pixel đánh dấu tọa độ tâm đường tròn bằng màu đỏ nguyên bản
            }

            // Giải phóng bộ nhớ tạm thời của ảnh xám nếu tạo mới
            if (grayOwned)
            {
                gray.Dispose(); // Gọi lệnh giải phóng bộ nhớ ma trận OpenCV ngay để tránh rò rỉ bộ nhớ (Memory Leak)
            }

            // Gán dữ liệu ra các cổng xuất đầu ra
            _outImage.Value = new MatVisionImage(overlay); // MatVisionImage nhận quyền quản lý và giải phóng 'overlay' khi hủy
            _outCircle.Value = result; // Đổ cấu trúc thông tin kết quả đo lường ra cổng _outCircle
            _outEdges.Value = edges.ToArray(); // Chuyển đổi List sang mảng tĩnh và xuất ra cổng danh sách điểm cạnh biên
            _outRms.Value = rms; // Xuất sai số RMS của phép tính toán hình học ra cổng ngoài
            _outScore.Value = score; // Xuất điểm số chất lượng của kết quả đo lường phục vụ giám sát dữ liệu

            // Log kết quả thực thi công cụ ra hệ thống điều khiển
            context.Log($"FindCircle: edges={edges.Count} judge={result.Judge} R={result.Circle.Radius:F1} rms={rms:F2} score={score:F2}"); // Ghi nhật ký chuỗi thông số đo lường định dạng hiển thị 1 đến 2 chữ số thập phân
        }

        #region Các phương thức Helper bổ trợ thuật toán

        /// <summary>
        /// Dò một điểm cạnh dọc theo đoạn thẳng Caliper bằng kỹ thuật lấy mẫu mức xám (Bilinear) 
        /// kết quả đạt độ chính xác sub-pixel thông qua nội suy Parabolic quanh đỉnh Gradient.
        /// </summary>
        private static P2? FindEdgeAlongLine(Mat gray, P2 start, P2 end, double threshold, string polarity, int margin)
        {
            double dist = Math.Sqrt((end.X - start.X) * (end.X - start.X) + (end.Y - start.Y) * (end.Y - start.Y)); // Tính chiều dài hình học thực tế của dòng quét Caliper hiện tại
            int n = (int)Math.Ceiling(dist); // Xác định số lượng điểm ảnh nguyên cần lấy mẫu dọc theo chiều dài dải thẳng
            if (n < 3) return null; // Đoạn quét quá ngắn (dưới 3 điểm) không thể thực hiện thuật toán sai phân tính toán cạnh

            // Lấy mẫu Profile độ sáng dọc theo đường quét
            var prof = new double[n]; // Tạo mảng lưu chuỗi giá trị độ xám lấy mẫu
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / (n - 1); // Xác định tỷ lệ nội suy khoảng cách tuyến tính dọc theo đường thẳng (từ 0.0 -> 1.0)
                double x = start.X + (end.X - start.X) * t; // Tọa độ X thực của điểm lấy mẫu trên ảnh
                double y = start.Y + (end.Y - start.Y) * t; // Tọa độ Y thực của điểm lấy mẫu trên ảnh
                prof[i] = SampleBilinear(gray, x, y); // Thực hiện nội suy song tuyến tính lấy mẫu mức xám thực tế từ lưới pixel ảnh -> giá trị nhận được: 0~255
            }

            int bestI = -1; // Vị trí điểm có độ dốc gradient tốt nhất, mặc định chưa tìm được (-1)
            double bestMag = 0; // Giá trị biên độ dốc tốt nhất đã được chuẩn hóa theo cực tính chuyển đổi màu
            double bestSigned = 0; // Lưu lại giá trị đạo hàm gốc có dấu tại vị trí tốt nhất đó để tính toán sub-pixel

            // Tính toán gradient mức xám dọc theo thước đo
            for (int i = 1; i < n - 1; i++)
            {
                double t = (double)i / (n - 1); // Tính lại tỷ lệ vị trí tương quan phục vụ kiểm tra biên ảnh
                double x = start.X + (end.X - start.X) * t; // Tọa độ X của điểm đang duyệt
                double y = start.Y + (end.Y - start.Y) * t; // Tọa độ Y của điểm đang duyệt

                // Bỏ qua các vị trí nằm quá sát biên ngoài ảnh để tránh lỗi biên tập ảnh
                if (x < margin || y < margin || x >= gray.Width - margin || y >= gray.Height - margin)
                    continue; // Bỏ qua điểm lấy mẫu nếu nằm trong vùng đệm biên an toàn để chống lỗi truy cập ngoài ma trận ảnh

                // Sử dụng toán tử đạo hàm trung tâm
                double g = (prof[i + 1] - prof[i - 1]) / 2.0; // Sai phân trung tâm: Tính độ dốc biến thiên mức xám cục bộ
                double signed = polarity switch // Nhánh phân tích giá trị gradient dựa trên cấu hình cực tính chuyển đổi màu sắc biên
                {
                    "DarkToLight" => g,    // Chuyển màu từ Tối sang Sáng (Gradient Dương): Giữ nguyên dấu
                    "LightToDark" => -g,   // Chuyển màu từ Sáng sang Tối (Gradient Âm): Đảo ngược dấu để tìm giá trị cực đại
                    _ => Math.Abs(g)       // Nhận mọi sự thay đổi: Lấy trị tuyệt đối của độ dốc bất kể hướng biến đổi
                };

                if (signed > threshold && signed > bestMag) // Điều kiện: vượt ngưỡng nhạy cấu hình và sắc nét hơn đỉnh cạnh tìm thấy trước đó
                {
                    bestMag = signed; // Cập nhật biên độ dốc tối ưu mới
                    bestI = i; // Ghi nhận chỉ số mảng tại vị trí đỉnh cạnh tạm thời này
                    bestSigned = g; // Lưu giữ giá trị độ dốc có dấu gốc phục vụ nội suy Parabol
                }
            }

            if (bestI < 1 || bestI >= n - 1) return null; // Trả về null nếu đỉnh cạnh nằm ở rìa mảng, không đủ lân cận để thực hiện nội suy mịn

            // Áp dụng phép nội suy Parabolic để tăng độ chính xác tìm biên lên hàng Sub-pixel
            double sub = bestI; // Khởi tạo vị trí sub-pixel mịn bằng chỉ số nguyên tốt nhất ban đầu
            double gm1 = Math.Abs((prof[bestI] - prof[bestI - 2 < 0 ? 0 : bestI - 2]) / 2.0); // Tính độ dốc gradient ở điểm lân cận phía trước (cách 1 bước đạo hàm rộng)
            double g0 = Math.Abs(bestSigned); // Giá trị độ lớn dốc gradient ngay tại đỉnh nguyên tốt nhất hiện tại
            double gp1 = Math.Abs((prof[Math.Min(n - 1, bestI + 2)] - prof[bestI]) / 2.0); // Tính độ dốc gradient ở điểm lân cận phía sau (cách 1 bước đạo hàm rộng)
            double denom = gm1 - 2 * g0 + gp1; // Tính toán giá trị đạo hàm bậc hai làm mẫu số cho phương trình tìm đỉnh Parabol

            if (Math.Abs(denom) > 1e-6) // Kiểm tra an toàn ma trận: Tránh phép toán chia cho 0 nếu mẫu số quá bé sát không
            {
                sub = bestI + 0.5 * (gm1 - gp1) / denom; // Phương thức giải hệ tìm đỉnh Parabol: Tinh chỉnh chỉ số mảng sang giá trị số thực mịn
            }
            sub = Math.Clamp(sub, 0, n - 1); // Bảo đảm chỉ số sub-pixel tinh chỉnh xong không vượt quá giới hạn mảng đo

            double tt = sub / (n - 1); // Quy đổi chỉ số vị trí số thực sang tỷ lệ độ dài tuyến tính mới (0.0 -> 1.0)
            return new P2(start.X + (end.X - start.X) * tt, start.Y + (end.Y - start.Y) * tt); // Nội suy trả về tọa độ điểm cạnh sub-pixel thực trên lưới ảnh
        }

        /// <summary>
        /// Lấy mẫu nội suy song tuyến tính (Bilinear Interpolation) mức xám tại tọa độ thực (double) trên ảnh.
        /// </summary>
        private static double SampleBilinear(Mat gray, double x, double y)
        {
            int x0 = (int)Math.Floor(x); // Làm tròn xuống phần nguyên của tọa độ thực X để xác định vị trí pixel bên trái
            int y0 = (int)Math.Floor(y); // Làm tròn xuống phần nguyên của tọa độ thực Y để xác định vị trí pixel phía trên

            // Kiểm tra điều kiện biên, nếu nằm ngoài ảnh thực hiện kẹp biên Pixel gần nhất
            if (x0 < 0 || y0 < 0 || x0 >= gray.Width - 1 || y0 >= gray.Height - 1)
            {
                int cxp = Math.Clamp(x0, 0, gray.Width - 1); // Ràng buộc tọa độ X nằm gọn trong biên chiều rộng ảnh hợp lệ
                int cyp = Math.Clamp(y0, 0, gray.Height - 1); // Ràng buộc tọa độ Y nằm gọn trong biên chiều cao ảnh hợp lệ
                return gray.At<byte>(cyp, cxp); // Trả về luôn giá trị pixel xám nguyên thủy tại điểm kẹp biên gần nhất
            }

            double fx = x - x0; // Tính toán độ lệch phần thập phân theo phương ngang trục X (khoảng cách trọng số)
            double fy = y - y0; // Tính toán độ lệch phần thập phân theo phương dọc trục Y (khoảng cách trọng số)

            double i00 = gray.At<byte>(y0, x0); // Trích xuất mức xám pixel góc trên-bên trái (dòng trước cột sau)
            double i10 = gray.At<byte>(y0, x0 + 1); // Trích xuất mức xám pixel góc trên-bên phải liền kề
            double i01 = gray.At<byte>(y0 + 1, x0); // Trích xuất mức xám pixel góc dưới-bên trái liền kề
            double i11 = gray.At<byte>(y0 + 1, x0 + 1); // Trích xuất mức xám pixel góc dưới-bên phải liền kề

            // Công thức nội suy song tuyến tính
            return i00 * (1 - fx) * (1 - fy) +
                   i10 * fx * (1 - fy) +
                   i01 * (1 - fx) * fy +
                   i11 * fx * fy; // Tính tổng trung bình trọng số của 4 ô pixel lân cận dựa trên khoảng cách phần thập phân
        }

        /// <summary>
        /// Bộ lọc loại bỏ nhiễu ngoại lai (Outlier Rejection):
        /// Fit thử 1 đường tròn sơ bộ, tính toán khoảng cách từ các điểm cạnh tới biên đường tròn vừa fit,
        /// giữ lại những điểm nằm trong khoảng dải nhân RMSE cho phép.
        /// </summary>
        private static List<P2> RejectOutliers(List<P2> pts, double k)
        {
            if (pts.Count < 4 || !FitCircleKasa(pts, out var c, out var rms) || rms <= 0) // Kiểm tra điều kiện lọc: Cần tối thiểu 4 điểm để thực hiện loại bỏ phần tử nhiễu
                return pts; // Trả về luôn tập điểm gốc nếu không đủ dữ liệu đầu vào hoặc phép fit sơ bộ thất bại

            var kept = new List<P2>(); // Khởi tạo danh sách chứa các điểm cạnh đạt yêu cầu bộ lọc thống kê
            foreach (var p in pts)
            {
                double d = Math.Sqrt((p.X - c.Center.X) * (p.X - c.Center.X) + (p.Y - c.Center.Y) * (p.Y - c.Center.Y)); // Tính khoảng cách hình học từ điểm hiện tại tới tâm đường tròn sơ bộ
                if (Math.Abs(d - c.Radius) <= k * rms) // Điều kiện lọc: Độ lệch khoảng cách tới biên tròn nằm trong dải sai số cho phép (k lần RMS)
                {
                    kept.Add(p); // Giữ lại điểm hợp lệ này đưa vào danh sách lọc sạch
                }
            }

            // Trả về tập điểm mới nếu lọc không quá gắt (còn tối thiểu 3 điểm để fit hình học)
            return kept.Count >= 3 ? kept : pts; // Phòng hờ: Nếu bộ lọc quá gắt làm mất sạch điểm (dưới 3 điểm), phục hồi trả lại tập điểm gốc để tránh lỗi tính toán
        }

        /// <summary>
        /// Thuật toán khớp đường tròn đại số Kasa thông qua giải phương trình chính tắc hệ tuyến tính 3x3.
        /// Trả về đối tượng Circle và sai số bình phương trung bình (RMS).
        /// </summary>
        private static bool FitCircleKasa(List<P2> pts, out Circle circle, out double rms)
        {
            circle = default; // Khởi tạo tham số đầu ra đường tròn bằng giá trị mặc định của cấu trúc
            rms = double.MaxValue; // Khởi tạo sai số RMS đầu ra bằng giá trị lớn nhất của kiểu thực double
            int n = pts.Count; // Lấy số lượng phần tử điểm cạnh có trong danh sách
            if (n < 3) return false; // Không thể khớp đường tròn nếu có ít hơn 3 điểm hình học độc lập

            double Sx = 0, Sy = 0, Sxx = 0, Syy = 0, Sxy = 0, Sxz = 0, Syz = 0, Sz = 0; // Khởi tạo các biến tích lũy tổng các bậc đại số phục vụ lập ma trận hệ phương trình
            foreach (var p in pts)
            {
                double x = p.X; // Lấy tọa độ X của điểm đang xét
                double y = p.Y; // Lấy tọa độ Y của điểm đang xét
                double z = x * x + y * y; // Tính tổng bình phương tọa độ (bậc hai) của điểm hiện tại
                Sx += x; // Tích lũy tổng X
                Sy += y; // Tích lũy tổng Y
                Sxx += x * x; // Tích lũy tổng X bình phương
                Syy += y * y; // Tích lũy tổng Y bình phương
                Sxy += x * y; // Tích lũy tổng tích chéo X*Y
                Sxz += x * z; // Tích lũy tổng tích chéo X*Z
                Syz += y * z; // Tích lũy tổng tích chéo Y*Z
                Sz += z; // Tích lũy tổng hệ số bậc hai Z
            }

            // Giải hệ phương trình tuyến tính 3 ẩn: [Sxx Sxy Sx; Sxy Syy Sy; Sx Sy n] · [A; B; C] = [Sxz; Syz; Sz]
            double[,] m = { { Sxx, Sxy, Sx }, { Sxy, Syy, Sy }, { Sx, Sy, n } }; // Thiết lập ma trận vuông các hệ số 3x3
            double[] v = { Sxz, Syz, Sz }; // Thiết lập vectơ cột các hằng số tự do vế phải
            if (!Solve3(m, v, out var sol)) return false; // Gọi hàm phụ giải hệ phương trình tuyến tính 3 ẩn, trả về false nếu hệ vô nghiệm/suy biến

            double a = sol[0] / 2.0; // Giải mã ẩn số 1: Tìm tọa độ X của tâm đường tròn lý thuyết
            double b = sol[1] / 2.0; // Giải mã ẩn số 2: Tìm tọa độ Y của tâm đường tròn lý thuyết
            double r2 = sol[2] + a * a + b * b; // Giải mã ẩn số 3: Tính giá trị bình phương bán kính đường tròn
            if (r2 <= 0) return false; // Bán kính bình phương phải lớn hơn không, ngược lại là hình học không hợp lệ

            double r = Math.Sqrt(r2); // Lấy căn bậc hai tính ra giá trị bán kính thực thực thể hình học
            circle = new Circle(new P2(a, b), r); // Khởi tạo thực thể cấu trúc đường tròn mới với tâm (a,b) và bán kính r

            // Tính sai số Root Mean Square (RMS) của tập điểm so với biên tròn lý thuyết mới khớp được
            double se = 0; // Biến tích lũy tổng bình phương các sai lệch khoảng cách biên hình học
            foreach (var p in pts)
            {
                double d = Math.Sqrt((p.X - a) * (p.X - a) + (p.Y - b) * (p.Y - b)) - r; // Khoảng cách từ điểm cạnh thực tế đến đường biên tròn lý thuyết vừa tìm được
                se += d * d; // Cộng dồn bình phương khoảng cách lệch sai số
            }
            rms = Math.Sqrt(se / n); // Tính căn bậc hai của trung bình lỗi tích lũy để có giá trị sai số RMS thực tế cuối cùng
            return true; // Xác nhận quá trình khớp dữ liệu và tính sai số hoàn thành mỹ mãn
        }

        /// <summary>
        /// Giải hệ phương trình tuyến tính 3 phương trình 3 ẩn bằng phương pháp ma trận Cramer.
        /// </summary>
        private static bool Solve3(double[,] m, double[] v, out double[] x)
        {
            x = new double[3]; // Cấp phát mảng chứa 3 phần tử ẩn số nghiệm đầu ra
            double det =
                m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1]) -
                m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0]) +
                m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]); // Tính định thức định danh tổng quát (Determinant) của ma trận hệ số 3x3 bằng quy tắc Sarrus

            if (Math.Abs(det) < 1e-9) return false; // Tránh lỗi chia cho 0 khi ma trận suy biến (hệ phương trình không có nghiệm duy nhất)

            double Dx =
                v[0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1]) -
                m[0, 1] * (v[1] * m[2, 2] - m[1, 2] * v[2]) +
                m[0, 2] * (v[1] * m[2, 1] - m[1, 1] * v[2]); // Tính định thức phụ Dx bằng cách thế cột hằng số v vào cột số 1 của ma trận m

            double Dy =
                m[0, 0] * (v[1] * m[2, 2] - m[1, 2] * v[2]) -
                v[0] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0]) +
                m[0, 2] * (m[1, 0] * v[2] - v[1] * m[2, 0]); // Tính định thức phụ Dy bằng cách thế cột hằng số v vào cột số 2 của ma trận m

            double Dz =
                m[0, 0] * (m[1, 1] * v[2] - v[1] * m[2, 1]) -
                m[0, 1] * (m[1, 0] * v[2] - v[1] * m[2, 0]) +
                v[0] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]); // Tính định thức phụ Dz bằng cách thế cột hằng số v vào cột số 3 của ma trận m

            x[0] = Dx / det; // Tính giá trị ẩn nghiệm thứ nhất dựa theo quy tắc Cramer
            x[1] = Dy / det; // Tính giá trị ẩn nghiệm thứ hai dựa theo quy tắc Cramer
            x[2] = Dz / det; // Tính giá trị ẩn nghiệm thứ ba dựa theo quy tắc Cramer
            return true; // Xác nhận tìm nghiệm thành công
        }

        /// <summary>
        /// Vẽ danh sách thước đo Caliper kèm mũi tên hướng quét ở điểm kết thúc (End).
        /// </summary>
        private static void DrawCalipersOnImage(Mat overlay, IEnumerable<(P2 Start, P2 End)> calipers)
        {
            var bodyColor = new Scalar(255, 190, 0);  // Màu thân thước
            var headColor = new Scalar(0, 255, 255);  // Màu mũi tên

            const double arrowHeadLength = 3; // Chiều dài cánh mũi tên

            foreach (var (start, end) in calipers)
            {
                // 1. Vẽ thân thước Caliper
                Cv2.Line(overlay,
                    (int)Math.Round(start.X), (int)Math.Round(start.Y),
                    (int)Math.Round(end.X), (int)Math.Round(end.Y),
                    bodyColor, 1);

                double dx = end.X - start.X, dy = end.Y - start.Y;
                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 1) continue;

                // Trích xuất vectơ đơn vị hướng quét (ux, uy)
                double ux = dx / len, uy = dy / len;

                // 2. Vẽ hai cánh mũi tên ở điểm kết thúc (End)
                Cv2.Line(overlay,
                    (int)Math.Round(end.X), (int)Math.Round(end.Y),
                    (int)Math.Round(end.X - arrowHeadLength * (ux + 0.5 * uy)),
                    (int)Math.Round(end.Y - arrowHeadLength * (uy - 0.5 * ux)),
                    headColor, 1);

                Cv2.Line(overlay,
                    (int)Math.Round(end.X), (int)Math.Round(end.Y),
                    (int)Math.Round(end.X - arrowHeadLength * (ux - 0.5 * uy)),
                    (int)Math.Round(end.Y - arrowHeadLength * (uy + 0.5 * ux)),
                    headColor, 1);
            }
        }
        #endregion
    }
}