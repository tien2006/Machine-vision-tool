// ==================== Vai trò chính:                Định nghĩa các kiểu dữ liệu hình học & kết quả thuật toán thuần túy (POCO/DTO)
// ==================== Thành phần / Class tiêu biểu: Point2d, Circle, LineSegment, RectRegion, RotatedRectRegion, CircleRegion,
                                                      // CaliperRegion, Judge, VisionResult, CircleResult, LineResult, AlignResult, VisionBlob
// ==================== Phụ thuộc vào:                Không phụ thuộc gì
// ==================== Pattern / Kỹ thuật nổi bật:   record struct (value type, immutable, hiệu năng cao, không rác GC)

using System;

namespace VisionFlow.Core.Models;

/// <summary>
/// Lớp nền trừu tượng cho mọi kết quả thuật toán thị giác trong VisionFlow.
/// Đảm bảo tất cả các kết quả đều mang theo trường phân định OK/NG.
/// </summary>
public abstract class VisionResult
{
    /// <summary>
    /// Trạng thái phân định kết quả (OK/NG/None). Mặc định là None.
    /// </summary>
    public Judge Judge { get; set; } = Judge.None;
}

/// <summary>
/// Kết quả trả về từ thuật toán tìm kiếm/đo đạc đường tròn.
/// </summary>
public sealed class CircleResult : VisionResult
{
    /// <summary>
    /// Thông tin chi tiết về đường tròn tìm được (Tâm và Bán kính).
    /// Vì Circle là struct nên tự động có giá trị mặc định (zero), không lo bị null.
    /// </summary>
    public Circle Circle { get; set; }

    /// <summary>
    /// Độ khớp hoặc điểm số tin cậy của đường tròn tìm được, nằm trong khoảng từ 0.0 đến 1.0.
    /// </summary>
    public double Score { get; set; }
}

/// <summary>
/// Kết quả trả về từ thuật toán tìm kiếm/dò đường thẳng.
/// </summary>
public sealed class LineResult : VisionResult
{
    /// <summary>
    /// Đoạn thẳng tìm được (gồm điểm đầu và điểm cuối).
    /// </summary>
    public LineSegment Segment { get; set; }

    /// <summary>
    /// Góc nghiêng của đường thẳng, được tính bằng đơn vị độ (degrees).
    /// </summary>
    public double AngleDeg { get; set; }
}

/// <summary>
/// Kết quả trả về từ bài toán căn chỉnh vị trí phôi (Alignment).
/// </summary>
public sealed class AlignResult : VisionResult
{
    /// <summary>
    /// Độ sai lệch căn chỉnh bao gồm lượng dịch chuyển X, Y và góc xoay Theta so với vị trí chuẩn.
    /// Dữ liệu này sẽ được khâu tiếp theo sử dụng để bù trừ tọa độ.
    /// </summary>
    public XYThetaOffset Offset { get; set; }

    /// <summary>
    /// Tọa độ tuyệt đối (pixel) của tâm đối tượng đã tìm thấy trên ảnh runtime hiện tại.
    /// Dùng kèm Offset để FixtureTool tính lại vị trí "chuẩn" (nominal) mà không cần thêm cổng dữ liệu nào khác,
    /// vì: NominalCenter = MatchedCenter - (Offset.X, Offset.Y).
    /// </summary>
    public Point2d MatchedCenter { get; set; }
    public double MatchedAngleDeg { get; init; }       // MỚI: góc xoay TUYỆT ĐỐI (θcur) — trước đây chỉ có ở Offset.Theta dạng LỆCH, không dùng trực tiếp cho Fixture được
    public double Scale { get; init; } = 1.0;          // MỚI: hệ số scale đều tìm được (Xcur so với Template lúc dạy) - mặc định 1.0 nếu không có giá trị thật (VD nhánh RANSAC thất bại)
}