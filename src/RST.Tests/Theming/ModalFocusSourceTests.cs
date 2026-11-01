// rst-c's own. SC-046 / #172 review (a): a new raw modal can clear keyboard focus when it
// closes, leaving Escape dead until a click. New dialogs go through FocusReturn.ShowDialog
// (or Around for native routes) and get a FocusReturnTests test. Exceptions below pin both
// the path and count, with a reason; stale exceptions fail too. This is a source tripwire,
// not a C# compiler: keep modal routes explicit rather than hiding them behind reflection.

using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace RST.Tests.Theming;

public class ModalFocusSourceTests
{
    private sealed record Allow(string Path, string Kind, int Count, string Reason);
    private sealed record Site(string Kind, int Line);
    private const string Advice = "route it through FocusReturn.ShowDialog, or add it to the allow-list with a reason and a FocusReturnTests test";

    private static readonly Allow[] Allowed =
    [
        new("src/RST.UI/Dialogs/WindowGuard.cs", "ShowDialog", 1, "Top-level Revit HWND owner; no WPF opener to restore."),
        new("src/RST.UI/Dialogs/FocusReturn.cs", "ShowDialog", 1, "FocusReturn's own method group invokes WPF inside Around."),
        new("src/RST.UI/Dialogs/DialogService.cs", "ShowDialog", 3, "Two native file routes use ShowNative; ShowModal's false branch is the tray opt-out, decision #62."),
        new("src/RST.UI/Dialogs/DialogService.cs", "OpenFileDialog", 1, "ShowNative wraps the native dialog in Around when ReturnsFocus."),
        new("src/RST.UI/Dialogs/DialogService.cs", "SaveFileDialog", 1, "ShowNative wraps the native dialog in Around when ReturnsFocus."),
        new("src/RST.UI/Loader/LoaderDialogs.cs", "ShowDialog", 1, "ShowModal's false branch keeps tray WPF behaviour, decision #62."),
        new("src/RST.Tray/TrayHost.cs", "ShowDialog", 1, "Tray shadow notice keeps WPF behaviour."),
        new("src/RST.Tray/TrayHost.cs", "DialogService without ReturnsFocus", 1, "Tray opt-out, decision #62."),
        new("src/RST.Tray/TrayHost.cs", "LoaderDialogs without ReturnsFocus", 1, "Tray opt-out, decision #62."),
        new("src/RST.Tray/Windows/TrayViewModel.cs", "DialogService without ReturnsFocus", 2, "Tray placeholder page services also keep WPF behaviour, decision #62."),
        new("src/RST.UI/Swatches/ColorPickerDialog.xaml.cs", "NativeColorDialog", 1, "Native More colours dialog returns to its picker HWND through Windows."),
        new("src/RST.UI/Dialogs/NativeColorDialog.cs", "ChooseColorW", 1, "Native colour implementation; Windows owns the modal focus."),
        new("src/RST.Engine/Commands/RstifyCommand.cs", "TaskDialog", 1, "Revit owns TaskDialog focus."),
        new("src/RST.Engine/Scanning/ScannerProbe.cs", "TaskDialog", 1, "Revit owns TaskDialog focus."),
        new("src/RST.Engine/Scanning/CatalogProbe.cs", "TaskDialog", 1, "Revit owns TaskDialog focus."),
        new("src/RST.Engine/Ribbon/SlotInvokeCommand.cs", "TaskDialog", 1, "Revit owns this instance TaskDialog's focus."),
        new("src/RST.UI/Builder/ProfileBuilderLauncher.cs", "WindowGuard", 1, "Top-level Builder launcher, owned by Revit."),
        new("src/RST.UI/Loader/ProfileLoaderLauncher.cs", "WindowGuard", 1, "Top-level Loader launcher, owned by Revit."),
        new("src/RST.UI/Health/HealthLauncher.cs", "WindowGuard", 1, "Top-level Health launcher, owned by Revit."),
    ];

