// Copied from splash-it src/SplashIt.UI/Mvvm/Behaviors.cs (splash-it commit ba86a71; originally gantt-it src/GanttIt.Revit/UI/Mvvm/Behaviors.cs, gantt-it commit a96d0a1), MIT, same owner.
// Changes: namespaces (SplashIt.* to RST.*).

using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace RST.UI.Mvvm;

/// <summary>Small attached properties shared by the views.</summary>
public static class Behaviors
{
    /// <summary>
    /// Marks a field as invalid. The implicit TextBox/ComboBox styles
    /// in Theme.xaml draw a red border when it is true.
    /// </summary>
    public static readonly DependencyProperty HasErrorProperty = DependencyProperty.RegisterAttached(
        "HasError", typeof(bool), typeof(Behaviors), new FrameworkPropertyMetadata(false));

    public static bool GetHasError(DependencyObject d) => (bool)d.GetValue(HasErrorProperty);

    public static void SetHasError(DependencyObject d, bool value) => d.SetValue(HasErrorProperty, value);

    /// <summary>
    /// A TextBox bound with <c>UpdateSourceTrigger=LostFocus</c> also commits on
    /// Enter, so typed lengths and durations apply without leaving the field.
    /// </summary>
    public static readonly DependencyProperty CommitOnEnterProperty = DependencyProperty.RegisterAttached(
        "CommitOnEnter", typeof(bool), typeof(Behaviors), new PropertyMetadata(false, OnCommitOnEnterChanged));

    public static bool GetCommitOnEnter(DependencyObject d) => (bool)d.GetValue(CommitOnEnterProperty);

    public static void SetCommitOnEnter(DependencyObject d, bool value) => d.SetValue(CommitOnEnterProperty, value);

    private static void OnCommitOnEnterChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box) return;
        box.KeyDown -= CommitOnEnter;
        if ((bool)e.NewValue) box.KeyDown += CommitOnEnter;
    }

    private static void CommitOnEnter(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        box.SelectAll();
        e.Handled = true;
    }
}
