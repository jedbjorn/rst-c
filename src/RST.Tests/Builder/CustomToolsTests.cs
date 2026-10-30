// rst-c's own (not vendored): the Custom Tools list (feature #18, spec #19, decision #20, AC1),
// over a temp profiles folder holding three profiles: a tool shared by two, a single-use one, an
// email tool (one copy typed bare, one with mailto:), and custom tools inside stacks. Derive; edit
// the link, label and icon everywhere (names given on a panel kept); remove everywhere (a stack
// left with one tool becomes a plain tool); one file that cannot be written gives a reported
// partial failure while the others are written. Each claim is checked on the files on disk.

using System.IO;
using FluentAssertions;
using RST.Core.Builder;
using RST.Core.Loader;
using RST.Core.Profiles;
using Xunit;

namespace RST.Tests.Builder;

public sealed class CustomToolsTests : IDisposable
{
    private const string ArchId = "a-arch";
    private const string MepId = "b-mep";
    private const string StructId = "c-struct";
    private const string Wiki = "https://wiki.example.com";
    private const string Support = "mailto:support@example.com";
    private const string StructDocs = "https://struct.example.com";
    private const string Tracker = "https://tracker.example.com";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rst-custom-tools-" + Guid.NewGuid().ToString("N"));
    private readonly RecordingLog _log = new();

