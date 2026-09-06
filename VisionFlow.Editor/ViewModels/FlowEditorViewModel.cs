using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using NodeNetwork;
using NodeNetwork.Toolkit;
using NodeNetwork.Toolkit.NodeList;
using NodeNetwork.ViewModels;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Registry;
using VisionFlow.Core.Tools;
using VisionFlow.Editor.Adapter;
using VisionFlow.Engine.Execution;
using VisionFlow.Engine.Persistence;
using VisionFlow.Editor.ViewModels;
using DynamicData;

namespace VisionFlow.Editor.ViewModels;

/// <summary>
/// ViewModel trung tâm điều phối toàn bộ màn hình trình soạn thảo Flow (Editor).
/// Quản lý Canvas đồ thị node, danh sách Palette công cụ, các lệnh Run/Save/Load và cập nhật Preview ảnh.
/// </summary>
public sealed partial class FlowEditorViewModel : ObservableObject
{
    private readonly ToolRegistry _registry;    // Khởi tạo danh sách các công cụ xử lý ảnh có sẵn (ToolRegistry) và sắp xếp theo danh mục.
    private readonly IFlowRepository _repository;
    private readonly FlowExecutor _executor = new();

    private double _nextX = 40;
    private double _nextY = 40;

    /// <summary>
    /// Canvas chứa mạng lưới các node và dây nối.
    /// </summary>
    public NetworkViewModel Network { get; } = new();

    /// <summary>
    /// Danh sách công cụ cho phép kéo/thả ra Canvas.
    /// </summary>
    public NodeListViewModel NodeList { get; } = new();

    /// <summary>
    /// Tập hợp các ToolDescriptor được sắp xếp theo thư mục/tên hiển thị.
    /// </summary>
    public ObservableCollection<ToolDescriptor> Palette { get; }

    /// <summary>
    /// Danh sách ViewModel hiển thị/chỉnh sửa tham số của Node đang được chọn.
    /// </summary>
    public ObservableCollection<ParameterEditorViewModel> SelectedParameters { get; } = new();

    /// <summary>
    /// Danh sách ghi nhận thời gian thực thi của từng Node sau khi chạy pipeline.
    /// </summary>
    public ObservableCollection<NodeExecutionResult> Timings { get; } = new();

    [ObservableProperty]
    private ToolNodeViewModel? _selectedNode;

    [ObservableProperty]
    private BitmapSource? _previewImage;

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private string _flowName = "Untitled";

