// ==================== Vai trò chính:                Phân vùng 1 vùng CỤ THỂ trong ảnh bằng cách lan tỏa (flood-fill có điều kiện) từ 1 điểm hạt giống (seed) đã biết trước
// ==================== Thành phần / Class tiêu biểu: RegionGrowingTool
// ==================== Phụ thuộc vào:                OpenCvSharp
// ==================== Pattern / Kỹ thuật nổi bật:   BFS flood-fill so sánh trực tiếp với giá trị SEED gốc
//                       (không so với pixel lân cận vừa lan tới) - giống "đổ 1 giọt sơn" lan ra tới khi gặp
//                       biên khác biệt đủ lớn so với màu sơn ban đầu

using System;
using System.Collections.Generic;
using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Models; // Point2d
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;
using Point2d = VisionFlow.Core.Models.Point2d;

namespace VisionFlow.Tools.Segmentation; // Cùng thư mục với ActiveContourTool / WatershedAlgorithmTool

/// <summary>Cách so sánh 1 pixel ứng viên với giá trị seed gốc.</summary>
public enum RegionGrowingComparisonMode
{
    Grayscale, // |pixel - seed| <= Threshold trên độ sáng đơn kênh - nhanh, phù hợp inspection công nghiệp
    Color      // Khoảng cách Euclidean trong không gian BGR <= Threshold - chậm hơn ~3 lần nhưng chính xác hơn với ảnh màu
}

/// <summary>Số hàng xóm xét trong mỗi bước lan tỏa.</summary>
public enum RegionGrowingConnectivity
{
    Four = 4, // Chỉ trên/dưới/trái/phải - lan chậm hơn, biên rõ hơn, chính xác hơn khi cần biên giới chặt chẽ
    Eight = 8 // Thêm cả 4 đường chéo - lan nhanh hơn, mượt mà hơn (mặc định)
}

/// <summary>
/// RegionGrowing: bắt đầu từ 1 điểm seed (SeedX, SeedY), lan dần ra các pixel lân cận nếu chúng đủ giống
/// seed (trong phạm vi Threshold) - giống "đổ 1 giọt sơn" lan tới khi gặp biên khác biệt (cạnh, vật khác).
/// Phù hợp khi người vận hành BIẾT CHÍNH XÁC 1 điểm thuộc vùng cần lấy và vùng đó có độ sáng/màu khá đồng
/// nhất. Khác WatershedAlgorithm (không cần biết trước vị trí, tách nhiều vật cùng lúc), RegionGrowing chỉ
/// tách ĐÚNG 1 vùng quanh 1 seed duy nhất mỗi lần chạy.
/// </summary>
[ToolMetadata(
    "RegionGrowing",
    DisplayName = "Region Growing",
    Category = "Segmentation",
    Description = "Grow a region outward from a seed pixel while neighbors stay within Threshold of the seed value")]
public sealed class RegionGrowingTool : VisionTool
{
    #region 1. Khai báo Port (In/Out)
    private readonly InputPort<IVisionImage> _imageMatrix;

    private readonly OutputPort<IVisionImage> _outImageMatrix;
    private readonly OutputPort<IVisionImage> _outRegionMask;
    private readonly OutputPort<double> _outRegionArea;
    private readonly OutputPort<Point2d> _outRegionCentroid;
    private readonly OutputPort<Rect> _outBoundingBox;
    private readonly OutputPort<IVisionImage> _outInteractiveVisualization;
    #endregion

    #region 2. Khai báo Parameter
    // --- Tab Seed ---
    private readonly ToolParameter<int> _seedX;
    private readonly ToolParameter<int> _seedY;

    // --- Tab Growth ---
    private readonly ToolParameter<double> _threshold;
    private readonly ToolParameter<RegionGrowingConnectivity> _connectivity;
    private readonly ToolParameter<RegionGrowingComparisonMode> _comparisonMode;

    // --- Tab Output ---
    private readonly ToolParameter<bool> _drawSeed;
    private readonly ToolParameter<bool> _drawBoundingBox;
    private readonly ToolParameter<bool> _drawContour;
    private readonly ToolParameter<bool> _outputAsColorImage;
    private readonly ToolParameter<double> _overlayAlpha; // CHỈ dùng cho UI hiển thị, KHÔNG ảnh hưởng pixel ảnh (đúng theo tài liệu) - xem ghi chú trong OnExecute
    #endregion

