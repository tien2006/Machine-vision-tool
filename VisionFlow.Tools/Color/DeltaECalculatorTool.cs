// ==================== Vai trò chính:                Tính độ lệch màu ΔE (CIEDE2000/CIE76) giữa màu đo được và màu mẫu chuẩn - chuẩn công nghiệp cho cảm quan màu
// ==================== Thành phần / Class tiêu biểu: DeltaECalculatorTool
// ==================== Phụ thuộc vào:                Toán học thuần (không cần OpenCvSharp)
// ==================== Pattern / Kỹ thuật nổi bật:   Công thức CIEDE2000 (2000) đầy đủ + CIE76 (1976) đơn giản để đối chiếu

using System;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;

namespace VisionFlow.Tools.Color;

/// <summary>
/// Tính ΔE (Delta-E) - tiêu chuẩn công nghiệp đo chính xác cảm quan màu, sai số gần với cách mắt người
/// cảm nhận. Nhận 3 giá trị L/A/B (thường nối từ LabColorDetector.L/.A/.B) so với màu mẫu chuẩn RefL/A/B.
/// Thang JND (Just-Noticeable Difference): &lt;1.0 không phân biệt được, 1.0-2.3 sai khác nhỏ,
/// 2.3-5.0 sai rõ, &gt;5.0 sai lớn dễ thấy.
/// </summary>
[ToolMetadata("DeltaECalculator", DisplayName = "Delta E Calculator", Category = "Color",
    Description = "Compute perceptual color difference (Delta-E) between measured and reference Lab colors")]
public sealed class DeltaECalculatorTool : VisionTool
{
    private readonly InputPort<double> _l;
    private readonly InputPort<double> _a;
    private readonly InputPort<double> _b;

    private readonly OutputPort<double> _outDeltaE;
    private readonly OutputPort<bool> _outIsMatch;
    private readonly OutputPort<string> _outSampleLab;
    private readonly OutputPort<string> _outRefLab;
    private readonly OutputPort<string> _outMethod;

    // ----- Tab Reference Color -----
    private readonly ToolParameter<double> _refL, _refA, _refB;

    // ----- Tab Comparison -----
    private readonly ToolParameter<string> _method;
    private readonly ToolParameter<double> _threshold;

    public DeltaECalculatorTool()
    {
        _l = AddInput<double>("L");
        _a = AddInput<double>("A");
        _b = AddInput<double>("B");

        _outDeltaE = AddOutput<double>("DeltaE");
        _outIsMatch = AddOutput<bool>("IsMatch");
        _outSampleLab = AddOutput<string>("SampleLab");
        _outRefLab = AddOutput<string>("RefLab");
        _outMethod = AddOutput<string>("Method");

        _refL = AddParameter("RefL", 50.0, "Ref L", 0.0, 100.0, category: "Reference Color", order: 1);
        _refA = AddParameter("RefA", 0.0, "Ref A", -128.0, 127.0, category: "Reference Color", order: 2);
        _refB = AddParameter("RefB", 0.0, "Ref B", -128.0, 127.0, category: "Reference Color", order: 3);

        _method = AddChoiceParameter("Method", "CIEDE2000", new[] { "CIEDE2000", "CIE76" }, "Method", category: "Comparison", order: 1);
        _threshold = AddParameter("Threshold", 2.0, "Threshold", 0.0, 100.0, category: "Comparison", order: 2);
    }

    protected override void OnExecute(IToolContext context)
    {
        double l1 = _l.Value, a1 = _a.Value, b1 = _b.Value;
        double l2 = _refL.Value, a2 = _refA.Value, b2 = _refB.Value;

        double deltaE = _method.Value == "CIE76" ? DeltaE76(l1, a1, b1, l2, a2, b2) : DeltaE2000(l1, a1, b1, l2, a2, b2);
        bool isMatch = deltaE <= _threshold.Value;

        _outDeltaE.Value = deltaE;
        _outIsMatch.Value = isMatch;
        _outSampleLab.Value = $"L={l1:F1}, a={a1:F1}, b={b1:F1}";
        _outRefLab.Value = $"L={l2:F1}, a={a2:F1}, b={b2:F1}";
        _outMethod.Value = _method.Value;

        context.Log($"DeltaE [{_method.Value}]: {deltaE:F2} (Threshold={_threshold.Value}) -> IsMatch={isMatch}");
    }

