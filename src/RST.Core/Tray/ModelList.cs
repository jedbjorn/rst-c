// rst-c's own (not vendored): the tray Health page's Models list logic (spec #25, Models →
// "Which models are visible", Grouping, Aggregate cards, Model card, Empty and edge states;
// F19 #25 WU1). Pure and Revit-, WPF- and Windows-free, so RST.Tests covers it on Linux.
//
//   var index  = ModelListIndex.Load(readOptions);              // index.json, once per refresh
//   var result = index.Query(query, ModelList.LocalToday(now, zone));   // every chip / filter change
//
// Load parses spec #24's index rows once into compact, clamped buckets and search fields per
// model; Query only filters, sums and sorts them, so a chip change stays cheap (AC 9).
//
//   Recent     the last 14 calendar days, from each row's recent_days_totals
//   3M/6M/1Y   the last 13/26/52 ISO weeks including the current one, from recent_weeks_totals
//   visible    any open (or active) time in the window; with a version filter, also a selected
//              major whose last active day falls in the window
//   search     case-insensitive substring of title, file name, local or central path (and the
//              titles and paths the model had before), every model, all time; chips and the
//              version filter are ignored while searching
//   group      the newest major the model was opened in within the window (all time when
//              searching; among the selected majors when filtering), then its newest minor or
//              "unknown minor"; groups newest version first, cards newest last-active first.
//              The card itself always shows the newest version in the window (filter or not)
//
// Implausible index values are clamped for display: nothing negative, no day over 24 h, no
// week over 7 × 24 h, no all-time total over 24 h per day between the first and last active day.
// A row carrying activity whose first or last active day is missing, or reversed, cannot be
// bounded: it is skipped and logged.

using RST.Core.Health;
using RST.Core.Telemetry;
using RST.Core.Telemetry.Rollups;

namespace RST.Core.Tray;

public static class ModelList
{
    /// <summary>The Recent chip's length in calendar days.</summary>
    public const int RecentDays = 14;

    /// <summary>Today as the user's calendar has it (the window's last day).</summary>
    public static DateOnly LocalToday(DateTimeOffset nowUtc, TimeZoneInfo zone) => ActiveTime.LocalDay(nowUtc, zone);

    /// <summary>ISO weeks a weekly chip covers, the current one included; 0 for Recent.</summary>
    public static int WeeksFor(ModelTimeChip chip) => chip switch
    {
        ModelTimeChip.ThreeMonths => 13,
        ModelTimeChip.SixMonths => 26,
        ModelTimeChip.OneYear => 52,
        _ => 0,
    };

    /// <summary>The chip's first and last day: Recent is today and the 13 days before it; a weekly
    /// chip starts on the Monday of its oldest ISO week and ends today.</summary>
    public static (DateOnly First, DateOnly Last) Window(ModelTimeChip chip, DateOnly today)
    {
        var weeks = WeeksFor(chip);
        return weeks == 0
            ? (today.AddDays(-(RecentDays - 1)), today)
            : (ActivityWeeks.WeekStart(today).AddDays(-7 * (weeks - 1)), today);
    }

    /// <summary>The range the model detail opens on: the shortest pill covering the chip (Recent → 1M);
    /// 1Y while searching.</summary>
    public static ModelDetailRange DetailRangeFor(ModelListQuery query)
    {
        if (query.IsSearching) return ModelDetailRange.OneYear;
        return query.Chip switch
        {
            ModelTimeChip.ThreeMonths => ModelDetailRange.ThreeMonths,
            ModelTimeChip.SixMonths => ModelDetailRange.SixMonths,
            ModelTimeChip.OneYear => ModelDetailRange.OneYear,
            _ => ModelDetailRange.OneMonth,
        };
    }

    /// <summary>The text before the first dot of a subversion ("2026.4" → "2026").</summary>
    public static string MajorOf(string version)
    {
        var dot = version.IndexOf('.');
        return (dot < 0 ? version : version[..dot]).Trim();
    }

