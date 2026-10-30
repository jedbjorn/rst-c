// Adapted from splash-it tests/SplashIt.RuntimeTests/Support/Fakes.cs (splash-it commit ba86a71), MIT, same owner.
// Changes: namespaces (SplashIt.* to RST.*); FakeDialogs and TempFolder only (splash-it's FakeHost fakes its settings host, which rst-c does not have).

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

    public bool Confirm(string title, string message, IReadOnlyList<string> items, string confirmText, bool danger, string automationId)
    {
        ConfirmCalls.Add((title, message, confirmText, danger, automationId));
        return ConfirmResult;
    }

    public Rgb? PickColor(Rgb current, IReadOnlyList<Rgb> palette, string title)
    {
        PickCalls.Add((current, palette, title));
        return PickResults.Count > 0 ? PickResults.Dequeue() : null;
    }

    public string? OpenFile(string title, string filter)
    {
        OpenCalls++;
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
