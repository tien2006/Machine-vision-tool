// ==================== Vai trò chính:                Lọc pixel theo dải giá trị trên 3 kênh màu đồng thời -> tạo mask nhị phân theo màu sắc
// ==================== Thành phần / Class tiêu biểu: InRangeTool
// ==================== Phụ thuộc vào:                OpenCvSharp
// ==================== Pattern / Kỹ thuật nổi bật:   Wrapper của Cv2.InRange, tự động chuyển không gian màu (Auto/BGR/HSV/HLS/LAB/Gray) trước khi lọc

using System;
using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;

namespace VisionFlow.Tools.Preprocess; // Cùng nhóm "Filtering" với Threshold/OtsuThreshold/AdaptiveThreshold

/// <summary>
/// Tool lọc pixel theo dải 3 kênh - khác với Threshold (chỉ 1 ngưỡng trên 1 kênh xám),
/// InRange hoạt động trên 3 kênh đồng thời -> phù hợp khi cần lọc theo MÀU (BGR/HSV/LAB range),
/// không chỉ độ sáng. Pixel được coi là "khớp" (trắng trong mask) khi CẢ 3 kênh đều nằm trong
/// khoảng [Lower, Upper] tương ứng của nó.
/// Ví dụ điển hình (lọc vùng da người): ColorSpace=HSV, Lower=(0,30,60), Upper=(20,150,255).
/// </summary>
[ToolMetadata("InRange", DisplayName = "In Range", Category = "Filtering",
    Description = "Filter pixels within a 3-channel color range (BGR/HSV/HLS/LAB/Gray)")]
public sealed class InRangeTool : VisionTool
{
    #region 1. Khai báo Port (In/Out)
    private readonly InputPort<IVisionImage> _input;          // Cổng vào: ảnh gốc (xám hoặc màu BGR)
    private readonly OutputPort<IVisionImage> _outMask;       // Cổng ra: mask nhị phân (1 kênh, trắng = trong range, đen = ngoài)
    private readonly OutputPort<IVisionImage> _outMaskedImage; // Cổng ra: ảnh gốc với pixel ngoài range bị che đen
    private readonly OutputPort<int> _outPixelCount;          // Cổng ra: số pixel khớp range
    private readonly OutputPort<double> _outCoverage;         // Cổng ra: % diện tích pixel khớp / tổng pixel ảnh
    #endregion

    #region 2. Khai báo Parameter theo từng Tab
    // ----- Tab Color Space -----
    private readonly ToolParameter<string> _colorSpace;

    // ----- Tab Lower Bound -----
    private readonly ToolParameter<double> _lower1;
    private readonly ToolParameter<double> _lower2;
    private readonly ToolParameter<double> _lower3;

    // ----- Tab Upper Bound -----
    private readonly ToolParameter<double> _upper1;
    private readonly ToolParameter<double> _upper2;
    private readonly ToolParameter<double> _upper3;

    // ----- Tab Output -----
    private readonly ToolParameter<bool> _invertMask;
    #endregion

    public InRangeTool()
    {
        // ----- Port -----
        _input = AddInput<IVisionImage>("Image");
        _outMask = AddOutput<IVisionImage>("Image");
        _outMaskedImage = AddOutput<IVisionImage>("MaskedImage");
        _outPixelCount = AddOutput<int>("PixelCount");
        _outCoverage = AddOutput<double>("Coverage");

        // ----- Tab Color Space -----
        _colorSpace = AddChoiceParameter(
            "ColorSpace", "Auto",
            choices: new[] { "Auto", "BGR", "HSV", "HLS", "LAB", "Gray" },
            displayName: "Color Space", category: "Color Space", order: 1);

        // ----- Tab Lower Bound (mặc định 0,0,0 = không giới hạn cận dưới) -----
        _lower1 = AddParameter("LowerChannel1", 0.0, "Lower Channel 1", 0.0, 255.0, category: "Lower Bound", order: 1);
        _lower2 = AddParameter("LowerChannel2", 0.0, "Lower Channel 2", 0.0, 255.0, category: "Lower Bound", order: 2);
        _lower3 = AddParameter("LowerChannel3", 0.0, "Lower Channel 3", 0.0, 255.0, category: "Lower Bound", order: 3);

        // ----- Tab Upper Bound (mặc định 255,255,255 = không giới hạn cận trên) -----
        _upper1 = AddParameter("UpperChannel1", 255.0, "Upper Channel 1", 0.0, 255.0, category: "Upper Bound", order: 1);
        _upper2 = AddParameter("UpperChannel2", 255.0, "Upper Channel 2", 0.0, 255.0, category: "Upper Bound", order: 2);
        _upper3 = AddParameter("UpperChannel3", 255.0, "Upper Channel 3", 0.0, 255.0, category: "Upper Bound", order: 3);

        // ----- Tab Output -----
        _invertMask = AddParameter("InvertMask", false, "Invert Mask", category: "Output", order: 1);
    }

