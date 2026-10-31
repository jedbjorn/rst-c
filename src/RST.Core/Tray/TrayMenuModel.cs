// rst-c's own (not vendored): what the RST tray icon's menu holds (spec #20 §1; F17 WU1), kept Windows-free so
// RST.Tests covers it on Linux. RST.Tray turns these entries into a native Win32 popup menu (SC-043) and maps the
// command the user picked back to an action.

using System;
using System.Collections.Generic;

namespace RST.Core.Tray;

/// <summary>A tray menu command. The values are the Win32 menu item ids; 0 is "nothing chosen" (dismissed).</summary>
public enum TrayMenuCommand
{
    None = 0,
    Open = 1,
    CheckForUpdates = 2,
    Settings = 3,
    Quit = 4,
}

/// <summary>One line of the menu: an item, or a separator (<see cref="TrayMenuCommand.None"/>, no text).</summary>
public sealed record TrayMenuEntry(TrayMenuCommand Command, string Text, bool Enabled = true, bool IsDefault = false)
{
    public bool IsSeparator => Command == TrayMenuCommand.None;

    public static TrayMenuEntry Separator { get; } = new(TrayMenuCommand.None, "", Enabled: false);
}

/// <summary>The menu's texts, from the tray's string table.</summary>
public sealed record TrayMenuTexts(string Open, string CheckForUpdates, string Settings, string Quit);

public static class TrayMenuModel
{
    /// <summary>
    /// Open RST (the default item, shown bold), Check for updates (disabled when <paramref name="canCheckForUpdates"/>
    /// is false), a separator, Settings, a separator, Quit.
    /// </summary>
    public static IReadOnlyList<TrayMenuEntry> Entries(TrayMenuTexts texts, bool canCheckForUpdates)
    {
        ArgumentNullException.ThrowIfNull(texts);
        return new[]
        {
            new TrayMenuEntry(TrayMenuCommand.Open, texts.Open, IsDefault: true),
            new TrayMenuEntry(TrayMenuCommand.CheckForUpdates, texts.CheckForUpdates, Enabled: canCheckForUpdates),
            TrayMenuEntry.Separator,
            new TrayMenuEntry(TrayMenuCommand.Settings, texts.Settings),
            TrayMenuEntry.Separator,
            new TrayMenuEntry(TrayMenuCommand.Quit, texts.Quit),
        };
    }

    /// <summary>
    /// A WPF access-key text ("_Open RST", the tray's strings) as a Win32 menu text ("&amp;Open RST"): the first single
    /// underscore marks the access key; "__" is a literal underscore, as is any later single one; a literal
    /// ampersand is doubled.
    /// </summary>
    public static string Win32Text(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sb = new System.Text.StringBuilder(text.Length + 2);
        var keyed = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '&')
            {
                sb.Append("&&");
            }
            else if (c == '_' && i + 1 < text.Length && text[i + 1] == '_')
            {
                sb.Append('_');
                i++;
            }
            else if (c == '_' && !keyed && i + 1 < text.Length)
            {
                sb.Append('&');
                keyed = true;
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// The command TrackPopupMenuEx returned (TPM_RETURNCMD): one of the enabled items in <paramref name="entries"/>,
    /// else <see cref="TrayMenuCommand.None"/> (dismissed, or an id the menu does not hold).
    /// </summary>
    public static TrayMenuCommand Chosen(IReadOnlyList<TrayMenuEntry> entries, int commandId)
    {
        ArgumentNullException.ThrowIfNull(entries);
        foreach (var entry in entries)
            if (!entry.IsSeparator && entry.Enabled && (int)entry.Command == commandId) return entry.Command;
        return TrayMenuCommand.None;
    }
}
