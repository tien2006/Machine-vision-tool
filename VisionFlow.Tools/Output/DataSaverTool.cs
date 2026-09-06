// ==================== Vai trò chính:                Các node đầu cuối (terminal) — xuất kết quả ra file hoặc kết thúc luồng với phán định OK/NG
// ==================== Thành phần / Class tiêu biểu: DataSaverTool, ImageSaverTool, OutputTool
// ==================== Phụ thuộc vào:                OpenCvSharp + System.Text.Json
// ==================== Pattern / Kỹ thuật nổi bật:   Terminal Node Pattern

using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;

namespace VisionFlow.Tools.Output
{
    /// <summary>
    /// Node đầu cuối trong đồ thị xử lý (Execution Graph): Thực hiện ghi và lưu trữ toàn bộ dữ liệu kết quả kiểm tra
    /// (như tọa độ, góc xoay, điểm số, trạng thái phán định) ra các định dạng file chuẩn công nghiệp JSON, TXT, hoặc CSV.
    /// [QUY TRÌNH XỬ LÝ]:
    /// 1. Kiểm tra tính hợp lệ của dữ liệu đầu vào và cấu trúc đường dẫn thư mục lưu trữ (Tự động tạo thư mục nếu chưa tồn tại).
    /// 2. Phân định định dạng file mở rộng dựa trên tham số lựa chọn cấu hình (.json, .txt, .csv).
    /// 3. Xử lý logic trùng lặp file: Nếu tắt cờ Overwrite, hệ thống tự động sinh tên file tuần tự duy nhất (Unique Path) dạng _1, _2...
    /// 4. Chuyển đổi cấu trúc đối tượng C# sang chuỗi văn bản (Serialization) thông qua bộ cơ chế tối ưu hóa bộ nhớ đệm.
    /// 5. Ghi dữ liệu xuống ổ cứng (SSD/HDD) một cách an toàn và xuất đường dẫn file thực tế ra cổng Output Port.
    /// </summary>
    [ToolMetadata("DataSaver", DisplayName = "Data Saver", Category = "OutputSource",
        Description = "Save result data to a JSON/TXT/CSV file.")]
    public sealed class DataSaverTool : VisionTool
    {
        #region 1. Khai Báo Các Cổng Vào / Ra Dữ Liệu (Inputs / Outputs Ports)
        private readonly InputPort<object> _data;          // Dữ liệu bất kỳ cần lưu (Hỗ trợ cấu trúc Nguyên bản, Mảng, chuỗi, hoặc đối tượng Phức hợp)
        private readonly OutputPort<string> _savedPath;    // Đường dẫn tuyệt đối của file thực tế đã được lưu xuống đĩa cứng thành công
        #endregion

        #region 2. Hệ Thống Tham Số Cấu Hình Đường Dẫn & Định Dạng (Parameters)
        private readonly ToolParameter<string> _outputPath; // Thư mục mục tiêu sẽ chứa file xuất ra
        private readonly ToolParameter<string> _fileName;   // Tên file gốc (Không bao gồm phần mở rộng)
        private readonly ToolParameter<string> _format;     // Định dạng mã hóa dữ liệu: "JSON" (Mặc định), "TXT", hoặc "CSV"
        private readonly ToolParameter<bool> _overwrite;    // Cờ thiết lập: True (Ghi đè nếu trùng tên), False (Tự động đổi tên để bảo toàn dữ liệu cũ)
        #endregion

        #region 3. Thành Phần Tối Ưu Hóa Bộ Nhớ Ghi JSON (Performance Optimization)
        // BẪY HIỆU SUẤT TRONG C# (GC PRESSURE PITFALL): 
        // Nếu khởi tạo 'new JsonSerializerOptions()' trực tiếp bên trong hàm xử lý, mỗi khi Tool chạy (vài chục lần một giây),
        // .NET sẽ phải thực hiện lại quá trình phân tích Reflection và cấp phát vùng nhớ Heap liên tục. Điều này kích hoạt cơ chế 
        // Garbage Collector (GC) làm gián đoạn hệ thống. Việc dùng biến 'static readonly' giúp cache lại luồng xử lý và tăng tốc độ tối đa.
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true, // Định dạng chuỗi JSON có thụt lề dòng (Pretty Print) giúp người vận hành dễ đọc hiểu
            Converters = { new JsonStringEnumConverter() } // Tự động chuyển đổi các kiểu Enum thành dạng chữ thay vì lưu mã số nguyên thô
        };
        #endregion

