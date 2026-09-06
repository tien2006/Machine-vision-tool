// ==================== Vai trò chính:                Hợp đồng chuẩn của một Tool + lớp nền (base class) dùng chung cho mọi thuật toán
// ==================== Thành phần / Class tiêu biểu: ITool, IToolContext, VisionTool (abstract), ToolParameter<T>, ToolMetadataAttribute, ToolState, ToolExecutionException
// ==================== Phụ thuộc vào:                Ports
// ==================== Pattern / Kỹ thuật nổi bật:   Strategy Pattern + Template Method Pattern

using System.Reflection;
using VisionFlow.Core.Ports;
using System.Runtime.CompilerServices; // Thêm namespace này

// Đặt dòng này ở trên cùng của file, TRƯỚC namespace VisionFlow.Core.Tools
// Nó có tác dụng mở quyền truy cập các thành phần internal của Assembly hiện tại (ở đây là VisionFlow.Core)
// cho một Assembly khác được chỉ định (ở đây là VisionFlow.Engine).
[assembly: InternalsVisibleTo("VisionFlow.Engine")]

namespace VisionFlow.Core.Tools;

/// <summary>
/// Lớp nền (Base class) cho mọi tool xử lý (Sử dụng mẫu thiết kế Strategy + Template Method). 
/// Tool con kế thừa từ lớp này cần:
/// <list type="number">
/// <item>Khai báo Input/Output/Parameter trong hàm khởi tạo (Constructor) qua các hàm <c>AddInput/AddOutput/AddParameter</c>;</item>
/// <item>Cài đặt thuật toán xử lý chính trong hàm <see cref="OnExecute"/>.</item>
/// </list>
/// Hàm <see cref="Execute"/> sẽ bọc bước kiểm tra (validate) trước khi gọi <see cref="OnExecute"/>; 
/// Hệ thống (Engine) bên ngoài sẽ lo việc đo thời gian chạy (timing) và bắt lỗi (try/catch).
/// </summary>
public abstract class VisionTool : ITool
{
    // Các danh sách lưu trữ cổng đầu vào, đầu ra và tham số cấu hình của Tool
    private readonly List<IInputPort> _inputs = new();
    private readonly List<IOutputPort> _outputs = new();
    private readonly List<IToolParameter> _parameters = new();

