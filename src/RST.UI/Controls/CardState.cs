// rst-c's own (not vendored): the "active" flag read by Theme.xaml's CardListBoxItem style.

using System.Windows;

namespace RST.UI.Controls;

/// <summary>
/// Marks a card as the active one (the Loader's loaded profile: a success border, which wins
/// over the selected accent border, as the HTML's <c>.active-profile</c> rule did). Set it from
/// the item container style:
/// <c>&lt;Setter Property="controls:CardState.IsActive" Value="{Binding IsLoaded}" /&gt;</c>.
/// </summary>
public static class CardState
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.RegisterAttached(
        "IsActive", typeof(bool), typeof(CardState), new FrameworkPropertyMetadata(false));

    public static bool GetIsActive(DependencyObject d) => (bool)d.GetValue(IsActiveProperty);

    public static void SetIsActive(DependencyObject d, bool value) => d.SetValue(IsActiveProperty, value);
}
