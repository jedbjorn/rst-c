// rst-c's own (not vendored): every Loader window stays off the taskbar (WU2 VM finding, applied
// to WU3): owned by Revit, they must not show their own taskbar entry, as the kit's MessageDialog
// and ColorPickerDialog do not.

using System.Windows;
using System.Windows.Threading;
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
                new ConfirmAddinChangesWindow(new ConfirmAddinChangesModel(["Kinship"], ["Twinmotion"], [])),
                new AddinCheckWindow(new AddinCheckModel("Architecture", QaCheck.Clear, _ => { })),
            ];
            foreach (var window in windows)
                Assert.False(window.ShowInTaskbar, $"{window.GetType().Name} must not have its own taskbar entry");
        }
        finally
        {
            Dispatcher.CurrentDispatcher.InvokeShutdown();
        }
    });
}
