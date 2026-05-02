// ScannedCommand.cs — one entry in the in-memory command catalog.
//
// Id is Revit's native command-id string:
//   - Built-in commands resolve via PostableCommand → "ID_BUTTON_MOVE" form.
//   - Add-in pushbuttons surface via the ribbon as "CustomCtrl_%TabName%PanelName%ButtonName".
// The Loader treats the Id space as unified — RevitCommandId.LookupCommandId(id)
// works for both classes.

namespace RST.Core.Scanning;

public sealed record ScannedCommand(
    string Id,
    string DisplayName,
    CommandOrigin Origin,
    string? AddinFile,
    string? AssemblyPath,
    string? SourceTab,
    string? SourcePanel);
