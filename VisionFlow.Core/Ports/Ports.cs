// ==================== Vai trò chính:                Cơ chế "chân cắm" dữ liệu có kiểu mạnh, dùng để nối các Tool với nhau trong đồ thị
// ==================== Thành phần / Class tiêu biểu: IPort, IInputPort, IOutputPort, InputPort<T>, OutputPort<T>
// ==================== Phụ thuộc vào:                Không phụ thuộc gì
// ==================== Pattern / Kỹ thuật nổi bật:   Generic Type + Explicit Interface Implementation (vừa có API kiểu mạnh T cho Tool, vừa có API object cho Engine)

using System;

namespace VisionFlow.Core.Ports;

/// <summary>
/// Định nghĩa chiều dữ liệu của chân cắm port.
/// </summary>
public enum PortDirection
{
    Input,
    Output
}

/// <summary>
/// Hợp đồng gốc mà mọi port trong hệ thống phải tuân theo.
/// </summary>
public interface IPort
{
    string Name { get; }
    string DisplayName { get; }
    Type DataType { get; }
    PortDirection Direction { get; }

    // Engine làm việc với object? chung thông qua interface này
    object? Value { get; set; }
}

/// <summary>
/// Interface đại diện cho chân cắm đầu vào (nhận dữ liệu).
/// </summary>
public interface IInputPort : IPort
{
    bool IsOptional { get; }
}

/// <summary>
/// Interface đánh dấu (marker interface) cho chân cắm đầu ra (phát dữ liệu).
/// </summary>
public interface IOutputPort : IPort
{
    // Không thêm thuộc tính mới, dùng để phân biệt an toàn lúc biên dịch
}

/// <summary>
/// Lớp triển khai cụ thể cho Port đầu vào, dùng kiểu mạnh T.
/// </summary>
public sealed class InputPort<T> : IInputPort
{
    private T? _value;

    public string Name { get; }
    public string DisplayName { get; }
    public bool IsOptional { get; }

    // Sử dụng cú pháp expression-bodied và typeof(T) để trả về kiểu thật của port
    public Type DataType => typeof(T);
    public PortDirection Direction => PortDirection.Input;

    // Constructor với tham số mặc định cho displayName và isOptional
    public InputPort(string name, string? displayName = null, bool isOptional = false)
    {
        Name = name;
        // Sử dụng toán tử null-coalescing (??): nếu displayName null thì lấy Name
        DisplayName = displayName ?? name;
        IsOptional = isOptional;
    }

    // Bản công khai cho các tool con đọc/ghi trực tiếp bằng kiểu mạnh T, không cần ép kiểu
    public T? Value
    {
        get => _value;
        set => _value = value;
    }

    // Triển khai Explicit Interface phục vụ riêng cho Engine xử lý thông qua object?
    object? IPort.Value
    {
        get => _value;
        set
        {
            if (value == null)
            {
                _value = default;
            }
            else if (value is T typedValue)
            {
                _value = typedValue; // Ép kiểu an toàn trước khi lưu trữ
            }
            else
            {
                throw new ArgumentException($"Kiểu dữ liệu không khớp. Kỳ vọng kiểu {typeof(T)}, nhưng nhận được {value.GetType()}.");
            }
        }
    }
}

/// <summary>
/// Lớp triển khai cụ thể cho Port đầu ra, dùng kiểu mạnh T.
/// </summary>
public sealed class OutputPort<T> : IOutputPort
{
    private T? _value;

    public string Name { get; }
    public string DisplayName { get; }

    public Type DataType => typeof(T);
    public PortDirection Direction => PortDirection.Output;

    public OutputPort(string name, string? displayName = null)
    {
        Name = name;
        DisplayName = displayName ?? name;
    }

    // Bản công khai kiểu mạnh dành cho tool ghi kết quả đầu ra
    public T? Value
    {
        get => _value;
        set => _value = value;
    }

    // Triển khai Explicit Interface phục vụ cho Engine đọc/truyền dữ liệu dạng object?
    object? IPort.Value
    {
        get => _value;
        set
        {
            if (value == null)
            {
                _value = default;
            }
            else if (value is T typedValue)
            {
                _value = typedValue;
            }
            else
            {
                throw new ArgumentException($"Kiểu dữ liệu không khớp. Kỳ vọng kiểu {typeof(T)}, nhưng nhận được {value.GetType()}.");
            }
        }
    }
}