using Castle.Components.DictionaryAdapter.Xml;
using CommunityToolkit.Mvvm.ComponentModel; // Nhập thư viện hỗ trợ MVVM ComponentModel từ Toolkit
using CommunityToolkit.Mvvm.Input; // Nhập thư viện hỗ trợ RelayCommand cho MVVM
using Microsoft.Win32; // Nhập thư viện để sử dụng hộp thoại chọn file của Windows (OpenFileDialog)
using VisionFlow.Core.Tools; // Nhập không gian tên chứa các đối tượng ToolParameter

namespace VisionFlow.Editor.ViewModels // Định nghĩa không gian tên chứa ViewModel cho Editor
{
    /// <summary>
    /// Phân loại editor phù hợp cho một tham số.
    /// </summary>
    public enum ParameterKind
    {
        Text,       // Kiểu văn bản thường
        Integer,    // Kiểu số nguyên
        Number,     // Kiểu số thực
        Bool,       // Kiểu đúng/sai (Boolean)
        Enum,       // Kiểu danh sách tùy chọn (Enumeration / Choices)
        FilePath,   // Kiểu đường dẫn tập tin (1 file cụ thể) -> OpenFileDialog
        FolderPath, // MỚI: Kiểu đường dẫn thư mục -> OpenFolderDialog (khác hẳn FilePath)
        ReadOnly    // Kiểu chỉ đọc (không cho chỉnh sửa trực tiếp)
    }

    /// <summary>
    /// Adapter cho Parameter View: bọc một <see cref="IToolParameter"/> và chọn widget phù hợp
    /// (slider có min/max cho số, file picker cho đường dẫn, combobox cho enum/choice, checkbox cho bool).
    /// Sửa giá trị ghi thẳng vào tham số tool; mọi thay đổi raise PropertyChanged để cửa sổ tự Run lại.
    /// </summary>
    public sealed partial class ParameterEditorViewModel : ObservableObject // Khai báo lớp ViewModel kế thừa từ ObservableObject
    {
        private readonly IToolParameter _parameter; // Biến lưu trữ tham số gốc (IToolParameter) dạng chỉ đọc

