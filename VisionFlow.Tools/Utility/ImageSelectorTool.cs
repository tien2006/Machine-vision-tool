// ==================== Vai trò chính:                Chọn ra MỘT ảnh cố định từ một danh sách ảnh (ImageList) theo chỉ số hoặc quy tắc
// ==================== Thành phần / Class tiêu biểu: ImageSelectorTool
// ==================== Phụ thuộc vào:                OpenCvSharp
// ==================== Pattern / Kỹ thuật nổi bật:   Không bao giờ xuất ra null cho downstream - luôn có
//                       cơ chế Fallback (ảnh hợp lệ khác trong danh sách) và Placeholder (ảnh báo lỗi) dự phòng

using System;
using System.Collections.Generic;
using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;

namespace VisionFlow.Tools.Utility; // Cùng thư mục với RegionSelectorTool / CompareTool / Aggregator

/// <summary>Cách chọn ra 1 ảnh trong danh sách.</summary>
public enum ImageSelectionMode
{
    ByIndex, // Chọn theo đúng chỉ số ImageIndex do người dùng nhập
    First,   // Luôn lấy ảnh đầu tiên (index 0)
    Last,    // Luôn lấy ảnh cuối cùng
    Best,    // Lấy index 0 - CHỈ đúng nghĩa "tốt nhất" nếu danh sách upstream đã sắp theo điểm số giảm dần
    Random   // Lấy ngẫu nhiên 1 ảnh trong danh sách mỗi lần chạy
}

/// <summary>Cách xử lý khi ảnh tại vị trí đã chọn bị null/rỗng (hỏng, không giải mã được...).</summary>
public enum ImageFallbackMode
{
    FirstAvailable, // Quét từ đầu danh sách, lấy ảnh HỢP LỆ đầu tiên tìm thấy
    LastAvailable,  // Quét từ cuối danh sách, lấy ảnh HỢP LỆ cuối cùng tìm thấy
    CreateEmpty,    // Bỏ qua danh sách, tạo hẳn 1 ảnh đen rỗng 200x200 để downstream luôn có ảnh hợp lệ về mặt kỹ thuật
    UseOriginal     // Thử lấy đúng ảnh tại index 0 (không quét toàn bộ danh sách như FirstAvailable)
}

/// <summary>
/// ImageSelector: đứng sau các Tool sinh ra NHIỀU ảnh (VD ExtractObjectsFromContours cắt ra nhiều mảnh vật
/// thể, ORBTemplateMatching tìm nhiều instance) để lấy ĐÚNG 1 ảnh đưa vào bước xử lý tiếp theo.
/// Khác <c>ImageListIterator</c>: ImageSelector chỉ lấy 1 ảnh CỐ ĐỊNH mỗi lần chạy, không lặp qua từng ảnh.
/// Nguyên tắc thiết kế quan trọng: Tool này KHÔNG BAO GIỜ để Output rỗng/null - luôn có 3 lớp phòng thủ
/// theo thứ tự: (1) ảnh đúng theo SelectionMode → (2) FallbackMode nếu ảnh đó hỏng → (3) Placeholder nếu
/// cả danh sách rỗng hoặc không còn ảnh nào dùng được. Luôn kiểm tra <c>IsValidSelection</c> ở downstream
/// để biết ảnh nhận được có đúng là ảnh yêu cầu hay chỉ là kết quả dự phòng.
/// </summary>
[ToolMetadata(
    "ImageSelector",
    DisplayName = "Image Selector",
    Category = "Utility",
    Description = "Pick a single fixed image out of an ImageList by index or rule, with safe fallback/placeholder")]
public sealed class ImageSelectorTool : VisionTool
{
    #region 1. Khai báo Port (In/Out)
    private readonly InputPort<IReadOnlyList<IVisionImage>> _imageList; // Danh sách ảnh đầu vào, thường nối từ ExtractObjectsFromContours/ORBTemplateMatching
    private readonly OutputPort<IVisionImage> _imageMatrix;             // Ảnh đã chọn (không bao giờ null - xem class doc)
    private readonly OutputPort<int> _selectedIndex;                    // Chỉ số ảnh THỰC TẾ đã xuất ra (-1 nếu dùng placeholder/CreateEmpty)
    private readonly OutputPort<int> _totalCount;                       // Tổng số ảnh trong danh sách đầu vào
    private readonly OutputPort<bool> _isValidSelection;                // true nếu đúng ảnh yêu cầu, false nếu phải dùng fallback/placeholder
    #endregion

