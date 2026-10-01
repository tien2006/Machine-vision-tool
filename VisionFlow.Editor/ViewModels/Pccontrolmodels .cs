// ==================== Vai trò chính:                Model dữ liệu cho màn hình "PC Control" (vận hành tay máy) — điểm I/O theo dõi,
//                                                     bước trong 1 Recipe, luật cảnh báo và dòng log cảnh báo.
// ==================== Thành phần / Class tiêu biểu: IoKind, IoPointViewModel, RecipeStepViewModel, RecipeFile, AlarmRuleViewModel, AlarmLogEntry
// ==================== Phụ thuộc vào:                CommunityToolkit.Mvvm (ObservableObject) — cùng thư viện MVVM đã dùng trong ParameterEditorViewModel
// ==================== Pattern / Kỹ thuật nổi bật:   Lấy cảm hứng tổ chức nghiệp vụ (I/O thủ công, Recipe theo Step, Alarm) từ CoreApp/UIApp
//                                                     (EquipInitControl, RecipeProgStepListControl, MainAlarmListControl) nhưng thay lớp giao tiếp
//                                                     phần cứng độc quyền (SharedDataSource/mailslot) bằng IPlcLink sẵn có của VisionFlow.
//
// VỊ TRÍ ĐẶT FILE: project VisionFlow.Editor, thư mục ViewModels/ (cùng nơi với FlowEditorViewModel.cs) — namespace VisionFlow.Editor.ViewModels.

using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace VisionFlow.Editor.ViewModels;

/// <summary>Loại thiết bị PLC mà 1 điểm I/O/Step đang thao tác — quyết định UI hiển thị Toggle (Bit) hay ô nhập số (Word).</summary>
public enum IoKind { Bit, Word }

/// <summary>
/// 1 điểm I/O do người vận hành tự khai báo trên tab "Manual I/O" — tương đương ý tưởng EquipInitControl của
/// CoreApp nhưng người dùng tự đặt tên/địa chỉ thay vì cố định cứng trong code.
/// </summary>
public sealed partial class IoPointViewModel : ObservableObject
{
    [ObservableProperty] private string _name = "IO mới";
    [ObservableProperty] private string _address = "M0";     // "Y10", "M100", "D1000"...
    [ObservableProperty] private IoKind _kind = IoKind.Bit;

    [ObservableProperty] private bool _boolValue;             // Giá trị hiện tại khi Kind = Bit
    [ObservableProperty] private double _wordValue;           // Giá trị hiện tại khi Kind = Word (đã áp Scale)
    [ObservableProperty] private double _scale = 1.0;         // Chỉ dùng khi Kind = Word (giống PlcWriteWordTool/PlcReadWordTool)

    [ObservableProperty] private bool _autoPoll = true;       // true = timer tự đọc định kỳ; false = chỉ đọc khi bấm nút "Read"
    [ObservableProperty] private string _statusText = "";     // Thông báo lỗi đọc/ghi gần nhất (rỗng = bình thường)
}

/// <summary>1 bước trong Recipe thủ công: ghi 1 giá trị xuống 1 địa chỉ, rồi chờ DelayMs trước khi sang bước kế.</summary>
public sealed partial class RecipeStepViewModel : ObservableObject
{
    [ObservableProperty] private int _order;
    [ObservableProperty] private string _description = "Bước mới";
    [ObservableProperty] private string _address = "Y10";
    [ObservableProperty] private IoKind _kind = IoKind.Bit;
    [ObservableProperty] private double _value;               // 0/1 khi Bit; giá trị thực khi Word
    [ObservableProperty] private double _scale = 1.0;         // Chỉ dùng khi Kind = Word
    [ObservableProperty] private int _delayAfterMs = 500;      // Thời gian chờ SAU KHI ghi xong, trước khi chạy bước kế
    [ObservableProperty] private bool _isCurrent;              // true = đang là bước thực thi (tô sáng trên UI) khi Run

