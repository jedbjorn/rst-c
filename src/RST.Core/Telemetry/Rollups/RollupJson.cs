// rst-c's own (not vendored): JSON encoding for the rollup store.
//
// Day rows are written as a table — a "columns" header naming each field
// once and one array per row — so ten years of daily rows stay well under
// 1 MB (spec #24, AC 5). Readers map by column name: a column they do not
// know is ignored, a column they miss reads as empty, so a column added
// within a schema version breaks no reader. Durations are seconds with
// millisecond precision; timestamps are UTC ISO-8601 with milliseconds.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using RST.Core.Health;

namespace RST.Core.Telemetry.Rollups;

internal static class RollupJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new UtcMsConverter() },
    };

    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public static T? Deserialize<T>(ReadOnlySpan<byte> utf8) => JsonSerializer.Deserialize<T>(utf8, Options);

    /// <summary>Seconds (ms precision, exact decimal text) from whole milliseconds.</summary>
    public static void WriteSeconds(Utf8JsonWriter w, long ms) => w.WriteNumberValue(ms / 1000m);

    public static long ReadMs(JsonElement e) =>
        e.ValueKind == JsonValueKind.Number && e.TryGetDecimal(out var s)
            ? (long)Math.Round(s * 1000m, MidpointRounding.AwayFromZero)
            : 0;

    /// <summary>Round seconds to whole milliseconds, the precision every stored duration has.</summary>
    public static double RoundSeconds(double seconds) => Math.Round(seconds * 1000.0) / 1000.0;

    public static DateOnly? ReadDate(JsonElement e) =>
        e.ValueKind == JsonValueKind.String &&
        DateOnly.TryParseExact(e.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : null;

    public static DateTimeOffset? ReadTs(JsonElement e) =>
        e.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(e.GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t)
            ? t
            : null;

    public static void WriteTs(Utf8JsonWriter w, DateTimeOffset ts) =>
        w.WriteStringValue(ts.UtcDateTime.ToString(UtcMsConverter.Format, CultureInfo.InvariantCulture));

    public static long? ReadLong(JsonElement e) =>
        e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var v) ? v : null;

    public static int ReadCount(JsonElement e) =>
        e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var v) && v > 0 ? v : 0;

    public static List<string> ReadStrings(JsonElement e)
    {
        var list = new List<string>();
        if (e.ValueKind != JsonValueKind.Array) return list;
        foreach (var item in e.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s) list.Add(s);
        list.Sort(StringComparer.Ordinal);
        return list.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>Truncate to whole milliseconds — the precision timestamps are stored at.</summary>
    public static DateTimeOffset TruncateMs(DateTimeOffset ts) =>
        new(ts.UtcTicks - ts.UtcTicks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero);

    /// <summary>"2026-07-18T14:03:07.123Z" — the outbox's timestamp shape.</summary>
    internal sealed class UtcMsConverter : JsonConverter<DateTimeOffset>
    {
        public const string Format = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            DateTimeOffset.Parse(reader.GetString() ?? "", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            WriteTs(writer, value);
    }
}

/// <summary>Reads and writes a list of rows as {"columns": [...], "rows": [[...], ...]}.
/// Cells are read into a <typeparamref name="TBuilder"/>, then built into a row.</summary>
internal abstract class ColumnTableConverter<T, TBuilder> : JsonConverter<List<T>> where TBuilder : new()
{
    protected abstract IReadOnlyList<string> Columns { get; }

    protected abstract void WriteRow(Utf8JsonWriter w, T row);

    /// <summary>Set one column of <paramref name="row"/> from <paramref name="value"/>.</summary>
    protected abstract void ReadCell(TBuilder row, string column, JsonElement value);

    /// <summary>The finished row, or null to drop it (for example a row without a valid date).</summary>
    protected abstract T? Build(TBuilder row);

    public override List<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var list = new List<T>();
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("columns", out var cols) || cols.ValueKind != JsonValueKind.Array ||
            !root.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
            return list;
        var names = cols.EnumerateArray().Select(c => c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "").ToArray();
        foreach (var r in rows.EnumerateArray())
        {
            if (r.ValueKind != JsonValueKind.Array) continue;
            var row = new TBuilder();
            var i = 0;
            foreach (var cell in r.EnumerateArray())
            {
                if (i < names.Length && names[i].Length > 0) ReadCell(row, names[i], cell);
                i++;
            }
            if (Build(row) is { } built) list.Add(built);
        }
        return list;
    }

    public override void Write(Utf8JsonWriter writer, List<T> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteStartArray("columns");
        foreach (var c in Columns) writer.WriteStringValue(c);
        writer.WriteEndArray();
        writer.WriteStartArray("rows");
        foreach (var row in value)
        {
            writer.WriteStartArray();
            WriteRow(writer, row);
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    protected static void WriteNullable(Utf8JsonWriter w, long? v)
    {
        if (v is { } x) w.WriteNumberValue(x); else w.WriteNullValue();
    }

    protected static void WriteNullableSeconds(Utf8JsonWriter w, double? v)
    {
        if (v is { } x) w.WriteNumberValue((decimal)RollupJson.RoundSeconds(x)); else w.WriteNullValue();
    }
}

/// <summary>Daily rows (spec #24, Rollup contents).</summary>
internal sealed class DayTableConverter : ColumnTableConverter<RollupDay, DayTableConverter.Cells>
{
    public static readonly string[] ColumnNames =
    {
        "date", "active_seconds", "open_seconds", "opens", "open_seconds_sum", "open_seconds_max",
        "syncs", "sync_seconds_sum", "sync_seconds_max", "revit_versions", "revit_subversions",
        "stats_ts", "warnings_count", "file_size_bytes",
        "health_file_size_bytes", "health_warnings_count", "health_open_median_seconds", "health_sync_median_seconds",
    };

    protected override IReadOnlyList<string> Columns => ColumnNames;

    protected override void WriteRow(Utf8JsonWriter w, RollupDay d)
    {
        w.WriteStringValue(d.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        RollupJson.WriteSeconds(w, d.ActiveMs);
        RollupJson.WriteSeconds(w, d.OpenMs);
        w.WriteNumberValue(d.Opens);
        RollupJson.WriteSeconds(w, d.OpenSumMs);
        RollupJson.WriteSeconds(w, d.OpenMaxMs);
        w.WriteNumberValue(d.Syncs);
        RollupJson.WriteSeconds(w, d.SyncSumMs);
        RollupJson.WriteSeconds(w, d.SyncMaxMs);
        WriteStrings(w, d.RevitVersions);
        WriteStrings(w, d.RevitSubversions);
        if (d.Stats is { } s) RollupJson.WriteTs(w, s.Ts); else w.WriteNullValue();
        WriteNullable(w, d.Stats?.WarningsCount);
        WriteNullable(w, d.Stats?.FileSizeBytes);
        WriteNullable(w, d.Health?.FileSizeBytes);
        WriteNullable(w, d.Health?.WarningsCount);
        WriteNullableSeconds(w, d.Health?.OpenMedianSeconds);
        WriteNullableSeconds(w, d.Health?.SyncMedianSeconds);
    }

    /// <summary>A row being read: the counters, plus the stats and health cells that become records once the row is complete.</summary>
    internal sealed class Cells
    {
        public readonly RollupDay Day = new();
        public DateTimeOffset? StatsTs;
        public long? Warnings, Size, HSize, HWarnings;
        public double? HOpen, HSync;

        /// <summary>The row carries health columns: all-null cells are a day with no inputs
        /// (a grey dot), not a day without health.</summary>
        public bool HealthSeen;
    }

    protected override void ReadCell(Cells c, string column, JsonElement v)
    {
        var d = c.Day;
        switch (column)
        {
            case "date": d.Date = RollupJson.ReadDate(v) ?? default; break;
            case "active_seconds": d.ActiveMs = RollupJson.ReadMs(v); break;
            case "open_seconds": d.OpenMs = RollupJson.ReadMs(v); break;
            case "opens": d.Opens = RollupJson.ReadCount(v); break;
            case "open_seconds_sum": d.OpenSumMs = RollupJson.ReadMs(v); break;
            case "open_seconds_max": d.OpenMaxMs = RollupJson.ReadMs(v); break;
            case "syncs": d.Syncs = RollupJson.ReadCount(v); break;
            case "sync_seconds_sum": d.SyncSumMs = RollupJson.ReadMs(v); break;
            case "sync_seconds_max": d.SyncMaxMs = RollupJson.ReadMs(v); break;
            case "revit_versions": d.RevitVersions = RollupJson.ReadStrings(v); break;
            case "revit_subversions": d.RevitSubversions = RollupJson.ReadStrings(v); break;
            case "stats_ts": c.StatsTs = RollupJson.ReadTs(v); break;
            case "warnings_count": c.Warnings = NonNegative(RollupJson.ReadLong(v)); break;
            case "file_size_bytes": c.Size = NonNegative(RollupJson.ReadLong(v)); break;
            case "health_file_size_bytes": c.HealthSeen = true; c.HSize = NonNegative(RollupJson.ReadLong(v)); break;
            case "health_warnings_count": c.HealthSeen = true; c.HWarnings = NonNegative(RollupJson.ReadLong(v)); break;
            case "health_open_median_seconds": c.HealthSeen = true; c.HOpen = NonNegativeSeconds(v); break;
            case "health_sync_median_seconds": c.HealthSeen = true; c.HSync = NonNegativeSeconds(v); break;
        }
    }

    protected override RollupDay? Build(Cells c)
    {
        var d = c.Day;
        if (d.Date == default) return null;
        if (c.StatsTs is { } ts)
            d.Stats = new ModelStatsPoint(ts, ToInt(c.Warnings), c.Size);
        if (c.HealthSeen)
            d.Health = new HealthInputs(c.HSize, ToInt(c.HWarnings), c.HOpen, c.HSync);
        d.Clamp();
        return d;
    }

    private static void WriteStrings(Utf8JsonWriter w, List<string> values)
    {
        w.WriteStartArray();
        foreach (var v in values) w.WriteStringValue(v);
        w.WriteEndArray();
    }

    private static long? NonNegative(long? v) => v is { } x && x >= 0 ? x : null;

    private static int? ToInt(long? v) => v is { } x ? (int)Math.Min(x, int.MaxValue) : null;

    private static double? NonNegativeSeconds(JsonElement v) =>
        v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var s) && s >= 0 && s <= RollupDay.MaxDayMs / 1000.0
            ? s
            : null;
}

/// <summary>Recent day and week totals in an index row.</summary>
internal sealed class TotalsTableConverter : ColumnTableConverter<ActivityTotals, ActivityTotals>
{
    public static readonly string[] ColumnNames =
    {
        "date", "active_seconds", "open_seconds", "opens", "open_seconds_sum", "syncs", "sync_seconds_sum",
    };

    protected override IReadOnlyList<string> Columns => ColumnNames;

    protected override void WriteRow(Utf8JsonWriter w, ActivityTotals t)
    {
        w.WriteStringValue(t.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        TotalsWriter.WriteValues(w, t);
    }

    protected override void ReadCell(ActivityTotals t, string column, JsonElement v)
    {
        if (column == "date") t.Date = RollupJson.ReadDate(v) ?? default;
        else TotalsWriter.ReadValue(t, column, v);
    }

    protected override ActivityTotals? Build(ActivityTotals t) => t.Date != default ? t : null;
}

internal static class TotalsWriter
{
    public static void WriteValues(Utf8JsonWriter w, ActivityTotals t)
    {
        RollupJson.WriteSeconds(w, t.ActiveMs);
        RollupJson.WriteSeconds(w, t.OpenMs);
        w.WriteNumberValue(t.Opens);
        RollupJson.WriteSeconds(w, t.OpenSumMs);
        w.WriteNumberValue(t.Syncs);
        RollupJson.WriteSeconds(w, t.SyncSumMs);
    }

    public static void ReadValue(ActivityTotals t, string column, JsonElement v)
    {
        switch (column)
        {
            case "active_seconds": t.ActiveMs = Math.Max(0, RollupJson.ReadMs(v)); break;
            case "open_seconds": t.OpenMs = Math.Max(0, RollupJson.ReadMs(v)); break;
            case "opens": t.Opens = RollupJson.ReadCount(v); break;
            case "open_seconds_sum": t.OpenSumMs = Math.Max(0, RollupJson.ReadMs(v)); break;
            case "syncs": t.Syncs = RollupJson.ReadCount(v); break;
            case "sync_seconds_sum": t.SyncSumMs = Math.Max(0, RollupJson.ReadMs(v)); break;
        }
    }
}

/// <summary>All-time totals as an object: {"active_seconds": …, …}.</summary>
internal sealed class TotalsObjectConverter : JsonConverter<ActivityTotals>
{
    public override ActivityTotals Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var t = new ActivityTotals();
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return t;
        foreach (var p in doc.RootElement.EnumerateObject()) TotalsWriter.ReadValue(t, p.Name, p.Value);
        return t;
    }

    public override void Write(Utf8JsonWriter writer, ActivityTotals value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("active_seconds"); RollupJson.WriteSeconds(writer, value.ActiveMs);
        writer.WritePropertyName("open_seconds"); RollupJson.WriteSeconds(writer, value.OpenMs);
        writer.WriteNumber("opens", value.Opens);
        writer.WritePropertyName("open_seconds_sum"); RollupJson.WriteSeconds(writer, value.OpenSumMs);
        writer.WriteNumber("syncs", value.Syncs);
        writer.WritePropertyName("sync_seconds_sum"); RollupJson.WriteSeconds(writer, value.SyncSumMs);
        writer.WriteEndObject();
    }
}
