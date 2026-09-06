using System.Globalization; // Nhập thư viện hỗ trợ định dạng số theo chuẩn văn hóa quốc tế (CultureInfo.InvariantCulture)
using System.Windows; // Nhập thư viện cơ bản cho WPF (Point, DependencyProperty, SizeChangedEventArgs...)
using System.Windows.Controls; // Nhập các điều khiển UI trong WPF (UserControl, Canvas, Image, GridLength...)
using System.Windows.Input; // Nhập thư viện xử lý sự kiện tương tác chuột và bàn phím (MouseWheelEventArgs, Cursors...)
using System.Windows.Media; // Nhập thư viện xử lý đồ họa, màu sắc và biến đổi (TransformGroup, ScaleTransform, Brush...)
using System.Windows.Media.Imaging; // Nhập thư viện xử lý ảnh hiển thị WPF (BitmapSource, FormatConvertedBitmap...)
using System.Windows.Shapes; // Nhập thư viện vẽ hình học vector (Line...)

namespace VisionFlow.Editor.Views // Định nghĩa không gian tên chứa các View giao diện
{
    /// <summary>
    /// Control hiển thị ảnh công nghiệp (học từ VisionPlatform): zoom-to-cursor bằng con lăn, pan bằng
    /// kéo chuột (giữa/phải), thước ngang–dọc theo toạ độ pixel, lưới pixel khi phóng lớn, và một lớp
    /// <see cref="OverlayCanvas"/> để vẽ ROI/kết quả theo toạ độ ảnh (redraw qua <see cref="TransformChanged"/>).
    /// </summary>
    public partial class ImageViewer : UserControl // Điều khiển ImageViewer kế thừa từ UserControl
    {
        private double _zoom = 1, _offsetX, _offsetY; // Các biến lưu trữ tỉ lệ phóng to (zoom) và độ lệch dịch chuyển (pan) X, Y
        private int _imgW, _imgH; // Kích thước chiều rộng và chiều cao thực tế của ảnh theo điểm ảnh (pixel)
        private bool _fitMode = true; // Cờ đánh dấu chế độ tự động căn vừa khung nhìn (Zoom to Fit)
        private bool _panning; // Cờ đánh dấu người dùng đang thực hiện kéo di chuyển ảnh (pan)
        private Point _panStart; // Vị trí điểm đặt chuột ban đầu trên Viewport khi bắt đầu kéo
        private double _panStartX, _panStartY; // Giá trị độ lệch _offsetX và _offsetY ban đầu trước khi kéo

        private const double MinZoom = 0.02, MaxZoom = 200, ZoomStep = 1.2, PixelGridZoom = 8.0; // Định nghĩa các hằng số cấu hình giới hạn Zoom, bước Zoom và ngưỡng hiển thị lưới pixel

        /// <summary>
        /// Khởi tạo một đối tượng ImageViewer.
        /// </summary>
        public ImageViewer() // Hàm khởi tạo
        {
            InitializeComponent(); // Khởi tạo các thành phần giao diện khai báo trong XAML
        }

        // DependencyProperty: cho phép các property có thêm nhiều khả năng nâng cao mà một property C# cơ bản get/set không làm được, chẳng hạn như:
        // Data Binding (ràng buộc dữ liệu từ XAML/ViewModel), Animation, Styling, và tự động thông báo khi giá trị thay đổi (Property Change Notification).
        public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
            nameof(Source),                              // 1. Tên thuộc tính C# tương ứng ("Source")
            typeof(ImageSource),                         // 2. Kiểu dữ liệu của thuộc tính
            typeof(ImageViewer),                         // 3. Class sở hữu (ImageViewer)
            new PropertyMetadata(null, OnSourceChanged)  // 4. Giá trị mặc định (null) & Callback lắng nghe sự thay đổi
        );
        // PropertyMetadata(null, OnSourceChanged): Thiết lập giá trị mặc định ban đầu là null. Khi giá trị của Source bị thay đổi (từ C# hoặc XAML),
        // WPF sẽ tự động gọi phương thức OnSourceChanged để vẽ lại ảnh, tính toán lại độ zoom (ZoomToFit),...

        /// <summary>Nguồn ảnh hiển thị (ImageSource).</summary>
        public ImageSource? Source // Thuộc tính bọc DependencyProperty cho nguồn ảnh
        {
            get => (ImageSource?)GetValue(SourceProperty); // Lấy giá trị nguồn ảnh
            set => SetValue(SourceProperty, value); // Đặt giá trị nguồn ảnh mới
        }

        /// <summary>Lớp vẽ overlay (toạ độ màn hình của viewport). Consumer vẽ lại trong handler TransformChanged.</summary>
        public Canvas OverlayCanvas => Overlay; // Trả về lớp Canvas vẽ đè overlay lên bề mặt ảnh

        public double Zoom => _zoom; // Trả về tỉ lệ Zoom hiện tại
        public double ZoomPercent => _zoom * 100; // Trả về phần trăm Zoom hiện tại
        public int ImagePixelWidth => _imgW; // Trả về chiều rộng pixel của ảnh
        public int ImagePixelHeight => _imgH; // Trả về chiều cao pixel của ảnh

