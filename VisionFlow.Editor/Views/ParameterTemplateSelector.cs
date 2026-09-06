using System.Windows; // Nhập thư viện nền tảng WPF chứa các lớp cơ bản (DependencyObject, DataTemplate)
using System.Windows.Controls; // Nhập thư viện Controls WPF chứa lớp DataTemplateSelector
using VisionFlow.Editor.ViewModels; // Nhập không gian tên chứa ParameterEditorViewModel

namespace VisionFlow.Editor.Views // Định nghĩa không gian tên chứa các View/Controls của Editor
{
    /// <summary>
    /// Chọn DataTemplate theo loại tham số. QUAN TRỌNG: không gộp nhiều widget trong một template — WPF
    /// đánh giá mọi binding kể cả widget ẩn, gây lỗi kiểu (vd Slider bind NumericValue cho param chuỗi →
    /// FormatException; CheckBox bind enum → BooleanConverter). Selector đảm bảo chỉ widget đúng kiểu tồn tại.
    /// </summary>
    public sealed class ParameterTemplateSelector : DataTemplateSelector // Lớp chọn Template giao diện kế thừa từ DataTemplateSelector
    {
        public DataTemplate? PlainTextTemplate { get; set; } // Template hiển thị ô nhập văn bản/số thông thường (TextBox)
        public DataTemplate? FilePathTemplate { get; set; } // Template hiển thị ô chọn đường dẫn file (TextBox + Nút Browse)
        public DataTemplate? FolderPathTemplate { get; set; } // MỚI: template riêng cho tham số chọn thư mục
        public DataTemplate? SliderTemplate { get; set; } // Template hiển thị thanh trượt chọn khoảng số (Slider)
        public DataTemplate? BoolTemplate { get; set; } // Template hiển thị hộp chọn đúng/sai (CheckBox)
        public DataTemplate? EnumTemplate { get; set; } // Template hiển thị danh sách lựa chọn (ComboBox)
        public DataTemplate? ReadOnlyTemplate { get; set; } // Template hiển thị giá trị chỉ đọc (TextBlock)

        /// <summary>
        /// Phương thức ghi đè để quyết định DataTemplate nào sẽ được sử dụng dựa trên kiểu của tham số.
        /// </summary>
        /// <param name="item">Đối tượng dữ liệu (ParameterEditorViewModel)</param>
        /// <param name="container">Thành phần giao diện chứa template</param>
        /// <returns>DataTemplate tương ứng hoặc template mặc định</returns>
        public override DataTemplate? SelectTemplate(object item, DependencyObject container) // Phương thức thực thi chọn Template tự động
        {
            if (item is ParameterEditorViewModel p) // Ép kiểu đối tượng item sang ParameterEditorViewModel
            {
                // QUAN TRỌNG: kiểm tra IsFolderPath TRƯỚC IsFilePath.
                // Về lý thuyết 2 cờ này loại trừ nhau (Kind chỉ nhận 1 giá trị) nên thứ tự if không bắt buộc,
                // nhưng đặt Folder lên trước giúp code đọc nhất quán với thứ tự ưu tiên đã sửa ở ParameterEditorViewModel.
                if (p.IsFolderPath) return FolderPathTemplate; // MỚI
                if (p.IsFilePath) return FilePathTemplate; // Nếu là tham số đường dẫn file -> Trả về FilePathTemplate
                if (p.IsBool) return BoolTemplate; // Nếu là tham số dạng đúng/sai -> Trả về BoolTemplate
                if (p.IsEnum) return EnumTemplate; // Nếu là tham số dạng danh sách tùy chọn -> Trả về EnumTemplate
                if (p.IsReadOnly) return ReadOnlyTemplate; // Nếu là tham số chỉ đọc -> Trả về ReadOnlyTemplate
                if (p.IsSlider) return SliderTemplate; // Nếu là tham số dạng số có khoảng Slider -> Trả về SliderTemplate
                return PlainTextTemplate; // Các trường hợp còn lại mặc định trả về PlainTextTemplate
            }
            return base.SelectTemplate(item, container); // Nếu item không phải ParameterEditorViewModel thì gọi xử lý mặc định của lớp cha
        }
    }
}