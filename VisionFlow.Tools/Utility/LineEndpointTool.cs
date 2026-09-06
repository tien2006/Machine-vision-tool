// ==================== Vai trò chính:                Tách 2 điểm đầu-cuối (P1, P2) và điểm giữa ra khỏi LineResult - "cầu nối" kiểu dữ liệu giữa FindLine và các tool nhận input P2 thuần
// ==================== Thành phần / Class tiêu biểu: LineEndpointTool
// ==================== Phụ thuộc vào:                Core.Models (LineResult, Point2d)
// ==================== Pattern / Kỹ thuật nổi bật:   Adapter Pattern - tương tự CircleCenterTool nhưng cho LineResult

using VisionFlow.Core.Models;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using P2 = VisionFlow.Core.Models.Point2d;

namespace VisionFlow.Tools.Utility;

/// <summary>
/// Tool "cầu nối": tách 2 điểm đầu-cuối của LineResult (output của FindLineTool) thành 2 cổng P2 riêng biệt,
/// để nối được vào các tool chỉ nhận Input kiểu P2 thuần (VD: Point1/Point2 của MidpointTool).
/// </summary>
[ToolMetadata("LineEndpoint", DisplayName = "Line Endpoint", Category = "Utility",
    Description = "Extract P1, P2, and midpoint from a LineResult")]
public sealed class LineEndpointTool : VisionTool
{
    private readonly InputPort<LineResult> _input; // Nhận thẳng output của FindLineTool
    private readonly OutputPort<P2> _outP1;         // Điểm đầu của đoạn thẳng
    private readonly OutputPort<P2> _outP2;         // Điểm cuối của đoạn thẳng
    private readonly OutputPort<P2> _outMidpoint;    // Điểm giữa - tiện dùng ngay không cần thêm MidpointTool riêng

    public LineEndpointTool()
    {
        _input = AddInput<LineResult>("Line");
        _outP1 = AddOutput<P2>("P1");
        _outP2 = AddOutput<P2>("P2");
        _outMidpoint = AddOutput<P2>("Midpoint");
    }

    protected override void OnExecute(IToolContext context)
    {
        LineResult result = _input.Value!;
        P2 p1 = result.Segment.P1, p2 = result.Segment.P2;

        _outP1.Value = p1;
        _outP2.Value = p2;
        _outMidpoint.Value = new P2((p1.X + p2.X) / 2.0, (p1.Y + p2.Y) / 2.0); // Trung điểm tính nhanh, tiện lợi

        context.Log($"LineEndpoint: P1=({p1.X:F1},{p1.Y:F1}), P2=({p2.X:F1},{p2.Y:F1})");
    }
}