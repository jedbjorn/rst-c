// rst-c's own (not vendored): every Loader window stays off the taskbar (WU2 VM finding, applied
// to WU3): owned by Revit, they must not show their own taskbar entry, as the kit's MessageDialog
// and ColorPickerDialog do not.

using System.Windows;
using System.Windows.Threading;
using RST.Core.Activation;
using RST.Core.Loader;
using RST.Core.Theming;
using RST.RuntimeTests.Support;
using RST.UI.Loader;
using RST.UI.Theming;
using Xunit;

namespace RST.RuntimeTests.Loader;

[Collection(WpfThemeCollection.Name)]
public class LoaderTaskbarTests
{
    [Fact]
    public void Every_loader_window_is_kept_off_the_taskbar() => Screenshots.RunSta(() =>
    {
        try
        {
            RstTheme.Apply(UiTheme.Light);
            var dialogs = new LoaderTestDialogs();
            Window[] windows =
            [
                new ProfileLoaderWindow(new ProfileLoaderViewModel(new FakeLoaderBackend(), dialogs, dialogs)),
                new ConfirmAddinChangesWindow(new ConfirmAddinChangesModel(["Twinmotion (2026)"], [], ["Kinship (2026)"], [])),
                new AddinReportWindow(AddinReportModel.ForLoad("Architecture", ActivationReport.Empty(ActivationIntent.Load), QaCheck.Clear, _ => { })),
                new AddinReportWindow(AddinReportModel.ForRestoreAll(ActivationReport.Empty(ActivationIntent.RestoreAll))),
            ];
            foreach (var window in windows)
                Assert.False(window.ShowInTaskbar, $"{window.GetType().Name} must not have its own taskbar entry");
        }
        finally
        {
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        }
    });

    /// <summary>
    /// Spec #18 (AC4): the Confirm and report dialogs close from their own buttons while shown
    /// modally, once, with no work in Closing (the Builder's VM finding: closing from inside a WPF
    /// event re-enters WPF).
    /// </summary>
    [Fact]
    public void The_add_in_dialogs_close_once_from_their_buttons_while_modal() => Screenshots.RunSta(() =>
    {
        try
        {
            RstTheme.Apply(UiTheme.Light);
            var confirm = new ConfirmAddinChangesWindow(new ConfirmAddinChangesModel(["Twinmotion (2026)"], [], [], ["Revizto (2027): locked on by your administrator"]));
            Assert.True(ShowAndClick(confirm, "ConfirmButton"));
            var cancel = new ConfirmAddinChangesWindow(new ConfirmAddinChangesModel(["Twinmotion (2026)"], [], [], []));
            Assert.False(ShowAndClick(cancel, "CancelButton"));
            var report = new AddinReportWindow(AddinReportModel.ForUnload(ActivationReport.Empty(ActivationIntent.Unload)));
            Assert.True(ShowAndClick(report, "ContinueButton"));
        }
        finally
        {
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        }
    });

    private static bool? ShowAndClick(Window dialog, string buttonName)
    {
        var closing = 0;
        dialog.Closing += (_, _) => closing++;
        dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        dialog.Left = -32000;
        dialog.Top = -32000;
        dialog.ShowActivated = false;
        dialog.ContentRendered += (_, _) => dialog.Dispatcher.BeginInvoke(() =>
        {
            var button = (System.Windows.Controls.Button)dialog.FindName(buttonName);
            var peer = new System.Windows.Automation.Peers.ButtonAutomationPeer(button);
            ((System.Windows.Automation.Provider.IInvokeProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)).Invoke();
        });
        var result = dialog.ShowDialog();
        Assert.Equal(1, closing);
        Assert.False(dialog.IsVisible);
        return result;
    }
}
