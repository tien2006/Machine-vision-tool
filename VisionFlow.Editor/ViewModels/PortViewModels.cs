using NodeNetwork; // Namespace thư viện NodeNetwork
using NodeNetwork.ViewModels; // Chứa các lớp base NodeInputViewModel, NodeOutputViewModel, ConnectionValidationResult
using System.Xml.Linq;
using VisionFlow.Core.Ports; // Namespace chứa giao diện domain IInputPort, IOutputPort

namespace VisionFlow.Editor.ViewModels;

/// <summary>
/// ViewModel đại diện cho cổng Đầu vào (Input Port) trên giao diện Node.
/// </summary>
public sealed class PortInputViewModel : NodeInputViewModel
{
    /// <summary>
    /// Cổng Input ở tầng Domain.
    /// </summary>
    public IInputPort DomainPort { get; }

    /// <summary>
    /// Khởi tạo PortInputViewModel từ một IInputPort.
    /// </summary>
    public PortInputViewModel(IInputPort port)
    {
        DomainPort = port; // Lưu lại tham chiếu cổng domain gốc
        Name = port.DisplayName; // Gán tên hiển thị của cổng trên UI
        MaxConnections = 1; // Giới hạn cổng Input chỉ được nhận tối đa 1 kết nối

        // Định nghĩa logic kiểm tra tính hợp lệ khi người dùng thực hiện kéo nối dây trên canvas
        ConnectionValidator = pending =>
        {
            // Lấy pending.Output ra để kiểm tra
            bool isCompatible = pending.Output is PortOutputViewModel portOutput
                                && port.DataType.IsAssignableFrom(portOutput.DomainPort.DataType);
            // Ý nghĩa: Kiểu dữ liệu của cổng Input (port.DataType) có thể chấp nhận/gán được giá trị
            // từ kiểu dữ liệu của cổng Output (portOutput.DomainPort.DataType) hay không?

            return new ConnectionValidationResult(isCompatible, isCompatible ? null : "Kiểu dữ liệu không tương thích!");
        };
    }
}

/// <summary>
/// ViewModel đại diện cho cổng Đầu ra (Output Port) trên giao diện Node.
/// </summary>
public sealed class PortOutputViewModel : NodeOutputViewModel
{
    /// <summary>
    /// Cổng Output ở tầng Domain.
    /// </summary>
    public IOutputPort DomainPort { get; }

    /// <summary>
    /// Khởi tạo PortOutputViewModel từ một IOutputPort.
    /// </summary>
    public PortOutputViewModel(IOutputPort port)
    {
        DomainPort = port; // Lưu lại tham chiếu cổng domain gốc
        Name = port.DisplayName; // Gán tên hiển thị của cổng trên UI
    }
}