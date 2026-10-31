// rst-c's own. SC-046 (follow-up to issue #163; the crash-path audit's checks 2 and 3): source scans that keep the
// hardening in place.
//   - No `async void` in product code: its exception has no task to land in and goes straight to the dispatcher (in
//     Revit: Revit's). An event handler that must be async void says so with the allow-list comment.
//   - No discarded `_ = SomethingAsync(...)`: the task's failure is never observed, so it is never logged. Await it,
//     make it an AsyncRelayCommand, or let TaskGuard.Forget / UiGuard.Start watch it.
//   - Every code-behind event handler in RST.UI (a method taking EventArgs, a lambda subscribed to an event) runs its
//     body through UiGuard.Run, so a fault there is logged and the window keeps running.

using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace RST.Tests.Theming;

public class CrashPathSourceTests
{
    /// <summary>The comment that allows one `async void` event handler (on its line or the line above).</summary>
    public const string AsyncVoidAllowed = "// async-void: event handler";

    private static readonly string[] ProductProjects = ["RST.Bootstrap", "RST.Core", "RST.Engine", "RST.Tray", "RST.UI"];

    private static readonly Regex AsyncVoid = new(@"\basync\s+void\b", RegexOptions.Compiled);

    private static readonly Regex DiscardedAsync = new(@"(?<![\w.])_\s*=\s*(?:await\s+)?[\w.?!]*Async\s*\(", RegexOptions.Compiled);

    private static readonly Regex HandlerMethod = new(
        @"\bvoid\s+(?<name>On\w*)\s*\((?<params>[^()]*EventArgs\s+\w+)\)", RegexOptions.Compiled);

    private static readonly Regex SubscribedLambda = new(
        @"(?:\+=\s*|new\s+\w*EventHandler\(\s*)(?:\([^()]*\)|\w+)\s*=>\s*", RegexOptions.Compiled);

    // ---- the rules ----------------------------------------------------------------------------

    [Fact]
    public void No_product_code_is_async_void_unless_it_is_an_allow_listed_event_handler()
    {
        var violations = ProductFiles().SelectMany(f => AsyncVoidViolations(Read(f)).Select(v => Where(f, v))).ToList();
        violations.Should().BeEmpty(
            "an async void method's exception reaches the dispatcher (inside Revit: Revit) with nothing to catch it; " +
            "return a Task and await it, or use AsyncRelayCommand (SC-046). An event handler that must be async void " +
            $"carries \"{AsyncVoidAllowed}\" and guards its own body");
    }

    [Fact]
    public void No_product_code_discards_an_async_call()
    {
        var violations = ProductFiles().SelectMany(f => DiscardViolations(Read(f)).Select(v => Where(f, v))).ToList();
        violations.Should().BeEmpty(
            "`_ = SomethingAsync()` never observes the task, so its failure is lost; await it, make the command an " +
            "AsyncRelayCommand, or watch it with TaskGuard.Forget(site) / UiGuard.Start (SC-046)");
    }

    [Fact]
    public void Every_code_behind_handler_in_RST_UI_runs_through_UiGuard()
    {
        var files = CodeBehindFiles();
        files.Should().Contain(f => f.EndsWith("ProfileBuilderWindow.xaml.cs", StringComparison.Ordinal), "the scan must reach the windows");
        var violations = files.SelectMany(f => UnguardedHandlers(Read(f)).Select(v => Where(f, v))).ToList();
        violations.Should().BeEmpty(
            "a code-behind handler runs on Revit's UI thread with nothing above it; wrap its body in " +
            "UiGuard.Run(\"<window>: <what>\", ...) so a fault is logged and the window keeps running (SC-046)");
    }

    // ---- the rules find what they are for -----------------------------------------------------

