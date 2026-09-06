// ==================== Vai trò chính:                Các node đầu cuối (terminal) — xuất kết quả ra file hoặc kết thúc luồng với phán định OK/NG
// ==================== Thành phần / Class tiêu biểu: DataSaverTool, ImageSaverTool, OutputTool
// ==================== Phụ thuộc vào:                OpenCvSharp + System.Text.Json
// ==================== Pattern / Kỹ thuật nổi bật:   Terminal Node Pattern

using OpenCvSharp;
using System;
using VisionFlow.Core.Imaging; // Nhận diện cấu trúc dữ liệu ảnh VisionImage[cite: 4]
using VisionFlow.Core.Models; // Kéo vào các cấu trúc dữ liệu kết quả đo đạc (CircleResult, Judge)[cite: 4]
using VisionFlow.Core.Ports; // Quản lý cổng kết nối đầu vào phục vụ kiểm tra điều kiện[cite: 4]
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging; // Kế thừa lớp nền tảng thiết kế VisionTool[cite: 4]

namespace VisionFlow.Tools.Output; // Định vị thuộc nhóm node terminal đầu ra kết quả[cite: 4]

/// <summary>
/// Node kết thúc (Terminal Node) của một flow xử lý ảnh đồ họa[cite: 4].
/// Tiêu thụ hình ảnh kết quả cuối cùng cùng dữ liệu hình học để phân định trạng thái OK/NG và không sinh thêm cổng ra[cite: 4].
/// </summary>
[ToolMetadata("Output", DisplayName = "Output", Category = "OutputSource", Description = "Flow endpoint: receives the result image and OK/NG judgment")] // Đăng ký nhãn terminal kết thúc luồng[cite: 4]
public sealed class OutputTool : VisionTool
{
    private readonly InputPort<IVisionImage> _image; // Cổng vào nhận hình ảnh kết quả cuối để hiển thị đồ họa preview[cite: 4]
    private readonly InputPort<CircleResult> _circle; // Cổng vào nhận thông số đo đạc hình học từ các node đo lường trước đó[cite: 4]

    public Judge LastJudge { get; private set; } = Judge.None; // Thuộc tính lưu trữ kết quả phân định kiểm tra gần nhất, bên ngoài chỉ có quyền đọc[cite: 4]

    public OutputTool()
    {
        _image = AddInput<IVisionImage>("Image"); // Khai báo cổng ảnh bắt buộc phải nối dây[cite: 4]
        _circle = AddInput<CircleResult>("Circle","Circle", optional: true); // Khai báo cổng kết quả đo tròn không bắt buộc nối dây (Optional)[cite: 4]
    }

    protected override void OnExecute(IToolContext context)
    {
        // 1. Lấy phán định từ các node đo đạc
        LastJudge = _circle.Value?.Judge ?? Judge.None;
        context.Log($"Output Judge = {LastJudge}");

        // 2. Bổ sung logic vẽ chữ lên ảnh
        if (_image.Value != null)
        {
            var mat = _image.Value.AsMat();

            string text = LastJudge.ToString();

            // Quyết định màu sắc hiển thị
            Scalar color = LastJudge switch
            {
                Judge.OK => new Scalar(0, 255, 0),   // Xanh lá tươi
                Judge.NG => new Scalar(0, 0, 255),   // Đỏ rực
                _ => new Scalar(0, 255, 255)  // Vàng (cho trạng thái None/Chưa phán định)
            };

            // Vẽ chữ OK/NG/None ở góc trên bên trái
            Cv2.PutText(mat, text, new Point(30, 60), HersheyFonts.HersheySimplex, 2.0, color, 3, LineTypes.AntiAlias);
        }
    }
}