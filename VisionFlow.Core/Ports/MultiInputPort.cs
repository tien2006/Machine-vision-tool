// ==================== Vai trò chính:                Định nghĩa loại Port ĐẶC BIỆT cho phép nhận NHIỀU dây nối cùng lúc (fan-in), khác InputPort<T> thường chỉ nhận đúng 1 dây
// ==================== Thành phần / Class tiêu biểu: IMultiInputPort, MultiInputPort<TItem>
// ==================== Phụ thuộc vào:                Core.Ports (IInputPort, IPort, PortDirection)
// ==================== Pattern / Kỹ thuật nổi bật:   Marker Interface (IMultiInputPort) để Engine nhận diện tại runtime + Dictionary<string, TItem> gom nhiều nguồn theo key "NodeName.Port"

using System;
using System.Collections.Generic;

namespace VisionFlow.Core.Ports;

/// <summary>
/// Interface đánh dấu (marker interface) cho 1 Input Port ĐẶC BIỆT chấp nhận NHIỀU dây nối đổ vào cùng lúc,
/// khác với <see cref="IInputPort"/> thường (qua <see cref="InputPort{T}"/>) chỉ nhận tối đa 1 dây.
/// Engine (FlowGraph.Connect, FlowExecutor.FeedInputs) kiểm tra "port is IMultiInputPort" để quyết định
/// cho phép nhiều kết nối hay áp ràng buộc 1-dây như cũ - hoàn toàn không ảnh hưởng các Port thường có sẵn.
/// </summary>
public interface IMultiInputPort : IInputPort
{
    /// <summary>
    /// Engine gọi hàm này để nạp TOÀN BỘ giá trị đã gom được từ mọi dây nối vào port, dạng
    /// key = "NodeName.PortName" (tên node nguồn + tên cổng output nguồn) -> value = giá trị output đó.
    /// Value truyền vào dạng "object?" chung (không cần Engine biết kiểu T cụ thể của Tool con).
    /// </summary>
    void SetValues(IReadOnlyDictionary<string, object?> values);
}

/// <summary>
/// Triển khai cụ thể của Multi-Input Port, dùng kiểu mạnh TItem cho từng giá trị nhận được.
/// Ví dụ: <c>MultiInputPort&lt;object&gt;</c> dùng cho AggregatorTool.Sources (chấp nhận MỌI kiểu output).
/// </summary>
public sealed class MultiInputPort<TItem> : IMultiInputPort
{
    private Dictionary<string, TItem> _values = new();

    public string Name { get; }
    public string DisplayName { get; }

    // Multi-input mặc định optional = true: hợp lệ ngay cả khi CHƯA nối dây nào (0 nguồn), không giống
    // Input thường thường bắt buộc phải có 1 nguồn mới chạy được.
    public bool IsOptional { get; }

    public Type DataType => typeof(TItem);
    public PortDirection Direction => PortDirection.Input;

    public MultiInputPort(string name, string? displayName = null, bool isOptional = true)
    {
        Name = name;
        DisplayName = displayName ?? name;
        IsOptional = isOptional;
    }

    /// <summary>Bản mạnh kiểu cho Tool con đọc trực tiếp trong OnExecute(), không cần ép kiểu object.</summary>
    public IReadOnlyDictionary<string, TItem> Values => _values;

    // Triển khai Explicit Interface phục vụ Engine xử lý qua object? chung (giữ đúng convention của InputPort<T>/OutputPort<T>)
    object? IPort.Value
    {
        get => _values;
        set
        {
            if (value is IReadOnlyDictionary<string, TItem> typedDict)
                _values = new Dictionary<string, TItem>(typedDict);
            else if (value == null)
                _values = new Dictionary<string, TItem>();
            else
                throw new ArgumentException($"MultiInputPort '{Name}': kiểu dữ liệu không khớp, kỳ vọng IReadOnlyDictionary<string,{typeof(TItem)}>.");
        }
    }

    /// <summary>Engine gọi hàm này mỗi lần FeedInputs - build lại Dictionary từ TOÀN BỘ dây nối đang có vào port này.</summary>
    public void SetValues(IReadOnlyDictionary<string, object?> values)
    {
        var result = new Dictionary<string, TItem>();
        foreach (var kv in values)
        {
            if (kv.Value is TItem typedItem)
            {
                result[kv.Key] = typedItem;
            }
            else if (kv.Value == null)
            {
                // Nguồn chưa chạy / không có giá trị -> bỏ qua entry này thay vì crash, giữ Aggregator chạy được
                // ngay cả khi 1 trong các nguồn nối vào chưa Execute hoặc bị lỗi.
                continue;
            }
            else
            {
                throw new ArgumentException(
                    $"MultiInputPort '{Name}': giá trị từ nguồn '{kv.Key}' có kiểu {kv.Value.GetType()}, " +
                    $"không khớp kiểu {typeof(TItem)} mà port này khai báo.");
            }
        }
        _values = result;
    }
}