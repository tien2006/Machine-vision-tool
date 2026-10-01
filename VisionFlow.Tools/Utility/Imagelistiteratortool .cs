// ==================== Vai trò chính:                Duyệt LẦN LƯỢT qua TẤT CẢ ảnh trong ImageList, mỗi lần Execute() xuất ra 1 ảnh
// ==================== Thành phần / Class tiêu biểu: ImageListIteratorTool
// ==================== Phụ thuộc vào:                OpenCvSharp, System.Diagnostics (Stopwatch cho AutoAdvance)
// ==================== Pattern / Kỹ thuật nổi bật:   Tool CÓ NHỚ (stateful) hiếm hoi trong hệ thống - chỉ số
//                       hiện tại được lưu giữa các lần Execute() liên tiếp, khác hầu hết Tool khác (vốn không
//                       trạng thái, tính toán lại từ đầu mỗi lần chạy).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using OpenCvSharp;
using VisionFlow.Core.Imaging;
using VisionFlow.Core.Ports;
using VisionFlow.Core.Tools;
using VisionFlow.Tools.Imaging;

namespace VisionFlow.Tools.Utility; // Cùng thư mục với ImageSelectorTool

/// <summary>Cách điều khiển việc chuyển ảnh.</summary>
public enum IterationMode
{
    Manual,      // Chỉ số điều khiển trực tiếp qua tham số CurrentIndex (đổi tay hoặc qua public API GoToImage)
    AutoAdvance, // Tự động chuyển ảnh sau mỗi khoảng AdvanceDelay (ms) - cần bật thêm cờ AutoAdvance=true
    Triggered    // Chỉ chuyển ảnh khi NextTrigger=true đúng lần Execute() đó - đồng bộ nhịp băng chuyền/PLC
}

/// <summary>
/// ImageListIterator: duyệt qua TOÀN BỘ ảnh trong 1 danh sách, mỗi lần chạy pipeline xuất ra 1 ảnh kèm
/// overlay số thứ tự + thanh tiến độ. Khác <see cref="ImageSelectorTool"/> (luôn lấy 1 ảnh CỐ ĐỊNH),
/// Iterator tự động tiến tới ảnh tiếp theo qua các lần Execute() kế tiếp - phù hợp khi cần xử lý/đo lần
/// lượt từng vật trong loạt vật được cắt ra (VD ExtractObjectsFromContours cắt 10 con ốc -> đo hết 10 con).
/// </summary>
[ToolMetadata(
    "ImageListIterator",
    DisplayName = "Image List Iterator",
    Category = "Utility",
    Description = "Iterate through all images in an ImageList, one image per Execute() call, with index/progress overlay")]
public sealed class ImageListIteratorTool : VisionTool
{
    #region 1. Khai báo Port (In/Out)
    private readonly InputPort<IReadOnlyList<IVisionImage>> _imageList; // Danh sách ảnh cần duyệt
    private readonly InputPort<bool> _nextTrigger;                      // Tín hiệu chuyển ảnh kế tiếp (mode Triggered)
    private readonly InputPort<bool> _resetTrigger;                     // Tín hiệu quay về ảnh đầu tiên (mọi mode)

    private readonly OutputPort<IVisionImage> _imageMatrix;
    private readonly OutputPort<int> _outCurrentIndex;
    private readonly OutputPort<int> _outTotalCount;
    private readonly OutputPort<bool> _outHasNext;
    private readonly OutputPort<bool> _outHasPrevious;
    private readonly OutputPort<bool> _outIsFirst;
    private readonly OutputPort<bool> _outIsLast;
    private readonly OutputPort<double> _outProgress; // 0-100
    #endregion

    #region 2. Khai báo Parameter (Tab Iteration)
    private readonly ToolParameter<IterationMode> _iterationMode;
    private readonly ToolParameter<int> _currentIndex;      // Mode Manual: người dùng đổi trực tiếp tham số này để nhảy ảnh
    private readonly ToolParameter<bool> _autoAdvanceEnabled; // Cờ bật/tắt riêng - CHỈ có tác dụng khi IterationMode = AutoAdvance (như nút Play/Pause)
    private readonly ToolParameter<bool> _loopMode;          // true: hết danh sách quay lại đầu | false: dừng ở ảnh cuối
    private readonly ToolParameter<int> _advanceDelay;       // Mili giây giữa 2 lần tự chuyển ảnh (mode AutoAdvance)
    private readonly ToolParameter<bool> _showProgress;      // Vẽ thanh tiến độ xanh lá góc trên-phải
    private readonly ToolParameter<bool> _showIndex;         // Vẽ chữ số thứ tự "3/10" góc trên-trái
    #endregion

