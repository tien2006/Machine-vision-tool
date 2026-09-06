using System.Collections.ObjectModel; // Nhập thư viện quản lý tập hợp tự động thông báo UI khi có thay đổi (ObservableCollection)
using System.Text.Json; // Nhập thư viện xử lý và chuỗi hóa JSON
using System.Windows.Media.Imaging; // Nhập thư viện xử lý ảnh hiển thị WPF (BitmapSource)
using System.Windows.Threading; // Nhập thư viện hỗ trợ DispatcherTimer điều khiển luồng UI
using CommunityToolkit.Mvvm.ComponentModel; // Nhập thư viện nền tảng MVVM ComponentModel từ Toolkit
using CommunityToolkit.Mvvm.Input; // Nhập thư viện hỗ trợ RelayCommand cho MVVM
using VisionFlow.Core.Imaging; // Nhập thư viện xử lý ảnh lõi (IVisionImage)
using VisionFlow.Core.Models; // Nhập các model hình học và kết quả (Point2d, RotatedRectRegion, CircleRegion, LineResult...)
using VisionFlow.Core.Tools; // Nhập không gian tên chứa giao diện Tool và Parameter
using VisionFlow.Editor.Adapter; // Nhập không gian tên chuyển đổi ảnh (ImagePreview)
using VisionFlow.Engine.Execution; // Nhập trình thực thi đồ thị thuật toán (FlowExecutor)
using VisionFlow.Engine.Graph; // Nhập cấu trúc đồ thị dòng chảy (FlowGraph, FlowNode)

namespace VisionFlow.Editor.ViewModels // Định nghĩa không gian tên chứa ViewModel cho Editor
{
    /// <summary>Loại ROI hiển thị/sửa trên ảnh trong Parameter Editor.</summary>
    public enum EditorRoiKind { None, Rect, Circle } // Định nghĩa các loại vùng quan tâm (ROI) hỗ trợ vẽ trên UI

    /// <summary>Một nhóm (tab) tham số theo Category.</summary>
    public sealed class ParameterCategoryGroup // Lớp gom nhóm các tham số theo từng danh mục
    {
        /// <summary>
        /// Khởi tạo một nhóm tham số với tên danh mục và danh sách các ViewModel tham số con.
        /// </summary>
        public ParameterCategoryGroup(string name, IEnumerable<ParameterEditorViewModel> items) // Hàm khởi tạo nhóm
        {
            Name = name; // Gán tên nhóm danh mục
            Items = new ObservableCollection<ParameterEditorViewModel>(items); // Khởi tạo danh sách tham số dạng ObservableCollection
        }

        public string Name { get; } // Thuộc tính lấy tên của nhóm danh mục
        public ObservableCollection<ParameterEditorViewModel> Items { get; } // Danh sách các ViewModel tham số thuộc nhóm này (Sửa lỗi kiểu dữ liệu: ObservableCollection<ParameterEditorViewModel>)
    }

    /// <summary>
    /// ViewModel cho cửa sổ cấu hình một node (mở khi double-click). Tự sinh form theo Category, hiển thị
    /// ảnh để vẽ ROI/kết quả, và Run thử node.
    /// <para>
    /// QUAN TRỌNG: mọi lần chạy đều **bất đồng bộ** (Task.Run) + **debounce** để KHÔNG treo UI khi tool
    /// nặng (vd Find Circle dùng HoughCircles). Ảnh BitmapSource được tạo & Freeze trên luồng nền rồi gán
    /// về UI. Tool KHÔNG có ROI sẽ hiển thị **ảnh output** to bằng input (giống VisionPlatform).
    /// </para>
    /// </summary>
    public sealed partial class ParameterEditorWindowViewModel : ObservableObject, IDisposable // Lớp ViewModel cho cửa sổ cấu hình node
    {
        private readonly FlowGraph _graph; // Lưu trữ đối tượng đồ thị dòng chảy chứa node
        private readonly FlowNode _node; // Lưu trữ đối tượng node đang được chỉnh sửa
        private readonly FlowExecutor _executor = new(); // Trình thực thi chạy thử node/đồ thị
        private readonly IToolParameter? _roiParam; // Tham số lưu trữ thông tin ROI (nếu node có hỗ trợ)
        private readonly DispatcherTimer _debounce; // Bộ đếm thời gian trì hoãn (debounce) chống spam lệnh thực thi khi nhập liên tục

