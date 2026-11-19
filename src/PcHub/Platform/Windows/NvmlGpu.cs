using PcHub.Platform;
using PcHub.Services;
using System.Text;

namespace PcHub.Platform.Windows;


/// <summary>One NVIDIA GPU read through NVML. Thread-safe for a single polling thread.</summary>
public sealed unsafe class NvmlGpu : IDisposable
{
    private nint _device;
    private bool _initialized;

    public bool Available => _initialized && _device != 0;
    public string Name { get; private set; } = "No NVIDIA GPU found";
    public string Driver { get; private set; } = "";
    public string? Error { get; private set; }

    /// <param name="preferredName">DXGI adapter name to pick when several NVIDIA cards are present.</param>
    public static NvmlGpu Open(string? preferredName)
    {
        var gpu = new NvmlGpu();
        try
        {
            int r = Nvml.Init();
            if (r != Nvml.Success)
            {
                gpu.Error = "NVML failed to start: " + Nvml.Describe(r);
                return gpu;
            }

            gpu._initialized = true;
            if (Nvml.DeviceGetCount(out uint count) != Nvml.Success || count == 0)
            {
                gpu.Error = "NVML found no NVIDIA GPU.";
                return gpu;
            }

            byte* buf = stackalloc byte[96];
            for (uint i = 0; i < count; i++)
            {
                if (Nvml.DeviceGetHandleByIndex(i, out nint dev) != Nvml.Success)
                {
                    continue;
                }

                string name = Nvml.DeviceGetName(dev, buf, 96) == Nvml.Success ? ReadAnsi(buf, 96) : "NVIDIA GPU";
                bool match = preferredName is null || string.Equals(name, preferredName, StringComparison.OrdinalIgnoreCase);
                if (gpu._device == 0 || match)
                {
                    gpu._device = dev;
                    gpu.Name = name;
                    if (match) break;
                }
            }

            if (gpu._device == 0)
            {
                gpu.Error = "NVML could not open the GPU.";
                return gpu;
            }

            if (Nvml.SystemGetDriverVersion(buf, 96) == Nvml.Success)
            {
                gpu.Driver = ReadAnsi(buf, 96);
            }
        }
        catch (DllNotFoundException)
        {
            gpu.Error = OperatingSystem.IsLinux()
                ? "libnvidia-ml.so.1 not found. Is the NVIDIA driver (not nouveau) installed?"
                : "nvml.dll not found. Is the NVIDIA driver installed?";
        }
        catch (EntryPointNotFoundException ex)
        {
            gpu.Error = "NVML is too old: " + ex.Message;
        }
        catch (BadImageFormatException)
        {
            gpu.Error = "The NVIDIA library doesn't match this system (32/64-bit or ARM). Reinstall the NVIDIA driver.";
        }

        return gpu;
    }

    public GpuSample? Read()
    {
        if (!Available)
        {
            return null;
        }

        if (Nvml.DeviceGetMemoryInfo(_device, out var mem) != Nvml.Success)
        {
            return null;
        }

        int load = Nvml.DeviceGetUtilizationRates(_device, out var util) == Nvml.Success ? (int)util.Gpu : 0;
        int? temp = Nvml.DeviceGetTemperature(_device, Nvml.TemperatureGpu, out uint t) == Nvml.Success ? (int)t : null;
        double? power = Nvml.DeviceGetPowerUsage(_device, out uint mw) == Nvml.Success ? mw / 1000.0 : null;
        double? limit = Nvml.DeviceGetEnforcedPowerLimit(_device, out uint lmw) == Nvml.Success ? lmw / 1000.0 : null;
        int? fan = Nvml.DeviceGetFanSpeed(_device, out uint f) == Nvml.Success ? (int)f : null;
        int? core = Nvml.DeviceGetClockInfo(_device, Nvml.ClockGraphics, out uint c) == Nvml.Success ? (int)c : null;
        int? memClock = Nvml.DeviceGetClockInfo(_device, Nvml.ClockMemory, out uint m) == Nvml.Success ? (int)m : null;

        return new GpuSample(mem.Used, mem.Total, load, temp, power, limit, fan, core, memClock);
    }

    /// <summary>
    /// VRAM per process from the driver (used on Linux). Compute (CUDA) and graphics users are merged.
    /// Null when the driver doesn't support the query.
    /// </summary>
    public Dictionary<int, ulong>? ReadProcesses()
    {
        if (!Available || _noProcessQuery) return null;
        var result = new Dictionary<int, ulong>();
        try
        {
            Collect(compute: true, result);
            Collect(compute: false, result);
        }
        catch (EntryPointNotFoundException)
        {
            _noProcessQuery = true; // driver older than 510
            return null;
        }

        return result;
    }

    private bool _noProcessQuery;

    private void Collect(bool compute, Dictionary<int, ulong> into)
    {
        uint count = 64;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var buf = new Nvml.ProcessInfo[count];
            uint n = count;
            int r;
            fixed (Nvml.ProcessInfo* p = buf)
            {
                r = compute ? Nvml.DeviceGetComputeRunningProcesses(_device, ref n, p) : Nvml.DeviceGetGraphicsRunningProcesses(_device, ref n, p);
            }

            if (r == Nvml.InsufficientSize)
            {
                count = Math.Max(n, count * 2) + 8; // more processes appeared; grow and retry
                continue;
            }

            if (r != Nvml.Success) return;
            for (int i = 0; i < n && i < buf.Length; i++)
            {
                // NVML_VALUE_NOT_AVAILABLE (all ones) when the driver can't attribute memory to the process.
                ulong used = buf[i].UsedGpuMemory == ulong.MaxValue ? 0 : buf[i].UsedGpuMemory;
                int pid = (int)buf[i].Pid;
                into[pid] = Math.Max(into.GetValueOrDefault(pid), used);
            }

            return;
        }
    }

    public void Dispose()
    {
        if (_initialized)
        {
            _initialized = false;
            _device = 0;
            try { _ = Nvml.Shutdown(); } catch (DllNotFoundException) { /* driver gone; nothing to release */ }
        }
    }

    private static string ReadAnsi(byte* buf, int max)
    {
        int len = 0;
        while (len < max && buf[len] != 0) len++;
        return Encoding.ASCII.GetString(buf, len).Trim();
    }
}
