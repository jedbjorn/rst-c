// Copied from splash-it src/SplashIt.UI/Mvvm/RelayCommand.cs (splash-it commit ba86a71; originally gantt-it src/GanttIt.Revit/UI/Mvvm/RelayCommand.cs, gantt-it commit a96d0a1), MIT, same owner.
// Changes: namespaces (SplashIt.* to RST.*); SC-046: Execute is guarded (an exception is logged at Error with the
// command's site and toasted, through UiGuard, instead of reaching Revit), and each command records its site.

using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace RST.UI.Mvvm;

/// <summary>An <see cref="ICommand"/> over delegates. CanExecute is re-queried through <see cref="CommandManager"/>.</summary>
/// <remarks>
/// A handler that throws is reported by <see cref="UiGuard.Report"/>: logged at Error with <see cref="Site"/>
/// (the owning type and the handler as written), and the view-model's toast says it did not work. A cancellation
/// is logged at Debug only.
/// </remarks>
public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null, [CallerArgumentExpression(nameof(execute))] string? site = null) : ICommand
{
    /// <summary>Where the command is, for the log: "ProfileBuilderViewModel: Duplicate".</summary>
    public string Site { get; } = CommandSite.Of(execute, site);

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        try
        {
            execute();
        }
        catch (Exception ex)
        {
            UiGuard.Report(Site, ex, execute.Target, toast: true);
        }
    }

    /// <summary>Asks every command to re-evaluate CanExecute (after validation changes).</summary>
    public static void Refresh() => CommandManager.InvalidateRequerySuggested();
}

/// <summary>A command whose handler takes the bound <c>CommandParameter</c>.</summary>
public sealed class RelayCommand<T>(Action<T> execute, Func<T, bool>? canExecute = null, [CallerArgumentExpression(nameof(execute))] string? site = null) : ICommand
{
    /// <summary>Where the command is, for the log.</summary>
    public string Site { get; } = CommandSite.Of(execute, site);

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    // A null parameter answers as if no predicate existed: WPF may query CanExecute
    // before the CommandParameter binding resolves, and doesn't always ask again.
    public bool CanExecute(object? parameter) =>
        parameter is T value ? canExecute?.Invoke(value) ?? true : canExecute is null;

    public void Execute(object? parameter)
    {
        if (parameter is not T value || !CanExecute(value)) return;
        try
        {
            execute(value);
        }
        catch (Exception ex)
        {
            UiGuard.Report(Site, ex, execute.Target, toast: true);
        }
    }
}