    public CustomToolsTests()
    {
        foreach (var p in new[] { Arch(), Mep(), Struct() }) ProfileStore.Save(p, _dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private static Slot Url(string name, string link, string? baseName = null, string? icon = null) =>
        new() { SlotType = "tool", Name = name, BaseName = baseName, CommandId = "URL:" + link, IconFile = icon };

    private static Slot Command(string id) => new() { SlotType = "tool", Name = id, CommandId = id };

    private static Profile Make(string id, string name, Dictionary<string, Stack> stacks, params Slot[] slots) => new()
    {
        Id = id,
        ProfileName = name,
        Tab = name + " tab",
        ExportDate = "2026-01-01",
        Stacks = stacks,
        Panels = [new Panel { Name = "Links", Color = "#4f8ef7", Slots = [.. slots] }],
    };

    // Arch: the wiki (base-named, with an icon), the support email, and a stack holding the wiki
    // under a name given on the panel.
    private static Profile Arch() => Make(ArchId, "Arch",
        new() { ["Stack 1"] = new Stack { Tools = [Url("Our wiki", Wiki, baseName: "Wiki"), Command("ID_WALL")] } },
        Url("Wiki", Wiki, baseName: "Wiki", icon: "pack:book"),
        Url("Support", Support, icon: "pack:mail"),
        PanelStacks.StackSlot("Stack 1"));

    // MEP: the support email typed bare (it normalises to the same link), and the tracker in a stack.
    private static Profile Mep() => Make(MepId, "MEP",
        new() { ["Stack 1"] = new Stack { Tools = [Url("Tracker", Tracker), Command("ID_DOOR")] } },
        Url("Help desk", "support@example.com"),
        PanelStacks.StackSlot("Stack 1"));

    // Struct: the wiki without a base name (once as its label, once renamed on the panel), and a
    // single-use tool.
    private static Profile Struct() => Make(StructId, "Struct", new(),
        Url("Wiki", Wiki),
        Url("Struct docs", StructDocs),
        Url("Team wiki", Wiki),
        Command("ID_BEAM"));

    private string FileOf(string id) => ProfileStore.List(_dir).Single(e => e.Profile.Id == id).FileName;

    private Profile OnDisk(string id) => ProfileStore.List(_dir).Single(e => e.Profile.Id == id).Profile;

    private string Bytes(string id) => File.ReadAllText(Path.Combine(_dir, FileOf(id)));

    private CustomToolEditor Editor() => new(_dir, new ProfileSaveService(_dir, _log), _log);

    private static IEnumerable<Slot> AllTools(Profile p) => p.Panels.SelectMany(x => x.Slots).SelectMany(s =>
        PanelStacks.IsStack(s) ? PanelStacks.ToolsOf(p, s) : [s]);

    /// <summary>Writes <paramref name="profile"/> under a file name of the test's choosing (a legacy or stale copy).</summary>
    private void WriteAs(string fileName, Profile profile)
    {
        using var fs = File.Create(Path.Combine(_dir, fileName));
        ProfileSerializer.Write(profile, fs);
    }

    private string[] Files() => Directory.GetFiles(_dir).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal).ToArray();

    private sealed class RecordingLog : ILoaderLog
    {
        public List<string> Lines { get; } = [];

        public void Debug(Exception? error, string template, params object?[] args) { }

        public void Information(string template, params object?[] args) => Lines.Add("I " + string.Join(" ", args));

        public void Warning(Exception? error, string template, params object?[] args) => Lines.Add("W " + string.Join(" ", args));

        public void Error(Exception? error, string template, params object?[] args) => Lines.Add("E " + string.Join(" ", args));
    }

    // ── derive ──

    [Fact]
    public void The_list_is_derived_from_every_saved_profile_by_normalised_link()
    {
        var tools = Editor().Derive(working: null);
        tools.Select(t => (t.Label, t.Link, t.Kind, string.Join(",", t.UsedIn), t.IconFile)).Should().Equal(
            ("Struct docs", StructDocs, CustomToolKind.Url, "Struct", (string?)null),
            ("Support", Support, CustomToolKind.Email, "Arch,MEP", "pack:mail"),
            ("Tracker", Tracker, CustomToolKind.Url, "MEP", null),
            ("Wiki", Wiki, CustomToolKind.Url, "Arch,Struct", "pack:book"));
    }

    [Fact]
    public void The_open_profile_s_working_copy_replaces_its_file_and_an_unsaved_one_is_added()
    {
        var working = ProfileEditing.ForEditing(OnDisk(ArchId));
        working.Panels[0].Slots.RemoveAt(1); // the support email, removed but not saved
        working.Panels[0].Slots.Add(Url("Intranet", "intranet.example.com"));
        var draft = Make("", "Draft", new(), Url("Wiki (draft)", Wiki));
        draft.Id = null;

        var tools = CustomTools.Derive(ProfileStore.List(_dir).Select(e => e.Profile), working);
        tools.Single(t => t.Link == Support).UsedIn.Should().Equal("MEP");
        tools.Single(t => t.Link == "https://intranet.example.com").UsedIn.Should().Equal("Arch");

        tools = CustomTools.Derive(ProfileStore.List(_dir).Select(e => e.Profile), draft);
        tools.Single(t => t.Link == Wiki).UsedIn.Should().Equal("Arch", "Draft", "Struct");
        tools.Single(t => t.Link == Wiki).Label.Should().Be("Wiki", "the first occurrence by profile name");
    }

    [Fact]
    public void The_label_falls_back_to_the_name_and_the_kind_follows_the_link()
    {
        CustomTools.LabelOf(Url("Name", Wiki)).Should().Be("Name");
        CustomTools.LabelOf(Url("Name", Wiki, baseName: "Base")).Should().Be("Base");
        CustomTools.KindOf("MAILTO:x@y").Should().Be(CustomToolKind.Email);
        CustomTools.KindOf("tel:123").Should().Be(CustomToolKind.Url);
        CustomTools.LinkOf(Command("ID_WALL")).Should().Be("");
        CustomTools.LinkOf(Url("x", " gmail.com ")).Should().Be("https://gmail.com");
    }

    [Fact]
    public void Unreadable_files_are_logged_and_left_out()
    {
        File.WriteAllText(Path.Combine(_dir, "broken.json"), "{ not json");
        Editor().Derive(null).Should().HaveCount(4);
        _log.Lines.Should().Contain(l => l.StartsWith("W ") && l.Contains("broken.json"));
    }

    // ── create ──

    [Fact]
    public void A_link_s_scheme_case_does_not_split_a_tool()
    {
        CustomTools.LinkOf(Url("x", "MAILTO:bob@x.com")).Should().Be("mailto:bob@x.com");
        CustomTools.LinkOf(Url("x", "bob@x.com")).Should().Be("mailto:bob@x.com");
        CustomTools.LinkOf(Url("x", "HTTPS://Wiki.example.com/Page")).Should().Be("https://Wiki.example.com/Page", "only the scheme is folded");
        CustomTools.Opens("MAILTO:Bob@x.com").Should().Be("mailto:Bob@x.com");
        CustomTools.NewSlot(new CustomToolEdit("B", "MAILTO:bob@x.com", null)).CommandId.Should().Be("URL:mailto:bob@x.com");
    }

    [Theory]
    [InlineData("mailto:")]
    [InlineData(" MAILTO: ")]
    [InlineData("https://")]
    public void A_bare_scheme_is_not_an_address(string address) =>
        CustomTools.Check(new CustomToolEdit("X", address, null)).Should().Be("Address required");

    [Fact]
    public void Create_checks_label_and_address_and_adds_to_the_selected_or_first_panel()
    {
        CustomTools.Check(new CustomToolEdit(" ", "x.com", null)).Should().Be("Label required");
        CustomTools.Check(new CustomToolEdit("X", "  ", null)).Should().Be("Address required");
        CustomTools.Check(new CustomToolEdit("X", "x.com", null)).Should().BeNull();
        CustomTools.Opens("help@example.com").Should().Be("mailto:help@example.com");

        var slot = CustomTools.NewSlot(new CustomToolEdit(" Help ", "help@example.com", "pack:mail"));
        slot.Should().BeEquivalentTo(new Slot { SlotType = "tool", Name = "Help", BaseName = "Help", CommandId = "URL:mailto:help@example.com", IconFile = "pack:mail" });

        var p = Arch();
        p.Panels.Add(new Panel { Name = "Second", Color = "#4f8ef7" });
        CustomTools.AddToPanel(p, 1, slot).Should().Be(1);
        CustomTools.AddToPanel(p, null, CustomTools.NewSlot(new CustomToolEdit("Wiki", Wiki, null))).Should().Be(0);
        p.Panels[0].Slots.Should().HaveCount(4, "an existing link is added anyway, as another placement");
        CustomTools.AddToPanel(new Profile(), 0, slot).Should().BeNull("a profile with no panels is refused");
    }

    // ── edit everywhere ──

    [Fact]
    public void Edit_changes_link_label_and_icon_in_every_profile_and_keeps_names_given_on_a_panel()
    {
        var mepBefore = Bytes(MepId);
        var working = ProfileEditing.ForEditing(OnDisk(ArchId));
        var editor = Editor();
        var wiki = editor.Derive(working).Single(t => t.Link == Wiki);
        editor.Affected(Wiki, working).Should().Equal("Arch", "Struct");

        var result = editor.Edit(wiki, new CustomToolEdit("Knowledge", "wiki2.example.com", "pack:bulb"), working, activeProfileId: StructId);

        result.Ok.Should().BeTrue();
        result.Written.Select(w => w.ProfileName).Should().Equal("Arch", "Struct");
        result.WorkingCopyChanged.Should().BeTrue();
        result.ActiveProfileChanged.Should().BeTrue("the loaded profile's file was written");

        const string newId = "URL:https://wiki2.example.com";
        var arch = OnDisk(ArchId);
        arch.Panels[0].Slots[0].Should().BeEquivalentTo(Url("Knowledge", "https://wiki2.example.com", baseName: "Knowledge", icon: "pack:bulb"));
        var stacked = arch.Stacks["Stack 1"].Tools[0];
        (stacked.Name, stacked.BaseName, stacked.CommandId, stacked.IconFile).Should().Be(("Our wiki", "Knowledge", newId, "pack:bulb"));

        var str = OnDisk(StructId).Panels[0].Slots;
        (str[0].Name, str[0].BaseName, str[0].CommandId).Should().Be(("Knowledge", "Knowledge", newId), "its name was its base name");
        (str[2].Name, str[2].BaseName, str[2].CommandId).Should().Be(("Team wiki", "Knowledge", newId), "a name given on the panel is kept");
        str[1].CommandId.Should().Be("URL:" + StructDocs);

        Bytes(MepId).Should().Be(mepBefore, "a profile without the tool is not written");
        AllTools(working).Count(s => s.CommandId == newId).Should().Be(2, "the working copy gets the same change");
        Editor().Derive(null).Select(t => t.Link).Should().Contain("https://wiki2.example.com").And.NotContain(Wiki);
    }

    [Fact]
    public void Edit_reports_the_active_profile_unchanged_when_its_file_did_not_hold_the_tool()
    {
        var editor = Editor();
        var docs = editor.Derive(null).Single(t => t.Link == StructDocs);
        var result = editor.Edit(docs, new CustomToolEdit("Docs", StructDocs, null), working: null, activeProfileId: ArchId);
        result.Written.Select(w => w.ProfileId).Should().Equal(StructId);
        result.ActiveProfileChanged.Should().BeFalse();
        result.WorkingCopyChanged.Should().BeFalse();
        OnDisk(StructId).Panels[0].Slots[1].Name.Should().Be("Docs");
    }

    [Fact]
    public void A_link_edited_into_an_existing_one_merges_the_two_entries()
    {
        var editor = Editor();
        var docs = editor.Derive(null).Single(t => t.Link == StructDocs);
        editor.Edit(docs, new CustomToolEdit("Wiki", Wiki, null), null, null).Ok.Should().BeTrue();
        var wiki = Editor().Derive(null).Single(t => t.Link == Wiki);
        wiki.UsedIn.Should().Equal("Arch", "Struct");
        Editor().Derive(null).Should().HaveCount(3);
    }

    // ── remove everywhere ──

    [Fact]
    public void Remove_takes_the_tool_out_of_every_profile_and_a_stack_left_with_one_becomes_a_plain_tool()
    {
        var working = ProfileEditing.ForEditing(OnDisk(MepId));
        var editor = Editor();
        editor.Affected(Tracker, working).Should().Equal("MEP");

        var result = editor.Remove(Tracker, working, activeProfileId: MepId);
        result.Ok.Should().BeTrue();
        result.Written.Select(w => w.ProfileName).Should().Equal("MEP");
        result.ActiveProfileChanged.Should().BeTrue();
        result.WorkingCopyChanged.Should().BeTrue();

        var mep = OnDisk(MepId);
        mep.Stacks.Should().BeEmpty();
        mep.Panels[0].Slots.Select(s => (s.SlotType, s.CommandId)).Should().Equal(("tool", "URL:support@example.com"), ("tool", "ID_DOOR"));
        working.Stacks.Should().BeEmpty();
        working.Panels[0].Slots.Select(s => s.CommandId).Should().Equal("URL:support@example.com", "ID_DOOR");

        // The email, held by two profiles under two spellings of one link.
        result = Editor().Remove(Support, null, null);
        result.Written.Select(w => w.ProfileName).Should().Equal("Arch", "MEP");
        AllTools(OnDisk(ArchId)).Should().NotContain(s => s.Name == "Support");
        OnDisk(MepId).Panels[0].Slots.Select(s => s.CommandId).Should().Equal("ID_DOOR");
        Editor().Derive(null).Select(t => t.Link).Should().Equal(StructDocs, Wiki);
    }

    [Fact]
    public void Removing_the_wiki_dissolves_the_stack_that_held_it()
    {
        Editor().Remove(Wiki, null, null).Written.Select(w => w.ProfileName).Should().Equal("Arch", "Struct");
        var arch = OnDisk(ArchId);
        arch.Stacks.Should().BeEmpty();
        arch.Panels[0].Slots.Select(s => (s.SlotType, s.CommandId)).Should().Equal(("tool", "URL:" + Support), ("tool", "ID_WALL"));
        OnDisk(StructId).Panels[0].Slots.Select(s => s.CommandId).Should().Equal("URL:" + StructDocs, "ID_BEAM");
    }

    [Fact]
    public void An_edit_that_changes_nothing_writes_nothing()
    {
        var editor = Editor();
        var docs = editor.Derive(null).Single(t => t.Link == StructDocs);
        var edit = new CustomToolEdit("Struct docs", StructDocs, null);
        editor.Edit(docs, edit, null, null).Written.Should().HaveCount(1, "the first edit sets the base name");

        var working = ProfileEditing.ForEditing(OnDisk(StructId));
        var file = Path.Combine(_dir, FileOf(StructId));
        var bytes = File.ReadAllText(file);
        var stamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, stamp);

        docs = Editor().Derive(working).Single(t => t.Link == StructDocs);
        var result = Editor().Edit(docs, edit, working, activeProfileId: StructId);
        result.Ok.Should().BeTrue();
        result.Written.Should().BeEmpty();
        result.WorkingCopyChanged.Should().BeFalse();
        result.ActiveProfileChanged.Should().BeFalse("nothing was written, so the ribbon does not reload");
        File.ReadAllText(file).Should().Be(bytes);
        File.GetLastWriteTimeUtc(file).Should().Be(stamp, "the file was not rewritten");
    }

