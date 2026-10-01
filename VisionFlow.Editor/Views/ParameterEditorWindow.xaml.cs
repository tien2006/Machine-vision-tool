using System.Globalization; // Nhập thư viện hỗ trợ định dạng/parse số theo vùng miền (dùng InvariantCulture cho ô nhập số)
using System.Windows; // Nhập thư viện cơ bản cho WPF (Window, Point, RoutedEventArgs...)
using System.Windows.Controls; // Nhập các điều khiển UI trong WPF (Canvas, Rectangle...)
using System.Windows.Input; // Nhập thư viện xử lý sự kiện chuột và phím
using System.Windows.Media; // Nhập thư viện xử lý đồ họa, màu sắc và biến đổi (Brush, Transform...)
using System.Windows.Shapes; // Nhập thư viện vẽ hình học vector (Rectangle, Ellipse, Line...)
using VisionFlow.Core.Models; // Nhập các model dữ liệu hình học lõi (Point2d, RotatedRectRegion, CircleRegion...)
using VisionFlow.Editor.ViewModels; // Nhập không gian tên chứa ParameterEditorWindowViewModel

namespace VisionFlow.Editor.Views // Định nghĩa không gian tên chứa các View giao diện
{
    /// <summary>
    /// Cửa sổ cấu hình node — bố cục mô phỏng VisionPlatform: tham số (tab category) bên trái, ảnh lớn
    /// (<see cref="ImageViewer"/> có thước/zoom/pan/pixel-grid) + status bar bên phía phải, top bar Run/Cancel/Apply.
    /// </summary>
    public partial class ParameterEditorWindow : Window // Lớp code-behind điều khiển cửa sổ chỉnh sửa tham số Node
    {
        private readonly ParameterEditorWindowViewModel _vm; // Tham chiếu đến ViewModel quản lý dữ liệu cho Window
        private readonly Rectangle _roiRect; // Shape hiển thị khung ROI hình chữ nhật xoay trên Canvas
        private readonly RotateTransform _roiRotate = new(); // Đối tượng thực hiện xoay khung ROI chữ nhật
        private readonly System.Windows.Shapes.Ellipse _roiEllipse; // Shape hiển thị vùng ROI hình tròn trên Canvas

        private enum Mode { None, Draw, Move, Resize } // Định nghĩa các chế độ thao tác chuột với ROI
        private Mode _mode = Mode.None; // Biến lưu trạng thái thao tác chuột hiện tại với ROI
        private Point2d _drawStart; // Điểm bắt đầu click chuột khi vẽ mới ROI
        private Point2d _grabOffset; // Khoảng cách chênh lệch từ vị trí click đến tâm ROI khi kéo di chuyển
        private int _resizeSx, _resizeSy; // Dấu của tay nắm đang kéo tương ứng trong hệ tọa độ cục bộ của ROI (-1, 0, 1)
        private bool _suppressAngle; // Cờ chặn vòng lặp sự kiện khi cập nhật giá trị góc xoay trên slider/ô nhập số
        private bool _suppressZoom; // Cờ chặn vòng lặp sự kiện khi cập nhật giá trị zoom trên slider/ô nhập số

        // Các shape overlay động (caliper, điểm cạnh, đường fit, tay nắm) — xóa & vẽ lại mỗi lần RedrawRoi.
        private readonly List<UIElement> _dynamic = new(); // Danh sách lưu các phần tử đồ họa động được vẽ thêm lên Canvas

        // 8 tay nắm resize theo dấu (sx, sy) trong hệ trục ROI cục bộ (các góc và các cạnh).
        private static readonly (int sx, int sy)[] Handles =
            { (-1, -1), (0, -1), (1, -1), (-1, 0), (1, 0), (-1, 1), (0, 1), (1, 1) }; // Danh sách các hướng tay nắm tương đối
        private const double HandleHit = 9.0; // Bán kính vùng bắt điểm cho phép click trúng tay nắm (tính theo pixel màn hình)