        /// <summary>
        /// Hàm khởi tạo (Constructor): Đăng ký các cổng kết nối và gán giá trị mặc định cho các tham số lưu trữ file.
        /// </summary>
        public DataSaverTool()
        {
            // Liên kết cổng dữ liệu vào cấu trúc Core của Tool
            _data = AddInput<object>("Data", "Data");
            _outputPath = AddParameter("OutputPath", DefaultDir(), "Output Folder", category: "Output", order: 1);
            _fileName = AddParameter("FileName", "output", "File Name", category: "Output", order: 2);
            _format = AddChoiceParameter("SaveFormat", "JSON", new[] { "JSON", "TXT", "CSV" }, "Format", category: "Format", order: 1);
            _overwrite = AddParameter("OverwriteExisting", true, "Overwrite Existing", category: "Options", order: 1);
            _savedPath = AddOutput<string>("SavedFilePath", "Saved Path");
        }

        /// <summary>
        /// Hàm nội bộ xác định thư mục lưu trữ mặc định an toàn cho hệ thống.
        /// </summary>
        private static string DefaultDir()
        {
            try
            {
                // Ưu tiên lấy đường dẫn đến thư mục Desktop (Màn hình chính) của tài khoản Windows hiện tại để người dùng dễ quan sát
                return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            }
            catch
            {
                // Phương án Fallback: Nếu hệ thống nhúng (IPC/OS) hạn chế quyền truy cập Desktop, chuyển sang dùng thư mục Temp tạm thời của hệ điều hành
                return Path.GetTempPath();
            }
        }

        /// <summary>
        /// Điểm thực thi lõi (Core Execution) xử lý chuyển đổi dữ liệu và thực hiện tác vụ I/O ghi đĩa cứng.
        /// </summary>
        protected override void OnExecute(IToolContext context)
        {
            // BƯỚC 3.1: Kiểm tra tính hợp lệ tuyệt đối của dữ liệu đầu vào (Validation Checks)
            var data = _data.Value;
            if (data is null)
                throw new ToolExecutionException("No input data to save. Connect the Data input.");

            var dir = _outputPath.Value;
            if (string.IsNullOrWhiteSpace(dir))
                throw new ToolExecutionException("Output Folder is empty.");

            // ĐẢM BẢO AN TOÀN I/O: Tự động kiểm tra hệ thống thư mục. Nếu thư mục chưa tồn tại trên ổ đĩa, 
            // hàm này sẽ tự động tạo chuỗi thư mục phân cấp tương ứng, tránh lỗi DirectoryNotFoundException.
            Directory.CreateDirectory(dir);

            // BƯỚC 3.2: Xác định phần mở rộng (Extension) dựa trên định dạng đã chọn trên giao diện phần mềm
            var ext = _format.Value.ToUpperInvariant() switch
            {
                "TXT" => ".txt",
                "CSV" => ".csv",
                _ => ".json" // Mặc định tất cả các trường hợp khác đều quy về chuẩn JSON cấu trúc
            };

            // Kết hợp các thành phần để tạo ra đường dẫn tuyệt đối đầy đủ (Full Path)
            var path = Path.Combine(dir, _fileName.Value + ext);

            // XỬ LÝ TRÙNG TÊN (FILE CONFLICT): Nếu tệp tin đã tồn tại VÀ người dùng cấu hình KHÔNG cho phép ghi đè (_overwrite == false),
            // tiến hành chạy thuật toán tìm đường dẫn duy nhất bằng cách chèn thêm chỉ số đếm tuần tự ở đuôi tên file.
            if (!_overwrite.Value && File.Exists(path))
                path = UniquePath(path);

            // BƯỚC 3.3: Thực hiện chuyển đổi dữ liệu từ đối tượng C# sang định dạng chuỗi văn bản tương ứng
            var content = ext switch
            {
                ".json" => ToJson(data),
                ".csv" => ToCsv(data),
                _ => data.ToString() ?? string.Empty // Định dạng TXT thuần túy sẽ gọi hàm ép kiểu chuỗi ToString nguyên bản
            };

            // BƯỚC 3.4: Tác vụ I/O vật lý - Ghi toàn bộ nội dung chuỗi văn bản xuống ổ đĩa cứng theo đường dẫn xác định
            File.WriteAllText(path, content);

            // Cập nhật đường dẫn file thực tế ra cổng Output Port để các Node xử lý sau (ví dụ: FTP Upload hoặc Cloud Sync) có thể lấy dữ liệu
            _savedPath.Value = path;

            // Ghi nhật ký hệ thống (System Log) để phục vụ công tác giám sát luồng chạy của dây chuyền nhà máy
            context.Log($"DataSaver: saved {path}");
        }

