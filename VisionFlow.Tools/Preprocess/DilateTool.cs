// ==================== Vai trò chính:                Giãn nở (mở rộng) vùng sáng trong ảnh nhị phân/xám -> nối chi tiết đứt đoạn, làm dày nét mảnh
// ==================== Thành phần / Class tiêu biểu: DilateTool
// ==================== Phụ thuộc vào:                OpenCvSharp
// ==================== Pattern / Kỹ thuật nổi bật:   Phép toán hình thái học cơ bản (Morphological Dilation) dùng Structuring Element trượt qua ảnh

using System;
using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;

namespace VisionFlow.Tools.Preprocess; // Cùng nhóm "xử lý mức pixel" với ThresholdTool/InRangeTool, dù Category hiển thị UI là "Morphological"

/// <summary>
/// Tool giãn nở (Dilate): với mỗi pixel trong ảnh, nếu BẤT KỲ pixel nào nằm dưới vùng phủ của kernel
/// là trắng (255) thì pixel trung tâm cũng trở thành trắng. Kết quả: vùng trắng (đối tượng/foreground)
/// phình to ra theo hình dạng của kernel, vùng đen (nền) thu hẹp lại.
/// Thường dùng SAU Threshold/InRange để: nối các nét bị đứt đoạn, lấp lỗ nhỏ bên trong vật thể,
/// hoặc làm dày các chi tiết mảnh (như đường mạch PCB) trước khi FindContours.
/// </summary>
[ToolMetadata("Dilate", DisplayName = "Dilate", Category = "Morphological",
    Description = "Expand (grow) bright regions in a binary/grayscale image")]
public sealed class DilateTool : VisionTool
{
    #region 1. Khai báo Port (In/Out)
    private readonly InputPort<IVisionImage> _input;   // Cổng vào: ảnh nhị phân (thường sau Threshold) hoặc ảnh xám
    private readonly OutputPort<IVisionImage> _output; // Cổng ra: ảnh sau khi đã giãn nở (vùng trắng to hơn)
    #endregion

    #region 2. Khai báo Parameter
    private readonly ToolParameter<int> _kernelSize;         // Kích thước nhân (kernel) NxN
    private readonly ToolParameter<MorphShapes> _shape;      // Hình dạng nhân: Rect/Cross/Ellipse
    private readonly ToolParameter<int> _iterations;         // Số lần lặp phép Dilate
    #endregion

    public DilateTool()
    {
        // ----- Port -----
        _input = AddInput<IVisionImage>("Image");
        _output = AddOutput<IVisionImage>("Image");

        // ----- Parameter -----
        // KernelSize: tăng từ 3 lên 5/7 thì vùng ảnh hưởng của mỗi pixel trắng rộng hơn -> vật to ra nhanh hơn mỗi lần quét
        _kernelSize = AddParameter("KernelSize", 3, "Kernel Size", 1, 99, category: "Morphology", order: 1);

        // Shape: hình chữ thập (Cross) giãn mạnh theo trục ngang/dọc, ít theo đường chéo;
        // Ellipse cho kết quả tự nhiên hơn với vật tròn; Rect tác động đều mọi hướng, mạnh nhất
        _shape = AddParameter("Shape", MorphShapes.Rect, "Shape", category: "Morphology", order: 2);

        // Iterations: thay vì dùng 1 kernel cực lớn (dễ méo hình dạng), lặp lại nhiều lần kernel nhỏ
        // để kiểm soát độ mịn của đường biên kết quả tốt hơn
        _iterations = AddParameter("Iterations", 1, "Iterations", 1, 50, category: "Morphology", order: 3);
    }

    protected override void OnExecute(IToolContext context)
    {
        Mat src = _input.Value!.AsMat(); // Lấy ma trận ảnh gốc từ cổng vào

        // ----- Bước 1: Ép KernelSize về số nguyên dương hợp lệ (kernel 0 hoặc âm sẽ khiến OpenCV ném lỗi) -----
        int kernelSize = Math.Max(1, _kernelSize.Value);

        // ----- Bước 2: Tạo Structuring Element (nhân cấu trúc) theo hình dạng và kích thước đã chọn -----
        // Ví dụ: Shape=Cross, KernelSize=3 -> tạo ma trận 3x3 hình dấu cộng (chỉ hàng/cột giữa = 1, còn lại = 0)
        using Mat kernel = Cv2.GetStructuringElement(_shape.Value, new Size(kernelSize, kernelSize));

        // ----- Bước 3: Chạy phép Dilate -----
        Mat dst = new Mat(); // Ma trận đích chứa kết quả sau giãn nở
        Cv2.Dilate(
            src, dst, kernel,
            anchor: null,                    // null -> OpenCV tự đặt tâm kernel ở chính giữa (mặc định)
            iterations: Math.Max(1, _iterations.Value), // Ép Iterations tối thiểu = 1
            borderType: BorderTypes.Constant, // Vùng ngoài biên ảnh coi như giá trị cố định (mặc định 0 = đen)
            borderValue: null);               // null -> OpenCV dùng giá trị biên mặc định (0)

        // ----- Ghi kết quả ra cổng output -----
        _output.Value = new MatVisionImage(dst);

        context.Log($"Dilate: kernel={kernelSize}x{kernelSize} ({_shape.Value}), iterations={_iterations.Value}");
    }
}