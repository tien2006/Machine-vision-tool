// ==================== Vai trò chính:                Node nguồn — nạp HÀNG LOẠT ảnh từ 1 thư mục thành ImageList
// ==================== Thành phần / Class tiêu biểu: FolderImageLoaderTool
// ==================== Phụ thuộc vào:                OpenCvSharp
// ==================== Pattern / Kỹ thuật nổi bật:   Batch Input Source Node — output là 1 danh sách ảnh
//                       dùng cho các Tool cần nhiều ảnh cùng lúc (VD: CalibCheckerboard.ImageList)

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;

namespace VisionFlow.Tools.Acquisition;

/// <summary>Tiêu chí sắp xếp danh sách file khi quét thư mục.</summary>
public enum FolderSortBy
{
    Name, // Sắp xếp theo tên file (ổn định nhất để chạy lặp lại nhiều lần cho kết quả giống nhau)
    Date, // Sắp xếp theo thời gian sửa đổi cuối (hữu ích khi muốn lấy ảnh mới nhất trước)
    Size  // Sắp xếp theo dung lượng file
}

/// <summary>
/// Node nguồn nạp HÀNG LOẠT ảnh cùng lúc, khác với <see cref="GrabImageTool"/> (chỉ trả về 1 ảnh/lần chạy).
/// Dùng khi cần xử lý theo lô (batch), ví dụ: nạp toàn bộ ảnh checkerboard để hiệu chuẩn camera
/// (CalibCheckerboardTool.ImageList), hoặc test 1 thuật toán trên hàng trăm ảnh mẫu cùng lúc.
/// </summary>
[ToolMetadata("FolderImageLoader", DisplayName = "Folder Image Loader", Category = "InputSource",
    Description = "Load all images in a folder (optionally recursive) into an ImageList for batch processing.")]
public sealed class FolderImageLoaderTool : VisionTool
{
    // --- Tab "Folder": xác định file nào được nạp ---
    private readonly ToolParameter<string> _folderPath;
    private readonly ToolParameter<bool> _includeSubfolders;   // Có quét cả thư mục con hay không
    private readonly ToolParameter<string> _searchPattern;     // Mẫu tên file (VD: "Product_*.png"), mặc định "*"
    private readonly ToolParameter<string> _fileExtensions;    // Danh sách đuôi file hợp lệ, phân cách bằng dấu phẩy

    // --- Tab "Load": chọn/giới hạn/sắp xếp file ---
    private readonly ToolParameter<int> _maxImages;            // 0 = không giới hạn
    private readonly ToolParameter<FolderSortBy> _sortBy;
    private readonly ToolParameter<bool> _sortDescending;

    // --- Tab "Processing": xử lý ảnh sau khi đọc ---
    private readonly ToolParameter<bool> _convertToGrayscale;
    private readonly ToolParameter<bool> _resizeImages;
    private readonly ToolParameter<int> _targetWidth;
    private readonly ToolParameter<int> _targetHeight;

    // --- Outputs ---
    private readonly OutputPort<IReadOnlyList<IVisionImage>> _imageListOutput; // Tên cổng "ImageList" - quy ước dùng chung toàn hệ thống (xem CalibCheckerboardTool)
    private readonly OutputPort<IReadOnlyList<string>> _imagePathsOutput;
    private readonly OutputPort<int> _imageCountOutput;

    // LƯU Ý QUAN TRỌNG VỀ BỘ NHỚ:
    // FlowExecutor.TrackOutputs() chỉ tự động theo dõi/giải phóng output có kiểu IVisionImage đơn lẻ,
    // KHÔNG nhận diện được output kiểu danh sách (IReadOnlyList<IVisionImage>). Nếu không tự quản lý,
    // mỗi lần Execute() chạy lại sẽ tạo ra N ảnh Mat mới trong khi N ảnh Mat của lần chạy trước
    // không bao giờ được Dispose() -> RÒ RỈ BỘ NHỚ tăng dần theo từng lần chạy flow.
    // => Tool phải tự lưu vết batch trước và tự giải phóng trước khi tạo batch mới.
    private readonly List<IVisionImage> _previousBatch = new();

    private static readonly string[] DefaultExtensions = { "jpg", "jpeg", "png", "bmp", "tif", "tiff" };

    public FolderImageLoaderTool()
    {
        _folderPath = AddParameter("FolderPath", string.Empty, "Folder Path", category: "Folder", order: 1);
        _includeSubfolders = AddParameter("IncludeSubfolders", false, "Include Subfolders", category: "Folder", order: 2);
        _searchPattern = AddParameter("SearchPattern", "*", "Search Pattern", category: "Folder", order: 3);
        _fileExtensions = AddParameter("FileExtensions", string.Join(",", DefaultExtensions), "File Extensions", category: "Folder", order: 4);

        _maxImages = AddParameter("MaxImages", 0, "Max Images", min: 0, max: 100000, category: "Load", order: 1);
        _sortBy = AddParameter("SortBy", FolderSortBy.Name, "Sort By", category: "Load", order: 2);
        _sortDescending = AddParameter("SortDescending", false, "Sort Descending", category: "Load", order: 3);

        _convertToGrayscale = AddParameter("ConvertToGrayscale", false, "Convert To Grayscale", category: "Processing", order: 1);
        _resizeImages = AddParameter("ResizeImages", false, "Resize Images", category: "Processing", order: 2);
        _targetWidth = AddParameter("TargetWidth", 640, "Target Width", min: 1, max: 8192, category: "Processing", order: 3);
        _targetHeight = AddParameter("TargetHeight", 480, "Target Height", min: 1, max: 8192, category: "Processing", order: 4);

        _imageListOutput = AddOutput<IReadOnlyList<IVisionImage>>("ImageList", "Image List");
        _imagePathsOutput = AddOutput<IReadOnlyList<string>>("ImagePaths", "Image Paths");
        _imageCountOutput = AddOutput<int>("ImageCount", "Image Count");
    }

