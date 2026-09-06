// ==================== Vai trò chính:                So sánh 1 giá trị đầu vào (InputValue) với giá trị tham chiếu (ReferenceValue) bằng toán tử so sánh -> trả về OK/NG
// ==================== Thành phần / Class tiêu biểu: CompareTool
// ==================== Phụ thuộc vào:                Core.Ports + Core.Tools (không phụ thuộc OpenCvSharp - tool logic thuần, không xử lý ảnh)
// ==================== Pattern / Kỹ thuật nổi bật:   Type-agnostic comparison (Number/String/Boolean) qua object InputValue + Tolerance cho so sánh số thực

using System;
using System.Globalization;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;

namespace VisionFlow.Tools.Utility;

/// <summary>
/// Tool "ra quyết định" cơ bản nhất trong pipeline: nhận 1 giá trị đo được (số/chuỗi/bool) từ tool phía trước
/// (VD: BlobAnalysis.RegionArea, FindCircle.Radius, TemplateMatchingNCC.MatchScore...), so sánh với 1 giá trị
/// chuẩn (ReferenceValue) bằng toán tử người dùng chọn, rồi trả về đúng/sai (OK/NG).
/// Ví dụ: BlobAnalysis đếm được 5 vật -> CompareTool kiểm tra "5 == 5" -> OK.
/// </summary>
[ToolMetadata("CompareTool", DisplayName = "Compare Tool", Category = "Utility",
    Description = "Compare an input value against a reference value using a comparison operator (==, >, <, >=, <=, !=).")]
public sealed class CompareTool : VisionTool
{
    #region 1. Ports
    private readonly InputPort<object> _inputValue; // Giá trị cần so sánh - nhận mọi kiểu (double, int, string, bool...)

    private readonly OutputPort<bool> _outResult;                // Result: true nếu phép so sánh đúng
    private readonly OutputPort<string> _outResultText;          // ResultText: PassValue hoặc FailValue
    private readonly OutputPort<object> _outInputValue;          // InputValue (Output): pass-through
    private readonly OutputPort<object> _outReferenceValue;      // ReferenceValue (Output): giá trị tham chiếu đã dùng, sau khi convert đúng kiểu
    private readonly OutputPort<string> _outComparisonDetails;   // ComparisonDetails: VD "48.3 >= 45 => True"
    #endregion

    #region 2. Parameters
    private readonly ToolParameter<bool> _caseSensitive;             // Phân biệt hoa-thường khi so sánh chuỗi
    private readonly ToolParameter<string> _comparisonOperator;      // "==", ">", "<", ">=", "<=", "!="
    private readonly ToolParameter<string> _comparisonType;          // "Number" | "String" | "Boolean"
    private readonly ToolParameter<string> _passValue;               // Chuỗi xuất ra khi đạt
    private readonly ToolParameter<string> _failValue;                // Chuỗi xuất ra khi không đạt
    private readonly ToolParameter<string> _referenceValue;          // Giá trị chuẩn, nhập dạng chuỗi rồi tool tự convert theo ComparisonType
    private readonly ToolParameter<double> _tolerance;               // Sai số cho phép khi so sánh số bằng == hoặc !=
    #endregion

    public CompareTool()
    {
        _inputValue = AddInput<object>("InputValue", "Input Value");

        _outResult = AddOutput<bool>("Result", "Result");
        _outResultText = AddOutput<string>("ResultText", "Result Text");
        _outInputValue = AddOutput<object>("InputValue", "Input Value");
        _outReferenceValue = AddOutput<object>("ReferenceValue", "Reference Value");
        _outComparisonDetails = AddOutput<string>("ComparisonDetails", "Comparison Details");

        _caseSensitive = AddParameter<bool>("CaseSensitive", false, "Case Sensitive", category: "Comparison", order: 1);
        _comparisonOperator = AddChoiceParameter("ComparisonOperator", "==",
            new[] { "==", ">", "<", ">=", "<=", "!=" }, "Comparison Operator", category: "Comparison", order: 2);
        _comparisonType = AddChoiceParameter("ComparisonType", "Number",
            new[] { "Number", "String", "Boolean" }, "Comparison Type", category: "Comparison", order: 3);
        _passValue = AddParameter<string>("PassValue", "OK", "Pass Value", category: "Comparison", order: 4);
        _failValue = AddParameter<string>("FailValue", "NG", "Fail Value", category: "Comparison", order: 5);
        _referenceValue = AddParameter<string>("ReferenceValue", "0", "Reference Value", category: "Comparison", order: 6);
        _tolerance = AddParameter<double>("Tolerance", 0.001, "Tolerance", min: 0.0, max: 1_000_000.0, category: "Comparison", order: 7);
    }

