using Microsoft.Extensions.DependencyInjection;
using NodeNetwork;
using OpenCvSharp;
using System.Windows;
using VisionFlow.Core.Registry;
using VisionFlow.Editor.ViewModels;
using VisionFlow.Engine.Persistence;
using VisionFlow.Tools.Acquisition;
using VisionFlow.Tools.Finding;
using VisionFlow.Tools.Preprocess;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace VisionFlow.WPF;

public partial class App : Application
{
    private ServiceProvider? _provider;
    // ServiceProvider: Đóng vai trò là Bộ quản lý trung tâm/ DI Container (Bộ quản lý Dependency Injection của .NET). Nó chứa và quản lý tất cả các
    // Dependency (Service, ViewModel, Window...) trong suốt vòng đời của phần mềm.
    // Nếu ServiceCollection là "Cuốn sách ghi công thức/quy tắc", thì ServiceProvider chính là "Người đầu bếp thực sự" đứng ra
    // khởi tạo và phân phát các đối tượng đó khi có nơi yêu cầu.
    // ServiceProvider có 3 nhiệm vụ cốt lõi:
    //   1. Khởi tạo đối tượng (Instantiation):
    //      Thực hiện tạo ra thực thể (instance) của các lớp khi bạn yêu cầu, dựa trên các quy tắc đăng ký trước đó (AddSingleton, AddTransient, AddScoped).
    //   2. Tự động giải quyết phụ thuộc (Dependency Resolution / Auto-wiring):
    //      Khi bạn xin một đối tượng (ví dụ MainWindow), ServiceProvider sẽ tự động "soi" xem MainWindow cần những gì trong Constructor của nó
    //      (ví dụ cần FlowEditorViewModel). Nó sẽ tự đi tìm, khởi tạo FlowEditorViewModel trước rồi mới "bơm" (inject) vào để tạo ra MainWindow hoàn chỉnh.
    //   3. Quản lý vòng đời (Lifetime Management):
    //      Nếu dịch vụ được đăng ký là Singleton: ServiceProvider chỉ tạo đúng 1 lần duy nhất và dùng lại instance đó cho tất cả những lần xin sau.
    //      Khi ứng dụng tắt (gọi _provider.Dispose()), ServiceProvider sẽ tự động giải phóng bộ nhớ cho tất cả các service triển khai IDisposable mà nó đã quản lý.

