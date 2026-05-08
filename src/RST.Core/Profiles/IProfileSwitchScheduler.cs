// IProfileSwitchScheduler.cs — abstraction so the WebView2 bridge can
// trigger a live ribbon rebuild without RST.UI taking a Revit API
// dependency. The Revit-bound implementation lives in RST.Engine
// (ProfileSwitchScheduler), wired up at LoaderCommand.Execute time.

namespace RST.Core.Profiles;

public interface IProfileSwitchScheduler
{
    /// <summary>
    /// Queue a live profile switch. Returns immediately; the rebuild
    /// runs asynchronously on Revit's UI thread once Revit's main loop
    /// is pumping again (i.e. after the modal Loader window closes).
    /// </summary>
    /// <param name="profile">Resolved profile to apply. Pass null to
    /// tear down the current profile tab without building a new one
    /// (i.e. unload).</param>
    void Schedule(Profile? profile);
}
