// ==================== Vai trò chính:                Node cuối flow dành cho tích hợp máy: gom phán quyết OK/NG + các số đo có tên để InspectionService gửi cho PLC / robot / log
// ==================== Thành phần / Class tiêu biểu: ResultPublisherTool
// ==================== Phụ thuộc vào:                Core.Tools (VisionTool, IInspectionResultSource), Core.Ports, Core.Models (Judge)
// ==================== Pattern / Kỹ thuật nổi bật:   Terminal Node + Snapshot bất biến (record) — Engine đọc kết quả qua interface, không phụ thuộc Tool cụ thể
//
// VỊ TRÍ ĐẶT FILE: project Tools, thư mục Output/ (cùng nơi với OutputTool.cs, DataSaverTool.cs) — namespace VisionFlow.Tools.Output.
// Không cần sửa App.xaml.cs: ToolRegistry tự quét [ToolMetadata] nên tool sẽ tự xuất hiện trong danh sách bên trái (nhóm "OutputSource").

using System;
using System.Collections.Generic;
using System.Globalization;
using VisionFlow.Core.Models;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;

namespace VisionFlow.Tools.Output;

/// <summary>
/// Điểm cuối của flow khi chạy trên máy: nhận phán quyết (từ Compare / Logic Gate) và tối đa 6 số đo,
/// rồi "công bố" thành <see cref="InspectionSnapshot"/> để <c>InspectionService</c> lấy ra gửi PLC.
/// <para>
/// Khác <c>OutputTool</c> hiện có: OutputTool chỉ dùng để xem ảnh + phán quyết vòng tròn trên editor,
/// còn tool này là hợp đồng dữ liệu giữa VisionFlow và phần còn lại của máy.
/// </para>
/// </summary>
[ToolMetadata("ResultPublisher", DisplayName = "Result Publisher", Category = "OutputSource",
    Description = "Flow endpoint for machine integration: collects the OK/NG judgment and up to 6 named numeric measurements for InspectionService (PLC / robot / log).")]
public sealed class ResultPublisherTool : VisionTool, IInspectionResultSource
{
    /// <summary>Số cổng số đo. Muốn nhiều hơn chỉ cần tăng hằng số này (flow cũ vẫn nạp được vì tham số thiếu sẽ bị bỏ qua).</summary>
    public const int SlotCount = 6;

    private readonly InputPort<object> _pass;                 // Phán quyết: bool (Compare/LogicGate), "OK"/"NG", số 0/1, hoặc Judge
    private readonly InputPort<object>[] _values = new InputPort<object>[SlotCount];        // Các số đo (mọi kiểu số), không bắt buộc nối
    private readonly ToolParameter<string>[] _names = new ToolParameter<string>[SlotCount]; // Tên hiển thị/log của từng số đo
    private readonly ToolParameter<bool> _okWhenTrue;         // Đảo nghĩa phán quyết khi cần

    // volatile: InspectionService đọc từ thread khác với thread chạy flow
    private volatile InspectionSnapshot? _snapshot;

    public InspectionSnapshot? Snapshot => _snapshot;
    public void ResetSnapshot() => _snapshot = null;

    public ResultPublisherTool()
    {
        // Dùng kiểu object (không dùng bool) cho cổng Pass vì lý do an toàn: với kiểu giá trị như bool, cổng chưa nối dây
        // vẫn mang giá trị mặc định false (không phải null) nên Engine KHÔNG phát hiện được "quên nối dây" và sẽ lặng lẽ ra NG.
        // Với object, cổng chưa nối = null => VisionTool.ValidateInputs() báo lỗi rõ ràng.
        _pass = AddInput<object>("Pass", "Pass (nối từ Compare / Logic Gate)");

        for (int i = 0; i < SlotCount; i++)
        {
            int slot = i + 1;
            _values[i] = AddInput<object>($"Value{slot}", $"Value {slot}", optional: true);
            _names[i] = AddParameter($"Name{slot}", $"Value{slot}", $"Name {slot}",
                category: "Measurement names", order: slot);
        }

        _okWhenTrue = AddParameter("OkWhenTrue", true, "OK when Pass = true",
            category: "Judgment", order: 1);
    }

    protected override void OnExecute(IToolContext context)
    {
        // 1. Phán quyết
        bool pass = ToBool(_pass.Value!);
        bool isOk = pass == _okWhenTrue.Value;

        // 2. Gom số đo: cổng nào không nối thì bỏ qua (slot vẫn giữ nguyên số thứ tự cho PLC)
        var values = new List<MeasuredValue>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < SlotCount; i++)
        {
            object? raw = _values[i].Value;
            if (raw is null) continue;

            string name = string.IsNullOrWhiteSpace(_names[i].Value) ? $"Value{i + 1}" : _names[i].Value.Trim();
            if (!usedNames.Add(name))
                throw new ToolExecutionException($"Result Publisher: tên số đo '{name}' bị trùng. Mỗi số đo phải có tên riêng (tham số Name {i + 1}).");

            values.Add(new MeasuredValue(i + 1, name, ToDouble(raw, name)));
        }

        // 3. Công bố kết quả (gán 1 lần, bất biến)
        _snapshot = new InspectionSnapshot(isOk, values);
        context.Log($"ResultPublisher: {(isOk ? "OK" : "NG")} ({values.Count} số đo)");
    }

    // ====================================================================
    // CHUYỂN KIỂU
    // ====================================================================

    /// <summary>Đổi giá trị bất kỳ từ cổng Pass sang bool; kiểu lạ thì báo lỗi rõ ràng thay vì đoán bừa.</summary>
    private static bool ToBool(object raw)
    {
        switch (raw)
        {
            case bool b:
                return b;

            case Judge j:
                if (j == Judge.None)
                    throw new ToolExecutionException("Result Publisher: cổng Pass nhận Judge.None (chưa có kết quả kiểm tra).");
                return j == Judge.OK;

            case string s:
                switch (s.Trim().ToUpperInvariant())
                {
                    case "OK": case "PASS": case "GOOD": case "TRUE": case "1": return true;
                    case "NG": case "FAIL": case "BAD": case "FALSE": case "0": return false;
                    default:
                        throw new ToolExecutionException($"Result Publisher: cổng Pass nhận chuỗi '{s}' không hiểu được (chấp nhận OK/NG, PASS/FAIL, TRUE/FALSE, 1/0).");
                }

            case sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal:
                return Convert.ToDouble(raw, CultureInfo.InvariantCulture) != 0.0;

            default:
                throw new ToolExecutionException($"Result Publisher: cổng Pass không nhận kiểu {raw.GetType().Name}. Hãy nối từ cổng Result (bool) của Compare hoặc Logic Gate.");
        }
    }

    /// <summary>Đổi số đo sang double (PLC chỉ nhận số hữu hạn: NaN/Infinity bị từ chối).</summary>
    private static double ToDouble(object raw, string name)
    {
        double value = raw switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            short s => s,
            byte b => b,
            uint ui => ui,
            ulong ul => ul,
            decimal m => (double)m,
            bool bo => bo ? 1.0 : 0.0,
            string str when double.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => throw new ToolExecutionException($"Result Publisher: số đo '{name}' có kiểu {raw.GetType().Name} không phải số.")
        };

        if (!double.IsFinite(value))
            throw new ToolExecutionException($"Result Publisher: số đo '{name}' = {value} không hợp lệ (NaN/Infinity), PLC không nhận được.");

        return value;
    }
}