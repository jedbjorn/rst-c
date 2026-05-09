// HealthSnapshot.cs — JSON shape for the system health snapshot.
//
// Schema mirrors the upstream Python tool exactly (RST/app/health_scanner.py)
// so a snapshot written here renders without changes in the existing
// health_viewer.html UI. Field names are camelCase to match the on-disk
// JSON; null is the explicit "not available" marker for every measurement.

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RST.Core.Health;

public sealed class HealthSnapshot
{
    [JsonPropertyName("captureTimestamp")] public string CaptureTimestamp { get; set; } = "";
    [JsonPropertyName("identity")] public Identity Identity { get; set; } = new();
    [JsonPropertyName("ram")]      public Ram Ram         { get; set; } = new();
    [JsonPropertyName("cpu")]      public Cpu Cpu         { get; set; } = new();
    [JsonPropertyName("gpu")]      public Gpu Gpu         { get; set; } = new();
    [JsonPropertyName("disk")]     public Disk Disk       { get; set; } = new();
    [JsonPropertyName("display")]  public Display Display { get; set; } = new();
    [JsonPropertyName("network")]  public Network Network { get; set; } = new();
    [JsonPropertyName("os")]       public OsInfo Os       { get; set; } = new();
    [JsonPropertyName("revit")]    public RevitInfo Revit { get; set; } = new();
}

public sealed class Identity
{
    [JsonPropertyName("windowsUsername")] public string WindowsUsername { get; set; } = "";
    [JsonPropertyName("deviceName")]      public string DeviceName      { get; set; } = "";
}

public sealed class Ram
{
    [JsonPropertyName("totalMB")]     public long? TotalMB     { get; set; }
    [JsonPropertyName("availableMB")] public long? AvailableMB { get; set; }
    [JsonPropertyName("usedMB")]      public long? UsedMB      { get; set; }
    [JsonPropertyName("usedPercent")] public int?  UsedPercent { get; set; }
}

public sealed class Cpu
{
    [JsonPropertyName("name")]          public string Name           { get; set; } = "";
    [JsonPropertyName("logicalCores")]  public int    LogicalCores   { get; set; }
    [JsonPropertyName("physicalCores")] public int    PhysicalCores  { get; set; }
    [JsonPropertyName("usedPercent")]   public int?   UsedPercent    { get; set; }
}

public sealed class Gpu
{
    [JsonPropertyName("name")]          public string  Name          { get; set; } = "";
    [JsonPropertyName("driverVersion")] public string  DriverVersion { get; set; } = "";
    [JsonPropertyName("vramTotalMB")]   public long?   VramTotalMB   { get; set; }
}

public sealed class Disk
{
    [JsonPropertyName("totalGB")]      public double? TotalGB      { get; set; }
    [JsonPropertyName("availableGB")]  public double? AvailableGB  { get; set; }
    [JsonPropertyName("usedGB")]       public double? UsedGB       { get; set; }
    [JsonPropertyName("usedPercent")]  public double? UsedPercent  { get; set; }
    [JsonPropertyName("type")]         public string  Type         { get; set; } = "Unknown";
    [JsonPropertyName("busType")]      public string  BusType      { get; set; } = "";
    [JsonPropertyName("friendlyName")] public string  FriendlyName { get; set; } = "";
}

public sealed class Display
{
    [JsonPropertyName("monitorCount")]      public int    MonitorCount      { get; set; }
    [JsonPropertyName("primaryResolution")] public string PrimaryResolution { get; set; } = "";
}

public sealed class Network
{
    [JsonPropertyName("adapterName")] public string AdapterName { get; set; } = "";
    [JsonPropertyName("type")]        public string Type        { get; set; } = "Unknown";
    [JsonPropertyName("speedMbps")]   public long?  SpeedMbps   { get; set; }
}

public sealed class OsInfo
{
    [JsonPropertyName("name")]    public string Name    { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("release")] public string Release { get; set; } = "";
    [JsonPropertyName("build")]   public string Build   { get; set; } = "";
}

public sealed class RevitInfo
{
    [JsonPropertyName("version")]              public string  Version              { get; set; } = "";
    [JsonPropertyName("build")]                public string  Build                { get; set; } = "";
    [JsonPropertyName("username")]             public string  Username             { get; set; } = "";
    [JsonPropertyName("hardwareAcceleration")] public bool?   HardwareAcceleration { get; set; }
    [JsonPropertyName("model")]                public RevitModel Model             { get; set; } = new();
    [JsonPropertyName("warningsCount")]        public int?    WarningsCount        { get; set; }
    [JsonPropertyName("warningsBySeverity")]   public Dictionary<string, int> WarningsBySeverity { get; set; } = new();
}

public sealed class RevitModel
{
    [JsonPropertyName("name")]   public string  Name   { get; set; } = "";
    [JsonPropertyName("path")]   public string  Path   { get; set; } = "";
    [JsonPropertyName("sizeMB")] public double? SizeMB { get; set; }
}
