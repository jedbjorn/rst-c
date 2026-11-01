// rst-c's own (not vendored): the Models cards' thumbnails (spec #25, Thumbnails; F19 #25 WU3).
//
//   - From Windows, the image Explorer shows (ShellThumbnailSource), 256 px, only for a file LocalPathGuard accepts
//     as local. Cloud models, network paths and mapped drives get the placeholder, and nothing here touches such a
//     path: the guard's drive-type probe is local metadata, and no File.Exists, last-write read or shell call runs
//     before it passes.
//   - Cached at %LocalAppData%\RST\Tray\thumbnails\<model_id>.png. The PNG carries the model file's last-write time
//     as its own, so a changed file is re-read and an unchanged one is served from the cache. A missing or moved
//     file keeps its last cached image.
//   - One background thread at below-normal priority, first come first served: the cards the list realizes (the
//     visible ones, then its small scroll cache) ask, and a card scrolled away cancels its request. The placeholder
//     shows until Ready arrives.
//   - Each model's outcome is logged once per tray session ("thumbnail: <model_id> shell ok", "… no shell
//     thumbnail (…)", "… skipped non-local", "… skipped cloud", "… file missing, kept cached image", …), never with
//     its path, so the VM check reads the outcomes from the tray log alone.
// RST never reads the .rvt's content or its embedded preview: that fallback needs FnB agreement (decision #59).

using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RST.Core.Logging;
using RST.Core.Telemetry;
using RST.Core.Tray;

namespace RST.Tray.Thumbnails;

/// <summary>A card asking for its model's thumbnail. <paramref name="Generation"/> is the asker's refresh generation,
/// echoed on <see cref="ThumbnailReadyEventArgs"/>, so an answer to an older refresh can be told apart and dropped.</summary>
internal sealed record ThumbnailRequest(string ModelId, string? LocalPath, string? CentralPath, bool IsCloud, int Generation = 0);

/// <summary>What became of a request (logged once per model and outcome).</summary>
internal enum ThumbnailOutcome
{
    /// <summary>Windows gave a thumbnail; it is cached now.</summary>
    ShellOk,

    /// <summary>The file is unchanged since the cached image was made.</summary>
    Cached,

    /// <summary>The file is missing or moved: the last cached image stays.</summary>
    KeptCached,

    /// <summary>The file is missing or moved and nothing was cached: the placeholder.</summary>
    FileMissing,

    /// <summary>Windows has no thumbnail for the file (no handler, a failed extraction): the placeholder, or the last cached image.</summary>
    NoShellThumbnail,

    /// <summary>A network path or mapped drive, or anything LocalPathGuard cannot verify as local: the placeholder, untouched.</summary>
    SkippedNonLocal,

    /// <summary>A cloud model: the placeholder (its local file is Revit's cache of the cloud model).</summary>
    SkippedCloud,

    /// <summary>The model has no path recorded.</summary>
    NoPath,

    /// <summary>A model id that cannot be a cache file name.</summary>
    InvalidId,
}

/// <summary>A thumbnail is ready (raised on the worker thread). A null image is the placeholder.</summary>
internal sealed class ThumbnailReadyEventArgs(string modelId, ImageSource? image, ThumbnailOutcome outcome, int generation = 0) : EventArgs
{
    public string ModelId { get; } = modelId;

    /// <summary>The generation of the request this answers.</summary>
    public int Generation { get; } = generation;
    public ImageSource? Image { get; } = image;
    public ThumbnailOutcome Outcome { get; } = outcome;
}

/// <summary>What the Models list uses: ask for a card's thumbnail, cancel it when the card scrolls away, hear when one is ready.</summary>
internal interface IModelThumbnails : IDisposable
{
    /// <summary>Queues a request; a repeat for a model already queued takes its place (its paths and generation), keeping
    /// its turn. Never blocks; never throws.</summary>
    void Request(ThumbnailRequest request);

    /// <summary>Drops a queued request (the card scrolled away); one being built finishes.</summary>
    void Cancel(string modelId);

    /// <summary>Raised on the worker thread: marshal to the UI thread before touching UI.</summary>
    event EventHandler<ThumbnailReadyEventArgs>? Ready;
}

/// <summary>The cache rules, run on the worker thread (synchronous, so RuntimeTests drive them directly).</summary>
internal sealed class ModelThumbnailCache
{
    /// <summary>The cached image's size: the shell's 256 px thumbnail.</summary>
    public const int CacheSize = 256;