    protected VisionTool()  // tự động thiết lập danh tính và thông tin mô tả (Metadata) cho mỗi công cụ xử lý ảnh khi nó được tạo ra.
    {
        // - Guid.NewGuid(): Sinh ra một chuỗi mã GUID 128-bit hoàn toàn ngẫu nhiên và đảm bảo duy nhất
        // trên toàn cầu (ví dụ: e0213d22-3142-4235-8ce6-c5e20422502c).
        // - .ToString("N"): Định dạng lại chuỗi GUID bằng cách loại bỏ tất cả các dấu gạch ngang (-).
        // Kết quả sẽ là một chuỗi liền mạch 32 ký tự Hex (ví dụ: e0213d22314242358ce6c5e20422502c).
        // - Mục đích: Giúp phân biệt từng Node/Tool riêng biệt trên giao diện đồ thị (Canvas/Pipeline),
        // ngay cả khi bạn kéo thả 5 Tool cùng loại FindLineTool vào màn hình.
        Id = Guid.NewGuid().ToString("N");

        // - Dùng Reflection để tự động đọc các thông tin mô tả (Metadata) được gắn trên class con
        // - GetCustomAttribute<ToolMetadataAttribute>(): Sử dụng kỹ thuật Reflection để quét xem trên đầu
        // class con đó có khai báo thẻ trang trí [ToolMetadata(...)] hay không.
        // - Ex: [ToolMetadata(Key = "FindLine", DisplayName = "Tìm Đường Thẳng", Category = "Measurement")]
        //       public class FindLineTool : VisionTool { ... }
        // Nếu tìm thấy attribute [ToolMetadata], biến meta sẽ chứa dữ liệu cấu hình đó.
        // Nếu lớp con quên không khai báo attribute này, meta sẽ nhận giá trị null.
        var meta = GetType().GetCustomAttribute<ToolMetadataAttribute>();

        // Ba dòng code tiếp theo giúp gán giá trị an toàn,
        // tránh bị lỗi crash chương trình kể cả khi lớp con không khai báo [ToolMetadata]:
        TypeKey = meta?.Key ?? GetType().Name;
        // Cơ chế: Sử dụng toán tử null-conditional (?.) và null-coalescing (??).
        // Nếu meta có tồn tại và thuộc tính Key có giá trị -> Lấy giá trị meta.Key (dùng làm khóa định danh khi lưu/mở file JSON/XML).
        // Nếu meta bị null hoặc không khai báo Key -> Tự động lấy tên của lớp con làm TypeKey (ví dụ: "FindLineTool").
        // GetType().Name: 
        // namespace VisionFlow.Tools
        // {
        //      public class FindLineTool : VisionTool   
        //      {
        //          // ...
        //      }
        //  }
        // -> Khi bạn khởi tạo đối tượng lớp con: VisionTool tool = new FindLineTool();
        // -> Trong hàm khởi tạo của VisionTool, khi dòng code sau chạy: string name = GetType().Name;
        // GetType() sẽ trả về kiểu dữ liệu: VisionFlow.Tools.FindLineTool
        // GetType().Name sẽ lấy ra đúng chuỗi: "FindLineTool"

        DisplayName = !string.IsNullOrEmpty(meta?.DisplayName) ? meta!.DisplayName : GetType().Name;
        // Nếu DisplayName trong attribute được truyền vào và không phải chuỗi rỗng -> Dùng tên thân thiện đó để hiển thị lên UI (ví dụ: "Tìm Đường Thẳng").
        // Ngược lại (bị null hoặc để chuỗi trống "") -> Dùng tạm tên lớp con (GetType().Name) để hiển thị.

        Category = meta?.Category ?? "General";
        // Lấy phân nhóm từ meta.Category (ví dụ: "Locate", "Inspection", "Color").
        // Nếu meta ko Null và Category có giá trị. Ex [ToolMetadata(Category = "Measurement")] -> Category = "Measurement"
        // Ngược lại, nếu meta Null hoặc Meta ko Null nhưng Category ko có giá trị -> Category = "General".
    }

    // --- CÁC THUỘC TÍNH THÔNG TIN CƠ BẢN ---
    public string Id { get; set; }
    public string TypeKey { get; }
    public string DisplayName { get; }
    public string Category { get; }

    // --- CÁC THUỘC TÍNH CUNG CẤP DỮ LIỆU RA BÊN NGOÀI (Chỉ đọc) ---
    public IReadOnlyList<IInputPort> Inputs => _inputs;
    public IReadOnlyList<IOutputPort> Outputs => _outputs;
    public IReadOnlyList<IToolParameter> Parameters => _parameters;

    // --- TRẠNG THÁI HOẠT ĐỘNG ---
    public ToolState State { get; internal set; } = ToolState.Idle; // Mặc định là đang rảnh (Idle)
    public long ElapsedMs { get; internal set; } // Thời gian chạy thuật toán (ms)
    public string? ErrorMessage { get; internal set; } // Chứa nội dung lỗi nếu có

    // ========================================================================
    // CÁC HÀM HỖ TRỢ (HELPERS) - Dùng trong hàm khởi tạo của Tool con
    // ========================================================================

    /// <summary>Thêm một cổng nhận dữ liệu đầu vào.</summary>
    protected InputPort<T> AddInput<T>(string name, string? displayName = null, bool optional = false)
    {
        var port = new InputPort<T>(name, displayName, optional);
        _inputs.Add(port);
        return port;
    }