    /// <summary>Công thức CIE76 (1976) - đơn giản, nhanh nhưng kém chính xác ở dải xanh dương/saturation cao.</summary>
    private static double DeltaE76(double l1, double a1, double b1, double l2, double a2, double b2)
        => Math.Sqrt(Math.Pow(l1 - l2, 2) + Math.Pow(a1 - a2, 2) + Math.Pow(b1 - b2, 2));

    /// <summary>
    /// Công thức CIEDE2000 (2000) - chuẩn mới, chính xác cao nhất trên toàn dải màu. Cài đặt đầy đủ
    /// theo công thức gốc của CIE (bao gồm hiệu chỉnh trọng số L/C/H và số hạng tương tác RT).
    /// </summary>
    private static double DeltaE2000(double l1, double a1, double b1, double l2, double a2, double b2)
    {
        const double kL = 1.0, kC = 1.0, kH = 1.0;

        double c1 = Math.Sqrt(a1 * a1 + b1 * b1);
        double c2 = Math.Sqrt(a2 * a2 + b2 * b2);
        double cBar = (c1 + c2) / 2.0;

        double g = 0.5 * (1 - Math.Sqrt(Math.Pow(cBar, 7) / (Math.Pow(cBar, 7) + Math.Pow(25, 7))));
        double a1p = a1 * (1 + g), a2p = a2 * (1 + g);
        double c1p = Math.Sqrt(a1p * a1p + b1 * b1), c2p = Math.Sqrt(a2p * a2p + b2 * b2);

        double h1p = (a1p == 0 && b1 == 0) ? 0 : Math.Atan2(b1, a1p) * 180.0 / Math.PI; if (h1p < 0) h1p += 360;
        double h2p = (a2p == 0 && b2 == 0) ? 0 : Math.Atan2(b2, a2p) * 180.0 / Math.PI; if (h2p < 0) h2p += 360;

        double deltaLp = l2 - l1;
        double deltaCp = c2p - c1p;

        double deltahp;
        if (c1p * c2p == 0) deltahp = 0;
        else if (Math.Abs(h2p - h1p) <= 180) deltahp = h2p - h1p;
        else if (h2p - h1p > 180) deltahp = h2p - h1p - 360;
        else deltahp = h2p - h1p + 360;
        double deltaHp = 2 * Math.Sqrt(c1p * c2p) * Math.Sin(deltahp * Math.PI / 360.0);

        double lBarp = (l1 + l2) / 2.0;
        double cBarp = (c1p + c2p) / 2.0;

        double hBarp;
        if (c1p * c2p == 0) hBarp = h1p + h2p;
        else if (Math.Abs(h1p - h2p) <= 180) hBarp = (h1p + h2p) / 2.0;
        else if (h1p + h2p < 360) hBarp = (h1p + h2p + 360) / 2.0;
        else hBarp = (h1p + h2p - 360) / 2.0;

        double t = 1 - 0.17 * Math.Cos((hBarp - 30) * Math.PI / 180.0)
                     + 0.24 * Math.Cos(2 * hBarp * Math.PI / 180.0)
                     + 0.32 * Math.Cos((3 * hBarp + 6) * Math.PI / 180.0)
                     - 0.20 * Math.Cos((4 * hBarp - 63) * Math.PI / 180.0);

        double deltaTheta = 30 * Math.Exp(-Math.Pow((hBarp - 275) / 25.0, 2));
        double rc = 2 * Math.Sqrt(Math.Pow(cBarp, 7) / (Math.Pow(cBarp, 7) + Math.Pow(25, 7)));
        double sl = 1 + (0.015 * Math.Pow(lBarp - 50, 2)) / Math.Sqrt(20 + Math.Pow(lBarp - 50, 2));
        double sc = 1 + 0.045 * cBarp;
        double sh = 1 + 0.015 * cBarp * t;
        double rt = -Math.Sin(2 * deltaTheta * Math.PI / 180.0) * rc;

        double termL = deltaLp / (kL * sl);
        double termC = deltaCp / (kC * sc);
        double termH = deltaHp / (kH * sh);

        return Math.Sqrt(termL * termL + termC * termC + termH * termH + rt * termC * termH);
    }
}