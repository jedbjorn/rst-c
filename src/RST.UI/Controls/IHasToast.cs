// rst-c's own (not vendored). SC-046: a view-model whose window shows a TransientMessage toast. A command of its
// that fails says so there (UiGuard.Report), as the window's own failures do.

namespace RST.UI.Controls;

/// <summary>A view-model with an in-window toast (Health, the Loader, the Builder).</summary>
public interface IHasToast
{
    TransientMessage Toast { get; }
}
