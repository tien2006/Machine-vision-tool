// ==================== Vai trò chính:                Node nguồn — nạp ảnh đầu vào cho toàn pipeline
// ==================== Thành phần / Class tiêu biểu: GrabImageTool (= "ImageLoader" theo tài liệu thuật toán)
// ==================== Phụ thuộc vào:                OpenCvSharp
// ==================== Pattern / Kỹ thuật nổi bật:   Input Source Node + Round-robin folder cycling (giống SimulationCamera ở Buổi 109)

using System;
using System.IO;
using System.Linq; // BỔ SUNG: cần cho .Where()/.OrderBy()/.ToArray() khi quét thư mục
using OpenCvSharp; // Sử dụng để gọi các hàm đọc ảnh native từ ổ đĩa
using VisionFlow.Core.Imaging; // Kéo vào kiểu dữ liệu ảnh trừu tượng Core
using VisionFlow.Core.Ports; // Quản lý cổng kết nối dữ liệu vào ra
using VisionFlow.Core.Tools; // Lớp nền tảng VisionTool và bộ quản lý ngoại lệ
using VisionFlow.Tools.Imaging; // Sử dụng lớp wrapper chuyển đổi ảnh MatVisionImage

namespace VisionFlow.Tools.Acquisition; // Thuộc phân hệ thu nhận dữ liệu đầu vào

/// <summary>
/// Node nguồn (Input Source) đầu tiên của pipeline — tương ứng "ImageLoader" trong tài liệu thuật toán.
/// Hỗ trợ 2 chế độ hoạt động, tự động chọn theo cấu hình:
/// <list type="number">
/// <item><b>Chế độ 1 file cố định</b> (Tab Input): dùng khi bạn muốn test thuật toán trên đúng một tấm
/// ảnh mẫu "vàng" — chỉ cần khai báo <see cref="_imagePath"/>, để trống <see cref="_folderPath"/>.</item>
/// <item><b>Chế độ quét thư mục</b> (Tab Load): khai báo <see cref="_folderPath"/> khác rỗng — Tool sẽ tự
/// động quét toàn bộ ảnh hợp lệ trong thư mục và mỗi lần Execute() chạy sẽ trả về MỘT ảnh, mô phỏng
/// camera đang chụp từng sản phẩm trên băng chuyền mà không cần phần cứng thật (giống SimulationCamera).</item>
/// </list>
/// Tab Processing (<see cref="_processSequentially"/>) quyết định cách chọn ảnh tiếp theo trong thư mục:
/// tuần tự (đúng thứ tự, lặp lại vòng tròn) hoặc ngẫu nhiên (random mỗi lần chạy).
/// </summary>
[ToolMetadata("Grab", DisplayName = "Image Loader", Category = "InputSource",
    Description = "Load a single image from a fixed path, or cycle through a folder (sequential/random) to simulate a production line without a camera.")]
public sealed class GrabImageTool : VisionTool
{
    // ============================================================
    // THAM SỐ CẤU HÌNH (Parameters) — chia theo đúng 3 Tab trong tài liệu
    // ============================================================

    // --- Tab "Input": chế độ 1 file cố định (hành vi gốc, giữ nguyên để tương thích ngược) ---
    private readonly ToolParameter<string> _imagePath;      // Đường dẫn file ảnh cụ thể (dùng khi FolderPath rỗng)
    private readonly ToolParameter<ImreadModes> _readMode;  // Chế độ đọc ảnh OpenCV: Color / Grayscale / Unchanged...

    // --- Tab "Load": chế độ quét thư mục (TÍNH NĂNG MỚI) ---
    private readonly ToolParameter<string> _folderPath;     // Đường dẫn thư mục chứa nhiều ảnh test

    // --- Tab "Processing" (TÍNH NĂNG MỚI) ---
    private readonly ToolParameter<bool> _processSequentially; // true = tuần tự đúng thứ tự, false = ngẫu nhiên

    // ============================================================
    // CỔNG XUẤT DỮ LIỆU (Outputs)
    // ============================================================
    private readonly OutputPort<IVisionImage> _output; // Cổng ra duy nhất: ImageMatrix (giữ tên "Image" để đồng bộ toàn hệ thống)

    // ============================================================
    // TRẠNG THÁI NỘI BỘ (không phải Parameter vì người dùng không cấu hình trực tiếp,
    // Tool tự quản lý qua các lần Execute() liên tiếp — giống hệt _currentIndex của SimulationCamera)
    // ============================================================
    private string[] _folderFiles = Array.Empty<string>(); // Danh sách file ảnh đã quét được trong thư mục, sắp xếp ổn định
    private string _cachedFolderPath = string.Empty;        // Ghi nhớ FolderPath của lần quét gần nhất để biết khi nào cần quét lại
    private int _cursor;                                     // Con trỏ chỉ vị trí ảnh kế tiếp sẽ trả về (chế độ tuần tự)
    private static readonly Random Rng = new(); // Bộ sinh số ngẫu nhiên dùng chung cho chế độ random (static: không cần Seed cố định vì đây là runtime thật, không phải Unit Test)

