// ==================== Vai trò chính:                Tạo 1 điểm tọa độ (X,Y) nhập tay - dùng để test các tool hình học mà không cần dựng pipeline detection thật
// ==================== Thành phần / Class tiêu biểu: PointConstantTool
// ==================== Phụ thuộc vào:                Core.Models (Point2d)
// ==================== Pattern / Kỹ thuật nổi bật:   Tool "nguồn" (source) thuần tham số, không có Input - giống vai trò 1 hằng số trong lập trình

using VisionFlow.Core.Models;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using P2 = VisionFlow.Core.Models.Point2d;

namespace VisionFlow.Tools.Utility;

/// <summary>
/// Tool "hằng số điểm": không có Input, chỉ có 2 tham số X/Y để bạn tự nhập tọa độ mong muốn.
/// Dùng chính để TEST NHANH các tool cần đầu vào là điểm (Midpoint, PointToLineDistance...) mà
/// không cần dựng cả pipeline GrabImage -> Threshold -> FindCircle chỉ để lấy 1 điểm. Trong thực tế
/// production cũng hữu ích khi cần 1 điểm tham chiếu CỐ ĐỊNH đã biết trước (VD: gốc tọa độ máy, vị trí home).
/// </summary>
[ToolMetadata("PointConstant", DisplayName = "Point Constant", Category = "Utility",
    Description = "Output a fixed (X,Y) point entered manually - useful for testing geometry tools")]
public sealed class PointConstantTool : VisionTool
{
    private readonly OutputPort<P2> _outPoint; // Cổng ra duy nhất: điểm tọa độ theo đúng tham số đã nhập

    // ----- Tab Point -----
    private readonly ToolParameter<double> _x;
    private readonly ToolParameter<double> _y;

    public PointConstantTool()
    {
        // Không có AddInput nào cả -> đây là tool "nguồn" (source node), luôn chạy được đầu tiên trong flow
        _outPoint = AddOutput<P2>("Point");

        _x = AddParameter("X", 0.0, "X", -100_000.0, 100_000.0, category: "Point", order: 1);
        _y = AddParameter("Y", 0.0, "Y", -100_000.0, 100_000.0, category: "Point", order: 2);
    }

    protected override void OnExecute(IToolContext context)
    {
        // Không cần xử lý ảnh, không cần thuật toán - chỉ đơn giản đóng gói 2 tham số X/Y thành 1 điểm P2
        var point = new P2(_x.Value, _y.Value);
        _outPoint.Value = point;

        context.Log($"PointConstant: ({point.X:F1}, {point.Y:F1})");
    }
}