    #region 3. Trạng thái nội bộ (stateful - lưu giữa các lần Execute())
    private int _cursorIndex;                 // Chỉ số đang đứng, dùng cho mode AutoAdvance/Triggered
    private IReadOnlyList<IVisionImage>? _lastListRef; // Phát hiện danh sách bị thay đổi (upstream tạo batch mới) -> reset về 0
    private readonly Stopwatch _autoAdvanceWatch = Stopwatch.StartNew();
    #endregion

    public ImageListIteratorTool()
    {
        _imageList = AddInput<IReadOnlyList<IVisionImage>>("ImageList", "Image List");
        _nextTrigger = AddInput<bool>("NextTrigger", "Next Trigger", optional: true);
        _resetTrigger = AddInput<bool>("ResetTrigger", "Reset Trigger", optional: true);

        _imageMatrix = AddOutput<IVisionImage>("ImageMatrix", "Image Matrix");
        _outCurrentIndex = AddOutput<int>("CurrentIndex", "Current Index");
        _outTotalCount = AddOutput<int>("TotalCount", "Total Count");
        _outHasNext = AddOutput<bool>("HasNext", "Has Next");
        _outHasPrevious = AddOutput<bool>("HasPrevious", "Has Previous");
        _outIsFirst = AddOutput<bool>("IsFirst", "Is First");
        _outIsLast = AddOutput<bool>("IsLast", "Is Last");
        _outProgress = AddOutput<double>("Progress", "Progress");

        _iterationMode = AddParameter("IterationMode", IterationMode.Manual, "Iteration Mode", category: "Iteration", order: 1);
        _currentIndex = AddParameter("CurrentIndex", 0, "Current Index", min: 0, max: 100000, category: "Iteration", order: 2);
        _autoAdvanceEnabled = AddParameter("AutoAdvance", false, "Auto Advance", category: "Iteration", order: 3);
        _loopMode = AddParameter("LoopMode", false, "Loop Mode", category: "Iteration", order: 4);
        _advanceDelay = AddParameter("AdvanceDelay", 1000, "Advance Delay (ms)", min: 100, max: 10000, category: "Iteration", order: 5);
        _showProgress = AddParameter("ShowProgress", true, "Show Progress", category: "Iteration", order: 6);
        _showIndex = AddParameter("ShowIndex", true, "Show Index", category: "Iteration", order: 7);
    }