    [Fact]
    public void Remove_on_a_saved_file_with_a_shared_stack_turns_each_slot_into_a_plain_tool()
    {
        var shared = Make("d-shared", "Shared", new() { ["S"] = new Stack { Tools = [Url("Wiki", Wiki), Command("ID_WALL")] } },
            PanelStacks.StackSlot("S"), Command("ID_DOOR"), PanelStacks.StackSlot("S"));
        ProfileStore.Save(shared, _dir);
        OnDisk("d-shared").Panels[0].Slots.Count(PanelStacks.IsStack).Should().Be(2, "the read path lets a shared stack through");

        var result = Editor().Remove(Wiki, null, null);

        result.Ok.Should().BeTrue();
        result.Written.Select(w => w.ProfileName).Should().Equal("Arch", "Shared", "Struct");
        var back = OnDisk("d-shared");
        back.Stacks.Should().BeEmpty();
        back.Panels[0].Slots.Select(s => (s.SlotType, s.CommandId)).Should().Equal(("tool", "ID_WALL"), ("tool", "ID_DOOR"), ("tool", "ID_WALL"));
        ProfileSerializer.Validate(back).Should().BeEmpty();
    }

    // ── files that are never written ──

    [Fact]
    public void A_profile_file_without_an_id_is_reported_and_left_as_it_is()
    {
        var legacy = Make("", "Legacy", new(), Url("Wiki", Wiki));
        legacy.Id = null;
        WriteAs("Legacy_2024-05-01.json", legacy);
        var before = File.ReadAllText(Path.Combine(_dir, "Legacy_2024-05-01.json"));
        var files = Files();

        var editor = Editor();
        var wiki = editor.Derive(null).Single(t => t.Link == Wiki);
        foreach (var result in new[]
                 {
                     editor.Edit(wiki, new CustomToolEdit("Knowledge", "wiki2.example.com", null), null, null),
                     Editor().Remove("https://wiki2.example.com", null, null),
                     Editor().Remove(Wiki, null, null),
                 })
        {
            Files().Should().Equal(files, "no second copy is minted beside the id-less file");
            result.Failed.Where(f => f.FileName == "Legacy_2024-05-01.json").Should().HaveCountLessThanOrEqualTo(1);
        }
        var failure = Editor().Remove(Wiki, null, null).Failed.Should().ContainSingle().Subject;
        (failure.ProfileName, failure.ProfileId, failure.FileName).Should().Be(("Legacy", (string?)null, "Legacy_2024-05-01.json"));
        failure.Error.Should().Contain("no id");
        File.ReadAllText(Path.Combine(_dir, "Legacy_2024-05-01.json")).Should().Be(before);
        Editor().Derive(null).Single(t => t.Link == Wiki).UsedIn.Should().ContainSingle().Which.Should().Be("Legacy", "it still holds the tool, and the list says so");
        AllTools(OnDisk(ArchId)).Should().NotContain(s => s.CommandId == "URL:" + Wiki, "the other profiles were changed");
    }