    public RegionGrowingTool()
    {
        _imageMatrix = AddInput<IVisionImage>("ImageMatrix", "Image Matrix");

        _outImageMatrix = AddOutput<IVisionImage>("ImageMatrix", "Image Matrix");
        _outRegionMask = AddOutput<IVisionImage>("RegionMask", "Region Mask");
        _outRegionArea = AddOutput<double>("RegionArea", "Region Area");
        _outRegionCentroid = AddOutput<Point2d>("RegionCentroid", "Region Centroid");
        _outBoundingBox = AddOutput<Rect>("BoundingBox", "Bounding Box");
        _outInteractiveVisualization = AddOutput<IVisionImage>("InteractiveVisualization", "Interactive Visualization");

        _seedX = AddParameter("SeedX", 0, "Seed X", min: 0, max: 100000, category: "Seed", order: 1);
        _seedY = AddParameter("SeedY", 0, "Seed Y", min: 0, max: 100000, category: "Seed", order: 2);

        _threshold = AddParameter("Threshold", 20.0, "Threshold", min: 0.0, max: 255.0, category: "Growth", order: 1);
        _connectivity = AddParameter("Connectivity", RegionGrowingConnectivity.Eight, "Connectivity", category: "Growth", order: 2);
        _comparisonMode = AddParameter("ComparisonMode", RegionGrowingComparisonMode.Grayscale, "Comparison Mode", category: "Growth", order: 3);

        _drawSeed = AddParameter("DrawSeed", true, "Draw Seed", category: "Output", order: 1);
        _drawBoundingBox = AddParameter("DrawBoundingBox", true, "Draw Bounding Box", category: "Output", order: 2);
        _drawContour = AddParameter("DrawContour", true, "Draw Contour", category: "Output", order: 3);
        _outputAsColorImage = AddParameter("OutputAsColorImage", true, "Output As Color Image", category: "Output", order: 4);
        _overlayAlpha = AddParameter("OverlayAlpha", 0.4, "Overlay Alpha", min: 0.0, max: 1.0, category: "Output", order: 5);
    }