    /// <summary>Thêm một cổng nhận dữ liệu đầu vào dạng MULTI-INPUT (chấp nhận NHIỀU dây nối cùng lúc - fan-in), 
    /// khác AddInput chỉ nhận 1 dây.</summary>
    protected MultiInputPort<T> AddMultiInput<T>(string name, string? displayName = null, bool optional = true)
    {
        var port = new MultiInputPort<T>(name, displayName, optional);
        _inputs.Add(port);
        return port;
    }

    /// <summary>Thêm một cổng xuất dữ liệu đầu ra.</summary>
    protected OutputPort<T> AddOutput<T>(string name, string? displayName = null)
    {
        var port = new OutputPort<T>(name, displayName);
        _outputs.Add(port);
        return port;
    }

    /// <summary>Thêm một tham số cấu hình (người dùng có thể chỉnh sửa trên UI).</summary>
    protected ToolParameter<T> AddParameter<T>(
        string name, T value, string? displayName = null, T? min = default, T? max = default,
        string category = "General", int order = 0, ParameterInteraction interaction = ParameterInteraction.None)
    {
        // Khởi tạo tham số với danh sách lựa chọn (choices) mặc định là null
        var p = new ToolParameter<T>(name, value, displayName, min, max, category, order, choices: null, interaction);
        _parameters.Add(p);
        return p;
    }

    /// <summary>Thêm tham số dạng danh sách thả xuống (ComboBox) để người dùng chọn.</summary>
    protected ToolParameter<string> AddChoiceParameter(
        string name, string value, IReadOnlyList<string> choices, string? displayName = null,
        string category = "General", int order = 0)
    {
        var p = new ToolParameter<string>(name, value, displayName, minimum: default, maximum: default,
            category, order, choices);
        _parameters.Add(p);
        return p;
    }

    // ========================================================================
    // CÁC HÀM TRUY XUẤT NHANH (TÌM KIẾM THEO TÊN)
    // ========================================================================

    public IInputPort? FindInput(string name) => _inputs.FirstOrDefault(p => p.Name == name);
    public IOutputPort? FindOutput(string name) => _outputs.FirstOrDefault(p => p.Name == name);
    public IToolParameter? FindParameter(string name) => _parameters.FirstOrDefault(p => p.Name == name);

    // ========================================================================
    // LUỒNG THỰC THI CHÍNH (TEMPLATE METHOD)
    // ========================================================================

    /// <summary>
    /// Hàm này được gọi bởi hệ thống (Engine). Nó quy định trình tự chạy chuẩn của một Tool.
    /// </summary>
    public void Execute(IToolContext context)
    {
        // 1. Kiểm tra xem tiến trình có bị người dùng nhấn "Stop/Cancel" giữa chừng không
        context.CancellationToken.ThrowIfCancellationRequested();

        // 2. Kiểm tra tính hợp lệ của dữ liệu đầu vào
        ValidateInputs();

        // 3. Thực thi thuật toán thực sự (được định nghĩa ở Tool con)
        OnExecute(context);
    }

    /// <summary>
    /// Nơi các Tool con (như FindLineTool, FindCircleTool) viết logic thuật toán xử lý ảnh.
    /// </summary>
    protected abstract void OnExecute(IToolContext context);

    /// <summary>
    /// Kiểm tra các input bắt buộc (không phải optional) xem đã được truyền dữ liệu vào chưa.
    /// Có thể ghi đè (override) ở lớp con nếu cần kiểm tra phức tạp hơn.
    /// </summary>
    protected virtual void ValidateInputs()
    {
        foreach (var input in _inputs)
        {
            // SỬA LỖI: Đã bỏ ép kiểu (IInputPort) thừa vì biến 'input' bản chất đã là IInputPort rồi.
            if (!input.IsOptional && input.Value is null)
            {
                // Dừng chạy và ném ra lỗi nếu một cổng bắt buộc bị thiếu dữ liệu
                throw new ToolExecutionException($"Dữ liệu đầu vào bắt buộc '{input.Name}' của tool '{DisplayName}' chưa được kết nối hoặc bị rỗng.");
            }
        }
    }
}