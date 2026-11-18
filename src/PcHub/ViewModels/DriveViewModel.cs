using System.Globalization;
using PcHub.Services;

namespace PcHub.ViewModels;

public sealed class DriveViewModel : ObservableObject
{
    private string _label = "", _kindText = "", _usedText = "", _fragText = "", _lastText = "", _advice = "";
    private double _usedFraction, _fragmentedFraction;
    private bool _needsAttention;

    public DriveViewModel(string letter)
    {
        Letter = letter;
        Seed = letter.Length > 0 ? letter[0] * 7919 : 1;
    }

    public string Letter { get; }
    public int Seed { get; }

    public string Label { get => _label; private set => Set(ref _label, value); }
    public string KindText { get => _kindText; private set => Set(ref _kindText, value); }
    public string UsedText { get => _usedText; private set => Set(ref _usedText, value); }
    public string FragText { get => _fragText; private set => Set(ref _fragText, value); }
    public string LastText { get => _lastText; private set => Set(ref _lastText, value); }
    public string Advice { get => _advice; private set => Set(ref _advice, value); }
    public double UsedFraction { get => _usedFraction; private set => Set(ref _usedFraction, value); }
    public double FragmentedFraction { get => _fragmentedFraction; private set => Set(ref _fragmentedFraction, value); }
    public bool NeedsAttention { get => _needsAttention; private set => Set(ref _needsAttention, value); }

    private bool _isOptimizing;
    private double _progress;
    private string _stageText = "";

    /// <summary>True while PCHUB is running Windows' optimizer on this drive (drives the 3D animation).</summary>
    public bool IsOptimizing
    {
        get => _isOptimizing;
        set { if (Set(ref _isOptimizing, value)) Raise(nameof(CanOptimize)); }
    }

    public bool CanOptimize => !_isOptimizing;
    public double Progress { get => _progress; set => Set(ref _progress, value); }
    public string StageText { get => _stageText; set => Set(ref _stageText, value); }
    public int? FragmentationPercent { get; private set; }

    /// <summary>Preview only: pretend part of the drive is fragmented so the animation has cubes to fix.</summary>
    public void ShowDemoFragments(double fractionOfDrive) => FragmentedFraction = Math.Min(UsedFraction, fractionOfDrive);
    public bool IsSsd { get; private set; }

    public void Update(DriveStatus d)
    {
        Label = d.Label;
        FragmentationPercent = d.FragmentationPercent;
        IsSsd = d.Kind == DriveKind.Ssd;
        KindText = d.Kind switch { DriveKind.Ssd => "SSD", DriveKind.Hdd => "Hard drive", _ => "Drive" };
        long used = Math.Max(0, d.TotalBytes - d.FreeBytes);
        UsedFraction = d.TotalBytes > 0 ? (double)used / d.TotalBytes : 0;
        UsedText = $"{Size(used)} of {Size(d.TotalBytes)} used";

        if (d.FragmentationPercent is int pct)
        {
            FragText = $"{pct}% fragmented";
            FragmentedFraction = UsedFraction * pct / 100.0;
            LastText = d.LastOptimized is DateTime t ? "Last optimized " + Ago(t) : "";
        }
        else
        {
            FragText = "Not analysed yet";
            FragmentedFraction = 0;
            LastText = "Windows hasn't checked this drive";
        }

        (Advice, NeedsAttention) = d.Kind switch
        {
            DriveKind.Ssd => ("Windows trims SSDs instead of defragmenting; fragmentation doesn't slow them down.", false),
            DriveKind.Hdd when d.FragmentationPercent >= 10 => ("Worth optimizing: fragmentation slows hard drives.", true),
            DriveKind.Hdd when d.FragmentationPercent is null => ("Run Optimize drives to analyse it.", true),
            DriveKind.Hdd => ("In good shape.", false),
            _ => (d.FragmentationPercent is null ? "Run Optimize drives to analyse it." : "", false),
        };
    }

    private static string Size(long bytes)
    {
        const double Tb = 1024d * 1024 * 1024 * 1024, Gb = 1024d * 1024 * 1024;
        return bytes >= Tb ? (bytes / Tb).ToString("0.0", CultureInfo.CurrentCulture) + " TB"
                           : (bytes / Gb).ToString("0", CultureInfo.CurrentCulture) + " GB";
    }

    private static string Ago(DateTime t)
    {
        var span = DateTime.Now - t;
        if (span.TotalDays < 1) return "today";
        if (span.TotalDays < 2) return "yesterday";
        if (span.TotalDays < 60) return $"{(int)span.TotalDays} days ago";
        return t.ToString("MMM d, yyyy", CultureInfo.CurrentCulture);
    }
}
