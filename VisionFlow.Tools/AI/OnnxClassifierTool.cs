// ==================== Vai trò chính:                Nạp và chạy MỘT model phân loại ảnh dạng ONNX bất kỳ (do bạn tự huấn luyện bằng Python rồi export .onnx) — hạ tầng dùng chung cho mọi bài toán phân loại sau này
// ==================== Thành phần / Class tiêu biểu: OnnxClassifierTool, OnnxPreprocessMode, OnnxChannelOrder
// ==================== Phụ thuộc vào:                Core.Imaging (IVisionImage), Core.Ports, Core.Tools, OpenCvSharp, Microsoft.ML.OnnxRuntime (NuGet — xem hướng dẫn thêm ở cuối file)
// ==================== Pattern / Kỹ thuật nổi bật:   Adapter Pattern (bọc InferenceSession thành 1 Tool) + cache theo đường dẫn model (giống ObjectDetectionEngine cache YoloPredictor) để không nạp lại model mỗi frame
//
// VỊ TRÍ ĐẶT FILE: project VisionFlow.Tools (cùng project với BlobAnalysisTool, AnomalyDetectionNodeTool), TẠO thư mục mới Tools/AI/ — namespace VisionFlow.Tools.AI.
// KHÔNG cần sửa ToolRegistry/App.xaml.cs: ToolRegistry.RegisterAssembly() quét theo assembly, tool này cùng assembly với các tool khác nên tự xuất hiện trong danh sách (nhóm "AI").
//
// ĐÂY LÀ HẠ TẦNG CHUNG, CHƯA GẮN VỚI BÀI TOÁN CỤ THỂ: tool chỉ giả định model có ĐÚNG 1 input ảnh và ĐÚNG 1 output là
// một vector số (logits hoặc probability) — đúng hình dạng của hầu hết model phân loại (classification). Nếu sau này
// bạn làm object detection tự huấn luyện (nhiều box, không phải 1 vector), sẽ cần một Tool hậu xử lý khác, không dùng lại tool này.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging; // Chứa extension method AsMat() để lấy Mat thật từ IVisionImage

namespace VisionFlow.Tools.AI;

/// <summary>Cách chuẩn hoá giá trị pixel (0-255) trước khi đưa vào model — PHẢI khớp với lúc huấn luyện ở Python.</summary>
public enum OnnxPreprocessMode
{
    /// <summary>value / 255.0 — cách đơn giản nhất, hợp với model tự thiết kế từ đầu (không dùng pretrained).</summary>
    ZeroToOne,

    /// <summary>(value/255 - mean) / std theo ImageNet — BẮT BUỘC nếu bạn fine-tune model pretrained của torchvision (resnet, mobilenet...).</summary>
    ImageNetMeanStd,

    /// <summary>value/127.5 - 1.0 — dải [-1, 1], một số model (MobileNet bản gốc, một số model TensorFlow) dùng cách này.</summary>
    MinusOneToOne,

    /// <summary>Giữ nguyên 0-255 dạng float — chỉ dùng khi bạn CHẮC CHẮN model không chuẩn hoá lúc huấn luyện.</summary>
    RawByte
}

/// <summary>Thứ tự kênh màu model mong đợi. OpenCvSharp đọc ảnh theo BGR; hầu hết model huấn luyện bằng Python (PIL/torchvision) dùng RGB.</summary>
public enum OnnxChannelOrder { Rgb, Bgr }

/// <summary>
/// Tool tổng quát: đọc 1 ảnh, resize theo đúng kích thước model cần, chuẩn hoá, chạy suy luận ONNX, rồi trả về
/// nhãn dự đoán + độ tin cậy + toàn bộ vector điểm số (để tool khác, ví dụ Compare, tự quyết định ngưỡng riêng).
/// </summary>
[ToolMetadata("OnnxClassifier", DisplayName = "ONNX Classifier", Category = "AI",
    Description = "Runs a custom ONNX classification model (trained in Python) on an image: outputs predicted class, confidence and raw scores.")]
