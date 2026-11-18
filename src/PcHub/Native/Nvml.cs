using System.Runtime.InteropServices;

namespace PcHub.Native;

/// <summary>
/// Minimal P/Invoke surface for NVIDIA's NVML (nvml.dll ships with the driver in System32).
/// Reading through NVML costs microseconds, versus ~100 ms to spawn nvidia-smi.
/// </summary>
internal static unsafe partial class Nvml
{
    private const string Lib = "nvml.dll";

    public const int Success = 0;
    public const int TemperatureGpu = 0;
    public const int ClockGraphics = 0;
    public const int ClockMemory = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct Memory
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Utilization
    {
        public uint Gpu;
        public uint Memory;
    }

    [LibraryImport(Lib, EntryPoint = "nvmlInit_v2")]
    public static partial int Init();

    [LibraryImport(Lib, EntryPoint = "nvmlShutdown")]
    public static partial int Shutdown();

    [LibraryImport(Lib, EntryPoint = "nvmlDeviceGetCount_v2")]
    public static partial int DeviceGetCount(out uint count);

    [LibraryImport(Lib, EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
    public static partial int DeviceGetHandleByIndex(uint index, out nint device);

    [LibraryImport(Lib, EntryPoint = "nvmlDeviceGetName")]
    public static partial int DeviceGetName(nint device, byte* name, uint length);

    [LibraryImport(Lib, EntryPoint = "nvmlSystemGetDriverVersion")]
    public static partial int SystemGetDriverVersion(byte* version, uint length);

    [LibraryImport(Lib, EntryPoint = "nvmlDeviceGetMemoryInfo")]
    public static partial int DeviceGetMemoryInfo(nint device, out Memory memory);

    [LibraryImport(Lib, EntryPoint = "nvmlDeviceGetUtilizationRates")]
    public static partial int DeviceGetUtilizationRates(nint device, out Utilization utilization);

    [LibraryImport(Lib, EntryPoint = "nvmlDeviceGetTemperature")]
    public static partial int DeviceGetTemperature(nint device, int sensor, out uint celsius);

    [LibraryImport(Lib, EntryPoint = "nvmlDeviceGetPowerUsage")]
    public static partial int DeviceGetPowerUsage(nint device, out uint milliwatts);

    [LibraryImport(Lib, EntryPoint = "nvmlDeviceGetEnforcedPowerLimit")]
    public static partial int DeviceGetEnforcedPowerLimit(nint device, out uint milliwatts);

    [LibraryImport(Lib, EntryPoint = "nvmlDeviceGetFanSpeed")]
    public static partial int DeviceGetFanSpeed(nint device, out uint percent);

    [LibraryImport(Lib, EntryPoint = "nvmlDeviceGetClockInfo")]
    public static partial int DeviceGetClockInfo(nint device, int clockType, out uint mhz);

    [LibraryImport(Lib, EntryPoint = "nvmlErrorString")]
    public static partial nint ErrorString(int result);

    /// <summary>
    /// Modern (DCH) drivers put nvml.dll in System32; older ones only in NVSMI under Program Files.
    /// Call once at startup. Only those two trusted locations are tried.
    /// </summary>
    public static void RegisterResolver() =>
        NativeLibrary.SetDllImportResolver(typeof(Nvml).Assembly, (name, _, _) =>
        {
            if (!name.Equals(Lib, StringComparison.OrdinalIgnoreCase)) return 0;
            string[] candidates =
            [
                Path.Combine(Environment.SystemDirectory, Lib),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"NVIDIA Corporation\NVSMI", Lib),
            ];
            foreach (string c in candidates)
            {
                if (File.Exists(c) && NativeLibrary.TryLoad(c, out nint h)) return h;
            }

            return 0;
        });

    public static string Describe(int result)
    {
        try
        {
            nint p = ErrorString(result);
            return p == 0 ? $"NVML error {result}" : Marshal.PtrToStringAnsi(p) ?? $"NVML error {result}";
        }
        catch
        {
            return $"NVML error {result}";
        }
    }
}