    [Fact]
    public void Every_product_modal_returns_focus_or_has_an_exact_documented_exception()
    {
        var src = Path.Combine(RepoRoot(), "src");
        var files = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(src, f).Split(Path.DirectorySeparatorChar)
                .Any(p => p is "bin" or "obj" || p.Contains("Tests", StringComparison.Ordinal)))
            .ToDictionary(f => Path.GetRelativePath(RepoRoot(), f).Replace('\\', '/'), File.ReadAllText);
        files.Should().ContainKey("src/RST.Tray/TrayHost.cs");
        files.Should().ContainKey("src/RST.UI/Builder/ProfileBuilderLauncher.cs");
        Violations(files, Allowed).Should().BeEmpty(Advice);
        Allowed.Should().OnlyContain(a => !string.IsNullOrWhiteSpace(a.Reason));
        Allowed.Select(a => (a.Path, a.Kind)).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Shared_service_exceptions_still_use_the_opted_in_focus_routes()
    {
        foreach (var path in new[] { "Dialogs/DialogService.cs", "Loader/LoaderDialogs.cs" })
        {
            var code = StripTrivia(File.ReadAllText(Path.Combine(RepoRoot(), "src/RST.UI", path)));
            Regex.IsMatch(code, @"ShowModal\s*\(Window\s+dialog\)\s*=>\s*ReturnsFocus\s*\?\s*FocusReturn\.ShowDialog\(dialog\)\s*:\s*dialog\.ShowDialog\(\)").Should().BeTrue(path);
            if (path.StartsWith("Dialogs/", StringComparison.Ordinal))
            {
                Regex.IsMatch(code, @"ShowNative\(Func<bool\?>\s+show\)\s*=>\s*ReturnsFocus\s*\?\s*FocusReturn\.Around\(Owner,\s*show\)\s*:\s*show\(\)").Should().BeTrue();
                Regex.Matches(code, @"ShowNative\(\(\)\s*=>\s*dialog\.ShowDialog\(Owner\)\)").Count.Should().Be(2);
            }
        }
        var focus = StripTrivia(File.ReadAllText(Path.Combine(RepoRoot(), "src/RST.UI/Dialogs/FocusReturn.cs")));
        Regex.IsMatch(focus, @"Around\(dialog\.Owner,\s*dialog\.ShowDialog\)").Should().BeTrue();
    }

    private static IEnumerable<string> Violations(IReadOnlyDictionary<string, string> files, IReadOnlyList<Allow> allowed)
    {
        var sites = files.ToDictionary(f => f.Key, f => Entries(f.Value).ToList());
        foreach (var (path, entries) in sites)
        {
            foreach (var group in entries.GroupBy(s => s.Kind))
            {
                var count = allowed.SingleOrDefault(a => a.Path == path && a.Kind == group.Key)?.Count ?? 0;
                if (group.Count() != count)
                    yield return $"{path}:{group.First().Line}: {group.Key}: found {group.Count()}, expected {count}; {Advice}";
            }
        }
        foreach (var entry in allowed)
        {
            if (!sites.TryGetValue(entry.Path, out var found) || found.All(s => s.Kind != entry.Kind))
                yield return $"{entry.Path}:1: stale {entry.Kind} exception (expected {entry.Count}); {Advice}";
        }
    }

