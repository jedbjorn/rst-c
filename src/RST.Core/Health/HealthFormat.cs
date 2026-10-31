// rst-c's own (not vendored): the Health window's value formatters, ported from the `fmt`
// object, fmtDur, fmtHoursShort and fmtElapsed of the retired
// src/RST.UI/Assets/health_viewer.html (spec #16, WU2). Output matches the JS:
// toLocaleString() becomes the given culture's grouped number (up to 3 decimals), toFixed and
// plain number-to-string stay culture-invariant, and Math.round rounds halves up.

using System.Globalization;

namespace RST.Core.Health;

/// <summary>Formats Health and Activity values. Every formatter returns <see cref="Missing"/> for a missing value.</summary>
public static class HealthFormat
{
    /// <summary>The "no value" mark (an em dash).</summary>
    public const string Missing = "—";

    private static readonly string[] ByteUnits = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>JS <c>Number.prototype.toLocaleString()</c>: grouped, up to three decimals.</summary>
    public static string Number(double value, CultureInfo? culture = null) =>
        value.ToString("#,0.###", culture ?? CultureInfo.CurrentCulture);

    /// <summary><c>fmt.num</c>: a grouped number.</summary>
    public static string Num(double? n, CultureInfo? culture = null) =>
        n is { } v ? Number(v, culture) : Missing;

    /// <summary><c>fmt.mb</c>: "16,384 MB".</summary>
    public static string Mb(double? n, CultureInfo? culture = null) =>
        n is { } v ? Number(v, culture) + " MB" : Missing;

    /// <summary><c>fmt.gb</c>: "123.45 GB".</summary>
    public static string Gb(double? n, CultureInfo? culture = null) =>
        n is { } v ? Number(v, culture) + " GB" : Missing;

    /// <summary><c>fmt.mbps</c>: "1,000 Mbps".</summary>
    public static string Mbps(double? n, CultureInfo? culture = null) =>
        n is { } v ? Number(v, culture) + " Mbps" : Missing;

    /// <summary><c>fmt.pct</c>: the raw number and a percent sign ("45.3%"), not grouped.</summary>
    public static string Pct(double? n) => n is { } v ? JsNumber(v) + "%" : Missing;

    /// <summary><c>fmt.str</c>: the text, or <see cref="Missing"/> when null or empty.</summary>
    public static string Str(string? s) => string.IsNullOrEmpty(s) ? Missing : s;

    /// <summary>
    /// <c>fmt.bytes</c>: binary units (B, KB, MB, GB, TB); one decimal under 10 of a unit above
    /// bytes ("5.2 GB", "4.0 KB"), none otherwise ("512 B", "140 MB"); "0 B" for zero.
    /// </summary>
    public static string Bytes(double? n)
    {
        if (n is not { } value) return Missing;
        if (value == 0) return "0 B";
        var i = 0;
        var v = value;
        while (v >= 1024 && i < ByteUnits.Length - 1)
        {
            v /= 1024;
            i++;
        }
        return ToFixed(v, v < 10 && i > 0 ? 1 : 0) + " " + ByteUnits[i];
    }

