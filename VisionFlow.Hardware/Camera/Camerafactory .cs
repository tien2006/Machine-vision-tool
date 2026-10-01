// ==================== Vai trò chính:                Tạo ICamera theo cấu hình (config-driven) — chuyển đổi Simulation <-> hardware thật chỉ bằng đổi config
// ==================== Thành phần / Class tiêu biểu: CameraConfig, CameraFactory
// ==================== Phụ thuộc vào:                ICamera, SimulationCamera
// ==================== Pattern / Kỹ thuật nổi bật:   Factory Pattern

using System;

namespace VisionFlow.Hardware.Camera;

/// <summary>
/// Cấu hình khai báo (declarative) cho 1 camera — nạp từ appsettings.json/DI, không hard-code trong
/// business logic. Gồm cả tham số riêng của SimulationCamera (SimulatedFps, DefectRate, FailureRate)
/// để có thể bật/tắt mô phỏng lỗi qua config mà không phải sửa code khi demo cho khách hàng xem.
/// </summary>
public sealed class CameraConfig
{
    public string Name { get; set; } = "";

    /// <summary>"Simulation" | "Hikvision" | "Basler" | ... — CameraFactory dùng để chọn adapter.</summary>
    public string Type { get; set; } = "Simulation";

    public string IpAddress { get; set; } = "";
    public string ImageFolder { get; set; } = "";

    /// <summary>Chỉ số thiết bị video cho Type = "Webcam": 0 = webcam mặc định của laptop, 1 = camera thứ hai...</summary>
    public int DeviceIndex { get; set; } = 0;

    public int Width { get; set; } = 2048;
    public int Height { get; set; } = 1536;

    public double InitialExposureUs { get; set; } = 10_000;
    public double InitialGain { get; set; } = 1.0;

    // --- Riêng cho SimulationCamera: để trống/giá trị mặc định thì hành vi y hệt bản gốc Buổi 109 ---
    public double SimulatedFps { get; set; } = 30.0;
    public double SimulatedFpsJitter { get; set; } = 0.10;
    public double SimulatedDefectRate { get; set; } = 0.15;
    public double SimulatedFailureRate { get; set; } = 0.0;
    public bool SimulatedApplyExposureEffect { get; set; } = true;
    public int? SimulatedRandomSeed { get; set; } // set giá trị cố định khi cần Unit Test lặp lại được
}

/// <summary>
/// Factory tạo <see cref="ICamera"/> theo <see cref="CameraConfig.Type"/>. Đây là điểm DUY NHẤT trong
/// toàn hệ thống được phép "biết" có những loại camera nào tồn tại — mọi nơi khác chỉ nên thấy ICamera.
/// </summary>
public static class CameraFactory
{
    public static ICamera Create(CameraConfig config)
    {
        ICamera camera = config.Type switch
        {
            "Simulation" => new SimulationCamera(config.Name, config.ImageFolder, config.SimulatedRandomSeed)
            {
                SimulatedFps = config.SimulatedFps,
                FpsJitter = config.SimulatedFpsJitter,
                DefectRate = config.SimulatedDefectRate,
                SimulatedFailureRate = config.SimulatedFailureRate,
                ApplyExposureSimulation = config.SimulatedApplyExposureEffect,
            },

            // Camera của laptop / USB camera — chọn bằng Type = "Webcam" trong CameraConfig
            "Webcam" => new WebcamCamera(config.Name, config.DeviceIndex)
            {
                ApplyExposureSimulation = config.SimulatedApplyExposureEffect, // tái dùng cờ có sẵn của config
            },

            // "Hikvision" / "Basler" chưa có adapter thật trong repo hiện tại — khi tích hợp camera
            // thật, chỉ cần thêm class HikvisionCamera/BaslerCamera : ICamera rồi thêm 1 case ở đây,
            // KHÔNG cần sửa gì ở CameraHub, LiveCameraSourceTool hay bất kỳ ViewModel nào khác.
            "Hikvision" => throw new NotSupportedException(
                "HikvisionCamera adapter chưa được triển khai trong repo này — chỉ mới có ICamera + SimulationCamera. " +
                "Xem báo cáo đánh giá kiến trúc: đây là hạng mục còn thiếu, không phải lỗi CameraFactory."),

            "Basler" => throw new NotSupportedException(
                "BaslerCamera adapter chưa được triển khai trong repo này — tương tự Hikvision."),

            _ => throw new NotSupportedException($"Camera type '{config.Type}' is not supported"),
        };

        if (!camera.Initialize(config.Width, config.Height))
            throw new InvalidOperationException($"CameraFactory: Initialize() thất bại cho camera '{config.Name}'.");

        camera.ExposureTime = config.InitialExposureUs;
        camera.Gain = config.InitialGain;

        return camera;
    }
}