        /// <summary>Phát khi zoom/pan đổi (để toolbar cập nhật % zoom).</summary>
        public event EventHandler? ZoomChanged; // Sự kiện phát ra mỗi khi góc thu phóng thay đổi

        /// <summary>Phát khi di chuột trên ảnh: toạ độ pixel + chuỗi giá trị pixel (R,G,B) — cho status bar.</summary>
        public event EventHandler<(System.Windows.Point image, string pixel)>? CursorMoved; // Sự kiện phát ra khi con trỏ di chuyển trên ảnh

        private FormatConvertedBitmap? _sample; // Biến lưu bản sao ảnh định dạng Bgra32 để phục vụ đọc màu từng pixel

        /// <summary>Đặt mức zoom (giữ tâm viewport).</summary>
        public void SetZoom(double zoom) // Thiết lập giá trị Zoom chủ động từ bên ngoài (Toolbar/Button)
        {
            // 1. Kiểm tra điều kiện an toàn: Nếu chiều rộng ảnh <= 0 (chưa nạp ảnh hoặc ảnh lỗi) thì thoát ngay
            if (_imgW <= 0) return;

            // 2. Kẹp giá trị Zoom đầu vào trong khoảng an toàn từ MinZoom (0.02) đến MaxZoom (200)
            var newZoom = Math.Clamp(zoom, MinZoom, MaxZoom);

            // 3. Tính tọa độ MÀN HÌNH của TÂM VIEWPORT (điểm chính giữa khung nhìn màu đen)
            double cx = Viewport.ActualWidth / 2, cy = Viewport.ActualHeight / 2;

            // 4. Đổi ngược tọa độ MÀN HÌNH của Tâm Viewport (cx, cy) sang TỌA ĐỘ PIXEL ẢNH thực tế (ix, iy)
            //    Đây là "điểm neo" (anchor) trên bức ảnh đang nằm ngay tại trung tâm khung nhìn lúc này
            var ix = (cx - _offsetX) / _zoom;
            var iy = (cy - _offsetY) / _zoom;

            // 5. Tính toán lại độ lệch X và Y (_offsetX, _offsetY) mới cho toàn bộ bức ảnh
            //    Mục đích: Giữ cho điểm pixel ảnh (ix, iy) ở BƯỚC 4 tiếp tục nằm ĐÚNG vị trí Tâm Viewport (cx, cy) ở BƯỚC 3 sau khi đổi tỉ lệ Zoom
            _offsetX = cx - ix * newZoom;
            _offsetY = cy - iy * newZoom;

            // 6. Cập nhật tỉ lệ Zoom mới cho toàn bộ Control
            _zoom = newZoom;

            // 7. Tắt cờ ZoomToFit vì người dùng đã chủ động đặt mức Zoom thủ công
            _fitMode = false;

            // 8. Áp dụng ma trận RenderTransform mới (Scale & Translate) lên UI và gọi vẽ lại Thước, Lưới, Crosshair
            ApplyTransform();
        }

        // Đăng ký một DependencyProperty có tên là ShowRulerProperty cho lớp ImageViewer.
        // 'public static readonly' đảm bảo thuộc tính này được đăng ký duy nhất 1 lần trong bộ nhớ cho toàn bộ class.
        public static readonly DependencyProperty ShowRulerProperty = DependencyProperty.Register(
            nameof(ShowRuler),           // 1. Tên thuộc tính C# đại diện ("ShowRuler"), dùng nameof để tránh gõ sai chuỗi.
            typeof(bool),                // 2. Kiểu dữ liệu của thuộc tính (bool: true/false - bật hoặc tắt thước).
            typeof(ImageViewer),         // 3. Kiểu của lớp sở hữu (Owner type): Lớp ImageViewer chứa thuộc tính này.
            new PropertyMetadata(        // 4. Khởi tạo Metadata chứa cấu hình mặc định và callback xử lý sự kiện:
                true,                    //    - Giá trị mặc định khi vừa khởi tạo Control là true (mặc định hiện thước).
                OnShowRulerChanged       //    - Phương thức callback (static) tự động kích hoạt mỗi khi giá trị ShowRuler bị thay đổi.
            )
        );

        /// <summary>Bật/tắt thước (gốc 0 ở giữa ảnh) + crosshair tâm ảnh.</summary>
        public bool ShowRuler // Thuộc tính bật/tắt hiển thị thước đo và đường tâm ảnh
        {
            get => (bool)GetValue(ShowRulerProperty);
            set => SetValue(ShowRulerProperty, value);
        }

        // Đăng ký DependencyProperty cho thuộc tính ShowPixelGrid trong hệ thống WPF.
        // Khai báo 'public static readonly' giúp thuộc tính được khởi tạo một lần duy nhất trong bộ nhớ ở mức Class.
        public static readonly DependencyProperty ShowPixelGridProperty = DependencyProperty.Register(
            nameof(ShowPixelGrid),   // 1. Tên thuộc tính C# tương ứng ("ShowPixelGrid"), dùng nameof để tự động cập nhật khi đổi tên.
            typeof(bool),            // 2. Kiểu dữ liệu của thuộc tính (bool: true = bật lưới pixel, false = tắt lưới pixel).
            typeof(ImageViewer),     // 3. Kiểu lớp sở hữu (Owner Type): Thuộc về ImageViewer Control.
            new PropertyMetadata(    // 4. Khai báo Metadata cấu hình giá trị mặc định và sự kiện phản ứng:
                true,                //    - Giá trị mặc định ban đầu là true (cho phép hiện lưới khi zoom đủ lớn).
                OnShowGridChanged    //    - Phương thức Callback static tự động gọi để vẽ lại/ẩn lưới mỗi khi cờ ShowPixelGrid thay đổi.
            )
        );

