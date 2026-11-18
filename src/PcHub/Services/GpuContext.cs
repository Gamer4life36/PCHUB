using PcHub.Native;

namespace PcHub.Services;

/// <summary>
/// The GPU being watched. NVIDIA cards get NVML (temperature, power, clocks, fan); every other
/// card (AMD, Intel, older NVIDIA drivers) falls back to Windows' GPU counters for VRAM and load.
/// </summary>
public sealed class GpuContext : IDisposable
{
    private GpuContext(IReadOnlyList<AdapterInfo> adapters, AdapterInfo? adapter, NvmlGpu? nvml, string? note)
    {
        Adapters = adapters;
        Adapter = adapter;
        Nvml = nvml;
        Note = note;
    }

    public IReadOnlyList<AdapterInfo> Adapters { get; }
    public AdapterInfo? Adapter { get; }
    public NvmlGpu? Nvml { get; }

    /// <summary>Why some readings are missing, if they are.</summary>
    public string? Note { get; }

    public bool HasNvml => Nvml?.Available == true;
    public string Name => HasNvml ? Nvml!.Name : Adapter?.Name ?? "No GPU detected";
    public ulong TotalBytes => Adapter?.DedicatedBytes ?? 0;

    public string Detail
    {
        get
        {
            if (HasNvml) return $"NVIDIA driver {Nvml!.Driver} · full sensors";
            if (Adapter is null) return "No hardware GPU found";
            return $"{Adapter.Vendor} · VRAM and load from Windows";
        }
    }

    /// <summary>
    /// Never throws and never blocks startup for long: a broken or slow-waking driver (laptop GPUs can
    /// take seconds to wake) falls back to "no sensors" after a few seconds.
    /// </summary>
    public static GpuContext CreateSafe(string? preferredLuid)
    {
        try
        {
            var task = Task.Run(() => Create(preferredLuid));
            if (task.Wait(TimeSpan.FromSeconds(6))) return task.Result;
            return new GpuContext([], null, null, "The GPU driver took too long to answer; sensors are off this session. Restart PCHUB to retry.");
        }
        catch (Exception ex)
        {
            CrashLog.Write("GPU detection failed", ex);
            return new GpuContext([], null, null, "GPU detection failed (" + ex.GetBaseException().Message + "). Process tracking still works.");
        }
    }

    public static GpuContext Create(string? preferredLuid)
    {
        var adapters = Dxgi.EnumerateAdapters();
        AdapterInfo? chosen = adapters.FirstOrDefault(a => a.LuidTag == preferredLuid) ?? adapters.FirstOrDefault();

        NvmlGpu? nvml = null;
        string? note = null;
        bool wantNvml = chosen is null ? true : chosen.VendorId == AdapterInfo.Nvidia;
        if (wantNvml)
        {
            nvml = NvmlGpu.Open(chosen?.Name);
            if (!nvml.Available)
            {
                note = nvml.Error;
                nvml.Dispose();
                nvml = null;
            }
        }
        else
        {
            note = "Temperature and power need an NVIDIA card; showing VRAM and load only.";
        }

        if (chosen is null && nvml is null)
        {
            note = "No GPU was found. Process tracking still works.";
        }

        return new GpuContext(adapters, chosen, nvml, note);
    }

    public void Dispose() => Nvml?.Dispose();
}
