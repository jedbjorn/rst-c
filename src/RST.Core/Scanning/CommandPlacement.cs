// CommandPlacement.cs — one ribbon home and its own label (SC-030, decision #67).

namespace RST.Core.Scanning;

/// <summary>A command's literal ribbon tab, panel, label and unlocalized tab id, in walk order.</summary>
public sealed record CommandPlacement(string Tab, string Panel, string Label, string? TabId);