        /// <summary>
        /// Khởi tạo ViewModel cho một tham số tool và phân tích kiểu dữ liệu để xác định Editor Widget phù hợp.
        /// </summary>
        /// <param name="parameter">Tham số gốc IToolParameter cần bọc</param>
        public ParameterEditorViewModel(IToolParameter parameter) // Hàm khởi tạo nhận tham số gốc
        {
            _parameter = parameter; // Gán tham số truyền vào cho biến nội bộ

            var t = Nullable.GetUnderlyingType(parameter.ValueType) ?? parameter.ValueType;
            // - "bóc tách" kiểu dữ liệu gốc (underlying type) ra khỏi một kiểu dữ liệu Nullable (có thể gán null)
            // - Nullable.GetUnderlyingType(...): Hàm này kiểm tra xem kiểu dữ liệu truyền vào có phải là dạng Nullable<T> hay không:
            //   Nếu ĐÚNG là kiểu Nullable (ví dụ: int? hay Nullable<int>): Hàm sẽ bóc lớp vỏ Nullable và trả về kiểu dữ liệu gốc bên trong là int(không còn ? nữa).
            //   Nếu KHÔNG PHẢI kiểu Nullable (ví dụ: int thường, string, bool thường): Hàm sẽ trả về null.
            // - Toán tử ??: kiểm tra kết quả bên trái:
            //   Nếu bên trái khác null -> Lấy giá trị bên trái (chính là kiểu gốc vừa được bóc tách).
            //   Nếu bên trái bằng null (do ValueType vốn là kiểu thường, không phải Nullable) -> Lấy luôn parameter.ValueType ban đầu.

            if (parameter.Choices is { Count: > 0 }) // = if (parameter.Choices != null && parameter.Choices.Count > 0)
            {
                Kind = ParameterKind.Enum; // Đặt phân loại là Enum
                EnumOptions = parameter.Choices.Cast<object>().ToList(); // Ép danh sách Choices sang kiểu List<object>
                // .Cast<object>(): Do parameter.Choices có thể đang mang một kiểu cụ thể (như List<string> hay IEnumerable<MyEnum>),
                // hàm này ép từng phần tử trong đó về kiểu chung là object
                // .ToList(): Gom tất cả lại thành một danh sách List<object> mới và gán vào EnumOptions để phục vụ Data Binding lên ComboBox trong WPF.
                // Ex:
                    // List<string>: ["Either", "PosToNeg", "NegToPos"]
                    // IEnumerable<MyEnum>: [EdgePolarity.Either, EdgePolarity.PosToNeg, EdgePolarity.NegToPos] (chúng là các hằng số định danh Enum, không phải chuỗi chữ)
                    // Kiểu chung object: là lớp cha cao nhất của tất cả các kiểu dữ liệu.
                        // object item1 = "Grab Image";   // Chứa string
                        // object item2 = 100;            // Chứa int
                        // object item3 = true;           // Chứa bool
                        // object item4 = EdgePolarity.Either; // Chứa Enum
                    // List<object>: 
                        // List<object> mixedList = new List<object>
                        //  {
                        //      "Grab Image",          // string
                        //      24,                    // int (Number Of Calipers)
                        //      true,                  // bool (Draw Fitted Circle)
                        //      EdgePolarity.PosToNeg  // Enum
                        //  };

            }
            else if (t.IsEnum) // Nếu kiểu dữ liệu gốc thực sự là một Enum
            {
                Kind = ParameterKind.Enum; // Đặt phân loại là Enum
                EnumOptions = Enum.GetValues(t).Cast<object>().ToList(); // Lấy tất cả giá trị của Enum và chuyển thành List<object>
            }
            else if (t == typeof(bool)) Kind = ParameterKind.Bool; // Nếu là kiểu bool -> phân loại Bool
            else if (t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte)) Kind = ParameterKind.Integer; // Nếu thuộc các kiểu số nguyên -> phân loại Integer
            else if (t == typeof(double) || t == typeof(float) || t == typeof(decimal)) Kind = ParameterKind.Number; // Nếu thuộc các kiểu số thực -> phân loại Number
            else if (t == typeof(string)) // Nếu thuộc kiểu chuỗi (string)
            {
                // QUAN TRỌNG: phải kiểm tra "Folder" TRƯỚC "Path", vì "FolderPath" cũng chứa chữ "Path" bên trong.
                // Nếu kiểm tra "Path" trước, FolderPath sẽ luôn rơi nhầm vào nhánh FilePath và không bao giờ
                // tới được nhánh FolderPath -> UI sẽ hiện sai loại hộp thoại (chọn file thay vì chọn thư mục).
                if (parameter.Name.Contains("Folder", StringComparison.OrdinalIgnoreCase))
                    Kind = ParameterKind.FolderPath;
                else if (parameter.Name.Contains("Path", StringComparison.OrdinalIgnoreCase)
                         || parameter.Name.Contains("File", StringComparison.OrdinalIgnoreCase))
                    Kind = ParameterKind.FilePath;
                else
                    Kind = ParameterKind.Text;
            }
            else Kind = ParameterKind.ReadOnly;

            //else if (t == typeof(string)) // Nếu thuộc kiểu chuỗi (string)
            //    Kind = (parameter.Name.Contains("Path", StringComparison.OrdinalIgnoreCase) // Tên chứa "Path"
            //            || parameter.Name.Contains("File", StringComparison.OrdinalIgnoreCase)) // Hoặc tên chứa "File"
            //            // StringComparison.OrdinalIgnoreCase: 
            //                // Ordinal: So sánh theo mã byte/ASCII của ký tự (cho tốc độ xử lý nhanh nhất).
            //                // IgnoreCase: Không quan tâm chữ hoa hay chữ thường.
            //        ? ParameterKind.FilePath // Thì phân loại là đường dẫn file (FilePath)
            //        : ParameterKind.Text; // Nối không thì phân loại là chuỗi văn bản thường (Text)
            //else Kind = ParameterKind.ReadOnly; // Các kiểu dữ liệu phức tạp khác -> phân loại chỉ đọc (ReadOnly)

