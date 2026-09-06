// ==================== Vai trò chính:                Tách tâm (Center) và bán kính ra khỏi CircleResult - "cầu nối" kiểu dữ liệu giữa FindCircle và các tool nhận input P2 thuần
// ==================== Thành phần / Class tiêu biểu: CircleCenterTool
// ==================== Phụ thuộc vào:                Core.Models (CircleResult, Point2d)
// ==================== Pattern / Kỹ thuật nổi bật:   Adapter Pattern - tool "chuyển đổi kiểu" đơn giản, không có thuật toán xử lý ảnh

using VisionFlow.Core.Models;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using P2 = VisionFlow.Core.Models.Point2d;

namespace VisionFlow.Tools.Utility;

/// <summary>
/// Tool "cầu nối" (adapter): FindCircleTool trả về CircleResult (kiểu bọc gồm Circle + Judge + RMS...),
/// nhưng nhiều tool khác (Midpoint, PointToLineDistance...) chỉ nhận thẳng kiểu P2 đơn giản làm Input.
/// Tool này tách riêng Center (P2) và Radius (double) ra để nối dây được sang các tool đó.
/// </summary>
[ToolMetadata("CircleCenter", DisplayName = "Circle Center", Category = "Utility",
    Description = "Extract the center point (P2) and radius from a CircleResult")]
public sealed class CircleCenterTool : VisionTool
{
    private readonly InputPort<CircleResult> _input;  // Nhận thẳng output của FindCircleTool (nối dây trực tiếp được)
    private readonly OutputPort<P2> _outCenter;        // Tâm hình tròn, kiểu P2 thuần - nối được vào Point1/Point2 của MidpointTool...
    private readonly OutputPort<double> _outRadius;    // Bán kính, tiện dùng tiếp cho các phép đo khác

    public CircleCenterTool()
    {
        _input = AddInput<CircleResult>("Circle");
        _outCenter = AddOutput<P2>("Center");
        _outRadius = AddOutput<double>("Radius");
    }

    protected override void OnExecute(IToolContext context)
    {
        CircleResult result = _input.Value!;

        // Chỉ đơn giản "mở hộp" - lấy đúng 2 trường ra khỏi cấu trúc CircleResult, không có tính toán gì thêm
        _outCenter.Value = result.Circle.Center;
        _outRadius.Value = result.Circle.Radius;

        context.Log($"CircleCenter: ({result.Circle.Center.X:F1}, {result.Circle.Center.Y:F1}), R={result.Circle.Radius:F1}");
    }
}