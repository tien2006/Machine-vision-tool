
using System.Windows;
using System.Windows.Input;     // MouseButtonEventArgs
using System.Windows.Media;     // VisualTreeHelper
using NodeNetwork.Views;
using VisionFlow.Editor.ViewModels;
using VisionFlow.Editor.Views;

namespace VisionFlow.WPF;

public partial class MainWindow : Window    // Lớp kế thừa Window nên nó chính là một cửa sổ WPF.
{
    private readonly FlowEditorViewModel _viewModel;    // readonly nghĩa là chỉ gán được một lần trong constructor, sau đó không đổi
                                                        // — đúng với mô hình DI: ViewModel được inject một lần và dùng suốt vòng đời cửa sổ.
    public MainWindow(FlowEditorViewModel viewModel)
    {
        InitializeComponent();      // để dựng cây UI từ XAML
        _viewModel = viewModel;
        DataContext = viewModel;    // gán vào DataContext để data binding trong XAML trỏ tới ViewModel này

        // Double-click một node trên canvas → mở cửa sổ cấu hình tham số (kèm ảnh + ROI).
        NetworkCanvas.PreviewMouseLeftButtonDown += OnNetworkPreviewMouseDown;
        // - NetworkCanvas: Là tên định danh (x:Name="NetworkCanvas") của control NetworkView hiển thị
        //   vùng làm việc/canvas sơ đồ node (đã được khai báo bên file XAML).
        // - PreviewMouseLeftButtonDown: Sự kiện phím chuột trái được nhấn xuống.
        // Trong WPF có 2 loại sự kiện tương tác chuột chính:
        //      Direct / Bubbling Event(MouseLeftButtonDown): Sự kiện phát ra từ trong ra ngoài(từ phần tử con nhỏ nhất bị click
        //          ➔ lan dần lên các khung cha ➔ ra đến Window).
        //      Tunneling Event(PreviewMouseLeftButtonDown): Sự kiện phát ra từ ngoài vào trong(từ Window ➔ qua các khung cha
        //          ➔ đi xuống dần phần tử con bị click).
        // - Preview... giúp MainWindow chặn đầu (intercept) và bắt được cú nhấp chuột trước khi các thành phần con
        //   inside thư viện NodeNetwork kịp can thiệp hoặc nuốt mất sự kiện.
    }

    private void OnNetworkPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 1. Kiểm tra xem có phải double-click không
        if (e.ClickCount != 2) return;

        // 2. Tìm NodeView mà người dùng đã click vào
        var nodeView = FindAncestor<NodeView>(e.OriginalSource as DependencyObject);
        if (nodeView?.DataContext is not ToolNodeViewModel nodeVm) return;

        // 3. Đánh dấu sự kiện đã được xử lý (ngăn các xử lý mặc định khác)
        // Handled = true chặn sự kiện lan tiếp (tránh node bị chọn/kéo). Cuối cùng tạo ViewModel cho cửa sổ con qua
        e.Handled = true;

        // 4. Khởi tạo ViewModel và hiển thị Cửa sổ Parameter Editor
        var editorVm = _viewModel.CreateParameterEditor(nodeVm);
        // _viewModel.CreateParameterEditor(nodeVm): Gọi hàm ở ViewModel chính để chuẩn bị dữ liệu cấu hình cho Node vừa click.

        var window = new ParameterEditorWindow(editorVm) { Owner = this };
        window.ShowDialog();    // Mở cửa sổ ParameterEditorWindow dưới dạng Modal Dialog (chế độ cửa sổ con - người dùng
                                // phải thao tác xong hoặc đóng cửa sổ này mới quay lại làm việc tiếp trên sơ đồ chính được).
        // Owner = this: Đặt MainWindow làm cửa sổ cha, giúp cửa sổ Parameter Editor luôn nằm đè lên trên MainWindow
        // và tự động thu nhỏ/đóng theo cửa sổ cha.
    }

    // Thuật toán tìm phần tử cha trên Visual Tree (FindAncestor<T>)
    // Mục đích: Đây là một hàm Helper kinh điển trong WPF để duyệt ngược cây giao diện (Visual Tree).
    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
        // where T : DependencyObject: Generic Constraint (Ràng buộc kiểu): Yêu cầu kiểu T truyền vào phải là
        // một thành phần UI của WPF (DependencyObject).
    {
        while (current is not null)
        {
            if (current is T t) return t;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }
}