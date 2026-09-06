// ==================== Vai trò chính:                Helper dùng chung cho nhóm Color: xử lý ROI, quy đổi màu mục tiêu, lấy mẫu màu, lọc pixel đen/trắng
// ==================== Thành phần / Class tiêu biểu: ColorUtil
// ==================== Phụ thuộc vào:                OpenCvSharp
// ==================== Pattern / Kỹ thuật nổi bật:   Static helper dùng chung giữa 6 file cùng namespace (giống CaliperUtil, GeometryColors)

using OpenCvSharp;
using System;
using VisionFlow.Core.Tools;

namespace VisionFlow.Tools.Color;

internal static class ColorUtil
{
    /// <summary>Trả về vùng ROI đã kẹp biên hợp lệ, hoặc toàn bộ ảnh nếu UseROI=false.</summary>
    public static Rect ResolveRoi(Mat src, bool useRoi, int x, int y, int w, int h)
    {
        if (!useRoi) return new Rect(0, 0, src.Cols, src.Rows);
        int rx = Math.Clamp(x, 0, src.Cols - 1);
        int ry = Math.Clamp(y, 0, src.Rows - 1);
        int rw = Math.Clamp(w, 1, src.Cols - rx);
        int rh = Math.Clamp(h, 1, src.Rows - ry);
        return new Rect(rx, ry, rw, rh);
    }

    /// <summary>Chuyển 3 giá trị R/G/B (do người dùng nhập theo thứ tự quen thuộc) thành Scalar BGR mà OpenCV dùng nội bộ.</summary>
    public static Scalar RgbToBgrScalar(double r, double g, double b) => new Scalar(b, g, r);

    /// <summary>Chuyển Hue(0-360)/Saturation(0-100%)/Value(0-100%) thành Scalar BGR bằng cách convert qua Mat 1x1.</summary>
    public static Scalar HsvToBgrScalar(double hue360, double satPct, double valPct)
    {
        using Mat hsv = new Mat(1, 1, MatType.CV_8UC3, new Scalar(hue360 / 2.0, satPct * 2.55, valPct * 2.55)); // /2 vì OpenCV Hue chạy 0-180
        using Mat bgr = new Mat();
        Cv2.CvtColor(hsv, bgr, ColorConversionCodes.HSV2BGR);
        Vec3b px = bgr.At<Vec3b>(0, 0);
        return new Scalar(px.Item0, px.Item1, px.Item2);
    }

    /// <summary>Lấy màu trung bình của 1 vùng vuông SampleSize x SampleSize quanh điểm (x,y), phục vụ "Sample from Image".</summary>
    public static Vec3b SamplePatchMean(Mat bgr, int x, int y, int size)
    {
        int half = Math.Max(1, size) / 2;
        int x0 = Math.Clamp(x - half, 0, bgr.Cols - 1);
        int y0 = Math.Clamp(y - half, 0, bgr.Rows - 1);
        int w = Math.Clamp(size, 1, bgr.Cols - x0);
        int h = Math.Clamp(size, 1, bgr.Rows - y0);
        using Mat patch = new Mat(bgr, new Rect(x0, y0, w, h));
        Scalar mean = Cv2.Mean(patch);
        return new Vec3b((byte)mean.Val0, (byte)mean.Val1, (byte)mean.Val2);
    }

    /// <summary>Dựng mask "giữ lại" (255=giữ, 0=loại) dựa trên IgnoreBlack/IgnoreWhite, dùng lọc trước khi thống kê màu.</summary>
    public static Mat BuildIgnoreMask(Mat bgr, bool ignoreBlack, bool ignoreWhite)
    {
        using Mat gray = new Mat();
        Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
        Mat keep = new Mat(gray.Size(), MatType.CV_8UC1, Scalar.All(255));

        if (ignoreBlack)
        {
            using Mat notBlack = new Mat();
            Cv2.Threshold(gray, notBlack, 15, 255, ThresholdTypes.Binary); // gray>15 -> 255 (không phải điểm đen)
            Cv2.BitwiseAnd(keep, notBlack, keep);
        }
        if (ignoreWhite)
        {
            using Mat notWhite = new Mat();
            Cv2.Threshold(gray, notWhite, 240, 255, ThresholdTypes.BinaryInv); // gray<240 -> 255 (không phải điểm trắng)
            Cv2.BitwiseAnd(keep, notWhite, keep);
        }
        return keep;
    }

    /// <summary>Khoảng cách Hue theo vòng tròn (OpenCV Hue chạy 0-180, không phải 0-360) - tránh sai số ở biên 0/180.</summary>
    public static double HueDistance180(double h1, double h2)
    {
        double d = Math.Abs(h1 - h2);
        return Math.Min(d, 180.0 - d);
    }

    /// <summary>Đảm bảo ảnh đầu vào là 3 kênh BGR - nếu lỡ nhận ảnh xám thì tự convert, kèm log cảnh báo.</summary>
    public static Mat EnsureBgr(Mat src, IToolContext context, string toolName)
    {
        if (src.Channels() == 3) return src;
        context.Log($"{toolName}: ảnh đầu vào không phải BGR 3 kênh -> tự convert từ grayscale.");
        Mat bgr = new Mat();
        Cv2.CvtColor(src, bgr, ColorConversionCodes.GRAY2BGR);
        return bgr;
    }
}