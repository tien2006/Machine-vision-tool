// ==================== Vai trò chính:                Cắt riêng từng vật thể ra khỏi ảnh gốc dựa trên danh sách contour đã có
// ==================== Thành phần / Class tiêu biểu: ExtractObjectsFromContoursTool, ExtractedObjectInfo
// ==================== Phụ thuộc vào:                OpenCvSharp + Core.Models (Point2d, RectRegion)
// ==================== Pattern / Kỹ thuật nổi bật:   ROI crop + mask theo contour (kỹ thuật giống InRangeTool.MaskedImage), Grid layout composite

using System;
using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Models;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;
using P2 = VisionFlow.Core.Models.Point2d;

namespace VisionFlow.Tools.Segmentation;

/// <summary>Thông tin thuộc tính hình học của 1 object đã trích xuất.</summary>
public sealed record ExtractedObjectInfo(double Area, double X, double Y, double Width, double Height, double Angle);

/// <summary>
/// Tool hậu xử lý: nhận ảnh gốc + danh sách contour (từ FindContoursTool) rồi cắt riêng từng vật thể
/// thành các ảnh nhỏ, kèm mask đúng hình dạng và bảng thuộc tính (diện tích/vị trí/kích thước/góc xoay).
/// </summary>
[ToolMetadata("ExtractObjectsFromContours", DisplayName = "Extract Objects From Contours", Category = "Segmentation",
    Description = "Crop individual objects from an image using pre-detected contours")]
public sealed class ExtractObjectsFromContoursTool : VisionTool
{
    #region 1. Khai báo Port (In/Out)
    private readonly InputPort<IVisionImage> _input;         // Cổng vào: ảnh gốc (nguồn để cắt)
    private readonly InputPort<P2[][]> _inContours;          // Cổng vào: danh sách contour (nhận từ FindContoursTool.AllContours)

    private readonly OutputPort<IVisionImage> _outImage;              // Ảnh gốc đã annotate contour/minAreaRect/label
    private readonly OutputPort<IVisionImage[]> _outExtractedObjects; // Danh sách ảnh nhỏ, mỗi object 1 ảnh
    private readonly OutputPort<IVisionImage[]> _outObjectMasks;      // Danh sách mask nhị phân tương ứng
    private readonly OutputPort<int> _outObjectCount;                 // Tổng số object trích xuất được
    private readonly OutputPort<IVisionImage> _outCombinedImage;      // Ảnh ghép tất cả object (dạng lưới hoặc dải ngang)
    private readonly OutputPort<ExtractedObjectInfo[]> _outObjectInfo; // Bảng thuộc tính từng object
    private readonly OutputPort<IVisionImage?> _outLargestObject;     // Object có diện tích lớn nhất
    private readonly OutputPort<IVisionImage?> _outSmallestObject;    // Object có diện tích nhỏ nhất
    #endregion

    #region 2. Khai báo Parameter theo từng Tab
    // ----- Tab Filtering -----
    private readonly ToolParameter<double> _minObjectArea;
    private readonly ToolParameter<double> _maxObjectArea;

    // ----- Tab Extraction -----
    private readonly ToolParameter<string> _backgroundColor; // "Black"/"White" - rút gọn so với color-picker đầy đủ trong tài liệu
    private readonly ToolParameter<int> _padding;
    private readonly ToolParameter<string> _sortMode;
    private readonly ToolParameter<int> _maxObjects;

    // ----- Tab Output -----
    private readonly ToolParameter<bool> _drawContoursOnOutput;
    private readonly ToolParameter<bool> _drawMinAreaRect;
    private readonly ToolParameter<bool> _drawLabels;
    private readonly ToolParameter<bool> _outputAsColorImage;
    private readonly ToolParameter<bool> _buildCombinedGrid;
    #endregion