    protected override void OnExecute(IToolContext context)
    {
        Mat src = _input.Value!.AsMat(); // Lấy ma trận ảnh gốc từ cổng vào

        // ----- Bước 1: Xác định không gian màu thực sự sẽ dùng -----
        // Auto: ảnh 1 kênh -> coi như Gray; ảnh 3 kênh -> coi như BGR (không convert gì cả)
        string space = _colorSpace.Value;
        if (space == "Auto")
        {
            space = src.Channels() == 1 ? "Gray" : "BGR";
        }

        // ----- Bước 2: Chuyển ảnh sang đúng không gian màu đã chọn -----
        // "working" là ảnh thực sự đem đi InRange; "workingIsTemporary" đánh dấu có cần Dispose hay không
        Mat working;
        bool workingIsTemporary = false;

        // Nếu ảnh gốc đang là ảnh xám (1 kênh) nhưng người dùng chọn không gian màu cần 3 kênh (BGR/HSV/HLS/LAB)
        // thì phải "bơm" ảnh xám thành ảnh màu giả (3 kênh B=G=R bằng nhau) trước khi convert tiếp
        Mat src3Channel = src;
        bool src3ChannelIsTemporary = false;
        if (src.Channels() == 1 && space != "Gray")
        {
            src3Channel = new Mat();
            Cv2.CvtColor(src, src3Channel, ColorConversionCodes.GRAY2BGR);
            src3ChannelIsTemporary = true;
        }

        switch (space)
        {
            case "BGR":
                working = src3Channel; // BGR là không gian màu gốc của OpenCV -> dùng thẳng, không cần convert
                break;
            case "HSV":
                working = new Mat();
                Cv2.CvtColor(src3Channel, working, ColorConversionCodes.BGR2HSV); // Channel1=Hue(0-179), Channel2=Saturation, Channel3=Value
                workingIsTemporary = true;
                break;
            case "HLS":
                working = new Mat();
                Cv2.CvtColor(src3Channel, working, ColorConversionCodes.BGR2HLS); // Channel1=Hue, Channel2=Lightness, Channel3=Saturation
                workingIsTemporary = true;
                break;
            case "LAB":
                working = new Mat();
                Cv2.CvtColor(src3Channel, working, ColorConversionCodes.BGR2Lab); // Channel1=L, Channel2=a, Channel3=b
                workingIsTemporary = true;
                break;
            case "Gray":
                if (src.Channels() == 1)
                {
                    working = src; // Đã là ảnh xám sẵn -> dùng thẳng
                }
                else
                {
                    working = new Mat();
                    Cv2.CvtColor(src, working, ColorConversionCodes.BGR2GRAY);
                    workingIsTemporary = true;
                }
                break;
            default:
                throw new ToolExecutionException($"InRangeTool: unknown ColorSpace '{space}'.");
        }

        // ----- Bước 3: Xây dựng Scalar Lower/Upper theo đúng số kênh của "working" -----
        // Với Gray (1 kênh) chỉ dùng Channel1, KHÔNG được truyền đủ 3 giá trị kẻo OpenCV đọc sai
        Scalar lower = space == "Gray" ? new Scalar(_lower1.Value) : new Scalar(_lower1.Value, _lower2.Value, _lower3.Value);
        Scalar upper = space == "Gray" ? new Scalar(_upper1.Value) : new Scalar(_upper1.Value, _upper2.Value, _upper3.Value);

        // ----- Bước 4: Chạy thuật toán lọc dải -----
        // Pixel pass khi TẤT CẢ các kênh đều thỏa: Lower_i <= channel_i <= Upper_i -> gán 255 (trắng), ngược lại 0 (đen)
        Mat mask = new Mat();
        Cv2.InRange(working, lower, upper, mask);

        // ----- Bước 5 (tùy chọn): Đảo mask - hữu ích để lọc NGƯỢC (giữ vùng KHÔNG khớp) -----
        if (_invertMask.Value)
        {
            Cv2.BitwiseNot(mask, mask);
        }

        // ----- Bước 6: Tính PixelCount và Coverage -----
        int pixelCount = Cv2.CountNonZero(mask); // Đếm số pixel khác 0 (tức pixel trắng = pixel khớp)
        double coverage = (double)pixelCount / (mask.Rows * mask.Cols) * 100.0; // Quy đổi sang phần trăm diện tích

        // ----- Bước 7: Tạo MaskedImage = ảnh gốc, chỉ giữ pixel trong vùng mask, còn lại che đen -----
        Mat maskedImage = Mat.Zeros(src.Size(), src.Type()); // Tạo nền đen cùng kích thước/kiểu dữ liệu với ảnh gốc
        src.CopyTo(maskedImage, mask); // CopyTo có mask: chỉ copy pixel tại vị trí mask != 0, nơi khác giữ nguyên màu đen đã khởi tạo

        // ----- Dọn dẹp bộ nhớ unmanaged của các ảnh tạm đã tạo ra -----
        if (workingIsTemporary) working.Dispose();
        if (src3ChannelIsTemporary) src3Channel.Dispose();

        // ----- Ghi kết quả ra các cổng output -----
        _outMask.Value = new MatVisionImage(mask);
        _outMaskedImage.Value = new MatVisionImage(maskedImage);
        _outPixelCount.Value = pixelCount;
        _outCoverage.Value = coverage;

        context.Log($"InRange [{space}]: {pixelCount} px matched ({coverage:F1}% coverage)");
    }
}