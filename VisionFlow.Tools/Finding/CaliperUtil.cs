// ==================== Vai trò chính:                TRÁI TIM thuật toán: các công cụ đo lường/tìm kiếm hình học kiểu công nghiệp (Caliper)
// ==================== Thành phần / Class tiêu biểu: CaliperUtil (helper), FindCircleTool, FindLineTool
// ==================== Phụ thuộc vào:                OpenCvSharp + Core.Models
// ==================== Pattern / Kỹ thuật nổi bật:   Sub-pixel edge detection, RANSAC, Circle/Line fitting

using System; // Kéo các hàm toán học cơ bản của hệ thống vào sử dụng
using OpenCvSharp; // Thư viện xử lý ảnh OpenCV cho C#
using P2 = VisionFlow.Core.Models.Point2d; // Sử dụng Alias rút ngắn theo tài liệu

namespace VisionFlow.Tools.Finding; // Định nghĩa namespace quản lý công cụ tìm kiếm biên/cạnh

internal static class CaliperUtil // Lớp tiện ích static xử lý thuật toán Caliper (thước kẹp tìm cạnh)
{
    public static P2? FindEdgeAlongLine(Mat gray, P2 start, P2 end, double threshold, string polarity, int margin = 2)
    {
        double dx = end.X - start.X; // Khoảng cách chênh lệch tọa độ X giữa điểm cuối và đầu
        double dy = end.Y - start.Y; // Khoảng cách chênh lệch tọa độ Y giữa điểm cuối và đầu
        double dist = Math.Sqrt(dx * dx + dy * dy); // Độ dài hình học của đoạn quét Caliper
        int n = (int)Math.Ceiling(dist); // Làm tròn lên để xác định số lượng điểm lấy mẫu trên đoạn thẳng

        if (n < 5) return null; // Đoạn quét quá ngắn không đủ dữ liệu tính toán

        // Bước 1: Lấy mẫu cường độ sáng dọc theo đoạn Caliper
        double[] prof = new double[n]; // Khởi tạo mảng chứa biên độ xám (profile) của các điểm mẫu
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / (n - 1); // Tỷ lệ vị trí điểm hiện tại trên đoạn thẳng (từ 0.0 đến 1.0)
            double cx = start.X + t * dx; // Tọa độ X thực tế của điểm lấy mẫu hiện tại trên ảnh
            double cy = start.Y + t * dy; // Tọa độ Y thực tế của điểm lấy mẫu hiện tại trên ảnh
            prof[i] = SampleBilinear(gray, cx, cy); // Lấy giá trị độ sáng bằng nội suy song tuyến tính
        }

        // Bước 2: Tìm vị trí cạnh có Gradient tốt nhất
        int bestIdx = -1; // Chỉ số mảng của điểm có cạnh tốt nhất, mặc định chưa tìm thấy (-1)
        double bestMagnitude = 0; // Giá trị độ lớn gradient tốt nhất tìm được
        double bestSignedGrad = 0; // Giá trị gradient có dấu tốt nhất để lưu vết hướng đổi màu

        for (int i = margin; i < n - margin - 1; i++)
        {
            double grad = (prof[i + 1] - prof[i - 1]) / 2.0; // Sai phân trung tâm: Tính độ dốc gradient sáng
            double magnitude = Math.Abs(grad); // Độ lớn tuyệt đối của độ dốc (không quan tâm hướng sáng/tối)

            if (magnitude < threshold) continue; // Bỏ qua nếu độ sắc nét của cạnh nhỏ hơn ngưỡng quy định

            bool valid = polarity switch // Kiểm tra tính hợp lệ dựa trên cực tính (chiều chuyển màu) mong muốn
            {
                "DarkToLight" => grad > 0, // Từ tối sang sáng (gradient dương)
                "LightToDark" => grad < 0, // Từ sáng sang tối (gradient âm)
                _ => true // Mặc định chấp nhận cả hai hướng
            };

            if (valid && magnitude > bestMagnitude) // Nếu thỏa mãn cực tính và có độ sắc nét vượt trội hơn đỉnh cũ
            {
                bestMagnitude = magnitude; // Cập nhật độ lớn gradient lớn nhất mới
                bestIdx = i; // Ghi nhận chỉ số mảng tại vị trí đỉnh cạnh này
                bestSignedGrad = grad; // Ghi nhận giá trị gradient có dấu tương ứng
            }
        }

