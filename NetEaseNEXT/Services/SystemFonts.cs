using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace NetEaseNEXT.Services;

internal static class SystemFonts
{
    internal static string[] Enumerate(CancellationToken ct)
    {
        var iid = typeof(IDWriteFactory).GUID;
        Marshal.ThrowExceptionForHR(DWriteCreateFactory(1, ref iid, out var factory));
        try
        {
            factory.GetSystemFontCollection(out var collection, true);
            try
            {
                var result = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
                for (uint index = 0; index < collection.GetFontFamilyCount(); index++)
                {
                    ct.ThrowIfCancellationRequested(); collection.GetFontFamily(index, out var family);
                    try
                    {
                        family.GetFamilyNames(out var names);
                        try
                        {
                            names.FindLocaleName(CultureInfo.CurrentUICulture.Name, out var locale, out var exists);
                            if (!exists) names.FindLocaleName("en-us", out locale, out exists);
                            if (!exists) locale = 0;
                            names.GetStringLength(locale, out var length); var value = new StringBuilder(checked((int)length + 1));
                            names.GetString(locale, value, length + 1); result.Add(value.ToString());
                        }
                        finally { Marshal.FinalReleaseComObject(names); }
                    }
                    finally { Marshal.FinalReleaseComObject(family); }
                }
                return result.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToArray();
            }
            finally { Marshal.FinalReleaseComObject(collection); }
        }
        finally { Marshal.FinalReleaseComObject(factory); }
    }
    [DllImport("dwrite.dll", ExactSpelling = true)] private static extern int DWriteCreateFactory(uint type, ref Guid iid, out IDWriteFactory factory);
    [ComImport, Guid("b859ee5a-d838-4b5b-a2e8-1adc7d93db48"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDWriteFactory { void GetSystemFontCollection(out IDWriteFontCollection collection, [MarshalAs(UnmanagedType.Bool)] bool checkForUpdates); }
    [ComImport, Guid("a84cee02-3eea-4eee-a827-87c1a02a0fcc"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDWriteFontCollection
    {
        [PreserveSig] uint GetFontFamilyCount();
        void GetFontFamily(uint index, out IDWriteFontFamily family);
    }
    [ComImport, Guid("da20d8ef-812a-4c43-9802-62ec4abd7add"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDWriteFontFamily
    {
        void GetFontCollection(out IDWriteFontCollection collection);
        [PreserveSig] uint GetFontCount();
        void GetFont(uint index, out nint font);
        void GetFamilyNames(out IDWriteLocalizedStrings names);
    }
    [ComImport, Guid("08256209-099a-4b34-b86d-c22b110e7771"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDWriteLocalizedStrings
    {
        [PreserveSig] uint GetCount();
        void FindLocaleName([MarshalAs(UnmanagedType.LPWStr)] string name, out uint index, [MarshalAs(UnmanagedType.Bool)] out bool exists);
        void GetLocaleNameLength(uint index, out uint length);
        void GetLocaleName(uint index, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, uint size);
        void GetStringLength(uint index, out uint length);
        void GetString(uint index, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, uint size);
    }
}