    /// <summary>
    /// Khởi tạo FlowEditorViewModel với các phụ thuộc dependency injection.
    /// </summary>
    /// <param name="registry">Kho lưu trữ và khởi tạo các ToolDescriptor</param>
    /// <param name="repository">Dịch vụ lưu/đọc cấu hình Flow</param>
    public FlowEditorViewModel(ToolRegistry registry, IFlowRepository repository)
    {
        _registry = registry;       // registry (ToolRegistry): Kho lưu trữ danh sách và khởi tạo các loại Tool xử lý ảnh.
        _repository = repository;   // repository (IFlowRepository): Dịch vụ đọc/ghi file cấu hình luồng xử lý (JSON).

        // Dựng danh sách Palette sắp xếp theo Nhóm (Category) rồi đến Tên (DisplayName)
        Palette = new ObservableCollection<ToolDescriptor>(
            registry.Descriptors
                .OrderBy(d => d.Category)
                .ThenBy(d => d.DisplayName)
        );

        // Đăng ký các loại Tool vào hộp công cụ Palette
        foreach (var descriptor in Palette)
        {
            var key = descriptor.Key; // Sử dụng biến cục bộ tránh lỗi closure trong lambda
            NodeList.AddNodeType(() => new ToolNodeViewModel(_registry.Create(key)));
            // AddNodeType: Chưa khởi tạo ngay Node đó lên màn hình, mà nó chỉ nhận vào một hàm tạo (Factory Delegate) để khi nào
            // người dùng kéo một mục ra Canvas, nó mới kích hoạt hàm đó để sinh ra một Node mới.
            // () => ...giúp NodeListView gọi hàm này mỗi lần người dùng thực hiện hành động kéo-thả để nhân bản ra một Node mới.
            // _registry.Create(key): _registry sẽ tra cứu từ khóa key (ví dụ: "Grab", "Threshold") trong Dictionary của nó
            // và thực thi hàm tạo delegate để trả về một thực thể Tool xử lý ảnh thực sự (kế thừa từ VisionTool).
        }

        // Bắt lỗi vòng lặp (loop/cycle) trong đồ thị NodeNetwork
        Network.Validator = network =>
        {
            var hasLoops = GraphAlgorithms.FindLoops(network).Any();
            if (hasLoops)
            {
                // Truyền đủ 3 tham số: isValid (false), isTraversable (false), messageViewModel
                return new NetworkValidationResult(
                    false,
                    false,
                    new ErrorMessageViewModel("Mạng có vòng lặp")
                );
            }

            // Khi hợp lệ: isValid (true), isTraversable (true), messageViewModel (null)
            return new NetworkValidationResult(true, true, null);
        };

        /// <summary>
        /// Lắng nghe và tự động cập nhật Node đang được chọn (SelectedNode).
        /// Khi danh sách các Node được chọn trên Canvas thay đổi, tự động lấy Node đầu tiên 
        /// kiểu ToolNodeViewModel để gán cho SelectedNode, từ đó kích hoạt hiển thị bảng tham số bên phải.
        /// </summary>
        Network.SelectedNodes.Connect() // Mở luồng lắng nghe (Observable Stream) mọi sự thay đổi trong danh sách Node đang được chọn
            .Subscribe(_ => // Đăng ký thực thi đoạn mã mỗi khi có sự thay đổi (chọn mới, bỏ chọn, hoặc chọn nhiều)
            {
                // Lấy ra danh sách các Node đang chọn, lọc chỉ lấy kiểu ToolNodeViewModel và lấy phần tử đầu tiên (nếu không có thì trả về null)
                // Khi dòng này chạy ➔ Nó gọi Setter của `SelectedNode`:
                //  - Setter SelectedNode kiểm tra xem giá trị có khác giá trị cũ hay không.
                //  - Nếu khác, nó tự động kích hoạt hàm partial: OnSelectedNodeChanged(value).
                SelectedNode = Network.SelectedNodes.Items
                    .OfType<ToolNodeViewModel>()
                    .FirstOrDefault();
            });

        /// <summary>
        /// Lắng nghe các thay đổi trong danh sách Node của Canvas (Thêm, Xóa node).
        /// Tự động đánh dấu chọn (IsSelected = true) cho Node vừa được thêm mới (Kéo-Thả hoặc nạp từ file),
        /// giúp bảng tham số bên phải lập tức hiển thị cấu hình của Node đó mà người dùng không cần click chọn thủ công.
        /// </summary>
        Network.Nodes.Connect() // Mở luồng lắng nghe mọi biến động trong tập hợp tất cả các Node trên Canvas
            .Subscribe(changes => // Đăng ký xử lý khi nhận được danh sách các sự thay đổi (Changeset)
            {
                // Duyệt qua từng sự thay đổi vừa xảy ra trong tập hợp Node
                foreach (var change in changes)
                {
                    // Kiểm tra xem hành động vừa xảy ra có phải là THÊM MỚI (Add) Node hay không
                    if (change.Reason == ListChangeReason.Add)
                    {
                        // Lấy đối tượng Node vừa được tạo/thêm vào Canvas thành công
                        var node = change.Item.Current;

                        // Duyệt qua toàn bộ các Node hiện có trên Canvas
                        foreach (var other in Network.Nodes.Items)
                        {
                            // Nếu 'other' chính là Node mới vừa thêm (so sánh tham chiếu địa chỉ ô nhớ ReferenceEquals), đặt IsSelected = true.
                            // Ngược lại, tất cả các Node cũ khác trên Canvas sẽ bị bỏ chọn (IsSelected = false).
                            other.IsSelected = ReferenceEquals(other, node);
                        }
                    }
                }
            });
    }