    protected override void OnStartup(StartupEventArgs e)
    // Hàm này tự động chạy ngay khi ứng dụng bắt đầu khởi động, thay thế cho hàm Main() truyền thống.
    {
        base.OnStartup(e);

        // Đăng ký view/viewmodel của NodeNetwork vào Splat locator (bắt buộc trước khi hiện NetworkView).
        NNViewRegistrar.RegisterSplat();

        // Đăng ký view cho các ViewModel lớp-con của VisionFlow (node/port) — nếu không, view location
        // trả null và node/port không render.
        VisionFlow.Editor.EditorViewRegistrar.Register();

        // ServiceCollection(): khởi tạo một chiếc "giỏ chứa" (hoặc bảng đăng ký) để bạn bắt đầu khai báo tất cả các dịch vụ (Services), ViewModel, Window...
        // cho hệ thống Dependency Injection (DI) trong .NET.
        var services = new ServiceCollection();     // STEP1: LẤY RA 1 TỜ GIẤY TRẮNG
        ConfigureServices(services);                // STEP 2: VIẾT DANH SÁCH LINH KIỆN VÀ QUY TRÌNH LẮP RÁP LÊN GIẤY
        _provider = services.BuildServiceProvider();    // STEP 3: XÂY DỰNG XONG NHÀ MÁY SẢN XUẤT XE HƠI HOÀN CHỈNH DỰA TRÊN TỜ GIẤY ĐÓ
        // Nếu xem services (IServiceCollection) là "Cuốn sách ghi danh sách quy tắc", thì câu lệnh BuildServiceProvider()
        // chính là hành động "đóng bản vẽ" và khởi tạo ra "Bộ máy quản lý/Nhà máy sản xuất" (ServiceProvider).
        // 1. "Đóng băng" danh sách dịch vụ (Locking / Freezing):
        //   - Trước dòng code này, bạn có thể thoải mái thêm các service vào services qua các hàm services.AddSingleton(...), services.AddTransient(...).
        //   - Khi câu lệnh BuildServiceProvider() được gọi:
        //      Hệ thống sẽ kiểm tra toàn bộ quy tắc bạn đã đăng ký trong services.
        //      Nó chép toàn bộ danh sách này vào một cấu trúc dữ liệu tối ưu (thường là Hash Table / Dictionary nội bộ) để chuẩn bị cho việc tra cứu cực nhanh sau này.
        //      Sau bước này, bạn không thể thêm hay bớt service vào services được nữa.
        // 2. Xây dựng "Bản đồ phụ thuộc" (Dependency Graph):
        //   - BuildServiceProvider() sẽ phân tích xem giữa các class có mối liên hệ như thế nào:
        //      MainWindow cần FlowEditorViewModel.
        //      FlowEditorViewModel cần IToolRegistry và IFlowRepository.
        //      IToolRegistry trỏ về instance ToolRegistry.
        //      -> Nó sẽ tự tạo ra một "bản đồ" để biết chính xác phải tạo cái nào trước, cái nào sau khi có nơi yêu cầu.
        // 3. Gán đối tượng đã hoàn thiện vào biến _provider:
        //   - Biến _provider (kiểu ServiceProvider) lúc này đã sẵn sàng hoạt động. Nó nắm giữ toàn bộ quyền năng để:
        //      Khởi tạo bất kỳ class nào bạn đã đăng ký.
        //      Quản lý vòng đời (Singleton/Transient) của các đối tượng đó.
        //      Tự động dọn dẹp bộ nhớ khi gọi _provider.Dispose().

        // GetRequiredService<T>(): Lấy ra service kiểu T. Nếu chưa đăng ký $T$, nó sẽ ném ra Exception (báo lỗi ngay).
        //  - Thường dùng cho các thành phần bắt buộc phải có như MainWindow.
        var window = _provider.GetRequiredService<MainWindow>();    // STEP 4: NHẤN NÚT TRÊN GIÂY CHUYỀN ĐỂ NHÀ MÁY TỰ ĐỘNG XUẤT RA 1 CHIẾC XE "MainWindow" HOÀN CHỈNH
        // Lúc này, ServiceProvider sẽ thực hiện chuỗi thao tác tự động:
        //   1. Mở class MainWindow ra kiểm tra các Constructor.
        //   2.Phát hiện MainWindow ĐANG BẮT BUỘC CẦN một tham số kiểu 'FlowEditorViewModel'.
        //   3.Tra cứu trong danh bạ DI xem 'FlowEditorViewModel' đã được đăng ký chưa ?
        //   ──> Thấy dòng: services.AddSingleton<FlowEditorViewModel>();
        //   4.Tự động khởi tạo(hoặc lấy lại bản Singleton) của 'FlowEditorViewModel'.
        //   5.Tự động truyền(inject) instance 'FlowEditorViewModel' đó vào Constructor của MainWindow.
        //   6.Bên trong Constructor MainWindow: `this.DataContext = viewModel` chạy ──> DataContext được thiết lập thành công!
        // -> Tóm lại: ServiceProvider giống như một "Tổng đài phân phối" hoặc "Kho vật tư".
        //      Thay vì bạn phải tự gõ new MainWindow(new FlowEditorViewModel(new ToolRegistry())) một cách thủ công và rối rắm,
        //      bạn chỉ cần hỏi ServiceProvider: "Cho tôi xin một MainWindow!", và nó sẽ tự động lắp ráp hoàn chỉnh rồi đưa cho bạn!

        window.Show();
    }

