// ==================== Vai trò chính:                Đo lường/thống kê phân phối độ sáng của ảnh (không sửa ảnh) - "khám bệnh" trước khi chọn tool xử lý
// ==================== Thành phần / Class tiêu biểu: HistogramAnalysisTool
// ==================== Phụ thuộc vào:                OpenCvSharp
// ==================== Pattern / Kỹ thuật nổi bật:   Cv2.CalcHist theo từng kênh, tự vẽ biểu đồ histogram bằng Cv2.Line/Polylines

using System;
using System.Linq;
using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;

namespace VisionFlow.Tools.Histogram;

/// <summary>
/// Tool "khám bệnh" cho ảnh: không sửa ảnh, chỉ đo và trả về các con số/biểu đồ mô tả phân phối
/// độ sáng - dùng để quyết định nên chọn BrightnessContrast, CLAHE hay GammaCorrection tiếp theo.
/// </summary>
[ToolMetadata("HistogramAnalysis", DisplayName = "Histogram Analysis", Category = "Histogram",
    Description = "Analyze pixel intensity distribution without modifying the image")]
public sealed class HistogramAnalysisTool : VisionTool
{
    #region 1. Khai báo Port (In/Out) - Tool này KHÔNG có Parameter (tự động hoàn toàn theo tài liệu)
    private readonly InputPort<IVisionImage> _input;
    private readonly OutputPort<IVisionImage> _outImage;          // Ảnh gốc pass-through (clone)
    private readonly OutputPort<IVisionImage> _outHistogramChart; // Biểu đồ trực quan 512x400
    private readonly OutputPort<int[]> _outRedHistogram;
    private readonly OutputPort<int[]> _outGreenHistogram;
    private readonly OutputPort<int[]> _outBlueHistogram;
    private readonly OutputPort<int[]> _outGrayHistogram;
    private readonly OutputPort<bool> _outIsColor;
    private readonly OutputPort<double> _outMeanValue;
    private readonly OutputPort<double> _outStdDev;
    private readonly OutputPort<double> _outMinValue;
    private readonly OutputPort<double> _outMaxValue;
    #endregion

    public HistogramAnalysisTool()
    {
        _input = AddInput<IVisionImage>("Image");
        _outImage = AddOutput<IVisionImage>("Image");
        _outHistogramChart = AddOutput<IVisionImage>("HistogramChart");
        _outRedHistogram = AddOutput<int[]>("RedHistogram");
        _outGreenHistogram = AddOutput<int[]>("GreenHistogram");
        _outBlueHistogram = AddOutput<int[]>("BlueHistogram");
        _outGrayHistogram = AddOutput<int[]>("GrayHistogram");
        _outIsColor = AddOutput<bool>("IsColor");
        _outMeanValue = AddOutput<double>("MeanValue");
        _outStdDev = AddOutput<double>("StdDev");
        _outMinValue = AddOutput<double>("MinValue");
        _outMaxValue = AddOutput<double>("MaxValue");
    }

    protected override void OnExecute(IToolContext context)
    {
        Mat src = _input.Value!.AsMat();
        bool isColor = src.Channels() == 3;

        // ----- Bước 1: Tính histogram từng kênh liên quan -----
        int[]? redHist = null, greenHist = null, blueHist = null, grayHist = null;
        if (isColor)
        {
            Mat[] channels = Cv2.Split(src); // OpenCV trả về mảng theo thứ tự lưu trữ BGR: [0]=Blue, [1]=Green, [2]=Red
            blueHist = ComputeHistogram(channels[0]);
            greenHist = ComputeHistogram(channels[1]);
            redHist = ComputeHistogram(channels[2]);
            foreach (var ch in channels) ch.Dispose();
        }
        else
        {
            grayHist = ComputeHistogram(src);
        }

        // ----- Bước 2: Tính Mean/StdDev/Min/Max trên phiên bản GRAYSCALE (đại diện "độ sáng tổng thể") -----
        Mat grayForStats; bool grayTemp = false;
        if (src.Channels() == 1) { grayForStats = src; }
        else { grayForStats = new Mat(); Cv2.CvtColor(src, grayForStats, ColorConversionCodes.BGR2GRAY); grayTemp = true; }

        Cv2.MeanStdDev(grayForStats, out Scalar mean, out Scalar stddev);
        Cv2.MinMaxLoc(grayForStats, out double minVal, out double maxVal);
        if (grayTemp) grayForStats.Dispose();

        // ----- Bước 3: Vẽ biểu đồ histogram trực quan 512x400 -----
        Mat chart = DrawHistogramChart(redHist, greenHist, blueHist, grayHist, isColor);

        // ----- Bước 4: Đẩy kết quả ra cổng output -----
        _outImage.Value = new MatVisionImage(src.Clone()); // Clone để pass-through độc lập, không ảnh hưởng ảnh gốc downstream
        _outHistogramChart.Value = new MatVisionImage(chart);
        _outRedHistogram.Value = redHist ?? Array.Empty<int>();
        _outGreenHistogram.Value = greenHist ?? Array.Empty<int>();
        _outBlueHistogram.Value = blueHist ?? Array.Empty<int>();
        _outGrayHistogram.Value = grayHist ?? Array.Empty<int>();
        _outIsColor.Value = isColor;
        _outMeanValue.Value = mean.Val0;
        _outStdDev.Value = stddev.Val0;
        _outMinValue.Value = minVal;
        _outMaxValue.Value = maxVal;

        context.Log($"HistogramAnalysis: Mean={mean.Val0:F1}, StdDev={stddev.Val0:F1}, Min={minVal}, Max={maxVal}, IsColor={isColor}");
    }