    protected override void OnExecute(IToolContext context)
    {
        object? input = _inputValue.Value;
        string op = _comparisonOperator.Value;
        string type = _comparisonType.Value;

        bool result;
        object referenceOut;
        string details;

        switch (type)
        {
            case "String":
                {
                    string a = input?.ToString() ?? "";
                    string b = _referenceValue.Value;
                    var comparisonMode = _caseSensitive.Value ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                    int cmp = string.Compare(a, b, comparisonMode);
                    result = ApplyOperatorToComparisonSign(op, cmp);
                    referenceOut = b;
                    details = $"\"{a}\" {op} \"{b}\" => {result}";
                    break;
                }
            case "Boolean":
                {
                    bool a = ToBool(input);
                    bool b = ParseBoolReference(_referenceValue.Value);
                    // Chỉ == và != có ý nghĩa thật sự với Boolean (đúng theo tài liệu); các toán tử khác quy về so sánh số 0/1 để không bao giờ crash pipeline
                    result = op switch
                    {
                        "==" => a == b,
                        "!=" => a != b,
                        _ => ApplyOperatorToComparisonSign(op, (a ? 1 : 0).CompareTo(b ? 1 : 0)),
                    };
                    referenceOut = b;
                    details = $"{a} {op} {b} => {result}";
                    break;
                }
            default: // "Number"
                {
                    double a = ToDouble(input);
                    double b = double.Parse(_referenceValue.Value, CultureInfo.InvariantCulture);
                    double tol = Math.Max(0.0, _tolerance.Value);
                    result = op switch
                    {
                        "==" => Math.Abs(a - b) <= tol,   // Đúng theo tài liệu: == dùng Tolerance để tránh lỗi so sánh số thực (0.1+0.2 != 0.3 chính xác)
                        "!=" => Math.Abs(a - b) > tol,
                        ">" => a > b,
                        "<" => a < b,
                        ">=" => a >= b,
                        "<=" => a <= b,
                        _ => throw new ToolExecutionException($"CompareTool: toán tử '{op}' không hợp lệ."),
                    };
                    referenceOut = b;
                    details = $"{a} {op} {b} => {result}";
                    break;
                }
        }

        _outResult.Value = result;
        _outResultText.Value = result ? _passValue.Value : _failValue.Value;
        _outInputValue.Value = input!;
        _outReferenceValue.Value = referenceOut;
        _outComparisonDetails.Value = details;

        context.Log($"CompareTool: {details}");
    }

    #region 3. Helpers

    /// <summary>Áp toán tử so sánh (==,>,<,>=,<=,!=) lên kết quả string.Compare/int.CompareTo (âm/0/dương).</summary>
    private static bool ApplyOperatorToComparisonSign(string op, int cmp) => op switch
    {
        "==" => cmp == 0,
        "!=" => cmp != 0,
        ">" => cmp > 0,
        "<" => cmp < 0,
        ">=" => cmp >= 0,
        "<=" => cmp <= 0,
        _ => throw new ToolExecutionException($"CompareTool: toán tử '{op}' không hợp lệ."),
    };

    /// <summary>Convert InputValue (object bất kỳ) sang double - hỗ trợ số có sẵn (int/double/float) lẫn chuỗi số.</summary>
    private static double ToDouble(object? value) => value switch
    {
        null => throw new ToolExecutionException("CompareTool: InputValue là null, không thể so sánh kiểu Number."),
        double d => d,
        float f => f,
        int i => i,
        long l => l,
        bool b => b ? 1.0 : 0.0,
        string s => double.Parse(s, CultureInfo.InvariantCulture),
        _ => Convert.ToDouble(value, CultureInfo.InvariantCulture),
    };

    /// <summary>Convert InputValue sang bool - chấp nhận bool có sẵn, số (khác 0 = true), hoặc chuỗi "true"/"1"/"OK"...</summary>
    private static bool ToBool(object? value) => value switch
    {
        null => false,
        bool b => b,
        double d => Math.Abs(d) > 1e-9,
        int i => i != 0,
        string s => ParseBoolReference(s),
        _ => Convert.ToBoolean(value, CultureInfo.InvariantCulture),
    };

    /// <summary>Parse chuỗi ReferenceValue thành bool - chấp nhận true/false, 1/0, OK/NG (thân thiện với các chuỗi PassValue/FailValue thường dùng).</summary>
    private static bool ParseBoolReference(string s)
    {
        if (bool.TryParse(s, out bool b)) return b;
        if (s.Trim() == "1") return true;
        if (s.Trim() == "0") return false;
        if (string.Equals(s, "OK", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(s, "NG", StringComparison.OrdinalIgnoreCase)) return false;
        throw new ToolExecutionException($"CompareTool: không parse được ReferenceValue '{s}' sang Boolean.");
    }

    #endregion
}