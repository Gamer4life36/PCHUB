using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace PcHub.ViewModels;

/// <summary>Tiny INotifyPropertyChanged base. Only raises when a value really changes, which keeps UI work minimal.</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A command backed by delegates. Call <see cref="RefreshAll"/> after state that affects CanExecute changes.</summary>
public sealed class RelayCommand : ICommand
{
    private static event EventHandler? Requery;

    private readonly Action<object?> _run;
    private readonly Func<object?, bool>? _can;

    public RelayCommand(Action run, Func<bool>? can = null)
        : this(_ => run(), can is null ? null : _ => can())
    {
    }

    public RelayCommand(Action<object?> run, Func<object?, bool>? can = null)
    {
        _run = run;
        _can = can;
        if (_can is not null) Requery += (_, _) => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _can?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _run(parameter);

    /// <summary>Re-evaluates every command that has a CanExecute rule (the cross-platform stand-in for WPF's CommandManager).</summary>
    public static void RefreshAll() => Requery?.Invoke(null, EventArgs.Empty);
}

/// <summary>Things only the window can do (file pickers, the key prompt). Set once by the main window.</summary>
public interface IDialogs
{
    Task<string?> PickProgramAsync();
    Task<string?> PickFolderAsync(string title, string? startFolder);

    /// <summary>null = cancelled, "" = remove the saved key, otherwise the new key.</summary>
    Task<string?> AskApiKeyAsync();
}

public static class Ui
{
    public static IDialogs? Dialogs { get; set; }
}