    /// <summary>Tính histogram 256-bin của 1 kênh ảnh 8-bit, trả về mảng int[256] (số pixel ứng với mỗi mức xám).</summary>
    private static int[] ComputeHistogram(Mat channel)
    {
        Mat hist = new Mat();
        Cv2.CalcHist(new[] { channel }, new[] { 0 }, null, hist, 1, new[] { 256 }, new[] { new Rangef(0, 256) });
        var result = new int[256];
        for (int i = 0; i < 256; i++) result[i] = (int)hist.At<float>(i); // CalcHist trả về kiểu float, ép sang int (số pixel nguyên)
        hist.Dispose();
        return result;
    }

    /// <summary>Vẽ ảnh biểu đồ 512x400: trục/grid/tiêu đề + đường cong histogram (1-3 kênh tùy IsColor).</summary>
    private static Mat DrawHistogramChart(int[]? red, int[]? green, int[]? blue, int[]? gray, bool isColor)
    {
        const int W = 512, H = 400, ChartTop = 40, ChartBottom = 370, ChartLeft = 40, ChartRight = 500;
        Mat chart = new Mat(H, W, MatType.CV_8UC3, Scalar.All(20)); // Nền xám đậm cho dễ nhìn đường cong

        // Lưới ngang mỗi 32 mức xám (8 vạch) để tiện đọc giá trị
        for (int gx = 0; gx <= 256; gx += 32)
        {
            int x = ChartLeft + (int)((double)gx / 255 * (ChartRight - ChartLeft));
            Cv2.Line(chart, new Point(x, ChartTop), new Point(x, ChartBottom), Scalar.All(60), 1);
            Cv2.PutText(chart, gx.ToString(), new Point(x - 8, ChartBottom + 15), HersheyFonts.HersheySimplex, 0.35, Scalar.White, 1);
        }
        Cv2.PutText(chart, "Histogram Analysis", new Point(ChartLeft, 20), HersheyFonts.HersheySimplex, 0.6, Scalar.White, 1);

        // Tìm giá trị lớn nhất trong TẤT CẢ các mảng histogram sẽ vẽ để chuẩn hóa chiều cao (tránh đường cong tràn khung)
        int maxCount = 1;
        foreach (var h in new[] { red, green, blue, gray })
            if (h != null) maxCount = Math.Max(maxCount, h.Skip(1).DefaultIfEmpty(0).Max()); // Bỏ bin đầu (thường bị dồn pixel đen tuyệt đối, làm lệch tỉ lệ)

        void DrawCurve(int[]? h, Scalar color)
        {
            if (h == null) return;
            var pts = new Point[256];
            for (int i = 0; i < 256; i++)
            {
                int x = ChartLeft + (int)((double)i / 255 * (ChartRight - ChartLeft));
                int y = ChartBottom - (int)((double)h[i] / maxCount * (ChartBottom - ChartTop));
                pts[i] = new Point(x, y);
            }
            Cv2.Polylines(chart, new[] { pts }, false, color, 1);
        }

        if (isColor) { DrawCurve(blue, Scalar.DodgerBlue); DrawCurve(green, Scalar.LimeGreen); DrawCurve(red, Scalar.OrangeRed); }
        else DrawCurve(gray, Scalar.White);

        return chart;
    }
}