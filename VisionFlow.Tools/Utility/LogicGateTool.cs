// ==================== Vai trò chính:                Cổng logic (AND/OR/NAND/NOR/XOR) kết hợp 2 tín hiệu boolean thành 1 phán quyết OK/NG cuối cùng
// ==================== Thành phần / Class tiêu biểu: LogicGateTool
// ==================== Phụ thuộc vào:                Core.Ports + Core.Tools (tool logic thuần, không xử lý ảnh)
// ==================== Pattern / Kỹ thuật nổi bật:   Bảng chân lý (Truth Table) cổ điển - thường đặt cuối pipeline để tổng hợp nhiều điều kiện kiểm tra

using System;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;

namespace VisionFlow.Tools.Utility;

/// <summary>
/// Cổng logic then chốt trong pipeline kiểm tra: nhận 2 tín hiệu boolean (thường từ Result của 2 CompareTool
/// khác nhau, VD "đúng kích thước" và "đúng màu"), kết hợp lại bằng 1 phép toán logic thành phán quyết OK/NG cuối cùng.
/// Ví dụ: sản phẩm chỉ đạt khi vừa đúng kích thước VÀ vừa đúng màu -> nối 2 kết quả vào LogicGate với AND.
/// </summary>
[ToolMetadata("LogicGate", DisplayName = "Logic Gate", Category = "Utility",
    Description = "Combine 2 boolean signals using a logic operation (AND, OR, NAND, NOR, XOR).")]
public sealed class LogicGateTool : VisionTool
{
    #region 1. Ports
    private readonly InputPort<bool> _input1; // Tín hiệu boolean thứ nhất - bắt buộc
    private readonly InputPort<bool> _input2; // Tín hiệu boolean thứ hai - bắt buộc

    private readonly OutputPort<bool> _outResult;              // Result: kết quả logic cuối cùng
    private readonly OutputPort<string> _outResultText;        // ResultText: TrueValue hoặc FalseValue (+ số input nếu ShowInputCount)
    private readonly OutputPort<int> _outInputCount;           // InputCount: luôn = 2 ở phiên bản này
    private readonly OutputPort<bool[]> _outInputValues;       // InputValues: [Input1, Input2]
    private readonly OutputPort<string> _outEvaluationDetails; // EvaluationDetails: chi tiết quá trình tính
    #endregion

    #region 2. Parameters
    private readonly ToolParameter<string> _logicOperation;   // "AND" | "OR" | "NAND" | "NOR" | "XOR"
    private readonly ToolParameter<string> _trueValue;        // Chuỗi xuất ra khi Result = true
    private readonly ToolParameter<string> _falseValue;       // Chuỗi xuất ra khi Result = false
    private readonly ToolParameter<bool> _showInputCount;     // Nếu bật, thêm "(2 inputs)" vào sau ResultText
    #endregion

    public LogicGateTool()
    {
        _input1 = AddInput<bool>("Input1", "Input 1");
        _input2 = AddInput<bool>("Input2", "Input 2");

        _outResult = AddOutput<bool>("Result", "Result");
        _outResultText = AddOutput<string>("ResultText", "Result Text");
        _outInputCount = AddOutput<int>("InputCount", "Input Count");
        _outInputValues = AddOutput<bool[]>("InputValues", "Input Values");
        _outEvaluationDetails = AddOutput<string>("EvaluationDetails", "Evaluation Details");

        _logicOperation = AddChoiceParameter("LogicOperation", "AND",
            new[] { "AND", "OR", "NAND", "NOR", "XOR" }, "Logic Operation", category: "Parameters", order: 1);
        _trueValue = AddParameter<string>("TrueValue", "OK", "True Value", category: "Parameters", order: 2);
        _falseValue = AddParameter<string>("FalseValue", "NG", "False Value", category: "Parameters", order: 3);
        _showInputCount = AddParameter<bool>("ShowInputCount", false, "Show Input Count", category: "Parameters", order: 4);
    }

    protected override void OnExecute(IToolContext context)
    {
        // Input1/Input2 được khai báo BẮT BUỘC (isOptional=false, mặc định của AddInput) -> Engine đã tự đảm bảo
        // cả 2 phải được nối trước khi OnExecute() này được gọi, đúng yêu cầu "thiếu 1 trong 2 -> báo lỗi" của tài liệu.
        bool a = _input1.Value;
        bool b = _input2.Value;
        string operation = _logicOperation.Value;

        // ----- Bảng chân lý (Truth Table) đúng theo tài liệu -----
        bool result = operation switch
        {
            "AND" => a && b,
            "OR" => a || b,
            "NAND" => !(a && b),
            "NOR" => !(a || b),
            "XOR" => a ^ b,
            _ => throw new ToolExecutionException($"LogicGate: phép toán '{operation}' không hợp lệ."),
        };

        string resultText = result ? _trueValue.Value : _falseValue.Value;
        if (_showInputCount.Value) resultText += " (2 inputs)";

        string details = $"Input1={a}, Input2={b} | Operation: {operation} | Result: {result}";

        _outResult.Value = result;
        _outResultText.Value = resultText;
        _outInputCount.Value = 2;
        _outInputValues.Value = new[] { a, b };
        _outEvaluationDetails.Value = details;

        context.Log($"LogicGate: {details}");
    }
}