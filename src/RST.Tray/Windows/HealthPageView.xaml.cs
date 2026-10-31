// rst-c's own (not vendored): the tray's Health page code-behind (spec #25; F19 WU2). The view-model is
// TrayHealthViewModel. The Junk modal takes the keyboard focus when it opens (the page behind it is disabled) and
// gives it back when it closes, as in the add-in's Health window (OverlayFocus; rc.3 VM finding F2). Escape is
// left to WPF, as everywhere in the tray (decision #62: "not the tray app").

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using RST.UI.Dialogs;
using RST.UI.Health;
using RST.UI.Mvvm;

namespace RST.Tray.Windows;

internal partial class HealthPageView : UserControl
{
    private HealthViewModel? _device;
    private OverlayFocus? _junkFocus;

    public HealthPageView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => UiGuard.Run("tray Health: bind", () => Bind((e.NewValue as TrayHealthViewModel)?.Device));
        Unloaded += (_, _) => UiGuard.Run("tray Health: unbind", () => Bind(null));
        Loaded += (_, _) => UiGuard.Run("tray Health: bind", () => Bind((DataContext as TrayHealthViewModel)?.Device));
    }

    private void Bind(HealthViewModel? device)
    {
        if (ReferenceEquals(device, _device)) return;
        if (_device is not null) _device.PropertyChanged -= OnDeviceChanged;
        _device = device;
        if (_device is not null) _device.PropertyChanged += OnDeviceChanged;
    }

    private void OnDeviceChanged(object? sender, PropertyChangedEventArgs e) => UiGuard.Run("tray Health: focus the Junk modal", () =>
    {
        if (e.PropertyName != nameof(HealthViewModel.IsJunkOpen) || _device is null) return;
        if (Window.GetWindow(this) is not { } window) return;
        _junkFocus ??= new OverlayFocus(window);
        if (!_device.IsJunkOpen)
        {
            _junkFocus.Closed();
            return;
        }
        _junkFocus.Opened();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            UiGuard.Run("tray Health: focus the Junk modal", () => JunkCard.MoveFocus(new TraversalRequest(FocusNavigationDirection.First))));
    });
}