    // ConfigureServices — khai báo phụ thuộc: Method static này gom mọi đăng ký DI vào một chỗ.
    // Mọi service ở đây đều dùng AddSingleton — mỗi type chỉ có một thực thể duy nhất dùng chung toàn ứng dụng.
    private static void ConfigureServices(IServiceCollection services)
    {
        // Registry: quét assembly Tools để tự đăng ký mọi [ToolMetadata].
        services.AddSingleton<ToolRegistry>(_ =>
        {
            var registry = new ToolRegistry();
            registry.RegisterAssembly(typeof(GrabImageTool).Assembly);
            // typeof(GrabImageTool): Trích xuất thông tin kiểu dữ liệu (Type) của class GrabImageTool.
            // Assembly: Trả về một đối tượng đại diện cho tệp thư viện biên dịch (Assembly / file .dll) mà class GrabImageTool đang sinh sống bên trong.
            // Vì sao có nhiều Tool mà ở đây CHỈ DÙNG GrabImageTool?
            // -> là nhờ cơ chế hoạt động của hàm RegisterAssembly: nhận đầu vào là cả một file DLL (Assembly),
            // sau đó nó dùng Reflection để tự động "lùng xới" (quét) toàn bộ các class có bên trong file DLL đó.
            // -> Miễn là GrabImageTool, ConvertColorTool, FindCircleTool, ThresholdTool... cùng nằm chung trong một dự án/file DLL (VisionFlow.Tools.dll).
            // -> Bạn chỉ cần chọn 1 Tool bất kỳ đại diện (ở đây chọn GrabImageTool) để lấy ra đối tượng Assembly chung đó.
            // -> Hàm RegisterAssembly sẽ tự động quét và đăng ký TẤT CẢ các Tool còn lại trong DLL đó mà bạn không cần phải gõ tay từng Tool một!
            // Nếu không dùng RegisterAssembly, bạn sẽ phải tự đăng ký thủ công từng Tool một như thế này:
                // registry.Register(typeof(GrabImageTool))
                // registry.Register(typeof(ConvertColorTool));
                // registry.Register(typeof(FindCircleTool));
                // registry.Register(typeof(ThresholdTool));
                // ... có 50 Tool thì phải gõ 50 dòng!
            return registry;    // Dictionary<string, ToolDescriptor>
        });

        // 2. Map IToolRegistry về cùng instance ToolRegistry ở trên (để dùng chung 1 Singleton)
        services.AddSingleton<IToolRegistry>(sp => sp.GetRequiredService<ToolRegistry>());
        // Ý nghĩa: Đăng ký interface IToolRegistry vào hệ thống DI, nhưng không tạo mới một instance khác, mà bắt nó
        // dùng chung đúng thực thể ToolRegistry đã được tạo ở dòng phía trên. Tại sao phải làm vậy?
        //   - Nếu bạn ghi services.AddSingleton<IToolRegistry, ToolRegistry>(), DI Container sẽ tạo ra 2 bản ToolRegistry khác nhau trong bộ nhớ.
        //   - Việc dùng cú pháp sp => sp.GetRequiredService<ToolRegistry>() (sử dụng tham số sp - IServiceProvider) giúp ánh xạ IToolRegistry
        //     về lại đối tượng ToolRegistry gốc đã được quét sẵn các Tool. Như vậy, dù class khác yêu cầu inject IToolRegistry hay ToolRegistry
        //     thì cả hai đều dùng chung 1 Singleton duy nhất.

        services.AddSingleton<IFlowRepository, JsonFlowRepository>();
        // Ý nghĩa: Khai báo quy tắc: "Bất kỳ class nào xin IFlowRepository, hãy đưa cho nó một bản instance của JsonFlowRepository".
        // Tác dụng: - Đây là việc áp dụng nguyên lý Dependency Inversion (D trong SOLID).
        //   - Các ViewModel (như FlowEditorViewModel) chỉ cần phụ thuộc vào Interface IFlowRepository (để lưu/mở file sơ đồ pipeline) mà không cần
        //     quan tâm bên dưới đang lưu bằng định dạng gì. Sau này nếu bạn muốn đổi sang lưu file XML hay Database (ví dụ SqlFlowRepository),
        //     bạn chỉ cần sửa duy nhất dòng đăng ký này trong App.xaml.cs mà không phải sửa một dòng code nào trong các ViewModel!

        services.AddSingleton<FlowEditorViewModel>();
        // Ý nghĩa: Khởi tạo ViewModel quản lý toàn bộ màn hình thiết kế pipeline dưới dạng Singleton.
        // Tác dụng: 
        //   - Giữ cho trạng thái sơ đồ các Node, các kết nối dây, danh sách Tool đang chạy... tồn tại xuyên suốt quá trình ứng dụng vận hành.
        //   - Khi DI Container khởi tạo FlowEditorViewModel, nó sẽ tự động soi vào Constructor của ViewModel này. Nếu ViewModel yêu cầu IToolRegistry
        //     hay IFlowRepository, DI Container sẽ tự gắp các đối tượng đã đăng ký ở trên truyền vào cho nó (Tự động Inject dependencies).

        services.AddSingleton<MainWindow>();
        // Ý nghĩa: Đăng ký Cửa sổ chính (MainWindow) của ứng dụng WPF dưới dạng Singleton.
        // Tác dụng:
        //   - Giúp tự động truyền (inject) FlowEditorViewModel vào làm DataContext cho MainWindow khi giao diện được tạo.
        //   - Giúp cho câu lệnh trong hàm OnStartup:
        //       var window = _provider.GetRequiredService<MainWindow>();
        //       window.Show();
        //     có thể tự động lắp ráp hoàn chỉnh toàn bộ cây phụ thuộc (MainWindow ➔ FlowEditorViewModel ➔ ToolRegistry / JsonFlowRepository) chỉ bằng 1 dòng gọi duy nhất!
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _provider?.Dispose();
        base.OnExit(e);
    }
}