    public ExtractObjectsFromContoursTool()
    {
        // ----- Port -----
        _input = AddInput<IVisionImage>("Image");
        _inContours = AddInput<P2[][]>("AllContours");

        _outImage = AddOutput<IVisionImage>("Image");
        _outExtractedObjects = AddOutput<IVisionImage[]>("ExtractedObjects");
        _outObjectMasks = AddOutput<IVisionImage[]>("ObjectMasks");
        _outObjectCount = AddOutput<int>("ObjectCount");
        _outCombinedImage = AddOutput<IVisionImage>("CombinedImage");
        _outObjectInfo = AddOutput<ExtractedObjectInfo[]>("ObjectInfo");
        _outLargestObject = AddOutput<IVisionImage?>("LargestObject");
        _outSmallestObject = AddOutput<IVisionImage?>("SmallestObject");

        // ----- Tab Filtering -----
        _minObjectArea = AddParameter("MinObjectArea", 50.0, "Min Object Area", 0.0, 10_000_000.0, category: "Filtering", order: 1);
        _maxObjectArea = AddParameter("MaxObjectArea", 1_000_000.0, "Max Object Area", 0.0, 10_000_000.0, category: "Filtering", order: 2);

        // ----- Tab Extraction -----
        _backgroundColor = AddChoiceParameter("BackgroundColor", "Black", new[] { "Black", "White" }, "Background Color", category: "Extraction", order: 1);
        _padding = AddParameter("Padding", 5, "Padding", 0, 200, category: "Extraction", order: 2);
        _sortMode = AddChoiceParameter("SortMode", "AreaDescending",
            new[] { "None", "AreaDescending", "AreaAscending", "PositionXY" }, "Sort Mode", category: "Extraction", order: 3);
        _maxObjects = AddParameter("MaxObjects", 100, "Max Objects", 1, 10_000, category: "Extraction", order: 4);

        // ----- Tab Output -----
        _drawContoursOnOutput = AddParameter("DrawContoursOnOutput", true, "Draw Contours On Output", category: "Output", order: 1);
        _drawMinAreaRect = AddParameter("DrawMinAreaRect", false, "Draw MinArea Rect", category: "Output", order: 2);
        _drawLabels = AddParameter("DrawLabels", true, "Draw Labels", category: "Output", order: 3);
        _outputAsColorImage = AddParameter("OutputAsColorImage", true, "Output As Color Image", category: "Output", order: 4);
        _buildCombinedGrid = AddParameter("BuildCombinedGrid", true, "Build Combined Grid", category: "Output", order: 5);
    }

    protected override void OnExecute(IToolContext context)
    {
        Mat src = _input.Value!.AsMat();
        P2[][] contoursIn = _inContours.Value ?? Array.Empty<P2[]>();

        // ----- Bước 1: Chuyển P2[][] (kiểu hệ thống) ngược lại Point[][] (kiểu OpenCV) để tính toán -----
        var contoursCv = contoursIn
            .Select(c => c.Select(p => new Point((int)p.X, (int)p.Y)).ToArray())
            .Where(c => c.Length >= 3) // Bỏ qua contour suy biến, không đủ điểm tạo vùng
            .ToList();

        // ----- Bước 2: Lọc theo diện tích (Tab Filtering) -----
        var candidates = contoursCv
            .Select(c => (Points: c, Area: Cv2.ContourArea(c)))
            .Where(x => x.Area >= _minObjectArea.Value && x.Area <= _maxObjectArea.Value)
            .ToList();

        // ----- Bước 3: Sắp xếp + giới hạn MaxObjects (Tab Extraction) -----
        var sorted = _sortMode.Value switch
        {
            "AreaAscending" => candidates.OrderBy(x => x.Area),
            "PositionXY" => candidates.OrderBy(x => Cv2.BoundingRect(x.Points).Y).ThenBy(x => Cv2.BoundingRect(x.Points).X),
            "None" => (IOrderedEnumerable<(Point[] Points, double Area)>)candidates.AsEnumerable().OrderBy(_ => 0),
            _ => candidates.OrderByDescending(x => x.Area),
        };
        var finalList = sorted.Take(Math.Max(1, _maxObjects.Value)).ToList();

        // ----- Bước 4: Cắt từng object (crop + mask + info) -----
        Scalar bgColor = _backgroundColor.Value == "White" ? Scalar.All(255) : Scalar.All(0);
        var extractedObjects = new List<Mat>();
        var objectMasks = new List<Mat>();
        var infoList = new List<ExtractedObjectInfo>();
        int padding = Math.Max(0, _padding.Value);

        foreach (var (points, area) in finalList)
        {
            Rect bbox = Cv2.BoundingRect(points);

            // Mở rộng bbox thêm Padding mỗi phía, đồng thời kẹp biên để không vượt ra ngoài ảnh gốc
            int x0 = Math.Max(0, bbox.X - padding);
            int y0 = Math.Max(0, bbox.Y - padding);
            int x1 = Math.Min(src.Cols, bbox.X + bbox.Width + padding);
            int y1 = Math.Min(src.Rows, bbox.Y + bbox.Height + padding);
            Rect paddedRect = new Rect(x0, y0, x1 - x0, y1 - y0);
            if (paddedRect.Width <= 0 || paddedRect.Height <= 0) continue; // Phòng thủ: bbox suy biến

            // Cắt ảnh gốc theo vùng đã padding (Clone() để có bộ nhớ độc lập, không share với "src")
            Mat crop = new Mat(src, paddedRect).Clone();

            // Dựng mask cùng kích thước "crop": vẽ contour đã dịch tọa độ về hệ tọa độ cục bộ của vùng crop
            Mat mask = Mat.Zeros(crop.Size(), MatType.CV_8UC1);
            var shifted = points.Select(p => new Point(p.X - paddedRect.X, p.Y - paddedRect.Y)).ToArray();
            Cv2.DrawContours(mask, new[] { shifted }, -1, Scalar.White, -1); // -1 = tô kín (filled)

            // Ghép object với nền BackgroundColor: tương tự kỹ thuật MaskedImage của InRangeTool
            Mat masked = new Mat(crop.Size(), crop.Type(), bgColor);
            crop.CopyTo(masked, mask);
            crop.Dispose();

            RotatedRect minRect = Cv2.MinAreaRect(points);
            infoList.Add(new ExtractedObjectInfo(area, bbox.X, bbox.Y, bbox.Width, bbox.Height, minRect.Angle));

            extractedObjects.Add(masked);
            objectMasks.Add(mask);
        }

        // ----- Bước 5: Xác định Largest/Smallest object -----
        Mat? largest = null, smallest = null;
        if (extractedObjects.Count > 0)
        {
            int maxIdx = infoList.Select((info, i) => (info.Area, i)).OrderByDescending(x => x.Area).First().i;
            int minIdx = infoList.Select((info, i) => (info.Area, i)).OrderBy(x => x.Area).First().i;
            largest = extractedObjects[maxIdx];
            smallest = extractedObjects[minIdx];
        }

        // ----- Bước 6: Vẽ overlay lên ảnh gốc (Tab Output) -----
        Mat overlay = new Mat();
        if (_outputAsColorImage.Value && src.Channels() == 1) Cv2.CvtColor(src, overlay, ColorConversionCodes.GRAY2BGR);
        else overlay = src.Clone();

        int idx = 1;
        foreach (var (points, _) in finalList)
        {
            if (_drawContoursOnOutput.Value)
                Cv2.DrawContours(overlay, new[] { points }, -1, Scalar.Yellow, 2);
            if (_drawMinAreaRect.Value)
            {
                RotatedRect rr = Cv2.MinAreaRect(points);
                Point2f[] box = rr.Points();
                for (int i = 0; i < 4; i++) Cv2.Line(overlay, (Point)box[i], (Point)box[(i + 1) % 4], Scalar.Magenta, 1);
            }
            if (_drawLabels.Value)
            {
                Rect bbox = Cv2.BoundingRect(points);
                Cv2.PutText(overlay, $"#{idx}", new Point(bbox.X, bbox.Y - 5), HersheyFonts.HersheySimplex, 0.5, Scalar.LimeGreen, 1);
            }
            idx++;
        }

        // ----- Bước 7: Ghép ảnh tổng hợp CombinedImage (Tab Output: BuildCombinedGrid) -----
        Mat combined = BuildCombinedImage(extractedObjects, _buildCombinedGrid.Value);

        // ----- Bước 8: Đẩy kết quả ra cổng output -----
        _outImage.Value = new MatVisionImage(overlay);
        _outExtractedObjects.Value = extractedObjects.Select(m => (IVisionImage)new MatVisionImage(m)).ToArray();
        _outObjectMasks.Value = objectMasks.Select(m => (IVisionImage)new MatVisionImage(m)).ToArray();
        _outObjectCount.Value = extractedObjects.Count;
        _outCombinedImage.Value = new MatVisionImage(combined);
        _outObjectInfo.Value = infoList.ToArray();
        _outLargestObject.Value = largest != null ? new MatVisionImage(largest.Clone()) : null;
        _outSmallestObject.Value = smallest != null ? new MatVisionImage(smallest.Clone()) : null;

        context.Log($"ExtractObjectsFromContours: {contoursCv.Count} input contours -> {extractedObjects.Count} objects extracted.");
    }