    // Danh sách đuôi file ảnh hợp lệ mà Tool sẽ nhận diện khi quét thư mục
    private static readonly string[] SupportedExtensions =
        { ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff" };

    public GrabImageTool()
    {
        // --- Đăng ký tham số Tab Input ---
        _imagePath = AddParameter("ImagePath", string.Empty, "File Path", category: "Input", order: 1);
        _readMode = AddParameter("ReadMode", ImreadModes.Color, "Read Mode", category: "Input", order: 2);

        // --- Đăng ký tham số Tab Load ---
        _folderPath = AddParameter("FolderPath", string.Empty, "Folder Path", category: "Load", order: 1);

        // --- Đăng ký tham số Tab Processing ---
        _processSequentially = AddParameter("ProcessSequentially", true, "Process Sequentially", category: "Processing", order: 1);

        // --- Đăng ký cổng ra ---
        _output = AddOutput<IVisionImage>("Image"); // Khởi tạo cổng xuất ảnh đầu ra
    }

    protected override void OnExecute(IToolContext context)
    {
        string folder = _folderPath.Value;

        Mat mat;
        string sourceDescription; // Chỉ dùng để ghi log / thông báo lỗi cho dễ debug, không xuất ra Output Port

        if (!string.IsNullOrWhiteSpace(folder))
        {
            // ===== CHẾ ĐỘ QUÉT THƯ MỤC: giả lập dây chuyền sản xuất chạy qua nhiều ảnh =====
            mat = LoadFromFolder(folder, out sourceDescription);
        }
        else
        {
            // ===== CHẾ ĐỘ 1 FILE CỐ ĐỊNH: hành vi gốc, dùng khi test 1 ảnh mẫu "vàng" =====
            string path = _imagePath.Value;

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new ToolExecutionException($"Image file does not exist at path: '{path}'");

            mat = Cv2.ImRead(path, _readMode.Value); // Thực thi hàm native đọc ảnh của OpenCV
            sourceDescription = path;
        }

        if (mat.Empty()) // OpenCV không ném ngoại lệ khi đọc lỗi mà trả về ma trận rỗng
        {
            mat.Dispose(); // Giải phóng vùng nhớ unmanaged ngay lập tức kể cả khi rỗng để tránh rò rỉ bộ nhớ
            throw new ToolExecutionException($"Failed to decode image from: '{sourceDescription}'");
        }

        _output.Value = new MatVisionImage(mat); // Bọc Mat thành đối tượng hệ thống, chuyển giao quyền sở hữu bộ nhớ
        context.Log($"ImageLoader: loaded '{sourceDescription}'"); // Ghi nhật ký tiến trình, tiện debug khi chạy vòng lặp dài
    }

    /// <summary>
    /// Quét (hoặc tái sử dụng bộ nhớ đệm đã quét) thư mục ảnh, chọn ra 1 ảnh theo cấu hình
    /// <see cref="_processSequentially"/>, rồi đọc file đó thành Mat.
    /// </summary>
    /// <param name="folder">Đường dẫn thư mục cần đọc.</param>
    /// <param name="loadedPath">Trả ra đường dẫn file thực tế vừa được chọn (phục vụ ghi log).</param>
    private Mat LoadFromFolder(string folder, out string loadedPath)
    {
        if (!Directory.Exists(folder))
            throw new ToolExecutionException($"Folder does not exist: '{folder}'");

        // TỐI ƯU HIỆU NĂNG: chỉ quét lại đĩa cứng (Directory.GetFiles) khi đường dẫn thư mục THAY ĐỔI
        // so với lần Execute() trước. Nếu không có cơ chế cache này, mỗi lần chạy flow (có thể vài chục
        // lần/giây khi kéo slider trong Parameter Editor) đều phải liệt kê lại toàn bộ thư mục — rất tốn
        // I/O một cách không cần thiết, nhất là với thư mục mạng (network share).
        if (!string.Equals(_cachedFolderPath, folder, StringComparison.OrdinalIgnoreCase) || _folderFiles.Length == 0)
        {
            _folderFiles = Directory.GetFiles(folder)
                .Where(f => SupportedExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase) // Sắp xếp theo tên -> thứ tự CHẠY LẶP LẠI ỔN ĐỊNH giữa các lần mở lại project
                .ToArray();

            _cachedFolderPath = folder;
            _cursor = 0; // Thư mục đổi -> reset lại con trỏ tuần tự về ảnh đầu tiên
        }

        if (_folderFiles.Length == 0)
            throw new ToolExecutionException($"No supported image files found in folder: '{folder}'");

        int index;

        if (_processSequentially.Value)
        {
            // TUẦN TỰ: lấy đúng ảnh tại vị trí con trỏ hiện tại, sau đó tăng con trỏ lên 1.
            // Phép chia lấy dư (%) giúp con trỏ tự động QUAY VÒNG về 0 khi đã chạy hết danh sách,
            // mô phỏng đúng hành vi băng chuyền chạy vô tận qua các sản phẩm.
            index = _cursor;
            _cursor = (_cursor + 1) % _folderFiles.Length;
        }
        else
        {
            // NGẪU NHIÊN: mỗi lần Execute() chọn đại 1 ảnh bất kỳ trong thư mục.
            // Hữu ích khi muốn test nhanh với dữ liệu không theo thứ tự,
            // tương tự nút "Random Defect" / "Random Product" trong các demo ở Buổi 105-106.
            index = Rng.Next(_folderFiles.Length);
        }

        loadedPath = _folderFiles[index];
        return Cv2.ImRead(loadedPath, _readMode.Value);
    }
}









/*
// ==================== Vai trò chính:                Node nguồn — nạp ảnh đầu vào cho toàn pipeline
// ==================== Thành phần / Class tiêu biểu: GrabImageTool
// ==================== Phụ thuộc vào:                OpenCvSharp
// ==================== Pattern / Kỹ thuật nổi bật:   Input Source Node

using System;
using System.IO;
using OpenCvSharp; // Sử dụng để gọi các hàm đọc ảnh native từ ổ đĩa[cite: 4]
using VisionFlow.Core.Imaging; // Kéo vào kiểu dữ liệu ảnh trừu tượng Core[cite: 4]
using VisionFlow.Core.Ports; // Quản lý cổng kết nối dữ liệu vào ra[cite: 4]
using VisionFlow.Core.Tools; // Lớp nền tảng VisionTool và bộ quản lý ngoại lệ[cite: 4]
using VisionFlow.Tools.Imaging; // Sử dụng lớp wrapper chuyển đổi ảnh MatVisionImage[cite: 4]

namespace VisionFlow.Tools.Acquisition; // Thuộc phân hệ thu nhận dữ liệu đầu vào[cite: 4]

/// <summary>
/// Công cụ nguồn nhập liệu (Input Source) đầu tiên của pipeline xử lý ảnh[cite: 4].
/// Thực hiện nạp tệp tin hình ảnh từ đường dẫn vật lý trên ổ đĩa thành đối tượng VisionImage nội bộ hệ thống[cite: 4].
/// </summary>
[ToolMetadata("Grab", DisplayName = "Grab Image", Category = "InputSource", Description = "Load an image from a file path")] // Đăng ký metadata thông tin node cho UI Palette[cite: 4]
public sealed class GrabImageTool : VisionTool
{
    private readonly ToolParameter<string> _imagePath; // Tham số lưu trữ chuỗi đường dẫn tệp ảnh[cite: 4]
    private readonly ToolParameter<ImreadModes> _readMode; // Tham số cấu hình chế độ đọc ảnh của OpenCV (màu, xám...)[cite: 4]
    private readonly OutputPort<IVisionImage> _output; // Cổng ra đẩy dữ liệu ảnh ra cho các tool downstream tiêu thụ[cite: 4]

    public GrabImageTool()
    {
        _imagePath = AddParameter("ImagePath", string.Empty, "Image Path",category: "Source",order: 1); // Đăng ký tham số chuỗi đường dẫn mặc định trống[cite: 4]
        _readMode = AddParameter("ReadMode", ImreadModes.Color, "Read Mode",category: "Source",order: 2); // Cấu hình mặc định đọc ảnh màu hệ BGR[cite: 4]
        _output = AddOutput<IVisionImage>("Image"); // Khởi tạo cổng xuất ảnh đầu ra[cite: 4]
    }

    protected override void OnExecute(IToolContext context)
    {
        string path = _imagePath.Value; // Đọc giá trị đường dẫn cấu hình[cite: 4]

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) // Kiểm tra phòng thủ chuỗi trống hoặc tệp tin không tồn tại thực tế[cite: 4]
        {
            throw new ToolExecutionException($"Image file does not exist at path: '{path}'"); // Báo lỗi tiến trình thực thi pipeline[cite: 4]
        }

        Mat mat = Cv2.ImRead(path, _readMode.Value); // Thực thi hàm native đọc ảnh của OpenCV[cite: 4]

        if (mat.Empty()) // OpenCV không ném ngoại lệ khi đọc lỗi mà trả về ma trận rỗng[cite: 4]
        {
            mat.Dispose(); // Giải phóng vùng nhớ unmanaged ngay lập tức kể cả khi rỗng để tránh rò rỉ bộ nhớ[cite: 4]
            throw new ToolExecutionException($"Failed to decode image from path: '{path}'"); // Thông báo lỗi giải mã tệp ảnh[cite: 4]
        }

        _output.Value = new MatVisionImage(mat); // Bọc Mat thành đối tượng hệ thống và đẩy ra cổng ra, chuyển giao quyền sở hữu bộ nhớ[cite: 4]
        // _output.Value mang kiểu IVisionImage (Interface trừu tượng), nhưng đối tượng thực tế bên trong memory (Runtime Type) chính là một MatVisionImage
        // Nói cách khác: _output.Value chứa đối tượng Mat, chứ không phải bản thân nó là kiểu Mat.
        context.Log($"Image grabbed successfully from: {path}"); // Ghi nhật ký tiến trình[cite: 4]
    }
}
*/