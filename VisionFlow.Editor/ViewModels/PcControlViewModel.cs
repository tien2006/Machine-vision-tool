// ==================== Vai trò chính:                ViewModel trung tâm cho tab "PC Control" — màn hình vận hành máy TÁCH BIỆT với
//                                                     Flow Editor (đồ thị Tool vision vẫn là luồng chính, không đổi). Gồm 3 việc:
//                                                     (1) Quản lý kết nối PLC, (2) Đọc/ghi I/O thủ công, (3) Chạy Recipe theo Step,
//                                                     (4) Theo dõi & ghi log Alarm — mô phỏng lại nghiệp vụ của CoreApp/UIApp
//                                                     (EquipInitControl, RecipeProgStepListControl, MainAlarmListControl) trên nền IPlcLink.
// ==================== Thành phần / Class tiêu biểu: PcControlViewModel
// ==================== Phụ thuộc vào:                PlcLinkHost/IPlcLink/PlcAddress (VisionFlow.Hardware.Plc), PcControlModels (file cùng thư mục)
// ==================== Pattern / Kỹ thuật nổi bật:   MVVM (CommunityToolkit.Mvvm) — CÙNG thư viện/pattern đã dùng trong FlowEditorViewModel,
//                                                     DispatcherTimer để poll UI-thread-safe (không cần tự Dispatcher.Invoke như PlcHandshakeHost).
//
// VỊ TRÍ ĐẶT FILE: project VisionFlow.Editor, thư mục ViewModels/ — namespace VisionFlow.Editor.ViewModels.
// CÁCH GẮN VÀO ỨNG DỤNG: xem HUONG_DAN_TICH_HOP.md — chỉ cần (1) đăng ký DI, (2) thêm 1 property PcControl vào
// FlowEditorViewModel để MainWindow dùng chung 1 DataContext gốc, (3) thêm TabControl trong MainWindow.xaml.

using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using VisionFlow.Hardware.Plc;
using VisionFlow.Mes;                    // <-- THÊM: tầng MES mới
using VisionFlow.Core.Data;              // <-- THÊM: ActivityRecord dùng cho tab Báo cáo

namespace VisionFlow.Editor.ViewModels;

public sealed partial class PcControlViewModel : ObservableObject, IDisposable
{
    private readonly PlcLinkHost _plc;
    private readonly MesService _mes;                 // <-- THÊM: gọi xuống tầng MES mỗi khi có sự kiện
    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _clockTimer;
    private CancellationTokenSource? _recipeCts;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public ObservableCollection<IoPointViewModel> IoPoints { get; } = new();
    public ObservableCollection<RecipeStepViewModel> RecipeSteps { get; } = new();
    public ObservableCollection<AlarmRuleViewModel> AlarmRules { get; } = new();
    public ObservableCollection<AlarmLogEntry> AlarmLog { get; } = new();

    /// <summary>Lịch sử các lần chạy Recipe đã KẾT THÚC (xong/bị dừng/lỗi) — tương đương 稼働履歴 (Operation history)
    /// của CoreApp. Mỗi lần RunRecipe() kết thúc (dù thành công, bị Stop hay lỗi) sẽ thêm đúng 1 dòng vào đây.</summary>
    public ObservableCollection<RecipeRunHistoryEntry> RecipeRunHistory { get; } = new();

    private int _nextAlarmLogNo = 1;  // Cột "No." (số thứ tự dòng) của bảng Alarm history — tự tăng, không reset khi Clear
    private int _nextAlarmNo = 1;     // Cột "Alarm No." (mã lỗi) — tự tăng riêng, KHÔNG đặt lại khi Clear log
    private int _nextRunHistoryNo = 1; // Cột "No." của bảng Operation history (Recipe run history)

    /// <summary>Chọn bảng nào đang hiện trong Tab "Lịch sử": false = Operation history (mặc định), true = Alarm history.
    /// Thay cho việc hiện cả 2 bảng cùng lúc như bản cũ — đúng ý người dùng muốn tách 2 nút bấm riêng.</summary>
    [ObservableProperty] private bool _isAlarmHistoryActive;
    public bool IsOperationHistoryActive => !IsAlarmHistoryActive;
    partial void OnIsAlarmHistoryActiveChanged(bool value) => OnPropertyChanged(nameof(IsOperationHistoryActive));

    [RelayCommand] private void ShowOperationHistory() => IsAlarmHistoryActive = false;
    [RelayCommand] private void ShowAlarmHistory() => IsAlarmHistoryActive = true;

