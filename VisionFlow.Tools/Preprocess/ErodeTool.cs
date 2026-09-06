// ==================== Vai trò chính:                Bào mòn (thu nhỏ) vùng sáng trong ảnh nhị phân/xám -> loại nhiễu nhỏ, tách vật thể dính liền
// ==================== Thành phần / Class tiêu biểu: ErodeTool
// ==================== Phụ thuộc vào:                OpenCvSharp
// ==================== Pattern / Kỹ thuật nổi bật:   Phép toán hình thái học cơ bản (Morphological Erosion) - đối ngẫu (dual) của Dilate

using System;
using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;

namespace VisionFlow.Tools.Preprocess; // Cùng nhóm "xử lý mức pixel" với DilateTool, Category hiển thị UI vẫn là "Morphological"

/// <summary>
/// Tool bào mòn (Erode): với mỗi pixel trong ảnh, chỉ khi TẤT CẢ pixel nằm dưới vùng phủ của kernel
/// đều là trắng (255) thì pixel trung tâm mới giữ trắng; chỉ cần MỘT pixel đen lọt vào vùng kernel
/// là pixel trung tâm bị "ăn" thành đen. Kết quả: vùng trắng thu hẹp lại theo hình dạng của kernel.
/// Là phép toán ĐỐI NGẪU (dual) của Dilate: Dilate làm to vùng foreground, Erode làm nhỏ đi.
/// Thường dùng để: xóa các chấm nhiễu nhỏ (noise) không đủ lớn để "sống sót" qua Erode, hoặc
/// tách 2 vật thể đang dính nhau qua 1 cầu nối mảnh (cầu nối sẽ bị Erode ăn đứt trước khi vật thể chính bị ảnh hưởng).
/// </summary>
[ToolMetadata("Erode", DisplayName = "Erode", Category = "Morphological",
    Description = "Shrink (erode) bright regions in a binary/grayscale image")]
public sealed class ErodeTool : VisionTool
{
    #region 1. Khai báo Port (In/Out)
    private readonly InputPort<IVisionImage> _input;   // Cổng vào: ảnh nhị phân (thường sau Threshold) hoặc ảnh xám
    private readonly OutputPort<IVisionImage> _output; // Cổng ra: ảnh sau khi đã bị bào mòn (vùng trắng nhỏ hơn)
    #endregion

    #region 2. Khai báo Parameter
    private readonly ToolParameter<int> _kernelSize;    // Kích thước nhân (kernel) NxN
    private readonly ToolParameter<MorphShapes> _shape; // Hình dạng nhân: Rect/Cross/Ellipse
    private readonly ToolParameter<int> _iterations;    // Số lần lặp phép Erode
    #endregion

    public ErodeTool()
    {
        // ----- Port -----
        _input = AddInput<IVisionImage>("Image");
        _output = AddOutput<IVisionImage>("Image");

        // ----- Parameter -----
        // KernelSize: kernel càng lớn thì Erode càng mạnh, vật thể co lại nhanh hơn mỗi lần quét
        _kernelSize = AddParameter("KernelSize", 3, "Kernel Size", 1, 99, category: "Morphology", order: 1);

        // Shape: Rect co đều mọi hướng, phù hợp xử lý chung hoặc remove noise nhanh nhất trong 3 hình dạng
        _shape = AddParameter("Shape", MorphShapes.Rect, "Shape", category: "Morphology", order: 2);

        // Iterations: số lần lặp phép Erode, mỗi lần xóa bớt 1 lớp pixel biên của vùng trắng
        _iterations = AddParameter("Iterations", 1, "Iterations", 1, 50, category: "Morphology", order: 3);
    }

    protected override void OnExecute(IToolContext context)
    {
        Mat src = _input.Value!.AsMat(); // Lấy ma trận ảnh gốc từ cổng vào

        // ----- Bước 1: Ép KernelSize về số nguyên dương hợp lệ -----
        int kernelSize = Math.Max(1, _kernelSize.Value);

        // ----- Bước 2: Tạo Structuring Element (nhân cấu trúc) theo hình dạng và kích thước đã chọn -----
        using Mat kernel = Cv2.GetStructuringElement(_shape.Value, new Size(kernelSize, kernelSize));

        // ----- Bước 3: Chạy phép Erode -----
        Mat dst = new Mat(); // Ma trận đích chứa kết quả sau bào mòn
        Cv2.Erode(
            src, dst, kernel,
            anchor: null,                     // null -> tâm kernel mặc định ở chính giữa
            iterations: Math.Max(1, _iterations.Value), // Ép Iterations tối thiểu = 1
            borderType: BorderTypes.Constant,  // Vùng ngoài biên ảnh coi như giá trị cố định
            borderValue: null);                // null -> dùng giá trị biên mặc định

        // ----- Ghi kết quả ra cổng output -----
        _output.Value = new MatVisionImage(dst);

        context.Log($"Erode: kernel={kernelSize}x{kernelSize} ({_shape.Value}), iterations={_iterations.Value}");
    }
}