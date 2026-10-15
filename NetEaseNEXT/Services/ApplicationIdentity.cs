using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace NetEaseNEXT.Services;

internal static class ApplicationIdentity
{
    internal const string LegacyId = "NetEaseNEXT.Desktop";
    internal static bool IsPackaged { get; } = GetPackageFamilyName() != null;
    internal static string Id { get; } = GetPackageFamilyName() is { } family ? family + "!App" : LegacyId;
    private static string? GetPackageFamilyName()
    {
        try { return Windows.ApplicationModel.Package.Current.Id.FamilyName; }
        catch (InvalidOperationException) { return null; }
        catch (COMException) { return null; }
    }
    private static string _iconPath = "";
    internal static string RegisteredIconPath => _iconPath;
    internal static string ReadWindowIconPath(nint window)
    {
        var iid = typeof(IPropertyStore).GUID;
        Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(window, ref iid, out var store));
        try
        {
            var property = new PropertyKey { Format = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = 3 };
            store.GetValue(ref property, out var value);
            try { return value.Type == 31 ? Marshal.PtrToStringUni(value.Value) ?? "" : ""; }
            finally { PropVariantClear(ref value); }
        }
        finally { Marshal.FinalReleaseComObject(store); }
    }
    internal static string ResolveDisplayName()
    {
        var iid = typeof(IShellItem).GUID;
        Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName("shell:AppsFolder\\" + Id, nint.Zero, ref iid, out var item));
        try
        {
            item.GetDisplayName(0, out var name);
            try { return Marshal.PtrToStringUni(name) ?? ""; }
            finally { Marshal.FreeCoTaskMem(name); }
        }
        finally { Marshal.FinalReleaseComObject(item); }
    }
    internal static void Initialize()
    {
        _iconPath = ShellAppearance.IconPath();
        // MSIX supplies the process identity and the sole Start menu entry.
        if (IsPackaged) return;
        Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(Id));
        // Register the display identity for Windows shell/SMTC on unpackaged builds.
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + Id);
        key.SetValue("DisplayName", "NetEaseNEXT");
        key.SetValue("IconUri", _iconPath);
        key.SetValue("IconBackgroundColor", "0");
        EnsureShortcut(_iconPath);
        UpdatePinnedShortcuts(_iconPath);
    }
    internal static void BindWindow(nint window)
    {
        var iid = typeof(IPropertyStore).GUID;
        Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(window, ref iid, out var store));
        try
        {
            if (!IsPackaged)
            {
                SetString(store, 5, Id);
                SetString(store, 2, "\"" + Environment.ProcessPath + "\"");
                SetString(store, 4, "NetEaseNEXT");
            }
            SetString(store, 3, _iconPath);
            store.Commit();
        }
        finally { Marshal.FinalReleaseComObject(store); }
    }
    internal static void UpdateIcon(nint window, string path)
    {
        if (_iconPath == path) return;
        _iconPath = path;
        if (IsPackaged) { BindWindow(window); return; }
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + Id);
        key.SetValue("IconUri", path);
        BindWindow(window);
        EnsureShortcut(path);
        UpdatePinnedShortcuts(path);
        SHChangeNotify(0x00002000, 0x0005, path, nint.Zero);
    }
    private static void EnsureShortcut(string iconPath)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "NetEaseNEXT");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "NetEaseNEXT.lnk");
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(Environment.ProcessPath!); link.SetWorkingDirectory(AppContext.BaseDirectory);
            link.SetDescription("NetEaseNEXT"); link.SetIconLocation(iconPath, 0);
            SetString((IPropertyStore)link, 5, Id);
            ((IPropertyStore)link).Commit();
            ((System.Runtime.InteropServices.ComTypes.IPersistFile)link).Save(path, true);
            SHChangeNotify(0x00002000, 0x0005, path, nint.Zero);
        }
        finally { Marshal.FinalReleaseComObject(link); }
    }
    private static void UpdatePinnedShortcuts(string iconPath)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
        if (!Directory.Exists(directory)) return;
        foreach (var path in Directory.EnumerateFiles(directory, "*.lnk"))
        {
            var link = (IShellLinkW)new ShellLink();
            try
            {
                var file = (System.Runtime.InteropServices.ComTypes.IPersistFile)link;
                file.Load(path, 0);
                var property = new PropertyKey { Format = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = 5 };
                ((IPropertyStore)link).GetValue(ref property, out var value);
                bool ours;
                try { ours = value.Type == 31 && Marshal.PtrToStringUni(value.Value) == Id; }
                finally { PropVariantClear(ref value); }
                if (!ours) continue;
                // Preserve the pinned shortcut's launch target and arguments.
                link.SetIconLocation(iconPath, 0); file.Save(path, true);
                SHChangeNotify(0x00002000, 0x0005, path, nint.Zero);
            }
            catch (Exception ex) { Log.Write(ex); }
            finally { Marshal.FinalReleaseComObject(link); }
        }
    }
    private static void SetString(IPropertyStore store, uint id, string value)
    {
        var key = new PropertyKey { Format = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), Id = id };
        var variant = new PropVariant { Type = 31, Value = Marshal.StringToCoTaskMemUni(value) };
        try { store.SetValue(ref key, ref variant); }
        finally { PropVariantClear(ref variant); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { public Guid Format; public uint Id; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] private struct PropVariant { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public nint Value; }
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")] private class ShellLink { }
    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(nint context, ref Guid handler, ref Guid iid, out nint value);
        void GetParent(out IShellItem parent); void GetDisplayName(uint mode, out nint name);
        void GetAttributes(uint mask, out uint attributes); void Compare(IShellItem other, uint hint, out int order);
    }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count); void GetAt(uint index, out PropertyKey key); void GetValue(ref PropertyKey key, out PropVariant value);
        void SetValue(ref PropertyKey key, ref PropVariant value); void Commit();
    }
    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath(nint path, int count, nint findData, uint flags); void GetIDList(out nint pidl); void SetIDList(nint pidl);
        void GetDescription(nint value, int count); void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string value);
        void GetWorkingDirectory(nint value, int count); void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string value);
        void GetArguments(nint value, int count); void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string value);
        void GetHotkey(out ushort key); void SetHotkey(ushort key); void GetShowCmd(out int command); void SetShowCmd(int command);
        void GetIconLocation(nint value, int count, out int index); void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string value, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string value, uint reserved); void Resolve(nint window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string value);
    }
    [DllImport("shell32.dll")] private static extern int SHGetPropertyStoreForWindow(nint window, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHCreateItemFromParsingName(string name, nint context, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern void SHChangeNotify(uint id, uint flags, string path, nint item2);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant variant);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SetCurrentProcessExplicitAppUserModelID(string id);
}
