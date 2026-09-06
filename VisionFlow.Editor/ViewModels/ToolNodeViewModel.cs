using DynamicData; // Thu viện xử lý các tập hợp reactive (Reactive Collections)
using NodeNetwork.ViewModels; // Các lớp base ViewModel của thư viện NodeNetwork
using System.Xml.Linq;
using VisionFlow.Core.Tools; // Namespace chứa đối tượng domain VisionTool
using VisionFlow.Editor.ViewModels; // Namespace chứa các ViewModel của phần Editor

namespace VisionFlow.Editor.ViewModels;

/// <summary>
/// Lớp Adapter chuyển đổi VisionTool ở tầng Domain thành một NodeViewModel hiển thị trên canvas NodeNetwork.
/// </summary>
public class ToolNodeViewModel : NodeViewModel
{
    // Dictionary lưu các cổng Input nội bộ theo tên (dùng Ordinal để so sánh chuỗi chính xác từng byte)
    private readonly Dictionary<string, PortInputViewModel> inputs = new(StringComparer.Ordinal);

    // Dictionary lưu các cổng Output nội bộ theo tên
    private readonly Dictionary<string, PortOutputViewModel> outputs = new(StringComparer.Ordinal);

    /// <summary>
    /// Khởi tạo một NodeViewModel đại diện cho VisionTool.
    /// </summary>
    /// <param name="tool">Đối tượng VisionTool ở tầng Domain</param>
    public ToolNodeViewModel(VisionTool tool)
    {
        Tool = tool; // Lưu đối tượng domain gốc
        Name = tool.DisplayName; // Gán tên hiển thị của Node trên UI bằng DisplayName của Tool

        // Duyệt qua danh sách các cổng Input domain của Tool
        foreach (var input in tool.Inputs)
        {
            var vm = new PortInputViewModel(input); // Bọc InputPort thành PortInputViewModel (UI)
            Inputs.Add(vm); // Thêm vào danh sách Inputs của lớp cha NodeViewModel để NodeNetwork vẽ endpoint
            inputs[input.Name] = vm; // Lưu vào Dictionary nội bộ theo tên để tra cứu nhanh
        }

        // Duyệt qua danh sách các cổng Output domain của Tool
        foreach (var output in tool.Outputs)
        {
            var vm = new PortOutputViewModel(output); // Bọc OutputPort thành PortOutputViewModel (UI)
            Outputs.Add(vm); // Thêm vào danh sách Outputs của lớp cha NodeViewModel để NodeNetwork vẽ endpoint
            outputs[output.Name] = vm; // Lưu vào Dictionary nội bộ theo tên để tra cứu nhanh
        }
    }

    /// <summary>
    /// Đối tượng VisionTool domain gốc được gắn liền với Node này.
    /// </summary>
    public VisionTool Tool { get; }

    /// <summary>
    /// Tra cứu PortInputViewModel theo tên cổng Input.
    /// </summary>
    public PortInputViewModel GetInput(string portName) => inputs[portName];

    /// <summary>
    /// Tra cứu PortOutputViewModel theo tên cổng Output.
    /// </summary>
    public PortOutputViewModel GetOutput(string portName) => outputs[portName];
}