    protected override void OnExecute(IToolContext context)
    {
        var list = _imageList.Value;
        int totalCount = list?.Count ?? 0;

        if (totalCount == 0)
            throw new ToolExecutionException("ImageListIterator: ImageList is empty - nothing to iterate.");

        // ----- Danh sách bị thay đổi (upstream sinh batch mới) -> tự reset về ảnh đầu tiên -----
        if (!ReferenceEquals(_lastListRef, list))
        {
            _lastListRef = list;
            _cursorIndex = 0;
            _currentIndex.Value = 0;
        }

        // ----- ResetTrigger có độ ưu tiên cao nhất, áp dụng cho MỌI mode -----
        if (_resetTrigger.Value)
        {
            _cursorIndex = 0;
            _currentIndex.Value = 0;
            _autoAdvanceWatch.Restart();
        }
        else
        {
            switch (_iterationMode.Value)
            {
                case IterationMode.Manual:
                    // Chỉ số điều khiển trực tiếp bằng tham số CurrentIndex - đồng bộ ngược lại _cursorIndex
                    // để các public API (NextImage/PreviousImage) sau này vẫn tính đúng từ vị trí hiện tại
                    _cursorIndex = _currentIndex.Value;
                    break;

                case IterationMode.AutoAdvance:
                    if (_autoAdvanceEnabled.Value && _autoAdvanceWatch.Elapsed.TotalMilliseconds >= _advanceDelay.Value)
                    {
                        _cursorIndex++;
                        _autoAdvanceWatch.Restart();
                    }
                    break;

                case IterationMode.Triggered:
                    if (_nextTrigger.Value)
                        _cursorIndex++;
                    break;
            }
        }

        // ----- Kẹp/quay vòng chỉ số về đúng phạm vi danh sách hiện tại -----
        int index;
        if (_cursorIndex >= totalCount)
            index = _loopMode.Value ? _cursorIndex % totalCount : totalCount - 1;
        else if (_cursorIndex < 0)
            index = _loopMode.Value ? ((_cursorIndex % totalCount) + totalCount) % totalCount : 0;
        else
            index = _cursorIndex;

        _cursorIndex = index;
        _currentIndex.Value = index; // Đồng bộ lại tham số để UI luôn hiển thị đúng vị trí thực tế đang đứng

        // ----- Vẽ overlay (nếu bật) lên bản CLONE, không sửa ảnh gốc trong danh sách -----
        Mat display = list![index].AsMat().Clone();
        if (_showIndex.Value)
            DrawIndexOverlay(display, index, totalCount);
        if (_showProgress.Value)
            DrawProgressBar(display, index, totalCount);

        bool isFirst = index == 0;
        bool isLast = index == totalCount - 1;

        _imageMatrix.Value = new MatVisionImage(display);
        _outCurrentIndex.Value = index;
        _outTotalCount.Value = totalCount;
        _outHasNext.Value = _loopMode.Value ? totalCount > 1 : !isLast;
        _outHasPrevious.Value = _loopMode.Value ? totalCount > 1 : !isFirst;
        _outIsFirst.Value = isFirst;
        _outIsLast.Value = isLast;
        _outProgress.Value = totalCount <= 1 ? 100.0 : (index + 1) * 100.0 / totalCount;

        context.Log($"ImageListIterator: {index + 1}/{totalCount} (mode={_iterationMode.Value})");
    }

    private static void DrawIndexOverlay(Mat img, int index, int totalCount)
    {
        string text = $"{index + 1}/{totalCount}";
        // Vẽ nền đen mờ phía sau chữ để chữ luôn đọc được dù ảnh nền sáng hay tối
        Cv2.Rectangle(img, new Rect(5, 5, 16 + text.Length * 14, 28), Scalar.Black, thickness: -1);
        Cv2.PutText(img, text, new Point(10, 26), HersheyFonts.HersheySimplex, 0.7, Scalar.White, 2);
    }

    private static void DrawProgressBar(Mat img, int index, int totalCount)
    {
        int barWidth = Math.Max(60, img.Width / 4);
        int barHeight = 10;
        int x = img.Width - barWidth - 10;
        int y = 10;

        Cv2.Rectangle(img, new Rect(x, y, barWidth, barHeight), new Scalar(60, 60, 60), thickness: -1); // Nền thanh progress
        int filled = totalCount <= 1 ? barWidth : (int)((index + 1) / (double)totalCount * barWidth);
        Cv2.Rectangle(img, new Rect(x, y, filled, barHeight), new Scalar(0, 200, 0), thickness: -1); // Phần đã hoàn thành (xanh lá - BGR)
        Cv2.Rectangle(img, new Rect(x, y, barWidth, barHeight), Scalar.White, thickness: 1); // Viền
    }

    #region 4. Public API - gọi từ nút bấm UI (VD "◀ Prev" / "Next ▶" / "Reset" trên node)
    /// <summary>Chuyển sang ảnh kế tiếp ngay lập tức, không cần chờ Execute() tiếp theo tự tính - hữu ích khi UI gọi trực tiếp qua nút bấm.</summary>
    public void NextImage() => _cursorIndex++;

    /// <summary>Lùi về ảnh trước đó.</summary>
    public void PreviousImage() => _cursorIndex--;

    /// <summary>Nhảy thẳng tới 1 chỉ số cụ thể (không kẹp/quay vòng ở đây - việc đó do OnExecute xử lý ở lần chạy kế tiếp).</summary>
    public void GoToImage(int index) => _cursorIndex = index;

    /// <summary>Quay về ảnh đầu tiên.</summary>
    public void Reset()
    {
        _cursorIndex = 0;
        _autoAdvanceWatch.Restart();
    }
    #endregion
}