    private readonly string _dir;
    private readonly IShellThumbnailSource _shell;
    private readonly Func<string, bool> _isLocal;
    private readonly Func<string, DateTime?> _lastWriteUtc;
    private readonly int _decodeHeight;

    /// <param name="dir">%LocalAppData%\RST\Tray\thumbnails (a temp folder in tests).</param>
    /// <param name="shell">Windows' thumbnails (a fake in tests).</param>
    /// <param name="isLocal">LocalPathGuard.IsLocalFile in the tray: the only check a path meets before anything reads it.</param>
    /// <param name="lastWriteUtc">The local file's last-write time, null when it is missing; called only for a path <paramref name="isLocal"/> accepted.</param>
    /// <param name="decodeHeight">The height the card's image is decoded at (the cache file stays 256 px).</param>
    public ModelThumbnailCache(string dir, IShellThumbnailSource shell, Func<string, bool>? isLocal = null,
                               Func<string, DateTime?>? lastWriteUtc = null, int decodeHeight = 192)
    {
        _dir = dir;
        _shell = shell;
        _isLocal = isLocal ?? (p => LocalPathGuard.IsLocalFile(p));
        _lastWriteUtc = lastWriteUtc ?? LastWriteUtc;
        _decodeHeight = decodeHeight;
    }

    /// <summary>The cache file for <paramref name="modelId"/>; null when the id cannot be a file name.</summary>
    public string? CacheFile(string modelId) => IsSafeId(modelId) ? Path.Combine(_dir, modelId + ".png") : null;

    /// <summary>The model's image (frozen; null = the placeholder) and what happened, with a reason for a shell failure.</summary>
    public (ImageSource? Image, ThumbnailOutcome Outcome, string? Reason) Resolve(ThumbnailRequest request)
    {
        if (CacheFile(request.ModelId) is not { } cache) return (null, ThumbnailOutcome.InvalidId, null);
        if (request.IsCloud) return (null, ThumbnailOutcome.SkippedCloud, null);
        var path = Blank(request.LocalPath) ?? Blank(request.CentralPath);
        if (path is null) return (null, ThumbnailOutcome.NoPath, null);
        // The guard first: a UNC path, a mapped drive or a cloud pseudo-path stops here, untouched.
        if (!_isLocal(path)) return (null, ThumbnailOutcome.SkippedNonLocal, null);

        var cachedStamp = CachedStamp(cache);
        var lastWrite = _lastWriteUtc(path);
        if (lastWrite is null)
            return cachedStamp is not null && Decode(cache) is { } kept
                ? (kept, ThumbnailOutcome.KeptCached, null)
                : (null, ThumbnailOutcome.FileMissing, null);
        if (cachedStamp == lastWrite && Decode(cache) is { } cached) return (cached, ThumbnailOutcome.Cached, null);

        var shell = _shell.Extract(path, CacheSize, out var reason);
        if (shell is null)
        {
            // The last cached image stays (an older version of the file is still a better card than the placeholder).
            var old = cachedStamp is not null ? Decode(cache) : null;
            return (old, ThumbnailOutcome.NoShellThumbnail, reason ?? "none");
        }
        var png = Encode(shell);
        Save(cache, png, lastWrite.Value);
        return (Decode(png), ThumbnailOutcome.ShellOk, null);
    }

