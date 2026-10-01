// ==================== Vai trò chính:                Hiển thị (HMI) NHIỀU ảnh trong ImageList cùng lúc dưới dạng bảng lưới (grid/contact sheet)
// ==================== Thành phần / Class tiêu biểu: ImageListViewerTool
// ==================== Phụ thuộc vào:                OpenCvSharp
// ==================== Pattern / Kỹ thuật nổi bật:   Contact Sheet / Grid Compositing - ghép N ảnh nhỏ vào
//                       1 ảnh lớn theo lưới GridColumns cột, tự tính số hàng, giống thư viện ảnh thu nhỏ

using System;
using System.Collections.Generic;
using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;

namespace VisionFlow.Tools.Utility; // Cùng thư mục với ImageSelectorTool / ImageListIteratorTool

/// <summary>Bảng màu đơn giản cho BackgroundColor/BorderColor - đủ dùng cho khung hiển thị HMI, không cần chọn màu tự do.</summary>
public enum ViewerColor { Black, White, Gray, Red, Green, Blue }

/// <summary>Kích thước lưới hiển thị - Columns x Rows - để các khối UI khác biết cách sắp bố cục.</summary>
public readonly record struct GridSize(int Columns, int Rows);

/// <summary>
/// ImageListViewer: khác <see cref="ImageListIteratorTool"/> (xuất 1 ảnh/lần, phải duyệt tuần tự),
/// tool này ghép TẤT CẢ ảnh trong danh sách vào 1 ảnh lớn dạng lưới để xem toàn cảnh cùng lúc -
/// hữu ích khi cần soát nhanh kết quả của nhiều sản phẩm/nhiều vùng kiểm tra trong 1 lần nhìn,
/// thay vì bấm Next từng ảnh một.
/// </summary>
[ToolMetadata(
    "ImageListViewer",
    DisplayName = "Image List Viewer",
    Category = "Utility",
    Description = "Composite an ImageList into a single grid image (contact sheet) for quick visual review")]
public sealed class ImageListViewerTool : VisionTool
{
    #region 1. Khai báo Port (In/Out)
    private readonly InputPort<IReadOnlyList<IVisionImage>> _imageList;
    private readonly OutputPort<IVisionImage> _imageMatrix; // Ảnh lưới tổng hợp
    private readonly OutputPort<int> _imageCount;
    private readonly OutputPort<GridSize> _gridSize;
    #endregion

    #region 2. Khai báo Parameter (Tab Display)
    private readonly ToolParameter<bool> _autoResize;       // true: co giãn ảnh giữ tỉ lệ cho vừa khít ô | false: kéo giãn (stretch) đúng bằng ô, có thể méo tỉ lệ
    private readonly ToolParameter<ViewerColor> _backgroundColor;
    private readonly ToolParameter<ViewerColor> _borderColor;
    private readonly ToolParameter<int> _cellSize;          // Kích thước mỗi ô vuông (pixel)
    private readonly ToolParameter<int> _gridColumns;       // Số cột trong lưới - số hàng tự tính = ceil(count / columns)
    private readonly ToolParameter<bool> _showBorder;
    private readonly ToolParameter<bool> _showIndex;
    private readonly ToolParameter<int> _spacing;           // Khoảng cách giữa các ô (pixel)
    #endregion

    public ImageListViewerTool()
    {
        _imageList = AddInput<IReadOnlyList<IVisionImage>>("ImageList", "Image List");

        _imageMatrix = AddOutput<IVisionImage>("ImageMatrix", "Image Matrix");
        _imageCount = AddOutput<int>("ImageCount", "Image Count");
        _gridSize = AddOutput<GridSize>("GridSize", "Grid Size");

        _autoResize = AddParameter("AutoResize", true, "Auto Resize", category: "Display", order: 1);
        _backgroundColor = AddParameter("BackgroundColor", ViewerColor.Gray, "Background Color", category: "Display", order: 2);
        _borderColor = AddParameter("BorderColor", ViewerColor.White, "Border Color", category: "Display", order: 3);
        _cellSize = AddParameter("CellSize", 150, "Cell Size", min: 20, max: 1000, category: "Display", order: 4);
        _gridColumns = AddParameter("GridColumns", 4, "Grid Columns", min: 1, max: 50, category: "Display", order: 5);
        _showBorder = AddParameter("ShowBorder", true, "Show Border", category: "Display", order: 6);
        _showIndex = AddParameter("ShowIndex", true, "Show Index", category: "Display", order: 7);
        _spacing = AddParameter("Spacing", 4, "Spacing", min: 0, max: 100, category: "Display", order: 8);
    }