    [Fact]
    public void Files_that_share_an_id_are_all_reported_and_none_is_written()
    {
        var current = FileOf(ArchId);
        var stale = Arch();
        stale.Panels[0].Name = "Stale";
        WriteAs("Arch_2024-05-01.json", stale);
        var bytes = Files().ToDictionary(f => f, f => File.ReadAllText(Path.Combine(_dir, f)));

        var editor = Editor();
        var wiki = editor.Derive(null).Single(t => t.Link == Wiki);
        var result = editor.Edit(wiki, new CustomToolEdit("Knowledge", Wiki, null), null, activeProfileId: ArchId);

        result.Failed.Select(f => (f.FileName, f.ProfileId)).Should().BeEquivalentTo([("Arch_2024-05-01.json", ArchId), (current, ArchId)]);
        result.Failed.Should().OnlyContain(f => f.Error.Contains("2 files share this profile's id"));
        result.Written.Select(w => w.ProfileName).Should().Equal("Struct");
        result.ActiveProfileChanged.Should().BeFalse();
        File.ReadAllText(Path.Combine(_dir, "Arch_2024-05-01.json")).Should().Be(bytes["Arch_2024-05-01.json"]);
        File.ReadAllText(Path.Combine(_dir, current)).Should().Be(bytes[current]);
    }

