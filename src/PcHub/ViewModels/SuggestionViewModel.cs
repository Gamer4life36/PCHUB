using PcHub.Services;

namespace PcHub.ViewModels;

/// <summary>An app "Find my AI apps" spotted. Starts unticked: the user decides what gets added.</summary>
public sealed class SuggestionViewModel : ObservableObject
{
    private bool _isChecked;

    public SuggestionViewModel(ToolDefinition definition) => Definition = definition;

    public ToolDefinition Definition { get; }
    public string Name => Definition.Name;
    public string Location => Definition.WorkingDirectory ?? Path.GetDirectoryName(Definition.FileName) ?? Definition.FileName;

    public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value); }
}
