// ==================== Vai trò chính:                Bộ Tool "PC Control" — cho phép Flow Vision hiện tại (Tool Graph) trực tiếp
//                                                     đọc/ghi PLC Mitsubishi (Q04UDEHCPU thật hoặc SimulatedPlc) NGAY TRONG luồng xử lý,
//                                                     ví dụ: bật xy-lanh sau khi Judge OK, chờ cảm biến "đã kẹp phôi" trước khi chụp,
//                                                     ghi kết quả đo xuống thanh ghi D cho PLC/HMI đọc, đọc số hiệu Recipe do PLC chọn...
// ==================== Thành phần / Class tiêu biểu: PlcWriteBitTool, PlcReadBitTool, PlcWaitBitTool, PlcWriteWordTool, PlcReadWordTool
// ==================== Phụ thuộc vào:                VisionFlow.Hardware.Plc (IPlcLink, PlcAddress, PlcHub — MỚI THÊM), VisionFlow.Core.Tools
// ==================== Pattern / Kỹ thuật nổi bật:   Giống hệt pattern LiveCameraSourceTool (Service Locator qua Hub tĩnh) — KHÔNG phát minh
//                                                     cách làm mới, chỉ áp dụng lại đúng khuôn đã có cho phần cứng PLC thay vì Camera.
//
// VỊ TRÍ ĐẶT FILE: project VisionFlow.Tools, thư mục MỚI PCControl/ — namespace VisionFlow.Tools.PCControl.
// KHÔNG cần sửa ToolRegistry/App.xaml.cs để các Tool này xuất hiện trên Palette: ToolRegistry.RegisterAssembly() quét
// theo Assembly, các Tool này nằm cùng project VisionFlow.Tools.dll với các Tool khác nên tự động được tìm thấy
// và hiện trong nhóm "PC Control" trên NodeListView (giống hệt ghi chú đã có ở file ONNX Classifier).
//
// LƯU Ý QUAN TRỌNG VỀ SYNC-OVER-ASYNC:
//   ITool.Execute (qua VisionTool.OnExecute) là hàm ĐỒNG BỘ (void), trong khi IPlcLink là API bất đồng bộ (Task).
//   FlowExecutor chạy tuần tự từng Tool trên 1 luồng nền (không phải UI thread), nên gọi .GetAwaiter().GetResult()
//   ở đây AN TOÀN (không có nguy cơ deadlock kiểu gọi Task đồng bộ trên UI thread của WPF).

using System;
using System.Threading;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Hardware.Plc;

namespace VisionFlow.Tools.PCControl;

// ============================================================================================
// TOOL 1: GHI 1 BIT (Y/M/L/B) — bật/tắt xy-lanh, đèn báo, còi, relay...
// ============================================================================================
[ToolMetadata("PlcWriteBit", DisplayName = "PLC Write Bit", Category = "PC Control",
    Description = "Ghi ON/OFF vào 1 địa chỉ bit PLC (Y/M/L/B). Có thể lấy giá trị từ Input (vd OK/NG của CompareTool) " +
                  "hoặc dùng cố định Parameter. Hỗ trợ chế độ Pulse (bật rồi tự tắt sau N ms) cho xy-lanh/relay xung.")]
public sealed class PlcWriteBitTool : VisionTool
{
    private readonly ToolParameter<string> _address;      // Ví dụ "Y10", "M100"
    private readonly ToolParameter<bool> _fixedValue;      // Dùng khi KHÔNG nối dây vào Input "Value"
    private readonly ToolParameter<bool> _pulseMode;        // true = bật rồi tự tắt lại sau PulseMs (xung), false = giữ nguyên trạng thái
    private readonly ToolParameter<int> _pulseMs;
    private readonly InputPort<bool> _valueInput;           // Optional: nối từ CompareTool/LogicGate để bật/tắt theo kết quả Judge

    public PlcWriteBitTool()
    {
        _address = AddParameter("Address", "Y10", "Địa chỉ Bit (vd Y10, M100)", category: "PLC", order: 1);
        _fixedValue = AddParameter("FixedValue", false, "Giá trị ghi (khi không nối Input)", category: "PLC", order: 2);
        _pulseMode = AddParameter("PulseMode", false, "Chế độ xung (Pulse)", category: "PLC", order: 3);
        _pulseMs = AddParameter("PulseMs", 200, "Độ rộng xung (ms)", min: 10, max: 60_000, category: "PLC", order: 4);

        _valueInput = AddInput<bool>("Value", "Giá trị (tuỳ chọn)", optional: true);
    }

