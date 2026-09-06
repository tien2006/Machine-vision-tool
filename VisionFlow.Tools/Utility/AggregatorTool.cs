// ==================== Vai trò chính:                Gom (fan-in) nhiều giá trị output từ nhiều tool khác vào 1 chuỗi Summary duy nhất
// ==================== Thành phần / Class tiêu biểu: AggregatorTool
// ==================== Phụ thuộc vào:                Core.Ports (MultiInputPort<T>) + Core.Tools (tool logic thuần, không xử lý ảnh)
// ==================== Pattern / Kỹ thuật nổi bật:   Fan-in node dùng Multi-Input Port THẬT (kéo bao nhiêu dây cũng được) - KHÔNG có tham số, chỉ gom + định dạng

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;

namespace VisionFlow.Tools.Utility;

/// <summary>
/// Gom kết quả của nhiều Tool khác vào 1 chuỗi tóm tắt duy nhất - tiện hiển thị HMI, ghi log, hoặc đẩy ra hệ
/// thống ngoài (báo cáo, database, SCADA). Không có tham số cấu hình, chỉ làm nhiệm vụ gom + định dạng.
///
/// Port "Sources" là MULTI-INPUT PORT THẬT (MultiInputPort&lt;object&gt;) - kéo bao nhiêu dây cũng được, mỗi dây
/// tự động được Engine gom vào Dictionary với key "TênNode.TênCổng" (xem FlowExecutor.FeedInputs đã cập nhật).
/// Yêu cầu: Engine đã áp dụng đủ 4 patch (MultiInputPort.cs mới + FlowGraph.Connect + FlowExecutor.FeedInputs +
/// VisionTool.AddMultiInput) - nếu chưa, Tool này vẫn build được nhưng Sources sẽ luôn rỗng vì Engine chưa biết
/// cách gom nhiều dây vào port loại này.
/// </summary>
[ToolMetadata("Aggregator", DisplayName = "Aggregator", Category = "Utility",
    Description = "Fan-in multiple values from other tools into a single formatted summary string.")]
public sealed class AggregatorTool : VisionTool
{
    private readonly MultiInputPort<object> _sources; // "NodeName.Port" -> giá trị - CHẤP NHẬN NHIỀU DÂY NỐI CÙNG LÚC
    private readonly OutputPort<string> _outSummary; // Summary: mỗi dòng 1 nguồn, định dạng "TênNode.Cổng = GiáTrị"

    public AggregatorTool()
    {
        _sources = AddMultiInput<object>("Sources", "Sources", optional: true);
        _outSummary = AddOutput<string>("Summary", "Summary");
    }

    protected override void OnExecute(IToolContext context)
    {
        var sources = _sources.Values; // IReadOnlyDictionary<string, object> - đã được Engine gom sẵn từ MỌI dây nối vào Sources

        if (sources.Count == 0)
        {
            _outSummary.Value = "";
            context.Log("Aggregator: không có nguồn dữ liệu nào được nối vào Sources.");
            return;
        }

        var sb = new StringBuilder();
        foreach (var kv in sources)
        {
            sb.Append(kv.Key).Append(" = ").Append(FormatValue(kv.Value)).Append('\n');
        }

        string summary = sb.ToString().TrimEnd('\n');
        _outSummary.Value = summary;

        context.Log($"Aggregator: đã gom {sources.Count} nguồn.");
    }

    /// <summary>Format 1 giá trị bất kỳ (số, chuỗi, bool, mảng...) thành chuỗi hiển thị dễ đọc.</summary>
    private static string FormatValue(object? value)
    {
        if (value == null) return "null";
        if (value is string s) return s;
        if (value is System.Collections.IEnumerable enumerable && value is not string)
        {
            var items = enumerable.Cast<object>().Select(FormatValue);
            return "[" + string.Join(", ", items) + "]";
        }
        return value.ToString() ?? "null";
    }
}