    protected override void OnExecute(IToolContext context)
    {
        string folder = _folderPath.Value;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            throw new ToolExecutionException($"Folder does not exist: '{folder}'");

        // ----- Bước 1: liệt kê file theo SearchPattern + IncludeSubfolders -----
        var searchOption = _includeSubfolders.Value ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        string pattern = string.IsNullOrWhiteSpace(_searchPattern.Value) ? "*" : _searchPattern.Value;
        var candidates = Directory.GetFiles(folder, pattern, searchOption);

        // ----- Bước 2: lọc theo đuôi file hợp lệ (FileExtensions) -----
        var allowedExt = (_fileExtensions.Value ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.StartsWith('.') ? e.ToLowerInvariant() : "." + e.ToLowerInvariant()) // Chuẩn hóa "png" -> ".png"
            .ToHashSet();
        if (allowedExt.Count == 0) // Nếu người dùng để trống -> dùng danh sách mặc định, tránh lọc ra rỗng
            allowedExt = DefaultExtensions.Select(e => "." + e).ToHashSet();

        var filtered = candidates.Where(f => allowedExt.Contains(Path.GetExtension(f).ToLowerInvariant()));

        // ----- Bước 3: sắp xếp theo SortBy + SortDescending -----
        IEnumerable<string> sorted = _sortBy.Value switch
        {
            FolderSortBy.Date => filtered.OrderBy(f => File.GetLastWriteTimeUtc(f)),
            FolderSortBy.Size => filtered.OrderBy(f => new FileInfo(f).Length),
            _ => filtered.OrderBy(f => f, StringComparer.OrdinalIgnoreCase) // Name (mặc định) - ổn định giữa các lần chạy
        };
        if (_sortDescending.Value)
            sorted = sorted.Reverse();

        // ----- Bước 4: giới hạn số lượng (MaxImages = 0 nghĩa là không giới hạn) -----
        var finalPaths = (_maxImages.Value > 0 ? sorted.Take(_maxImages.Value) : sorted).ToList();

        if (finalPaths.Count == 0)
            throw new ToolExecutionException($"No images matched in folder '{folder}' (pattern='{pattern}', extensions='{_fileExtensions.Value}').");

        // ----- Bước 5: đọc từng file, xử lý (grayscale/resize) rồi đóng gói thành IVisionImage -----
        var images = new List<IVisionImage>(finalPaths.Count);
        var loadedPaths = new List<string>(finalPaths.Count);

        try
        {
            foreach (var path in finalPaths)
            {
                var mat = Cv2.ImRead(path, ImreadModes.Color);
                if (mat.Empty())
                {
                    // Ảnh lỗi/hỏng trong 1 batch lớn không nên làm sập toàn bộ quá trình nạp -> chỉ ghi log cảnh báo và bỏ qua file đó
                    context.Log($"FolderImageLoader: skip invalid image '{path}'");
                    mat.Dispose();
                    continue;
                }

                if (_convertToGrayscale.Value && mat.Channels() == 3)
                {
                    var gray = new Mat();
                    Cv2.CvtColor(mat, gray, ColorConversionCodes.BGR2GRAY);
                    mat.Dispose(); // Giải phóng ảnh màu gốc ngay sau khi đã chuyển đổi xong, tránh giữ 2 bản trong bộ nhớ
                    mat = gray;
                }

                if (_resizeImages.Value)
                {
                    var resized = new Mat();
                    Cv2.Resize(mat, resized, new Size(_targetWidth.Value, _targetHeight.Value));
                    mat.Dispose();
                    mat = resized;
                }

                images.Add(new MatVisionImage(mat));
                loadedPaths.Add(path);
            }
        }
        catch
        {
            // Batch mới chưa được publish; dọn nó và giữ nguyên batch cũ để UI không trỏ vào Mat đã dispose.
            foreach (var image in images)
                image.Dispose();
            throw;
        }

        if (images.Count == 0)
            throw new ToolExecutionException($"All {finalPaths.Count} matched file(s) failed to decode as images.");

        // Publish batch mới trước, rồi mới giải phóng batch cũ. Nếu pattern không khớp hoặc việc đọc lỗi,
        // các output vẫn trỏ tới batch cũ còn sống thay vì Mat đã dispose.
        _imageListOutput.Value = images;
        _imagePathsOutput.Value = loadedPaths;
        _imageCountOutput.Value = images.Count;

        foreach (var old in _previousBatch)
            old.Dispose();
        _previousBatch.Clear();
        _previousBatch.AddRange(images); // Ghi vết để lần Execute() kế tiếp tự giải phóng đúng batch này

        context.Log($"FolderImageLoader: loaded {images.Count}/{finalPaths.Count} image(s) from '{folder}'");
    }
}