        /// <summary>
        /// Khởi tạo cửa sổ ParameterEditorWindow với ViewModel đi kèm.
        /// </summary>
        /// <param name="vm">ViewModel quản lý dữ liệu và logic cửa sổ</param>
        public ParameterEditorWindow(ParameterEditorWindowViewModel vm) // Hàm khởi tạo cửa sổ
        {
            InitializeComponent(); // Khởi tạo các thành phần giao diện khai báo trong XAML
            _vm = vm; // Lưu tham chiếu ViewModel
            DataContext = vm; // Gán DataContext cho Binding XAML

            _roiRect = new Rectangle // Khởi tạo đối tượng hình chữ nhật hiển thị ROI
            {
                Stroke = new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xFF)), // Viền xanh lam neon
                StrokeThickness = 2, // Độ dày viền 2px
                Fill = new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0xE5, 0xFF)), // Màu nền mờ trong suốt
                Visibility = Visibility.Collapsed, // Ban đầu ẩn đi
                IsHitTestVisible = false, // Không nhận sự kiện chuột trực tiếp để tránh cản trở tương tác Canvas
                RenderTransform = _roiRotate // Gán phép xoay cho hình chữ nhật
            };
            _roiEllipse = new System.Windows.Shapes.Ellipse // Khởi tạo đối tượng hình tròn hiển thị ROI
            {
                Stroke = new SolidColorBrush(Color.FromRgb(0x00, 0xE5, 0xFF)), // Viền xanh lam neon
                StrokeThickness = 2, // Độ dày viền 2px
                Fill = new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0xE5, 0xFF)), // Màu nền mờ trong suốt
                Visibility = Visibility.Collapsed, // Ban đầu ẩn đi
                IsHitTestVisible = false // Không nhận sự kiện chuột trực tiếp
            };
            Viewer.OverlayCanvas.Children.Add(_roiRect); // Thêm khung ROI chữ nhật vào Canvas hiển thị của Viewer
            Viewer.OverlayCanvas.Children.Add(_roiEllipse); // Thêm khung ROI tròn vào Canvas hiển thị của Viewer
            Viewer.OverlayCanvas.MouseLeftButtonDown += Overlay_MouseDown; // Đăng ký sự kiện nhấn chuột trái trên Canvas
            Viewer.OverlayCanvas.MouseMove += Overlay_MouseMove; // Đăng ký sự kiện di chuyển chuột trên Canvas
            Viewer.OverlayCanvas.MouseLeftButtonUp += Overlay_MouseUp; // Đăng ký sự kiện thả chuột trái trên Canvas
            Viewer.TransformChanged += (_, _) => RedrawRoi(); // Khi góc nhìn/pan/zoom thay đổi -> Vẽ lại ROI tương ứng
            Viewer.ZoomChanged += (_, _) => UpdateZoomUi(); // Khi tỉ lệ zoom thay đổi -> Cập nhật thông tin lên UI
            Viewer.CursorMoved += (_, e) => UpdateStatus(e.image, e.pixel); // Khi con trỏ di chuyển trên ảnh -> Cập nhật tọa độ và giá trị pixel

            Loaded += (_, _) => // Sự kiện khi cửa sổ hoàn tất hiển thị
            {
                _vm.Initialize(); // Khởi tạo dữ liệu bất đồng bộ trong ViewModel
                if (_vm.HasRoi) // Nếu node có sử dụng ROI
                {
                    _suppressAngle = true; // Bật cờ ngắt sự kiện slider/textbox góc
                    AngleSlider.Value = _vm.Roi.AngleDeg; // Đồng bộ giá trị góc xoay ban đầu lên Slider
                    // MỚI: đồng bộ luôn ô nhập số góc ngay từ lúc mở cửa sổ, tránh hiển thị rỗng/lệch với Slider
                    AngleTextBox.Text = _vm.Roi.AngleDeg.ToString("0.##", CultureInfo.InvariantCulture);
                    _suppressAngle = false; // Tắt cờ ngắt sự kiện
                }
                UpdateZoomUi(); // Cập nhật thông tin thu phóng ban đầu
                RedrawRoi(); // Vẽ lại ROI ban đầu
            };
            Closed += (_, _) => _vm.Dispose(); // Khi đóng cửa sổ -> Giải phóng tài nguyên ViewModel
        }

        // ---- Zoom / status ----

        private void UpdateZoomUi() // Cập nhật trạng thái hiển thị của các điều khiển thu phóng
        {
            _suppressZoom = true; // Bật cờ ngắt sự kiện để tránh gọi vòng lặp OnZoomSliderChanged / OnZoomTextChanged
            ZoomSlider.Value = Math.Clamp(Viewer.Zoom, ZoomSlider.Minimum, ZoomSlider.Maximum); // Giới hạn và gán giá trị Zoom vào Slider
            ZoomText.Text = $"{Viewer.ZoomPercent:F0}%"; // Hiển thị phần trăm thu phóng (giờ TextBox có thể gõ được, nhưng vẫn set .Text bình thường ở đây)
            _suppressZoom = false; // Tắt cờ ngắt sự kiện
            SizeLabel.Text = Viewer.ImagePixelWidth > 0 // Hiển thị kích thước ảnh (Chiều rộng x Chiều cao)
                ? $"Size: {Viewer.ImagePixelWidth} × {Viewer.ImagePixelHeight}"
                : "Size: —";
        }

        private void UpdateStatus(Point image, string pixel) // Cập nhật thông tin vị trí con trỏ chuột và giá trị điểm ảnh
        {
            if (double.IsNaN(image.X)) // Nếu vị trí nằm ngoài phạm vi ảnh
            {
                PosLabel.Text = "Position: —"; // Đặt lại vị trí rỗng
                PixelLabel.Text = "Pixel: —"; // Đặt lại giá trị pixel rỗng
            }
            else // Nếu nằm trên ảnh hợp lệ
            {
                PosLabel.Text = $"Position: {image.X:0}, {image.Y:0}"; // Hiển thị tọa độ X, Y của pixel
                PixelLabel.Text = $"Pixel: {pixel}"; // Hiển thị giá trị màu/xám của pixel
            }
        }

        private void OnZoomSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e) // Sự kiện khi kéo Slider điều chỉnh Zoom
        {
            if (_suppressZoom || Viewer is null) return; // Nếu đang cập nhật từ code hoặc Viewer chưa sẵn sàng thì dừng
            Viewer.SetZoom(e.NewValue); // Thiết lập tỉ lệ Zoom mới cho Viewer -> sẽ tự kích hoạt Viewer.ZoomChanged -> UpdateZoomUi() đồng bộ lại ZoomText
        }

        // ===== MỚI: Ô nhập số Zoom (%) — cho phép gõ trực tiếp thay vì chỉ kéo Slider =====

        /// <summary>Kích hoạt khi rời khỏi ô ZoomText (click ra ngoài / Tab sang control khác).</summary>
        private void OnZoomTextChanged(object sender, RoutedEventArgs e) => ApplyZoomText();
        // Dùng LostFocus (thay vì TextChanged) để tránh cố gắng parse số ngay khi người dùng đang gõ dở dang
        // (ví dụ mới gõ được "1" của "150", nếu parse ngay sẽ áp luôn 1% rồi lại nhảy về UI gây giật hình).

        /// <summary>Kích hoạt khi người dùng nhấn phím trong ô ZoomText — bắt riêng phím Enter để áp dụng ngay.</summary>
        private void OnZoomTextKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) // Chỉ xử lý khi nhấn Enter, các phím khác (số, dấu chấm...) để TextBox tự nhận bình thường
            {
                ApplyZoomText(); // Áp giá trị vừa gõ ngay lập tức
                Keyboard.ClearFocus(); // Bỏ focus khỏi ô (giống hành vi quen thuộc của các phần mềm khác khi Enter)
            }
        }

        /// <summary>Đọc chuỗi trong ZoomText, parse thành % rồi áp vào Viewer; nếu gõ sai thì khôi phục hiển thị cũ.</summary>
        private void ApplyZoomText()
        {
            if (_suppressZoom) return; // Nếu đang trong lượt code tự gán giá trị (từ UpdateZoomUi) thì bỏ qua, tránh vòng lặp

            string text = ZoomText.Text.Trim().TrimEnd('%', ' '); // Cho phép người dùng gõ có hoặc không có ký hiệu %, ví dụ "150" hoặc "150%" đều được
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double percent))
            {
                // Gõ sai định dạng (chữ, ký tự lạ...) -> không crash, chỉ khôi phục lại đúng % hiện tại của Viewer
                UpdateZoomUi();
                return;
            }

            // Chuyển từ % (VD 150) sang hệ số Zoom thực (VD 1.5), rồi kẹp trong khoảng Min/Max của ZoomSlider
            // (0.05 - 20, tức 5% - 2000%) để không set ra giá trị vô lý (âm hoặc quá lớn làm treo render).
            double zoom = Math.Clamp(percent / 100.0, ZoomSlider.Minimum, ZoomSlider.Maximum);
            Viewer.SetZoom(zoom); // Áp dụng zoom mới -> Viewer.ZoomChanged sẽ tự bắn ra -> UpdateZoomUi() đồng bộ lại cả Slider và TextBox
        }

        private void OnFit(object sender, RoutedEventArgs e) => Viewer.ZoomToFit(); // Sự kiện bấm nút Fit -> Tự động căn chỉnh ảnh vừa khung nhìn

        // ---- ROI ----

        private void RedrawRoi() // Phương thức vẽ lại toàn bộ các đối tượng ROI và Overlay trên Canvas
        {
            _roiRect.Visibility = Visibility.Collapsed; // Tạm thời ẩn khung ROI chữ nhật
            _roiEllipse.Visibility = Visibility.Collapsed; // Tạm thời ẩn khung ROI tròn
            foreach (var el in _dynamic) Viewer.OverlayCanvas.Children.Remove(el); // Xóa toàn bộ các nét vẽ overlay động cũ khỏi Canvas
            _dynamic.Clear(); // Xóa danh sách lưu overlay động
            if (!_vm.HasRoi || _vm.BackgroundImage is null) return; // Nếu node không hỗ trợ ROI hoặc chưa có ảnh nền thì dừng

            if (_vm.RoiKind == EditorRoiKind.Rect) // Trường hợp ROI dạng hình chữ nhật xoay
            {
                var r = _vm.Roi; // Lấy dữ liệu vùng ROI chữ nhật hiện tại
                var center = Viewer.ImageToScreen(new Point(r.Center.X, r.Center.Y)); // Chuyển tọa độ tâm từ ảnh sang tọa độ màn hình
                double dw = r.Width * Viewer.Zoom, dh = r.Height * Viewer.Zoom; // Tính chiều rộng/cao hiển thị theo tỉ lệ Zoom
                Canvas.SetLeft(_roiRect, center.X - dw / 2); // Căn lề trái cho Rectangle
                Canvas.SetTop(_roiRect, center.Y - dh / 2); // Căn lề trên cho Rectangle
                _roiRect.Width = Math.Max(0, dw); // Cập nhật chiều rộng hình chữ nhật
                _roiRect.Height = Math.Max(0, dh); // Cập nhật chiều cao hình chữ nhật
                _roiRotate.CenterX = dw / 2; // Đặt tâm xoay X ở giữa Rectangle
                _roiRotate.CenterY = dh / 2; // Đặt tâm xoay Y ở giữa Rectangle
                _roiRotate.Angle = r.AngleDeg; // Đặt góc xoay cho phép biến đổi
                _roiRect.Visibility = Visibility.Visible; // Hiển thị khung ROI chữ nhật

                // Kết quả (caliper + điểm cạnh + đường) đã được tool vẽ sẵn vào ảnh OUTPUT đang hiển thị,
                // nên lớp WPF chỉ cần khung ROI + tay nắm để chỉnh (tránh vẽ trùng, tránh nhiễu khi kéo).
                DrawHandles(r); // Vẽ 8 tay nắm điều chỉnh kích thước cho ROI
            }
            else if (_vm.RoiKind == EditorRoiKind.Circle) // Trường hợp ROI dạng hình tròn
            {
                var c = _vm.RoiCircle; // Lấy dữ liệu vùng ROI tròn hiện tại
                var center = Viewer.ImageToScreen(new Point(c.Center.X, c.Center.Y)); // Chuyển tâm sang tọa độ màn hình
                double rPix = c.Radius * Viewer.Zoom; // Tính bán kính hiển thị theo tỉ lệ Zoom
                Canvas.SetLeft(_roiEllipse, center.X - rPix); // Căn lề trái cho Ellipse
                Canvas.SetTop(_roiEllipse, center.Y - rPix); // Căn lề trên cho Ellipse
                _roiEllipse.Width = Math.Max(0, 2 * rPix); // Cập nhật chiều rộng (đường kính)
                _roiEllipse.Height = Math.Max(0, 2 * rPix); // Cập nhật chiều cao (đường kính)
                _roiEllipse.Visibility = Visibility.Visible; // Hiển thị khung ROI tròn
            }
        }

        // ---- Overlay vector (kiểu Cognex/VisionPlatform) ----

        private void AddOverlay(UIElement el) // Phương thức phụ trợ thêm đối tượng đồ họa vào danh sách quản lý động
        {
            Viewer.OverlayCanvas.Children.Add(el); // Thêm phần tử UI vào Canvas
            _dynamic.Add(el); // Lưu vào danh sách để xóa khi vẽ lại
        }

        private void AddLine(Point a, Point b, Brush stroke, double thickness, DoubleCollection? dash = null) // Phương thức bổ trợ vẽ đường thẳng overlay
        {
            var line = new Line // Khởi tạo Line WPF
            {
                X1 = a.X,
                Y1 = a.Y,
                X2 = b.X,
                Y2 = b.Y, // Đặt tọa độ điểm đầu và điểm cuối
                Stroke = stroke,
                StrokeThickness = thickness,
                IsHitTestVisible = false // Đặt màu, độ dày viền và bỏ bắt sự kiện chuột
            };
            if (dash is not null) line.StrokeDashArray = dash; // Nếu có cấu hình nét đứt -> Gán mẫu nét đứt
            AddOverlay(line); // Thêm đường thẳng vào overlay
        }

        private void DrawCalipers() // Phương thức vẽ các vạch đo Caliper
        {
            if (!_vm.HasCalipers) return; // Nếu node không hỗ trợ Caliper thì dừng
            var calipers = _vm.CaliperSegments(); // Lấy danh sách đoạn thẳng Caliper từ ViewModel
            if (calipers.Count == 0) return; // Nếu danh sách rỗng thì dừng

            // Bảo vệ hiệu năng: rất nhiều caliper thì vẽ thưa.
            int step = calipers.Count > 300 ? (int)Math.Ceiling(calipers.Count / 300.0) : 1; // Nếu trên 300 vạch -> Giảm mật độ vẽ
            var body = new SolidColorBrush(Color.FromArgb(0xCC, 0x33, 0xD6, 0x33));   // Màu xanh lá cho thân vạch Caliper
            var head = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xD7, 0x00));   // Màu vàng cho mũi tên chỉ hướng tìm kiếm

            for (int i = 0; i < calipers.Count; i += step) // Duyệt qua danh sách Caliper theo bước nhảy step
            {
                var (sImg, eImg) = calipers[i]; // Tọa độ điểm đầu/cuối trên hệ ảnh
                var s = Viewer.ImageToScreen(new Point(sImg.X, sImg.Y)); // Chuyển điểm đầu sang tọa độ màn hình
                var e = Viewer.ImageToScreen(new Point(eImg.X, eImg.Y)); // Chuyển điểm cuối sang tọa độ màn hình
                AddLine(s, e, body, 1); // Vẽ thân vạch Caliper

                // Mũi tên ở đầu caliper → chỉ hướng quét (start→end).
                double dx = e.X - s.X, dy = e.Y - s.Y, len = Math.Sqrt(dx * dx + dy * dy); // Tính độ dài vạch trên màn hình
                if (len < 1) continue; // Nếu vạch quá ngắn thì bỏ qua mũi tên
                double ux = dx / len, uy = dy / len; // Vector đơn vị chỉ hướng vạch
                const double h = 6; // Độ dài cánh mũi tên
                AddLine(e, new Point(e.X - h * (ux + 0.5 * uy), e.Y - h * (uy - 0.5 * ux)), head, 1.5); // Vẽ nhánh mũi tên trái
                AddLine(e, new Point(e.X - h * (ux - 0.5 * uy), e.Y - h * (uy + 0.5 * ux)), head, 1.5); // Vẽ nhánh mũi tên phải
            }
        }

        private void DrawResults() // Phương thức vẽ các kết quả xử lý (điểm cạnh, đường thẳng)
        {
            // Điểm cạnh tìm được.
            var edgeBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x00, 0xFF, 0x66)); // Màu xanh lục tươi cho điểm cạnh
            foreach (var p in _vm.ResultEdgePoints()) // Duyệt qua danh sách điểm cạnh phát hiện
            {
                var sp = Viewer.ImageToScreen(new Point(p.X, p.Y)); // Chuyển tọa độ sang màn hình
                var dot = new Ellipse // Tạo chấm tròn nhỏ đánh dấu điểm
                {
                    Width = 5,
                    Height = 5,
                    Fill = edgeBrush,
                    IsHitTestVisible = false // Bán kính 2.5px, không bắt tương tác chuột
                };
                Canvas.SetLeft(dot, sp.X - 2.5); // Định vị tâm chấm theo X
                Canvas.SetTop(dot, sp.Y - 2.5); // Định vị tâm chấm theo Y
                AddOverlay(dot); // Thêm chấm vào overlay
            }

            // Đường fit — cam nếu OK, đỏ nếu NG (vẫn vẽ để thấy fit tới đâu).
            if (_vm.ResultLine() is { } line) // Nếu tìm được đường thẳng khớp kết quả
            {
                var a = Viewer.ImageToScreen(new Point(line.p1.X, line.p1.Y)); // Chuyển điểm đầu sang màn hình
                var b = Viewer.ImageToScreen(new Point(line.p2.X, line.p2.Y)); // Chuyển điểm cuối sang màn hình
                var brush = line.ok // Đánh giá kết quả để chọn màu
                    ? new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xA5, 0x00)) // Màu cam nếu OK
                    : new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0x30, 0x30)); // Màu đỏ nếu NG
                AddLine(a, b, brush, 2.5); // Vẽ đường thẳng fit kết quả lên màn hình
            }
        }

        private void DrawHandles(RotatedRectRegion r) // Phương thức vẽ 8 tay nắm điểm kéo xung quanh ROI chữ nhật
        {
            var fill = new SolidColorBrush(Colors.White); // Nền màu trắng cho nút nắm
            var stroke = new SolidColorBrush(Color.FromRgb(0x00, 0x99, 0xCC)); // Viền màu xanh dương
            foreach (var (sx, sy) in Handles) // Duyệt qua 8 hướng tay nắm
            {
                var img = HandleImagePoint(r, sx, sy); // Tính tọa độ điểm nắm trên hệ ảnh
                var sp = Viewer.ImageToScreen(new Point(img.X, img.Y)); // Chuyển sang tọa độ màn hình
                var box = new Rectangle // Khởi tạo hình ô vuông tay nắm
                {
                    Width = 8,
                    Height = 8,
                    Fill = fill,
                    Stroke = stroke,
                    StrokeThickness = 1, // Kích thước 8x8 px
                    IsHitTestVisible = false // Không nhận tương tác trực tiếp
                };
                Canvas.SetLeft(box, sp.X - 4); // Căn lề trái sao cho vị trí nằm chính giữa điểm nắm
                Canvas.SetTop(box, sp.Y - 4); // Căn lề trên sao cho vị trí nằm chính giữa điểm nắm
                AddOverlay(box); // Thêm hình vuông tay nắm vào overlay
            }
        }

        // Vì một hàm không thể return trực tiếp 2 vector cùng lúc, tác giả đã dùng mẹo kết hợp:
        //  * Trả về vector $u$ thông qua kiểu trả về của hàm: (double X, double Y).
        //  * Trả về vector $v$ thông qua các tham số truyền tham chiếu out double vx, out double vy.
        // -> Ý nghĩa toán học bên trong hàm:
        //  * return (Math.Cos(a), Math.Sin(a));: Đây là tọa độ của vector $u$ (trục X sau khi xoay góc $a$).
        //  * vx = -Math.Sin(a); vy = Math.Cos(a);: Đây là tọa độ của vector $v$ (trục Y sau khi xoay góc $a$).
        private static (double X, double Y) Axes(double angleDeg, out double vx, out double vy) // Tính toán các vector trục đơn vị u và v từ góc xoay
        {
            double a = angleDeg * Math.PI / 180.0; // Chuyển đổi góc từ độ sang radian
            vx = -Math.Sin(a); vy = Math.Cos(a);     // Trục v (hướng theo chiều cao Height)
            return (Math.Cos(a), Math.Sin(a));        // Trục u (hướng theo chiều rộng Width)
        }

        private static Point2d HandleImagePoint(RotatedRectRegion r, int sx, int sy) // Tính tọa độ tuyệt đối của một tay nắm cụ thể trên hệ trục ảnh
        {
            var (ux, uy) = Axes(r.AngleDeg, out double vx, out double vy); // Lấy vector chỉ hướng 2 trục
            double hw = r.Width / 2, hh = r.Height / 2; // Bán chiều rộng và bán chiều cao
            return new Point2d(
                r.Center.X + sx * hw * ux + sy * hh * vx, // Tọa độ X tương ứng hướng tay nắm
                r.Center.Y + sx * hw * uy + sy * hh * vy); // Tọa độ Y tương ứng hướng tay nắm
        }

        private Point2d ToImage(MouseEventArgs e) // Chuyển đổi vị trí con trỏ chuột trên Canvas sang tọa độ ảnh tuyệt đối
        {
            var p = Viewer.ScreenToImage(e.GetPosition(Viewer.OverlayCanvas)); // Chuyển đổi thông qua hàm của Viewer
            return new Point2d(p.X, p.Y); // Trả về dạng Point2d
        }

        private void Overlay_MouseDown(object sender, MouseButtonEventArgs e) // Xử lý sự kiện nhấn chuột trên Canvas để tương tác ROI
        {
            if (!_vm.HasRoi) return; // Nếu không có ROI thì ngưng
            var p = ToImage(e); // Lấy vị trí click trên hệ tọa độ ảnh

            if (_vm.RoiKind == EditorRoiKind.Rect) // Thao tác với ROI hình chữ nhật xoay
            {
                var r = _vm.Roi; // Lấy dữ liệu ROI

                // 1) Tay nắm resize (ưu tiên cao nhất) — bắt theo toạ độ màn hình.
                var screen = e.GetPosition(Viewer.OverlayCanvas); // Lấy vị trí click trên hệ màn hình
                foreach (var (sx, sy) in Handles) // Duyệt thử qua 8 tay nắm
                {
                    var hImg = HandleImagePoint(r, sx, sy); // Lấy vị trí tay nắm trên hệ ảnh
                    var hScreen = Viewer.ImageToScreen(new Point(hImg.X, hImg.Y)); // Chuyển vị trí tay nắm sang màn hình
                    if ((hScreen - screen).Length <= HandleHit) // Nếu khoảng cách click đủ gần tay nắm
                    {
                        _mode = Mode.Resize; _resizeSx = sx; _resizeSy = sy; // Chuyển sang chế độ thay đổi kích thước (Resize)
                        Viewer.OverlayCanvas.CaptureMouse(); // Bắt giữ sự kiện chuột cho Canvas
                        return; // Kết thúc không xét tiếp
                    }
                }

                // 2) Trong khung → di chuyển; ngoài khung → vẽ mới.
                bool inside = InsideRoi(p, r); // Kiểm tra click nằm trong hay ngoài khung ROI
                if (inside) { _mode = Mode.Move; _grabOffset = new Point2d(p.X - r.Center.X, p.Y - r.Center.Y); } // Nằm trong -> Chuyển chế độ di chuyển (Move)
                else { _mode = Mode.Draw; _drawStart = p; } // Nằm ngoài -> Chuyển chế độ vẽ mới (Draw)
            }
            else if (_vm.RoiKind == EditorRoiKind.Circle) // Thao tác với ROI hình tròn
            {
                var c = _vm.RoiCircle; // Lấy dữ liệu ROI tròn
                double d = Dist(p, c.Center); // Tính khoảng cách từ vị trí click đến tâm hình tròn
                // Gần tâm → di chuyển; còn lại → đặt bán kính
                if (d < Math.Max(8, c.Radius * 0.35)) { _mode = Mode.Move; _grabOffset = new Point2d(p.X - c.Center.X, p.Y - c.Center.Y); } // Gần tâm -> Chế độ di chuyển
                else _mode = Mode.Draw; // Ở xa tâm -> Chế độ thay đổi bán kính/vẽ mới
            }
            Viewer.OverlayCanvas.CaptureMouse(); // Giữ quyền bắt sự kiện chuột
        }

        /// <summary>Kiểm tra điểm nằm trong ROI chữ nhật xoay (đưa về hệ trục cục bộ).</summary>
        private static bool InsideRoi(Point2d p, RotatedRectRegion r) // Hàm kiểm tra một điểm có thuộc vùng ROI xoay hay không
        {
            var (ux, uy) = Axes(r.AngleDeg, out double vx, out double vy); // Lấy vector trục
            double dx = p.X - r.Center.X, dy = p.Y - r.Center.Y; // Tính độ lệch tọa độ từ tâm
            double lu = dx * ux + dy * uy, lv = dx * vx + dy * vy; // Chiếu độ lệch lên hệ trục u, v cục bộ của ROI
            return Math.Abs(lu) <= r.Width / 2 && Math.Abs(lv) <= r.Height / 2; // Kiểm tra nếu khoảng cách chiếu nhỏ hơn bán chiều rộng/chiều cao
        }

        private void Overlay_MouseMove(object sender, MouseEventArgs e) // Xử lý sự kiện rê chuột để kéo/vẽ ROI
        {
            if (_mode == Mode.None) return; // Nếu không ở chế độ thao tác nào thì dừng
            var p = ToImage(e); // Lấy vị trí con trỏ hiện tại trên hệ ảnh

            if (_vm.RoiKind == EditorRoiKind.Rect) // Khi làm việc với ROI hình chữ nhật
            {
                var r = _vm.Roi;
                if (_mode == Mode.Draw) // Chế độ vẽ mới
                    _vm.Roi = new RotatedRectRegion(new Point2d((_drawStart.X + p.X) / 2, (_drawStart.Y + p.Y) / 2),
                        Math.Abs(p.X - _drawStart.X), Math.Abs(p.Y - _drawStart.Y), r.AngleDeg); // Tạo ROI mới với tâm ở giữa vị trí click ban đầu và vị trí hiện tại
                else if (_mode == Mode.Move) // Chế độ di chuyển ROI
                    _vm.Roi = new RotatedRectRegion(new Point2d(p.X - _grabOffset.X, p.Y - _grabOffset.Y), r.Width, r.Height, r.AngleDeg); // Giữ nguyên kích thước, cập nhật tâm mới
                else if (_mode == Mode.Resize) // Chế độ kéo thay đổi kích thước
                    _vm.Roi = ResizeRoi(r, p, _resizeSx, _resizeSy); // Tính toán hình chữ nhật ROI mới qua hàm ResizeRoi
            }
            else if (_vm.RoiKind == EditorRoiKind.Circle) // Khi làm việc với ROI hình tròn
            {
                var c = _vm.RoiCircle;
                if (_mode == Mode.Move) // Di chuyển hình tròn
                    _vm.RoiCircle = new CircleRegion(new Point2d(p.X - _grabOffset.X, p.Y - _grabOffset.Y), c.Radius); // Giữ bán kính, đổi tâm
                else // Draw = đặt bán kính theo khoảng cách tới tâm
                    _vm.RoiCircle = new CircleRegion(c.Center, Dist(p, c.Center)); // Giữ tâm, cập nhật bán kính theo vị trí con trỏ chuột
            }
            RedrawRoi(); // Vẽ lại giao diện ROI ngay lập tức khi chuột di chuyển
        }

        /// <summary>Resize ROI bằng cách kéo tay nắm (sx,sy): điểm/cạnh đối diện được neo cố định. Hỗ trợ ROI xoay.</summary>
        private static RotatedRectRegion ResizeRoi(RotatedRectRegion r, Point2d p, int sx, int sy) // Hàm tính toán kích thước ROI mới khi kéo tay nắm điểm góc/cạnh
        {
            var (ux, uy) = Axes(r.AngleDeg, out double vx, out double vy); // Lấy vector trục chỉ hướng u, v
            double hw = r.Width / 2, hh = r.Height / 2; // Bán chiều rộng và bán chiều cao ban đầu

            // Neo = điểm đối diện tay nắm đang kéo (giữ cố định khi resize).
            double ax = r.Center.X - sx * hw * ux - sy * hh * vx; // Tọa độ điểm neo X
            double ay = r.Center.Y - sx * hw * uy - sy * hh * vy; // Tọa độ điểm neo Y

            double pax = p.X - ax, pay = p.Y - ay; // Khoảng cách từ vị trí chuột tới điểm neo
            double du = pax * ux + pay * uy;     // Chiếu độ dài lên trục u
            double dv = pax * vx + pay * vy;     // Chiếu độ dài lên trục v

            double newHw = sx != 0 ? Math.Max(2, Math.Abs(du) / 2) : hw; // Bán chiều rộng mới (tối thiểu là 2px)
            double newHh = sy != 0 ? Math.Max(2, Math.Abs(dv) / 2) : hh; // Bán chiều cao mới (tối thiểu là 2px)

            double cu = sx != 0 ? Math.Sign(du) * newHw : 0; // Khoảng dịch chuyển tâm theo trục u
            double cv = sy != 0 ? Math.Sign(dv) * newHh : 0; // Khoảng dịch chuyển tâm theo trục v
            double ncx = ax + cu * ux + cv * vx; // Tọa độ tâm X mới
            double ncy = ay + cu * uy + cv * vy; // Tọa độ tâm Y mới

            return new RotatedRectRegion(new Point2d(ncx, ncy), 2 * newHw, 2 * newHh, r.AngleDeg); // Trả về cấu trúc ROI đã thay đổi kích thước
        }

        private static double Dist(Point2d a, Point2d b) // Hàm phụ trợ tính khoảng cách Euclide giữa hai điểm
            => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

        private void Overlay_MouseUp(object sender, MouseButtonEventArgs e) // Xử lý sự kiện thả chuột trái
        {
            if (_mode == Mode.None) return; // Nếu không thao tác gì thì dừng
            _mode = Mode.None; // Reset chế độ về None
            Viewer.OverlayCanvas.ReleaseMouseCapture(); // Giải phóng quyền bắt giữ sự kiện chuột
            _vm.RunNodeCommand.Execute(null); // Thực thi lệnh chạy thử Node trong ViewModel ngay khi vừa thả chuột điều chỉnh xong ROI
            RedrawRoi(); // Vẽ lại ROI
        }

        private void OnAngleChanged(object sender, RoutedPropertyChangedEventArgs<double> e) // Sự kiện khi người dùng kéo Slider xoay góc ROI
        {
            if (_suppressAngle || _vm is null || !_vm.HasRoi) return; // Kiểm tra cờ ngắt và tính hợp lệ
            var r = _vm.Roi;
            _vm.Roi = new RotatedRectRegion(r.Center, r.Width, r.Height, e.NewValue); // Cập nhật góc xoay mới cho ROI

            // MỚI: đồng bộ ngược giá trị số sang AngleTextBox mỗi khi Slider đổi (do kéo tay hoặc do code gán)
            _suppressAngle = true; // Bật cờ để lệnh set .Text dưới đây KHÔNG kích hoạt lại OnAngleTextChanged
            AngleTextBox.Text = e.NewValue.ToString("0.##", CultureInfo.InvariantCulture); // Định dạng tối đa 2 chữ số thập phân, gọn cho ô nhỏ
            _suppressAngle = false; // Tắt cờ ngay sau khi set xong

            RedrawRoi(); // Vẽ lại ROI theo góc mới
        }

        // ===== MỚI: Ô nhập số góc (độ) — cho phép gõ chính xác thay vì chỉ kéo Slider =====

        /// <summary>Kích hoạt khi rời khỏi ô AngleTextBox (click ra ngoài / Tab sang control khác).</summary>
        private void OnAngleTextChanged(object sender, RoutedEventArgs e) => ApplyAngleTextBox();
        // Dùng LostFocus thay vì TextChanged vì lý do tương tự Zoom: tránh parse lỡ dở khi đang gõ
        // (ví dụ đang gõ "-1" của "-15", ký tự "-" một mình chưa phải số hợp lệ).

        /// <summary>Kích hoạt khi người dùng nhấn phím trong ô AngleTextBox — bắt riêng Enter để áp dụng ngay.</summary>
        private void OnAngleTextKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) // Chỉ can thiệp khi Enter, để các phím số/dấu trừ/dấu chấm gõ bình thường
            {
                ApplyAngleTextBox(); // Áp giá trị ngay
                Keyboard.ClearFocus(); // Bỏ focus, kích hoạt luôn hiệu ứng "chốt" giá trị giống nhiều phần mềm khác
            }
        }

        /// <summary>Đọc chuỗi trong AngleTextBox, chuẩn hoá về [-180,180], áp vào Slider + ROI; nếu gõ sai thì khôi phục.</summary>
        private void ApplyAngleTextBox()
        {
            if (_suppressAngle || _vm is null || !_vm.HasRoi) return; // Đang trong lượt code tự gán, hoặc chưa sẵn sàng -> bỏ qua

            if (!double.TryParse(AngleTextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double newAngle))
            {
                // Gõ sai định dạng (chữ, ký tự lạ, để trống...) -> không crash, khôi phục lại đúng giá trị hiện tại của Slider
                AngleTextBox.Text = AngleSlider.Value.ToString("0.##", CultureInfo.InvariantCulture);
                return;
            }

            // Chuẩn hoá góc về khoảng [-180, 180) theo kiểu "vòng tròn" (modulo), KHÔNG dùng Math.Clamp.
            // Lý do: nếu người dùng gõ 200°, Clamp sẽ cắt cứng về 180° (SAI Ý NGHĨA HÌNH HỌC, vì 200° và 180°
            // là 2 hướng khác nhau). Công thức dưới đây tự động quy 200° -> -160° (đúng vị trí góc thực tế),
            // giữ nguyên ý nghĩa vật lý của góc xoay thay vì chỉ chặn biên một cách máy móc.
            newAngle = ((newAngle + 180.0) % 360.0 + 360.0) % 360.0 - 180.0;

            _suppressAngle = true; // Chặn để việc set Slider.Value dưới đây không gọi ngược lại OnAngleChanged -> ApplyAngleTextBox
            AngleSlider.Value = newAngle; // Đồng bộ Slider theo giá trị vừa gõ
            AngleTextBox.Text = newAngle.ToString("0.##", CultureInfo.InvariantCulture); // Ghi lại giá trị đã chuẩn hoá (phòng trường hợp vừa quy đổi 200->-160)
            _suppressAngle = false; // Mở lại cờ

            // Vì OnAngleChanged bị chặn bởi cờ ở trên nên KHÔNG tự cập nhật _vm.Roi — phải tự làm ở đây:
            var r = _vm.Roi;
            _vm.Roi = new RotatedRectRegion(r.Center, r.Width, r.Height, newAngle); // Ghi góc mới vào ROI thật (tự động gói lại đúng kiểu TemplateImageRef nếu cần, xem property Roi trong ViewModel)
            RedrawRoi(); // Vẽ lại khung ROI theo góc mới ngay lập tức
        }

        // ---- Buttons ----

        private void OnRunClicked(object sender, RoutedEventArgs e) => _vm.RunNodeCommand.Execute(null); // Bấm nút Run -> Gọi Command thực thi node

        private void OnCancelClicked(object sender, RoutedEventArgs e) // Bấm nút Cancel
        {
            _vm.Revert(); // Hoàn tác lại giá trị tham số ban đầu
            Close(); // Đóng cửa sổ
        }

        private void OnCloseClicked(object sender, RoutedEventArgs e) => Close(); // Bấm nút Apply/Close -> Đóng cửa sổ và giữ nguyên giá trị mới

        /// <summary>Chọn tab (0 = Image Preview, 1 = Data Output).</summary>
        public void SelectTab(int index) => RightTabs.SelectedIndex = index; // Chuyển đổi tab hiển thị ở panel phải

        private void OnCopyJson(object sender, RoutedEventArgs e) // Sự kiện bấm nút sao chép JSON kết quả
        {
            try { Clipboard.SetText(_vm.OutputJson); } catch { /* clipboard busy */ } // Sao chép chuỗi OutputJson vào Clipboard của Windows
        }
    }
}