// BrandingDefaultsTests.cs — round-trip + seeding for the per-machine
// company branding store. Each test creates a fresh temp dir and points
// AppDataPaths.Root at it via the test hook, so nothing touches the
// real %AppData%\RST\.

using System;
using System.IO;
using FluentAssertions;
using RST.Core.Configuration;
using RST.Core.Profiles;
using Xunit;

namespace RST.Tests.Configuration;

public sealed class BrandingDefaultsTests : IDisposable
{
    private readonly string _tempRoot;

    public BrandingDefaultsTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "rst-branding-tests-" + Path.GetRandomFileName());
        AppDataPaths.OverrideRootForTests(_tempRoot);
    }

    public void Dispose()
    {
        AppDataPaths.OverrideRootForTests(null);
        if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmptyDefaults()
    {
        var loaded = BrandingDefaults.Load();
        loaded.Url.Should().BeNull();
        BrandingDefaults.HasLogo.Should().BeFalse();
    }

    [Fact]
    public void Load_CorruptFile_ReturnsEmptyDefaults()
    {
        Directory.CreateDirectory(_tempRoot);
        File.WriteAllText(BrandingDefaults.ConfigPath, "{ this is not valid json");
        var loaded = BrandingDefaults.Load();
        loaded.Url.Should().BeNull();
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsUrl()
    {
        new BrandingDefaults { Url = "https://example.com" }.Save();
        BrandingDefaults.Load().Url.Should().Be("https://example.com");
    }

    [Fact]
    public void Save_WhitespaceUrl_PersistsAsNull()
    {
        new BrandingDefaults { Url = "   " }.Save();
        BrandingDefaults.Load().Url.Should().BeNull();
    }

    [Fact]
    public void EnsureSeeded_NoBundledFile_DoesNotThrow_AndDoesNotCreateLogo()
    {
        // Test runner has no Assets/branding.png next to its assembly,
        // so seed should be a no-op and HasLogo should remain false.
        BrandingDefaults.EnsureSeeded();
        BrandingDefaults.HasLogo.Should().BeFalse();
    }

    [Fact]
    public void EnsureSeeded_PreservesExistingLogo_WhenAlreadyPresent()
    {
        Directory.CreateDirectory(_tempRoot);
        File.WriteAllBytes(BrandingDefaults.LogoPath, new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        var beforeBytes = File.ReadAllBytes(BrandingDefaults.LogoPath);

        BrandingDefaults.EnsureSeeded();

        File.ReadAllBytes(BrandingDefaults.LogoPath).Should().BeEquivalentTo(beforeBytes);
    }

    [Fact]
    public void Resolve_ProfileWithNullBranding_FallsBackToDefault_WhenLogoExists()
    {
        Directory.CreateDirectory(_tempRoot);
        File.WriteAllBytes(BrandingDefaults.LogoPath, new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        new BrandingDefaults { Url = "https://acme.test" }.Save();

        var profile = new Profile { Branding = null };
        var (logoPath, url) = BrandingDefaults.Resolve(profile);

        logoPath.Should().Be(BrandingDefaults.LogoPath);
        url.Should().Be("https://acme.test");
    }

    [Fact]
    public void Resolve_ProfileWithNullBranding_NoDefaultLogo_ReturnsNullPath()
    {
        var profile = new Profile { Branding = null };
        var (logoPath, _) = BrandingDefaults.Resolve(profile);
        logoPath.Should().BeNull();
    }

    [Fact]
    public void Resolve_PerProfileOverride_WinsWhenFileExists()
    {
        Directory.CreateDirectory(_tempRoot);
        // Default logo also present, but profile override should take it.
        File.WriteAllBytes(BrandingDefaults.LogoPath, new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        var overrideRel = "custom-brand.png";
        var overrideAbs = Path.Combine(_tempRoot, overrideRel);
        File.WriteAllBytes(overrideAbs, new byte[] { 0x42, 0x4D });

        var profile = new Profile
        {
            Branding = new Branding { LogoFile = overrideRel, Url = "https://override.test" }
        };
        var (logoPath, url) = BrandingDefaults.Resolve(profile);

        logoPath.Should().Be(overrideAbs);
        url.Should().Be("https://override.test");
    }

    [Fact]
    public void Resolve_PerProfileOverride_FallsBackToDefault_WhenOverrideMissing()
    {
        Directory.CreateDirectory(_tempRoot);
        File.WriteAllBytes(BrandingDefaults.LogoPath, new byte[] { 0x89, 0x50, 0x4E, 0x47 });

        var profile = new Profile
        {
            Branding = new Branding { LogoFile = "does-not-exist.png", Url = null }
        };
        var (logoPath, _) = BrandingDefaults.Resolve(profile);

        logoPath.Should().Be(BrandingDefaults.LogoPath);
    }
}