    #region 2. Khai báo Parameter (Tab Selection)
    private readonly ToolParameter<ImageSelectionMode> _selectionMode;
    private readonly ToolParameter<int> _imageIndex;      // Chỉ số cần lấy - CHỈ có tác dụng khi SelectionMode = ByIndex. Bắt đầu từ 0.
    private readonly ToolParameter<bool> _wrapAround;     // true: index vượt quá sẽ quay vòng (modulo) | false: kẹp về ảnh cuối
    private readonly ToolParameter<ImageFallbackMode> _fallbackMode;
    private readonly ToolParameter<bool> _createPlaceholder; // true: danh sách rỗng/không còn ảnh dùng được -> tạo ảnh báo lỗi thay vì ném lỗi dừng pipeline
    #endregion

    private static readonly Random Rng = new(); // Dùng chung cho SelectionMode = Random, giống quy ước ở GrabImageTool

    public ImageSelectorTool()
    {
        _imageList = AddInput<IReadOnlyList<IVisionImage>>("ImageList", "Image List");

        _imageMatrix = AddOutput<IVisionImage>("ImageMatrix", "Image Matrix");
        _selectedIndex = AddOutput<int>("SelectedIndex", "Selected Index");
        _totalCount = AddOutput<int>("TotalCount", "Total Count");
        _isValidSelection = AddOutput<bool>("IsValidSelection", "Is Valid Selection");

        _selectionMode = AddParameter("SelectionMode", ImageSelectionMode.ByIndex, "Selection Mode", category: "Selection", order: 1);
        _imageIndex = AddParameter("ImageIndex", 0, "Image Index", min: 0, max: 100000, category: "Selection", order: 2);
        _wrapAround = AddParameter("WrapAround", false, "Wrap Around", category: "Selection", order: 3);
        _fallbackMode = AddParameter("FallbackMode", ImageFallbackMode.FirstAvailable, "Fallback Mode", category: "Selection", order: 4);
        _createPlaceholder = AddParameter("CreatePlaceholder", true, "Create Placeholder", category: "Selection", order: 5);
    }