    /// <summary>The file name of a Windows or cloud path (either separator); null when none.</summary>
    public static string? FileNameOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Trim().TrimEnd('\\', '/');
        var cut = p.LastIndexOfAny(new[] { '\\', '/' });
        var name = cut < 0 ? p : p[(cut + 1)..];
        return name.Length == 0 ? null : name;
    }

    internal static string WithoutExtension(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        return dot > 0 ? fileName[..dot] : fileName;
    }
}

/// <summary>Revit versions in release order: dot-separated parts compared numerically
/// ("2026.10" after "2026.4"), text parts ordinally, a longer version after its prefix.</summary>
public sealed class RevitVersionOrder : IComparer<string?>
{
    public static readonly RevitVersionOrder Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        ReadOnlySpan<char> a = x, b = y;
        while (true)
        {
            var pa = Next(ref a, out var moreA);
            var pb = Next(ref b, out var moreB);
            var c = long.TryParse(pa, out var na) && long.TryParse(pb, out var nb)
                ? na.CompareTo(nb)
                : pa.CompareTo(pb, StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
            if (!moreA || !moreB) return moreA.CompareTo(moreB);
        }
    }

    /// <summary>The next dot-separated part, trimmed; <paramref name="more"/> says whether another follows.</summary>
    private static ReadOnlySpan<char> Next(ref ReadOnlySpan<char> rest, out bool more)
    {
        var dot = rest.IndexOf('.');
        more = dot >= 0;
        var part = more ? rest[..dot] : rest;
        rest = more ? rest[(dot + 1)..] : ReadOnlySpan<char>.Empty;
        return part.Trim();
    }
}

/// <summary>
/// spec #24's index, prepared for the Models list: built once per index read, then queried for
/// every chip, filter, group-by and search change. Immutable, so a query may run off the UI thread.
/// </summary>
public sealed class ModelListIndex
{
    private const long DayMs = 24L * 60 * 60 * 1000;

    public static readonly ModelListIndex Empty = new(Array.Empty<Entry>());

    private readonly Entry[] _entries;

    private ModelListIndex(Entry[] entries)
    {
        _entries = entries;
        var majors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
            foreach (var (v, _) in e.Majors) majors.Add(v);
        Versions = majors.OrderByDescending(v => v, RevitVersionOrder.Instance).ToList();
    }

    /// <summary>Models in the index.</summary>
    public int Count => _entries.Length;

    /// <summary>Every major version in the index, newest first (the version filter's choices).</summary>
    public IReadOnlyList<string> Versions { get; }

    /// <summary>Reads index.json through spec #24's reader (bad rows skipped and logged) and
    /// prepares it. Never throws: an unreadable index is an empty list.</summary>
    public static ModelListIndex Load(ActivityReadOptions options)
    {
        try
        {
            return From(ModelActivityReader.ReadIndex(options.RollupsDir, options.NowUtc, options.Zone, options.Log), options.Log);
        }
        catch (Exception ex)
        {
            SafeLog(options.Log, "models list: index load failed: " + ex.Message);
            return Empty;
        }
    }

