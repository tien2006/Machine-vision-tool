// ==================== Vai trò chính:                Các node đầu cuối (terminal) — xuất kết quả ra file hoặc kết thúc luồng với phán định OK/NG
// ==================== Thành phần / Class tiêu biểu: DataSaverTool, ImageSaverTool, OutputTool
// ==================== Phụ thuộc vào:                OpenCvSharp + System.Text.Json
// ==================== Pattern / Kỹ thuật nổi bật:   Terminal Node Pattern

using System;
using System.IO;
using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;

namespace VisionFlow.Tools.Output
{
    /// <summary>
    /// Node đầu cuối trong đồ thị xử lý: Thực hiện lưu trữ ma trận ảnh (Mat) xuống đĩa cứng thành file ảnh chuẩn (PNG/JPG/BMP/TIFF).
    /// [QUY TRÌNH XỬ LÝ I/O]:
    /// 1. Kiểm tra tính toàn vẹn của dữ liệu ảnh đầu vào, tránh ghi các ma trận rỗng hoặc bị lỗi bộ nhớ unmanaged.
    /// 2. Xác định phần mở rộng (.png, .jpg, .bmp, .tiff) và thiết lập cấu hình nén chất lượng nén cho JPEG/PNG.
    /// 3. Xử lý logic tránh xung đột tệp tin (Unique Path) tuần tự _1, _2... nếu tắt cờ ghi đè (Overwrite).
    /// 4. Chuyển đổi mã hóa ma trận byte OpenCV và thực hiện tác vụ I/O vật lý ghi đĩa cứng qua hàm Cv2.ImWrite.
    /// 5. Đẩy thông tin kích thước ảnh dạng chuỗi (WxH), đường dẫn thực tế, và trạng thái phán định Success ra các cổng Output.
    /// </summary>
    [ToolMetadata("ImageSaver", DisplayName = "Image Saver", Category = "OutputSource",
        Description = "Save an image to a file (PNG/JPG/BMP/TIFF).")]
    public sealed class ImageSaverTool : VisionTool
    {
        #region 1. Khai Báo Các Cổng Vào / Ra Dữ Liệu (Inputs / Outputs Ports)
        private readonly InputPort<IVisionImage> _input;     // Ảnh gốc đầu vào cần lưu (Hỗ trợ cấu trúc ảnh Gray, BGR, BGRA)
        private readonly OutputPort<string> _savedPath;    // Đường dẫn tuyệt đối của file ảnh đã lưu thành công trên ổ đĩa
        private readonly OutputPort<bool> _success;        // Trạng thái lưu file: True (Thành công), False (Thất bại)
        private readonly OutputPort<string> _imageSize;      // Chuỗi văn bản hiển thị kích thước ảnh (ví dụ: "1920x1080")
        #endregion

        #region 2. Hệ Thống Tham Số Cấu Hình Định Dạng & Chất Lượng (Parameters)
        private readonly ToolParameter<string> _outputPath; // Thư mục đích sẽ chứa file ảnh xuất ra
        private readonly ToolParameter<string> _fileName;   // Tên file ảnh gốc (Không bao gồm phần mở rộng hình ảnh)
        private readonly ToolParameter<string> _format;     // Định dạng ảnh: "PNG" (Mặc định), "JPG", "BMP", hoặc "TIFF"
        private readonly ToolParameter<int> _quality;       // Tỷ lệ nén ảnh JPEG (Từ 0 đến 100). Số càng cao ảnh càng nét nhưng dung lượng file càng lớn
        private readonly ToolParameter<bool> _overwrite;    // Cờ thiết lập: True (Ghi đè nếu trùng tên), False (Tự động sinh tên file tuần tự mới)
        #endregion

