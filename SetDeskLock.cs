using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

// One executable, two roles:
//   SetDeskLock.exe "image path"   applies the image to the desktop and lock screen.
//   SetDeskLock.exe -Embedding     COM server that Explorer starts to show the menu item.
static class Program
{
    const string AppName = "Set as Lock and Home";
    const string SettingsKey = @"HKEY_CURRENT_USER\Software\SetDeskLock";
    const string CommandClsid = "7E3C2A14-9B6D-4F58-A1C7-2D84E6F05B93";
    const uint SPI_SETDESKWALLPAPER = 0x0014;
    const uint SPIF_UPDATEINIFILE = 0x01;
    const uint SPIF_SENDCHANGE = 0x02;
    const uint CLSCTX_LOCAL_SERVER = 4;
    const uint REGCLS_MULTIPLEUSE = 1;
    const int ServerIdleSeconds = 120;

    static long lastUsedTicks;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SystemParametersInfo(uint action, uint param, string value, uint flags);

    [DllImport("ole32.dll")]
    static extern int CoRegisterClassObject(ref Guid clsid, [MarshalAs(UnmanagedType.IUnknown)] object factory, uint context, uint flags, out uint cookie);

    [DllImport("ole32.dll")]
    static extern int CoRevokeClassObject(uint cookie);

    static int Main(string[] args)
    {
        if (args.Length >= 1 && args[0].TrimStart('-', '/').Equals("Embedding", StringComparison.OrdinalIgnoreCase))
            return RunComServer();
        return ApplyImage(args);
    }

    public static string MenuTitle()
    {
        switch (Microsoft.Win32.Registry.GetValue(SettingsKey, "Language", null) as string)
        {
            case "UA": return "Встановити як шпалери всюди";
            case "RU": return "Установить как обои везде";
            default: return AppName;
        }
    }

    public static void MarkUsed()
    {
        Interlocked.Exchange(ref lastUsedTicks, DateTime.UtcNow.Ticks);
    }

    static int RunComServer()
    {
        MarkUsed();
        Guid clsid = new Guid(CommandClsid);
        uint cookie;
        int hr = CoRegisterClassObject(ref clsid, new CommandFactory(), CLSCTX_LOCAL_SERVER, REGCLS_MULTIPLEUSE, out cookie);
        if (hr != 0)
            return hr;

        while (DateTime.UtcNow.Ticks - Interlocked.Read(ref lastUsedTicks) < TimeSpan.FromSeconds(ServerIdleSeconds).Ticks)
            Thread.Sleep(1000);

        CoRevokeClassObject(cookie);
        return 0;
    }

