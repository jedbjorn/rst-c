// rst-c's own (not vendored): severity colors for the Health window (spec #16, WU2), as the HTML's
// rst-section__dot--*, rst-section__row-value--*, sub--* and sub-accent--* classes drew them.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using RST.Core.Health;
using RST.Core.Theming;

namespace RST.UI.Health;

/// <summary>Maps a severity to its palette brush key.</summary>
public static class HealthBrushes
{
    /// <summary>
    /// Ok: success; WarnSoft: caution; Warn: warning; Danger: error; Info: the accent;
    /// None: <paramref name="fallback"/> (the text's own color).
    /// </summary>
    public static string KeyFor(HealthSeverity severity, string fallback) => severity switch
    {
        HealthSeverity.Ok => UiPalette.Success,
        HealthSeverity.WarnSoft => UiPalette.CautionText,
        HealthSeverity.Warn => UiPalette.WarningText,
        HealthSeverity.Danger => UiPalette.Error,
        HealthSeverity.Info => UiPalette.AccentForeground,
        _ => fallback,
    };
}

/// <summary>
/// <c>HealthText.Runs</c>: fills a TextBlock with a sub-line's runs, each in its severity color
/// (uncolored runs keep the sub-line's muted color).
/// </summary>
public static class HealthText
{
    public static readonly DependencyProperty RunsProperty = DependencyProperty.RegisterAttached(
        "Runs", typeof(IReadOnlyList<HealthRun>), typeof(HealthText), new PropertyMetadata(null, OnRunsChanged));

    public static IReadOnlyList<HealthRun>? GetRuns(DependencyObject d) => (IReadOnlyList<HealthRun>?)d.GetValue(RunsProperty);

    public static void SetRuns(DependencyObject d, IReadOnlyList<HealthRun>? value) => d.SetValue(RunsProperty, value);

    private static void OnRunsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock text) return;
        text.Inlines.Clear();
        if (e.NewValue is not IReadOnlyList<HealthRun> runs) return;
        foreach (var r in runs)
        {
            var run = new Run(r.Text);
            if (r.Severity != HealthSeverity.None)
                run.SetResourceReference(TextElement.ForegroundProperty, HealthBrushes.KeyFor(r.Severity, UiPalette.MutedText));
            text.Inlines.Add(run);
        }
    }
}
