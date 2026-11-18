namespace PcHub.ViewModels;

public sealed class GpuProcViewModel : ObservableObject
{
    private string _vramText = "";
    private string _gpuText = "";
    private string _owner = "";
    private double _vramShare;

    public GpuProcViewModel(int pid, long createTime, string name, string? path)
    {
        Pid = pid;
        CreateTime = createTime;
        Name = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        Path = path ?? "(path hidden — protected or admin process)";
    }

    public int Pid { get; }
    public long CreateTime { get; }
    public string Name { get; }
    public string Path { get; }
    public long VramBytes { get; private set; }

    public string VramText { get => _vramText; private set => Set(ref _vramText, value); }
    public string GpuText { get => _gpuText; private set => Set(ref _gpuText, value); }
    public string Owner { get => _owner; private set => Set(ref _owner, value); }

    /// <summary>0..1 share of total VRAM, drawn as a bar.</summary>
    public double VramShare { get => _vramShare; private set => Set(ref _vramShare, value); }

    public void Update(long vram, double util, string owner, double totalBytes)
    {
        VramBytes = Math.Max(0, vram);
        VramText = vram < 0 ? "?" : Format.Bytes(vram);
        GpuText = util >= 0.5 ? $"{util:0}%" : "";
        Owner = owner;
        VramShare = totalBytes > 0 && vram > 0 ? Math.Clamp(vram / totalBytes, 0, 1) : 0;
    }
}
