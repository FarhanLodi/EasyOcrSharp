using System.Runtime.InteropServices;
using System.Text;

namespace EasyOcrSharp.Internal;

/// <summary>
/// Best-effort detection of an installed GPU on the host. Used <i>only</i> to point the user at the
/// correct provider package when EasyOcrSharp has fallen back to CPU — never to enable a provider (that
/// requires the matching NuGet package, which can't be added at runtime). Windows-only; returns
/// <see cref="GpuVendor.None"/> on other platforms and on any failure (detection is purely advisory).
/// <para>
/// It reads the installed display-adapter drivers from the registry (the Display device-class key)
/// rather than <c>EnumDisplayDevices</c>, which only reports the <i>active</i> display driver and so
/// misses a physical GPU whenever the desktop is currently on the Microsoft Basic Display Driver.
/// </para>
/// </summary>
internal static class GpuProbe
{
    // Ordered by preference so the numeric comparison in DetectWindows picks the best vendor found.
    internal enum GpuVendor { None, Other, Intel, Amd, Nvidia }

    /// <summary>Returns the most capable GPU vendor among the host's installed display adapters.</summary>
    public static GpuVendor Detect()
    {
        if (!OperatingSystem.IsWindows()) return GpuVendor.None;
        try { return DetectWindows(); }
        catch { return GpuVendor.None; }
    }

    // The Display device-class GUID; its numeric subkeys ("0000", "0001", ...) are installed adapters.
    private const string DisplayClassKey =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    private static GpuVendor DetectWindows()
    {
        if (RegOpenKeyEx(HKEY_LOCAL_MACHINE, DisplayClassKey, 0, KEY_READ, out var hClass) != 0)
            return GpuVendor.None;

        try
        {
            var best = GpuVendor.None;
            var nameBuf = new char[256];
            for (uint i = 0; ; i++)
            {
                int nameLen = nameBuf.Length;
                int rc = RegEnumKeyEx(hClass, i, nameBuf, ref nameLen, IntPtr.Zero, null, IntPtr.Zero, IntPtr.Zero);
                if (rc != 0) break; // ERROR_NO_MORE_ITEMS (259) or any error ends enumeration

                var subKey = new string(nameBuf, 0, nameLen);
                if (subKey.Length != 4 || !subKey.All(char.IsAsciiDigit)) continue; // skip "Properties" etc.

                var vendor = Classify(ReadString(hClass, subKey, "DriverDesc"));
                if (vendor > best) best = vendor;
                if (best == GpuVendor.Nvidia) break; // nothing outranks it
            }
            return best;
        }
        finally { RegCloseKey(hClass); }
    }

    private static GpuVendor Classify(string? name)
    {
        if (string.IsNullOrEmpty(name)) return GpuVendor.None;
        // Brand names are not localized, so plain substring matching is safe across locales.
        if (Has(name, "NVIDIA")) return GpuVendor.Nvidia;
        if (Has(name, "Radeon") || Has(name, "AMD") || Has(name, "Advanced Micro Devices")) return GpuVendor.Amd;
        if (Has(name, "Intel")) return GpuVendor.Intel;
        // Software / virtual / remote adapters are not usable GPUs for acceleration.
        if (Has(name, "Microsoft") || Has(name, "Basic") || Has(name, "Remote") || Has(name, "Virtual"))
            return GpuVendor.None;
        return GpuVendor.Other;
    }

    private static bool Has(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads a REG_SZ value at <paramref name="parent"/>\<paramref name="subKey"/>\<paramref name="value"/>.</summary>
    private static string? ReadString(nint parent, string subKey, string value)
    {
        const int RRF_RT_REG_SZ = 0x00000002;
        int cb = 0;
        if (RegGetValue(parent, subKey, value, RRF_RT_REG_SZ, IntPtr.Zero, null, ref cb) != 0 || cb <= 0)
            return null;

        var buf = new byte[cb];
        if (RegGetValue(parent, subKey, value, RRF_RT_REG_SZ, IntPtr.Zero, buf, ref cb) != 0)
            return null;

        // cb is byte count including the UTF-16 null terminator.
        int chars = Math.Max(0, (cb / 2) - 1);
        return Encoding.Unicode.GetString(buf, 0, chars * 2);
    }

    private static readonly nint HKEY_LOCAL_MACHINE = unchecked((nint)0x80000002u);
    private const int KEY_READ = 0x20019;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegOpenKeyExW")]
    private static extern int RegOpenKeyEx(nint hKey, string subKey, int options, int samDesired, out nint result);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegEnumKeyExW")]
    private static extern int RegEnumKeyEx(nint hKey, uint index, char[] name, ref int nameLen,
        IntPtr reserved, char[]? className, IntPtr classLen, IntPtr lastWrite);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegGetValueW")]
    private static extern int RegGetValue(nint hKey, string subKey, string value, int flags,
        IntPtr type, byte[]? data, ref int dataLen);

    [DllImport("advapi32.dll")]
    private static extern int RegCloseKey(nint hKey);
}