        if (bestIdx == -1) return null; // Không tìm thấy cạnh đạt yêu cầu

        // Bước 3: Nội suy Parabol để đạt độ chính xác Sub-pixel
        double p1 = Math.Abs((prof[bestIdx] - prof[bestIdx - 2]) / 2.0);    // Điểm bên trái (cách 1 bước về gradient)
        double p2 = bestMagnitude;                                          // Điểm đỉnh hiện tại
        double p3 = Math.Abs((prof[bestIdx + 2] - prof[bestIdx]) / 2.0);    // Điểm bên phải (cách 1 bước về gradient)

        double denom = 2.0 * (p1 - 2.0 * p2 + p3); // Mẫu số của phương trình đạo hàm nội suy Parabol
        double subIdx = bestIdx; // Khởi tạo chỉ số sub-pixel bằng chỉ số nguyên tốt nhất ban đầu
        if (Math.Abs(denom) > 1e-5) // Kiểm tra tránh lỗi chia cho 0 nếu mẫu số quá nhỏ gần bằng không
        {
            subIdx = bestIdx - (p1 - p3) / denom; // Đỉnh của Parabol: Tìm vị trí cực trị mịn thực sự
        }
        subIdx = Math.Clamp(subIdx, 0.0, n - 1); // Ràng buộc chỉ số sub-pixel nằm trong phạm vi mảng hợp lệ

        // Quy đổi chỉ số mảng ngược lại tọa độ thực của ảnh
        double finalT = subIdx / (n - 1); // Tính lại tỷ lệ vị trí chính xác sau nội suy (từ 0.0 đến 1.0)
        return new P2(start.X + finalT * dx, start.Y + finalT * dy); // Tính và trả về tọa độ điểm cạnh sub-pixel thực tế
    }

    // Lấy giá trị độ sáng tại một tọa độ có số thập phân (x, y).
    // Bilinear Interpolation: Nội suy song tuyến tính. x, y là số thực, nhưng giá trị pixel là số nguyên 
    //  -> Tìm 4 pixel số nguyên vây quanh điểm thập phân đó, rồi tính trung bình trọng số của chúng dựa vào khoảng cách.
    private static double SampleBilinear(Mat gray, double x, double y)
    {
        int x0 = (int)Math.Floor(x);    // Làm tròn xuống để tìm pixel bên trái
        int y0 = (int)Math.Floor(y);    // Làm tròn xuống để tìm pixel phía trên
        int x1 = x0 + 1;                // Pixel bên phải
        int y1 = y0 + 1;                // Pixel phía dưới

        if (x0 < 0 || x1 >= gray.Width || y0 < 0 || y1 >= gray.Height) // Kiểm tra nếu tọa độ nằm ngoài vùng ảnh
        {
            // Kẹp biên nếu rơi sát rìa ảnh
            int cx = Math.Clamp((int)Math.Round(x), 0, gray.Width - 1); // Giới hạn tọa độ X trong khoảng ảnh hợp lệ
            // Nếu giá trị nằm trong khoảng từ min đến max thì giữ nguyên. Nếu nhỏ hơn min thì lấy min. Nếu lớn hơn max thì lấy max
            int cy = Math.Clamp((int)Math.Round(y), 0, gray.Height - 1); // Giới hạn tọa độ Y trong khoảng ảnh hợp lệ
            return gray.At<byte>(cy, cx); // Trả về giá trị pixel xám tại biên ảnh gần nhất
        }

        double fx = x - x0; // Tỷ lệ phần thập phân (khoảng cách sai lệch) theo trục X
        double fy = y - y0; // Tỷ lệ phần thập phân (khoảng cách sai lệch) theo trục Y

        // Trích xuất độ sáng 4 pixel lân cận (Lưu ý: OpenCV truy cập theo thứ tự dòng trước cột sau y,x)
        double p00 = gray.At<byte>(y0, x0); // Giá trị pixel góc trên-trái
        double p10 = gray.At<byte>(y0, x1); // Giá trị pixel góc trên-phải
        double p01 = gray.At<byte>(y1, x0); // Giá trị pixel góc dưới-trái
        double p11 = gray.At<byte>(y1, x1); // Giá trị pixel góc dưới-phải

        // Công thức trộn nội suy song tuyến tính cổ điển
        return (1 - fx) * (1 - fy) * p00 + fx * (1 - fy) * p10 + (1 - fx) * fy * p01 + fx * fy * p11;
    }
}