    protected override void OnExecute(IToolContext context)
    {
        Mat src = _imageMatrix.Value!.AsMat();
        int width = src.Width, height = src.Height;

        int seedX = _seedX.Value, seedY = _seedY.Value;
        if (seedX < 0 || seedY < 0 || seedX >= width || seedY >= height)
            throw new ToolExecutionException("RegionGrowing: Seed outside image bounds.");

        bool colorMode = _comparisonMode.Value == RegionGrowingComparisonMode.Color;

        // ----- Bước 1: chuẩn bị dữ liệu so sánh theo đúng ComparisonMode -----
        // Grayscale: chỉ cần 1 kênh độ sáng. Color: cần đủ 3 kênh BGR để tính khoảng cách Euclidean.
        using Mat gray = colorMode ? new Mat() : (src.Channels() == 1 ? src.Clone() : ToGray(src));
        using Mat colorSrc = colorMode ? ToColor(src) : new Mat();

        double threshold = _threshold.Value;
        int connectivity = (int)_connectivity.Value;

        // ----- Bước 2: BFS flood-fill, luôn so sánh ứng viên với GIÁ TRỊ SEED GỐC (không so với hàng xóm vừa lan tới) -----
        using Mat mask = new Mat(height, width, MatType.CV_8UC1, Scalar.Black);
        var queue = new Queue<(int X, int Y)>();
        queue.Enqueue((seedX, seedY));
        mask.Set(seedY, seedX, (byte)255);

        // 8 hướng lân cận đầy đủ - khi Connectivity=4 chỉ dùng 4 phần tử đầu (trên/dưới/trái/phải)
        (int dx, int dy)[] neighbors8 =
        {
            (1, 0), (-1, 0), (0, 1), (0, -1),
            (1, 1), (1, -1), (-1, 1), (-1, -1)
        };
        int neighborCount = connectivity == 4 ? 4 : 8;

        long sumX = 0, sumY = 0, area = 0;
        int minX = seedX, maxX = seedX, minY = seedY, maxY = seedY;

        Vec3b seedColor = colorMode ? colorSrc.At<Vec3b>(seedY, seedX) : default;
        byte seedGray = colorMode ? (byte)0 : gray.At<byte>(seedY, seedX);

        while (queue.Count > 0)
        {
            var (x, y) = queue.Dequeue();

            // Thống kê ngay khi 1 pixel được CHẤP NHẬN vào vùng (đã đánh dấu mask=255 lúc Enqueue)
            sumX += x; sumY += y; area++;
            if (x < minX) minX = x; if (x > maxX) maxX = x;
            if (y < minY) minY = y; if (y > maxY) maxY = y;

            for (int i = 0; i < neighborCount; i++)
            {
                int nx = x + neighbors8[i].dx;
                int ny = y + neighbors8[i].dy;
                if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                if (mask.At<byte>(ny, nx) != 0) continue; // Đã ở trong vùng hoặc đã xét rồi

                bool accepted = colorMode
                    ? ColorDistance(colorSrc.At<Vec3b>(ny, nx), seedColor) <= threshold
                    : Math.Abs(gray.At<byte>(ny, nx) - (int)seedGray) <= threshold;

                if (accepted)
                {
                    mask.Set(ny, nx, (byte)255);
                    queue.Enqueue((nx, ny));
                }
            }
        }

        var centroid = new Point2d(area > 0 ? (double)sumX / area : seedX, area > 0 ? (double)sumY / area : seedY);
        var boundingBox = new Rect(minX, minY, maxX - minX + 1, maxY - minY + 1);

        // ----- Bước 3: dựng 2 lớp output ImageMatrix (nền ảnh gốc) + InteractiveVisualization (nền đen) -----
        Mat baseImage = _outputAsColorImage.Value ? ToColor(src) : src.Clone();
        Mat overlayOnBlack = new Mat(src.Size(), MatType.CV_8UC3, Scalar.Black);

        DrawAnnotations(baseImage, mask, seedX, seedY, boundingBox, centroid);
        DrawAnnotations(overlayOnBlack, mask, seedX, seedY, boundingBox, centroid);

        _outImageMatrix.Value = new MatVisionImage(baseImage);
        _outInteractiveVisualization.Value = new MatVisionImage(overlayOnBlack);
        _outRegionMask.Value = new MatVisionImage(mask.Clone());
        _outRegionArea.Value = area;
        _outRegionCentroid.Value = centroid;
        _outBoundingBox.Value = boundingBox;

        // Lưu ý: OverlayAlpha CHỦ ĐÍCH không được dùng ở đây - theo đúng tài liệu, tham số này chỉ phục vụ
        // việc hiển thị độ trong suốt overlay bên phía UI/Editor (khi chồng InteractiveVisualization lên ảnh
        // gốc để xem), KHÔNG làm thay đổi bất kỳ giá trị pixel nào trong các Mat mà Tool này tạo ra.

        context.Log($"RegionGrowing: seed=({seedX},{seedY}), mode={_comparisonMode.Value}, area={area}px, threshold={threshold}");
    }

    private void DrawAnnotations(Mat target, Mat mask, int seedX, int seedY, Rect boundingBox, Point2d centroid)
    {
        if (_drawContour.Value)
        {
            Cv2.FindContours(mask, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
            Cv2.DrawContours(target, contours, -1, new Scalar(0, 255, 255), thickness: 2); // Vàng - đúng màu tài liệu mô tả

            // Vẽ tâm cùng lúc với contour: tài liệu không có tham số DrawCentroid riêng trong Tab Output,
            // nhưng InteractiveVisualization luôn mô tả có "centroid (vàng)" - gộp chung điều kiện bật/tắt với DrawContour.
            Cv2.Circle(target, new Point((int)centroid.X, (int)centroid.Y), 4, new Scalar(0, 255, 255), thickness: -1);
        }

        if (_drawBoundingBox.Value)
            Cv2.Rectangle(target, boundingBox, new Scalar(0, 0, 255), thickness: 2); // Đỏ

        if (_drawSeed.Value)
            Cv2.Circle(target, new Point(seedX, seedY), 5, new Scalar(255, 0, 0), thickness: -1); // Xanh dương
    }

    private static double ColorDistance(Vec3b a, Vec3b b)
    {
        double db = a.Item0 - b.Item0, dg = a.Item1 - b.Item1, dr = a.Item2 - b.Item2;
        return Math.Sqrt(db * db + dg * dg + dr * dr);
    }

    private static Mat ToGray(Mat src)
    {
        var gray = new Mat();
        Cv2.CvtColor(src, gray, src.Channels() == 4 ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY);
        return gray;
    }

    private static Mat ToColor(Mat src)
    {
        if (src.Channels() == 3) return src.Clone();
        var color = new Mat();
        Cv2.CvtColor(src, color, src.Channels() == 1 ? ColorConversionCodes.GRAY2BGR : ColorConversionCodes.BGRA2BGR);
        return color;
    }
}