            // Khoảng giá trị cho slider (chỉ khi có cả min lẫn max và khác nhau).
            if ((Kind == ParameterKind.Number || Kind == ParameterKind.Integer) // Nếu là kiểu số (Number hoặc Integer)
                && parameter.Minimum is not null && parameter.Maximum is not null) // Và có cài đặt cả Minimum lẫn Maximum
            {
                MinValue = System.Convert.ToDouble(parameter.Minimum); // Chuyển đổi giá trị Minimum sang double
                MaxValue = System.Convert.ToDouble(parameter.Maximum); // Chuyển đổi giá trị Maximum sang double
                HasRange = MaxValue > MinValue; // Đánh dấu HasRange = true nếu MaxValue thực sự lớn hơn MinValue
            }
        }

        public string Name => _parameter.DisplayName; // Thuộc tính lấy tên hiển thị của tham số
        public string Category => _parameter.Category; // Thuộc tính lấy nhóm (phân loại Category) của tham số
        public ParameterInteraction Interaction => _parameter.Interaction; // Thuộc tính lấy hình thức tương tác (ROI, Graph, ...) của tham số
        public ParameterKind Kind { get; } // Thuộc tính lấy kiểu phân loại Editor Kind
        public IReadOnlyList<object> EnumOptions { get; } = Array.Empty<object>(); // Danh sách chứa các lựa chọn Enum (mặc định là mảng rỗng)

        public double MinValue { get; } // Giá trị nhỏ nhất dùng cho Slider
        public double MaxValue { get; } // Giá trị lớn nhất dùng cho Slider
        public bool HasRange { get; } // Cờ báo tham số có khoảng giới hạn Min-Max hợp lệ hay không

        public bool IsEnum => Kind == ParameterKind.Enum; // Cờ kiểm tra có phải dạng Enum/Choices hay không
        public bool IsBool => Kind == ParameterKind.Bool; // Cờ kiểm tra có phải dạng Boolean (Checkbox) hay không
        public bool IsFilePath => Kind == ParameterKind.FilePath; // Cờ kiểm tra có phải dạng đường dẫn File hay không
        public bool IsFolderPath => Kind == ParameterKind.FolderPath; // MỚI: UI dùng cờ này để hiện nút "Browse Folder"
        public bool IsSlider => (Kind is ParameterKind.Number or ParameterKind.Integer) && HasRange; // Cờ kiểm tra xem có đủ điều kiện hiện Slider không
        public bool IsPlainText => (Kind is ParameterKind.Text or ParameterKind.Number or ParameterKind.Integer) // Cờ kiểm tra xem có hiển thị TextBox thường không
                                   && !IsFilePath && !IsSlider;
        public bool IsReadOnly => Kind == ParameterKind.ReadOnly; // Cờ kiểm tra tham số ở chế độ chỉ đọc không
        public bool IsInteger => Kind == ParameterKind.Integer; // Cờ kiểm tra tham số có phải kiểu số nguyên không

        /// <summary>Giá trị hai chiều cho text/enum/bool/filepath.</summary>
        public object? Value
        {
            get => _parameter.Value; // Lấy giá trị trực tiếp từ tham số gốc
            set
            {
                _parameter.Value = value; // Gán giá trị mới vào tham số gốc
                OnPropertyChanged(); // Thông báo cho UI cập nhật thuộc tính Value ở Textbox
                OnPropertyChanged(nameof(NumericValue)); // Báo cho Slider cập nhật theo!
            }
        }

