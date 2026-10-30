// rst-c's own (not vendored): which RST, Gantt-It and Splash-It products Windows Installer has for this user
// (spec #20 §6; research Q6; F17 WU3). P/Invoke to msi.dll, no admin needed:
//
//   MsiEnumRelatedProductsW(upgradeCode, 0, i, buf)  every installed or advertised ProductCode with that UpgradeCode
//                                                    this user can see (their per-user products and per-machine ones)
//   MsiGetProductInfoExW(code, null, context, "VersionString", …)
//                                                    asked per context (this user unmanaged, this user managed,
//                                                    machine): it answers only in the context the product is
//                                                    installed in, and VersionString is an installed-only property,
//                                                    so an advertised-only product is not counted
//
// Another Windows user's per-user install is invisible here, as it should be. Nothing here throws.

using System.Runtime.InteropServices;
using System.Text;
using RST.Core.Updates;

namespace RST.Tray.Updates;

internal sealed class MsiProducts : IInstalledProducts
{
    private const uint ErrorSuccess = 0;
    private const uint ErrorMoreData = 234;
    private const uint ErrorNoMoreItems = 259;

    private const string VersionStringProperty = "VersionString"; // INSTALLPROPERTY_VERSIONSTRING

    /// <summary>Asked in this order; the first context that answers is the product's.</summary>
    private static readonly (InstallContext Context, int Native)[] Contexts =
    {
        (InstallContext.User, 2),        // MSIINSTALLCONTEXT_USERUNMANAGED
        (InstallContext.UserManaged, 1), // MSIINSTALLCONTEXT_USERMANAGED
        (InstallContext.Machine, 4),     // MSIINSTALLCONTEXT_MACHINE
    };

    private readonly Action<string> _log;

    public MsiProducts(Action<string>? log = null) => _log = log ?? (_ => { });

    public IReadOnlyList<InstalledProduct> Related(string upgradeCode)
    {
        var found = new List<InstalledProduct>();
        if (ProductCatalog.NormalizeGuid(upgradeCode) is not { } code) return found;
        try
        {
            foreach (var productCode in RelatedProductCodes("{" + code + "}"))
            {
                foreach (var (context, native) in Contexts)
                {
                    var version = ProductInfo(productCode, native, VersionStringProperty);
                    if (version is null) continue;
                    found.Add(new InstalledProduct(productCode, version, context));
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            _log("detection: msi.dll unavailable: " + ex.Message);
        }
        return found;
    }

    private IEnumerable<string> RelatedProductCodes(string upgradeCode)
    {
        var codes = new List<string>();
        for (uint i = 0; i < 64; i++)
        {
            var buffer = new StringBuilder(39); // a braced GUID and its terminator
            var result = MsiEnumRelatedProductsW(upgradeCode, 0, i, buffer);
            if (result == ErrorNoMoreItems) break;
            if (result != ErrorSuccess)
            {
                _log($"detection: MsiEnumRelatedProducts {upgradeCode} #{i} returned {result}");
                break;
            }
            codes.Add(buffer.ToString());
        }
        return codes;
    }

    /// <summary>One product property in one context, or null when the product is not installed there (or it fails).</summary>
    private static string? ProductInfo(string productCode, int context, string property)
    {
        uint length = 64;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buffer = new StringBuilder((int)length + 1);
            var size = length + 1;
            var result = MsiGetProductInfoExW(productCode, null, context, property, buffer, ref size);
            if (result == ErrorSuccess) return buffer.ToString();
            if (result != ErrorMoreData) return null; // 1605 unknown product (not in this context), 1608 unknown property, …
            length = size + 1;
        }
        return null;
    }

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiEnumRelatedProductsW(string lpUpgradeCode, uint dwReserved, uint iProductIndex, StringBuilder lpProductBuf);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiGetProductInfoExW(string szProductCode, string? szUserSid, int dwContext, string szProperty,
                                                    StringBuilder? szValue, ref uint pcchValue);
}