public sealed class OnnxClassifierTool : VisionTool, IDisposable
{
    private readonly InputPort<IVisionImage> _input;

    private readonly OutputPort<int> _outIndex;
    private readonly OutputPort<string> _outLabel;
    private readonly OutputPort<double> _outConfidence;
    private readonly OutputPort<double[]> _outScores;
    private readonly OutputPort<double> _outInferenceMs;

    private readonly ToolParameter<string> _modelPath;
    private readonly ToolParameter<string> _labelsPath;
    private readonly ToolParameter<int> _inputWidth;
    private readonly ToolParameter<int> _inputHeight;
    private readonly ToolParameter<OnnxPreprocessMode> _preprocess;
    private readonly ToolParameter<OnnxChannelOrder> _channelOrder;
    private readonly ToolParameter<bool> _applySoftmax;
    private readonly ToolParameter<string> _inputName;
    private readonly ToolParameter<string> _outputName;

    // Cache model theo đường dẫn: chỉ nạp lại (chậm, vài trăm ms) khi ModelPath thực sự đổi, không nạp lại mỗi lần Execute
    private InferenceSession? _session;
    private string? _cachedModelPath;
    private IReadOnlyList<string>? _cachedLabels;
    private string? _cachedLabelsPath;

    // Hệ số chuẩn hoá ImageNet chuẩn (thứ tự R, G, B) — dùng khi Preprocess = ImageNetMeanStd
    private static readonly float[] ImageNetMean = { 0.485f, 0.456f, 0.406f };
    private static readonly float[] ImageNetStd = { 0.229f, 0.224f, 0.225f };

    public OnnxClassifierTool()
    {
        _input = AddInput<IVisionImage>("Image", "Image Matrix");

        _outIndex = AddOutput<int>("PredictedIndex", "Predicted Index");
        _outLabel = AddOutput<string>("PredictedLabel", "Predicted Label");
        _outConfidence = AddOutput<double>("Confidence", "Confidence");
        _outScores = AddOutput<double[]>("Scores", "Scores (all classes)");
        _outInferenceMs = AddOutput<double>("InferenceMs", "Inference Time (ms, bonus)");

        _modelPath = AddParameter("ModelPath", "", "Model File (.onnx)", category: "Model", order: 1);
        _labelsPath = AddParameter("LabelsPath", "", "Labels File (.txt, optional)", category: "Model", order: 2);
        _inputWidth = AddParameter("InputWidth", 224, "Input Width", min: 1, max: 4096, category: "Model", order: 3);
        _inputHeight = AddParameter("InputHeight", 224, "Input Height", min: 1, max: 4096, category: "Model", order: 4);

        _preprocess = AddParameter("Preprocess", OnnxPreprocessMode.ImageNetMeanStd, "Preprocess Mode", category: "Preprocessing", order: 1);
        _channelOrder = AddParameter("ChannelOrder", OnnxChannelOrder.Rgb, "Channel Order", category: "Preprocessing", order: 2);

        _applySoftmax = AddParameter("ApplySoftmax", true, "Apply Softmax to Output", category: "Postprocessing", order: 1);
        _inputName = AddParameter("InputName", "", "Input Tensor Name (blank = auto)", category: "Advanced", order: 1);
        _outputName = AddParameter("OutputName", "", "Output Tensor Name (blank = auto)", category: "Advanced", order: 2);
    }

