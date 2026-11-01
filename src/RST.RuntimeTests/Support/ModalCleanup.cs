// rst-c's own. SC-046 review M3: failure cleanup must end modal loops even when Close
// is cancelled or opens an unsaved-changes prompt. Never depend on a scripted dialog answer.

using System.Windows;
using System.Windows.Threading;

namespace RST.RuntimeTests.Support;

internal static class ModalCleanup
{
    internal static void Close(Window window)
    {
        var dispatcher = window.Dispatcher;
        // Close can enter another ShowDialog loop before returning. That loop runs this hide.
        // Hide only children there: WPF may reject hiding the owner while its Closing is active.
        // Shutdown also bounds an unowned prompt, or a failure while hiding the owned tree.
        var backstop = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Send, (s, _) =>
        {
            ((DispatcherTimer)s!).Stop();
            dispatcher.InvokeShutdown();
        }, dispatcher);
        var hide = dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
        {
            foreach (var child in window.OwnedWindows.OfType<Window>().ToList()) HideTree(child);
        }));
        try
        {
            window.Close();
        }
        finally
        {
            hide.Abort();
            backstop.Stop();
            HideTree(window);
        }
    }

    internal static void HideTree(Window window)
    {
        foreach (var child in window.OwnedWindows.OfType<Window>().ToList()) HideTree(child);
        if (window.IsVisible) window.Hide();
    }
}