        /// <summary>Bật/tắt lưới pixel (chỉ vẽ khi zoom đủ lớn).</summary>
        public bool ShowPixelGrid // Thuộc tính bật/tắt hiển thị lưới phân tách điểm ảnh
        {
            get => (bool)GetValue(ShowPixelGridProperty);
            set => SetValue(ShowPixelGridProperty, value);
        }

        /// <summary>
        /// Phương thức Callback tự động kích hoạt bởi WPF Framework mỗi khi giá trị của ShowRulerProperty bị thay đổi.
        /// </summary>
        private static void OnShowRulerChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            // 1. Ép kiểu tham số 'd' (đối tượng phát sinh sự kiện) về đúng kiểu điều khiển 'ImageViewer'.
            // Vì OnShowRulerChanged là phương thức static (không thuộc thể hiện cụ thể), nên ta cần dùng cách này 
            // để truy cập vào các thuộc tính, control con và phương thức giao diện (instance members) của ImageViewer.
            var v = (ImageViewer)d;

            // 2. Lấy giá trị mới (bool: true hoặc false) vừa được gán cho ShowRuler từ 'e.NewValue'.
            var on = (bool)e.NewValue;

            // 3. Ẩn/Hiện Thước Ngang bằng cách thay đổi độ cao của Row chứa thước trên Grid layout:
            // - Nếu 'on' == true : Đặt độ cao Row là 22px (đủ không gian hiện các vạch số thước ngang).
            // - Nếu 'on' == false: Đặt độ cao Row về 0px (xóa/ẩn hoàn toàn không gian của thước ngang).
            v.RulerRow.Height = on ? new GridLength(22) : new GridLength(0);

            // 4. Ẩn/Hiện Thước Dọc bằng cách thay đổi chiều rộng của Column chứa thước trên Grid layout:
            // - Nếu 'on' == true : Đặt chiều rộng Column là 34px (đủ không gian hiện các vạch số thước dọc).
            // - Nếu 'on' == false: Đặt chiều rộng Column về 0px (xóa/ẩn hoàn toàn không gian của thước dọc).
            v.RulerCol.Width = on ? new GridLength(34) : new GridLength(0);

            // 5. Gọi hàm vẽ lại các vạch số và chữ số trên thước (Ruler).
            // Nếu 'on' == false, hàm này sẽ chỉ thực hiện xóa (Clear) các phần tử đường nét cũ trên Canvas.
            v.DrawRulers();