    [ObservableProperty] private string _connectionDescription = "PLC chưa cấu hình";
    [ObservableProperty] private PlcLinkState _linkState = PlcLinkState.Disconnected;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunRecipeCommand))] // Đổi IsRecipeRunning -> tự báo lại nút Run có bấm được không
    [NotifyCanExecuteChangedFor(nameof(PauseResumeRecipeCommand))] // <-- THÊM (mục 8): Pause/Skip chỉ bấm được khi đang chạy
    [NotifyCanExecuteChangedFor(nameof(SkipCurrentStepCommand))]   // <-- THÊM (mục 8)
    private bool _isRecipeRunning;
    [ObservableProperty] private string _statusText = "Sẵn sàng";
    [ObservableProperty] private string _recipeName = "Recipe1";

    // ==================================================================================
    // DASHBOARD "TỔNG QUAN" — dựng theo đúng bố cục màn hình mẫu bạn gửi.
    // ==================================================================================

    public PcAddressSettings Addresses { get; } = new();

    /// <summary>Thẻ trạng thái 5 thiết bị hàng đầu Dashboard: PLC (thật) + Camera/Robot/Băng tải/SQL (mô phỏng).</summary>
    public ObservableCollection<DeviceStatusViewModel> DeviceCards { get; } = new();

    /// <summary>Danh sách I/O CỐ ĐỊNH hiển thị khung "IO / Trạng thái thiết bị" (X0/X1 vào, Y0..Y4 ra).</summary>
    public ObservableCollection<SystemIoPointViewModel> SystemIoPoints { get; } = new();

    public ObservableCollection<ActivityLogEntry> ActivityLog { get; } = new();

    public ObservableCollection<string> AvailableRecipes { get; } = new() { "Default_01" };

    [ObservableProperty] private DateTime _currentTime = DateTime.Now;
    [ObservableProperty] private string _operatorName = "Operator";
    [ObservableProperty] private int _pendingWriteCount; // Số bản ghi đang chờ đẩy xuống SQL — xem BufferedProductionRepository

    // ==================================================================================
    // ĐĂNG NHẬP / PHÂN QUYỀN — mô phỏng Function Menu (nhập mật khẩu) của CoreApp, nhưng thêm Username
    // và bắt buộc mật khẩu đúng 6 CHỮ SỐ (không cho chữ) theo yêu cầu riêng của bạn.
    // LƯU Ý BẢO MẬT: tài khoản/mật khẩu đang để CỨNG trong code (demo) — hệ thống thật nên đọc từ 1 file cấu hình
    // KHÔNG đưa lên Git (giống CoreApp đọc từ ConstValue.PASSWORDFILE_PATH), hoặc lưu mật khẩu đã băm (hash) thay vì để trần.
    // ==================================================================================

    private static readonly (string Username, string Password, UserRole Role)[] Accounts =
    {
        ("Admin", "123456", UserRole.Administrator),
        ("Maintenance", "123456", UserRole.Maintenance),
    };

    [ObservableProperty] private UserRole _currentRole = UserRole.Guest;
    [ObservableProperty] private bool _isLoginPopupOpen;
    [ObservableProperty] private string _loginUsername = "";
    [ObservableProperty] private string _loginPassword = "";
    [ObservableProperty] private string _loginError = "";

    /// <summary>true nếu đã đăng nhập (Administrator hoặc Maintenance) — dùng để hiện/ẩn các Tab Điều khiển/Cài đặt/Data change.</summary>
    public bool IsLoggedIn => CurrentRole != UserRole.Guest;
    /// <summary>true CHỈ khi đăng nhập Maintenance — dùng để hiện/ẩn riêng Tab Maintenance.</summary>
    public bool CanSeeMaintenance => CurrentRole == UserRole.Maintenance;

    partial void OnCurrentRoleChanged(UserRole value)
    {
        OnPropertyChanged(nameof(IsLoggedIn));
        OnPropertyChanged(nameof(CanSeeMaintenance));
    }

    [RelayCommand]
    private void OpenLoginPopup()
    {
        LoginUsername = ""; LoginPassword = ""; LoginError = "";
        IsLoginPopupOpen = true;
    }

    [RelayCommand]
    private void CancelLogin() => IsLoginPopupOpen = false;

    [RelayCommand]
    private void SubmitLogin()
    {
        // Bắt buộc mật khẩu đúng 6 CHỮ SỐ, không cho chữ — kiểm tra ĐỊNH DẠNG trước khi so khớp tài khoản.
        if (LoginPassword.Length != 6 || !LoginPassword.All(char.IsDigit))
        {
            LoginError = "Mật khẩu phải gồm đúng 6 chữ số (0-9), không được có chữ.";
            return;
        }

        // So khớp Username/Password CÓ PHÂN BIỆT chữ hoa/thường (StringComparison.Ordinal — không dùng OrdinalIgnoreCase).
        var match = Accounts.FirstOrDefault(a =>
            string.Equals(a.Username, LoginUsername, StringComparison.Ordinal) &&
            string.Equals(a.Password, LoginPassword, StringComparison.Ordinal));

        if (match.Username is null)
        {
            LoginError = "Sai tài khoản hoặc mật khẩu.";
            return;
        }

        CurrentRole = match.Role;
        OperatorName = match.Username;
        IsLoginPopupOpen = false;
        LogActivity($"Đăng nhập: {match.Username} ({match.Role})", "-");
    }

    [RelayCommand]
    private void Logout()
    {
        LogActivity($"Đăng xuất: {OperatorName}", "-");
        CurrentRole = UserRole.Guest;
        OperatorName = "Operator";
    }

    // ==================================================================================
    // BUZZSTOP / ALMRESET — giống 2 nút CallDelegateButton cùng tên trong FuncMenu của CoreApp,
    // đặt cố định ở mọi màn hình con của Function Menu (ở đây đặt cố định trên Sidebar).
    // ==================================================================================

    [RelayCommand]
    private async Task BuzzStop()
    {
        if (await PulseAddressAsync(Addresses.BuzzStopAddress)) LogActivity("Dừng còi báo (BUZZSTOP)", "-");
    }

    [RelayCommand]
    private async Task AlarmReset()
    {
        if (!await PulseAddressAsync(Addresses.AlarmResetAddress)) return;
        // Giống ALMRESET của CoreApp: coi như người vận hành đã xác nhận toàn bộ cảnh báo đang hiện — đánh dấu ACK hết.
        foreach (var e in AlarmLog.Where(e => !e.Acknowledged).ToList()) { e.Acknowledged = true; e.AcknowledgedAt = DateTime.Now; }
        LogActivity("Reset cảnh báo (ALMRESET)", "-");
    }

    // ==================================================================================
    // "RECIPE START" — nút ở Tổng quan cho phép CHỌN và CHẠY 1 Recipe NGAY TẠI ĐÂY, không phải sang tab Công thức
    // trước (giống nút レシピ開始/"recipe kaishi" của CoreApp). Chỉ CHỌN + CHẠY ở đây — việc tạo/sửa Recipe vẫn
    // làm ở tab "Công thức" (mục 6), đúng yêu cầu "Recipe được tạo/sửa ở Công thức, nhưng bắt đầu từ Tổng quan".
    // ==================================================================================

    [ObservableProperty] private bool _isRecipeStartPopupOpen;
    [ObservableProperty] private RecipeLibraryEntry? _selectedStartRecipe;

    [RelayCommand]
    private void OpenRecipeStartPopup()
    {
        if (IsRecipeRunning) { StatusText = "Đang có Recipe chạy — Stop trước khi chọn Recipe khác."; return; }
        RefreshRecipeLibrary(); // Luôn load lại danh sách mới nhất (phòng khi vừa Save/Import/Delete ở tab Công thức)
        SelectedStartRecipe = RecipeLibrary.FirstOrDefault(r => string.Equals(r.FilePath, _currentEditingFilePath, StringComparison.OrdinalIgnoreCase));
        IsRecipeStartPopupOpen = true;
    }

    [RelayCommand]
    private void CancelRecipeStart() => IsRecipeStartPopupOpen = false;

    [RelayCommand]
    private async Task ConfirmRecipeStart()
    {
        if (SelectedStartRecipe is null) { StatusText = "Chưa chọn Recipe nào để bắt đầu."; return; }
        if (!TryLoadRecipeFromFile(SelectedStartRecipe.FilePath, SelectedStartRecipe.Name)) return;

        IsRecipeStartPopupOpen = false;
        StatusText = $"Bắt đầu Recipe \"{RecipeName}\" từ Tổng quan ({RecipeSteps.Count} bước)...";
        await RunRecipe(); // Gọi thẳng method nền của RunRecipeCommand — Steps vừa nạp xong chắc chắn Count > 0 nên bỏ qua CanExecute cũng an toàn
    }

    [ObservableProperty] private int _totalTarget = 500;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _okCount;
    [ObservableProperty] private int _ngCount;

    [ObservableProperty] private bool _isAutoMode = true;
    [ObservableProperty] private bool _isCellRunning;

    [ObservableProperty] private bool _isConveyorRunning;
    [ObservableProperty] private double _conveyorSpeedPercent = 50;

    /// <summary>Kết quả kiểm tra gần nhất — gắn cho khung "Hình ảnh từ Camera". Gọi ReportInspectionResult(...)
    /// từ FlowEditorViewModel sau mỗi lần chạy Flow để đồng bộ dữ liệu thật thay vì để trống/demo.</summary>
    [ObservableProperty] private bool _lastResultIsOk = true;
    [ObservableProperty] private double _lastGripX;
    [ObservableProperty] private double _lastGripY;
    [ObservableProperty] private double _lastGripR;

    public int TotalPercent => TotalTarget <= 0 ? 0 : (int)Math.Round(100.0 * TotalCount / TotalTarget);
    public double OkPercent => TotalCount <= 0 ? 0 : Math.Round(100.0 * OkCount / TotalCount, 1);
    public double NgPercent => TotalCount <= 0 ? 0 : Math.Round(100.0 * NgCount / TotalCount, 1);

    partial void OnTotalCountChanged(int value) { OnPropertyChanged(nameof(TotalPercent)); OnPropertyChanged(nameof(OkPercent)); OnPropertyChanged(nameof(NgPercent)); }
    partial void OnOkCountChanged(int value) { OnPropertyChanged(nameof(OkPercent)); }
    partial void OnNgCountChanged(int value) { OnPropertyChanged(nameof(NgPercent)); }
    partial void OnTotalTargetChanged(int value) { OnPropertyChanged(nameof(TotalPercent)); }


    public PcControlViewModel(PlcLinkHost plc, MesService mes)
    {
        _plc = plc;
        _mes = mes;
        ConnectionDescription = plc.Description;
        LinkState = plc.Link?.State ?? PlcLinkState.Disconnected;

        if (plc.Link is { } link)
            link.StateChanged += HandlePlcLinkStateChanged; // Bắn trên thread bất kỳ — post qua Dispatcher bên dưới

        // --- Khởi tạo 5 thẻ thiết bị hàng đầu Dashboard (đúng thứ tự ảnh mẫu) ---
        DeviceCards.Add(new DeviceStatusViewModel { Name = "PLC Mitsubishi", SubtitleText = "Q04UDEHCPU", IsRealHardware = true });
        DeviceCards.Add(new DeviceStatusViewModel { Name = "Camera Cognex", SubtitleText = "In-Sight", IsRealHardware = false, IsOnline = true, StatusText = "Online (mô phỏng)" });
        DeviceCards.Add(new DeviceStatusViewModel { Name = "Robot UR3e", SubtitleText = "UR3e", IsRealHardware = false, IsOnline = true, StatusText = "Online (mô phỏng)" });
        DeviceCards.Add(new DeviceStatusViewModel { Name = "Băng tải", SubtitleText = "Tốc độ: 50 %", IsRealHardware = false, IsOnline = true, StatusText = "Đang chạy (mô phỏng)" });
        DeviceCards.Add(new DeviceStatusViewModel { Name = "SQL Server", SubtitleText = "Lưu log & báo cáo", IsRealHardware = false, IsOnline = true, StatusText = "Kết nối (mô phỏng)" });

        // --- Khởi tạo bảng IO cố định (đúng địa chỉ trong Addresses — sửa ở tab "Cài đặt" nếu máy thật khác) ---
        RebuildSystemIoPoints();

        // --- Tải sẵn thư viện Recipe (thư mục Recipes/) để tab "Công thức" có danh sách ngay khi mở màn hình ---
        RecipeLibrary.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoRecipesInLibrary));
        RefreshRecipeLibrary();

        // Đồng hồ hiển thị góc trên Dashboard — 1 giây/lần, không liên quan gì tới poll PLC.
        _clockTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => { CurrentTime = DateTime.Now; PendingWriteCount = _mes.PendingWriteCount; };
        _clockTimer.Start();

        // Poll định kỳ: đọc các IoPoint có AutoPoll = true + kiểm tra AlarmRules + refresh SystemIoPoints — chạy
        // trên UI thread nên an toàn cập nhật ObservableCollection trực tiếp mà KHÔNG cần Dispatcher.Invoke thủ công.
        _pollTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(300) };
        _pollTimer.Tick += async (_, _) => await PollOnceAsync();
        _pollTimer.Start();
    }

    /// <summary>Dựng lại danh sách IO cố định theo Addresses hiện tại — gọi lại khi người dùng đổi địa chỉ ở tab "Cài đặt".</summary>
    private void RebuildSystemIoPoints()
    {
        SystemIoPoints.Clear();
        SystemIoPoints.Add(new SystemIoPointViewModel { Address = Addresses.SensorHasPartAddress, Label = "Có sản phẩm", IsOutput = false });
        SystemIoPoints.Add(new SystemIoPointViewModel { Address = Addresses.SensorGripPositionAddress, Label = "Vị trí gắp", IsOutput = false });
        SystemIoPoints.Add(new SystemIoPointViewModel { Address = Addresses.ConveyorStopAddress, Label = "Dừng băng tải", IsOutput = true });
        SystemIoPoints.Add(new SystemIoPointViewModel { Address = Addresses.TriggerCameraAddress, Label = "Trigger camera", IsOutput = true });
        SystemIoPoints.Add(new SystemIoPointViewModel { Address = Addresses.RobotGripAddress, Label = "Lệnh gắp robot", IsOutput = true });
        SystemIoPoints.Add(new SystemIoPointViewModel { Address = Addresses.RobotPlaceOkAddress, Label = "Đặt OK", IsOutput = true });
        SystemIoPoints.Add(new SystemIoPointViewModel { Address = Addresses.RobotPlaceNgAddress, Label = "Đặt NG", IsOutput = true });
    }

    [RelayCommand]
    private void ApplyAddressSettings() => RebuildSystemIoPoints();

    private void HandlePlcLinkStateChanged(PlcLinkState state)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            LinkState = state;
            var plcCard = DeviceCards.FirstOrDefault(d => d.IsRealHardware);
            if (plcCard is not null)
            {
                plcCard.IsOnline = state == PlcLinkState.Connected;
                plcCard.StatusText = state switch
                {
                    PlcLinkState.Connected => "Online",
                    PlcLinkState.Connecting => "Đang kết nối",
                    PlcLinkState.Faulted => "Lỗi",
                    _ => "Offline",
                };
            }
        });
    }

    // ==================================================================================
    // DASHBOARD — Điều khiển hệ thống (Bắt đầu/Dừng/Reset/Tự động-Bằng tay)
    // ==================================================================================

    [RelayCommand]
    private async Task EnableAutoMode() => await SetAutoMode(true);

    [RelayCommand]
    private async Task EnableManualMode() => await SetAutoMode(false);

    private async Task SetAutoMode(bool auto)
    {
        if (!await TryPulseAsync(Addresses.AutoModeAddress, holdValue: auto)) return; // Bit giữ trạng thái (không phải xung)
        IsAutoMode = auto;
        LogActivity(auto ? "Chuyển chế độ Tự động" : "Chuyển chế độ Bằng tay", "-");
    }

    [RelayCommand]
    private async Task StartSystem()
    {
        if (!await PulseAddressAsync(Addresses.StartAddress)) return; // PLC lỗi -> dừng, giữ nguyên thông báo lỗi
        IsCellRunning = true;
        LogActivity("Bắt đầu hệ thống", "-");
    }

    [RelayCommand]
    private async Task StopSystem()
    {
        if (!await PulseAddressAsync(Addresses.StopAddress)) return;
        IsCellRunning = false;
        LogActivity("Dừng hệ thống", "-");
    }

    [RelayCommand]
    private async Task ResetSystem()
    {
        if (!await PulseAddressAsync(Addresses.ResetAddress)) return;
        TotalCount = 0; OkCount = 0; NgCount = 0;
        LogActivity("Reset hệ thống", "-");
    }

    // ==================================================================================
    // DASHBOARD — Điều khiển Robot (Gắp / Đặt OK / Đặt NG / Home)
    // ==================================================================================

    [RelayCommand]
    private async Task RobotGrip()
    {
        if (await PulseAddressAsync(Addresses.RobotGripAddress)) LogActivity("Gắp phôi", "-");
    }

    [RelayCommand]
    private async Task RobotPlaceOk()
    {
        if (!await PulseAddressAsync(Addresses.RobotPlaceOkAddress)) return;
        TotalCount++; OkCount++;
        LogActivity("Gắp và đặt", "OK");
    }

    [RelayCommand]
    private async Task RobotPlaceNg()
    {
        if (!await PulseAddressAsync(Addresses.RobotPlaceNgAddress)) return;
        TotalCount++; NgCount++;
        LogActivity("Gắp và đặt", "NG");
    }

    [RelayCommand]
    private async Task RobotHome()
    {
        if (await PulseAddressAsync(Addresses.RobotHomeAddress)) LogActivity("Robot về Home", "-");
    }

    // ==================================================================================
    // DASHBOARD — Điều khiển băng tải (Chạy/Dừng + tốc độ %)
    // ==================================================================================

    [RelayCommand]
    private async Task ConveyorRun()
    {
        if (!await TryPulseAsync(Addresses.ConveyorStopAddress, holdValue: false)) return; // Y0 OFF = cho chạy
        IsConveyorRunning = true;
        LogActivity("Chạy băng tải", "-");
    }

    [RelayCommand]
    private async Task ConveyorStop()
    {
        if (!await TryPulseAsync(Addresses.ConveyorStopAddress, holdValue: true)) return; // Y0 ON = dừng
        IsConveyorRunning = false;
        LogActivity("Dừng băng tải", "-");
    }

    [RelayCommand]
    private async Task ApplyConveyorSpeed()
    {
        if (!PlcAddress.TryParse(Addresses.ConveyorSpeedAddress, out var addr) || _plc.Link is not { } link)
        { StatusText = "Địa chỉ tốc độ băng tải không hợp lệ hoặc chưa có PLC."; return; }
        try
        {
            await PausePollDuring(() => link.WriteWordAsync(addr, (ushort)Math.Clamp(ConveyorSpeedPercent, 0, 100)));
            LogActivity($"Đặt tốc độ băng tải {ConveyorSpeedPercent:0} %", "-");
        }
        catch (Exception ex) { StatusText = $"Lỗi đặt tốc độ băng tải: {ex.Message}"; }
    }

    /// <summary>Tạm DỪNG hẳn Timer Auto Poll trong lúc thực hiện 1 thao tác Ghi, rồi mới cho Poll chạy lại.
    /// Lý do cần: McProtocolClient chỉ cho 1 lệnh bay tại một thời điểm (FIFO, không phân biệt ưu tiên) — nếu không
    /// tạm dừng Poll, lệnh Ghi của bạn phải xếp hàng ngang với các lệnh Đọc tự động đang bắn liên tục mỗi 300ms,
    /// có thể trễ hoặc (nếu Poll cứ liên tục xếp thêm) khiến Ghi cảm giác như "không ăn". Dừng Poll trong lúc Ghi
    /// đảm bảo lệnh Ghi được xử lý gần như ngay lập tức, đúng yêu cầu "Write ưu tiên trước Read".</summary>
    private async Task PausePollDuring(Func<Task> action)
    {
        _pollTimer.Stop();
        try { await action(); }
        finally { _pollTimer.Start(); }
    }

    // --- Helper dùng chung cho các nút Dashboard: ghi ON rồi tự OFF lại sau Addresses.PulseMs.
    // Trả về true nếu ghi PLC thành công; false nếu lỗi (StatusText đã được set sẵn thông báo lỗi bên trong).
    private async Task<bool> PulseAddressAsync(string addressText)
    {
        if (!PlcAddress.TryParse(addressText, out var addr) || _plc.Link is not { } link)
        { StatusText = $"Địa chỉ '{addressText}' không hợp lệ hoặc chưa có PLC."; return false; }
        try
        {
            await PausePollDuring(async () =>
            {
                await link.WriteBitAsync(addr, true);
                await Task.Delay(Math.Max(10, Addresses.PulseMs));
                await link.WriteBitAsync(addr, false);
            });
            return true;
        }
        catch (Exception ex) { StatusText = $"Lỗi thao tác {addressText}: {ex.Message}"; return false; }
    }

    // --- Helper cho các bit GIỮ trạng thái (không phải xung), vd Auto/Manual, Dừng băng tải. Cùng quy ước trả bool như trên.
    private async Task<bool> TryPulseAsync(string addressText, bool holdValue)
    {
        if (!PlcAddress.TryParse(addressText, out var addr) || _plc.Link is not { } link)
        { StatusText = $"Địa chỉ '{addressText}' không hợp lệ hoặc chưa có PLC."; return false; }
        try { await PausePollDuring(() => link.WriteBitAsync(addr, holdValue)); return true; }
        catch (Exception ex) { StatusText = $"Lỗi thao tác {addressText}: {ex.Message}"; return false; }
    }

    private void LogActivity(string eventText, string result)
    {
        ActivityLog.Insert(0, new ActivityLogEntry { EventText = eventText, Result = result });
        StatusText = $"{eventText} — đã thực hiện lúc {DateTime.Now:HH:mm:ss}."; // <-- THÊM: luôn báo ngay, dù MES thành công hay chưa
        FireAndForgetMes(() => _mes.RecordActivityAsync(eventText, result)); // Nếu lưu MES/DB lỗi, dòng trên sẽ bị ghi đè bởi dòng lỗi bên dưới
    }

    /// <summary>Chạy 1 lệnh MES ở chế độ "bắn rồi quên" (fire-and-forget) — KHÔNG chặn UI chờ SQL trả lời.
    /// Nếu lỗi (mất kết nối DB...) chỉ hiện lên StatusText, không làm crash hay đứng màn hình.</summary>
    private async void FireAndForgetMes(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { StatusText = $"Lỗi lưu MES/DB: {ex.Message}"; }
    }

    /// <summary>Gọi hàm này từ FlowEditorViewModel sau mỗi lần chạy Flow xong, để khung "Hình ảnh từ Camera"
    /// và số liệu OK/NG trên Dashboard phản ánh ĐÚNG kết quả kiểm tra thật thay vì chỉ cập nhật khi bấm tay
    /// qua RobotPlaceOk/RobotPlaceNg. Không bắt buộc — bỏ qua nếu bạn chỉ cần vận hành tay qua Dashboard.</summary>
    public void ReportInspectionResult(bool isOk, double gripX, double gripY, double gripR)
    {
        LastResultIsOk = isOk;
        LastGripX = gripX; LastGripY = gripY; LastGripR = gripR;
        TotalCount++;
        if (isOk) OkCount++; else NgCount++;
        LogActivity("Kiểm tra vision", isOk ? "OK" : "NG");
        FireAndForgetMes(() => _mes.RecordInspectionAsync(isOk, gripX, gripY, gripR, RecipeName)); // <-- THÊM: ghi xuống MES/DB
    }

    // ==================================================================================
    // BÁO CÁO — đọc NGƯỢC dữ liệu từ MES/DB lên UI (chứng minh chiều đọc, không chỉ ghi)
    // ==================================================================================

    [ObservableProperty] private bool _isLoadingReport;
    [ObservableProperty] private int _reportTotalCount;
    [ObservableProperty] private int _reportOkCount;
    [ObservableProperty] private int _reportNgCount;
    public ObservableCollection<ActivityRecord> ReportRecentActivity { get; } = new();

    [RelayCommand]
    private async Task RefreshReport()
    {
        IsLoadingReport = true;
        try
        {
            var summary = await _mes.GetTodaySummaryAsync();
            ReportTotalCount = summary.TotalCount;
            ReportOkCount = summary.OkCount;
            ReportNgCount = summary.NgCount;

            var recent = await _mes.GetRecentActivityAsync(50);
            ReportRecentActivity.Clear();
            foreach (var item in recent) ReportRecentActivity.Add(item);

            StatusText = $"Đã tải Báo cáo từ MES/DB lúc {DateTime.Now:HH:mm:ss}.";
        }
        catch (Exception ex)
        {
            StatusText = $"Không tải được Báo cáo: {ex.Message}";
        }
        finally
        {
            IsLoadingReport = false;
        }
    }

    // ==================================================================================
    // KẾT NỐI
    // ==================================================================================

    [RelayCommand]
    private async Task Connect()
    {
        if (_plc.Link is null)
        {
            StatusText = "Không có đường truyền PLC — kiểm tra lại mục \"PlcLink\" trong appsettings.json.";
            return;
        }
        try
        {
            StatusText = $"Đang kết nối {_plc.Description}...";
            await _plc.Link.ConnectAsync();
            StatusText = $"Đã kết nối {_plc.Description}.";
        }
        catch (Exception ex)
        {
            StatusText = $"Kết nối PLC thất bại: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task Disconnect()
    {
        if (_plc.Link is null) return;
        await _plc.Link.DisconnectAsync();
        StatusText = "Đã ngắt kết nối PLC.";
    }

    // ==================================================================================
    // MANUAL I/O — thêm/xoá điểm theo dõi, đọc/ghi tay từng điểm
    // ==================================================================================

    [RelayCommand]
    private void AddIoPoint() => IoPoints.Add(new IoPointViewModel());

    [RelayCommand]
    private void RemoveIoPoint(IoPointViewModel point) => IoPoints.Remove(point);

    [RelayCommand]
    private async Task ReadIoPoint(IoPointViewModel point) => await ReadPointAsync(point, isManual: true);

    [RelayCommand]
    private async Task WriteIoPoint(IoPointViewModel point)
    {
        if (_plc.Link is not { } link) { point.StatusText = "Chưa có PLC."; return; }
        if (!PlcAddress.TryParse(point.Address, out var addr)) { point.StatusText = "Địa chỉ sai."; return; }

        try
        {
            if (point.Kind == IoKind.Bit)
            {
                bool readBack = false;
                // Dừng Auto Poll trong lúc Ghi + đọc lại kiểm tra -> đảm bảo không có lệnh Đọc nào của Poll
                // chen ngang ngay giữa lúc đang chẩn đoán (nếu không dừng, có thể tự nhầm lẫn do Poll đọc xen kẽ).
                await PausePollDuring(async () =>
                {
                    await link.WriteBitAsync(addr, point.BoolValue);

                    // CHẨN ĐOÁN: đọc lại NGAY (trong vòng vài chục ms) để biết giá trị có giữ được dù chỉ 1
                    // khoảnh khắc hay bị PLC/ladder trả về ngay lập tức.
                    await Task.Delay(50);
                    readBack = await link.ReadBitAsync(addr);
                });

                point.StatusText = readBack == point.BoolValue
                    ? $"OK — đã ghi vào {addr} và đọc lại khớp lúc {DateTime.Now:HH:mm:ss}"
                    : $"CẢNH BÁO — ghi {point.BoolValue} vào {addr} nhưng đọc lại ngay ra {readBack}: có thứ gì trong PLC đang ghi đè {addr} (kiểm tra ladder/CPU RUN-STOP).";
            }
            else
            {
                await PausePollDuring(() => link.WriteWordAsync(addr, ToRawWord(point.WordValue, point.Scale)));
                point.StatusText = $"OK — đã ghi vào {addr} lúc {DateTime.Now:HH:mm:ss}";
            }
        }
        catch (Exception ex)
        {
            point.StatusText = $"Lỗi ghi: {ex.Message}";
        }
    }

    /// <param name="isManual">true = do người dùng bấm nút "Read" (được phép xóa/ghi đè StatusText khi thành công);
    /// false = do vòng lặp Auto Poll gọi ngầm (KHÔNG được xóa StatusText khi thành công, để không đè mất thông báo
    /// "OK — đã ghi" vừa hiện ra sau khi bấm Write — đây chính là lỗi trước đó khiến thông báo hiện rồi tắt ngay).

    private async Task ReadPointAsync(IoPointViewModel point, bool isManual = false)
    {
        if (_plc.Link is not { } link) { if (isManual) point.StatusText = "Chưa có PLC."; return; }
        if (!PlcAddress.TryParse(point.Address, out var addr)) { if (isManual) point.StatusText = "Địa chỉ sai."; return; }

        try
        {
            if (point.Kind == IoKind.Bit)
                point.BoolValue = await link.ReadBitAsync(addr);
            else
                point.WordValue = FromRawWord(await link.ReadWordAsync(addr), point.Scale);

            if (isManual) point.StatusText = $"Đã đọc lúc {DateTime.Now:HH:mm:ss}"; // Auto Poll: giữ nguyên StatusText cũ
        }
        catch (Exception ex)
        {
            point.StatusText = $"Lỗi đọc: {ex.Message}"; // Lỗi thì LUÔN hiện, kể cả khi đang Auto Poll
        }
    }

    private static ushort ToRawWord(double real, double scale) =>
        unchecked((ushort)(short)Math.Round(real * (scale == 0 ? 1 : scale), MidpointRounding.AwayFromZero));

    private static double FromRawWord(ushort raw, double scale) => (short)raw / (scale == 0 ? 1 : scale);

    // ==================================================================================
    // RECIPE — chạy tuần tự các Step (ghi giá trị hoặc chờ điều kiện), giống RecipeExecute của CoreApp
    // nhưng đơn giản hoá: 1 danh sách Step tuyến tính, không rẽ nhánh.
    // ==================================================================================

    [RelayCommand]
    private void AddRecipeStep()
    {
        RecipeSteps.Add(new RecipeStepViewModel { Order = RecipeSteps.Count + 1 });
        Renumber();
    }

    [RelayCommand]
    private void RemoveRecipeStep(RecipeStepViewModel step)
    {
        RecipeSteps.Remove(step);
        Renumber();
    }

    private void Renumber()
    {
        for (int i = 0; i < RecipeSteps.Count; i++) RecipeSteps[i].Order = i + 1;
        RunRecipeCommand.NotifyCanExecuteChanged(); // <-- THÊM: báo lại nút Run biết Count vừa đổi
        RecalculateRecipeTiming(); // <-- THÊM: Total time panel "Thông số công thức" đổi theo mỗi khi Add/Remove Step
    }

    // ==================================================================================
    // THÔNG SỐ CÔNG THỨC (Recipe Parameter) — panel bên phải màn hình "Tổng quan", mô phỏng đúng các ô
    // Recipe No./Name, Step No./Name, Total/Remaining time, Status, Main alarm... của CoreApp.
    // ==================================================================================

    [ObservableProperty] private int _recipeNo = 1;
    [ObservableProperty] private RecipeExecutionStatus _recipeStatus = RecipeExecutionStatus.Standby;

    [ObservableProperty] private TimeSpan _recipeTotalTime;
    [ObservableProperty] private TimeSpan _recipeRemainingTime;
    [ObservableProperty] private int _currentStepNo;
    [ObservableProperty] private string _currentStepName = "-";
    [ObservableProperty] private TimeSpan _stepTotalTime;
    [ObservableProperty] private TimeSpan _stepRemainingTime;

    [ObservableProperty] private string _mainAlarmText = "Không có cảnh báo";

    // Robot/Camera status: lấy lại đúng StatusText đang hiển thị trên 2 thẻ thiết bị ở hàng đầu Dashboard
    // (DeviceCards[1] = Camera Cognex, DeviceCards[2] = Robot UR3e — đúng thứ tự khởi tạo trong constructor).
    public string RobotStatusText => DeviceCards.Count > 2 ? DeviceCards[2].StatusText : "-";
    public string CameraStatusText => DeviceCards.Count > 1 ? DeviceCards[1].StatusText : "-";

    public string RecipeTotalTimeText => FormatHms(RecipeTotalTime);
    public string RecipeRemainingTimeText => FormatHms(RecipeRemainingTime);
    public string StepTotalTimeText => FormatHms(StepTotalTime);
    public string StepRemainingTimeText => FormatHms(StepRemainingTime);

    private static string FormatHms(TimeSpan t) => $"{(int)t.TotalHours}h {t.Minutes}m {t.Seconds}s";

    partial void OnRecipeTotalTimeChanged(TimeSpan value) => OnPropertyChanged(nameof(RecipeTotalTimeText));
    partial void OnRecipeRemainingTimeChanged(TimeSpan value) => OnPropertyChanged(nameof(RecipeRemainingTimeText));
    partial void OnStepTotalTimeChanged(TimeSpan value) => OnPropertyChanged(nameof(StepTotalTimeText));
    partial void OnStepRemainingTimeChanged(TimeSpan value) => OnPropertyChanged(nameof(StepRemainingTimeText));

    /// <summary>Tính lại Total time (tổng cộng dồn DelayAfterMs/WaitTimeoutMs mọi Step) — gọi lại mỗi khi danh sách
    /// Step thay đổi (Renumber) hoặc sau khi Load 1 Recipe khác. CHỈ LÀ ƯỚC TÍNH cho bước Wait (dùng Timeout làm
    /// mốc tối đa vì thời gian chờ thật phụ thuộc PLC, không biết trước được).</summary>
    private void RecalculateRecipeTiming()
    {
        double totalSeconds = RecipeSteps.Sum(s => (s.IsWaitStep ? s.WaitTimeoutMs : s.DelayAfterMs) / 1000.0);
        RecipeTotalTime = TimeSpan.FromSeconds(totalSeconds);
        if (!IsRecipeRunning) RecipeRemainingTime = RecipeTotalTime;
    }

    private void RefreshMainAlarmText()
    {
        var active = AlarmLog.FirstOrDefault(e => !e.Acknowledged);
        MainAlarmText = active is null ? "Không có cảnh báo" : $"{active.RuleName}: {active.Message}";
    }

    /// <summary>Trạng thái THỰC SỰ hiển thị lên panel "Thông số công thức": khi Recipe đang chạy hoặc vừa lỗi thì
    /// hiện đúng RecipeStatus; còn lúc rảnh (Standby) mà PLC CHƯA kết nối thì hiện Idling thay vì Standby —
    /// cách phân biệt "đang chờ Recipe" (có PLC, sẵn sàng chạy) với "chưa sẵn sàng" (mất kết nối) theo đúng ý
    /// nghĩa STANDBY/IDLE trong CoreApp.</summary>
    public RecipeExecutionStatus DisplayedRecipeStatus =>
        RecipeStatus is RecipeExecutionStatus.Running or RecipeExecutionStatus.Error or RecipeExecutionStatus.Paused
            ? RecipeStatus
            : (LinkState == PlcLinkState.Connected ? RecipeExecutionStatus.Standby : RecipeExecutionStatus.Idling);

    partial void OnLinkStateChanged(PlcLinkState value) => OnPropertyChanged(nameof(DisplayedRecipeStatus));
    partial void OnRecipeStatusChanged(RecipeExecutionStatus value)
    {
        OnPropertyChanged(nameof(DisplayedRecipeStatus));
        OnPropertyChanged(nameof(PauseResumeButtonText)); // <-- THÊM (mục 8): đổi nhãn nút Pause/Resume theo Paused hay không
    }

    private readonly System.Diagnostics.Stopwatch _recipeStopwatch = new();
    private readonly System.Diagnostics.Stopwatch _stepStopwatch = new();
    private DispatcherTimer? _recipeProgressTimer;

    private void UpdateRecipeProgress()
    {
        StepRemainingTime = TimeSpanMax(StepTotalTime - _stepStopwatch.Elapsed, TimeSpan.Zero);
        RecipeRemainingTime = TimeSpanMax(RecipeTotalTime - _recipeStopwatch.Elapsed, TimeSpan.Zero);
    }

    private static TimeSpan TimeSpanMax(TimeSpan a, TimeSpan b) => a > b ? a : b;

    // ==================================================================================
    // "PROCESS CHANGE" / "PROCESS MONITOR" (mục 8) — "Process change" hiện dãy nút Start/Pause/Skip/Reset để
    // CAN THIỆP vào Recipe đang chạy (giống "Process sousa" của CoreApp); "Process monitor" ẩn dãy nút đó đi,
    // chỉ còn xem tiến trình (giống chế độ "chỉ theo dõi", tránh bấm nhầm khi không cần thao tác).
    // ==================================================================================

    [ObservableProperty] private bool _isProcessControlVisible;

    [RelayCommand] private void ShowProcessChange() => IsProcessControlVisible = true;
    [RelayCommand] private void ShowProcessMonitor() => IsProcessControlVisible = false;

    /// <summary>CancellationTokenSource của RIÊNG bước đang chạy hiện tại — null khi không có Recipe nào đang chạy.
    /// SkipCurrentStepCommand Cancel đúng cái này để bỏ qua 1 bước mà KHÔNG dừng cả Recipe (khác với StopRecipe/
    /// ResetRecipeProgress, vốn Cancel _recipeCts để dừng HẲN).</summary>
    private CancellationTokenSource? _currentStepSkipCts;

    private bool CanControlRunningRecipe() => IsRecipeRunning;

    /// <summary>Nhãn nút Pause đổi thành "Resume" khi Recipe đang Paused — 1 nút làm 2 việc, giống hành vi
    /// nút Pause/Resume thường thấy trên các màn hình vận hành công nghiệp (tránh phải có 2 nút riêng chồng chéo).</summary>
    public string PauseResumeButtonText => RecipeStatus == RecipeExecutionStatus.Paused ? "▶ Resume" : "⏸ Pause";

    [RelayCommand(CanExecute = nameof(CanControlRunningRecipe))]
    private void PauseResumeRecipe()
    {
        if (RecipeStatus == RecipeExecutionStatus.Paused)
        {
            RecipeStatus = RecipeExecutionStatus.Running;
            _recipeStopwatch.Start(); // Stop() rồi Start() lại cộng dồn tiếp thời gian đã trôi qua, KHÔNG reset về 0
            _stepStopwatch.Start();
            LogActivity("Resume Recipe", "-");
        }
        else
        {
            RecipeStatus = RecipeExecutionStatus.Paused;
            _recipeStopwatch.Stop();
            _stepStopwatch.Stop();
            LogActivity("Pause Recipe", "-");
        }
    }

    [RelayCommand(CanExecute = nameof(CanControlRunningRecipe))]
    private void SkipCurrentStep()
    {
        // Nếu đang Paused mà bấm Skip -> hiểu là "bỏ qua bước này rồi chạy tiếp luôn", không để kẹt Paused mãi.
        if (RecipeStatus == RecipeExecutionStatus.Paused)
        {
            RecipeStatus = RecipeExecutionStatus.Running;
            _recipeStopwatch.Start();
            _stepStopwatch.Start();
        }
        int skippedStepNo = CurrentStepNo;
        _currentStepSkipCts?.Cancel();
        LogActivity($"Skip bước {skippedStepNo}", "-");
    }

    [RelayCommand]
    private void ResetRecipeProgress()
    {
        if (IsRecipeRunning)
        {
            // Đang chạy -> Reset nghĩa là HUỶ HẲN (giống Stop); nhánh catch(OperationCanceledException) sẵn có trong
            // RunRecipe sẽ tự đưa RecipeStatus về Standby và dọn sạch Step/Remaining trong khối finally.
            _recipeCts?.Cancel();
            LogActivity("Reset tiến trình Recipe (đang chạy -> dừng)", "-");
        }
        else
        {
            // Không chạy -> chỉ cần xoá sạch trạng thái Error/Paused còn sót + đưa Step/Remaining về 0, KHÔNG động
            // tới Recipe đã nạp (RecipeSteps/RecipeName giữ nguyên để có thể Start lại ngay).
            RecipeStatus = RecipeExecutionStatus.Standby;
            CurrentStepNo = 0;
            CurrentStepName = "-";
            StepTotalTime = TimeSpan.Zero;
            StepRemainingTime = TimeSpan.Zero;
            RecalculateRecipeTiming();
            LogActivity("Reset tiến trình Recipe", "-");
        }
    }

    /// <summary>Chặn lại khi Recipe đang ở trạng thái Paused — gọi TRƯỚC thao tác PLC của mỗi bước trong RunRecipe.
    /// Nhận stepCt (không phải ct chính) để SkipCurrentStepCommand cũng bỏ qua được 1 bước đang đứng chờ Pause ở đây.</summary>
    private async Task WaitWhilePausedAsync(CancellationToken ct)
    {
        while (RecipeStatus == RecipeExecutionStatus.Paused)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(100, ct);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunRecipe))]
    private async Task RunRecipe()
    {
        if (_plc.Link is not { } link) { StatusText = "Không có PLC."; return; }

        IsRecipeRunning = true;
        RecipeStatus = RecipeExecutionStatus.Running; // <-- THÊM
        _recipeCts = new CancellationTokenSource();
        var ct = _recipeCts.Token;
        _pollTimer.Stop(); // Dừng Auto Poll suốt Recipe — tránh Poll chen ngang giữa các bước điều khiển tuần tự

        // THÊM: ghi nhận mốc bắt đầu để lưu vào "Operation history" (RecipeRunHistory) khi kết thúc, dù kết quả thế nào.
        var runStartedAt = DateTime.Now;
        int runStepCount = RecipeSteps.Count;
        int runRecipeNo = RecipeNo;
        string runRecipeName = RecipeName;
        string runResult = "Error"; // Giá trị mặc định phòng hờ — luôn bị ghi đè bởi 1 trong 3 nhánh catch/try bên dưới trước khi tới finally

        // THÊM: khởi động đồng hồ đếm Total/Remaining time cho panel "Thông số công thức"
        RecalculateRecipeTiming();
        _recipeStopwatch.Restart();
        _recipeProgressTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
        _recipeProgressTimer.Tick += (_, _) => UpdateRecipeProgress();
        _recipeProgressTimer.Start();

        try
        {
            foreach (var step in RecipeSteps)
            {
                step.IsCurrent = true;
                StatusText = $"Recipe: đang chạy bước {step.Order} — {step.Description}";

                // THÊM: cập nhật Step No./Name + Step Total time + reset đồng hồ Step cho panel "Thông số công thức"
                CurrentStepNo = step.Order;
                CurrentStepName = step.Description;
                StepTotalTime = TimeSpan.FromMilliseconds(step.IsWaitStep ? step.WaitTimeoutMs : step.DelayAfterMs);
                _stepStopwatch.Restart();

                // THÊM (mục 8 — "Process change"/Skip): 1 CancellationTokenSource RIÊNG cho từng bước, link với ct chính
                // của cả Recipe — SkipCurrentStepCommand chỉ Cancel cái này (bỏ qua 1 bước), KHÔNG đụng tới ct chính
                // (ct chính chỉ bị Cancel bởi Stop/Reset, nghĩa là dừng HẲN cả Recipe).
                using var stepSkipCts = new CancellationTokenSource();
                _currentStepSkipCts = stepSkipCts;
                using var stepLinkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, stepSkipCts.Token);
                var stepCt = stepLinkedCts.Token;

                try
                {
                    // THÊM (mục 8 — Pause/Resume): chặn lại TRƯỚC KHI thao tác PLC của bước này, nếu đang Paused.
                    // Dùng stepCt (không phải ct) để Skip cũng bỏ qua được 1 bước đang bị Pause đứng chờ ở đây.
                    await WaitWhilePausedAsync(stepCt);

                    if (!PlcAddress.TryParse(step.Address, out var addr))
                        throw new InvalidOperationException($"Bước {step.Order}: địa chỉ '{step.Address}' không hợp lệ.");

                    if (step.IsWaitStep)
                    {
                        await WaitBitAsync(link, addr, step.Value != 0, step.WaitTimeoutMs, stepCt);
                    }
                    else if (step.Kind == IoKind.Bit)
                    {
                        await link.WriteBitAsync(addr, step.Value != 0, stepCt);
                    }
                    else
                    {
                        await link.WriteWordAsync(addr, ToRawWord(step.Value, step.Scale), stepCt);
                    }

                    step.IsCurrent = false;
                    if (step.DelayAfterMs > 0) await Task.Delay(step.DelayAfterMs, stepCt);
                }
                catch (OperationCanceledException) when (stepSkipCts.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    // Bị Skip (không phải Stop/Reset cả Recipe) — coi bước này là xong, sang bước kế tiếp luôn.
                    step.IsCurrent = false;
                    StatusText = $"Đã Skip bước {step.Order} — {step.Description}.";
                }
                finally
                {
                    _currentStepSkipCts = null;
                }
            }

            StatusText = $"Recipe \"{RecipeName}\" chạy xong ({RecipeSteps.Count} bước).";
            RecipeStatus = RecipeExecutionStatus.Standby; // <-- THÊM
            runResult = "OK"; // <-- THÊM
        }
        catch (OperationCanceledException)
        {
            StatusText = "Recipe đã bị dừng.";
            RecipeStatus = RecipeExecutionStatus.Standby; // <-- THÊM: người dùng chủ động Stop, không tính là Error
            runResult = "Stopped"; // <-- THÊM
        }
        catch (Exception ex)
        {
            StatusText = $"Recipe lỗi: {ex.Message}";
            RecipeStatus = RecipeExecutionStatus.Error; // <-- THÊM
            runResult = "Error"; // <-- THÊM
        }
        finally
        {
            foreach (var step in RecipeSteps) step.IsCurrent = false;
            IsRecipeRunning = false;
            _recipeCts?.Dispose();
            _recipeCts = null;
            _pollTimer.Start(); // Cho Auto Poll chạy lại sau khi Recipe kết thúc (dù xong, lỗi hay bị Stop)

            // THÊM: dừng đồng hồ tiến trình, đưa Step/Remaining về trạng thái nghỉ
            _recipeProgressTimer?.Stop();
            _recipeProgressTimer = null;
            CurrentStepNo = 0;
            CurrentStepName = "-";
            StepTotalTime = TimeSpan.Zero;
            StepRemainingTime = TimeSpan.Zero;
            RecipeRemainingTime = RecipeTotalTime;

            // THÊM: ghi 1 dòng vào "Operation history" (RecipeRunHistory) — luôn chạy dù OK/Stopped/Error,
            // vì finally luôn thực thi sau khi 1 trong 3 nhánh trên đã set runResult.
            var runEndedAt = DateTime.Now;
            var duration = runEndedAt - runStartedAt;
            RecipeRunHistory.Insert(0, new RecipeRunHistoryEntry
            {
                No = _nextRunHistoryNo++,
                RecipeNo = runRecipeNo,
                RecipeName = runRecipeName,
                StartTime = runStartedAt,
                EndTime = runEndedAt,
                DurationText = $"{(int)duration.TotalHours}h {duration.Minutes}m {duration.Seconds}s",
                Result = runResult,
                StepCount = runStepCount,
            });
        }
    }

    private bool CanRunRecipe() => !IsRecipeRunning && RecipeSteps.Count > 0;

    [RelayCommand]
    private void StopRecipe() => _recipeCts?.Cancel();

    private static async Task WaitBitAsync(IPlcLink link, PlcAddress addr, bool target, int timeoutMs, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (await link.ReadBitAsync(addr, ct) == target) return;
            if (sw.ElapsedMilliseconds >= timeoutMs)
                throw new TimeoutException($"Chờ {addr} = {(target ? "ON" : "OFF")} quá {timeoutMs} ms.");
            await Task.Delay(20, ct);
        }
    }

    /// <summary>Lưu Recipe đang mở trong màn Edit xuống ĐÚNG file của nó trong thư viện (Recipes/). Lần Save đầu tiên
    /// của 1 Recipe MỚI (chưa gắn file) sẽ tạo file theo RecipeName hiện tại; các lần Save sau đó luôn ghi đè lại
    /// đúng file đang mở (_currentEditingFilePath), dù RecipeName có bị đổi — tránh tạo file rác trùng lặp mỗi lần Save.
    /// Thay cho hộp thoại "Save As" cũ — đúng tinh thần "sửa xong Save, không phải tự chọn nơi lưu mỗi lần" của CoreApp.</summary>
    [RelayCommand]
    private void SaveRecipe()
    {
        if (string.IsNullOrWhiteSpace(RecipeName)) { StatusText = "Chưa đặt tên Recipe."; return; }
        try
        {
            Directory.CreateDirectory(RecipeFolderPath);
            string path = _currentEditingFilePath ?? Path.Combine(RecipeFolderPath, RecipeName + ".json");

            var file = new RecipeFile
            {
                Name = RecipeName,
                Steps = RecipeSteps.Select(s => new RecipeStepData
                {
                    Description = s.Description,
                    Address = s.Address,
                    Kind = s.Kind,
                    Value = s.Value,
                    Scale = s.Scale,
                    DelayAfterMs = s.DelayAfterMs,
                    IsWaitStep = s.IsWaitStep,
                    WaitTimeoutMs = s.WaitTimeoutMs
                }).ToList()
            };
            File.WriteAllText(path, JsonSerializer.Serialize(file, JsonOptions));
            _currentEditingFilePath = path;
            StatusText = $"Đã lưu Recipe: {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            StatusText = $"Lưu Recipe thất bại: {ex.Message}";
        }
    }

    // ==================================================================================
    // THƯ VIỆN RECIPE (tab "Công thức") — Create new / Edit / Copy / Paste / Delete / Back / Import / Export,
    // mô phỏng đúng màn hình chọn Recipe của CoreApp. Mỗi Recipe là 1 file .json riêng trong thư mục RecipeFolderPath
    // (mặc định "Recipes" cạnh file .exe) — KHÔNG còn phải tự mở/lưu bằng hộp thoại mỗi lần như bản cũ.
    // ==================================================================================

    private static readonly string RecipeFolderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Recipes");

    public ObservableCollection<RecipeLibraryEntry> RecipeLibrary { get; } = new();

    /// <summary>true khi thư viện Recipe đang rỗng — dùng hiện dòng cảnh báo trong popup "Recipe start" (mục 7).
    /// Raise lại qua CollectionChanged của RecipeLibrary (đăng ký trong constructor) vì đây không phải [ObservableProperty].</summary>
    public bool HasNoRecipesInLibrary => RecipeLibrary.Count == 0;

    [ObservableProperty] private RecipeLibraryEntry? _selectedLibraryRecipe;

    /// <summary>true = đang hiện danh sách Recipe (màn hình chính của tab); false = đang hiện màn Edit (RecipeSteps).
    /// "Back" chỉ đơn thuần chuyển về true — KHÔNG tự Save, người dùng phải tự bấm Save trước nếu muốn giữ thay đổi
    /// (đúng hành vi RecipeProgStepListControl của CoreApp: Back không âm thầm lưu hộ).</summary>
    [ObservableProperty] private bool _isRecipeListView = true;
    /// <summary>Phần bù của IsRecipeListView — dùng cho Visibility của màn Edit trong XAML (BooleanToVisibilityConverter
    /// không hỗ trợ "invert" qua ConverterParameter, nên tính sẵn 1 property ngược thay vì viết thêm 1 class Converter).</summary>
    public bool IsRecipeEditView => !IsRecipeListView;
    partial void OnIsRecipeListViewChanged(bool value) => OnPropertyChanged(nameof(IsRecipeEditView));

    // "Clipboard" nội bộ cho Copy/Paste — chỉ giữ ĐƯỜNG DẪN FILE đã Copy (không giữ nội dung runtime), để Paste luôn
    // đọc lại đúng nội dung file tại thời điểm Paste, tránh dán ra bản cũ nếu file gốc đã bị sửa/xoá sau khi Copy.
    private string? _clipboardRecipeFilePath;

    // File .json đang mở trong màn Edit — null nếu đây là Recipe MỚI (CreateNewRecipe) chưa từng Save lần nào.
    private string? _currentEditingFilePath;

    [RelayCommand]
    private void RefreshRecipeLibrary()
    {
        RecipeLibrary.Clear();
        try
        {
            Directory.CreateDirectory(RecipeFolderPath);
            int no = 1;
            foreach (var path in Directory.GetFiles(RecipeFolderPath, "*.json").OrderBy(f => f))
            {
                try
                {
                    var file = JsonSerializer.Deserialize<RecipeFile>(File.ReadAllText(path), JsonOptions);
                    RecipeLibrary.Add(new RecipeLibraryEntry
                    {
                        No = no++,
                        Name = file?.Name ?? Path.GetFileNameWithoutExtension(path),
                        FilePath = path,
                        StepCount = file?.Steps.Count ?? 0,
                        ModifiedAt = File.GetLastWriteTime(path),
                    });
                }
                catch { /* 1 file .json lỗi định dạng — bỏ qua, không để nó làm sập cả danh sách */ }
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Không đọc được thư mục Recipe: {ex.Message}";
        }
    }

    [RelayCommand]
    private void CreateNewRecipe()
    {
        RecipeName = "Recipe_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
        RecipeSteps.Clear();
        _currentEditingFilePath = null; // Chưa gắn file nào — lần Save đầu tiên sẽ tạo file mới theo RecipeName
        RecalculateRecipeTiming();
        RunRecipeCommand.NotifyCanExecuteChanged();
        IsRecipeListView = false;
        StatusText = "Đang tạo Recipe mới — bấm Save để lưu vào thư viện.";
    }

    /// <summary>Nạp 1 file Recipe .json vào RecipeSteps/RecipeName hiện hành — dùng CHUNG cho cả EditRecipe (tab
    /// Công thức) lẫn ConfirmRecipeStart (nút "Recipe start" ở Tổng quan), tránh lặp lại cùng 1 đoạn code đọc JSON.
    /// Trả về true nếu nạp thành công.</summary>
    private bool TryLoadRecipeFromFile(string filePath, string displayName)
    {
        try
        {
            var file = JsonSerializer.Deserialize<RecipeFile>(File.ReadAllText(filePath), JsonOptions)
                       ?? throw new InvalidDataException("File Recipe rỗng.");
            RecipeName = file.Name;
            RecipeSteps.Clear();
            int order = 1;
            foreach (var s in file.Steps)
            {
                RecipeSteps.Add(new RecipeStepViewModel
                {
                    Order = order++,
                    Description = s.Description,
                    Address = s.Address,
                    Kind = s.Kind,
                    Value = s.Value,
                    Scale = s.Scale,
                    DelayAfterMs = s.DelayAfterMs,
                    IsWaitStep = s.IsWaitStep,
                    WaitTimeoutMs = s.WaitTimeoutMs
                });
            }
            _currentEditingFilePath = filePath;
            RecalculateRecipeTiming();
            RunRecipeCommand.NotifyCanExecuteChanged();
            return true;
        }
        catch (Exception ex)
        {
            StatusText = $"Không mở được Recipe \"{displayName}\": {ex.Message}";
            return false;
        }
    }

    [RelayCommand]
    private void EditRecipe(RecipeLibraryEntry? entry)
    {
        if (entry is null) { StatusText = "Chưa chọn Recipe nào trong danh sách."; return; }
        if (!TryLoadRecipeFromFile(entry.FilePath, entry.Name)) return;
        IsRecipeListView = false;
        StatusText = $"Đang sửa Recipe: {entry.Name} ({RecipeSteps.Count} bước).";
    }

    [RelayCommand]
    private void CopyRecipe(RecipeLibraryEntry? entry)
    {
        if (entry is null) { StatusText = "Chưa chọn Recipe nào trong danh sách."; return; }
        _clipboardRecipeFilePath = entry.FilePath;
        StatusText = $"Đã Copy Recipe \"{entry.Name}\" — bấm Paste để tạo bản sao mới.";
    }

    [RelayCommand]
    private void PasteRecipe()
    {
        if (_clipboardRecipeFilePath is null || !File.Exists(_clipboardRecipeFilePath))
        { StatusText = "Chưa Copy Recipe nào (hoặc file gốc đã bị xoá)."; return; }

        try
        {
            var file = JsonSerializer.Deserialize<RecipeFile>(File.ReadAllText(_clipboardRecipeFilePath), JsonOptions)
                       ?? throw new InvalidDataException("File Recipe rỗng.");

            string baseName = file.Name + "_Copy";
            string newName = baseName;
            string newPath = Path.Combine(RecipeFolderPath, newName + ".json");
            int suffix = 2;
            while (File.Exists(newPath)) { newName = $"{baseName}{suffix++}"; newPath = Path.Combine(RecipeFolderPath, newName + ".json"); }

            file.Name = newName;
            Directory.CreateDirectory(RecipeFolderPath);
            File.WriteAllText(newPath, JsonSerializer.Serialize(file, JsonOptions));
            RefreshRecipeLibrary();
            StatusText = $"Đã Paste thành Recipe mới: {newName}.";
        }
        catch (Exception ex)
        {
            StatusText = $"Paste thất bại: {ex.Message}";
        }
    }

    [RelayCommand]
    private void DeleteRecipe(RecipeLibraryEntry? entry)
    {
        if (entry is null) { StatusText = "Chưa chọn Recipe nào trong danh sách."; return; }
        var result = System.Windows.MessageBox.Show(
            $"Xoá hẳn Recipe \"{entry.Name}\"? Không thể hoàn tác.", "Xác nhận xoá Recipe",
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (result != System.Windows.MessageBoxResult.Yes) return;

        try
        {
            File.Delete(entry.FilePath);
            if (string.Equals(_currentEditingFilePath, entry.FilePath, StringComparison.OrdinalIgnoreCase))
                _currentEditingFilePath = null; // Đang sửa đúng Recipe vừa xoá -> coi màn Edit hiện tại là "Recipe mới" nếu Save tiếp
            RefreshRecipeLibrary();
            StatusText = $"Đã xoá Recipe: {entry.Name}.";
        }
        catch (Exception ex)
        {
            StatusText = $"Xoá thất bại: {ex.Message}";
        }
    }

    [RelayCommand]
    private void BackToRecipeList()
    {
        IsRecipeListView = true;
        RefreshRecipeLibrary(); // Cập nhật lại ngay số bước/ngày sửa trong danh sách nếu vừa Save trước khi Back
    }

    [RelayCommand]
    private void ImportRecipe()
    {
        var dialog = new OpenFileDialog { Filter = "Recipe VisionFlow (*.json)|*.json", Title = "Import Recipe từ file ngoài thư viện" };
        if (dialog.ShowDialog() != true) return;

        try
        {
            Directory.CreateDirectory(RecipeFolderPath);
            var file = JsonSerializer.Deserialize<RecipeFile>(File.ReadAllText(dialog.FileName), JsonOptions)
                       ?? throw new InvalidDataException("File Recipe rỗng.");

            string baseName = string.IsNullOrWhiteSpace(file.Name) ? Path.GetFileNameWithoutExtension(dialog.FileName) : file.Name;
            string newName = baseName;
            string newPath = Path.Combine(RecipeFolderPath, newName + ".json");
            int suffix = 2;
            while (File.Exists(newPath)) { newName = $"{baseName}_{suffix++}"; newPath = Path.Combine(RecipeFolderPath, newName + ".json"); }

            file.Name = newName;
            File.WriteAllText(newPath, JsonSerializer.Serialize(file, JsonOptions));
            RefreshRecipeLibrary();
            StatusText = $"Đã Import Recipe: {newName} (từ {dialog.FileName}).";
        }
        catch (Exception ex)
        {
            StatusText = $"Import thất bại: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ExportRecipe(RecipeLibraryEntry? entry)
    {
        if (entry is null) { StatusText = "Chưa chọn Recipe nào trong danh sách."; return; }
        var dialog = new SaveFileDialog { Filter = "Recipe VisionFlow (*.json)|*.json", FileName = entry.Name + ".json", Title = "Export Recipe ra ngoài thư viện" };
        if (dialog.ShowDialog() != true) return;

        try
        {
            File.Copy(entry.FilePath, dialog.FileName, overwrite: true);
            StatusText = $"Đã Export Recipe \"{entry.Name}\" ra: {dialog.FileName}";
        }
        catch (Exception ex)
        {
            StatusText = $"Export thất bại: {ex.Message}";
        }
    }

    // ==================================================================================
    // ALARM — theo dõi cạnh lên/xuống của các bit đã khai báo, ghi log khi có sự kiện
    // ==================================================================================

    [RelayCommand]
    private void AddAlarmRule() => AlarmRules.Add(new AlarmRuleViewModel());

    [RelayCommand]
    private void RemoveAlarmRule(AlarmRuleViewModel rule) => AlarmRules.Remove(rule);

    [RelayCommand]
    private void AckAlarm(AlarmLogEntry entry)
    {
        entry.Acknowledged = true;
        entry.AcknowledgedAt = DateTime.Now;
        RefreshMainAlarmText(); // <-- THÊM
    }

    [RelayCommand]
    private void ClearAcknowledgedAlarms()
    {
        foreach (var e in AlarmLog.Where(e => e.Acknowledged).ToList()) AlarmLog.Remove(e);
        RefreshMainAlarmText(); // <-- THÊM
    }

    // ==================================================================================
    // VÒNG LẶP POLL: chạy mỗi 300ms trên UI thread (DispatcherTimer) — đọc IoPoint AutoPoll + kiểm tra Alarm
    // ==================================================================================

    private bool _polling; // chặn chồng lệnh khi 1 chu kỳ poll trước đó chưa xong (PLC chậm/timeout)

    private async Task PollOnceAsync()
    {
        if (_polling || _plc.Link is not { } link) return;
        _polling = true;
        try
        {
            foreach (var point in IoPoints.Where(p => p.AutoPoll).ToList())
                await ReadPointAsync(point);

            // Cập nhật bảng "IO / Trạng thái thiết bị" trên Dashboard (X0/X1/Y0..Y4) — đọc lại mỗi chu kỳ.
            foreach (var io in SystemIoPoints.ToList())
            {
                if (!PlcAddress.TryParse(io.Address, out var addr)) continue;
                try { io.Value = await link.ReadBitAsync(addr); }
                catch { /* lỗi đọc thoáng qua — giữ nguyên giá trị cũ, không spam log */ }
            }

            foreach (var rule in AlarmRules.Where(r => r.IsEnabled).ToList())
            {
                if (!PlcAddress.TryParse(rule.Address, out var addr)) continue;
                bool current;
                try { current = await link.ReadBitAsync(addr); }
                catch { continue; } // Lỗi đọc thoáng qua — bỏ qua chu kỳ này, không spam log

                bool becameActive = current == rule.ActiveWhen && rule.LastKnownState != rule.ActiveWhen;
                rule.LastKnownState = current;

                if (becameActive)
                {
                    // Chụp nhanh (snapshot) trạng thái Recipe NGAY lúc Alarm xảy ra — nếu không có Recipe nào đang chạy
                    // thì để "-"/0 đúng quy ước cột "Recipe name"/"Step No."/"Step name" của CoreApp khi Alarm xảy ra
                    // ngoài lúc chạy Recipe (vd Alarm cảm biến/an toàn trong lúc đứng máy).
                    bool recipeActive = IsRecipeRunning;
                    AlarmLog.Insert(0, new AlarmLogEntry
                    {
                        No = _nextAlarmLogNo++,
                        AlarmNo = _nextAlarmNo++,
                        RuleName = rule.Name,
                        Message = rule.Message,
                        RecipeName = recipeActive ? RecipeName : "-",
                        StepNo = recipeActive ? CurrentStepNo : 0,
                        StepName = recipeActive ? CurrentStepName : "-",
                        RemainingTimeText = recipeActive ? StepRemainingTimeText : "-",
                    });
                    RefreshMainAlarmText(); // <-- THÊM: cảnh báo mới -> cập nhật ngay "Main alarm" trên panel Thông số công thức
                    FireAndForgetMes(() => _mes.RecordAlarmAsync(rule.Name, rule.Message)); // <-- THÊM: ghi xuống MES/DB
                }
            }
        }
        finally
        {
            _polling = false;
        }
    }

    public void Dispose()
    {
        _pollTimer.Stop();
        _clockTimer.Stop();
        if (_plc.Link is { } link) link.StateChanged -= HandlePlcLinkStateChanged;
        _recipeCts?.Cancel();
        _recipeCts?.Dispose();
    }
}