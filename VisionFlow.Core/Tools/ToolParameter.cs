// ==================== Vai trò chính:                Hợp đồng chuẩn của một Tool + lớp nền (base class) dùng chung cho mọi thuật toán
// ==================== Thành phần / Class tiêu biểu: ITool, IToolContext, VisionTool (abstract), ToolParameter<T>, ToolMetadataAttribute, ToolState, ToolExecutionException
// ==================== Phụ thuộc vào:                Ports
// ==================== Pattern / Kỹ thuật nổi bật:   Strategy Pattern + Template Method Pattern

using System;
using System.Collections.Generic;
using System.Globalization;

namespace VisionFlow.Core.Tools; // File-scoped namespace (C# 10+) giúp giảm một cấp thụt lề cho toàn bộ file

/// <summary>
/// Kiểu liệt kê các hình thức tương tác hình học trực tiếp trên giao diện ảnh để cấu hình tham số.
/// </summary>
public enum ParameterInteraction
{
    None,               // Không tương tác ảnh, UI render widget thường (ô số, combobox)
    RectRegion,         // Vẽ vùng chọn hình chữ nhật thẳng đứng trên ảnh
    RotatedRectRegion,  // Vẽ vùng chọn hình chữ nhật có thể xoay góc trên ảnh
    CircleRegion,       // Vẽ vùng chọn hình tròn trên ảnh
    Polygon,            // Vẽ vùng chọn hình đa giác bất kỳ trên ảnh
    Caliper,            // Bật công cụ thước cặp Caliper đo lường trên ảnh[cite: 1]
    Point,              // Click chọn một điểm tọa độ duy nhất trên ảnh[cite: 1]
    Template            // Vẽ vùng chọn để lấy ảnh mẫu (Template Matching)[cite: 1]
}

/// <summary>
/// Giao diện chung không định kiểu phục vụ quản lý, duyệt tham số hàng loạt (UI/Serializer)[cite: 1].
/// </summary>
public interface IToolParameter
{
    string Name { get; }            // Tên định danh kỹ thuật của tham số[cite: 1]
    string DisplayName { get; }     // Tên hiển thị thân thiện trên UI cấu hình tool[cite: 1]
    object? Value { get; set; }      // Đọc/ghi giá trị dạng object cho UI và bộ tuần tự hóa[cite: 1]
    Type ValueType { get; }         // Trả về kiểu dữ liệu thật CLR của tham số[cite: 1]
    object? Minimum { get; }         // Cận dưới của tham số dạng object[cite: 1]
    object? Maximum { get; }         // Cận trên của tham số dạng object[cite: 1]
    string Category { get; }        // Tên danh mục gom nhóm tham số trên UI palette[cite: 1]
    int Order { get; }       // Thứ tự sắp xếp hiển thị trên bảng tham số[cite: 1]
    IReadOnlyList<string>? Choices { get; } // Danh sách lựa chọn chỉ đọc cho tham số Dropdown[cite: 1]
    ParameterInteraction Interaction { get; } // Loại hình tương tác đồ họa liên kết[cite: 1]
}

/// <summary>
/// Lớp Generic hiện thực IToolParameter, cung cấp tính năng Type-safe cho logic Backend[cite: 1].
/// </summary>
public sealed class ToolParameter<T> : IToolParameter // 'sealed' chặn kế thừa để JIT tối ưu hóa hiệu năng[cite: 1]
{
    public ToolParameter(
        string name,
        T value,
        string? displayName = null,
        T? minimum = default, // default của T (số là 0, object/nullable là null)[cite: 1]
        T? maximum = default,
        string category = "General",
        int order = 0,
        IReadOnlyList<string>? choices = null,
        ParameterInteraction interaction = ParameterInteraction.None)
    {
        Name = name;
        DisplayName = displayName ?? name; // Toán tử ?? (null-coalescing): lấy Name nếu displayName bị null[cite: 1]
        Value = value;
        //TypedValue = value;
        Minimum = minimum;
        Maximum = maximum;
        Category = category;
        Order = order;
        Choices = choices;
        Interaction = interaction;
    }
    public string Name { get; }            // Auto-property chỉ đọc tên hệ thống[cite: 1]
    public string DisplayName { get; }     // Auto-property chỉ đọc tên hiển thị[cite: 1]
    public Type ValueType => typeof(T);     // Expression-bodied trả về kiểu thật khi đóng generic[cite: 1]
    public T Value { get; set; }
    public T? Minimum { get; }              // Cận dưới mang kiểu định kiểu chuẩn T[cite: 1]
    public T? Maximum { get; }              // Cận trên mang kiểu định kiểu chuẩn T[cite: 1]
    public string Category { get; }        // Nhóm phân loại hiển thị của tham số[cite: 1]
    public int Order { get; }       // Thứ tự hiển thị trên giao diện[cite: 1]
    public IReadOnlyList<string>? Choices { get; } // Danh sách tùy chọn định kiểu type-safe T[cite: 1]
    public ParameterInteraction Interaction { get; } // Hình thức tương tác ảnh[cite: 1]
    //public T TypedValue { get; set; }      // Lưu trữ giá trị Type-safe cho các tool con tính toán