    [Fact]
    public void A_file_that_is_locked_is_reported_as_not_checked()
    {
        var structFile = FileOf(StructId);
        var editor = Editor();
        var wiki = editor.Derive(null).Single(t => t.Link == Wiki);
        CustomToolUpdate result;
        using (new FileStream(Path.Combine(_dir, structFile), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            result = editor.Edit(wiki, new CustomToolEdit("Knowledge", Wiki, null), null, null);

        result.Ok.Should().BeFalse();
        var failure = result.Failed.Should().ContainSingle().Subject;
        failure.FileName.Should().Be(structFile);
        failure.Error.Should().StartWith("Could not be read, so it was not checked");
        result.Written.Select(w => w.ProfileName).Should().Equal("Arch");
        OnDisk(StructId).Panels[0].Slots[0].Name.Should().Be("Wiki", "the locked file was not changed");
    }

    // ── partial failure ──

    [Fact]
    public void A_file_that_cannot_be_written_is_reported_and_logged_while_the_others_are_written()
    {
        // A directory where the atomic write puts its temp file: no user, root included, can create it.
        var structFile = FileOf(StructId);
        var structBefore = Bytes(StructId);
        Directory.CreateDirectory(Path.Combine(_dir, structFile + ".rsttmp"));
        var editor = Editor();
        var wiki = editor.Derive(null).Single(t => t.Link == Wiki);

        var result = editor.Edit(wiki, new CustomToolEdit("Knowledge", Wiki, null), null, activeProfileId: StructId);

        result.Ok.Should().BeFalse();
        result.Written.Select(w => w.ProfileName).Should().Equal("Arch");
        var failure = result.Failed.Should().ContainSingle().Subject;
        (failure.ProfileName, failure.ProfileId, failure.FileName).Should().Be(("Struct", StructId, structFile));
        failure.Error.Should().StartWith("Write failed: ");
        result.ActiveProfileChanged.Should().BeFalse("the loaded profile's file was not written");

        OnDisk(ArchId).Panels[0].Slots[0].Name.Should().Be("Knowledge", "the file written stays written");
        Bytes(StructId).Should().Be(structBefore, "the failed file is as it was");
        _log.Lines.Should().Contain(l => l.StartsWith("W ") && l.Contains("Struct") && l.Contains("Write failed"));
    }
}
