// ProfileZipTests.cs — pack / unpack / install round-trip for the
// .zip profile package format (RST-018 / RST-019).

using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using FluentAssertions;
using RST.Core.Configuration;
using RST.Core.Profiles;
using Xunit;

namespace RST.Tests.Profiles;

public sealed class ProfileZipTests : IDisposable
{
    private readonly string _root;

    public ProfileZipTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "RST-ZipTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        AppDataPaths.OverrideRootForTests(_root);
    }

    public void Dispose()
    {
        AppDataPaths.OverrideRootForTests(null);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* test cleanup is best-effort */ }
    }

    private string WriteTempLogo(byte[] bytes)
    {
        var path = Path.Combine(_root, "src-logo.png");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static Profile MakeProfile(string name = "Test Profile") => new()
    {
        Id = Guid.NewGuid().ToString(),
        ProfileName = name,
        Tab = "Test Tab",
        ExportDate = "2026-05-09",
    };

    [Fact]
    public void Pack_with_logo_emits_profile_json_and_assets_branding()
    {
        var profile = MakeProfile();
        var logoBytes = new byte[] { 1, 2, 3, 4, 5 };
        var logoPath = WriteTempLogo(logoBytes);
        using var ms = new MemoryStream();

        ProfileZip.Pack(profile, logoPath, "https://co.example", ms);

        ms.Position = 0;
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
        archive.GetEntry("profile.json").Should().NotBeNull();
        archive.GetEntry("assets/branding.png").Should().NotBeNull();
        archive.Entries.Count.Should().Be(2);
    }

    [Fact]
    public void Pack_rewrites_branding_to_per_profile_namespace()
    {
        var profile = MakeProfile();
        var profileId = profile.Id!;
        var logoPath = WriteTempLogo(new byte[] { 9 });
        using var ms = new MemoryStream();

        ProfileZip.Pack(profile, logoPath, "https://co.example", ms);

        // The mutation is observable on the passed-in profile reference.
        profile.Branding.Should().NotBeNull();
        profile.Branding!.LogoFile.Should().Be($"{profileId}/branding.png");
        profile.Branding.Url.Should().Be("https://co.example");
    }

    [Fact]
    public void Pack_without_logo_or_url_clears_branding()
    {
        var profile = MakeProfile();
        profile.Branding = new Branding { LogoFile = "old/path.png", Url = "https://old" };
        using var ms = new MemoryStream();

        ProfileZip.Pack(profile, resolvedLogoPath: null, resolvedUrl: null, ms);

        profile.Branding.Should().BeNull();
        ms.Position = 0;
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
        archive.GetEntry("assets/branding.png").Should().BeNull();
    }

    [Fact]
    public void Pack_with_url_only_emits_branding_section_no_asset()
    {
        var profile = MakeProfile();
        using var ms = new MemoryStream();

        ProfileZip.Pack(profile, resolvedLogoPath: null, resolvedUrl: "https://co.example", ms);

        profile.Branding.Should().NotBeNull();
        profile.Branding!.LogoFile.Should().BeNull();
        profile.Branding.Url.Should().Be("https://co.example");

        ms.Position = 0;
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
        archive.GetEntry("assets/branding.png").Should().BeNull();
    }

    [Fact]
    public void Unpack_reads_profile_and_logo()
    {
        var profile = MakeProfile("Round Trip");
        var logoBytes = new byte[] { 10, 20, 30, 40 };
        var logoPath = WriteTempLogo(logoBytes);
        using var ms = new MemoryStream();
        ProfileZip.Pack(profile, logoPath, "https://co.example", ms);

        ms.Position = 0;
        var package = ProfileZip.Unpack(ms);

        package.Profile.ProfileName.Should().Be("Round Trip");
        package.LogoBytes.Should().NotBeNull();
        package.LogoBytes!.Should().BeEquivalentTo(logoBytes);
    }

    [Fact]
    public void Unpack_missing_profile_json_throws()
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var bogus = archive.CreateEntry("not-a-profile.txt");
            using var s = bogus.Open();
            s.Write(new byte[] { 1 }, 0, 1);
        }
        ms.Position = 0;

        var act = () => ProfileZip.Unpack(ms);
        act.Should().Throw<InvalidDataException>().WithMessage("*profile.json*");
    }

    [Fact]
    public void Install_writes_logo_under_appdata_profile_id_and_persists_profile()
    {
        var profile = MakeProfile("Installed");
        var profileId = profile.Id!;
        var logoBytes = new byte[] { 100, 101, 102 };
        var logoPath = WriteTempLogo(logoBytes);
        using var ms = new MemoryStream();
        ProfileZip.Pack(profile, logoPath, "https://co.example", ms);

        ms.Position = 0;
        var package = ProfileZip.Unpack(ms);

        var fileName = ProfileZip.Install(package);

        // Logo asset extracted to the per-profile namespace under our temp root.
        var expectedAsset = Path.Combine(_root, profileId, "branding.png");
        File.Exists(expectedAsset).Should().BeTrue();
        File.ReadAllBytes(expectedAsset).Should().BeEquivalentTo(logoBytes);

        // Profile JSON written to ProfilesDir.
        var profilePath = Path.Combine(AppDataPaths.ProfilesDir, fileName);
        File.Exists(profilePath).Should().BeTrue();

        // The persisted JSON should reference the per-profile relative
        // logo path so BrandingDefaults.Resolve picks it up cleanly.
        using var fs = File.OpenRead(profilePath);
        var saved = ProfileSerializer.Read(fs);
        saved.Branding.Should().NotBeNull();
        saved.Branding!.LogoFile.Should().Be($"{profileId}/branding.png");
    }

    [Theory]
    [InlineData(@"..\..\..\..\rst-escape-marker")]
    [InlineData("/etc/cron.d/rst")]
    [InlineData("")]
    public void Install_coerces_unsafe_profile_id_to_guid_and_writes_under_root(string maliciousId)
    {
        // A crafted package whose profile.json carried a traversal/absolute id.
        var profile = MakeProfile("Evil");
        profile.Id = maliciousId;
        var logoBytes = new byte[] { 7, 7, 7 };
        var package = new ProfilePackage(profile, logoBytes);

        ProfileZip.Install(package);

        // Id must have been replaced with a real GUID...
        Guid.TryParse(package.Profile.Id, out _).Should().BeTrue();
        // ...and the asset must live under the data root, not outside it.
        var asset = Path.Combine(_root, package.Profile.Id!, "branding.png");
        File.Exists(asset).Should().BeTrue();
        AppDataPaths.IsUnderRoot(asset).Should().BeTrue();
    }

    [Fact]
    public void Install_without_logo_persists_profile_unchanged()
    {
        var profile = MakeProfile("UrlOnly");
        using var ms = new MemoryStream();
        ProfileZip.Pack(profile, resolvedLogoPath: null, resolvedUrl: "https://co.example", ms);

        ms.Position = 0;
        var package = ProfileZip.Unpack(ms);

        var fileName = ProfileZip.Install(package);

        var profilePath = Path.Combine(AppDataPaths.ProfilesDir, fileName);
        File.Exists(profilePath).Should().BeTrue();

        using var fs = File.OpenRead(profilePath);
        var saved = ProfileSerializer.Read(fs);
        saved.Branding.Should().NotBeNull();
        saved.Branding!.LogoFile.Should().BeNull();
        saved.Branding.Url.Should().Be("https://co.example");

        // No per-profile asset dir created.
        Directory.Exists(Path.Combine(_root, profile.Id!)).Should().BeFalse();
    }
}