    /// <summary>true = ghi giá trị lên PLC; false = chỉ WAIT cho tới khi Address đạt Value (giống PlcWaitBitTool, chỉ áp dụng Kind=Bit).</summary>
    [ObservableProperty] private bool _isWaitStep;
    [ObservableProperty] private int _waitTimeoutMs = 5000;
}

/// <summary>Cấu trúc lưu/nạp 1 Recipe ra file JSON (System.Text.Json) — cùng tinh thần JsonFlowRepository đã có cho Flow.</summary>
public sealed class RecipeFile
{
    public string Name { get; set; } = "Recipe1";
    public List<RecipeStepData> Steps { get; set; } = new();
}

/// <summary>Bản ghi thuần dữ liệu (không phải ObservableObject) dùng để (de)serialize Recipe ra/vào JSON.</summary>
public sealed class RecipeStepData
{
    public string Description { get; set; } = "";
    public string Address { get; set; } = "";
    public IoKind Kind { get; set; }
    public double Value { get; set; }
    public double Scale { get; set; } = 1.0;
    public int DelayAfterMs { get; set; }
    public bool IsWaitStep { get; set; }
    public int WaitTimeoutMs { get; set; } = 5000;
}

/// <summary>1 dòng trong danh sách "thư viện" Recipe ở tab "Công thức" — tương đương màn hình chọn Recipe (レシピ選択)
/// của CoreApp: mỗi Recipe là 1 file .json riêng nằm trong thư mục Recipes/ cạnh file .exe, KHÔNG còn phải tự mở/lưu
/// bằng hộp thoại mỗi lần như bản cũ. Các nút Create new/Edit/Copy/Paste/Delete/Import/Export đều thao tác trên
/// danh sách này; "Back" chỉ đơn thuần chuyển panel Edit (RecipeSteps) về lại panel danh sách (IsRecipeListView).</summary>
public sealed partial class RecipeLibraryEntry : ObservableObject
{
    [ObservableProperty] private int _no;
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _filePath = "";
    [ObservableProperty] private int _stepCount;
    [ObservableProperty] private DateTime _modifiedAt;
}

/// <summary>1 luật cảnh báo do người vận hành khai báo: theo dõi 1 bit, báo động khi lên mức BitActiveWhen.</summary>
public sealed partial class AlarmRuleViewModel : ObservableObject
{
    [ObservableProperty] private string _name = "Cảnh báo mới";
    [ObservableProperty] private string _address = "M900";
    [ObservableProperty] private bool _activeWhen = true;     // true = cảnh báo khi bit lên ON; false = khi bit về OFF
    [ObservableProperty] private string _message = "Có cảnh báo từ PLC";
    [ObservableProperty] private bool _isEnabled = true;
    [ObservableProperty] private bool _lastKnownState;        // Trạng thái lần poll trước — dùng để phát hiện cạnh lên/xuống (edge)
}

// ============================================================================================
// CÁC MODEL DƯỚI ĐÂY PHỤC VỤ RIÊNG TAB "TỔNG QUAN" (Dashboard) — dựng theo đúng bố cục màn hình
// mẫu bạn gửi (PLC Mitsubishi / Camera Cognex / Robot UR3e / Băng tải / SQL Server).
// ============================================================================================

/// <summary>1 thẻ trạng thái thiết bị trên hàng đầu Dashboard (PLC/Camera/Robot/Băng tải/SQL Server).
/// Riêng thẻ PLC được cập nhật TỰ ĐỘNG từ IPlcLink thật; các thẻ còn lại là placeholder mô phỏng
/// (chưa có phần cứng Camera Cognex/Robot UR3e/SQL Server thật) — bạn có thể bấm chuyển Online/Offline
/// bằng tay trong tab "Cài đặt" để demo, hoặc nối vào driver thật sau này.</summary>
public sealed partial class DeviceStatusViewModel : ObservableObject
{
    public string Name { get; init; } = "";
    public string SubtitleText { get; init; } = "";     // Vd "Q04UDEHCPU", "In-Sight", "UR3e"...
    [ObservableProperty] private bool _isOnline;
    [ObservableProperty] private string _statusText = "Offline";
    /// <summary>true = thiết bị này là phần cứng thật đang thao tác qua IPlcLink; false = đang mô phỏng bằng tay.</summary>
    public bool IsRealHardware { get; init; }
}