        /// <summary>
        /// Hàm khởi tạo (Constructor): Liên kết các cổng dữ liệu và thiết lập dải tham số mặc định cho UI Grid.
        /// </summary>
        public ImageSaverTool()
        {
            // Đăng ký các cổng kết nối vào lõi Engine của hệ thống Tool
            _input = AddInput<IVisionImage>("Image", "Image");
            _outputPath = AddParameter("OutputPath", DefaultDir(), "Output Folder", category: "Output", order: 1);
            _fileName = AddParameter("FileName", "saved_image", "File Name", category: "Output", order: 2);
            _format = AddChoiceParameter("ImageFormat", "PNG", new[] { "PNG", "JPG", "BMP", "TIFF" }, "Format", category: "Output", order: 3);

            // Tham số chất lượng nén ảnh chỉ có tác dụng đối với định dạng nén mất dữ liệu (Lossy Compression) như JPEG
            _quality = AddParameter("Quality", 95, "JPEG Quality", 0, 100, category: "Processing", order: 1);
            _overwrite = AddParameter("OverwriteExisting", true, "Overwrite Existing", category: "Processing", order: 2);

            _savedPath = AddOutput<string>("SavedFilePath", "Saved Path");
            _success = AddOutput<bool>("Success", "Success");
            _imageSize = AddOutput<string>("ImageSize", "Image Size");
        }

        /// <summary>
        /// Xác định thư mục lưu trữ mặc định an toàn dựa trên cấu trúc hệ điều hành (Windows/Linux Embedded).
        /// </summary>
        private static string DefaultDir()
        {
            try
            {
                // Ưu tiên trích xuất đường dẫn màn hình Desktop của User hiện hành để kỹ sư nhà máy dễ kiểm tra ảnh
                return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            }
            catch
            {
                // Phương án dự phòng (Fallback): Nếu chạy dưới quyền Service bị hạn chế Profile, chuyển sang dùng thư mục Temp tạm thời
                return Path.GetTempPath();
            }
        }