        /// <summary>
        /// Hàm chuyển đổi đối tượng C# phức tạp thành chuỗi văn bản JSON cấu trúc cao.
        /// </summary>
        private static string ToJson(object data)
        {
            try
            {
                // Sử dụng cấu hình tĩnh JsonOptions đã được tối ưu hóa luồng nạp dữ liệu ở trên
                return JsonSerializer.Serialize(data, JsonOptions);
            }
            catch
            {
                // XỬ LÝ NGOẠI LỆ ĐẶC BIỆT (VISION EDGE CASE): 
                // Nếu đối tượng truyền vào là một kiểu dữ liệu không thể tuần tự hóa trực tiếp (ví dụ: Đối tượng ảnh IVisionImage chứa con trỏ Mat unmanaged),
                // hàm Serialize sẽ ném ra lỗi. Lúc này ta chủ động bắt lỗi để chuyển sang ghi thông tin mô tả kỹ thuật (Kích thước ảnh) thay vì làm crash hệ thống.
                return data is IVisionImage img
                    ? $"\"<image {img.Width}x{img.Height}>\""
                    : $"\"{data}\"";
            }
        }

        /// <summary>
        /// Hàm chuyển đổi danh sách hoặc tập hợp dữ liệu thành định dạng CSV (Comma-Separated Values).
        /// Đã được tối ưu hóa hiệu suất bằng StringBuilder để tránh phân mảnh bộ nhớ RAM.
        /// </summary>
        private static string ToCsv(object data)
        {
            // Kiểm tra xem dữ liệu có phải là một tập hợp (Danh sách/Mảng) tuần tự hay không và loại trừ trường hợp dữ liệu là chuỗi ký tự đơn thuần
            if (data is IEnumerable e && data is not string)
            {
                // TỐI ƯU HÓA HIỆU SUẤT MẠNG (GC & MEMORY): Sử dụng StringBuilder để lắp ghép các dòng dữ liệu 
                // thay vì dùng toán tử cộng chuỗi hoặc LINQ Cast/Select giúp giảm thiểu việc cấp phát chuỗi rác trên RAM.
                var sb = new StringBuilder();
                bool first = true;

                foreach (var item in e)
                {
                    if (!first) sb.Append('\n'); // Thêm ký tự xuống dòng giữa các hàng dữ liệu
                    sb.Append(item?.ToString() ?? string.Empty);
                    first = false;
                }
                return sb.ToString();
            }
            // Nếu dữ liệu đầu vào chỉ là một biến đơn (Scalar), trả về giá trị chuỗi nguyên bản của biến đó
            return data.ToString() ?? string.Empty;
        }

        /// <summary>
        /// Thuật toán tìm kiếm đường dẫn tệp tin duy nhất (Unique Path Resolution).
        /// Hàm này sẽ liên tục quét kiểm tra sự tồn tại của file và tăng tiến chỉ số đếm (1, 2, 3...) cho đến khi tìm được tên file chưa bị trùng.
        /// </summary>
        private static string UniquePath(string path)
        {
            // Tách biệt các thành phần hình học của đường dẫn để xử lý chèn chỉ số đếm
            var dir = Path.GetDirectoryName(path)!;                    // Lấy thư mục tổng
            var name = Path.GetFileNameWithoutExtension(path);         // Lấy tên file gốc (ví dụ: "result")
            var ext = Path.GetExtension(path);                         // Lấy phần mở rộng (ví dụ: ".json")

            var i = 1;
            var p = path;

            // VÒNG LẶP KIỂM TRA ĐĨA CỨNG (DISK CHECK LOOP): Chạy liên tục cho đến khi tên file tạo ra không bị trùng với bất kỳ file nào đang có
            while (File.Exists(p))
            {
                // Tạo tên file mới theo cấu trúc chuẩn: Thư_mục/Tên_file_Chỉ_số.Đuôi_mở_rộng (ví dụ: "C:/Data/result_1.json")
                p = Path.Combine(dir, $"{name}_{i++}{ext}");
            }
            return p; // Trả về đường dẫn sạch, an toàn để ghi file mà không lo đè mất dữ liệu cũ
        }
    }
}