    static int ApplyImage(string[] args)
    {
        if (args.Length != 1 || !File.Exists(args[0]))
        {
            MessageBox.Show("Image file not found.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        string path = Path.GetFullPath(args[0]);
        string desktopError = Try(() => SetDesktop(path));
        string lockError = Try(() => SetLockScreen(path));

        if (desktopError == null && lockError == null)
            return 0;

        string message = path;
        message += "\n\nDesktop: " + (desktopError ?? "OK");
        message += "\nLock screen: " + (lockError ?? "OK");
        MessageBox.Show(message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        return 1;
    }

    static void SetDesktop(string path)
    {
        if (!SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, path, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    static void SetLockScreen(string path)
    {
        var open = Windows.Storage.StorageFile.GetFileFromPathAsync(path);
        Wait(open);
        Wait(Windows.System.UserProfile.LockScreen.SetImageFileAsync(open.GetResults()));
    }

    static void Wait(Windows.Foundation.IAsyncInfo operation)
    {
        while (operation.Status == Windows.Foundation.AsyncStatus.Started)
            Thread.Sleep(10);
        if (operation.Status != Windows.Foundation.AsyncStatus.Completed)
            throw operation.ErrorCode;
    }

    static string Try(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception e)
        {
            return e.Message;
        }
    }
}

[ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellItem
{
    void BindToHandler();
    void GetParent();
    [PreserveSig]
    int GetDisplayName(uint sigdn, [MarshalAs(UnmanagedType.LPWStr)] out string name);
    void GetAttributes();
    void Compare();
}

[ComImport, Guid("b63ea76d-1f85-456f-a19c-48159efa858b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellItemArray
{
    void BindToHandler();
    void GetPropertyStore();
    void GetPropertyDescriptionList();
    void GetAttributes();
    [PreserveSig]
    int GetCount(out uint count);
    [PreserveSig]
    int GetItemAt(uint index, out IShellItem item);
    void EnumItems();
}

[ComImport, Guid("a08ce4d0-fa25-44ab-b57c-c7b1c323e0b9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IExplorerCommand
{
    [PreserveSig]
    int GetTitle(IShellItemArray items, [MarshalAs(UnmanagedType.LPWStr)] out string title);
    [PreserveSig]
    int GetIcon(IShellItemArray items, [MarshalAs(UnmanagedType.LPWStr)] out string icon);
    [PreserveSig]
    int GetToolTip(IShellItemArray items, [MarshalAs(UnmanagedType.LPWStr)] out string tip);
    [PreserveSig]
    int GetCanonicalName(out Guid name);
    [PreserveSig]
    int GetState(IShellItemArray items, int okToBeSlow, out uint state);
    [PreserveSig]
    int Invoke(IShellItemArray items, IntPtr bindContext);
    [PreserveSig]
    int GetFlags(out uint flags);
    [PreserveSig]
    int EnumSubCommands(out IntPtr commands);
}

[ComImport, Guid("00000001-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IClassFactory
{
    [PreserveSig]
    int CreateInstance(IntPtr outer, ref Guid iid, out IntPtr instance);
    [PreserveSig]
    int LockServer(int lockServer);
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class CommandFactory : IClassFactory
{
    const int CLASS_E_NOAGGREGATION = unchecked((int)0x80040110);

    public int CreateInstance(IntPtr outer, ref Guid iid, out IntPtr instance)
    {
        instance = IntPtr.Zero;
        if (outer != IntPtr.Zero)
            return CLASS_E_NOAGGREGATION;
        Program.MarkUsed();
        IntPtr unknown = Marshal.GetIUnknownForObject(new SetDeskLockCommand());
        try
        {
            return Marshal.QueryInterface(unknown, ref iid, out instance);
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    public int LockServer(int lockServer)
    {
        Program.MarkUsed();
        return 0;
    }
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class SetDeskLockCommand : IExplorerCommand
{
    const int S_OK = 0;
    const int E_FAIL = unchecked((int)0x80004005);
    const int E_NOTIMPL = unchecked((int)0x80004001);
    const uint ECS_ENABLED = 0;
    const uint ECS_HIDDEN = 2;
    const uint ECF_DEFAULT = 0;
    const uint SIGDN_FILESYSPATH = 0x80058000;

    static readonly string ExePath = Process.GetCurrentProcess().MainModule.FileName;

    public int GetTitle(IShellItemArray items, out string title)
    {
        Program.MarkUsed();
        title = Program.MenuTitle();
        return S_OK;
    }

    public int GetIcon(IShellItemArray items, out string icon)
    {
        icon = Path.Combine(Path.GetDirectoryName(ExePath), UsesDarkTheme() ? "SetDeskLockDark.ico" : "SetDeskLock.ico");
        return S_OK;
    }

    static bool UsesDarkTheme()
    {
        object light = Microsoft.Win32.Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1);
        return light is int && (int)light == 0;
    }

    public int GetToolTip(IShellItemArray items, out string tip)
    {
        tip = null;
        return E_NOTIMPL;
    }

    public int GetCanonicalName(out Guid name)
    {
        name = typeof(SetDeskLockCommand).GUID;
        return S_OK;
    }

    public int GetState(IShellItemArray items, int okToBeSlow, out uint state)
    {
        Program.MarkUsed();
        uint count = 0;
        if (items != null)
            items.GetCount(out count);
        state = count > 1 ? ECS_HIDDEN : ECS_ENABLED;
        return S_OK;
    }

    public int Invoke(IShellItemArray items, IntPtr bindContext)
    {
        Program.MarkUsed();
        try
        {
            IShellItem item;
            string path;
            int hr = items.GetItemAt(0, out item);
            if (hr != S_OK)
                return hr;
            hr = item.GetDisplayName(SIGDN_FILESYSPATH, out path);
            if (hr != S_OK)
                return hr;
            Process.Start(ExePath, "\"" + path + "\"");
            return S_OK;
        }
        catch (Exception)
        {
            return E_FAIL;
        }
    }

    public int GetFlags(out uint flags)
    {
        flags = ECF_DEFAULT;
        return S_OK;
    }

    public int EnumSubCommands(out IntPtr commands)
    {
        commands = IntPtr.Zero;
        return E_NOTIMPL;
    }
}