    protected override void OnExecute(IToolContext context)
    {
        if (!PlcHub.IsAvailable)
            throw new ToolExecutionException("PLC Write Bit: chưa có đường truyền PLC (PlcHub chưa Register hoặc appsettings.json sai).");

        if (!PlcAddress.TryParse(_address.Value, out var address))
            throw new ToolExecutionException($"PLC Write Bit: địa chỉ '{_address.Value}' không hợp lệ (vd Y10, M100).");
        if (address.Unit != PlcDeviceUnit.Bit)
            throw new ToolExecutionException($"PLC Write Bit: '{_address.Value}' không phải thiết bị BIT (chỉ dùng M/L/B/X/Y).");

        bool value = _valueInput.Value;
        var link = PlcHub.Link!;

        try
        {
            link.WriteBitAsync(address, value, context.CancellationToken).GetAwaiter().GetResult();
            context.Log($"PLC Write Bit: {address} = {(value ? "ON" : "OFF")}");

            if (_pulseMode.Value)
            {
                // Giữ nguyên PulseMs rồi tự ghi lại giá trị đảo ngược — dùng cho xy-lanh/relay chỉ cần 1 xung ngắn.
                Thread.Sleep(Math.Max(0, _pulseMs.Value));
                context.CancellationToken.ThrowIfCancellationRequested();
                link.WriteBitAsync(address, !value, context.CancellationToken).GetAwaiter().GetResult();
                context.Log($"PLC Write Bit (pulse trả về): {address} = {(!value ? "ON" : "OFF")}");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (PlcLinkException ex)
        {
            throw new ToolExecutionException($"PLC Write Bit: lỗi giao tiếp PLC — {ex.Message}", ex);
        }
    }
}

// ============================================================================================
// TOOL 2: ĐỌC 1 BIT (X/M/L/B) — đọc cảm biến, công tắc hành trình, nút nhấn...
// ============================================================================================
[ToolMetadata("PlcReadBit", DisplayName = "PLC Read Bit", Category = "PC Control",
    Description = "Đọc 1 địa chỉ bit PLC (X/M/L/B) và xuất ra Output bool để nối vào LogicGate/CompareTool phía sau.")]
public sealed class PlcReadBitTool : VisionTool
{
    private readonly ToolParameter<string> _address; // Ví dụ "X1F", "M50"
    private readonly OutputPort<bool> _output;

    public PlcReadBitTool()
    {
        _address = AddParameter("Address", "X0", "Địa chỉ Bit (vd X1F, M50)", category: "PLC", order: 1);
        _output = AddOutput<bool>("Value", "Giá trị Bit");
    }

    protected override void OnExecute(IToolContext context)
    {
        if (!PlcHub.IsAvailable)
            throw new ToolExecutionException("PLC Read Bit: chưa có đường truyền PLC (PlcHub chưa Register hoặc appsettings.json sai).");

        if (!PlcAddress.TryParse(_address.Value, out var address))
            throw new ToolExecutionException($"PLC Read Bit: địa chỉ '{_address.Value}' không hợp lệ (vd X1F, M50).");
        if (address.Unit != PlcDeviceUnit.Bit)
            throw new ToolExecutionException($"PLC Read Bit: '{_address.Value}' không phải thiết bị BIT (chỉ dùng M/L/B/X/Y).");

        try
        {
            bool value = PlcHub.Link!.ReadBitAsync(address, context.CancellationToken).GetAwaiter().GetResult();
            _output.Value = value;
            context.Log($"PLC Read Bit: {address} = {(value ? "ON" : "OFF")}");
        }
        catch (OperationCanceledException) { throw; }
        catch (PlcLinkException ex)
        {
            throw new ToolExecutionException($"PLC Read Bit: lỗi giao tiếp PLC — {ex.Message}", ex);
        }
    }
}

// ============================================================================================
// TOOL 3: CHỜ 1 BIT ĐẠT TRẠNG THÁI MONG MUỐN (vd "chờ kẹp phôi xong" trước khi Camera Source chụp)
// ============================================================================================
[ToolMetadata("PlcWaitBit", DisplayName = "PLC Wait Bit", Category = "PC Control",
    Description = "Chặn Flow lại (poll định kỳ), chờ 1 bit PLC đạt đúng TargetValue trong TimeoutMs. " +
                  "Dùng để đồng bộ máy-vision, vd: chờ 'đã kẹp phôi' = ON rồi mới cho FindCircle chạy tiếp.")]
public sealed class PlcWaitBitTool : VisionTool
{
    private readonly ToolParameter<string> _address;
    private readonly ToolParameter<bool> _targetValue;
    private readonly ToolParameter<int> _timeoutMs;
    private readonly ToolParameter<int> _pollIntervalMs;
    private readonly ToolParameter<bool> _failOnTimeout;   // true = ném lỗi (Node Failed); false = trả Success=false, Flow chạy tiếp
    private readonly OutputPort<bool> _success;

    public PlcWaitBitTool()
    {
        _address = AddParameter("Address", "M60", "Địa chỉ Bit cần chờ (vd M60, X10)", category: "PLC", order: 1);
        _targetValue = AddParameter("TargetValue", true, "Giá trị chờ đạt được", category: "PLC", order: 2);
        _timeoutMs = AddParameter("TimeoutMs", 5000, "Thời gian chờ tối đa (ms)", min: 50, max: 300_000, category: "PLC", order: 3);
        _pollIntervalMs = AddParameter("PollIntervalMs", 20, "Chu kỳ hỏi lại PLC (ms)", min: 5, max: 5000, category: "PLC", order: 4);
        _failOnTimeout = AddParameter("FailOnTimeout", true, "Hết giờ thì báo lỗi Node (Failed)", category: "PLC", order: 5);

        _success = AddOutput<bool>("Success", "Đã đạt trạng thái?");
    }

    protected override void OnExecute(IToolContext context)
    {
        if (!PlcHub.IsAvailable)
            throw new ToolExecutionException("PLC Wait Bit: chưa có đường truyền PLC (PlcHub chưa Register hoặc appsettings.json sai).");

        if (!PlcAddress.TryParse(_address.Value, out var address))
            throw new ToolExecutionException($"PLC Wait Bit: địa chỉ '{_address.Value}' không hợp lệ.");
        if (address.Unit != PlcDeviceUnit.Bit)
            throw new ToolExecutionException($"PLC Wait Bit: '{_address.Value}' không phải thiết bị BIT.");

        var link = PlcHub.Link!;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            while (true)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                bool current = link.ReadBitAsync(address, context.CancellationToken).GetAwaiter().GetResult();
                if (current == _targetValue.Value)
                {
                    _success.Value = true;
                    context.Log($"PLC Wait Bit: {address} đã đạt {(_targetValue.Value ? "ON" : "OFF")} sau {sw.ElapsedMilliseconds} ms.");
                    return;
                }

                if (sw.ElapsedMilliseconds >= _timeoutMs.Value)
                {
                    _success.Value = false;
                    string msg = $"PLC Wait Bit: hết thời gian chờ {_timeoutMs.Value} ms — {address} chưa đạt " +
                                 $"{(_targetValue.Value ? "ON" : "OFF")}.";
                    if (_failOnTimeout.Value) throw new ToolExecutionException(msg);
                    context.Log(msg + " (đã bỏ qua vì FailOnTimeout = false)");
                    return;
                }

                Thread.Sleep(Math.Max(1, _pollIntervalMs.Value));
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (PlcLinkException ex)
        {
            throw new ToolExecutionException($"PLC Wait Bit: lỗi giao tiếp PLC — {ex.Message}", ex);
        }
    }
}

// ============================================================================================
// TOOL 4: GHI 1 WORD/REGISTER (D/W/R/ZR) — ghi kết quả đo, chọn số Recipe, set setpoint...
// ============================================================================================
[ToolMetadata("PlcWriteWord", DisplayName = "PLC Write Word", Category = "PC Control",
    Description = "Ghi 1 giá trị số vào thanh ghi PLC (D/W/R/ZR). Giá trị thực (double, vd 12.503mm) được nhân với " +
                  "Scale rồi làm tròn thành số nguyên 16-bit trước khi ghi (cùng quy ước ValueScale của PlcHandshakeHost).")]
public sealed class PlcWriteWordTool : VisionTool
{
    private readonly ToolParameter<string> _address;
    private readonly ToolParameter<double> _fixedValue;
    private readonly ToolParameter<double> _scale;
    private readonly InputPort<double> _valueInput; // Optional: nối từ Measurement Tool (vd Diameter, Distance)

    public PlcWriteWordTool()
    {
        _address = AddParameter("Address", "D1020", "Địa chỉ Word (vd D1020)", category: "PLC", order: 1);
        _fixedValue = AddParameter("FixedValue", 0.0, "Giá trị ghi (khi không nối Input)", category: "PLC", order: 2);
        _scale = AddParameter("Scale", 1000.0, "Hệ số nhân trước khi ghi (vd 1000 = giữ 3 số lẻ)", category: "PLC", order: 3);

        _valueInput = AddInput<double>("Value", "Giá trị (tuỳ chọn)", optional: true);
    }

    protected override void OnExecute(IToolContext context)
    {
        if (!PlcHub.IsAvailable)
            throw new ToolExecutionException("PLC Write Word: chưa có đường truyền PLC (PlcHub chưa Register hoặc appsettings.json sai).");

        if (!PlcAddress.TryParse(_address.Value, out var address))
            throw new ToolExecutionException($"PLC Write Word: địa chỉ '{_address.Value}' không hợp lệ (vd D1020).");
        if (address.Unit != PlcDeviceUnit.Word)
            throw new ToolExecutionException($"PLC Write Word: '{_address.Value}' không phải thiết bị WORD (chỉ dùng D/W/R/ZR).");

        double real = _valueInput.Value;
        double scaled = real * _scale.Value;
        if (scaled < short.MinValue || scaled > ushort.MaxValue)
            throw new ToolExecutionException($"PLC Write Word: giá trị sau khi nhân Scale ({scaled}) vượt phạm vi 16-bit.");

        ushort raw = unchecked((ushort)(short)Math.Round(scaled, MidpointRounding.AwayFromZero));

        try
        {
            PlcHub.Link!.WriteWordAsync(address, raw, context.CancellationToken).GetAwaiter().GetResult();
            context.Log($"PLC Write Word: {address} = {raw} (giá trị thực {real} × scale {_scale.Value})");
        }
        catch (OperationCanceledException) { throw; }
        catch (PlcLinkException ex)
        {
            throw new ToolExecutionException($"PLC Write Word: lỗi giao tiếp PLC — {ex.Message}", ex);
        }
    }
}

// ============================================================================================
// TOOL 5: ĐỌC 1 WORD/REGISTER (D/W/R/ZR) — đọc số hiệu Recipe, setpoint nhiệt độ, giá trị cảm biến analog...
// ============================================================================================
[ToolMetadata("PlcReadWord", DisplayName = "PLC Read Word", Category = "PC Control",
    Description = "Đọc 1 thanh ghi PLC (D/W/R/ZR), chia cho Scale để ra giá trị thực (double), xuất ra Output " +
                  "để nối vào Parameter/Compare phía sau (vd chọn ngưỡng theo số Recipe do PLC set).")]
public sealed class PlcReadWordTool : VisionTool
{
    private readonly ToolParameter<string> _address;
    private readonly ToolParameter<double> _scale;
    private readonly OutputPort<double> _output;

    public PlcReadWordTool()
    {
        _address = AddParameter("Address", "D1000", "Địa chỉ Word (vd D1000)", category: "PLC", order: 1);
        _scale = AddParameter("Scale", 1000.0, "Hệ số chia sau khi đọc (cùng quy ước với PLC Write Word)", category: "PLC", order: 2);
        _output = AddOutput<double>("Value", "Giá trị thực");
    }

    protected override void OnExecute(IToolContext context)
    {
        if (!PlcHub.IsAvailable)
            throw new ToolExecutionException("PLC Read Word: chưa có đường truyền PLC (PlcHub chưa Register hoặc appsettings.json sai).");

        if (!PlcAddress.TryParse(_address.Value, out var address))
            throw new ToolExecutionException($"PLC Read Word: địa chỉ '{_address.Value}' không hợp lệ (vd D1000).");
        if (address.Unit != PlcDeviceUnit.Word)
            throw new ToolExecutionException($"PLC Read Word: '{_address.Value}' không phải thiết bị WORD (chỉ dùng D/W/R/ZR).");

        try
        {
            ushort raw = PlcHub.Link!.ReadWordAsync(address, context.CancellationToken).GetAwaiter().GetResult();
            double real = (short)raw / _scale.Value; // (short) để nhận đúng số âm theo quy ước bù 2 của PLC
            _output.Value = real;
            context.Log($"PLC Read Word: {address} = {raw} → {real} (÷ scale {_scale.Value})");
        }
        catch (OperationCanceledException) { throw; }
        catch (PlcLinkException ex)
        {
            throw new ToolExecutionException($"PLC Read Word: lỗi giao tiếp PLC — {ex.Message}", ex);
        }
    }
}