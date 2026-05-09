// RoundTripTests.cs — read-write-read fidelity, validation, and v0→v1 migration.

using System.IO;
using System.Linq;
using FluentAssertions;
using RST.Core.Profiles;
using Xunit;

namespace RST.Tests.Profiles;

public class RoundTripTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Profiles", name);

    [Theory]
    [InlineData("minimal.json")]
    [InlineData("full.json")]
    public void Reads_writes_and_rereads_with_identical_object_graph(string fixture)
    {
        using var stream = File.OpenRead(FixturePath(fixture));
        var first = ProfileSerializer.Read(stream);

        var roundTrip = ProfileSerializer.WriteString(first);
        var second = ProfileSerializer.ReadString(roundTrip);

        // Validation must pass on both
        ProfileSerializer.Validate(first).Should().BeEmpty();
        ProfileSerializer.Validate(second).Should().BeEmpty();

        // Field-by-field equality on the parts we care about
        second.Id.Should().Be(first.Id);
        second.ProfileName.Should().Be(first.ProfileName);
        second.Tab.Should().Be(first.Tab);
        second.PanelOpacity.Should().Be(first.PanelOpacity);
        second.SchemaVersion.Should().Be(first.SchemaVersion);

        second.Panels.Count.Should().Be(first.Panels.Count);
        for (int i = 0; i < first.Panels.Count; i++)
        {
            second.Panels[i].Name.Should().Be(first.Panels[i].Name);
            second.Panels[i].Color.Should().Be(first.Panels[i].Color);
            second.Panels[i].CornerRadius.Should().Be(first.Panels[i].CornerRadius);
            second.Panels[i].Slots.Count.Should().Be(first.Panels[i].Slots.Count);
        }

        second.Stacks.Keys.Should().BeEquivalentTo(first.Stacks.Keys);
        second.RequiredAddins.Count.Should().Be(first.RequiredAddins.Count);
        second.HideRules.Count.Should().Be(first.HideRules.Count);

        if (first.Branding is null)
        {
            second.Branding.Should().BeNull();
        }
        else
        {
            second.Branding.Should().NotBeNull();
            second.Branding!.LogoFile.Should().Be(first.Branding.LogoFile);
            second.Branding.Url.Should().Be(first.Branding.Url);
        }
    }

    [Fact]
    public void Presets_round_trip_through_serializer()
    {
        var first = new Profile
        {
            Id = "abc",
            ProfileName = "Presets RT",
            Tab = "T",
            Panels = new System.Collections.Generic.List<Panel>
            {
                new Panel { Name = "P", Color = "#4f8ef7", Slots = new System.Collections.Generic.List<Slot>() },
            },
            Presets = new ProfilePresets
            {
                HiddenTabs = new System.Collections.Generic.List<string> { "Architecture", "Annotate" },
                DisableUnusedAddins = true,
            },
        };

        var json = ProfileSerializer.WriteString(first);
        var second = ProfileSerializer.ReadString(json);

        second.Presets.Should().NotBeNull();
        second.Presets!.HiddenTabs.Should().BeEquivalentTo(new[] { "Architecture", "Annotate" });
        second.Presets.DisableUnusedAddins.Should().BeTrue();
    }

    [Fact]
    public void Profile_without_presets_round_trips_with_null()
    {
        var first = new Profile
        {
            Id = "abc",
            ProfileName = "No Presets",
            Tab = "T",
            Panels = new System.Collections.Generic.List<Panel>
            {
                new Panel { Name = "P", Color = "#4f8ef7", Slots = new System.Collections.Generic.List<Slot>() },
            },
        };

        var json = ProfileSerializer.WriteString(first);
        var second = ProfileSerializer.ReadString(json);

        second.Presets.Should().BeNull();
    }

    [Fact]
    public void Legacy_v0_profile_migrates_to_current_schema_on_load()
    {
        using var stream = File.OpenRead(FixturePath("legacy_v0.json"));
        var profile = ProfileSerializer.Read(stream);

        profile.SchemaVersion.Should().Be(ProfileSerializer.CurrentSchemaVersion);
        profile.ProfileName.Should().Be("Legacy");
        profile.Panels.Should().HaveCount(1);
        ProfileSerializer.Validate(profile).Should().BeEmpty();
    }

    [Fact]
    public void Read_rejects_profile_with_invalid_hex_color()
    {
        var act = () => ProfileSerializer.ReadString(@"{
          ""schemaVersion"": 1, ""profile"": ""X"", ""tab"": ""X"",
          ""panels"": [{ ""name"": ""P"", ""color"": ""#zzz"", ""slots"": [] }]
        }");

        act.Should().Throw<ProfileLoadException>()
           .WithMessage("*color*#zzz*");
    }

    [Fact]
    public void Validate_flags_invalid_hex_color_directly()
    {
        var profile = new Profile
        {
            SchemaVersion = 1,
            ProfileName = "X",
            Tab = "X",
            Panels = { new Panel { Name = "P", Color = "#zzz" } },
        };

        var errors = ProfileSerializer.Validate(profile);
        errors.Should().Contain(e => e.Contains("color") && e.Contains("#zzz"));
    }

    [Fact]
    public void Validate_flags_stack_with_wrong_tool_count()
    {
        var profile = new Profile
        {
            SchemaVersion = 1,
            ProfileName = "X",
            Tab = "X",
            Stacks = { ["TooFew"] = new Stack { Tools = { new Slot { Name = "A", CommandId = "ID_A" } } } },
        };

        var errors = ProfileSerializer.Validate(profile);
        errors.Should().Contain(e => e.Contains("TooFew") && e.Contains("2-3"));
    }

    [Fact]
    public void Validate_flags_stack_slot_referencing_unknown_stack()
    {
        var profile = new Profile
        {
            SchemaVersion = 1,
            ProfileName = "X",
            Tab = "X",
            Panels =
            {
                new Panel
                {
                    Name = "P", Color = "#4f8ef7",
                    Slots = { new Slot { SlotType = "stack", Name = "Missing" } },
                },
            },
        };

        var errors = ProfileSerializer.Validate(profile);
        errors.Should().Contain(e => e.Contains("Missing"));
    }

    [Fact]
    public void Read_throws_ProfileLoadException_on_invalid_json()
    {
        var act = () => ProfileSerializer.ReadString("{ not-json");
        act.Should().Throw<ProfileLoadException>();
    }
}
