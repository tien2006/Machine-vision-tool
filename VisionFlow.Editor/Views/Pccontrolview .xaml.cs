// VỊ TRÍ ĐẶT FILE: project VisionFlow.Editor (WPF), thư mục Views/ — namespace VisionFlow.Editor.Views.
// Toàn bộ hành vi nằm trong PcControlViewModel (MVVM thuần) — CHỈ 1 ngoại lệ: PasswordBox.Password là thuộc tính
// bảo mật của WPF, KHÔNG hỗ trợ Binding trực tiếp (tránh lộ mật khẩu qua Binding debug/Snoop) — nên bắt buộc
// phải đẩy giá trị qua code-behind như dưới đây, đây là cách làm chuẩn của Microsoft, không phải lỗi kiến trúc.

using System.Windows;
using System.Windows.Controls;

namespace VisionFlow.Editor.Views;

public partial class PcControlView : UserControl
{
    public PcControlView()
    {
        InitializeComponent();
    }

    private void LoginPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is VisionFlow.Editor.ViewModels.PcControlViewModel vm && sender is PasswordBox box)
            vm.LoginPassword = box.Password;
    }
}