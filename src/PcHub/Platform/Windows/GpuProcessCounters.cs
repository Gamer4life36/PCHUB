using PcHub.Platform;
using PcHub.Services;
using System.Diagnostics;
using System.Globalization;

namespace PcHub.Platform.Windows;


public sealed record CounterReading(
    Dictionary<int, GpuProcUsage> PerProcess,
    long AdapterDedicatedBytes,
    double AdapterUtilPercent);

/// <summary>
/// Per-process dedicated VRAM and GPU load from Windows' "GPU Process Memory", "GPU Engine" and
/// "GPU Adapter Memory" counters — the same data Task Manager shows, for NVIDIA, AMD and Intel
/// alike (Windows 10 1709+). Each category is read in one bulk call per scan.
/// </summary>
public sealed class GpuProcessCounters
{
    private readonly PerformanceCounterCategory? _procMemory;
    private readonly PerformanceCounterCategory? _engine;
    private readonly PerformanceCounterCategory? _adapterMemory;
    private readonly string? _luidTag;
    private Dictionary<string, CounterSample> _previous = new(StringComparer.Ordinal);

    public string? Error { get; private set; }

    /// <param name="luidTag">Only count this adapter ("luid_0x..._0x..."); null counts every adapter.</param>
    public GpuProcessCounters(string? luidTag)
    {
        _luidTag = luidTag;
        try
        {
            _procMemory = new PerformanceCounterCategory("GPU Process Memory");
            _engine = new PerformanceCounterCategory("GPU Engine");
            _adapterMemory = new PerformanceCounterCategory("GPU Adapter Memory");
            if (!PerformanceCounterCategory.Exists("GPU Engine"))
            {
                Error = "This Windows version has no GPU counters (needs Windows 10 1709 or newer).";
                _procMemory = _engine = _adapterMemory = null;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Error = "Windows GPU counters unavailable: " + ex.Message;
            _procMemory = _engine = _adapterMemory = null;
        }
    }

    public CounterReading Read()
    {
        var perProcess = new Dictionary<int, GpuProcUsage>();
        long adapterBytes = 0;
        double adapterUtil = 0;
        if (_procMemory is null || _engine is null)
        {
            return new CounterReading(perProcess, 0, 0);
        }

        try
        {
            var memData = _procMemory.ReadCategory()["Dedicated Usage"];
            if (memData is not null)
            {
                foreach (InstanceData d in memData.Values)
                {
                    if (!Matches(d.InstanceName) || !TryPid(d.InstanceName, out int pid))
                    {
                        continue;
                    }

                    perProcess.TryGetValue(pid, out var u);
                    perProcess[pid] = u with { DedicatedBytes = u.DedicatedBytes + d.RawValue };
                }
            }

            if (_adapterMemory is not null)
            {
                var adData = _adapterMemory.ReadCategory()["Dedicated Usage"];
                if (adData is not null)
                {
                    foreach (InstanceData d in adData.Values)
                    {
                        if (Matches(d.InstanceName))
                        {
                            adapterBytes += d.RawValue;
                        }
                    }
                }
            }

            var engData = _engine.ReadCategory()["Utilization Percentage"];
            if (engData is not null)
            {
                var current = new Dictionary<string, CounterSample>(engData.Count, StringComparer.Ordinal);
                var perPid = new Dictionary<int, Dictionary<string, double>>();
                var adapterTypes = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (InstanceData d in engData.Values)
                {
                    string name = d.InstanceName;
                    if (!Matches(name) || !TryPid(name, out int pid))
                    {
                        continue;
                    }

                    var sample = d.Sample;
                    current[name] = sample;
                    if (!_previous.TryGetValue(name, out var prev))
                    {
                        continue;
                    }

                    double pct = CounterSample.Calculate(prev, sample);
                    if (pct <= 0 || double.IsNaN(pct) || double.IsInfinity(pct))
                    {
                        continue;
                    }

                    string engType = EngineType(name);
                    if (!perPid.TryGetValue(pid, out var types))
                    {
                        perPid[pid] = types = new Dictionary<string, double>(StringComparer.Ordinal);
                    }

                    types[engType] = types.GetValueOrDefault(engType) + pct;
                    adapterTypes[engType] = adapterTypes.GetValueOrDefault(engType) + pct;
                }

                _previous = current;

                // A process's (and the card's) load is its busiest engine type, like Task Manager.
                foreach (var (pid, types) in perPid)
                {
                    perProcess.TryGetValue(pid, out var u);
                    perProcess[pid] = u with { UtilPercent = Math.Min(100, types.Values.Max()) };
                }

                if (adapterTypes.Count > 0)
                {
                    adapterUtil = Math.Min(100, adapterTypes.Values.Max());
                }
            }

            // Windows sometimes reports impossible values (e.g. dwm.exe above the whole card's usage).
            // Mark those unknown (-1) rather than show a number that can't be true.
            if (adapterBytes > 0)
            {
                long limit = adapterBytes + adapterBytes / 20;
                foreach (int pid in perProcess.Keys.ToList())
                {
                    var u = perProcess[pid];
                    if (u.DedicatedBytes > limit) perProcess[pid] = u with { DedicatedBytes = -1 };
                }
            }

            Error = memData is null || engData is null
                ? "Windows didn't return the GPU counters (per-app VRAM is unavailable on this PC)."
                : null;
        }
        catch (Exception ex)
        {
            // Any failure here must only blank the per-process numbers, never stop the monitor.
            Error = "Reading GPU counters failed: " + ex.Message;
        }

        return new CounterReading(perProcess, adapterBytes, adapterUtil);
    }

    /// <summary>Dedicated VRAM in use on the watched adapter, from one small category read (-1 if unavailable).</summary>
    public long ReadAdapterBytes()
    {
        if (_adapterMemory is null) return -1;
        try
        {
            var data = _adapterMemory.ReadCategory()["Dedicated Usage"];
            if (data is null) return -1;
            long sum = 0;
            foreach (InstanceData d in data.Values)
            {
                if (Matches(d.InstanceName)) sum += d.RawValue;
            }

            return sum;
        }
        catch (Exception)
        {
            return -1;
        }
    }

    private bool Matches(string instance) =>
        _luidTag is null || instance.Contains(_luidTag, StringComparison.OrdinalIgnoreCase);

    // Instance names look like: pid_1234_luid_0x00000000_0x0000D1C2_phys_0[_eng_0_engtype_3D]
    private static bool TryPid(string instance, out int pid)
    {
        pid = 0;
        if (!instance.StartsWith("pid_", StringComparison.Ordinal))
        {
            return false;
        }

        int end = instance.IndexOf('_', 4);
        return end > 4 && int.TryParse(instance.AsSpan(4, end - 4), NumberStyles.None, CultureInfo.InvariantCulture, out pid) && pid > 0;
    }

    private static string EngineType(string instance)
    {
        int i = instance.LastIndexOf("engtype_", StringComparison.Ordinal);
        return i < 0 ? "" : instance[(i + 8)..];
    }
}
