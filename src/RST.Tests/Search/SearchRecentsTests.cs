// rst-c's own (not vendored): Search-It's recently used tools (SearchRecents): newest first, one per tool, at most
// five; the file's text read back, and an empty or broken file read as no recents; only the tools the window loaded
// for the open file, and can run there, shown. The store: a missing or broken file is no recents, a run is recorded
// in front, and a write that fails returns false without throwing.

using FluentAssertions;
using RST.Core.Search;
using Xunit;

namespace RST.Tests.Search;

public sealed class SearchRecentsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rst-recents-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static SearchRecent R(string id) => new(id, "Name of " + id);

    private static IEnumerable<string> Ids(IEnumerable<SearchRecent> recents) => recents.Select(r => r.Id);

    private static SearchTool Tool(string id, SearchToolMark mark = SearchToolMark.None) =>
        new(id, "Tool " + id, "Tab", "Panel")
        {
            Mark = mark,
            MustRunIn = mark == SearchToolMark.MustRunIn ? ["Elevation"] : Array.Empty<string>(),
        };

    // ── the list ─────────────────────────────────────────────────────────

    [Fact]
    public void A_run_goes_in_front_newest_first()
    {
        IReadOnlyList<SearchRecent> recents = [];
        recents = SearchRecents.Push(recents, R("A"));
        recents = SearchRecents.Push(recents, R("B"));
        recents = SearchRecents.Push(recents, R("C"));
        Ids(recents).Should().Equal("C", "B", "A");
    }

    [Fact]
    public void Running_a_tool_again_moves_it_to_the_front_and_keeps_it_once()
    {
        var recents = SearchRecents.Push([R("C"), R("B"), R("A")], R("A"));
        Ids(recents).Should().Equal("A", "C", "B");
        SearchRecents.Push(recents, R("A")).Should().HaveCount(3);
    }

    [Fact]
    public void At_most_five_are_kept_and_the_oldest_goes()
    {
        IReadOnlyList<SearchRecent> recents = [];
        foreach (var id in new[] { "1", "2", "3", "4", "5", "6", "7" }) recents = SearchRecents.Push(recents, R(id));
        SearchRecents.Max.Should().Be(5);
        Ids(recents).Should().Equal("7", "6", "5", "4", "3");
    }

    [Fact]
    public void A_blank_id_is_never_kept()
    {
        SearchRecents.Push([R("A")], new SearchRecent(" ", "Blank")).Select(r => r.Id).Should().Equal("A");
        SearchRecents.Push(null, R("A")).Select(r => r.Id).Should().Equal("A");
    }

    // ── the file's text ──────────────────────────────────────────────────

    [Fact]
    public void The_text_reads_back_as_written()
    {
        var recents = SearchRecents.Push([R("ID_OBJECTS_WALL"), R("CustomCtrl_%CustomCtrl_%Add-Ins%Kinship%Open")], R("ID_OBJECTS_DOOR"));
        SearchRecents.TryParse(SearchRecents.Serialize(recents), out var back).Should().BeTrue();
        back.Should().Equal(recents);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"recent\": null}")]
    [InlineData("{\"recent\": 5}")]
    [InlineData("{\"recent\": [{\"id\": 42}]}")]
    public void Empty_or_broken_text_is_no_recents(string? text)
    {
        SearchRecents.TryParse(text, out var recents).Should().BeFalse();
        recents.Should().BeEmpty();
    }

    [Fact]
    public void Entries_without_an_id_are_skipped_repeats_dropped_and_the_list_cut_to_five()
    {
        const string text = """
            {"recent": [
              {"id": "A", "name": "Align"}, null, {"name": "no id"}, {"id": ""}, {"id": "A", "name": "again"},
              {"id": "B"}, {"id": "C"}, {"id": "D"}, {"id": "E"}, {"id": "F"}
            ], "later": true}
            """;
        SearchRecents.TryParse(text, out var recents).Should().BeTrue();
        Ids(recents).Should().Equal("A", "B", "C", "D", "E");
        recents[0].Name.Should().Be("Align");
        recents[1].Name.Should().Be("", "a missing name is empty, never null");
    }

    // ── what shows ───────────────────────────────────────────────────────

    [Fact]
    public void Only_the_loaded_tools_show_in_the_stored_order()
    {
        var tools = new[] { Tool("A"), Tool("B"), Tool("C") };
        var shown = SearchRecents.Resolve([R("C"), R("GONE"), R("A")], tools);
        shown.Select(t => t.Id).Should().Equal("C", "A");
        shown[0].Should().BeSameAs(tools[2], "the chip runs the tool the window loaded");
    }

    [Fact]
    public void A_must_run_in_tool_is_hidden_and_a_not_in_gui_one_shows()
    {
        var tools = new[] { Tool("A", SearchToolMark.MustRunIn), Tool("B", SearchToolMark.NotInGui), Tool("C") };
        SearchRecents.Resolve([R("A"), R("B"), R("C")], tools).Select(t => t.Id).Should().Equal("B", "C");
    }

    [Fact]
    public void Nothing_shows_without_recents_or_tools()
    {
        SearchRecents.Resolve(null, [Tool("A")]).Should().BeEmpty();
        SearchRecents.Resolve([], [Tool("A")]).Should().BeEmpty();
        SearchRecents.Resolve([R("A")], null).Should().BeEmpty();
        SearchRecents.Resolve([R("A")], []).Should().BeEmpty();
    }

    [Fact]
    public void Ids_match_exactly()
    {
        SearchRecents.Resolve([R("id_align")], [Tool("ID_ALIGN")]).Should().BeEmpty();
    }

    // ── the store ────────────────────────────────────────────────────────

    [Fact]
    public void A_missing_file_or_folder_is_no_recents()
    {
        SearchRecentsStore.Read(Path.Combine(_dir, "nowhere", "recents.json")).Should().BeEmpty();
    }

    [Fact]
    public void A_broken_file_is_no_recents_and_the_next_run_rewrites_it()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "recents.json");
        File.WriteAllText(path, "{ torn");
        SearchRecentsStore.Read(path).Should().BeEmpty();

        SearchRecentsStore.Record(Tool("A"), path).Should().BeTrue();
        Ids(SearchRecentsStore.Read(path)).Should().Equal("A");
    }

    [Fact]
    public void A_run_is_recorded_in_front_with_its_name_and_survives_a_new_read()
    {
        var path = Path.Combine(_dir, "sub", "recents.json"); // the folder is made on the first write
        SearchRecentsStore.Record(Tool("A"), path).Should().BeTrue();
        SearchRecentsStore.Record(Tool("B"), path).Should().BeTrue();
        SearchRecentsStore.Record(Tool("A"), path).Should().BeTrue();

        var read = SearchRecentsStore.Read(path);
        Ids(read).Should().Equal("A", "B");
        read[0].Name.Should().Be("Tool A");
        Directory.GetFiles(Path.Combine(_dir, "sub")).Should().ContainSingle("no temp file is left behind");
    }

    [Fact]
    public void A_write_that_fails_returns_false_and_does_not_throw()
    {
        Directory.CreateDirectory(_dir);
        // The path is a folder: the move onto it fails.
        var path = Path.Combine(_dir, "taken");
        Directory.CreateDirectory(path);
        var act = () => SearchRecentsStore.Record(Tool("A"), path);
        act.Should().NotThrow();
        SearchRecentsStore.Record(Tool("A"), path).Should().BeFalse();
        Directory.GetFiles(_dir).Should().BeEmpty("the temp file is removed");
    }

    [Fact]
    public void The_file_lives_with_RSTs_other_per_user_state()
    {
        SearchRecentsStore.DefaultPath.Should().Be(Path.Combine(RST.Core.Configuration.AppDataPaths.Root, "search_it_recents.json"));
    }
}