        /// <summary>
        /// Điểm thực thi lõi (Core Execution) mã hóa cấu trúc ảnh và tương tác I/O với hệ thống tệp tin vật lý.
        /// </summary>
        protected override void OnExecute(IToolContext context)
        {
            // BƯỚC 1: Trích xuất ma trận ảnh OpenCV từ lớp bọc IVisionImage
            var mat = _input.Value!.AsMat();

            // BẪY HOẠT ĐỘNG (SAFETY GUARD): Kiểm tra xem ma trận ảnh có bị rỗng không (Ví dụ do camera mất kết nối hoặc lỗi bộ đệm)
            if (mat.Empty())
                throw new ToolExecutionException("Input image is empty.");

            // BƯỚC 2: Kiểm tra và chuẩn bị cấu trúc thư mục lưu trữ ảnh
            var dir = _outputPath.Value;
            if (string.IsNullOrWhiteSpace(dir))
                throw new ToolExecutionException("Output Folder is empty.");

            // Tự động tạo thư mục phân cấp nếu chưa tồn tại trên ổ đĩa, ngăn ngừa lỗi DirectoryNotFoundException
            Directory.CreateDirectory(dir);

            // BƯỚC 3: Ánh xạ chuỗi lựa chọn cấu hình định dạng thành phần mở rộng file ảnh chuẩn định dạng hệ thống
            var ext = _format.Value.ToUpperInvariant() switch
            {
                "JPG" or "JPEG" => ".jpg", // Định dạng nén mất dữ liệu (Lossy), dung lượng nhỏ, phù hợp lưu ảnh mẫu
                "BMP" => ".bmp",           // Định dạng thô không nén (Raw), ghi file cực nhanh vì CPU không phải tính toán nhưng rất tốn dung lượng
                "TIFF" => ".tiff",         // Định dạng đồ họa kỹ thuật chất lượng cao, thường dùng trong đo lường chính xác
                _ => ".png"                // Định dạng mặc định nén không mất dữ liệu (Lossless), tối ưu cho ảnh đồ họa biên cạnh
            };

            // Kết hợp tạo thành đường dẫn tuyệt đối đầy đủ của tệp tin ảnh mục tiêu
            var path = Path.Combine(dir, _fileName.Value + ext);

            // LOGIC XỬ LÝ XUNG ĐỘT FILE (DUPLICATE FILENAME): Nếu cấu hình KHÔNG cho phép ghi đè (_overwrite == false),
            // gọi thuật toán vòng lặp đĩa cứng để tự động chèn thêm số đếm tăng tiến (_1, _2, _3...) đảm bảo không làm mất ảnh cũ của máy.
            if (!_overwrite.Value && File.Exists(path))
                path = UniquePath(path);

            // BƯỚC 4: THIẾT LẬP THAM SỐ MÃ HÓA CHO ĐỘNG CƠ OPENCV (ENCODING PARAMETERS)
            // Khởi tạo mảng số nguyên chứa cặp giá trị cấu hình [Flag ID, Tham số giá trị] truyền vào hàm ImWrite.
            // Nếu là ảnh JPG, gán Flag JpegQuality kèm mức chất lượng được ép dải an toàn từ 0 đến 100 bằng hàm Math.Clamp.
            // Nếu là các định dạng khác (PNG/BMP), truyền mảng rỗng Array.Empty để OpenCV tự động áp dụng cấu hình mặc định của hệ thống.
            int[] pars = ext == ".jpg"
                ? new[] { (int)ImwriteFlags.JpegQuality, Math.Clamp(_quality.Value, 0, 100) }
                : Array.Empty<int>();

            // BƯỚC 5: GHI FILE VẬT LÝ (DISK I/O WRITING)
            // Hàm Cv2.ImWrite gọi tầng mã nguồn C++ gốc để nén luồng byte ma trận và ghi xuống ổ cứng.
            // Kết quả trả về kiểu 'bool' chỉ thị hành vi ghi file có được hệ điều hành Windows thông qua hay không.
            var ok = Cv2.ImWrite(path, mat, pars);

            // Gán trạng thái thành công/thất bại trực tiếp ra cổng đầu ra để các Node logic (như Logic Gate, Script) có thể rẽ nhánh
            _success.Value = ok;

            // Nếu hệ điều hành từ chối ghi file (Ví dụ: Ổ cứng đầy, phân quyền thư mục bị khóa Admin, hoặc file đang bị app khác mở), ném ngoại lệ dừng luồng.
            if (!ok)
                throw new ToolExecutionException($"Failed to save image to: {path}");

            // BƯỚC 6: XUẤT DỮ LIỆU HOÀN THIỆN RA CÁC CỔNG OUTPUT PORT
            _savedPath.Value = path;

            // Định dạng chuỗi văn bản kích thước ảnh dạng "Width x Height" giúp giao diện người dùng hiển thị thông tin trực quan
            _imageSize.Value = $"{mat.Width}x{mat.Height}";

            // Ghi vết lịch sử hệ thống (System Traceability Log) hỗ trợ bảo trì dây chuyền sản xuất từ xa
            context.Log($"ImageSaver: saved {path}");
        }

        /// <summary>
        /// Thuật toán tìm kiếm đường dẫn duy nhất (Unique Path Resolution).
        /// Thực hiện phân tách hình học chuỗi đường dẫn và chạy vòng lặp kiểm tra đĩa cứng (Disk Lookahead) để sinh tên file sạch.
        /// </summary>
        private static string UniquePath(string path)
        {
            var dir = Path.GetDirectoryName(path)!;                    // Trích xuất thư mục cha
            var name = Path.GetFileNameWithoutExtension(path);         // Trích xuất tên ảnh gốc (ví dụ: "saved_image")
            var ext = Path.GetExtension(path);                         // Trích xuất phần mở rộng ảnh (ví dụ: ".png")

            var i = 1;
            var p = path;

            // Chạy liên tục kiểm tra trên đĩa cứng, nếu file tên "saved_image_1.png" đã tồn tại thì tăng tiến lên "saved_image_2.png"
            while (File.Exists(p))
            {
                p = Path.Combine(dir, $"{name}_{i++}{ext}");
            }
            return p; // Trả về đường dẫn duy nhất đã được xác thực không trùng lặp
        }
    }
}