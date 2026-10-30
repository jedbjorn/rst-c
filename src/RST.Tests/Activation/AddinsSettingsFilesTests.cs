// rst-c's own (not vendored): Revit's Add-Ins Manager files, against the real captures from the
// VM spike (shared/f15/spike/settings, copied to Fixtures/AddinsManager): the user file
// round-trips byte for byte, one flip changes exactly one token (as the dialog's own Apply does),
// new entries take Revit's key order, the structural guard refuses what is not the format, and the
// admin file's locks are read with their manifest paths.

using System.Text;
using FluentAssertions;
using RST.Core.Activation;
using Xunit;

namespace RST.Tests.Activation;

public class AddinsSettingsFilesTests
{
    [Theory]
    [InlineData("orig-2026.json")]
    [InlineData("orig-2027.json")]
    public void A_real_settings_file_round_trips_byte_for_byte(string fixture)
    {
        var json = FakeAddinTree.Fixture(fixture);
        var doc = AddinsSettingsDocument.TryParse(json, out var error)!;
        error.Should().BeNull();
        doc.ToJson().Should().Be(json);
        doc.ToBytes().Should().Equal(Encoding.UTF8.GetBytes(json), "UTF-8 without a BOM, no newline");
    }

    [Fact]
    public void One_flip_changes_exactly_one_token_as_the_add_ins_managers_apply_does()
    {
        var json = FakeAddinTree.Fixture("orig-2026.json");
        var doc = AddinsSettingsDocument.TryParse(json, out _)!;
        doc.SetDisabled("B4AD7489-2899-46B3-ACA6-C0DEF7135528", true, "ignored", "ignored").Should().BeTrue();
        doc.ToJson().Should().Be(json.Replace(
            "{\"Disabled\":false,\"GUID\":\"b4ad7489-2899-46b3-aca6-c0def7135528\"",
            "{\"Disabled\":true,\"GUID\":\"b4ad7489-2899-46b3-aca6-c0def7135528\""));
        doc.SetDisabled("b4ad7489-2899-46b3-aca6-c0def7135528", true, null, null).Should().BeFalse("already off");
        doc.SetDisabled("b4ad7489-2899-46b3-aca6-c0def7135528", false, null, null);
        doc.ToJson().Should().Be(json, "off then on is byte-identical (the spike's round trip)");
    }

    [Fact]
    public void A_missing_entry_is_appended_in_revits_key_order_and_unknown_fields_are_kept()
    {
        var doc = AddinsSettingsDocument.TryParse(
            "{\"DisableAllAddIns\":false,\"Future\":{\"x\":[1,2]},\"AddInItemSettings\":[{\"Disabled\":false,\"GUID\":\"a47e7259-a459-4fdd-8435-fb0bc0f86268\",\"Extra\":\"keep\"}]}", out _)!;
        doc.SetDisabled("{D4C6E3E0-0000-4000-8000-525354500001}", true, "RST-Probe", "RSTC");
        doc.ToJson().Should().Be(
            "{\"DisableAllAddIns\":false,\"Future\":{\"x\":[1,2]},\"AddInItemSettings\":[{\"Disabled\":false,\"GUID\":\"a47e7259-a459-4fdd-8435-fb0bc0f86268\",\"Extra\":\"keep\"},"
            + "{\"Disabled\":true,\"GUID\":\"d4c6e3e0-0000-4000-8000-525354500001\",\"LoadTime\":0,\"Name\":\"RST-Probe\",\"Vendor\":\"RSTC\"}]}");
    }

    [Fact]
    public void A_new_file_is_the_minimal_shape_revit_accepts()
    {
        var doc = AddinsSettingsDocument.CreateEmpty();
        doc.SetDisabled("b4ad7489-2899-46b3-aca6-c0def7135528", true, "DiRoots.One", "www.diroots.com");
        doc.ToJson().Should().Be("{\"DisableAllAddIns\":false,\"AddInItemSettings\":[{\"Disabled\":true,\"GUID\":\"b4ad7489-2899-46b3-aca6-c0def7135528\",\"LoadTime\":0,\"Name\":\"DiRoots.One\",\"Vendor\":\"www.diroots.com\"}]}");
    }

