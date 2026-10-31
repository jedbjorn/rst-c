// rst-c's own (not vendored). SC-046 (R5, T3): the async commands were `new RelayCommand(() => _ = WorkAsync())`,
// so a failure after the first await was lost in a discarded task: never logged, never shown. AsyncRelayCommand
// awaits the work and reports a failure as RelayCommand does. CommandSite names a command for the log.

using System.Runtime.CompilerServices;
using System.Windows.Input;
using RST.Core.Logging;

namespace RST.UI.Mvvm;

/// <summary>
/// An <see cref="ICommand"/> over an async handler (SC-046). Execute awaits it, so a failure is reported as
/// <see cref="RelayCommand"/>'s is (logged at Error with <see cref="Site"/>, toasted), never lost in a discarded
/// task. The handler runs synchronously up to its first await, as a <c>() =&gt; _ = WorkAsync()</c> command did.
/// </summary>
/// <remarks>
/// It does not block a second Execute while the first runs: each view-model already disables its commands while
/// its work runs (IsIdle, CanStartWork, IsWorking), and a click its state allows must not be dropped.
/// </remarks>
public sealed class AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null, [CallerArgumentExpression(nameof(execute))] string? site = null) : ICommand
{
    /// <summary>Where the command is, for the log.</summary>
    public string Site { get; } = CommandSite.Of(execute, site);

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => ExecuteAsync().Forget(Site);

    /// <summary>Runs the handler when CanExecute allows it (tests await this). The task never faults.</summary>
    public async Task ExecuteAsync()
    {
        if (!CanExecute(null)) return;
        try
        {
            await execute();
        }
        catch (Exception ex)
        {
            UiGuard.Report(Site, ex, execute.Target, toast: true);
        }
    }
}

/// <summary>A command's site for the log: its owning type, then its handler as the source wrote it.</summary>
internal static class CommandSite
{
    private const int MaxLength = 100;

    public static string Of(Delegate handler, string? expression)
    {
        var what = Shorten(expression) ?? handler.Method.Name;
        var owner = handler.Method.DeclaringType;
        // A lambda lives in a compiler-generated class nested in the type that wrote it.
        while (owner is { DeclaringType: not null } && owner.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false))
            owner = owner.DeclaringType;
        return owner is null ? what : owner.Name + ": " + what;
    }

    private static string? Shorten(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) return null;
        var flat = string.Join(' ', expression.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= MaxLength ? flat : flat[..(MaxLength - 1)] + "…";
    }
}
