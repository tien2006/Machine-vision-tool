// ==================== Vai trò chính:                Cắt tách TỪNG đối tượng đã tìm thấy bởi Template Matching thành ảnh riêng biệt (VD 100 con ốc -> 100 ảnh nhỏ)
// ==================== Thành phần / Class tiêu biểu: ExtractTemplateMatchesTool
// ==================== Phụ thuộc vào:                OpenCvSharp (WarpAffine để bù xoay), Core.Models (TemplateMatchingNCCResult, TemplateMatchInstance)
// ==================== Pattern / Kỹ thuật nổi bật:   Rotation-compensated cropping - nếu vật thể bị xoay lệch so
//                       với mẫu gốc, ảnh được xoay ngược lại quanh tâm khớp mẫu TRƯỚC khi cắt, để ảnh cắt ra luôn
//                       thẳng đứng giống hệt hướng của Template, thuận tiện cho các bước so sánh/OCR phía sau.

using System;
using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Models; // TemplateMatchingNCCResult, TemplateMatchInstance, Point2d
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;
using Point2d = VisionFlow.Core.Models.Point2d;

namespace VisionFlow.Tools.Utility; // Cùng thư mục với ImageSelectorTool / AnnularMaskTool

/// <summary>Cách xác định kích thước mỗi ảnh cắt ra.</summary>
public enum ExtractOutputSizeMode
{
    TemplateSize,     // Đúng theo tài liệu: kích thước = kích thước ảnh mẫu (TemplateImageMatrix) gốc
    MatchBoundingBox, // Suy luận thêm: dùng đúng khung 4 góc (LeftTop/RightTop/RightBottom/LeftBottom) mà chính kết quả khớp đó báo về
    Custom            // Suy luận thêm: kích thước cố định do người dùng nhập (CustomWidth/CustomHeight)
}

/// <summary>Cách trích xuất vùng ảnh (hiện tại tài liệu chỉ định nghĩa BoundingBox).</summary>
public enum ExtractionMode
{
    BoundingBox // Vẽ khung chữ nhật bao quanh đối tượng rồi cắt phần đó ra
}

/// <summary>
/// ExtractTemplateMatches: nhận kết quả từ 1 Tool Template Matching (TemplateMatchingNCC/ContourTemplateMatching/
/// ORBTemplateMatching - cả 3 đều dùng chung <see cref="TemplateMatchingNCCResult"/>) và CẮT RA từng vùng ảnh
/// tương ứng với mỗi vị trí khớp mẫu tìm được, đóng gói thành danh sách để đưa vào các Tool đo lường/kiểm tra
/// chạy riêng trên TỪNG đối tượng (VD FindCircle đo từng lỗ ốc, OCR đọc từng nhãn dán).
/// </summary>
[ToolMetadata(
    "ExtractTemplateMatches",
    DisplayName = "Extract Template Matches",
    Category = "Utility",
    Description = "Crop each template-matched instance out of the source image into a separate list of images")]
public sealed class ExtractTemplateMatchesTool : VisionTool
{
    #region 1. Khai báo Port (In/Out)
    private readonly InputPort<IVisionImage> _imageMatrix;                  // Ảnh gốc chứa các đối tượng cần trích xuất
    private readonly InputPort<TemplateMatchingNCCResult> _templateResult;  // Kết quả từ TemplateMatchingNCC/ContourTemplateMatching/ORBTemplateMatching
    private readonly InputPort<IVisionImage> _templateImageMatrix;          // Ảnh mẫu gốc - dùng khi OutputSize=TemplateSize

    private readonly OutputPort<IVisionImage> _outImageMatrix;                     // Xuất lại ảnh gốc (nối tiếp luồng)
    private readonly OutputPort<IVisionImage> _outMatchedImages;                   // MỘT ảnh hiện tại/đầu tiên trong danh sách (xem ProcessSequentially)
    private readonly OutputPort<IReadOnlyList<IVisionImage>> _outAllMatchedImages; // TOÀN BỘ ảnh đã cắt
    private readonly OutputPort<int> _outMatchCount;
    private readonly OutputPort<IReadOnlyList<TemplateMatchInstance>> _outMatchDetails; // Tọa độ + điểm số từng đối tượng, tái dùng đúng type gốc
    #endregion

    #region 2. Khai báo Parameter
    private readonly ToolParameter<ExtractionMode> _extractionMode;
    private readonly ToolParameter<bool> _handleRotation;
    private readonly ToolParameter<int> _maxMatches;
    private readonly ToolParameter<double> _minScore;
    private readonly ToolParameter<ExtractOutputSizeMode> _outputSize;
    private readonly ToolParameter<int> _customWidth;   // Chỉ dùng khi OutputSize=Custom
    private readonly ToolParameter<int> _customHeight;  // Chỉ dùng khi OutputSize=Custom
    private readonly ToolParameter<int> _paddingPixels;
    private readonly ToolParameter<bool> _processSequentially; // true: MatchedImages xoay vòng qua từng ảnh mỗi lần Execute | false: luôn là ảnh đầu tiên
    #endregion