    [Fact]
    public void The_async_void_rule_finds_one_and_honours_the_allow_list()
    {
        const string source = """
            class A
            {
                private async void RunLoad() { await Task.Yield(); }
                // async-void: event handler
                private async void OnClick(object sender, RoutedEventArgs e) => await UiGuard.RunAsync("x", Work);
                private async Task Fine() { await Task.Yield(); }
                // A comment about async void is not code.
            }
            """;
        AsyncVoidViolations(source).Should().ContainSingle().Which.Should().Contain("RunLoad");
    }

    [Fact]
    public void The_discard_rule_finds_each_shape_and_leaves_the_rest()
    {
        const string source = """
            class A
            {
                void M()
                {
                    _ = SaveAsync();
                    _ = Model.ExitAsync();
                    _ = _updates?.RefreshInstalledAsync(); // what is installed now
                    ScanCommand = new RelayCommand(() => _ = ScanAsync(), () => true);
                    _ = Task.Run(async () => await WorkAsync());
                    _ = AllowSetForegroundWindow(-1);
                    _updates.CheckNowAsync().Forget("tray: check now");
                    // _ = OldAsync();
                    var x = await LoadAsync();
                }
            }
            """;
        DiscardViolations(source).Should().HaveCount(4)
            .And.Contain(v => v.Contains("SaveAsync"))
            .And.Contain(v => v.Contains("ExitAsync"))
            .And.Contain(v => v.Contains("RefreshInstalledAsync"))
            .And.Contain(v => v.Contains("ScanAsync"));
    }

    [Fact]
    public void The_handler_rule_finds_unguarded_handlers_and_lambdas()
    {
        const string source = """
            public partial class W : Window
            {
                public W()
                {
                    Loaded += (_, _) => Focus();
                    ContentRendered += (_, _) => UiGuard.Run("W: rendered", Select);
                    model.Picked += color => Pick(color);
                    AddHandler(DragDrop.DragLeaveEvent, new DragEventHandler((_, _) => Clear()), handledEventsToo: true);
                    AddHandler(PreviewMouseUpEvent, new MouseButtonEventHandler((_, _) => UiGuard.Run("W: up", Clear)), true);
                    PreviewKeyDown += OnKey;
                }

                private void OnKey(object sender, KeyEventArgs e)
                {
                    if (e.Key == Key.Escape) Close();
                }

                private void OnDrop(object sender, DragEventArgs e) => UiGuard.Run("W: drop", () => Drop(e));

                protected override void OnKeyDown(KeyEventArgs e)
                {
                    base.OnKeyDown(e);
                    UiGuard.Run("W: Escape", () => Close());
                }

                protected override void OnSourceInitialized(EventArgs e)
                {
                    base.OnSourceInitialized(e);
                    WindowPlacement.CapToWorkArea(this);
                }

                private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;

                private void Helper() => Close();
            }
            """;
        UnguardedHandlers(source).Should().HaveCount(6)
            .And.Contain(v => v.Contains("Focus()"))
            .And.Contain(v => v.Contains("Pick(color)"))
            .And.Contain(v => v.Contains("Clear()"))
            .And.Contain(v => v.Contains("OnKey("))
            .And.Contain(v => v.Contains("OnSourceInitialized"))
            .And.Contain(v => v.Contains("OnConfirm"));
    }

    // ---- the scans ----------------------------------------------------------------------------