    // --- Explicit Interface Implementation (Hiện thực giao diện tường minh) ---
    // Giúp một lớp vừa có thuộc tính kiểu object (cho UI), vừa có kiểu T (cho Backend) mà không xung đột[cite: 1].

    object? IToolParameter.Value
    {
        get => Value; // Đóng hộp (box) dữ liệu T trả về dạng object[cite: 1]
        set => Value = Convert(value); // Chuyển đổi an toàn dữ liệu từ UI/JSON nạp vào[cite: 1]
    }

    object? IToolParameter.Minimum => Minimum; // Ánh xạ explicit trả về cận dưới dạng object[cite: 1]
    object? IToolParameter.Maximum => Maximum; // Ánh xạ explicit trả về cận trên dạng object[cite: 1]
                                               //IReadOnlyList<object> IToolParameter.Choices => (IReadOnlyList<object>)Choices; // Cast an toàn danh sách lựa chọn[cite: 1]

    // Hàm phòng thủ dữ liệu: Ép kiểu an toàn mọi đầu vào từ UI/JSON mà không làm sập ứng dụng[cite: 1]
    /// <summary>
    /// Chuyển đổi an toàn giá trị dữ liệu từ các nguồn không định kiểu (như UI Control, JSON Deserializer)
    /// về kiểu dữ liệu đích <typeparamref name="T"/> của tham số.
    /// </summary>
    /// <param name="v">Giá trị đầu vào dạng <see cref="object"/> cần chuyển đổi.</param>
    /// <returns>
    /// Trả về giá trị đã chuyển đổi sang kiểu <typeparamref name="T"/>. 
    /// Nếu đầu vào không hợp lệ hoặc xảy ra lỗi chuyển đổi, giữ nguyên giá trị <see cref="Value"/> hiện tại.
    /// </returns>
    private T Convert(object? v)
    {
        // 1. Trường hợp đầu vào là null: Trả về giá trị mặc định của T (default!). Sử dụng '!' để báo với C# Nullable Reference Types rằng ta chấp nhận kết quả này.
        if (v is null) return default!;

        // 2. Trường hợp đầu vào đã là kiểu T (hoặc lớp con của T): Ép kiểu trực tiếp và trả về ngay mà không cần qua logic chuyển đổi tốn chi phí hiệu năng.
        if (v is T t) return t;

        // 3. Lấy thông tin kiểu dữ liệu đích: Xác định kiểu dữ liệu đích (T) và xử lý riêng cho trường hợp Nullable<T> (ví dụ: int? -> int)
        var target = typeof(T);
        var underlying = Nullable.GetUnderlyingType(target) ?? target;

        // 4. Bảo vệ trước chuỗi rỗng/khoảng trắng:
        // Khi người dùng xóa trắng ô nhập liệu trên UI, trả về Value hiện tại để tránh kích hoạt FormatException không cần thiết.
        if (v is string s && string.IsNullOrWhiteSpace(s))
            return Value;

        try
        {
            // 5. Xử lý chuyên biệt cho kiểu Enum:
            if (underlying.IsEnum)
            {
                return v is string es
                    // Trường hợp 5a: Chuỗi tên Enum (vd: "Red") -> Chuyển thành giá trị Enum (không phân biệt hoa/thường)
                    ? (T)Enum.Parse(underlying, es, ignoreCase: true)
                    // Trường hợp 5b: Số nguyên đại diện Enum (vd: 1) -> Chuyển thành giá trị Enum dựa trên kiểu nền (int, byte, v.v.)
                    : (T)Enum.ToObject(underlying, System.Convert.ChangeType(v, Enum.GetUnderlyingType(underlying)));
            }

            // 6. Xử lý ép kiểu cho các kiểu chuẩn (Primitive, DateTime, String, v.v.):
            // Sử dụng CultureInfo.InvariantCulture để tránh lỗi phân tách số thập phân (dấu chấm '.' vs dấu phẩy ',') giữa các vùng miền.
            return (T)System.Convert.ChangeType(v, underlying, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException or ArgumentException)
        {
            // 7. Bẫy lỗi phòng thủ (Defensive Error Handling):
            // Bắt chính xác các ngoại lệ khi người dùng nhập sai định dạng (vd: nhập "abc" vào ô số, hoặc số quá to gây tràn bộ nhớ).
            // Trả về giá trị Value cũ để UI khôi phục trạng thái an toàn thay vì bắn exception làm sập phần mềm.
            return Value;
        }
    }
}