    /// <summary>
    /// Partial method được gọi tự động sau khi giá trị SelectedNode thay đổi. Cập nhật bảng tham số và hình ảnh Preview tương ứng.
    /// private ToolNodeViewModel? _selectedNode; khai báo ở trên, [ObservableProperty] sinh ra OnSelectedNodeChanged(value);
    /// // CODE DO MVVM TOOLKIT TỰ ĐỘNG SINH RA BÊN DƯỚI (Bạn không thấy trong file này):
    /// public ToolNodeViewModel? SelectedNode
    /// {
    ///     get => _selectedNode;
    ///     set
    ///     {
    ///         if (SetProperty(ref _selectedNode, value))
    ///         {
    ///             // 🔥 ĐÂY CHÍNH LÀ NƠI NÓ TỰ ĐỘNG GỌI HÀM CỦA BẠN!
    ///             OnSelectedNodeChanged(value);
    ///         }
    ///     }
    /// }
    /// </summary>
    partial void OnSelectedNodeChanged(ToolNodeViewModel? value)
    {
        SelectedParameters.Clear();
        if (value is not null)
        {
            foreach (var p in value.Tool.Parameters)
            {
                SelectedParameters.Add(new ParameterEditorViewModel(p));
            }
        }
        UpdatePreview();
    }

    /// <summary>
    /// Lệnh thêm một Tool vào Canvas dựa theo ToolDescriptor được truyền vào.
    /// </summary>
    [RelayCommand]
    private void AddTool(ToolDescriptor? descriptor)
    {
        if (descriptor is null) return;

        var node = new ToolNodeViewModel(_registry.Create(descriptor.Key));
        node.Position = new Point(_nextX, _nextY);
        Network.Nodes.Add(node);

        // Tự động sắp xếp vị trí cho Node tiếp theo trên Canvas
        _nextX += 60;
        _nextY += 50;
        if (_nextY > 400)
        {
            _nextY = 40;
            _nextX += 200;
        }
    }

    /// <summary>
    /// Dựng ViewModel cho cửa sổ cấu hình chi tiết khi double-click vào một Node.
    /// </summary>
    
    // Đầu vào (ToolNodeViewModel nodeVM): Là Node giao diện (UI Node) mà người dùng vừa double-click vào trên Canvas.
    // Đầu ra (ParameterEditorWindowViewModel): Trả về một ViewModel hoàn chỉnh để làm DataContext cho cửa sổ Popup điều chỉnh tham số (ParameterEditorWindow).
    public ParameterEditorWindowViewModel CreateParameterEditor(ToolNodeViewModel nodeVM)
    {
        var graph = GraphSyncService.ToFlowGraph(Network, FlowName);    // Bước 1: Chuyển đổi từ UI Graph sang Core Data Graph
        // Tại sao phải làm việc này?
        //   - Network (chứa trên Canvas) là đồ thị giao diện (chỉ để vẽ các node, các dây nối UI, tọa độ X/Y...).
        //   - ParameterEditorWindow lại cần đồ thị dữ liệu thuần C# (FlowGraph) – nơi thực sự chứa logic chạy
        //     thuật toán, danh sách tham số, danh mục (Category), và mối quan hệ phụ thuộc giữa các bước xử lý ảnh.
        // GraphSyncService.ToFlowGraph(...): Đóng vai trò làm "phiên dịch viên", quét toàn bộ màn hình Network hiện tại
        // và trích xuất/chuyển đổi nó thành một đối tượng FlowGraph chuẩn.

        var node = graph.GetNode(nodeVM.Tool.Id) ?? throw new InvalidOperationException("Node không có trong graph");   // Bước 2: Truy vết và tìm kiếm Node tương ứng
        // nodeVM.Tool.Id: Lấy định danh duy nhất (GUID/ID) của Tool đang nằm trên giao diện.
        // graph.GetNode(...): Tìm kiếm Node tương ứng với ID đó bên trong đồ thị graph vừa chuyển đổi ở bước 1. 
        
        return new ParameterEditorWindowViewModel(graph, node);
        // graph: Cung cấp toàn bộ ngữ cảnh của luồng xử lý (giúp window này biết được node hiện tại nối với node nào trước/sau nó để preview ảnh hoặc chạy thử).
        // node: Node cụ thể mà người dùng muốn chỉnh sửa tham số (giúp window này hiển thị đúng danh mục Categories và danh sách tham số tương ứng).
    }