/// <summary>1 dòng trong lịch sử cảnh báo (tương đương ý tưởng AlarmHistControl/MainAlarmListControl của CoreApp).
/// Mở rộng thêm các cột giống đúng bảng Alarm History của CoreApp (No./Alarm No./Alarm name/Recipe name/
/// thời gian xảy ra/Step No./Step name/Remaining time/Thời gian reset).</summary>
public sealed partial class AlarmLogEntry : ObservableObject
{
    public int No { get; init; }                      // Số thứ tự hiển thị (1,2,3...)
    public int AlarmNo { get; init; }                  // Mã số cảnh báo — tăng dần mỗi lần có cảnh báo mới kích hoạt
    public DateTime Timestamp { get; init; } = DateTime.Now; // "Thời gian xảy ra"
    public string RuleName { get; init; } = "";        // "Alarm name"
    public string Message { get; init; } = "";
    public string RecipeName { get; init; } = "-";      // Recipe đang chạy lúc cảnh báo xảy ra (hoặc "-" nếu không chạy Recipe)
    public int StepNo { get; init; }                    // Step đang chạy lúc cảnh báo xảy ra (0 nếu không chạy Recipe)
    public string StepName { get; init; } = "-";
    public string RemainingTimeText { get; init; } = "-"; // Thời gian còn lại của Step tại thời điểm cảnh báo xảy ra (snapshot)

    [ObservableProperty] private bool _acknowledged;
    [ObservableProperty] private DateTime? _acknowledgedAt;   // "Thời gian reset"
}

/// <summary>1 dòng trong "Operation history" — lịch sử các LẦN CHẠY Recipe đã hoàn tất (xong/bị Stop/lỗi),
/// tương đương ý tưởng 稼働履歴 (WorkHistControl) của CoreApp nhưng rút gọn về đúng những gì hệ thống này
/// theo dõi được (không có dữ liệu nhiệt độ/áp suất/MFC như CVD gốc — thay bằng số liệu Recipe/Step của mình).</summary>
public sealed class RecipeRunHistoryEntry
{
    public int No { get; init; }
    public int RecipeNo { get; init; }
    public string RecipeName { get; init; } = "";
    public DateTime StartTime { get; init; }
    public DateTime EndTime { get; init; }
    public string DurationText { get; init; } = "-";
    /// <summary>"OK" (chạy xong trọn vẹn) / "Stopped" (bị bấm Stop) / "Error" (lỗi giữa chừng).</summary>
    public string Result { get; init; } = "-";
    public int StepCount { get; init; }
}

/// <summary>1 dòng trong lịch sử cảnh báo (tương đương ý tưởng AlarmHistControl/MainAlarmListControl của CoreApp).</summary>
public sealed class ActivityLogEntry
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string EventText { get; init; } = "";
    /// <summary>"OK" / "NG" / "-" (trung tính, vd "Dừng băng tải" không có kết quả đúng/sai).</summary>
    public string Result { get; init; } = "-";
}

/// <summary>1 điểm I/O CỐ ĐỊNH hiển thị trong khung "IO / Trạng thái thiết bị" ở Dashboard — khác với
/// IoPointViewModel (người dùng tự thêm ở tab "Điều khiển"), danh sách này CỐ ĐỊNH theo đúng bảng địa chỉ
/// I/O thật của cell (X0/X1 ngõ vào, Y0..Y4 ngõ ra) để hiển thị nhanh mà không cần khai báo lại.</summary>
public sealed partial class SystemIoPointViewModel : ObservableObject
{
    public string Address { get; init; } = "";
    public string Label { get; init; } = "";
    public bool IsOutput { get; init; }
    [ObservableProperty] private bool _value;
}