    protected override void OnExecute(IToolContext context)
    {
        var list = _imageList.Value;
        int count = list?.Count ?? 0;

        if (count == 0)
        {
            // Viewer là công cụ HMI hiển thị - không nên làm gián đoạn pipeline chỉ vì chưa có ảnh để xem,
            // nên xuất 1 canvas placeholder thay vì ném lỗi (khác triết lý fail-fast của ImageListIterator).
            var placeholder = new Mat(120, 240, MatType.CV_8UC3, ToScalar(_backgroundColor.Value));
            Cv2.PutText(placeholder, "No Images", new Point(20, 65), HersheyFonts.HersheySimplex, 0.7, Scalar.White, 2);
            _imageMatrix.Value = new MatVisionImage(placeholder);
            _imageCount.Value = 0;
            _gridSize.Value = new GridSize(0, 0);
            context.Log("ImageListViewer: ImageList is empty, showing placeholder.");
            return;
        }

        int cell = Math.Max(1, _cellSize.Value);
        int spacing = Math.Max(0, _spacing.Value);
        int columns = Math.Max(1, _gridColumns.Value);
        int rows = (int)Math.Ceiling(count / (double)columns);

        int canvasWidth = spacing + columns * (cell + spacing);
        int canvasHeight = spacing + rows * (cell + spacing);

        Mat canvas = new Mat(canvasHeight, canvasWidth, MatType.CV_8UC3, ToScalar(_backgroundColor.Value));
        Scalar borderColor = ToScalar(_borderColor.Value);

        for (int i = 0; i < count; i++)
        {
            int col = i % columns;
            int row = i / columns;
            int cellX = spacing + col * (cell + spacing);
            int cellY = spacing + row * (cell + spacing);
            var cellRect = new Rect(cellX, cellY, cell, cell);

            DrawThumbnail(canvas, list![i], cellRect);

            if (_showBorder.Value)
                Cv2.Rectangle(canvas, cellRect, borderColor, thickness: 1);

            if (_showIndex.Value)
            {
                string label = i.ToString();
                Cv2.Rectangle(canvas, new Rect(cellX, cellY, 14 + label.Length * 10, 18), Scalar.Black, thickness: -1);
                Cv2.PutText(canvas, label, new Point(cellX + 3, cellY + 14), HersheyFonts.HersheySimplex, 0.45, Scalar.White, 1);
            }
        }

        _imageMatrix.Value = new MatVisionImage(canvas);
        _imageCount.Value = count;
        _gridSize.Value = new GridSize(columns, rows);

        context.Log($"ImageListViewer: {count} image(s) -> grid {columns}x{rows}, cell={cell}px");
    }

    /// <summary>Vẽ 1 ảnh nhỏ (thumbnail) vào đúng ô cellRect trên canvas, tự chuyển ảnh xám sang BGR nếu cần.</summary>
    private void DrawThumbnail(Mat canvas, IVisionImage sourceImage, Rect cellRect)
    {
        Mat src = sourceImage.AsMat();
        if (src.Empty()) return; // Bỏ qua phần tử null/rỗng trong danh sách - vẫn giữ đúng ô trống cho index đó

        // Đưa về 3 kênh BGR để ghép chung canvas màu, tránh lỗi lệch số kênh khi CopyTo
        Mat bgr = src;
        bool isTemp = false;
        if (src.Channels() == 1)
        {
            bgr = new Mat();
            Cv2.CvtColor(src, bgr, ColorConversionCodes.GRAY2BGR);
            isTemp = true;
        }

        using Mat thumb = new Mat();
        if (_autoResize.Value)
        {
            // Co giãn GIỮ TỈ LỆ cho vừa khít trong ô, phần dư căn giữa (letterbox) - không làm méo hình vật thể
            double scale = Math.Min((double)cellRect.Width / bgr.Width, (double)cellRect.Height / bgr.Height);
            int w = Math.Max(1, (int)(bgr.Width * scale));
            int h = Math.Max(1, (int)(bgr.Height * scale));
            Cv2.Resize(bgr, thumb, new Size(w, h));

            int offsetX = cellRect.X + (cellRect.Width - w) / 2;
            int offsetY = cellRect.Y + (cellRect.Height - h) / 2;
            using Mat roi = new Mat(canvas, new Rect(offsetX, offsetY, w, h));
            thumb.CopyTo(roi);
        }
        else
        {
            // Không giữ tỉ lệ - kéo giãn (stretch) cho khớp đúng kích thước ô, đơn giản và lấp đầy hoàn toàn ô
            Cv2.Resize(bgr, thumb, new Size(cellRect.Width, cellRect.Height));
            using Mat roi = new Mat(canvas, cellRect);
            thumb.CopyTo(roi);
        }

        if (isTemp) bgr.Dispose();
    }

    private static Scalar ToScalar(ViewerColor color) => color switch
    {
        ViewerColor.Black => Scalar.Black,
        ViewerColor.White => Scalar.White,
        ViewerColor.Gray => new Scalar(128, 128, 128),
        ViewerColor.Red => new Scalar(0, 0, 255),   // OpenCV dùng thứ tự kênh BGR, không phải RGB
        ViewerColor.Green => new Scalar(0, 255, 0),
        ViewerColor.Blue => new Scalar(255, 0, 0),
        _ => Scalar.Black
    };
}