    #region 3. Trạng thái nội bộ (chỉ dùng khi ProcessSequentially=true - giống pattern ImageListIteratorTool)
    private int _cursorIndex;
    private TemplateMatchingNCCResult? _lastResultRef;
    #endregion

    public ExtractTemplateMatchesTool()
    {
        _imageMatrix = AddInput<IVisionImage>("ImageMatrix", "Image Matrix");
        _templateResult = AddInput<TemplateMatchingNCCResult>("TemplateMatchingResult", "Template Matching Result");
        _templateImageMatrix = AddInput<IVisionImage>("TemplateImageMatrix", "Template Image Matrix", optional: true);

        _outImageMatrix = AddOutput<IVisionImage>("ImageMatrix", "Image Matrix");
        _outMatchedImages = AddOutput<IVisionImage>("MatchedImages", "Matched Images");
        _outAllMatchedImages = AddOutput<IReadOnlyList<IVisionImage>>("AllMatchedImages", "All Matched Images");
        _outMatchCount = AddOutput<int>("MatchCount", "Match Count");
        _outMatchDetails = AddOutput<IReadOnlyList<TemplateMatchInstance>>("MatchDetails", "Match Details");

        _extractionMode = AddParameter("ExtractionMode", ExtractionMode.BoundingBox, "Extraction Mode", category: "Extraction", order: 1);
        _handleRotation = AddParameter("HandleRotation", true, "Handle Rotation", category: "Extraction", order: 2);
        _maxMatches = AddParameter("MaxMatches", 50, "Max Matches", min: 1, max: 10000, category: "Extraction", order: 3);
        _minScore = AddParameter("MinScore", 0.7, "Min Score", min: 0.0, max: 1.0, category: "Extraction", order: 4);
        _outputSize = AddParameter("OutputSize", ExtractOutputSizeMode.TemplateSize, "Output Size", category: "Extraction", order: 5);
        _customWidth = AddParameter("CustomWidth", 100, "Custom Width", min: 1, max: 10000, category: "Extraction", order: 6);
        _customHeight = AddParameter("CustomHeight", 100, "Custom Height", min: 1, max: 10000, category: "Extraction", order: 7);
        _paddingPixels = AddParameter("PaddingPixels", 5, "Padding Pixels", min: 0, max: 500, category: "Extraction", order: 8);
        _processSequentially = AddParameter("ProcessSequentially", true, "Process Sequentially", category: "Extraction", order: 9);
    }

    protected override void OnExecute(IToolContext context)
    {
        Mat src = _imageMatrix.Value!.AsMat();
        var result = _templateResult.Value ?? throw new ToolExecutionException("ExtractTemplateMatches: TemplateMatchingResult input is not connected.");

        // ----- Bước 1: lọc theo MinScore rồi giới hạn theo MaxMatches (giữ nguyên thứ tự upstream đã sắp) -----
        var filtered = result.Matches
            .Where(m => m.MatchScore >= _minScore.Value)
            .Take(_maxMatches.Value)
            .ToList();

        _outImageMatrix.Value = new MatVisionImage(src.Clone());

        if (filtered.Count == 0)
        {
            EmitEmpty(context);
            return;
        }

        // ----- Bước 2: xác định kích thước output chung theo OutputSize -----
        (double baseW, double baseH) = ResolveBaseSize(filtered[0]);

        // ----- Bước 3: cắt từng vị trí khớp mẫu -----
        var cropped = new List<IVisionImage>(filtered.Count);
        foreach (var match in filtered)
        {
            (double w, double h) = _outputSize.Value == ExtractOutputSizeMode.MatchBoundingBox
                ? (Distance(match.LeftTop, match.RightTop), Distance(match.LeftTop, match.LeftBottom))
                : (baseW, baseH);

            w += _paddingPixels.Value * 2;
            h += _paddingPixels.Value * 2;

            cropped.Add(new MatVisionImage(CropOneMatch(src, match, w, h)));
        }

        // ----- Bước 4: chọn ảnh cho output "MatchedImages" (đơn lẻ) theo ProcessSequentially -----
        bool listChanged = !ReferenceEquals(_lastResultRef, result);
        _lastResultRef = result;
        if (listChanged) _cursorIndex = 0;

        int singleIndex;
        if (_processSequentially.Value)
        {
            singleIndex = _cursorIndex % cropped.Count;
            _cursorIndex = (_cursorIndex + 1) % cropped.Count; // Chuẩn bị sẵn cho lần Execute() kế tiếp, giống pattern GrabImageTool
        }
        else
        {
            singleIndex = 0; // Luôn là ảnh đầu tiên khi xử lý đồng thời tất cả qua AllMatchedImages
        }

        _outMatchedImages.Value = cropped[singleIndex];
        _outAllMatchedImages.Value = cropped;
        _outMatchCount.Value = cropped.Count;
        _outMatchDetails.Value = filtered;

        context.Log($"ExtractTemplateMatches: extracted {cropped.Count} match(es), MatchedImages index={singleIndex} (sequential={_processSequentially.Value})");
    }