    /// <summary>Lowercase or uppercase letters, digits, '-' and '_' only (spec #24's model ids are hashes).</summary>
    internal static bool IsSafeId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 128) return false;
        foreach (var c in id)
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return false;
        return true;
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static DateTime? LastWriteUtc(string path)
    {
        try
        {
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
        }
        catch
        {
            return null; // unreadable reads as missing: the last cached image stays
        }
    }

    private static DateTime? CachedStamp(string cache)
    {
        try
        {
            return File.Exists(cache) ? File.GetLastWriteTimeUtc(cache) : null;
        }
        catch
        {
            return null;
        }
    }

    private static byte[] Encode(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>Writes the PNG beside the cache file, then moves it over, stamped with the model file's last-write time.</summary>
    private void Save(string cache, byte[] png, DateTime lastWriteUtc)
    {
        Directory.CreateDirectory(_dir);
        var temp = cache + ".tmp";
        File.WriteAllBytes(temp, png);
        File.SetLastWriteTimeUtc(temp, lastWriteUtc);
        File.Move(temp, cache, overwrite: true);
    }

    private ImageSource? Decode(string cache)
    {
        try
        {
            return Decode(File.ReadAllBytes(cache));
        }
        catch
        {
            return null; // unreadable or corrupt: rebuilt when the shell has one
        }
    }

    private ImageSource? Decode(byte[] png)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelHeight = _decodeHeight;
            image.StreamSource = new MemoryStream(png);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>The worker: one below-normal STA thread serving requests in order.</summary>
internal sealed class ModelThumbnails : IModelThumbnails
{
    private readonly ModelThumbnailCache _cache;
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private readonly LinkedList<ThumbnailRequest> _queue = new();
    private readonly Dictionary<string, LinkedListNode<ThumbnailRequest>> _queued = new(StringComparer.Ordinal);
    // Per tray session, not per window: the window is rebuilt on a theme change.
    private static readonly HashSet<string> Logged = new(StringComparer.Ordinal);
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private volatile bool _stopped;

    /// <summary>The tray's: the shell, %LocalAppData%\RST\Tray\thumbnails, the tray log.</summary>
    public static ModelThumbnails ForTray() => new(
        new ModelThumbnailCache(Path.Combine(TrayPaths.LocalRoot, "thumbnails"), ShellThumbnailSource.Instance),
        m => Log.Information("{Message}", m));

    public ModelThumbnails(ModelThumbnailCache cache, Action<string> log)
    {
        _cache = cache;
        _log = log;
        _thread = new Thread(Run) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "RST thumbnails" };
        _thread.SetApartmentState(ApartmentState.STA); // the shell's thumbnail objects are apartment-threaded
        _thread.Start();
    }

    public event EventHandler<ThumbnailReadyEventArgs>? Ready;

    public void Request(ThumbnailRequest request)
    {
        if (_stopped || string.IsNullOrEmpty(request.ModelId)) return;
        lock (_gate)
        {
            if (_queued.TryGetValue(request.ModelId, out var queued))
            {
                queued.Value = request;
                return;
            }
            _queued[request.ModelId] = _queue.AddLast(request);
        }
        _wake.Set();
    }

    public void Cancel(string modelId)
    {
        lock (_gate)
        {
            if (!_queued.Remove(modelId, out var node)) return;
            _queue.Remove(node);
        }
    }

    public void Dispose()
    {
        _stopped = true;
        lock (_gate)
        {
            _queue.Clear();
            _queued.Clear();
        }
        _wake.Set(); // the thread ends after the request it is on (it is a background thread: it never holds the tray open)
    }

    private void Run()
    {
        while (!_stopped)
        {
            ThumbnailRequest? next;
            lock (_gate)
            {
                next = _queue.First?.Value;
                if (next is not null)
                {
                    _queue.RemoveFirst();
                    _queued.Remove(next.ModelId);
                }
            }
            if (next is null)
            {
                _wake.WaitOne();
                continue;
            }
            Serve(next);
        }
    }

    private void Serve(ThumbnailRequest request)
    {
        ImageSource? image = null;
        var outcome = ThumbnailOutcome.NoShellThumbnail;
        string? reason;
        try
        {
            (image, outcome, reason) = _cache.Resolve(request);
        }
        catch (Exception ex)
        {
            reason = ex.GetType().Name + ": " + ex.Message;
        }
        LogOnce(request.ModelId, outcome, reason);
        if (_stopped) return;
        try
        {
            Ready?.Invoke(this, new ThumbnailReadyEventArgs(request.ModelId, image, outcome, request.Generation));
        }
        catch (Exception ex)
        {
            _log("thumbnail: " + request.ModelId + " ready handler failed: " + ex.Message);
        }
    }

    private void LogOnce(string modelId, ThumbnailOutcome outcome, string? reason)
    {
        lock (Logged)
        {
            if (!Logged.Add(modelId + "|" + outcome)) return;
        }
        try
        {
            _log("thumbnail: " + modelId + " " + Describe(outcome) + (reason is null ? "" : " (" + reason + ")"));
        }
        catch
        {
            // logging never stops the thumbnails
        }
    }

    internal static string Describe(ThumbnailOutcome outcome) => outcome switch
    {
        ThumbnailOutcome.ShellOk => "shell ok",
        ThumbnailOutcome.Cached => "cached",
        ThumbnailOutcome.KeptCached => "file missing, kept cached image",
        ThumbnailOutcome.FileMissing => "file missing, placeholder",
        ThumbnailOutcome.NoShellThumbnail => "no shell thumbnail",
        ThumbnailOutcome.SkippedNonLocal => "skipped non-local",
        ThumbnailOutcome.SkippedCloud => "skipped cloud",
        ThumbnailOutcome.NoPath => "no path, placeholder",
        ThumbnailOutcome.InvalidId => "skipped invalid id",
        _ => outcome.ToString(),
    };
}
