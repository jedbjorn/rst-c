// rst-c's own: the in-house logger's message templates and line format (decision #33). The expected
// strings are what Serilog's file sink wrote for {Message:lj}: strings bare, numbers and dates in the
// invariant culture, sequences and destructured objects as JSON.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using FluentAssertions;
using RST.Core.Logging;
using Xunit;

namespace RST.Tests.Logging;

public sealed class MessageTemplateTests
{
    private static string R(string template, params object?[] values) => MessageTemplate.Render(template, values);

    [Fact]
    public void Named_properties_bind_in_order_and_strings_are_bare()
    {
        R("Loader.load_profile OK: name={Name} hiddenTabs={Hidden}", "Documentation", 5)
            .Should().Be("Loader.load_profile OK: name=Documentation hiddenTabs=5");
    }

    [Fact]
    public void A_format_goes_to_IFormattable_in_the_invariant_culture()
    {
        var due = new DateTimeOffset(2026, 10, 30, 14, 5, 6, TimeSpan.Zero);
        R("first update check slot at {Due:u}", due).Should().Be("first update check slot at 2026-10-30 14:05:06Z");
        R("hr=0x{Hr:X8}", unchecked((int)0x8007000E)).Should().Be("hr=0x8007000E");
        R("{Ms:0.0}ms", 12.345).Should().Be("12.3ms");
    }

    [Fact]
    public void Numbers_and_dates_ignore_the_current_culture()
    {
        var previous = Thread.CurrentThread.CurrentCulture;
        Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            R("{A} {B}", 1.5, 1234567.25m).Should().Be("1.5 1234567.25");
            R("{When}", new DateTime(2026, 10, 30, 14, 0, 0)).Should().Be("10/30/2026 14:00:00");
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Scalars_render_as_their_text()
    {
        R("{B} {E} {G} {N}", true, DayOfWeek.Friday, Guid.Empty, null)
            .Should().Be("True Friday 00000000-0000-0000-0000-000000000000 null");
        R("{C}", 'x').Should().Be("x");
    }

    [Fact]
    public void Alignment_pads_right_or_left()
    {
        R("[{A,5}] [{B,-5}] [{C,6:0.0}]", "ab", "cd", 1.24).Should().Be("[   ab] [cd   ] [   1.2]");
    }

    [Fact]
    public void Sequences_and_dictionaries_render_as_json()
    {
        R("tabs={Tabs}", new List<string> { "Docs", "Arch" }).Should().Be("tabs=[\"Docs\",\"Arch\"]");
        R("{Ids}", new[] { 1, 2, 3 }).Should().Be("[1,2,3]");
        R("{Map}", new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 }).Should().Be("{\"a\":1,\"b\":2}");
        R("{Nested}", (object)new object?[] { "a", new[] { 1 }, null, true }).Should().Be("[\"a\",[1],null,true]");
    }

    [Fact]
    public void Destructured_objects_render_as_json_and_scalars_as_text()
    {
        R("{@Slot}", new { Id = "s1", Size = 32, Ok = true }).Should().Be("{\"Id\":\"s1\",\"Size\":32,\"Ok\":true}");
        R("{@Count}", 5).Should().Be("5");
        R("{@Name}", "plain").Should().Be("plain");
    }

    [Fact]
    public void A_non_destructured_object_renders_its_ToString()
    {
        R("{V}", new Version(4, 2, 0, 0)).Should().Be("4.2.0.0");
        R("{O}", new Uri("https://example.com/a")).Should().Be("https://example.com/a");
        R("{X}", new Opaque()).Should().Be("opaque!");
        R("{$X}", new { A = 1 }).Should().Be("{ A = 1 }");
    }

    [Fact]
    public void Positional_tokens_bind_by_index()
    {
        R("{1} then {0}", "a", "b").Should().Be("b then a");
    }

    [Fact]
    public void A_positional_index_too_large_for_an_int_is_an_unmatched_token_and_the_rest_still_renders()
    {
        R("{0} and {99999999999}", "a").Should().Be("a and {99999999999}");
    }