    /// <summary>Prepares index rows. Null rows and rows without a model id are skipped; a repeated
    /// model id keeps its first row.</summary>
    public static ModelListIndex From(IEnumerable<ModelIndexRow?> rows, Action<string>? log = null)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<Entry>();
        foreach (var row in rows)
        {
            if (row is null || string.IsNullOrWhiteSpace(row.ModelId) || !seen.Add(row.ModelId)) continue;
            if (Entry.From(row, out var skipped) is { } entry) entries.Add(entry);
            else SafeLog(log, "models list: skipped index row " + row.ModelId + ": " + skipped);
        }
        return new ModelListIndex(entries.ToArray());
    }

    private static void SafeLog(Action<string>? log, string message)
    {
        try { log?.Invoke(message); }
        catch { /* logging never takes the list down */ }
    }

    /// <summary>The list for <paramref name="query"/> with today taken from the clock and zone.</summary>
    public ModelListResult Query(ModelListQuery query, DateTimeOffset nowUtc, TimeZoneInfo zone) =>
        Query(query, ModelList.LocalToday(nowUtc, zone));

    /// <summary>The list for <paramref name="query"/>, the window ending on <paramref name="today"/>.</summary>
    public ModelListResult Query(ModelListQuery query, DateOnly today)
    {
        query ??= ModelListQuery.Default;
        var search = query.SearchText;
        var searching = search is not null;
        var (first, last) = ModelList.Window(query.Chip, today);
        var weeks = ModelList.WeeksFor(query.Chip);
        var filter = searching || query.Versions is null || query.Versions.Count == 0
            ? null
            : new HashSet<string>(query.Versions.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()),
                StringComparer.OrdinalIgnoreCase);
        if (filter is { Count: 0 }) filter = null;

        var cards = new List<ModelCard>();
        foreach (var e in _entries)
        {
            Bucket totals;
            // latest: the card's version (the newest in the window, all time when searching).
            // group: where the card sits — the newest *selected* version under a filter, so no
            // group outside the filter appears (planner ruling, spec #25 Grouping).
            string? latest = null, group = null;
            DateOnly? from = null, to = null;
            if (searching)
            {
                if (!e.Matches(search!)) continue;
                totals = e.AllTime;
                latest = group = e.Majors.Length > 0 ? e.Majors[0].Version : null;
            }
            else
            {
                totals = weeks == 0 ? Sum(e.Days, first, last) : Sum(e.Weeks, ActivityWeeks.WeekStart(first), last);
                if (!totals.HasTime) continue;
                from = first;
                to = last;
                foreach (var (v, day) in e.Majors) // newest first
                {
                    if (day < first || day > last) continue;
                    latest ??= v;
                    if (filter is not null && !filter.Contains(v)) continue;
                    group = v;
                    break;
                }
                if (filter is not null && group is null) continue;
            }
            var latestMinor = latest is null ? null : NewestMinor(e, latest, from, to);
            var groupMinor = group is null ? null
                : string.Equals(group, latest, StringComparison.OrdinalIgnoreCase) ? latestMinor
                : NewestMinor(e, group, from, to);
            cards.Add(e.Card(totals, latest, latestMinor, group, groupMinor));
        }

        var groups = Group(cards, query.GroupBy);
        var ordered = groups.SelectMany(g => g.Cards).ToList();
        var empty = _entries.Length == 0 ? ModelListEmptyState.NoModels
            : ordered.Count > 0 ? ModelListEmptyState.None
            : searching ? ModelListEmptyState.NoMatches
            : ModelListEmptyState.NothingInTimeFrame;

        return new ModelListResult
        {
            Query = query,
            IsSearching = searching,
            WindowFirst = searching ? null : first,
            WindowLast = searching ? null : last,
            Aggregates = Aggregate(ordered),
            Groups = ordered.Count == 0 ? Array.Empty<ModelGroup>() : groups,
            Cards = ordered,
            EmptyState = empty,
            OfferOneYear = empty == ModelListEmptyState.NothingInTimeFrame && query.Chip != ModelTimeChip.OneYear,
            DetailRange = ModelList.DetailRangeFor(query),
            AvailableVersions = Versions,
            IndexedModels = _entries.Length,
        };
    }

    private static string? NewestMinor(Entry e, string major, DateOnly? first, DateOnly? last)
    {
        foreach (var (v, m, day) in e.Minors) // newest first
        {
            if (!string.Equals(m, major, StringComparison.OrdinalIgnoreCase)) continue;
            if (first is { } f && day < f) continue;
            if (last is { } l && day > l) continue;
            return v;
        }
        return null;
    }

    private static Bucket Sum(Bucket[] buckets, DateOnly first, DateOnly last)
    {
        var t = default(Bucket);
        foreach (var b in buckets)
            if (b.Date >= first && b.Date <= last) t = t.Plus(b);
        return t;
    }

    private static ModelListAggregates Aggregate(IReadOnlyList<ModelCard> cards)
    {
        double active = 0, openSum = 0, syncSum = 0;
        long opens = 0, syncs = 0;
        foreach (var c in cards)
        {
            active += c.Totals.ActiveSeconds;
            opens += c.Totals.Opens;
            openSum += c.Totals.OpenSecondsSum;
            syncs += c.Totals.Syncs;
            syncSum += c.Totals.SyncSecondsSum;
        }
        return new ModelListAggregates(cards.Count, active, opens, opens > 0 ? openSum / opens : null,
            syncs, syncs > 0 ? syncSum / syncs : null);
    }

    private static List<ModelGroup> Group(List<ModelCard> cards, ModelGroupBy by)
    {
        if (by == ModelGroupBy.None)
            return new List<ModelGroup> { NewGroup("all", by, null, null, cards) };

        var groups = cards
            .GroupBy(c => (Major: c.GroupMajor, Minor: by == ModelGroupBy.MinorVersion ? c.GroupMinor : null))
            .Select(g => NewGroup(
                g.Key.Major is null ? "unknown"
                    : by == ModelGroupBy.MajorVersion ? g.Key.Major
                    : g.Key.Minor ?? g.Key.Major + "/unknown",
                by, g.Key.Major, g.Key.Minor, g))
            .ToList();
        groups.Sort(CompareGroups);
        return groups;
    }

    /// <summary>Newest major first (no version last); within a major, newest minor first, unknown minor last.</summary>
    private static int CompareGroups(ModelGroup a, ModelGroup b)
    {
        var c = NullLastDescending(a.Major, b.Major);
        return c != 0 ? c : NullLastDescending(a.Minor, b.Minor);
    }

    private static int NullLastDescending(string? a, string? b)
    {
        if (a is null) return b is null ? 0 : 1;
        if (b is null) return -1;
        return RevitVersionOrder.Instance.Compare(b, a);
    }

    private static ModelGroup NewGroup(string key, ModelGroupBy by, string? major, string? minor, IEnumerable<ModelCard> cards)
    {
        var sorted = cards
            .OrderByDescending(c => c.LastActiveDay ?? DateOnly.MinValue)
            .ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.ModelId, StringComparer.Ordinal)
            .ToList();
        return new ModelGroup
        {
            Key = key,
            GroupBy = by,
            Major = major,
            Minor = minor,
            IsUnknownMinor = by == ModelGroupBy.MinorVersion && major is not null && minor is null,
            IsUnknownVersion = by != ModelGroupBy.None && major is null,
            Cards = sorted,
            ActiveSeconds = sorted.Sum(c => c.Totals.ActiveSeconds),
        };
    }

    /// <summary>A day's, week's or all-time totals in whole milliseconds, clamped.</summary>
    private readonly record struct Bucket(DateOnly Date, long ActiveMs, long OpenMs, long Opens, long OpenSumMs, long Syncs, long SyncSumMs)
    {
        public bool HasTime => OpenMs > 0 || ActiveMs > 0;

        public static Bucket From(ActivityTotals t, DateOnly date, long capMs) => new(
            date, Ms(t.ActiveMs, capMs), Ms(t.OpenMs, capMs), Math.Max(0, t.Opens), Ms(t.OpenSumMs, capMs),
            Math.Max(0, t.Syncs), Ms(t.SyncSumMs, capMs));

        public Bucket Plus(Bucket o) => new(Date, ActiveMs + o.ActiveMs, OpenMs + o.OpenMs, Opens + o.Opens,
            OpenSumMs + o.OpenSumMs, Syncs + o.Syncs, SyncSumMs + o.SyncSumMs);

        public Bucket Cap(long capMs) => this with
        {
            ActiveMs = Ms(ActiveMs, capMs), OpenMs = Ms(OpenMs, capMs),
            OpenSumMs = Ms(OpenSumMs, capMs), SyncSumMs = Ms(SyncSumMs, capMs),
        };

        public ModelListTotals ToTotals() =>
            new(ActiveMs / 1000.0, OpenMs / 1000.0, Opens, OpenSumMs / 1000.0, Syncs, SyncSumMs / 1000.0);

        private static long Ms(long ms, long cap) => ms < 0 ? 0 : ms > cap ? cap : ms;
    }

    /// <summary>One model, prepared.</summary>
    private sealed class Entry
    {
        public string ModelId = "";
        public string Title = "";
        public string? FileName;
        public bool ShowFileName;
        public string? LocalPath;
        public string? CentralPath;
        public bool IsWorkshared;
        public bool IsCloud;
        public ModelHealthColour Health;
        public DateOnly? FirstActiveDay;
        public DateOnly? LastActiveDay;
        public Bucket[] Days = Array.Empty<Bucket>();
        public Bucket[] Weeks = Array.Empty<Bucket>();
        public Bucket AllTime;

        /// <summary>Major → last day open in it, newest version first.</summary>
        public (string Version, DateOnly Last)[] Majors = Array.Empty<(string, DateOnly)>();

        /// <summary>Minor → its major and last day open in it, newest version first.</summary>
        public (string Version, string Major, DateOnly Last)[] Minors = Array.Empty<(string, string, DateOnly)>();

        /// <summary>The search fields: title, file name, local and central path, and the titles
        /// and paths the model had before; matched case-insensitively, in place.</summary>
        public string[] SearchFields = Array.Empty<string>();

        public bool Matches(string search)
        {
            foreach (var f in SearchFields)
                if (f.Contains(search, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>The prepared model, or null (with <paramref name="skipped"/> saying why) for a row
        /// whose activity dates are missing or reversed while it carries activity: its totals could
        /// not be bounded, so it is not shown (spec #25, Beyond Intention).</summary>
        public static Entry? From(ModelIndexRow row, out string? skipped)
        {
            skipped = null;
            var datesOk = row.FirstActiveDay is { } fd && row.LastActiveDay is { } ld && ld >= fd;
            if (!datesOk && HasActivity(row))
            {
                skipped = row.FirstActiveDay is null || row.LastActiveDay is null
                    ? "activity without a first or last active day"
                    : "last active day before first active day";
                return null;
            }

            var e = new Entry
            {
                ModelId = row.ModelId,
                LocalPath = Blank(row.LocalPath),
                CentralPath = Blank(row.CentralPath),
                IsCloud = row.IsCloud == true,
                FirstActiveDay = row.FirstActiveDay,
                LastActiveDay = row.LastActiveDay,
                Health = ModelHealth.Evaluate(row.Health).Colour,
            };
            e.IsWorkshared = row.IsWorkshared == true || e.IsCloud;
            e.FileName = ModelList.FileNameOf(e.LocalPath) ?? ModelList.FileNameOf(e.CentralPath);
            var title = Blank(row.Title);
            e.Title = title ?? (e.FileName is { } f ? ModelList.WithoutExtension(f) : row.ModelId);
            e.ShowFileName = !e.IsCloud && e.FileName is { } name &&
                !string.Equals(e.Title, name, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(e.Title, ModelList.WithoutExtension(name), StringComparison.OrdinalIgnoreCase);

            e.Days = Buckets(row.RecentDaysTotals, weekly: false, DayMs);
            e.Weeks = Buckets(row.RecentWeeksTotals, weekly: true, 7 * DayMs);
            // At most 24 h per day between the first and last active day (0 when the row has
            // neither: it then carries no activity, or it was skipped above).
            var spanDays = datesOk ? row.LastActiveDay!.Value.DayNumber - row.FirstActiveDay!.Value.DayNumber + 1L : 0;
            e.AllTime = Bucket.From(row.Totals ?? new ActivityTotals(), default, spanDays * DayMs);

            var majors = new List<(string Version, DateOnly Last)>(4);
            var minors = new List<(string Version, DateOnly Last)>(4);
            if (row.RevitVersions is not null)
                foreach (var (v, day) in row.RevitVersions)
                    if (Blank(v) is { } major) Latest(majors, major, day);
            if (row.RevitSubversions is not null)
                foreach (var (v, day) in row.RevitSubversions)
                {
                    if (Blank(v) is not { } minor || ModelList.MajorOf(minor) is not { Length: > 0 } major) continue;
                    Latest(minors, minor, day);
                    Latest(majors, major, day); // a subversion implies its major
                }
            majors.Sort((a, b) => RevitVersionOrder.Instance.Compare(b.Version, a.Version));
            minors.Sort((a, b) => RevitVersionOrder.Instance.Compare(b.Version, a.Version));
            e.Majors = majors.ToArray();
            e.Minors = new (string, string, DateOnly)[minors.Count];
            for (var i = 0; i < minors.Count; i++)
                e.Minors[i] = (minors[i].Version, ModelList.MajorOf(minors[i].Version), minors[i].Last);

            var seen = row.KeysSeen;
            var fields = new List<string>(4 + (seen is null ? 0 : seen.Titles.Count + seen.LocalPaths.Count + seen.CentralPaths.Count));
            AddField(fields, e.Title);
            AddField(fields, e.FileName);
            AddField(fields, e.LocalPath);
            AddField(fields, e.CentralPath);
            if (seen is not null)
            {
                foreach (var v in seen.Titles) AddField(fields, v);
                foreach (var v in seen.LocalPaths) AddField(fields, v);
                foreach (var v in seen.CentralPaths) AddField(fields, v);
            }
            e.SearchFields = fields.ToArray();
            return e;
        }

        private static bool HasActivity(ModelIndexRow row)
        {
            static bool Any(ActivityTotals? t) => t is not null &&
                (t.ActiveMs != 0 || t.OpenMs != 0 || t.Opens != 0 || t.OpenSumMs != 0 || t.Syncs != 0 || t.SyncSumMs != 0);
            if (Any(row.Totals)) return true;
            if (row.RecentDaysTotals is { } days)
                foreach (var t in days) if (Any(t)) return true;
            if (row.RecentWeeksTotals is { } weeks)
                foreach (var t in weeks) if (Any(t)) return true;
            return false;
        }

        private static void AddField(List<string> fields, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) fields.Add(value);
        }

        public ModelCard Card(Bucket totals, string? major, string? minor, string? groupMajor, string? groupMinor) => new()
        {
            ModelId = ModelId,
            Title = Title,
            FileName = FileName,
            ShowFileName = ShowFileName,
            LocalPath = LocalPath,
            CentralPath = CentralPath,
            IsWorkshared = IsWorkshared,
            IsCloud = IsCloud,
            Marker = IsCloud ? ModelMarker.Cloud : IsWorkshared ? ModelMarker.Workshared : ModelMarker.None,
            Health = Health,
            HealthSeverity = ModelHealth.ToSeverity(Health),
            LatestMajor = major,
            LatestMinor = minor,
            GroupMajor = groupMajor,
            GroupMinor = groupMinor,
            LastActiveDay = LastActiveDay,
            FirstActiveDay = FirstActiveDay,
            Totals = totals.ToTotals(),
        };

        /// <summary>Rows by bucket date, oldest first (a repeated date sums), each clamped to its cap.</summary>
        private static Bucket[] Buckets(List<ActivityTotals>? rows, bool weekly, long capMs)
        {
            if (rows is null || rows.Count == 0) return Array.Empty<Bucket>();
            var list = new List<Bucket>(rows.Count);
            foreach (var t in rows)
            {
                if (t is null || t.Date == default) continue;
                list.Add(Bucket.From(t, weekly ? ActivityWeeks.WeekStart(t.Date) : t.Date, capMs));
            }
            list.Sort((a, b) => a.Date.CompareTo(b.Date));
            var merged = 0;
            for (var i = 0; i < list.Count; i++)
            {
                if (merged > 0 && list[merged - 1].Date == list[i].Date)
                    list[merged - 1] = list[merged - 1].Plus(list[i]).Cap(capMs);
                else list[merged++] = list[i];
            }
            return merged == list.Count ? list.ToArray() : list.GetRange(0, merged).ToArray();
        }

        private static void Latest(List<(string Version, DateOnly Last)> list, string key, DateOnly day)
        {
            for (var i = 0; i < list.Count; i++)
            {
                if (!string.Equals(list[i].Version, key, StringComparison.OrdinalIgnoreCase)) continue;
                if (day > list[i].Last) list[i] = (list[i].Version, day);
                return;
            }
            list.Add((key, day));
        }

        private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }
}