    protected override void OnExecute(IToolContext context)
    {
        Mat src = _input.Value!.AsMat();

        var session = GetOrLoadSession();
        var labels = GetOrLoadLabels();

        int width = _inputWidth.Value;
        int height = _inputHeight.Value;
        if (width < 1 || height < 1)
            throw new ToolExecutionException("ONNX Classifier: InputWidth/InputHeight phải lớn hơn 0.");

        var tensor = BuildInputTensor(src, width, height);

        string inputName = string.IsNullOrWhiteSpace(_inputName.Value) ? session.InputMetadata.Keys.First() : _inputName.Value;
        string? requestedOutput = string.IsNullOrWhiteSpace(_outputName.Value) ? null : _outputName.Value;

        var inputs = new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(inputName, tensor) };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var results = session.Run(inputs);
        sw.Stop();

        var outputValue = requestedOutput is null
            ? results.First()
            : results.FirstOrDefault(r => r.Name == requestedOutput)
              ?? throw new ToolExecutionException($"ONNX Classifier: model không có output tên '{requestedOutput}'. Các output có sẵn: {string.Join(", ", session.OutputMetadata.Keys)}.");

        float[] raw = outputValue.AsTensor<float>().ToArray();
        if (raw.Length == 0)
            throw new ToolExecutionException("ONNX Classifier: output của model rỗng.");

        double[] scores = _applySoftmax.Value ? Softmax(raw) : raw.Select(v => (double)v).ToArray();

        int predictedIndex = 0;
        for (int i = 1; i < scores.Length; i++)
            if (scores[i] > scores[predictedIndex]) predictedIndex = i;

        string label = labels is not null && predictedIndex < labels.Count
            ? labels[predictedIndex]
            : $"Class {predictedIndex}";

        if (labels is not null && labels.Count != scores.Length)
            context.Log($"ONNX Classifier: CẢNH BÁO — file nhãn có {labels.Count} dòng nhưng model xuất {scores.Length} lớp, nhãn có thể không khớp.");