    /// <summary>Kích thước cơ sở dùng chung cho mọi match khi OutputSize khác MatchBoundingBox.</summary>
    private (double w, double h) ResolveBaseSize(TemplateMatchInstance firstMatch)
    {
        switch (_outputSize.Value)
        {
            case ExtractOutputSizeMode.Custom:
                return (_customWidth.Value, _customHeight.Value);

            case ExtractOutputSizeMode.MatchBoundingBox:
                // Giá trị thật sự được tính RIÊNG cho từng match ở vòng lặp chính - trả tạm giá trị của match đầu tiên, không dùng tới ở đây
                return (Distance(firstMatch.LeftTop, firstMatch.RightTop), Distance(firstMatch.LeftTop, firstMatch.LeftBottom));

            default: // TemplateSize
                var templateImg = _templateImageMatrix.Value;
                if (templateImg == null)
                    throw new ToolExecutionException("ExtractTemplateMatches: OutputSize=TemplateSize requires TemplateImageMatrix input to be connected.");
                Mat t = templateImg.AsMat();
                return (t.Width, t.Height);
        }
    }

    /// <summary>Cắt 1 vùng ảnh tương ứng với 1 kết quả khớp mẫu, có bù xoay nếu HandleRotation=true.</summary>
    private Mat CropOneMatch(Mat src, TemplateMatchInstance match, double w, double h)
    {
        if (_handleRotation.Value && Math.Abs(match.MatchedAngle) > 0.01)
        {
            // Xoay CẢ ẢNH GỐC ngược lại quanh tâm khớp mẫu để đưa vật thể về thẳng đứng như Template gốc,
            // rồi mới cắt hình chữ nhật canh giữa - nhờ vậy ảnh cắt ra luôn đúng hướng dù vật thể bị xoay lệch.
            // LƯU Ý CHIỀU XOAY: nếu ảnh cắt ra bị NGƯỢC hướng so với mong đợi, đổi dấu thành +match.MatchedAngle
            // bên dưới cho khớp đúng quy ước xoay của TemplateMatchingNCCTool đang dùng trong dự án.
            using Mat rotMatrix = Cv2.GetRotationMatrix2D(new Point2f((float)match.Center.X, (float)match.Center.Y), -match.MatchedAngle, 1.0);
            using Mat rotated = new Mat();
            Cv2.WarpAffine(src, rotated, rotMatrix, src.Size());

            Rect roi = BuildCenteredRect(match.Center, w, h, rotated.Width, rotated.Height);
            return new Mat(rotated, roi).Clone();
        }
        else
        {
            Rect roi = BuildCenteredRect(match.Center, w, h, src.Width, src.Height);
            return new Mat(src, roi).Clone();
        }
    }

    private static Rect BuildCenteredRect(Point2d center, double w, double h, int imgWidth, int imgHeight)
    {
        int x = (int)Math.Round(center.X - w / 2);
        int y = (int)Math.Round(center.Y - h / 2);
        int width = Math.Max(1, (int)Math.Round(w));
        int height = Math.Max(1, (int)Math.Round(h));

        // Kẹp về đúng phạm vi ảnh - vật thể ở gần mép ảnh có thể khiến khung cắt lý thuyết tràn ra ngoài
        x = Math.Clamp(x, 0, Math.Max(0, imgWidth - 1));
        y = Math.Clamp(y, 0, Math.Max(0, imgHeight - 1));
        width = Math.Min(width, imgWidth - x);
        height = Math.Min(height, imgHeight - y);

        return new Rect(x, y, Math.Max(1, width), Math.Max(1, height));
    }

    private static double Distance(Point2d a, Point2d b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>Không có match nào đạt MinScore - vẫn xuất kết quả "rỗng nhưng hợp lệ" thay vì để null, tránh crash downstream.</summary>
    private void EmitEmpty(IToolContext context)
    {
        var placeholder = new Mat(100, 150, MatType.CV_8UC3, new Scalar(128, 128, 128));
        Cv2.PutText(placeholder, "No Match", new Point(15, 55), HersheyFonts.HersheySimplex, 0.6, Scalar.White, 1);

        _outMatchedImages.Value = new MatVisionImage(placeholder);
        _outAllMatchedImages.Value = Array.Empty<IVisionImage>();
        _outMatchCount.Value = 0;
        _outMatchDetails.Value = Array.Empty<TemplateMatchInstance>();

        context.Log("ExtractTemplateMatches: no match passed MinScore threshold.");
    }
}