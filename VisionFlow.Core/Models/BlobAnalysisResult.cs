using System;
using System.Collections.Generic;

namespace VisionFlow.Core.Models;

/// <summary>
/// Kết quả trả về từ tool BlobAnalysis: danh sách toàn bộ Blob tìm được sau khi lọc,
/// kèm theo các số liệu tổng hợp nhanh (Count, TotalArea) và trạng thái phân định OK/NG.
/// Theo đúng pattern VisionResult (Judge) như CircleResult/LineResult đã có trong hệ thống.
/// </summary>
public sealed class BlobAnalysisResult : VisionResult
{
    /// <summary>
    /// Danh sách các Blob đã tìm được, đã lọc theo MinArea/MaxArea và đã sắp xếp theo cấu hình SortBy.
    /// </summary>
    public IReadOnlyList<VisionBlob> Blobs { get; set; } = Array.Empty<VisionBlob>();

    /// <summary>
    /// Tổng số lượng Blob hợp lệ sau khi lọc (bằng Blobs.Count, tách riêng ra để tiện nối dây
    /// trực tiếp vào CompareTool/LogicGate mà không cần bung cả danh sách).
    /// </summary>
    public int Count { get; set; }

    /// <summary>
    /// Tổng diện tích cộng dồn của toàn bộ Blob hợp lệ (đơn vị pixel vuông).
    /// </summary>
    public double TotalArea { get; set; }
}