    private static IEnumerable<Site> Entries(string source)
    {
        var code = StripTrivia(source);
        // Resolve simple type aliases without introducing a parser dependency. Newlines stay intact.
        foreach (Match alias in Regex.Matches(code, @"\busing\s+(\w+)\s*=\s*(?:[\w]+\.)*(MessageBox|TaskDialog|OpenFileDialog|SaveFileDialog|NativeColorDialog|FocusReturn|WindowGuard|DialogService|LoaderDialogs)\s*;"))
            code = Regex.Replace(code, @"\b" + Regex.Escape(alias.Groups[1].Value) + @"\b", alias.Groups[2].Value);
        var around = Regex.Matches(code, @"\bFocusReturn\s*\.\s*Around\s*\(")
            .Select(m => (Start: m.Index, End: ClosingDelimiter(code, m.Index + m.Length - 1))).ToList();
        bool Wrapped(int index) => around.Any(a => index > a.Start && index < a.End);
        Site At(string kind, int index) => new(kind, code.AsSpan(0, index).Count('\n') + 1);

        foreach (Match m in Regex.Matches(code, @"(?:(?<owner>\w+)\s*\?*\.\s*)?\bShowDialog\b"))
        {
            if (Wrapped(m.Index) || IsDeclaration(code, m.Index)) continue;
            var owner = m.Groups["owner"].Value;
            if (owner == "FocusReturn" && Regex.IsMatch(code[(m.Index + m.Length)..], @"^\s*\(")) continue;
            yield return At(owner == "WindowGuard" ? "WindowGuard" : "ShowDialog", m.Index);
        }
        foreach (Match m in Regex.Matches(code, @"\b(?<kind>MessageBox|TaskDialog|NativeColorDialog)\s*\.\s*Show\s*\("))
            if (!Wrapped(m.Index)) yield return At(m.Groups["kind"].Value, m.Index);
        // Native file dialogs count their construction and, separately, any raw ShowDialog
        // above. This also covers inline new OpenFileDialog/SaveFileDialog { ... }.ShowDialog(...).
        foreach (Match m in Regex.Matches(code, @"\b(?:new\s+(?:Microsoft\.Win32\.)?(?<kind>OpenFileDialog|SaveFileDialog)\b|(?<kind>OpenFileDialog|SaveFileDialog)\s*\()"))
            if (!Wrapped(m.Index)) yield return At(m.Groups["kind"].Value, m.Index);
        foreach (Match m in Regex.Matches(code, @"\bnew\s+(?:[\w]+\.)*TaskDialog\b"))
            if (!Wrapped(m.Index) && ShowsInline(code, m.Index, m.Index + m.Length))
                yield return At("TaskDialog", m.Index);
        // TaskDialog instance Show, including a typed variable and target-typed new.
        var taskNames = Regex.Matches(code, @"\b(?:TaskDialog\s+(?<name>\w+)\b|var\s+(?<name>\w+)\s*=\s*new\s+(?:[\w]+\.)*TaskDialog\b)")
            .Select(m => m.Groups["name"].Value).Distinct();
        foreach (var name in taskNames)
            foreach (Match m in Regex.Matches(code, @"\b" + Regex.Escape(name) + @"\s*\.\s*Show\s*\("))
                if (!Wrapped(m.Index)) yield return At("TaskDialog", m.Index);
        foreach (Match m in Regex.Matches(code, @"\bChooseColorW\s*\("))
            if (!IsDeclaration(code, m.Index) && !Wrapped(m.Index)) yield return At("ChooseColorW", m.Index);
        foreach (Match m in Regex.Matches(code, @"\b(?:new\s+(?:[\w]+\.)*(?<kind>DialogService|LoaderDialogs)\b|(?<kind>DialogService|LoaderDialogs)\s+\w+\s*=\s*new(?=\s*\())(?:\s*\([^;{}]*\))?\s*(?<init>\{[^{}]*\})?"))
            if (!Regex.IsMatch(m.Groups["init"].Value, @"\bReturnsFocus\s*=\s*true\b"))
                yield return At(m.Groups["kind"].Value + " without ReturnsFocus", m.Index);
    }

    private static bool IsDeclaration(string code, int index)
    {
        var start = code.LastIndexOfAny([';', '{', '}'], Math.Max(0, index - 1)) + 1;
        return Regex.IsMatch(code[start..index], @"\b(?:public|private|internal|protected)\s+(?:(?:static|extern|new|virtual|override)\s+)*[\w?]+\s+$");
    }