        /// <summary>Proxy double cho Slider (ép về int nếu tham số là số nguyên). An toàn nếu giá trị không phải số.</summary>
        public double NumericValue
        {
            get => _parameter.Value switch // Ép/chuyển đổi giá trị gốc về kiểu double để Binding với Slider
            {
                null => 0, // Nếu null trả về 0
                double d => d, // Nếu đã là double thì trả về trực tiếp
                int i => i, // Nếu là int thì ép kiểu ngầm định về double
                // - C# nhận thấy tập giá trị của int nằm hoàn toàn trong khoảng biểu diễn của double, nên nó tự động biến đổi
                //   số nguyên i thành số thực double ngay thời điểm trả về (return).
                // - Bạn không cần phải viết thủ công int i => (double)i hay int i => Convert.ToDouble(i) vì
                //   trình biên dịch C# đã làm điều đó giúp bạn một cách tối ưu nhất!
                IConvertible c when _parameter.Value is not string => SafeToDouble(c), // Nếu thực thi IConvertible và không phải chuỗi -> chuyển đổi an toàn
                // IConvertible là một Interface có sẵn trong thư viện nền tảng của C#. Trong C#, hầu hết các kiểu dữ liệu
                // số cơ bản như int, float, double, decimal, byte, short, long... đều thực thi (implement) interface IConvertible này.
                _ => 0 // Trường hợp còn lại mặc định trả về 0
            };
            set
            {
                _parameter.Value = IsInteger ? (object)(int)Math.Round(value) : value; // Nếu tham số là số nguyên thì làm tròn rồi gán kiểu int, ngược lại gán double
                OnPropertyChanged(); // Thông báo cho UI cập nhật thuộc tính NumericValue
                OnPropertyChanged(nameof(Value)); // Thông báo đồng bộ lại thuộc tính Value
            }
        }

        private static double SafeToDouble(IConvertible c) // Phương thức trợ giúp chuyển đổi kiểu dữ liệu về double an toàn
        {
            try { return c.ToDouble(System.Globalization.CultureInfo.InvariantCulture); } // Thử chuyển đổi dùng chuẩn mã hóa InvariantCulture
            // System.Globalization.CultureInfo.InvariantCulture: 
                // Trên thế giới, cách ghi số thực (số thập phân) giữa các quốc gia có sự khác nhau về dấu chấm (.) và dấu phẩy (,):
                // -> C# sẽ ép buộc chương trình luôn hiểu dấu chấm (.) là dấu phân cách thập phân, giúp ứng dụng chạy ổn định 100% trên mọi máy tính ở bất kỳ quốc gia nào.
            catch { return 0; } // Nếu xảy ra lỗi ép kiểu thì trả về 0
        }

        [RelayCommand] // Đánh dấu tạo Command tự động (BrowseCommand)
        private void Browse() // Phương thức thực thi khi ấn nút chọn file
        {
            var dialog = new OpenFileDialog // Khởi tạo hộp thoại mở file Windows
            {
                Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff|All files|*.*" // Cấu hình bộ lọc định dạng tệp ảnh
            };
            if (dialog.ShowDialog() == true) // Hiển thị hộp thoại, nếu người dùng bấm chọn OK/Open
                Value = dialog.FileName; // Gán đường dẫn file đã chọn vào thuộc tính Value
        }
        [RelayCommand] // Đánh dấu tạo Command tự động (BrowseFolderCommand)
        private void BrowseFolder() // Phương thức thực thi khi ấn nút chọn THƯ MỤC
        {
            // Microsoft.Win32.OpenFolderDialog được bổ sung từ .NET 8 (WPF) - hộp thoại chọn thư mục chuẩn hệ điều hành.
            // Nếu project của bạn target .NET Framework / .NET 6-7 (chưa có OpenFolderDialog),
            // thay bằng System.Windows.Forms.FolderBrowserDialog (cần thêm reference System.Windows.Forms trong .csproj).
            var dialog = new OpenFolderDialog
            {
                Title = "Chọn thư mục chứa ảnh test"
            };
            if (dialog.ShowDialog() == true)
                Value = dialog.FolderName; // Gán đường dẫn thư mục đã chọn vào tham số FolderPath
        }
    }
}