    /// <summary>Ghép danh sách ảnh nhỏ thành 1 ảnh tổng hợp: dạng lưới (grid, tự tính số cột ~sqrt(N)) hoặc dải ngang đơn giản.</summary>
    private static Mat BuildCombinedImage(List<Mat> objects, bool asGrid)
    {
        if (objects.Count == 0) return new Mat(64, 64, MatType.CV_8UC3, Scalar.All(0)); // Ảnh rỗng nếu không có object nào

        const int thumbSize = 128; // Chuẩn hóa mỗi object về ô vuông 128x128 để ghép lưới đều nhau
        var thumbs = objects.Select(o =>
        {
            Mat colorObj = o.Channels() == 1 ? new Mat() : o;
            if (o.Channels() == 1) Cv2.CvtColor(o, colorObj, ColorConversionCodes.GRAY2BGR);
            Mat thumb = new Mat();
            Cv2.Resize(colorObj, thumb, new Size(thumbSize, thumbSize)); // Resize méo tỉ lệ chấp nhận được cho mục đích xem nhanh
            if (o.Channels() == 1) colorObj.Dispose();
            return thumb;
        }).ToList();

        int cols = asGrid ? Math.Max(1, (int)Math.Ceiling(Math.Sqrt(thumbs.Count))) : thumbs.Count; // Grid: gần vuông; Strip: 1 hàng ngang
        int rows = (int)Math.Ceiling((double)thumbs.Count / cols);

        Mat combined = new Mat(rows * thumbSize, cols * thumbSize, MatType.CV_8UC3, Scalar.All(0));
        for (int i = 0; i < thumbs.Count; i++)
        {
            int r = i / cols, c = i % cols;
            Rect cell = new Rect(c * thumbSize, r * thumbSize, thumbSize, thumbSize);
            thumbs[i].CopyTo(new Mat(combined, cell));
            thumbs[i].Dispose();
        }
        return combined;
    }
}