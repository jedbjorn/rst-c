// Adapted from splash-it tests/SplashIt.RuntimeTests/Support/Fakes.cs (splash-it commit ba86a71), MIT, same owner.
// Changes: namespaces (SplashIt.* to RST.*); FakeDialogs and TempFolder only (splash-it's FakeHost fakes its settings host, which rst-c does not have);
// FakeDialogs answers Choose (the three-way prompt) and can act while the Open dialog shows.

using System.IO;
using RST.Core.Colors;
using RST.UI.Dialogs;

namespace RST.RuntimeTests.Support;

/// <summary>Scripted answers for every dialog, and a record of what was asked.</summary>
public sealed class FakeDialogs : IDialogService
{
    public Queue<Rgb?> PickResults { get; } = new();

    public List<(Rgb Current, IReadOnlyList<Rgb> Palette, string Title)> PickCalls { get; } = [];

    public bool ConfirmResult { get; set; }

    public List<(string Title, string Message, string ConfirmText, bool Danger, string AutomationId)> ConfirmCalls { get; } = [];

    public List<(string Title, string Message)> Messages { get; } = [];

    public string? OpenResult { get; set; }

    public string? SaveResult { get; set; }

    public int OpenCalls { get; private set; }

    public int SaveCalls { get; private set; }

    public void ShowMessage(string title, string message, string automationId = MessageDialog.MessageId) =>
        Messages.Add((title, message));

    /// <summary>The items each confirm listed, in the order of <see cref="ConfirmCalls"/>.</summary>
    public List<IReadOnlyList<string>> ConfirmItems { get; } = [];

    public bool Confirm(string title, string message, IReadOnlyList<string> items, string confirmText, bool danger, string automationId)
    {
        ConfirmCalls.Add((title, message, confirmText, danger, automationId));
        ConfirmItems.Add(items);
        return ConfirmResult;
    }

    public Queue<DialogChoice> ChooseAnswers { get; } = new();

    public List<(string Title, string Message, string PrimaryText, string SecondaryText, string AutomationId)> ChooseCalls { get; } = [];

    public DialogChoice Choose(string title, string message, string primaryText, string secondaryText, string automationId)
    {
        ChooseCalls.Add((title, message, primaryText, secondaryText, automationId));
        return ChooseAnswers.Count > 0 ? ChooseAnswers.Dequeue() : DialogChoice.Cancel;
    }

    public Rgb? PickColor(Rgb current, IReadOnlyList<Rgb> palette, string title)
    {
        PickCalls.Add((current, palette, title));
        return PickResults.Count > 0 ? PickResults.Dequeue() : null;
    }

    /// <summary>Runs while the Open dialog is "showing" (e.g. what the dialog does to the window behind it).</summary>
    public Action? DuringOpen { get; set; }

    public string? OpenFile(string title, string filter)
    {
        OpenCalls++;
        DuringOpen?.Invoke();
        return OpenResult;
    }

    public string? SaveFile(string title, string filter, string fileName)
    {
        SaveCalls++;
        return SaveResult;
    }
}

/// <summary>A temp folder deleted on dispose.</summary>
public sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rst-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