    private static bool ShowsInline(string code, int start, int afterType)
    {
        int SkipSpace(int index)
        {
            while (index < code.Length && char.IsWhiteSpace(code[index])) index++;
            return index;
        }
        var at = SkipSpace(afterType);
        if (at < code.Length && code[at] == '(')
            at = SkipSpace(ClosingDelimiter(code, at) + 1);
        if (at < code.Length && code[at] == '{')
            at = SkipSpace(ClosingDelimiter(code, at) + 1);
        // Only consume parentheses that enclose the construction itself.
        var before = start - 1;
        while (at < code.Length && code[at] == ')')
        {
            while (before >= 0 && char.IsWhiteSpace(code[before])) before--;
            if (before < 0 || code[before--] != '(') return false;
            while (before >= 0 && char.IsWhiteSpace(code[before])) before--;
            if (before >= 0 && code[before] is '>' or ']' or ')') return false;
            var token = Regex.Match(code[..(before + 1)], @"@?\w+$");
            // A return expression can be grouped; an identifier starts a call's arguments.
            if (token.Success && token.Value != "return") return false;
            at = SkipSpace(at + 1);
        }
        return at < code.Length && Regex.IsMatch(code[at..], @"^\??\.\s*Show\s*\(");
    }

    // Trivia is already blanked, so nested constructor arguments, initializers and
    // lambda bodies can be skipped without a regex trying to balance delimiters.
    private static int ClosingDelimiter(string code, int open)
    {
        var opening = code[open];
        var closing = opening == '(' ? ')' : '}';
        var depth = 0;
        for (var i = open; i < code.Length; i++)
        {
            if (code[i] == opening) depth++;
            if (code[i] == closing && --depth == 0) return i;
        }
        return code.Length;
    }

