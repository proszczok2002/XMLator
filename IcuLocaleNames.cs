using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace XMLator;

internal static class IcuLocaleNames
{
    internal static string? DisplayName(string id, string language) => Read(id, language, country: false);
    internal static string? Country(string ISORegion, string language) => Read($"und_{ISORegion}", language, country: true);

    private static string? Read(string id, string language, bool country)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362))
            return null;

        var buffer = new StringBuilder(256);
        int error = 0;
        string displayLocale = UiText.CultureName(language).Replace('-', '_');
        try
        {
            int length = country
                ? GetDisplayCountry(id, displayLocale, buffer, buffer.Capacity, ref error)
                : GetDisplayName(id, displayLocale, buffer, buffer.Capacity, ref error);
            return error <= 0 && length is > 0 and < 256
                ? UiText.Culture(language).TextInfo.ToUpper(buffer[0]) + buffer.ToString()[1..]
                : null;
        }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("icu.dll", EntryPoint = "uloc_getDisplayName", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int GetDisplayName([MarshalAs(UnmanagedType.LPStr)] string locale, [MarshalAs(UnmanagedType.LPStr)] string displayLocale,
        StringBuilder name, int capacity, ref int error);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("icu.dll", EntryPoint = "uloc_getDisplayCountry", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int GetDisplayCountry([MarshalAs(UnmanagedType.LPStr)] string locale, [MarshalAs(UnmanagedType.LPStr)] string displayLocale,
        StringBuilder name, int capacity, ref int error);
}