    /// <summary>"line N: …" for each async void not marked with <see cref="AsyncVoidAllowed"/>.</summary>
    private static IEnumerable<string> AsyncVoidViolations(string source)
    {
        var lines = source.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var code = CodeOf(lines[i]);
            if (!AsyncVoid.IsMatch(code)) continue;
            var allowed = lines[i].Contains(AsyncVoidAllowed, StringComparison.Ordinal)
                          || (i > 0 && lines[i - 1].Contains(AsyncVoidAllowed, StringComparison.Ordinal));
            if (!allowed) yield return $"line {i + 1}: {code.Trim()}";
        }
    }

    /// <summary>"line N: …" for each `_ = …Async(` outside a comment.</summary>
    private static IEnumerable<string> DiscardViolations(string source)
    {
        var lines = source.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var code = CodeOf(lines[i]);
            if (DiscardedAsync.IsMatch(code)) yield return $"line {i + 1}: {code.Trim()}";
        }
    }

    /// <summary>"line N: …" for each handler method (an On… taking EventArgs) or subscribed lambda not going through UiGuard.Run.</summary>
    private static IEnumerable<string> UnguardedHandlers(string source)
    {
        var code = string.Join('\n', source.Split('\n').Select(CodeOf));
        foreach (Match m in HandlerMethod.Matches(code))
        {
            var body = BodyAfter(code, m.Index + m.Length);
            if (!body.Contains("UiGuard.Run(", StringComparison.Ordinal))
                yield return $"line {LineOf(code, m.Index)}: {m.Groups["name"].Value}({m.Groups["params"].Value}) does not run through UiGuard.Run";
        }
        foreach (Match m in SubscribedLambda.Matches(code))
        {
            var rest = code[(m.Index + m.Length)..];
            if (rest.StartsWith("UiGuard.Run(", StringComparison.Ordinal)) continue;
            var shown = rest.Split('\n')[0].Trim();
            yield return $"line {LineOf(code, m.Index)}: {m.Value.Trim()} {shown} does not run through UiGuard.Run";
        }
    }

    /// <summary>A method's body after its parameter list: an expression body to its `;`, or a block to its closing brace.</summary>
    private static string BodyAfter(string code, int at)
    {
        var i = at;
        while (i < code.Length && char.IsWhiteSpace(code[i])) i++;
        if (i + 1 < code.Length && code[i] == '=' && code[i + 1] == '>')
        {
            var depth = 0;
            for (var j = i; j < code.Length; j++)
            {
                switch (code[j])
                {
                    case '(' or '{' or '[': depth++; break;
                    case ')' or '}' or ']': depth--; break;
                    case ';' when depth == 0: return code[i..j];
                }
            }
            return code[i..];
        }
        if (i < code.Length && code[i] == '{')
        {
            var depth = 0;
            for (var j = i; j < code.Length; j++)
            {
                if (code[j] == '{') depth++;
                else if (code[j] == '}' && --depth == 0) return code[i..(j + 1)];
            }
        }
        return "";
    }

    /// <summary>The line without its // comment (a // inside a string literal cuts it short; the scans only look for code).</summary>
    private static string CodeOf(string line)
    {
        var inString = false;
        for (var i = 0; i + 1 < line.Length; i++)
        {
            if (line[i] == '"' && (i == 0 || line[i - 1] != '\\')) inString = !inString;
            if (!inString && line[i] == '/' && line[i + 1] == '/') return line[..i];
        }
        return line;
    }

    private static int LineOf(string code, int index) => code.AsSpan(0, index).Count('\n') + 1;

    private static string Read(string path) => File.ReadAllText(path).Replace("\r\n", "\n");

    private static string Where(string file, string violation) =>
        $"{Path.GetRelativePath(RepoRoot(), file).Replace('\\', '/')}:{violation}";

    private static List<string> ProductFiles()
    {
        var src = Path.Combine(RepoRoot(), "src");
        var files = ProductProjects
            .SelectMany(p => Directory.EnumerateFiles(Path.Combine(src, p), "*.cs", SearchOption.AllDirectories))
            .Where(f => !IsBuildOutput(src, f))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        files.Should().Contain(f => f.EndsWith(Path.Combine("RST.Tray", "TrayHost.cs"), StringComparison.Ordinal), "the scan must reach the tray");
        return files;
    }

    private static List<string> CodeBehindFiles()
    {
        var src = Path.Combine(RepoRoot(), "src");
        return Directory.EnumerateFiles(Path.Combine(src, "RST.UI"), "*.xaml.cs", SearchOption.AllDirectories)
            .Where(f => !IsBuildOutput(src, f))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsBuildOutput(string src, string file)
    {
        var parts = Path.GetRelativePath(src, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Contains("obj") || parts.Contains("bin");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RST-C.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("RST-C.sln not found above " + AppContext.BaseDirectory);
    }
}