    protected override void OnExecute(IToolContext context)
    {
        var list = _imageList.Value;
        int totalCount = list?.Count ?? 0;

        if (totalCount == 0)
        {
            EmitFallback(context, totalCount, "danh sách ImageList rỗng");
            return;
        }

        // ----- Bước 1: xác định chỉ số THÔ theo SelectionMode -----
        int rawIndex = _selectionMode.Value switch
        {
            ImageSelectionMode.First => 0,
            ImageSelectionMode.Last => totalCount - 1,
            ImageSelectionMode.Best => 0, // Chỉ đúng nghĩa nếu danh sách upstream đã sắp theo điểm số giảm dần
            ImageSelectionMode.Random => Rng.Next(totalCount),
            _ => _imageIndex.Value // ByIndex
        };

        // ----- Bước 2: xử lý chỉ số vượt phạm vi theo WrapAround -----
        int index;
        bool isRequestedIndexHonored;
        if (rawIndex >= 0 && rawIndex < totalCount)
        {
            index = rawIndex;
            isRequestedIndexHonored = true;
        }
        else if (_wrapAround.Value)
        {
            // Modulo an toàn cho cả số âm: (-1 mod 5) phải ra 4, không phải -1
            index = ((rawIndex % totalCount) + totalCount) % totalCount;
            isRequestedIndexHonored = true; // Wrap là hành vi CHỦ ĐỊNH được bật -> vẫn coi là lựa chọn hợp lệ
        }
        else
        {
            index = Math.Clamp(rawIndex, 0, totalCount - 1);
            isRequestedIndexHonored = false; // Bị kẹp về khác với ảnh yêu cầu ban đầu -> không còn là lựa chọn đúng ý người dùng
        }

        // ----- Bước 3: kiểm tra ảnh tại index có dùng được không -----
        var candidate = list![index];
        if (IsUsable(candidate))
        {
            Emit(candidate!, index, totalCount, isRequestedIndexHonored);
            context.Log($"ImageSelector: mode={_selectionMode.Value}, index={index}/{totalCount - 1}, valid={isRequestedIndexHonored}");
            return;
        }

        // ----- Bước 4: ảnh tại index bị hỏng/null -> áp dụng FallbackMode -----
        switch (_fallbackMode.Value)
        {
            case ImageFallbackMode.FirstAvailable:
                for (int i = 0; i < totalCount; i++)
                {
                    if (IsUsable(list[i]))
                    {
                        Emit(list[i]!, i, totalCount, false);
                        context.Log($"ImageSelector: fallback FirstAvailable -> index {i}");
                        return;
                    }
                }
                break;

            case ImageFallbackMode.LastAvailable:
                for (int i = totalCount - 1; i >= 0; i--)
                {
                    if (IsUsable(list[i]))
                    {
                        Emit(list[i]!, i, totalCount, false);
                        context.Log($"ImageSelector: fallback LastAvailable -> index {i}");
                        return;
                    }
                }
                break;

            case ImageFallbackMode.UseOriginal:
                if (IsUsable(list[0]))
                {
                    Emit(list[0]!, 0, totalCount, false);
                    context.Log("ImageSelector: fallback UseOriginal -> index 0");
                    return;
                }
                break;

            case ImageFallbackMode.CreateEmpty:
                EmitEmptyMat(totalCount);
                context.Log("ImageSelector: fallback CreateEmpty (200x200 black image)");
                return;
        }

        // ----- Bước 5: không còn ảnh nào dùng được trong toàn bộ danh sách -----
        EmitFallback(context, totalCount, "không tìm thấy ảnh hợp lệ nào trong danh sách");
    }

    /// <summary>Ảnh được coi là dùng được khi khác null và Mat bên trong không rỗng.</summary>
    private static bool IsUsable(IVisionImage? image) => image != null && !image.AsMat().Empty();

    private void Emit(IVisionImage source, int index, int totalCount, bool isValid)
    {
        _imageMatrix.Value = new MatVisionImage(source.AsMat().Clone()); // Clone để tool này không giữ tham chiếu sống chung với ảnh gốc trong ImageList
        _selectedIndex.Value = index;
        _totalCount.Value = totalCount;
        _isValidSelection.Value = isValid;
    }

    private void EmitEmptyMat(int totalCount)
    {
        var mat = new Mat(200, 200, MatType.CV_8UC3, Scalar.Black);
        _imageMatrix.Value = new MatVisionImage(mat);
        _selectedIndex.Value = -1; // Ảnh không đến từ danh sách -> không có chỉ số thực
        _totalCount.Value = totalCount;
        _isValidSelection.Value = false;
    }

    /// <summary>Danh sách rỗng hoặc không còn ảnh nào dùng được: tạo Placeholder nếu được phép, ngược lại báo lỗi dừng pipeline.</summary>
    private void EmitFallback(IToolContext context, int totalCount, string reason)
    {
        if (!_createPlaceholder.Value)
            throw new ToolExecutionException($"ImageSelector: {reason} và CreatePlaceholder=false.");

        var mat = new Mat(200, 200, MatType.CV_8UC3, new Scalar(128, 128, 128)); // Nền xám
        Cv2.PutText(mat, "No Images", new Point(20, 90), HersheyFonts.HersheySimplex, 0.6, Scalar.White, 1);
        Cv2.PutText(mat, "Available", new Point(20, 115), HersheyFonts.HersheySimplex, 0.6, Scalar.White, 1);

        _imageMatrix.Value = new MatVisionImage(mat);
        _selectedIndex.Value = -1;
        _totalCount.Value = totalCount;
        _isValidSelection.Value = false;

        context.Log($"ImageSelector: {reason} -> xuất ảnh placeholder.");
    }
}