            // 6. Gọi hàm tính toán và vẽ lại 2 đường nét đứt cyan (Crosshair).
            // Khi thước ẩn/hiện, vùng Viewport hiển thị bị co/giãn kích thước, nên cần vẽ lại Crosshair
            // để đảm bảo 2 đường cyan vẫn dóng chuẩn qua Tâm Ảnh.
            v.DrawCrosshair();
        }

        private static void OnShowGridChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) // Sự kiện xử lý khi ẩn/hiện lưới pixel
            => ((ImageViewer)d).DrawPixelGrid(); // Gọi vẽ lại lưới pixel

        /// <summary>Phát mỗi khi zoom/pan thay đổi — consumer dùng để vẽ lại ROI/overlay theo toạ độ mới.</summary>
        public event EventHandler? TransformChanged; // Sự kiện báo hiệu phép biến đổi ma trận ảnh (Zoom/Pan) vừa được cập nhật

        public Point ScreenToImage(Point p) => new((p.X - _offsetX) / _zoom, (p.Y - _offsetY) / _zoom); // Chuyển điểm từ hệ tọa độ màn hình Viewport sang hệ tọa độ điểm ảnh
        public Point ImageToScreen(Point p) => new(p.X * _zoom + _offsetX, p.Y * _zoom + _offsetY); // Chuyển điểm từ hệ tọa độ điểm ảnh sang hệ tọa độ màn hình Viewport

        /// <summary>
        /// Callback tự động gọi khi thuộc tính 'Source' (nguồn ảnh) của ImageViewer bị thay đổi từ bên ngoài.
        /// </summary>
        private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            // 1. Ép kiểu 'd' thành 'ImageViewer' để truy cập vào các biến và control con bên trong instance.
            var v = (ImageViewer)d;

            // 2. Lưu lại chiều rộng và chiều cao của bức BỨC ẢNH CŨ trước khi nạp ảnh mới.
            // Việc này dùng để so sánh kích thước giữa ảnh cũ và ảnh mới ở các bước sau.
            int oldW = v._imgW, oldH = v._imgH;

            // 3. Gán nguồn ảnh mới (e.NewValue) vào thuộc tính Source của control hiển thị <Image x:Name="Img"/> trong XAML.
            // Lúc này WPF Render engine bắt đầu tiếp nhận dữ liệu ảnh mới để sẵn sàng đưa lên màn hình.
            v.Img.Source = e.NewValue as ImageSource;

            // 4. Kiểm tra xem dữ liệu ảnh mới có thuộc kiểu 'BitmapSource' (chuẩn chứa dữ liệu pixel trong WPF) hay không:
            if (e.NewValue is BitmapSource bs)
            {
                // 4.1. Trích xuất đúng KÍCH THƯỚC PIXEL THỰC TẾ của ảnh (PixelWidth / PixelHeight), 
                // không dùng Width/Height tiêu chuẩn vì Width/Height có thể bị ảnh hưởng bởi chỉ số DPI.
                v._imgW = bs.PixelWidth;
                v._imgH = bs.PixelHeight;

                // 4.2. Chuyển đổi định dạng ảnh sang chuẩn 'Bgra32' (4 byte: Blue, Green, Red, Alpha) và lưu vào biến '_sample'.
                // MỤC ĐÍCH: Giúp hàm 'ReportCursor' sau này có thể trích xuất màu sắc RGB tại tọa độ con trỏ chuột một cách chính xác và hiệu năng cao nhất.
                // Nếu chuyển đổi lỗi (ví dụ: ảnh định dạng lạ), gán _sample = null để tránh crash ứng dụng.
                try { v._sample = new FormatConvertedBitmap(bs, PixelFormats.Bgra32, null, 0); }
                catch { v._sample = null; }
            }
            else
            {
                // 4.3. Trường hợp người dùng truyền vào 'null' (xóa ảnh) hoặc nguồn ảnh không hợp lệ:
                // Reset toàn bộ thông số kích thước về 0 và hủy bỏ mẫu ảnh sample.
                v._imgW = v._imgH = 0;
                v._sample = null;
            }

            // 5. TỐI ƯU HÓA TRẢI NGHIỆM NGƯỜI DÙNG (UX):
            // Trường hợp 1: Ảnh mới nạp có KÍCH THƯỚC BẰNG HOÀN TOÀN với ảnh cũ (_imgW == oldW và _imgH == oldH)
            // Ví dụ thực tế: Trong hệ thống Machine Vision, camera chụp liên tục các sản phẩm cùng kích thước (Re-run),
            // hoặc người dùng vừa bấm nút xử lý ảnh/vẽ ROI và nạp lại kết quả ảnh đã qua bộ lọc.
            if (v._imgW > 0 && v._imgW == oldW && v._imgH == oldH)
            {
                // Giữ nguyên mức Zoom (_zoom) và vị trí kéo ảnh (_offsetX, _offsetY) hiện tại.
                // Chỉ gọi ApplyTransform() để cập nhật hiển thị, giúp góc nhìn của kỹ sư không bị gián đoạn/nhảy vị trí.
                v.ApplyTransform();
                return; // Kết thúc sớm hàm.
            }

            // Trường hợp 2: Ảnh mới nạp có kích thước KHÁC HOÀN TOÀN ảnh cũ (hoặc nạp ảnh lần đầu)
            // Đánh dấu bật chế độ Fit và tự động tính toán lại tỷ lệ Zoom sao cho toàn bộ bức ảnh mới nằm vừa gọn trong khung hình.
            v._fitMode = true;
            v.ZoomToFit();
        }

        public void ZoomToFit() // Phương thức tính toán đưa toàn bộ ảnh vừa vặn vào khung nhìn Viewport
        {
            double vw = Viewport.ActualWidth, vh = Viewport.ActualHeight; // Lấy chiều rộng và chiều cao thực tế của Viewport
            if (_imgW <= 0 || _imgH <= 0 || vw <= 0 || vh <= 0) // Nếu ảnh hoặc khung nhìn có kích thước rỗng
            {
                _zoom = 1; _offsetX = _offsetY = 0; // Đặt lại thông số chuẩn mặc định
                ApplyTransform(); // Áp dụng phép biến đổi
                return;
            }
            _zoom = Math.Min(vw / _imgW, vh / _imgH); // Lựa chọn tỉ lệ Zoom nhỏ hơn giữa 2 trục để ảnh nằm trọn trong Viewport
            _offsetX = (vw - _imgW * _zoom) / 2; // Căn giữa ảnh theo trục ngang X
            _offsetY = (vh - _imgH * _zoom) / 2; // Căn giữa ảnh theo trục dọc Y
            _fitMode = true; // Đánh dấu đang ở chế độ Fit
            ApplyTransform(); // Áp dụng cập nhật hiển thị
        }

        private void OnViewportSizeChanged(object sender, SizeChangedEventArgs e) // Sự kiện khi cửa sổ/khung nhìn Viewport co giãn kích thước
        {
            if (_fitMode) ZoomToFit(); // Nếu đang ở chế độ Fit -> Tự động tính toán căn lại theo khung mới
            else ApplyTransform(); // Nếu ở chế độ Zoom thủ công -> Vẽ lại các thành phần đồ họa theo khung mới
        }

        private void OnMouseWheel(object sender, MouseWheelEventArgs e) // Sự kiện lăn con lăn chuột để Zoom ảnh tại vị trí con trỏ
        {
            if (_imgW <= 0) return; // Nếu chưa có ảnh thì không xử lý
            var p = e.GetPosition(Viewport); // Lấy tọa độ con trỏ chuột trên khung nhìn Viewport
            var factor = e.Delta > 0 ? ZoomStep : 1.0 / ZoomStep; // Tăng hoặc giảm tỉ lệ theo hướng lăn con lăn
            var newZoom = Math.Clamp(_zoom * factor, MinZoom, MaxZoom); // Kẹp tỉ lệ Zoom trong giới hạn cho phép

            // Giữ điểm ảnh dưới con trỏ cố định (Zoom-to-cursor)
            var ix = (p.X - _offsetX) / _zoom; // Tọa độ ảnh hiện tại dưới con trỏ
            var iy = (p.Y - _offsetY) / _zoom;
            _offsetX = p.X - ix * newZoom; // Tính toán lại độ lệch X để điểm ảnh dưới con trỏ không bị dịch chuyển
            _offsetY = p.Y - iy * newZoom; // Tính toán lại độ lệch Y để điểm ảnh dưới con trỏ không bị dịch chuyển
            _zoom = newZoom; // Cập nhật tỉ lệ Zoom mới
            _fitMode = false; // Tắt cờ FitMode vì người dùng đã chủ động Zoom
            ApplyTransform(); // Áp dụng biến đổi mới
            e.Handled = true; // Đánh dấu sự kiện đã xử lý
        }

        private void OnMouseDown(object sender, MouseButtonEventArgs e) // Sự kiện nhấn phím chuột trên Viewport
        {
            // Pan bằng nút giữa hoặc phải (chừa chuột trái cho thao tác ROI trên Overlay).
            if (e.ChangedButton is MouseButton.Middle or MouseButton.Right) // Nếu nhấn chuột giữa hoặc chuột phải
            {
                _panning = true; // Bật cờ đánh dấu đang kéo di chuyển ảnh
                _panStart = e.GetPosition(Viewport); // Lưu lại vị trí click chuột ban đầu
                _panStartX = _offsetX; _panStartY = _offsetY; // Lưu lại độ lệch dịch chuyển X, Y ban đầu
                Viewport.CaptureMouse(); // Bắt giữ sự kiện chuột cho Viewport
                Viewport.Cursor = Cursors.SizeAll; // Đổi biểu tượng con trỏ chuột sang dạng mũi tên 4 hướng
                e.Handled = true; // Đánh dấu đã xử lý xong sự kiện
            }
        }

        private void OnMouseMove(object sender, MouseEventArgs e) // Sự kiện di chuyển chuột trên Viewport
        {
            var p = e.GetPosition(Viewport); // Lấy tọa độ con trỏ hiện tại
            ReportCursor(p); // Báo cáo thông tin tọa độ và giá trị pixel tại vị trí con trỏ
            if (!_panning) return; // Nếu không trong chế độ kéo Pan thì dừng
            _offsetX = _panStartX + (p.X - _panStart.X); // Tính toán độ lệch X mới theo khoảng di chuyển chuột
            _offsetY = _panStartY + (p.Y - _panStart.Y); // Tính toán độ lệch Y mới theo khoảng di chuyển chuột
            _fitMode = false; // Tắt cờ FitMode
            ApplyTransform(); // Áp dụng cập nhật hiển thị vị trí ảnh mới
        }

        private void ReportCursor(System.Windows.Point p) // Phương thức tính toán tọa độ pixel và đọc giá trị màu điểm ảnh
        {
            if (_imgW <= 0) return; // Nếu không có ảnh thì ngưng
            var img = ScreenToImage(p); // Chuyển tọa độ con trỏ chuột từ màn hình sang tọa độ ảnh
            int ix = (int)Math.Floor(img.X), iy = (int)Math.Floor(img.Y); // Lấy phần nguyên tọa độ điểm ảnh
            if (ix < 0 || iy < 0 || ix >= _imgW || iy >= _imgH) // Nếu nằm ngoài phạm vi vùng ảnh
            {
                CursorMoved?.Invoke(this, (new System.Windows.Point(double.NaN, double.NaN), "—")); // Báo sự kiện không tìm thấy vị trí điểm ảnh hợp lệ
                return;
            }
            var pix = "—"; // Biến lưu chuỗi thông tin màu pixel
            if (_sample is not null) // Nếu ảnh đệm đọc pixel có tồn tại
            {
                try
                {
                    var b = new byte[4]; // Mảng 4 byte chứa các kênh màu BGRA
                    _sample.CopyPixels(new System.Windows.Int32Rect(ix, iy, 1, 1), b, 4, 0); // Đọc giá trị 1 pixel tại tọa độ (ix, iy)
                    pix = $"{b[2]},{b[1]},{b[0]}"; // Trích xuất định dạng dạng chuỗi: R, G, B
                }
                catch { /* ignore */ } // Bỏ qua nếu lỗi đọc bộ nhớ pixel
            }
            CursorMoved?.Invoke(this, (new System.Windows.Point(ix, iy), pix)); // Bắn sự kiện CursorMoved về UI kèm tọa độ và màu sắc
        }

        private void OnMouseUp(object sender, MouseButtonEventArgs e) // Sự kiện khi người dùng thả nút chuột
        {
            if (_panning) // Nếu đang ở chế độ kéo di chuyển ảnh
            {
                _panning = false; // Tắt cờ panning
                Viewport.ReleaseMouseCapture(); // Giải phóng bắt giữ sự kiện chuột
                Viewport.Cursor = Cursors.Arrow; // Khôi phục biểu tượng con trỏ chuột về dạng mũi tên mặc định
            }
        }

        private void ApplyTransform() // Phương thức áp dụng ma trận biến đổi không gian hiển thị ảnh và vẽ lại đồ họa phụ trợ
        {
            // Img: Là đối tượng Control hiển thị hình ảnh (<Image/> trong XAML).
            // RenderTransform: Thuộc tính của WPF cho phép áp dụng các phép biến đổi hình học (thu phóng, xoay, dịch chuyển, nghiêng)
            //   lên giao diện ngay tại công đoạn hiển thị (Render phase) mà không làm thay đổi kích thước thực tế (Layout/Measure phase) của phần tử.
            // TransformGroup: Một lớp gom nhóm cho phép kết hợp nhiều phép biến đổi cùng một lúc theo một thứ tự nhất định.
            Img.RenderTransform = new TransformGroup // 1. Gán ma trận cho ảnh (Cực nhẹ - GPU xử lý)
            {
                Children = { new ScaleTransform(_zoom, _zoom), new TranslateTransform(_offsetX, _offsetY) } // Áp dụng tỉ lệ Zoom và độ lệch dịch chuyển Translate
            };
            // Children: Danh sách chứa các phép biến đổi con nằm trong TransformGroup
            // ScaleTransform(_zoom, _zoom): Phép co giãn không gian. Nó nhân cả chiều ngang (X) và chiều dọc (Y) của bức ảnh với tỉ lệ _zoom.
            // TranslateTransform(_offsetX, _offsetY): Phép dịch chuyển vị trí. Nó đẩy bức ảnh đi một khoảng _offsetX theo chiều ngang và _offsetY theo chiều dọc.

            ZoomLabel.Text = $"{_zoom * 100:F0}%"; // 2. Cập nhật chuỗi phần trăm Zoom hiển thị lên giao diện

            // 3. Xóa và VẼ LẠI các đường nét Vector phụ trợ (Xử lý trên CPU)
            DrawRulers(); // Vẽ lại thước đo
            DrawCrosshair(); // Vẽ lại đường tâm crosshair
            DrawPixelGrid(); // Vẽ lại lưới điểm ảnh
            TransformChanged?.Invoke(this, EventArgs.Empty); // Bắn sự kiện báo biến đổi hình học thay đổi
            ZoomChanged?.Invoke(this, EventArgs.Empty); // Bắn sự kiện báo tỉ lệ Zoom thay đổi
        }

        // ---- Thước ----

        private static double NiceStep(double rough)
        {
            // 1. Kiểm tra điều kiện biên an toàn:
            // Nếu khoảng chia thô <= 0 (do lỗi tính toán hoặc zoom quá mức), trả về bước chia mặc định là 1.
            if (rough <= 0) return 1;

            // 2. Tìm bậc lượng (Order of Magnitude / Cấp độ hàng đơn vị) của số rough dưới dạng lũy thừa của 10.
            // Ví dụ: 
            // - Nếu rough = 45  -> Log10(45) = 1.65  -> Floor(1.65) = 1 -> Math.Pow(10, 1) = 10
            // - Nếu rough = 0.03 -> Log10(0.03) = -1.52 -> Floor(-1.52) = -2 -> Math.Pow(10, -2) = 0.01
            var pow = Math.Pow(10, Math.Floor(Math.Log10(rough)));

            // 3. Chuẩn hóa số rough về một số thực 'n' nằm trong khoảng từ [1.0 đến 10.0).
            // Bằng cách chia cho pow, ta bóc tách phần BẬC CỦA 10 ra, chỉ giữ lại phần HỆ SỐ.
            // Ví dụ: 45 / 10 = 4.5  |  0.03 / 0.01 = 3.0
            var n = rough / pow;

            // 4. Ép hệ số 'n' (đang từ 1.0 -> 10.0) về các giá trị "SỐ ĐẸP" chuẩn (1, 2, 5, 10):
            // - Nếu n < 1.5 -> Chọn 1  (Ví dụ: 1.2 -> 1)
            // - Nếu n < 3.0 -> Chọn 2  (Ví dụ: 2.1 -> 2)
            // - Nếu n < 7.0 -> Chọn 5  (Ví dụ: 4.5 -> 5)
            // - Ngược lại    -> Chọn 10 (Ví dụ: 8.2 -> 10)
            var nice = n < 1.5 ? 1 : n < 3 ? 2 : n < 7 ? 5 : 10;

            // 5. Nhân con "số đẹp" hệ số (1, 2, 5, 10) trả lại cho bậc cơ số 10 ban đầu để ra kết quả cuối cùng.
            // Ví dụ: 5 * 10 = 50  |  2 * 0.01 = 0.02
            return nice * pow;
        }

        private void DrawRulers() // Phương thức vẽ các vạch và con số trên thước ngang & đứng
        {
            // 1. Dọn dẹp sạch sẽ các vạch và chữ số cũ đã vẽ trên 2 Canvas thước để chuẩn bị vẽ lại bộ vạch mới
            TopRuler.Children.Clear();
            LeftRuler.Children.Clear();

            // 2. Kiểm tra điều kiện dừng (Guard Clauses): 
            // Nếu thuộc tính ShowRuler = false (tắt thước) hoặc chiều rộng ảnh <= 0 (chưa nạp ảnh) thì không vẽ gì thêm
            if (!ShowRuler || _imgW <= 0) return;

            // 3. Lấy kích thước hiển thị thực tế (chiều rộng vw, chiều cao vh) của khung nhìn màu đen (Viewport)
            double vw = Viewport.ActualWidth, vh = Viewport.ActualHeight;
            if (vw <= 0 || vh <= 0) return; // Nếu giao diện chưa render xong (kích thước = 0) thì dừng

            // 4. Định nghĩa bảng màu thiết kế giao diện (Palette):
            var line = new SolidColorBrush(Color.FromRgb(0x70, 0x70, 0x74)); // Màu xám trung tính vẽ vạch chia (Ticks)
            var text = new SolidColorBrush(Color.FromRgb(0xC0, 0xC0, 0xC0)); // Màu xám sáng vẽ con số nhãn (Labels)

            // 5. THUẬT TOÁN TÍNH BƯỚC CHIA THƯỚC (STEP) ĐỘNG THEO ZOOM:
            // - Mong muốn: Các vạch số cách nhau khoảng ~70px trên màn hình để không bị đè chữ vào nhau.
            // - Quy đổi 70px màn hình ra khoảng cách Pixel Ảnh thực tế: (70.0 / _zoom)
            // - Dùng hàm 'NiceStep' đưa khoảng cách thô đó về chuỗi "SỐ ĐẸP" (1, 2, 5, 10, 20, 50, 100...)
            double stepImg = NiceStep(70.0 / _zoom);

            // 6. Xác định tọa độ TÂM ẢNH (Image Center) làm GỐC TỌA ĐỘ 0 CỦA THƯỚC:
            double cxImg = _imgW / 2.0, cyImg = _imgH / 2.0;

            // ==========================================
            // A. XỬ LÝ VẼ THƯỚC NGANG (TOP RULER)
            // ==========================================

            // 7. Tính khoảng cách ảnh (đơn vị pixel ảnh) từ TÂM ẢNH đến MÉT TRÁI (0) và MÉP PHẢI (vw) của màn hình hiện tại:
            // - vminX: Khoảng pixel ảnh tương ứng với mép trái màn hình (x = 0).
            // - vmaxX: Khoảng pixel ảnh tương ứng với mép phải màn hình (x = vw).
            // Vì sao lại phải trừ cho cxImg? -> Để dời gốc tọa độ về Tâm Ảnh
            double vminX = (0 - _offsetX) / _zoom - cxImg;
            double vmaxX = (vw - _offsetX) / _zoom - cxImg;

            // 8. Vòng lặp tính vị trí từng vạch chia từ vminX đến vmaxX:
            // - Math.Ceiling(vminX / stepImg) * stepImg: Làm tròn điểm bắt đầu lặp lên đúng bội số gần nhất của 'stepImg'
            // để vạch số luôn rơi vào số tròn (vd: -100, -50, 0, 50, 100...).
            for (double off = Math.Ceiling(vminX / stepImg) * stepImg; off <= vmaxX; off += stepImg)
            {
                // 8.1. Quy đổi ngược khoảng cách ảnh 'off' (tính từ tâm) ra tọa độ MÀN HÌNH thực tế 'sx' (pixel)
                // Để vẽ một vạch kẻ lên Canvas giao diện WPF, Canvas chỉ hiểu tọa độ màn hình sx, nó không thể tự hiểu tọa độ pixel của bức ảnh nếu không tính sx
                double sx = (cxImg + off) * _zoom + _offsetX;

                // 8.2. Vẽ đường vạch thước (Line) thẳng đứng nhỏ ở đáy thước ngang (từ Y=14px đến Y=22px)
                TopRuler.Children.Add(new Line { X1 = sx, Y1 = 14, X2 = sx, Y2 = 22, Stroke = line, StrokeThickness = 1 });

                // 8.3. Tạo chữ hiển thị số (TextBlock), định dạng không dùng dấu phẩy phân cách
                // Foreground = text: Gán thuộc tính màu chữ (Foreground) cho thẻ TextBlock bằng đối tượng text (SolidColorBrush) đã khởi tạo ở đầu hàm DrawRulers
                var t = new TextBlock { Text = off.ToString("0", CultureInfo.InvariantCulture), Foreground = text, FontSize = 9 };

                // 8.4. Đặt vị trí cho nhãn chữ (nhích sang phải 2px và cách mép trên 1px) rồi thêm vào Canvas thước ngang
                Canvas.SetLeft(t, sx + 2);
                Canvas.SetTop(t, 1);
                TopRuler.Children.Add(t);
            }

            // ==========================================
            // B. XỬ LÝ VẼ THƯỚC DỌC (LEFT RULER)
            // ==========================================

            // 9. Tính khoảng cách ảnh từ TÂM ẢNH đến MÉP TRÊN (0) và MÉP DƯỚI (vh) của màn hình:
            double vminY = (0 - _offsetY) / _zoom - cyImg;
            double vmaxY = (vh - _offsetY) / _zoom - cyImg;

            // 10. Vòng lặp tính vị trí từng vạch chia từ vminY đến vmaxY:
            for (double off = Math.Ceiling(vminY / stepImg) * stepImg; off <= vmaxY; off += stepImg)
            {
                // 10.1. Quy đổi ngược khoảng cách 'off' ra tọa độ MÀN HÌNH thực tế 'sy'
                double sy = (cyImg + off) * _zoom + _offsetY;

                // 10.2. Vẽ đường vạch thước nằm ngang nhỏ ở lề phải thước dọc (từ X=22px đến X=34px)
                LeftRuler.Children.Add(new Line { X1 = 22, Y1 = sy, X2 = 34, Y2 = sy, Stroke = line, StrokeThickness = 1 });

                // 10.3. Tạo nhãn chữ hiển thị số cho thước dọc
                var t = new TextBlock { Text = off.ToString("0", CultureInfo.InvariantCulture), Foreground = text, FontSize = 9 };

                // 10.4. Đặt vị trí nhãn chữ và thêm vào Canvas thước dọc
                Canvas.SetLeft(t, 1);
                Canvas.SetTop(t, sy + 1);
                LeftRuler.Children.Add(t);
            }
        }

        private void DrawCrosshair() // Phương thức vẽ 2 đường gạch chữ thập tâm ảnh (Crosshair)
        {
            CrosshairCanvas.Children.Clear(); // Xóa crosshair cũ
            if (!ShowRuler || _imgW <= 0) return; // Nếu không bật thước hoặc không có ảnh thì ngưng

            double vw = Viewport.ActualWidth, vh = Viewport.ActualHeight; // Kích thước khung nhìn
            var center = ImageToScreen(new Point(_imgW / 2.0, _imgH / 2.0)); // Chuyển vị trí tâm ảnh sang hệ tọa độ màn hình
            var brush = new SolidColorBrush(Color.FromArgb(0xAA, 0x00, 0xE5, 0xFF)); // Màu xanh cyan neon mờ
            var dash = new DoubleCollection { 4, 4 }; // Mẫu đường nét đứt 4px

            CrosshairCanvas.Children.Add(new Line { X1 = center.X, Y1 = 0, X2 = center.X, Y2 = vh, Stroke = brush, StrokeThickness = 1, StrokeDashArray = dash }); // Vẽ đường dọc nét đứt đi qua tâm ảnh
            CrosshairCanvas.Children.Add(new Line { X1 = 0, Y1 = center.Y, X2 = vw, Y2 = center.Y, Stroke = brush, StrokeThickness = 1, StrokeDashArray = dash }); // Vẽ đường ngang nét đứt đi qua tâm ảnh
        }

        // ---- Lưới pixel ----

        private void DrawPixelGrid() // Phương thức vẽ các dòng lưới chia ranh giới từng pixel khi phóng to
        {
            GridCanvas.Children.Clear(); // Xóa lưới cũ
            if (!ShowPixelGrid || _imgW <= 0 || _zoom < PixelGridZoom) return; // Chỉ vẽ khi bật cờ ShowPixelGrid và tỉ lệ Zoom đủ lớn (>= 8.0x)

            double vw = Viewport.ActualWidth, vh = Viewport.ActualHeight; // Lấy kích thước Viewport
            var brush = new SolidColorBrush(Color.FromArgb(0x66, 0x88, 0x88, 0x88)); // Màu đường lưới xám mảnh mờ

            // Chuyển sang tọa độ ảnh là để LỌC BỚT công việc, còn chuyển về tọa độ Viewport là để VẼ.
            int x0 = Math.Max(0, (int)Math.Floor((0 - _offsetX) / _zoom)); // Chỉ số pixel X bắt đầu hiển thị trên Viewport
            int x1 = Math.Min(_imgW, (int)Math.Ceiling((vw - _offsetX) / _zoom)); // Chỉ số pixel X kết thúc hiển thị trên Viewport
            for (int x = x0; x <= x1; x++) // Duyệt và vẽ các đường dọc ranh giới pixel
            {
                double sx = x * _zoom + _offsetX; // Chuyển tọa độ cột pixel sang màn hình
                GridCanvas.Children.Add(new Line { X1 = sx, Y1 = Math.Max(0, _offsetY), X2 = sx, Y2 = Math.Min(vh, _offsetY + _imgH * _zoom), Stroke = brush, StrokeThickness = 0.5 }); // Vẽ vạch lưới dọc
            }

            int y0 = Math.Max(0, (int)Math.Floor((0 - _offsetY) / _zoom)); // Chỉ số pixel Y bắt đầu hiển thị trên Viewport
            int y1 = Math.Min(_imgH, (int)Math.Ceiling((vh - _offsetY) / _zoom)); // Chỉ số pixel Y kết thúc hiển thị trên Viewport
            for (int y = y0; y <= y1; y++) // Duyệt và vẽ các đường ngang ranh giới pixel
            {
                double sy = y * _zoom + _offsetY; // Chuyển tọa độ dòng pixel sang màn hình
                GridCanvas.Children.Add(new Line { X1 = Math.Max(0, _offsetX), Y1 = sy, X2 = Math.Min(vw, _offsetX + _imgW * _zoom), Y2 = sy, Stroke = brush, StrokeThickness = 0.5 }); // Vẽ vạch lưới ngang
            }
        }
    }
}