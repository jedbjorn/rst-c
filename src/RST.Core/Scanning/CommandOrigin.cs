// CommandOrigin.cs — provenance label for a scanned command.

namespace RST.Core.Scanning;

public enum CommandOrigin
{
    Unknown,
    Native,
    Autodesk,
    ThirdParty,
    Custom,
}
