// ==================== Vai trò chính:                Ghép lớp kết quả vẽ (InteractiveVisualization) từ tool phân tích phía trước đè lên ảnh màu gốc để hiển thị trực quan
// ==================== Thành phần / Class tiêu biểu: OverlayRendererTool
// ==================== Phụ thuộc vào:                OpenCvSharp (ConnectedComponents, CopyTo với mask) + Core.Ports + Core.Tools
// ==================== Pattern / Kỹ thuật nổi bật:   Chroma-key kiểu "đen = trong suốt" (copy pixel có mask) + đếm item bằng Connected Components trên chính lớp overlay

using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;

namespace VisionFlow.Tools.Utility;

/// <summary>
/// Nhiều tool phân tích (VD BlobAnalysis) phải làm việc trên ảnh nhị phân (đen-trắng, đã qua Threshold) nên
/// ảnh Output của chúng rất khó nhìn cho người vận hành. OverlayRenderer giải quyết vấn đề này: lấy ảnh MÀU GỐC
/// (từ ImageLoader/GrabImageTool, trước khi bị Threshold) làm nền, rồi ghép lớp kết quả vẽ (khung/đường/điểm -
/// InteractiveVisualization của tool phân tích) đè lên trên, cho ra 1 ảnh vừa có màu thật vừa thấy rõ kết quả.
///
/// Kỹ thuật ghép: coi phần PIXEL ĐEN THUẦN (0,0,0) trong lớp InteractiveVisualization là "trong suốt" (không có
/// gì được vẽ ở đó) - chỉ những pixel KHÁC đen (màu của nét vẽ: khung xanh, tâm đỏ...) mới được dán đè lên nền.
/// Đây là kỹ thuật chroma-key đơn giản, không cần lớp Alpha riêng.
///
/// Không có tham số - toàn bộ màu sắc/độ dày nét vẽ đã được quyết định sẵn ở tool phân tích phía trước.
/// </summary>
[ToolMetadata("OverlayRenderer", DisplayName = "Overlay Renderer", Category = "Utility",
    Description = "Composite an analysis tool's visualization layer (boxes, points, lines) onto the original color image for operator display.")]
public sealed class OverlayRendererTool : VisionTool
{
    private readonly InputPort<IVisionImage> _imageMatrix;             // Ảnh nền - LUÔN lấy ảnh màu gốc (ImageLoader/Grab), KHÔNG lấy ảnh nhị phân của tool phân tích
    private readonly InputPort<IVisionImage> _interactiveVisualization; // Lớp kết quả vẽ (khung/đường/điểm) từ tool phân tích phía trước, VD BlobAnalysis.InteractiveVisualization

    private readonly OutputPort<IVisionImage> _outImageMatrix;             // Ảnh đã ghép: nền màu gốc + kết quả vẽ đè lên trên
    private readonly OutputPort<IVisionImage> _outInteractiveVisualization; // Lớp vẽ gốc được giữ nguyên (clone) - tiện nối tiếp vào 1 OverlayRenderer khác nếu cần chồng nhiều lớp
    private readonly OutputPort<int> _outItemCount;                        // Số lượng đối tượng riêng biệt đang được vẽ trong lớp overlay (đếm bằng Connected Components)

    public OverlayRendererTool()
    {
        _imageMatrix = AddInput<IVisionImage>("ImageMatrix", "Image Matrix (Background)");
        _interactiveVisualization = AddInput<IVisionImage>("InteractiveVisualization", "Interactive Visualization (Overlay)");

        _outImageMatrix = AddOutput<IVisionImage>("ImageMatrix", "Image Matrix (Composited)");
        _outInteractiveVisualization = AddOutput<IVisionImage>("InteractiveVisualization", "Interactive Visualization");
        _outItemCount = AddOutput<int>("ItemCount", "Item Count");
    }

    protected override void OnExecute(IToolContext context)
    {
        Mat background = _imageMatrix.Value!.AsMat();
        Mat overlaySource = _interactiveVisualization.Value!.AsMat();

        // ----- Bước 1: Chuẩn hoá cả 2 ảnh về BGR 3 kênh để ghép màu đúng (ảnh nhị phân/xám phải convert trước) -----
        Mat backgroundBgr = ToBgr(background, out bool backgroundOwned);
        Mat overlayBgr = ToBgr(overlaySource, out bool overlayOwned);

        // ----- Bước 2: Nếu 2 ảnh khác kích thước (trường hợp hiếm, VD nhánh xử lý có resize) - resize overlay khớp nền -----
        Mat overlayResized = overlayBgr;
        bool overlayResizedOwned = false;
        if (overlayBgr.Size() != backgroundBgr.Size())
        {
            overlayResized = new Mat();
            Cv2.Resize(overlayBgr, overlayResized, backgroundBgr.Size(), interpolation: InterpolationFlags.Nearest); // Nearest để không làm mờ/lem nét vẽ mảnh (đường kẻ 1px)
            overlayResizedOwned = true;
            context.Log("OverlayRenderer: kích thước InteractiveVisualization khác ImageMatrix - đã tự resize overlay để khớp nền.");
        }

        // ----- Bước 3: Tạo mask "khác đen" từ lớp overlay - pixel đen thuần (0,0,0) coi như trong suốt -----
        using Mat overlayGray = new Mat();
        Cv2.CvtColor(overlayResized, overlayGray, ColorConversionCodes.BGR2GRAY);
        using Mat mask = new Mat();
        Cv2.Threshold(overlayGray, mask, 0, 255, ThresholdTypes.Binary); // Bất kỳ pixel nào > 0 (không phải đen thuần) -> mask = 255

        // ----- Bước 4: Ghép - Clone nền rồi chỉ copy đè đúng những pixel có mask (đúng kỹ thuật chroma-key đen=trong suốt) -----
        Mat composited = backgroundBgr.Clone();
        overlayResized.CopyTo(composited, mask);

        // ----- Bước 5: Đếm số đối tượng riêng biệt trong lớp overlay bằng Connected Components trên mask -----
        int itemCount = Cv2.ConnectedComponents(mask, new Mat()) - 1; // Trừ 1 vì label 0 luôn là nền (background), không tính là "item"
        if (itemCount < 0) itemCount = 0;

        // ----- Bước 6: Xuất kết quả -----
        // composited "cho đi" thẳng vào Output -> KHÔNG Dispose(composited) sau đây
        _outImageMatrix.Value = new MatVisionImage(composited);
        _outInteractiveVisualization.Value = new MatVisionImage(overlayResized.Clone()); // Clone riêng để không chung tham chiếu Mat với overlayResized (biến này có thể bị Dispose bên dưới)
        _outItemCount.Value = itemCount;

        // ----- Bước 7: Dọn dẹp tài nguyên Mat trung gian -----
        if (overlayResizedOwned) overlayResized.Dispose();
        if (backgroundOwned) backgroundBgr.Dispose();
        if (overlayOwned) overlayBgr.Dispose();

        context.Log($"OverlayRenderer: đã ghép overlay lên nền, ItemCount={itemCount}.");
    }

    /// <summary>Convert ảnh bất kỳ (1 kênh xám hoặc 3 kênh màu) về BGR 3 kênh; owned=true nếu hàm này tự tạo Mat mới (cần Dispose sau khi dùng xong).</summary>
    private static Mat ToBgr(Mat src, out bool owned)
    {
        if (src.Channels() == 3) { owned = false; return src; }
        Mat bgr = new Mat();
        Cv2.CvtColor(src, bgr, ColorConversionCodes.GRAY2BGR);
        owned = true;
        return bgr;
    }
}