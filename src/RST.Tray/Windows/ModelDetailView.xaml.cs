// rst-c's own (not vendored): the model detail's code-behind (spec #25 Model Detail; F19 #25 WU4). The view-model is
// ModelDetailViewModel. Here: each time a detail opens it starts at the top and Back takes the keyboard focus, so the
// next Escape reaches it; Escape (a fresh press nothing inside has used) goes back to the list, as Back does, and is
// marked handled so it goes no further (the tray window's Close is its Cancel button).

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using RST.UI.Mvvm;

namespace RST.Tray.Windows;

internal partial class ModelDetailView : UserControl
{
    private ModelDetailViewModel? _model;

    public ModelDetailView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => UiGuard.Run("Model detail: bind", () => Bind(e.NewValue as ModelDetailViewModel));
        Loaded += (_, _) => UiGuard.Run("Model detail: bind", () => Bind(DataContext as ModelDetailViewModel));
        Unloaded += (_, _) => UiGuard.Run("Model detail: unbind", () => Bind(null));
        KeyDown += OnKeyDown;
    }

    private void Bind(ModelDetailViewModel? model)
    {
        if (ReferenceEquals(model, _model)) return;
        if (_model is not null) _model.PropertyChanged -= OnModelChanged;
        _model = model;
        if (_model is not null) _model.PropertyChanged += OnModelChanged;
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e) => UiGuard.Run("Model detail: opened", () =>
    {
        if (e.PropertyName != nameof(ModelDetailViewModel.ModelId) || _model is null) return;
        Scroller.ScrollToTop();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => UiGuard.Run("Model detail: focus Back", () =>
        {
            if (_model is { IsOpen: true } && IsVisible) BackButton.Focus();
        }));
    });

    internal void OnKeyDown(object sender, KeyEventArgs e) => UiGuard.Run("Model detail: Escape", () =>
    {
        if (e.Handled || e.Key != Key.Escape || e.IsRepeat || _model is not { IsOpen: true }) return;
        e.Handled = true;
        _model.Back();
    });
}
