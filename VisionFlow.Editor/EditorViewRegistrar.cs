using NodeNetwork.Views; // Chứa các WPF View dựng sẵn: NodeView, NodeInputView, NodeOutputView
using ReactiveUI; // Thư viện ReactiveUI chứa giao diện IViewFor
using Splat; // Dependency Injector / Service Locator của ReactiveUI
using VisionFlow.Editor.ViewModels; // Namespace chứa các ViewModel của Editor

namespace VisionFlow.Editor;

/// <summary>
/// Đăng ký thủ công các View trong NodeNetwork cho các ViewModel tùy chỉnh của VisionFlow.
/// </summary>
public static class EditorViewRegistrar
{
    /// <summary>
    /// - Hàm thực thi đăng ký các View với Service Locator (Splat). Cần gọi 1 lần khi ứng dụng bắt đầu khởi chạy (App Startup).
    /// - Lý do bạn bắt buộc phải có EditorViewRegistrar là vì kiến trúc MVVM kết hợp với thư viện ReactiveUI / Splat (Service Locator) 
    ///   mà thư viện đồ thị NodeNetwork đang sử dụng.
    /// </summary>

    // Nếu không có class này, khi ứng dụng chạy, màn hình thiết kế của bạn sẽ hoàn toàn trắng xóa hoặc không thể hiển thị (render) các Node và các Cổng (Port).
    // Chi tiết lý do nằm ở 3 điểm cốt lõi sau:
    // 1. Cơ chế tự động tìm kiếm View của ReactiveUI (DataTemplate Mapping): 
    //    - Trong WPF thuần, khi bạn có một ViewModel và muốn hiển thị nó ra giao diện, bạn thường dùng DataTemplate trong XAML.
    //    - Tuy nhiên, thư viện NodeNetwork xây dựng dựa trên ReactiveUI. ReactiveUI sử dụng một cơ chế gọi là View Location via Splat Locator:
    //    - Khi danh sách các Node hiện lên màn hình, khung nhìn (Canvas) chỉ nhận được danh sách các ViewModel (ví dụ: ToolNodeViewModel, PortInputViewModel).
    //    - Khung nhìn sẽ hỏi hệ thống Splat (Locator.Current): "Tôi đang có một đối tượng kiểu ToolNodeViewModel, cho tôi biết View (UI) tương ứng để vẽ nó lên màn hình là gì?"
    //    - Splat sẽ tra cứu trong "danh bạ" mà bạn đã đăng ký để trả về giao diện tương ứng (NodeView, NodeInputView...).
    // 2. Vì sao NodeNetwork mặc định KHÔNG TỰ BIẾT các class của bạn?
    //    - Mặc định, thư viện NodeNetwork chỉ biết cách vẽ các class ViewModel nguyên bản của nó (như NodeViewModel, PortViewModel).
    //    - Trong dự án VisionFlow, bạn đã tạo ra các class ViewModel mở rộng riêng để phục vụ bài toán xử lý ảnh:
    //     * ToolNodeViewModel (kế thừa hoặc tùy biến từ Node của NodeNetwork)
    //     * PortInputViewModel (Cổng vào tùy biến)
    //     * PortOutputViewModel (Cổng ra tùy biến)
    //     -> Do đây là các class do chính bạn tự viết thêm, thư viện NodeNetwork không hề biết sự tồn tại của chúng.
    // 3. Vai trò "Ghép đôi" của EditorViewRegistrar"
    //    - Đoạn code trong EditorViewRegistrar.Register() chính là thao tác khai báo ghép đôi

    public static void Register()
    {
        // 1. Nếu bạn muốn khởi tạo môi trường mặc định của NodeNetwork (Bao gồm cả các View mặc định)
        // Bạn có thể gọi dòng dưới đây (NodeNetwork sẽ tự lo việc đăng ký mặc định của nó):
        // NodeNetwork.NNViewRegistrar.RegisterSplat();

        // 2. Đăng ký đè (Override) các ViewModel tùy chỉnh của bạn với các View dựng sẵn của NodeNetwork
        // "Hễ gặp dữ liệu ToolNodeViewModel thì hãy dùng giao diện NodeView để vẽ!"
        Locator.CurrentMutable.Register(() => new NodeView(), typeof(IViewFor<ToolNodeViewModel>));

        // "Hễ gặp dữ liệu PortInputViewModel thì hãy dùng giao diện NodeInputView để vẽ!"
        Locator.CurrentMutable.Register(() => new NodeInputView(), typeof(IViewFor<PortInputViewModel>));

        // "Hễ gặp dữ liệu PortOutputViewModel thì hãy dùng giao diện NodeOutputView để vẽ!"
        Locator.CurrentMutable.Register(() => new NodeOutputView(), typeof(IViewFor<PortOutputViewModel>));
    }
}