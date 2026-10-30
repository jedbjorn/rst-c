// Copied from splash-it src/SplashIt.UI/Dialogs/NativeColorDialog.cs (splash-it commit ba86a71; originally gantt-it src/GanttIt.Revit/UI/Dialogs/NativeColorDialog.cs, gantt-it commit a96d0a1), MIT, same owner.
// Changes: namespaces (SplashIt.* to RST.*).

using System.Runtime.InteropServices;
using RST.Core.Colors;

namespace RST.UI.Dialogs;

/// <summary>
/// The Windows color dialog (gantt-it §2.4 "More…"), through comdlg32's ChooseColor.
/// P/Invoke instead of WinForms keeps the add-in on WPF and the BCL only (gantt-it §8).
/// </summary>
public static class NativeColorDialog
{
    private const int CcRgbInit = 0x1;
    private const int CcFullOpen = 0x2;

    /// <summary>The 16 custom colors the dialog remembers, kept for the Revit session.</summary>
    private static readonly int[] CustomColors = Enumerable.Repeat(0xFFFFFF, 16).ToArray();

    /// <summary>Shows the dialog owned by <paramref name="owner"/>. Null when cancelled.</summary>
    public static Rgb? Show(IntPtr owner, Rgb initial)
    {
        var custom = Marshal.AllocHGlobal(sizeof(int) * CustomColors.Length);
        try
        {
            Marshal.Copy(CustomColors, 0, custom, CustomColors.Length);
            var cc = new ChooseColorData
            {
                StructSize = Marshal.SizeOf<ChooseColorData>(),
                Owner = owner,
                Result = ToColorRef(initial),
                CustomColors = custom,
                Flags = CcRgbInit | CcFullOpen,
            };
            if (!ChooseColorW(ref cc)) return null;
            Marshal.Copy(custom, CustomColors, 0, CustomColors.Length);
            return FromColorRef(cc.Result);
        }
        finally
        {
            Marshal.FreeHGlobal(custom);
        }
    }

    /// <summary>COLORREF is 0x00BBGGRR.</summary>
    private static int ToColorRef(Rgb c) => c.R | (c.G << 8) | (c.B << 16);

    private static Rgb FromColorRef(int v) => new((byte)(v & 0xFF), (byte)((v >> 8) & 0xFF), (byte)((v >> 16) & 0xFF));

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ChooseColorData
    {
        public int StructSize;
        public IntPtr Owner;
        public IntPtr Instance;
        public int Result;
        public IntPtr CustomColors;
        public int Flags;
        public IntPtr CustData;
        public IntPtr Hook;
        public IntPtr TemplateName;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChooseColorW(ref ChooseColorData data);
}