/// <summary>
/// Bảng địa chỉ PLC dùng cho các nút điều khiển "thân thiện" trên Dashboard (Bắt đầu/Dừng/Reset, Gắp/Đặt OK/Đặt NG/Home,
/// Chạy/Dừng băng tải...). Để dạng ObservableObject + chuỗi text (không hard-code) để bạn tự sửa lại đúng theo chương
/// trình ladder thật của máy mình ngay trên tab "Cài đặt" mà KHÔNG cần build lại phần mềm.
/// </summary>
public sealed partial class PcAddressSettings : ObservableObject
{
    // --- Điều khiển hệ thống ---
    [ObservableProperty] private string _autoModeAddress = "M10";     // ON = Tự động, OFF = Bằng tay
    [ObservableProperty] private string _startAddress = "M11";        // Pulse
    [ObservableProperty] private string _stopAddress = "M12";         // Pulse
    [ObservableProperty] private string _resetAddress = "M13";        // Pulse
    [ObservableProperty] private string _cellRunningAddress = "M14";  // Bit trạng thái "Đang chạy" đọc về từ PLC

    // --- Điều khiển Robot (trùng đúng bảng IO mẫu: Y2 Lệnh gắp robot, Y3 Đặt OK, Y4 Đặt NG) ---
    [ObservableProperty] private string _robotGripAddress = "Y2";     // Pulse
    [ObservableProperty] private string _robotPlaceOkAddress = "Y3";  // Pulse
    [ObservableProperty] private string _robotPlaceNgAddress = "Y4";  // Pulse
    [ObservableProperty] private string _robotHomeAddress = "M15";    // Pulse

    // --- Điều khiển băng tải (trùng đúng Y0 = "Dừng băng tải" trong bảng IO mẫu -> ON nghĩa là DỪNG) ---
    [ObservableProperty] private string _conveyorStopAddress = "Y0";  // ON = dừng băng tải, OFF = cho chạy
    [ObservableProperty] private string _conveyorSpeedAddress = "D50"; // Word 0..100 (%)

    // --- IO cố định hiển thị Dashboard (đúng theo bảng mẫu) ---
    [ObservableProperty] private string _sensorHasPartAddress = "X0";     // "Có sản phẩm"
    [ObservableProperty] private string _sensorGripPositionAddress = "X1"; // "Vị trí gắp"
    [ObservableProperty] private string _triggerCameraAddress = "Y1";      // "Trigger camera"

    // --- Thông tin mạng hiển thị (chỉ để xem — IP PLC lấy thật từ appsettings.json/PlcLinkHost) ---
    [ObservableProperty] private string _cameraIp = "192.168.1.20";
    [ObservableProperty] private string _robotIp = "192.168.1.30";
    [ObservableProperty] private string _pcIp = "192.168.1.100";
    [ObservableProperty] private string _subnetMask = "255.255.255.0";

    /// <summary>Pulse width mặc định (ms) cho mọi lệnh dạng xung (Bắt đầu/Dừng/Reset/Gắp/Đặt OK/Đặt NG/Home).</summary>
    [ObservableProperty] private int _pulseMs = 200;

    // --- BUZZSTOP / ALMRESET (giống CallDelegateButton "BUZZSTOP"/"ALMRESET" trong FuncMenu của CoreApp) ---
    [ObservableProperty] private string _buzzStopAddress = "M20";     // Pulse — tắt còi báo động
    [ObservableProperty] private string _alarmResetAddress = "M21";   // Pulse — reset trạng thái Alarm trên PLC
}

/// <summary>3 cấp quyền truy cập — mô phỏng UserMode (Guest/Operator/Administrator) của CoreApp, thêm cấp Maintenance
/// theo yêu cầu riêng của bạn. Guest = mặc định khi CHƯA đăng nhập.</summary>
public enum UserRole { Guest, Administrator, Maintenance }

/// <summary>Trạng thái vận hành Recipe — mô phỏng RecipeStatus.STATUS1 (STANDBY/EXECUTE...) của CoreApp,
/// đặt tên tiếng Anh theo đúng 5 trạng thái bạn yêu cầu.</summary>
public enum RecipeExecutionStatus { Standby, Idling, Running, Paused, Error }