        _outIndex.Value = predictedIndex;
        _outLabel.Value = label;
        _outConfidence.Value = scores[predictedIndex];
        _outScores.Value = scores;
        _outInferenceMs.Value = sw.Elapsed.TotalMilliseconds;
    }

    // ====================================================================
    // TIỀN XỬ LÝ ẢNH -> TENSOR
    // ====================================================================

    /// <summary>Resize ảnh về đúng kích thước model, đổi thứ tự kênh nếu cần, chuẩn hoá, rồi sắp theo layout NCHW mà ONNX cần.</summary>
    private DenseTensor<float> BuildInputTensor(Mat src, int width, int height)
    {
        using Mat resized = new Mat();
        Cv2.Resize(src, resized, new Size(width, height), interpolation: InterpolationFlags.Linear);

        using Mat colorFixed = new Mat();
        bool needRgb = _channelOrder.Value == OnnxChannelOrder.Rgb;
        if (resized.Channels() == 1)
            Cv2.CvtColor(resized, colorFixed, ColorConversionCodes.GRAY2BGR); // Model 3 kênh nhưng ảnh vào là ảnh xám: nhân bản 3 lần
        else if (needRgb)
            Cv2.CvtColor(resized, colorFixed, ColorConversionCodes.BGR2RGB); // OpenCvSharp đọc BGR; hầu hết model Python cần RGB
        else
            resized.CopyTo(colorFixed);

        byte[] pixels = new byte[colorFixed.Width * colorFixed.Height * 3];
        System.Runtime.InteropServices.Marshal.Copy(colorFixed.Data, pixels, 0, pixels.Length);

        var mode = _preprocess.Value;
        var data = new float[3 * height * width];
        int hw = height * width;

        // Ảnh trong bộ nhớ đang ở layout HWC (từng pixel: kênh0,kênh1,kênh2 liên tiếp) — phải chuyển sang CHW (từng kênh nằm liền khối) vì ONNX chuẩn dùng NCHW
        for (int i = 0; i < hw; i++)
        {
            for (int c = 0; c < 3; c++)
            {
                float v = pixels[i * 3 + c];
                data[c * hw + i] = mode switch
                {
                    OnnxPreprocessMode.ZeroToOne => v / 255f,
                    OnnxPreprocessMode.MinusOneToOne => v / 127.5f - 1f,
                    OnnxPreprocessMode.RawByte => v,
                    OnnxPreprocessMode.ImageNetMeanStd => (v / 255f - ImageNetMean[c]) / ImageNetStd[c],
                    _ => throw new ToolExecutionException($"ONNX Classifier: Preprocess mode {mode} chưa được hỗ trợ.")
                };
            }
        }

        return new DenseTensor<float>(data, new[] { 1, 3, height, width });
    }

    private static double[] Softmax(float[] logits)
    {
        double max = logits.Max();
        double[] exp = logits.Select(v => Math.Exp(v - max)).ToArray(); // trừ max trước để tránh tràn số (overflow) khi logits lớn
        double sum = exp.Sum();
        return sum > 0 ? exp.Select(v => v / sum).ToArray() : exp;
    }

    // ====================================================================
    // NẠP MODEL / NHÃN (CÓ CACHE)
    // ====================================================================

    private InferenceSession GetOrLoadSession()
    {
        string resolvedPath = ResolveModelPath(_modelPath.Value);

        if (_session is not null && _cachedModelPath == resolvedPath)
            return _session; // Không đổi đường dẫn: dùng lại model đã nạp, không mất thời gian nạp lại

        if (!File.Exists(resolvedPath))
            throw new ToolExecutionException(
                $"ONNX Classifier: không tìm thấy model '{resolvedPath}'. Điền đường dẫn tuyệt đối, hoặc chỉ tên file nếu đã đặt trong thư mục 'Models' cạnh file .exe.");

        InferenceSession newSession;
        try
        {
            newSession = new InferenceSession(resolvedPath);
        }
        catch (Exception ex)
        {
            throw new ToolExecutionException($"ONNX Classifier: không nạp được model '{resolvedPath}': {ex.Message}", ex);
        }

        _session?.Dispose(); // Nhả model cũ trước khi giữ model mới, tránh giữ 2 model trong bộ nhớ cùng lúc
        _session = newSession;
        _cachedModelPath = resolvedPath;
        return _session;
    }

    private IReadOnlyList<string>? GetOrLoadLabels()
    {
        string path = _labelsPath.Value;
        if (string.IsNullOrWhiteSpace(path)) return null;

        string resolved = ResolveModelPath(path);
        if (resolved == _cachedLabelsPath) return _cachedLabels;

        if (!File.Exists(resolved))
            throw new ToolExecutionException($"ONNX Classifier: không tìm thấy file nhãn '{resolved}'.");

        _cachedLabels = File.ReadAllLines(resolved).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        _cachedLabelsPath = resolved;
        return _cachedLabels;
    }

    /// <summary>Đường dẫn tuyệt đối dùng nguyên; đường dẫn tương đối / chỉ tên file thì tìm trong thư mục "Models" cạnh file .exe
    /// (cùng quy ước với model YOLO có sẵn của ObjectDetectionEngine, để bạn đặt mọi model vào một chỗ).</summary>
    private static string ResolveModelPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ToolExecutionException("ONNX Classifier: chưa điền ModelPath.");
        if (Path.IsPathRooted(path)) return path;
        return Path.Combine(AppContext.BaseDirectory, "Models", path);
    }

    public void Dispose() => _session?.Dispose();
}

// ============================================================================================
// HƯỚNG DẪN THÊM GÓI NUGET (làm 1 lần, trong Visual Studio):
//   Chuột phải VisionFlow.Tools -> Manage NuGet Packages -> Browse -> gõ "Microsoft.ML.OnnxRuntime" -> Install.
//   Đây là bản CPU (đủ dùng cho hầu hết bài toán phân loại nhỏ). Muốn chạy bằng GPU (NVIDIA) thì cài thêm
//   "Microsoft.ML.OnnxRuntime.Gpu" thay vì bản CPU — không đổi code, chỉ đổi gói.
// ============================================================================================