    [Fact]
    public void Escaped_braces_are_literal()
    {
        R("{{literal}} {A} }} {{", 1).Should().Be("{literal} 1 } {");
    }

    [Fact]
    public void A_token_without_an_argument_stays_as_written()
    {
        R("{A} {B}", 1).Should().Be("1 {B}");
        R("{A}").Should().Be("{A}");
    }

    [Fact]
    public void Malformed_tokens_are_literal_text()
    {
        R("{ spaced } {A", 1).Should().Be("{ spaced } {A");
        R("{a-b} {}", 1).Should().Be("{a-b} {}");
        R("open { then {A}", 1).Should().Be("open { then 1");
    }

    [Fact]
    public void Rendering_never_throws()
    {
        var act = () => R("{X} {@Y} {Z:bad-format}", new Throwing(), new Throwing(), 1);
        act.Should().NotThrow();
        R("{X}", new Throwing()).Should().Be("?");
        MessageTemplate.Render(null, null).Should().BeEmpty();
    }

    private sealed class Opaque
    {
        public override string ToString() => "opaque!";
    }

    private sealed class Throwing
    {
        public int Boom => throw new InvalidOperationException("getter");

        public override string ToString() => throw new InvalidOperationException("ToString");
    }
}

public sealed class OutputTemplateTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 30, 14, 0, 0, 123, TimeSpan.FromHours(1));

    [Fact]
    public void The_engine_line_is_time_level_message()
    {
        var t = new OutputTemplate("{Timestamp:HH:mm:ss.fff} {Level:u3} {Message:lj}{NewLine}{Exception}");
        var e = new LogEvent(At, LogLevel.Information, "Builder.save_profile OK: name={Name}", new object?[] { "VM Test" }, null);

        t.Render(e).Should().Be("14:00:00.123 INF Builder.save_profile OK: name=VM Test" + Environment.NewLine);
    }

    [Fact]
    public void An_exception_follows_on_the_next_lines()
    {
        var t = new OutputTemplate("{Timestamp:HH:mm:ss.fff} {Level:u3} {Message:lj}{NewLine}{Exception}");
        Exception ex;
        try { throw new InvalidOperationException("boom"); }
        catch (Exception caught) { ex = caught; }
        var e = new LogEvent(At, LogLevel.Error, "failed", Array.Empty<object?>(), ex);

        t.Render(e).Should().Be("14:00:00.123 ERR failed" + Environment.NewLine + ex + Environment.NewLine);
    }

    [Fact]
    public void The_tray_line_carries_the_date_and_the_process_id_property()
    {
        var t = new OutputTemplate("{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3} [{ProcessId}] {Message:lj}{NewLine}{Exception}");
        var e = new LogEvent(At, LogLevel.Warning, "{Message}", new object?[] { "tray icon: retrying" }, null);

        t.Render(e, new Dictionary<string, object?> { ["ProcessId"] = 4242, ["Component"] = "RST.Tray" })
            .Should().Be("2026-10-30 14:00:00.123 WRN [4242] tray icon: retrying" + Environment.NewLine);
    }

    [Theory]
    [InlineData(LogLevel.Verbose, "VRB", "vrb", "Verbose")]
    [InlineData(LogLevel.Debug, "DBG", "dbg", "Debug")]
    [InlineData(LogLevel.Information, "INF", "inf", "Information")]
    [InlineData(LogLevel.Warning, "WRN", "wrn", "Warning")]
    [InlineData(LogLevel.Error, "ERR", "err", "Error")]
    [InlineData(LogLevel.Fatal, "FTL", "ftl", "Fatal")]
    public void Level_names_are_serilogs(LogLevel level, string u3, string w3, string full)
    {
        OutputTemplate.LevelText(level, "u3").Should().Be(u3);
        OutputTemplate.LevelText(level, "w3").Should().Be(w3);
        OutputTemplate.LevelText(level, null).Should().Be(full);
    }

    [Fact]
    public void An_unknown_property_renders_nothing()
    {
        var t = new OutputTemplate("[{Missing}] {Message}");
        t.Render(new LogEvent(At, LogLevel.Debug, "x", Array.Empty<object?>(), null)).Should().Be("[] x");
    }
}