    /// <summary>
    /// Lệnh thực thi toàn bộ luồng xử lý (Flow Pipeline) bất đồng bộ.
    /// </summary>
    [RelayCommand]
    private async Task Run()
    {
        var graph = GraphSyncService.ToFlowGraph(Network, FlowName);
        Timings.Clear();
        StatusText = "Running...";

        try
        {
            var result = await Task.Run(() => _executor.Run(graph));

            foreach (var r in result.Nodes)
            {
                Timings.Add(r);
            }

            StatusText = result.Success
                ? $"Done ({result.TotalMs} ms)"
                : $"Completed with errors ({result.Nodes.Count} nodes failed)";
        }
        catch (FlowExecutionException)
        {
            StatusText = "Cannot run: Graph has errors";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }

        UpdatePreview();
    }

    /// <summary>
    /// Lệnh lưu cấu hình Flow hiện tại ra file JSON.
    /// </summary>
    [RelayCommand]
    private void Save()
    {
        var dialog = new SaveFileDialog { Filter = "VisionFlow (*.json)|*.json" };
        if (dialog.ShowDialog() != true) return;

        var graph = GraphSyncService.ToFlowGraph(Network, FlowName);
        _repository.Save(graph, dialog.FileName);
        StatusText = $"Saved to {dialog.FileName}";
    }

    /// <summary>
    /// Lệnh mở hộp thoại chọn file Flow để nạp dữ liệu.
    /// </summary>
    [RelayCommand]
    private void Load()
    {
        var dialog = new OpenFileDialog { Filter = "VisionFlow (*.json)|*.json" };
        if (dialog.ShowDialog() != true) return;

        LoadFile(dialog.FileName);
    }

    /// <summary>
    /// Đọc và tải cấu hình Flow từ đường dẫn file cụ thể lên Canvas.
    /// </summary>
    public void LoadFile(string path)
    {
        try
        {
            var graph = _repository.Load(path);
            FlowName = graph.Name;
            GraphSyncService.LoadInto(Network, graph);
            Timings.Clear();
            PreviewImage = null;
            StatusText = $"Loaded {path} ({graph.Nodes.Count} nodes)";
        }
        catch (Exception ex)
        {
            StatusText = $"Không nạp được: {ex.Message}";
        }
    }

    /// <summary>
    /// Lệnh tạo mới / xoá toàn bộ Node và kết nối trên Canvas về trạng thái mặc định.
    /// </summary>
    [RelayCommand]
    private void ClearAll()
    {
        Network.Connections.Clear();
        Network.Nodes.Clear();
        SelectedNode = null;
        Timings.Clear();
        PreviewImage = null;
        FlowName = "Untitled";
        StatusText = "Flow cleared";
    }

    /// <summary>
    /// Cập nhật hình ảnh hiển thị trên khung Preview.
    /// </summary>
    private void UpdatePreview()
    {
        IVisionImage? image = null;

        if (SelectedNode != null)
        {
            image = GetNodeImage(SelectedNode);
        }

        image ??= Network.Nodes.Items
            .OfType<ToolNodeViewModel>()
            .Where(n => n.Tool.TypeKey == "Output")
            .Select(GetNodeImage)
            .FirstOrDefault(img => img != null);

        image ??= Network.Nodes.Items
            .OfType<ToolNodeViewModel>()
            .Select(GetNodeImage)
            .LastOrDefault(img => img != null);

        PreviewImage = ImagePreview.ToBitmapSource(image);
    }

    /// <summary>
    /// Hàm bổ trợ lấy ra ảnh đầu tiên tìm thấy từ các tham số Output/Input của một Node.
    /// </summary>
    private static IVisionImage? GetNodeImage(ToolNodeViewModel node)
    {
        foreach (var p in node.Tool.Outputs)
        {
            if (p.Value is IVisionImage outImg) return outImg;
        }
        foreach (var p in node.Tool.Inputs)
        {
            if (p.Value is IVisionImage inImg) return inImg;
        }
        return null;
    }
}