    /// <summary>
    /// <c>fmt.time</c>: an ISO 8601 timestamp in local time, the culture's general date and time
    /// (JS <c>toLocaleString()</c>); the text unchanged when it does not parse.
    /// </summary>
    public static string Time(string? iso, CultureInfo? culture = null, TimeZoneInfo? zone = null)
    {
        if (string.IsNullOrEmpty(iso)) return Missing;
        if (!DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ts)) return iso;
        return TimeZoneInfo.ConvertTime(ts, zone ?? TimeZoneInfo.Local).ToString("G", culture ?? CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// A chart tick's date: JS <c>toLocaleDateString(undefined, { month: 'short', day: 'numeric' })</c>,
    /// the culture's month-day pattern with the abbreviated month ("Oct 28" in en-US, "28. Okt." in de-DE).
    /// </summary>
    public static string ShortMonthDay(DateTimeOffset date, CultureInfo? culture = null)
    {
        var c = culture ?? CultureInfo.CurrentCulture;
        var pattern = c.DateTimeFormat.MonthDayPattern;
        pattern = pattern.Contains("MMMM", StringComparison.Ordinal) ? pattern.Replace("MMMM", "MMM", StringComparison.Ordinal) : "MMM d";
        return date.ToString(pattern, c);
    }

    /// <summary>
    /// <c>fmtDur</c>: a duration in seconds. Under 90 s: "5.3s" (one decimal under 10) or "45s";
    /// under 90 minutes: "1m 30s"; otherwise "1h 30m".
    /// </summary>
    public static string Dur(double? seconds)
    {
        if (seconds is not { } s || !double.IsFinite(s)) return Missing;
        if (s < 90) return (s < 10 ? ToFixed(s, 1) : JsNumber(JsRound(s))) + "s";
        var mTot = Math.Floor(s / 60);
        if (mTot < 90) return JsNumber(mTot) + "m " + JsNumber(JsRound(s % 60)).PadLeft(2, '0') + "s";
        return JsNumber(Math.Floor(mTot / 60)) + "h " + JsNumber(mTot % 60).PadLeft(2, '0') + "m";
    }

    /// <summary><c>fmtHoursShort</c>: hours to one decimal, trailing zeros dropped ("2.5h", "4h").</summary>
    public static string HoursShort(double? hours)
    {
        if (hours is not { } h || !double.IsFinite(h)) return Missing;
        return JsNumber(JsRound(h * 10) / 10) + "h";
    }

    /// <summary>
    /// Active time (spec #24): whole minutes, "0m", "11m", "1h 05m", "26h 00m". Active time is
    /// counted in edit-minute spans, so seconds would be noise.
    /// </summary>
    public static string Active(double? seconds)
    {
        if (seconds is not { } s || !double.IsFinite(s)) return Missing;
        var minutes = JsRound(Math.Max(0, s) / 60);
        if (minutes < 60) return JsNumber(minutes) + "m";
        return JsNumber(Math.Floor(minutes / 60)) + "h " + JsNumber(minutes % 60).PadLeft(2, '0') + "m";
    }

    /// <summary><c>fmtElapsed</c>: milliseconds as HH:MM:SS; <see cref="Missing"/> when negative.</summary>
    public static string Elapsed(double? ms)
    {
        if (ms is not { } m || !double.IsFinite(m) || m < 0) return Missing;
        var s = Math.Floor(m / 1000);
        return JsNumber(Math.Floor(s / 3600)).PadLeft(2, '0') + ":" +
               JsNumber(Math.Floor(s % 3600 / 60)).PadLeft(2, '0') + ":" +
               JsNumber(s % 60).PadLeft(2, '0');
    }

    /// <summary>
    /// The y-axis labels of an hours chart (Session time). <see cref="HoursShort"/> as the HTML drew
    /// them, unless two gridlines would read the same ("0.1h, 0.1h, 0h, 0h" for small values; the HTML
    /// had that defect): then whole minutes, then whole seconds, then finer seconds, the first that
    /// keeps every label distinct.
    /// </summary>
    public static IReadOnlyList<string> HoursAxis(IReadOnlyList<double> hours) => DistinctLabels(hours,
        h => HoursShort(h),
        h => JsNumber(JsRound(h * 60)) + "m",
        h => JsNumber(JsRound(h * 3600)) + "s",
        h => ToFixed(h * 3600, 1) + "s",
        h => ToFixed(h * 3600, 3) + "s",
        h => JsNumber(h * 3600) + "s");

    /// <summary>
    /// The y-axis labels of a duration chart (Opening time, Sync history): <see cref="Dur"/>, unless two
    /// gridlines would read the same (sub-second values: "0.0s" four times); then seconds with two,
    /// then three decimals, the first that keeps every label distinct.
    /// </summary>
    public static IReadOnlyList<string> DurAxis(IReadOnlyList<double> seconds) => DistinctLabels(seconds,
        s => Dur(s),
        s => ToFixed(s, 2) + "s",
        s => ToFixed(s, 3) + "s",
        s => JsNumber(s) + "s");

    /// <summary>The labels from the first formatter that gives every distinct value its own label; the last formatter otherwise.</summary>
    public static IReadOnlyList<string> DistinctLabels(IReadOnlyList<double> values, params Func<double, string>[] ladder)
    {
        string[] labels = [];
        foreach (var format in ladder)
        {
            labels = values.Select(format).ToArray();
            if (labels.Distinct(StringComparer.Ordinal).Count() == values.Distinct().Count()) return labels;
        }
        return labels;
    }

    /// <summary>JS <c>Math.round</c>: halves round up (toward +∞).</summary>
    public static double JsRound(double x) => Math.Floor(x + 0.5);

    /// <summary>JS <c>Number.prototype.toFixed(digits)</c> (culture-invariant).</summary>
    public static string ToFixed(double x, int digits) =>
        x.ToString("F" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    /// <summary>JS number-to-string: the shortest round-trip form, culture-invariant ("2.5", "4", "0.3").</summary>
    public static string JsNumber(double x) => x.ToString(CultureInfo.InvariantCulture);
}
