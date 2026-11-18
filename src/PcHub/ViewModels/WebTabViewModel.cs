using System.ComponentModel;

namespace PcHub.ViewModels;

/// <summary>A tool's web page shown inside PCHUB (ComfyUI, Forge, ...).</summary>
public sealed class WebTabViewModel : ObservableObject
{
    private bool _isSelected;
    private string _title;
    private string _url;

    public WebTabViewModel(string key, string title, string url, ToolViewModel? tool)
    {
        Key = key;
        _title = title;
        _url = url;
        Tool = tool;
        if (tool is not null)
        {
            tool.PropertyChanged += OnToolChanged;
        }
    }

    /// <summary>Tool id, or the URL for pages that don't belong to a tool.</summary>
    public string Key { get; }
    public ToolViewModel? Tool { get; }

    public string Title { get => _title; set => Set(ref _title, value); }
    public string Url { get => _url; set => Set(ref _url, value); }
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    /// <summary>The page can be loaded: its tool is up and answering (or it has no tool).</summary>
    public bool IsReady => Tool is null || Tool.State == ToolState.Ready ||
                           (Tool.State == ToolState.Running && Tool.Definition.Port is null);

    public bool CanStartTool => Tool is { CanStart: true };

    public string WaitingText => Tool switch
    {
        null => "",
        { State: ToolState.Starting } => $"{Tool.Name} is starting… the page opens here by itself when it's ready.",
        { State: ToolState.Stopping } => $"{Tool.Name} is stopping.",
        _ => $"{Tool.Name} isn't running.",
    };

    public event Action? ReadyChanged;

    private void OnToolChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ToolViewModel.State)) return;
        Raise(nameof(IsReady));
        Raise(nameof(CanStartTool));
        Raise(nameof(WaitingText));
        ReadyChanged?.Invoke();
    }

    public void Detach()
    {
        if (Tool is not null) Tool.PropertyChanged -= OnToolChanged;
    }
}