        private readonly Dictionary<string, object?> _original; // Từ điển lưu trữ giá trị tham số ban đầu để phục hồi khi ấn Cancel
        private bool _initialized; // Cờ đánh dấu ViewModel đã khởi tạo xong hay chưa
        private bool _running; // Cờ báo node đang trong quá trình chạy bất đồng bộ
        private bool _rerunPending; // Cờ ghi nhớ cần phải chạy lại node sau khi lượt chạy hiện tại kết thúc

        /// <summary>
        /// Hàm khởi tạo ViewModel cửa sổ cấu hình tham số Node.
        /// </summary> // Khởi tạo ViewModel nhận vào đồ thị và node cần sửa
        public ParameterEditorWindowViewModel(FlowGraph graph, FlowNode node)
        {
            _graph = graph; // Gán đồ thị truyền vào
            _node = node; // Gán node truyền vào

            var groups = node.Tool.Parameters // Lấy tất cả tham số của Tool
                .GroupBy(p => p.Category) // Gom nhóm tham số theo tên Category
                .OrderBy(g => g.Min(p => p.Order)) // Sắp xếp nhóm theo thứ tự Order nhỏ nhất của tham số trong nhóm
                .ThenBy(g => g.Key) // Nếu bằng nhau thì sắp xếp theo tên Category
                .Select(g => new ParameterCategoryGroup(g.Key, // Tạo đối tượng nhóm mới
                    g.OrderBy(p => p.Order).Select(p => new ParameterEditorViewModel(p)))); // Sắp xếp các tham số trong nhóm theo Order và bọc vào ParameterEditorViewModel
            Categories = new ObservableCollection<ParameterCategoryGroup>(groups); // Gán danh sách các nhóm tham số vào thuộc tính Categories
            SelectedCategory = Categories.FirstOrDefault(); // Chọn mặc định tab danh mục đầu tiên

            // Mỗi khi người dùng nhập/chỉnh sửa bất kỳ thông số nào trên màn hình, node xử lý ảnh sẽ tự động
            // chạy lại (Run) ngay lập tức để cập nhật kết quả lên màn hình!
            foreach (var item in Categories.SelectMany(c => c.Items)) // Duyệt qua từng ViewModel tham số trong tất cả các nhóm
                // Categories.SelectMany(c => c.Items)): Làm phẳng danh sách
                    // Danh sách Categories vốn có cấu trúc 2 tầng: Nhóm (Tab Category) -> Các tham số bên trong nhóm (Items).
                    // Nếu dùng foreach thông thường, bạn sẽ phải viết 2 vòng lặp lồng nhau (vòng 1 duyệt từng Tab, vòng 2 duyệt từng Tham số trong Tab đó).
                    // Hàm LINQ .SelectMany() giúp "làm phẳng" (flatten) danh sách 2 tầng này thành
                    // 1 danh sách duy nhất chứa toàn bộ ParameterEditorViewModel của tất cả các nhóm, giúp code gọn gàng hơn rất nhiều.

                item.PropertyChanged += (_, e) => // Đăng ký sự kiện lắng nghe sự thay đổi thuộc tính
                // Cú pháp += dùng để đăng ký lắng nghe sự kiện PropertyChanged của tham số đó.
                // Ký tự _ (discard) đại diện cho tham số sender (đối tượng phát ra sự kiện, ở đây chúng ta không cần dùng đến nên đặt tên là _).
                // Biến e chứa thông tin về thuộc tính vừa bị thay đổi (e.PropertyName).
                {
                    if (_initialized && e.PropertyName is nameof(ParameterEditorViewModel.Value) // Nếu đã khởi tạo xong và thuộc tính thay đổi là Value
                    // _initialized: Cờ đánh dấu cửa sổ đã tải xong dữ liệu chưa.
                        // Tại sao cần cờ này? Khi mới mở cửa sổ, các tham số sẽ được nạp giá trị ban đầu (trực tiếp gây ra sự kiện
                        // PropertyChanged). Nhờ cờ _initialized, ứng dụng sẽ bỏ qua không tự chạy node trong lúc đang khởi tạo,
                        // tránh làm treo giao diện.
                    // e.PropertyName is nameof(...) or nameof(...):
                        // Chỉ phản hồi khi thuộc tính vừa đổi đúng là Value (dành cho TextBox, ComboBox, Checkbox, File Picker)
                        // hoặc NumericValue (dành cho Slider).
                    // Việc dùng nameof(...) giúp code an toàn, tránh lỗi gõ sai chuỗi chữ ("Value").
                            or nameof(ParameterEditorViewModel.NumericValue)) // Hoặc NumericValue
                        RequestRun(); // Tự động yêu cầu chạy lại node - Kích hoạt bộ đếm thời gian (Debounce)
                        // Hàm này chưa chạy thuật toán ngay, mà sẽ khởi động bộ đếm _debounce (chờ 180ms).
                        // Lợi ích cực lớn: Nếu bạn kéo thanh trượt Slider liên tục từ 1 đến 100, sự kiện này sẽ bắn ra hàng trăm lần.
                        // Nhờ RequestRun() + Debounce, chương trình sẽ đợi bạn dừng kéo chuột 180ms rồi mới chạy thuật toán 1 lần
                        // duy nhất, giúp UI không bị giật lag!
                };

            _roiParam = node.Tool.Parameters.FirstOrDefault(p =>     // Tìm tham số đầu tiên đóng vai trò tương tác ROI hình chữ nhật xoay, hình tròn hoặc Template
                 p.Interaction is ParameterInteraction.RotatedRectRegion
                                or ParameterInteraction.CircleRegion
                                or ParameterInteraction.Template); // Thêm Template để PMAlignt cũng được vẽ ROI tương tác
            _original = node.Tool.Parameters.ToDictionary(p => p.Name, p => p.Value); // Sao lưu giá trị ban đầu của tất cả tham số vào Dictionary

            _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) }; // Đặt thời gian chờ trì hoãn debounce là 180ms
            _debounce.Tick += (_, _) => { _debounce.Stop(); _ = RunAsync(); }; // Khi hết thời gian chờ -> Dừng bộ đếm và gọi chạy node bất đồng bộ
        }

        public string Title => $"Parameters — {_node.Tool.DisplayName}"; // Tiêu đề của cửa sổ cấu hình
        public ObservableCollection<ParameterCategoryGroup> Categories { get; } // Tập hợp các nhóm danh mục tham số hiển thị ở panel trái

        /// <summary>Category (tab) đang chọn ở panel trái.</summary>
        [ObservableProperty] private ParameterCategoryGroup? _selectedCategory; // Thuộc tính tự sinh thông báo UI cho nhóm danh mục đang được chọn

        /// <summary>Ảnh lớn (output cho tool không-ROI; input cho tool có ROI để vẽ vùng).</summary>
        [ObservableProperty] private BitmapSource? _backgroundImage; // Thuộc tính tự sinh thông báo UI cho ảnh nền lớn chính

        /// <summary>Ảnh preview phụ (bổ trợ cho ảnh lớn).</summary>
        [ObservableProperty] private BitmapSource? _resultImage; // Thuộc tính tự sinh thông báo UI cho ảnh xem trước phụ

        [ObservableProperty] private string _statusText = string.Empty; // Thuộc tính hiển thị chuỗi trạng thái xử lý

        /// <summary>JSON của toàn bộ output port của node (hiển thị ở tab "Data Output").</summary>
        [ObservableProperty] private string _outputJson = "{}"; // Thuộc tính lưu trữ chuỗi định dạng JSON kết quả các cổng ra

        public bool HasRoi => _roiParam is not null; // Kiểm tra node này có cấu hình tham số ROI hay không

        /// <summary>Loại ROI cần vẽ trên ảnh: chữ nhật xoay hay hình tròn.</summary>
        public EditorRoiKind RoiKind => _roiParam?.Interaction switch // Xác định loại hình vẽ ROI dựa theo loại tương tác của tham số
        {
            ParameterInteraction.RotatedRectRegion => EditorRoiKind.Rect, // Nếu là RotatedRectRegion -> Trả về loại hình chữ nhật
            ParameterInteraction.Template => EditorRoiKind.Rect, // Template dùng chung UI vẽ Rect, chỉ khác cách lưu dữ liệu
            ParameterInteraction.CircleRegion => EditorRoiKind.Circle, // Nếu là CircleRegion -> Trả về loại hình tròn
            _ => EditorRoiKind.None // Mặc định không có ROI
        };

        public RotatedRectRegion Roi
        {
            get => _roiParam?.Value switch
            {
                RotatedRectRegion r => r,                              // Trường hợp tham số là RotatedRectRegion thuần (RegionSelector, FindCircle...)
                TemplateImageRef t => t.SourceRegion ?? default,       // Trường hợp tham số là TemplateImageRef (PMAlignt) -> bóc lấy SourceRegion bên trong
                _ => default
            };
            set
            {
                if (_roiParam is not null)
                {
                    // Nếu giá trị hiện tại đang là TemplateImageRef thì phải GÓI lại đúng kiểu khi ghi ngược vào,
                    // giữ nguyên FilePath cũ (nếu có) và chỉ thay phần SourceRegion vừa được người dùng kéo/xoay.
                    _roiParam.Value = _roiParam.Value is TemplateImageRef existing
                        ? existing with { SourceRegion = value }
                        : (object)value;
                }
                OnPropertyChanged();
            }
        }

        public CircleRegion RoiCircle // Thuộc tính truy cập/cập nhật dữ liệu vùng ROI hình tròn
        {
            get => _roiParam?.Value is CircleRegion c ? c : default; // Lấy giá trị CircleRegion từ tham số
            set
            {
                if (_roiParam is not null) _roiParam.Value = value; // Cập nhật giá trị vào tham số ROI gốc
                OnPropertyChanged(); // Thông báo UI cập nhật thay đổi
            }
        }

        // ---- Caliper / result overlay (cho các tool caliper như Find Line) ----

        /// <summary>Node có caliper để vẽ overlay không (có tham số NumberOfCalipers + ROI chữ nhật).</summary>
        public bool HasCalipers => RoiKind == EditorRoiKind.Rect && _node.Tool.FindParameter("NumberOfCalipers") is not null; // Kiểm tra node có hỗ trợ vẽ Caliper không

        private int ParamInt(string name, int fallback) => _node.Tool.FindParameter(name)?.Value is int i ? i : fallback; // Phương thức bổ trợ lấy tham số kiểu int an toàn
        private bool ParamBool(string name, bool fallback) => _node.Tool.FindParameter(name)?.Value is bool b ? b : fallback; // Phương thức bổ trợ lấy tham số kiểu bool an toàn

        /// <summary>
        /// Các đoạn caliper (toạ độ ảnh) để vẽ overlay — phản chiếu chính xác
        /// <c>FindLineTool.GenerateRectangleCalipers</c> để khung hiển thị khớp thuật toán.
        /// </summary>
        public IReadOnlyList<(Point2d start, Point2d end)> CaliperSegments() // Tính toán danh sách các đoạn thẳng Caliper để vẽ đè lên ảnh
        {
            var list = new List<(Point2d, Point2d)>(); // Khởi tạo danh sách các cặp điểm đầu-cuối
            if (!HasCalipers) return list; // Nếu không hỗ trợ Caliper thì trả về danh sách rỗng

            var r = Roi; // Lấy thông tin vùng ROI hình chữ nhật xoay hiện tại
            double cx = r.Center.X, cy = r.Center.Y, regionWidth = r.Width, caliperLength = r.Height; // Lấy tọa độ tâm, chiều rộng và chiều cao (độ dài Caliper)
            double angle = r.AngleDeg * Math.PI / 180.0; // Chuyển đổi góc xoay từ độ sang Radian
            int n = Math.Max(1, ParamInt("NumberOfCalipers", 10)); // Lấy số lượng vạch Caliper (tối thiểu là 1)
            bool forward = ParamBool("CaliperDirectionForward", true); // Lấy hướng quét Caliper
            bool horizontal = regionWidth > caliperLength; // Xác định hướng phân bổ Caliper là nằm ngang hay thẳng đứng

            for (int i = 0; i < n; i++) // Vòng lặp chia đều các vị trí Caliper
            {
                double t = (double)i / Math.Max(1, n - 1); // Tính tỉ lệ phân bổ vị trí từ 0.0 đến 1.0
                Point2d s, e; // Điểm bắt đầu (s) và kết thúc (e) của vạch Caliper
                if (horizontal) // Nếu phân bổ theo chiều ngang
                {
                    double x = cx + (t - 0.5) * regionWidth, y = cy; // Tọa độ X phân bố dọc chiều rộng, Y ở tâm
                    (s, e) = forward
                        ? (new Point2d(x, y - caliperLength / 2), new Point2d(x, y + caliperLength / 2)) // Hướng thuận
                        : (new Point2d(x, y + caliperLength / 2), new Point2d(x, y - caliperLength / 2)); // Hướng ngược
                }
                else // Nếu phân bổ theo chiều dọc
                {
                    double x = cx, y = cy + (t - 0.5) * caliperLength; // Tọa độ Y phân bố dọc chiều cao, X ở tâm
                    (s, e) = forward
                        ? (new Point2d(x - caliperLength / 2, y), new Point2d(x + caliperLength / 2, y)) // Hướng thuận
                        : (new Point2d(x + caliperLength / 2, y), new Point2d(x - caliperLength / 2, y)); // Hướng ngược
                }
                if (Math.Abs(angle) > 0.01) { s = Rotate(s, cx, cy, angle); e = Rotate(e, cx, cy, angle); } // Nếu có góc xoay -> Xoay tọa độ điểm theo góc của ROI
                list.Add((s, e)); // Thêm đoạn thẳng vào danh sách
            }
            return list; // Trả về danh sách các vạch Caliper
        }

        private static Point2d Rotate(Point2d p, double cx, double cy, double angle) // Phương thức xoay 1 điểm quanh tâm cx, cy một góc angle (Radian)
        {
            double cos = Math.Cos(angle), sin = Math.Sin(angle); // Tính Cosin và Sin của góc xoay
            double dx = p.X - cx, dy = p.Y - cy; // Tính khoảng cách tương đối từ điểm đến tâm xoay
            return new Point2d(cx + dx * cos - dy * sin, cy + dx * sin + dy * cos); // Trả về tọa độ điểm mới sau khi xoay
        }

        /// <summary>Điểm cạnh tìm được (từ output port "EdgePoints") để vẽ chấm overlay.</summary>
        public IReadOnlyList<Point2d> ResultEdgePoints() // Lấy danh sách điểm cạnh phát hiện được từ cổng ra EdgePoints
            => _node.Tool.FindOutput("EdgePoints")?.Value is Point2d[] pts ? pts : Array.Empty<Point2d>();

        /// <summary>Đường fit được (từ output "Line") + cờ OK để tô màu — vẽ cả khi NG (giống VisionPro).</summary>
        public (Point2d p1, Point2d p2, bool ok)? ResultLine() // Lấy thông tin đoạn thẳng đã tìm được từ cổng ra Line
        {
            if (_node.Tool.FindOutput("Line")?.Value is LineResult lr) // Nếu cổng Line có dữ liệu LineResult
            {
                var s = lr.Segment; // Lấy đoạn thẳng kết quả
                if (s.P1.X != s.P2.X || s.P1.Y != s.P2.Y)   // Bỏ qua đoạn rỗng nếu chưa fit được đường thẳng
                    return (s.P1, s.P2, lr.Judge == Judge.OK); // Trả về điểm đầu, điểm cuối và cờ đánh giá OK/NG
            }
            return null; // Không có đường thẳng hợp lệ
        }

        /// <summary>
        /// Nếu ROI mặc định nằm ngoài ảnh thì đưa về giữa ảnh (kích thước kẹp trong ảnh) để tool ra kết quả ngay
        /// như VisionPro, thay vì NG vì caliper chạy ngoài ảnh. Chỉ chỉnh khi tâm ROI rõ ràng ngoài ảnh.
        /// </summary>
        public void RecenterRoiIfOutside() // Căn chỉnh tâm ROI vào giữa ảnh nếu vị trí mặc định nằm ngoài kích thước ảnh
        {
            var img = InputImage(); // Lấy ảnh đầu vào
            if (img is null || !HasRoi) return; // Nếu không có ảnh hoặc không có ROI thì dừng

            if (RoiKind == EditorRoiKind.Rect) // Nếu ROI dạng hình chữ nhật
            {
                var r = Roi; // Lấy dữ liệu ROI
                bool outside = r.Center.X < 0 || r.Center.Y < 0 || r.Center.X > img.Width || r.Center.Y > img.Height; // Kiểm tra tâm có nằm ngoài ảnh không
                if (outside) // Nếu nằm ngoài ảnh
                    Roi = new RotatedRectRegion( // Đặt lại ROI ở chính giữa ảnh
                        new Point2d(img.Width / 2.0, img.Height / 2.0),
                        Math.Min(r.Width, img.Width * 0.6), Math.Min(r.Height, img.Height * 0.6), r.AngleDeg);
            }
            else if (RoiKind == EditorRoiKind.Circle) // Nếu ROI dạng hình tròn
            {
                var c = RoiCircle; // Lấy dữ liệu ROI tròn
                bool outside = c.Center.X < 0 || c.Center.Y < 0 || c.Center.X > img.Width || c.Center.Y > img.Height; // Kiểm tra tâm có nằm ngoài ảnh không
                if (outside) // Nếu nằm ngoài ảnh
                    RoiCircle = new CircleRegion( // Đặt lại ROI tròn ở chính giữa ảnh
                        new Point2d(img.Width / 2.0, img.Height / 2.0),
                        Math.Min(c.Radius, Math.Min(img.Width, img.Height) * 0.35));
            }
        }

        /// <summary>Khởi tạo: chạy thượng nguồn (bất đồng bộ) để lấy ảnh, rồi hiển thị.</summary>
        public void Initialize() // Phương thức khởi tạo dữ liệu ban đầu cho cửa sổ
        {
            StatusText = "Loading…"; // Cập nhật trạng thái đang tải
            _ = InitAsync(); // Gọi thực thi khởi tạo bất đồng bộ
        }

        private async Task InitAsync() // Phương thức khởi tạo bất đồng bộ
        {
            // 1) Chạy CHỈ các node thượng nguồn (nhẹ) để lấy ảnh đầu vào → hiển thị NGAY,
            //    không chờ thuật toán nặng của node (vd HoughCircles) — đây là điểm làm UI mượt.
            _running = true; // Đánh dấu đang chạy
            BitmapSource? input = null; // Biến tạm chứa ảnh đầu vào
            try
            {
                input = await Task.Run(() => // Chạy tác vụ chuẩn bị dữ liệu đầu vào ở luồng nền
                {
                    try { _executor.PrepareInputs(_graph, _node); } catch { /* thiếu nguồn — xử lý bằng status */ } // Chuẩn bị dữ liệu từ các node phía trước
                    return ImagePreview.ToBitmapSource(InputImage()); // Chuyển đổi ảnh đầu vào sang BitmapSource
                });
            }
            finally
            {
                _running = false; // Giải phóng cờ đang chạy
                _initialized = true; // Đánh dấu đã khởi tạo xong
            }

            OutputJson = BuildOutputJson(); // Dựng chuỗi JSON mô tả kết quả cổng ra
            if (input is not null) // Nếu lấy được ảnh đầu vào thành công
            {
                BackgroundImage = input;          // Gán ảnh nền chính (hiển thị ngay)
                ResultImage = input; // Gán ảnh phụ
                StatusText = "Input loaded — running…"; // Cập nhật trạng thái
                RecenterRoiIfOutside();           // Căn lại ROI nếu bị nằm ngoài khung ảnh
            }
            else if (!RequiredInputsReady()) // Nếu thiếu các kết nối cổng vào bắt buộc
            {
                StatusText = "Connect an image to this node's input, then it runs automatically."; // Thông báo người dùng cần nối dây đầu vào
                return; // Dừng khởi tạo
            }

            // 2) Chạy node ở luồng nền → đổi ảnh lớn sang OUTPUT (tool không-ROI) khi xong.
            await RunAsync(); // Chạy thực thi chính node hiện tại
        }

        /// <summary>Lệnh Run thủ công (toolbar / sau khi vẽ ROI) — chạy ngay.</summary>
        [RelayCommand] // Đánh dấu lệnh RelayCommand cho nút bấm Run
        private Task RunNode() => RunAsync(); // Thực thi chạy ngay node
        // Khi người dùng nhấn nút "Run" trên giao diện, ứng dụng sẽ thực thi hàm RunAsync() để chạy lại node hiện tại ngay lập tức.

        /// <summary>Đổi tham số → lên lịch chạy lại (gộp nhiều thay đổi liên tiếp).</summary>
        private void RequestRun() // Yêu cầu chạy lại node thông qua cơ chế Debounce
        {
            _debounce.Stop(); // Dừng bộ đếm thời gian trì hoãn hiện tại
            _debounce.Start(); // Bắt đầu lại bộ đếm thời gian (chờ 180ms)
        }

        private async Task RunAsync() // Phương thức thực thi chạy node chính bất đồng bộ
        {
            if (!RequiredInputsReady()) // Kiểm tra nếu các cổng vào bắt buộc chưa đủ dữ liệu
            {
                StatusText = "Connect an image to this node's input, then it runs automatically."; // Đặt trạng thái yêu cầu kết nối
                return; // Dừng không chạy
            }
            if (_running) { _rerunPending = true; return; } // Nếu node đang chạy lượt trước -> Bật cờ hẹn chạy lại lượt sau rồi dừng

            _running = true; // Bật cờ báo đang chạy
            StatusText = "Running…"; // Đặt trạng thái đang xử lý
            try
            {
                var built = await Task.Run(() => // Đưa toàn bộ thuật toán xử lý nặng xuống luồng nền (Task.Run)
                {
                    var result = _executor.RunSingleNode(_graph, _node); // Cho Executor thực thi riêng một node này
                    var nr = result.Nodes.FirstOrDefault(); // Lấy kết quả thực thi của node
                    var imgs = BuildImages(); // Dựng ảnh hiển thị lớn và ảnh phụ
                    var status = nr is { State: ToolState.Failed } // Đánh giá trạng thái xử lý
                        ? "Error: " + nr.Error // Báo lỗi nếu thất bại
                        : $"OK • {nr?.ElapsedMs ?? 0} ms" + ResultSummary(); // Hiện thời gian xử lý và tóm tắt kết quả nếu thành công
                    return (imgs.big, imgs.small, status); // Trả về bộ 3 dữ liệu ảnh và trạng thái
                });

                BackgroundImage = built.big; // Cập nhật ảnh nền lớn lên UI
                ResultImage = built.small; // Cập nhật ảnh phụ lên UI
                StatusText = built.status; // Cập nhật thông tin trạng thái lên UI
                OutputJson = BuildOutputJson(); // Cập nhật chuỗi dữ liệu JSON cổng ra
            }
            catch (Exception ex) // Bắt ngoại lệ nếu xảy ra lỗi trong quá trình thực thi
            {
                StatusText = "Error: " + ex.Message; // Hiển thị thông điệp lỗi lên thanh trạng thái
            }
            finally
            {
                _running = false; // Đặt lại cờ không còn chạy
                if (_rerunPending) { _rerunPending = false; await RunAsync(); } // Nếu có yêu cầu chạy đệm trong lúc đang xử lý -> Thực hiện lượt chạy tiếp theo
            }
        }

        /// <summary>Tạo BitmapSource (đã Freeze) trên luồng nền. big = ảnh hiển thị lớn, small = preview phụ.</summary>
        private (BitmapSource? big, BitmapSource? small) BuildImages() // Phương thức quyết định loại ảnh sẽ gán cho các khung hiển thị
        {
            var input = InputImage(); // Lấy ảnh đầu vào của node
            var output = OutputImage(); // Lấy ảnh đầu ra của node
            IVisionImage? big, small; // Khai báo 2 biến tạm chứa ảnh chính và ảnh phụ

            if (HasRoi) // Nếu node có sử dụng ROI
            {
                // Tool ROI mà output CÙNG kích thước input (vd Find Line vẽ overlay kết quả lên ảnh) →
                // hiện OUTPUT để THẤY KẾT QUẢ NGAY (đường, caliper, điểm cạnh) như VisionPro; ROI vẫn vẽ/sửa đúng toạ độ.
                // Nếu output khác kích thước (vd crop của Region Selector) → hiện input để vẽ ROI đúng.
                bool sameSize = input is not null && output is not null
                    && output.Width == input.Width && output.Height == input.Height; // Kiểm tra ảnh đầu vào và ra có cùng kích thước không
                big = sameSize ? output : (input ?? output); // Chọn ảnh chính hiển thị
                small = sameSize ? input : output; // Chọn ảnh phụ hiển thị
            }
            else // Nếu node không dùng ROI
            {
                big = output ?? input; // Ảnh chính ưu tiên lấy ảnh Output
                small = input; // Ảnh phụ lấy ảnh Input
            }
            return (ImagePreview.ToBitmapSource(big), ImagePreview.ToBitmapSource(small)); // Chuyển đổi cả 2 ảnh sang BitmapSource an toàn cho UI
        }

        /// <summary>Dựng JSON từ các output port của node (ảnh → mô tả gọn).</summary>
        private string BuildOutputJson() // Dựng chuỗi JSON hiển thị dữ liệu kết quả tất cả các cổng ra
        {
            var dict = new Dictionary<string, object?>(); // Khởi tạo Dictionary chứa cặp Cổng ra - Giá trị
            foreach (var o in _node.Tool.Outputs) // Duyệt qua danh sách cổng ra của Tool
                dict[o.Name] = o.Value is IVisionImage img ? $"<image {img.Width}x{img.Height}>" : o.Value; // Nếu cổng ra là ảnh thì viết tắt mô tả kích thước, ngược lại lấy giá trị gốc
            try
            {
                return JsonSerializer.Serialize(dict, new JsonSerializerOptions // Chuỗi hóa Dictionary sang JSON đẹp định dạng thụt lề
                {
                    WriteIndented = true, // Định dạng thụt lề rõ ràng
                    Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } // Tự động chuyển Enum sang tên dạng chữ
                });
            }
            catch (Exception ex) { return "{ \"error\": \"" + ex.Message + "\" }"; } // Nếu gặp lỗi chuỗi hóa thì trả về thông điệp lỗi JSON
        }

        /// <summary>Tóm tắt kết quả node (cho tool có "Line") để hiện thẳng lên status: Judge, số điểm cạnh, score, rms.</summary>
        private string ResultSummary() // Phương thức tạo chuỗi tóm tắt kết quả xử lý
        {
            if (_node.Tool.FindOutput("Line")?.Value is not LineResult line) return ""; // Nếu node không phải loại xuất kết quả đường thẳng Line -> Trả về chuỗi rỗng
            int edges = (_node.Tool.FindOutput("EdgePoints")?.Value as Point2d[])?.Length ?? 0; // Lấy số lượng điểm cạnh tìm được
            double score = _node.Tool.FindOutput("Score")?.Value is double s ? s : 0; // Lấy điểm số tin cậy (Score)
            double rms = _node.Tool.FindOutput("RMSError")?.Value is double r ? r : 0; // Lấy sai số bình phương trung bình (RMSError)
            return $"  •  {line.Judge}  •  edges={edges}  •  score={score:F2}  •  rms={rms:F2}"; // Trả về chuỗi tóm tắt thông số
        }

        private bool RequiredInputsReady() // Kiểm tra các cổng đầu vào bắt buộc đã có kết nối/giá trị chưa
            => _node.Tool.Inputs.All(i => i.IsOptional || i.Value is not null); // Tất cả cổng vào phải là tùy chọn (Optional) hoặc giá trị không null

        private IVisionImage? InputImage() // Tìm lấy đối tượng ảnh đầu vào đầu tiên của node
        {
            foreach (var i in _node.Tool.Inputs) // Duyệt các cổng vào
                if (i.Value is IVisionImage img) return img; // Nếu có giá trị là IVisionImage -> Trả về ảnh
            return null; // Không có ảnh
        }

        private IVisionImage? OutputImage() // Tìm lấy đối tượng ảnh đầu ra đầu tiên của node
        {
            foreach (var o in _node.Tool.Outputs) // Duyệt các cổng ra
                if (o.Value is IVisionImage img) return img; // Nếu có giá trị là IVisionImage -> Trả về ảnh
            return null; // Không có ảnh
        }

        /// <summary>Khôi phục tham số về giá trị lúc mở (cho nút Cancel).</summary>
        public void Revert() // Phương thức khôi phục toàn bộ giá trị tham số về lúc ban đầu mở cửa sổ
        {
            foreach (var p in _node.Tool.Parameters) // Duyệt danh sách các tham số của node
                if (_original.TryGetValue(p.Name, out var v)) // Nếu tìm thấy tên trong từ điển gốc
                    p.Value = v; // Gán lại giá trị ban đầu
        }

        public void Dispose() // Phương thức giải phóng tài nguyên khi đóng ViewModel
        {
            _debounce.Stop(); // Dừng bộ đếm thời gian trì hoãn

            // KHÔNG gọi _executor.Dispose() ở đây nữa!
            // Lý do: _executor này chạy trực tiếp trên các Tool instance DÙNG CHUNG với _graph
            // (main canvas). DisposeTracked() bên trong sẽ giải phóng ảnh đang nằm ở Output của
            // các node thượng nguồn (GrabImage, Threshold...) — nhưng đó là ảnh CHUNG với đồ thị
            // chính, không phải bản sao riêng của cửa sổ này. Dispose chúng ở đây khiến các node
            // đó cầm tham chiếu tới ảnh đã giải phóng, gây crash khi mở lại / chạy Flow chính.
            //
            // Việc dọn dẹp ảnh cũ nên để cho:
            //   1. Main canvas's FlowExecutor.Run() tự dọn dẹp giữa 2 lần "Run Flow" của NÓ
            //      (đã đúng sẵn — xem dòng đầu hàm Run() có gọi DisposeTracked() rồi).
            //   2. Mỗi lần 1 Tool re-execute, ảnh mới sẽ tự ghi đè ảnh cũ ở chính Output đó
            //      (ảnh cũ trở thành rác không tham chiếu, sẽ được GC dọn dần — chấp nhận được,
            //      không gây crash, chỉ là không giải phóng native memory NGAY LẬP TỨC).
        }
    }
}