    [Theory]
    [InlineData("s9-input-seed-corrupt-2026.json")]
    [InlineData(null)]
    public void The_structural_guard_refuses_what_is_not_the_add_ins_manager_format(string? fixture)
    {
        var inputs = fixture is null
            ? new[] { "[]", "{}", "{\"AddInItemSettings\":{}}", "{\"AddInItemSettings\":[{\"Disabled\":\"no\",\"GUID\":\"b4ad7489-2899-46b3-aca6-c0def7135528\"}]}",
                      "{\"AddInItemSettings\":[{\"Disabled\":true,\"GUID\":\"not-a-guid\"}]}", "{\"DisableAllAddIns\":1,\"AddInItemSettings\":[]}" }
            : [FakeAddinTree.Fixture(fixture)];
        foreach (var json in inputs)
        {
            AddinsSettingsDocument.TryParse(json, out var error).Should().BeNull(json);
            error.Should().NotBeNullOrEmpty();
        }
    }

    [Fact]
    public void The_spikes_minimal_and_upper_case_seeds_parse()
    {
        var minimal = AddinsSettingsDocument.TryParse(FakeAddinTree.Fixture("s7-input-seed-minimal-2026.json"), out _)!;
        minimal.DisabledIds().Should().BeEquivalentTo("b4ad7489-2899-46b3-aca6-c0def7135528", "0badc0de-0000-4000-8000-000000000001");
        AddinsSettingsDocument.TryParse(FakeAddinTree.Fixture("s8-input-seed-uppercase-2026.json"), out _)!
            .IsDisabled("5a9e8c41-09d6-4cdf-8556-38f8119137a4").Should().BeTrue();
    }

    [Fact]
    public void Admin_locks_are_read_from_autodesks_own_and_the_compact_format()
    {
        var api = AdminSettings.Parse(FakeAddinTree.Fixture("AdminSettings-sample-from-API-2026.json"));
        api.Locks.Should().Equal(new AdminLockEntry("b4ad7489-2899-46b3-aca6-c0def7135528", @"C:\ProgramData\Autodesk\Revit\Addins\2026\DiRoots.One.addin", AdminLock.AlwaysDisabled));
        api.LockFor("B4AD7489-2899-46B3-ACA6-C0DEF7135528", @"c:\programdata\autodesk\revit\addins\2026\DiRoots.One.addin").Should().Be(AdminLock.AlwaysDisabled);
        api.LockFor("b4ad7489-2899-46b3-aca6-c0def7135528", @"C:\Other\DiRoots.One.addin").Should().Be(AdminLock.None);

        var compact = AdminSettings.Parse(FakeAddinTree.Fixture("s11-input-AdminSettings-2027.json"));
        compact.LockFor("b4ad7489-2899-46b3-aca6-c0def7135528", @"C:\Program Files\Autodesk\Revit\Addins\2027\DiRoots.One.addin").Should().Be(AdminLock.AlwaysDisabled);

        AdminSettings.Parse("{\"DisableAllAddIns\":true,\"AddInManifestAdminSettings\":[{\"AddInItemAdminSettings\":[{\"AddInId\":\"b4ad7489-2899-46b3-aca6-c0def7135528\",\"LoadOption\":1},{\"AddInId\":\"a47e7259-a459-4fdd-8435-fb0bc0f86268\",\"LoadOption\":0}]}]}")
            .Should().Match<AdminSettings>(a => a.DisableAll && a.Locks.Count == 1 && a.LockFor("b4ad7489-2899-46b3-aca6-c0def7135528", "any") == AdminLock.AlwaysEnabled);
        AdminSettings.Parse("garbage").Should().BeSameAs(AdminSettings.None);
        AdminSettings.Read("/no/such/file").Should().BeSameAs(AdminSettings.None);
    }

    [Theory]
    [InlineData("4F8EF7A0-0001-4000-8000-525354000001", "4f8ef7a0-0001-4000-8000-525354000001")]
    [InlineData("{4f8ef7a0-0001-4000-8000-525354000001}", "4f8ef7a0-0001-4000-8000-525354000001")]
    [InlineData(" 4f8ef7a0000140008000525354000001 ", "4f8ef7a0-0001-4000-8000-525354000001")]
    [InlineData("nope", null)]
    [InlineData(null, null)]
    public void Add_in_ids_normalise_to_revits_lower_case_key(string? input, string? expected) => AddinIds.Normalize(input).Should().Be(expected);
}