    // SC-046 review M2: blank literal text, but scan interpolation holes as code (including
    // nested literals). Offsets/newlines stay intact; raw strings use one brace per dollar.
    internal static string StripTrivia(string source)
    {
        var chars = source.ToCharArray();
        void Blank(int from, int to)
        {
            for (var j = from; j < to; j++) if (chars[j] is not '\r' and not '\n') chars[j] = ' ';
        }
        int Run(int at, char c)
        {
            var end = at;
            while (end < source.Length && source[end] == c) end++;
            return end - at;
        }
        void Code(ref int i, int closingBraces = 0)
        {
            var braces = 0;
            var parens = 0;
            while (i < source.Length)
            {
                if (closingBraces > 0 && braces == 0 && Run(i, '}') >= closingBraces) return;
                var start = i;
                if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '/')
                {
                    while (i < source.Length && source[i] != '\n') i++;
                    Blank(start, i);
                }
                else if (source[i] == '/' && i + 1 < source.Length && source[i + 1] == '*')
                {
                    var end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = end < 0 ? source.Length : end + 2;
                    Blank(start, i);
                }
                else if (Literal(ref i)) { }
                else if (closingBraces > 0 && braces == 0 && parens == 0 && source[i] == ':' &&
                         (i == 0 || source[i - 1] != ':') && (i + 1 == source.Length || source[i + 1] != ':'))
                {
                    // A format suffix is literal text, not an expression. Conditional expressions
                    // in interpolation holes must be parenthesized; alias qualifiers use ::.
                    while (i < source.Length && Run(i, '}') < closingBraces) i++;
                    Blank(start, i);
                }
                else
                {
                    if (source[i] == '{') braces++;
                    if (source[i] == '}') braces--;
                    if (source[i] is '(' or '[') parens++;
                    if (source[i] is ')' or ']') parens--;
                    i++;
                }
            }
        }
        bool Literal(ref int i)
        {
            var start = i;
            var quoteAt = i;
            var dollars = 0;
            var verbatim = false;
            while (quoteAt < source.Length && source[quoteAt] is '$' or '@')
            {
                if (source[quoteAt++] == '$') dollars++;
                else verbatim = true;
            }
            if (quoteAt >= source.Length || (source[quoteAt] != '"' &&
                (source[quoteAt] != '\'' || quoteAt != start))) return false;
            var quote = source[quoteAt];
            var quotes = quote == '"' ? Run(quoteAt, '"') : 1;
            var raw = quotes >= 3;
            if (!raw) quotes = 1;
            var holeBraces = raw ? dollars : Math.Min(dollars, 1);
            i = quoteAt + quotes;
            Blank(start, i);
            while (i < source.Length)
            {
                start = i;
                if (Run(i, quote) >= quotes)
                {
                    i += quotes;
                    if (!raw && verbatim && i < source.Length && source[i] == quote) i++;
                    else { Blank(start, i); return true; }
                }
                else if (!raw && !verbatim && source[i] == '\\') i = Math.Min(source.Length, i + 2);
                else if (holeBraces > 0 && source[i] == '{')
                {
                    var braces = Run(i, '{');
                    if (!raw && braces >= 2) i += 2; // Escaped {{ in ordinary/verbatim text.
                    else if (braces >= holeBraces)
                    {
                        i += braces; // Extra leading raw braces are literal text.
                        Blank(start, i);
                        Code(ref i, holeBraces);
                        start = i;
                        i = Math.Min(source.Length, i + holeBraces);
                    }
                    else i += braces;
                }
                else i++;
                Blank(start, i);
            }
            return true;
        }
        var at = 0;
        Code(ref at);
        return new string(chars);
    }

    [Theory]
    [InlineData("var s = \"ShowDialog()\";", true)]
    [InlineData("var s = $\"text ShowDialog() {x}\";", true)]
    [InlineData("var s = $\"Result: {dialog.ShowDialog()}\";", false)]
    [InlineData("var s = $@\"Result: {dialog.ShowDialog()}\";", false)]
    [InlineData("var s = @$\"Result: {dialog.ShowDialog()}\";", false)]
    [InlineData("var s = $\"\"\"Result: {dialog.ShowDialog()}\"\"\";", false)]
    [InlineData("var s = $$\"\"\"Result: {{dialog.ShowDialog()}}\"\"\";", false)]
    [InlineData("var s = $$\"\"\"Result: {{{dialog.ShowDialog()}}}\"\"\";", false)]
    [InlineData("var s = $$\"\"\"text {dialog.ShowDialog()} {{x}}\"\"\";", true)]
    [InlineData("var s = $$$\"\"\"text {{dialog.ShowDialog()}} {{{x}}}\"\"\";", true)]
    [InlineData("var s = $\"text {{dialog.ShowDialog()}} {x}\";", true)]
    [InlineData("var s = $@\"text {{dialog.ShowDialog()}} {x}\";", true)]
    [InlineData("var s = $\"Result: {Get(\"ShowDialog()\")}\";", true)]
    [InlineData("var s = $\"Result: {Get(@\"ShowDialog()\", dialog.ShowDialog())}\";", false)]
    [InlineData("var s = $\"Result: {Get($\"nested {dialog.ShowDialog()}\")}\";", false)]
    [InlineData("var s = $$\"\"\"Result: {{Get(\"\"\"ShowDialog()\"\"\")}}\"\"\";", true)]
    [InlineData("var s = $$\"\"\"Result: {{Get(new[] { \"ShowDialog()\", $\"{dialog.ShowDialog()}\" })}}\"\"\";", false)]
    [InlineData("var s = $\"Result: {x:ShowDialog()}\";", true)]
    [InlineData("var s = $\"Result: {dialog.ShowDialog():value}\";", false)]
    [InlineData("var s = $\"Result: {(x ? dialog.ShowDialog() : null)}\";", false)]
    [InlineData("var s = $\"Result: {global::Dialog.ShowDialog()}\";", false)]
    [InlineData("var s = $\"Result: {x /* ShowDialog() */}\";", true)]
    [InlineData("x.ShowDialog();", false)]
    [InlineData("Run(x.ShowDialog);", false)]
    [InlineData("ShowDialog();", false)]
    [InlineData("// x.ShowDialog()\nvar s = \"MessageBox.Show()\";", true)]
    [InlineData("var s = @\"x.ShowDialog(\"\"text\"\")\"; var c = '\"';", true)]
    [InlineData("var s = \"\"\"x.ShowDialog()\"\"\"; /* MessageBox.Show() */", true)]
    [InlineData("FocusReturn.ShowDialog(x);", true)]
    [InlineData("FocusReturn.Around(owner, () => x.ShowDialog());", true)]
    [InlineData("FocusReturn.ShowDialog(x); y.ShowDialog();", false)]
    [InlineData("MessageBox.Show(\"x\");", false)]
    [InlineData("using M = System.Windows.MessageBox; M.Show(\"x\");", false)]
    [InlineData("new OpenFileDialog();", false)]
    [InlineData("var dlg = new TaskDialog(\"x\"); dlg.Show();", false)]
    [InlineData("TaskDialog dlg = CreateReport(); dlg.Show();", false)]
    [InlineData("new DialogService();", false)]
    [InlineData("new RST.UI.Dialogs.DialogService();", false)]
    [InlineData("DialogService dialogs = new();", false)]
    [InlineData("LoaderDialogs dialogs = new() { ReturnsFocus = true };", true)]
    [InlineData("var dlg = new Autodesk.Revit.UI.TaskDialog(\"x\"); dlg.Show();", false)]
    [InlineData("new LoaderDialogs { ReturnsFocus = true };", true)]
    [InlineData("WindowGuard.ShowDialog(x, \"x\");", false)]
    public void Modal_entry_self_tests(string source, bool passes) =>
        Violations(new Dictionary<string, string> { ["fixture.cs"] = source }, []).Any().Should().Be(!passes);

    [Theory]
    [InlineData("new TaskDialog(\"x\") { A = 1, B = () => { } }.Show();", "TaskDialog")]
    [InlineData("new Autodesk.Revit.UI.TaskDialog(\"x\").Show();", "TaskDialog")]
    [InlineData("(new TaskDialog(\"x\")).Show();", "TaskDialog")]
    [InlineData("x = (new TaskDialog(\"x\")).Show();", "TaskDialog")]
    [InlineData("return (new TaskDialog(\"x\")).Show();", "TaskDialog")]
    [InlineData("new TaskDialog(\"x\")?.Show();", "TaskDialog")]
    [InlineData("(new TaskDialog(\"x\"))?.Show();", "TaskDialog")]
    [InlineData("((new TaskDialog(GetTitle(\"x\")) { A = new[] { 1, 2 }, B = () => { if (ok) { Run(); } }, Text = \"} )\" })).Show();", "TaskDialog")]
    [InlineData("new TaskDialog(\n\"x\"\n)\n{\nA = 1,\nB = () => { }\n}\n.Show();", "TaskDialog")]
    [InlineData("var d = new TaskDialog(\"x\") { A = 1 }; d.Show();", "TaskDialog")]
    [InlineData("TaskDialog d = new(\"x\"); d.Show();", "TaskDialog")]
    [InlineData("TaskDialog.Show(\"x\");", "TaskDialog")]
    [InlineData("new SaveFileDialog { Filter = \"x\", B = () => { } }.ShowDialog(owner);", "ShowDialog,SaveFileDialog")]
    [InlineData("(new Microsoft.Win32.SaveFileDialog() { Filter = \"x\" }).ShowDialog(owner);", "ShowDialog,SaveFileDialog")]
    [InlineData("new Microsoft.Win32.OpenFileDialog { Filter = \"x\" }.ShowDialog(owner);", "ShowDialog,OpenFileDialog")]
    [InlineData("var d = new OpenFileDialog(); d.ShowDialog(owner);", "ShowDialog,OpenFileDialog")]
    [InlineData("new MyWindow { Owner = o }.ShowDialog();", "ShowDialog")]
    [InlineData("(new MyWindow(GetTitle()) { Owner = o }).ShowDialog();", "ShowDialog")]
    [InlineData("// new TaskDialog(\"x\").Show();\n/* (new TaskDialog(\"x\")).Show(); */\nvar s = \"new TaskDialog(x) { A = 1 }.Show();\";", "")]
    [InlineData("var d = new TaskDialog(\"x\") { A = 1 }; other.Show();", "")]
    [InlineData("Factory(new TaskDialog(\"x\")).Show();", "")]
    [InlineData("Factory((new TaskDialog(\"x\"))).Show();", "")]
    [InlineData("FocusReturn.Around(o, () => new TaskDialog(\"x\")?.Show())", "")]
    [InlineData("FocusReturn.Around(owner, () => new TaskDialog(\"x\") { A = 1 }.Show());", "")]
    [InlineData("FocusReturn.Around(owner, () => new SaveFileDialog { Filter = \"x\" }.ShowDialog(owner));", "")]
    public void Inline_and_named_modal_entries_have_exact_counts(string source, string kinds) =>
        Entries(source).Select(s => s.Kind).Should().Equal(kinds.Split(',', StringSplitOptions.RemoveEmptyEntries));

    [Theory]
    [InlineData("TaskDialog.Show(\"RSTify\", \"No tabs configured to hide.\");")]
    [InlineData("""
        new TaskDialog(UiStrings.Common_RstTitle)
        {
            TitleAutoPrefix = false,
            MainInstruction = UiStrings.Rstify_NoTabs,
            MainContent = UiStrings.Rstify_ConfigureTabs,
            CommonButtons = TaskDialogCommonButtons.Close,
        }.Show();
        """)]
    public void Rstify_exception_accepts_both_static_and_inline_forms(string source)
    {
        var exception = Allowed.Single(a => a.Path == "src/RST.Engine/Commands/RstifyCommand.cs");
        Violations(new Dictionary<string, string> { [exception.Path] = source }, [exception]).Should().BeEmpty();
    }

    [Theory]
    [InlineData("x.ShowDialog();", true)]
    [InlineData("x.ShowDialog(); y.ShowDialog();", false)]
    [InlineData("", false)]
    public void An_exception_pins_the_count_including_stale_entries(string source, bool passes) =>
        Violations(new Dictionary<string, string> { ["fixture.cs"] = source },
            [new("fixture.cs", "ShowDialog", 1, "Test exception")]).Any().Should().Be(!passes);

    [Theory]
    [InlineData("DialogService", "new DialogService")]
    [InlineData("DialogService", "new DialogService()")]
    [InlineData("DialogService", "new()")]
    [InlineData("LoaderDialogs", "new LoaderDialogs")]
    [InlineData("LoaderDialogs", "new LoaderDialogs()")]
    [InlineData("LoaderDialogs", "new()")]
    public void Typed_service_constructions_require_focus_opt_in(string type, string construction)
    {
        Modal_entry_self_tests($"{type} dialogs = {construction} {{ ReturnsFocus = true }};", true);
        Modal_entry_self_tests($"{type} dialogs = {construction} {{ ReturnsFocus = false }};", false);
        Modal_entry_self_tests($"{type} dialogs = {construction} {{ }};", false);
    }

    [Fact]
    public void Interpolation_preserves_source_offsets_and_line_numbers()
    {
        const string source = "var s = $$\"\"\"\nResult: {{dialog.ShowDialog()}}\n\"\"\";";
        var code = StripTrivia(source);
        code.Length.Should().Be(source.Length);
        code.IndexOf("dialog.ShowDialog()", StringComparison.Ordinal)
            .Should().Be(source.IndexOf("dialog.ShowDialog()", StringComparison.Ordinal));
        Entries(source).Should().ContainSingle().Which.Line.Should().Be(2);
    }

    [Fact]
    public void A_deleted_allow_listed_file_fails() =>
        Violations(new Dictionary<string, string>(), [new("gone.cs", "ShowDialog", 1, "Test exception")]).Should().ContainSingle();

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RST-C.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("RST-C.sln not found above " + AppContext.BaseDirectory);
    }
}
