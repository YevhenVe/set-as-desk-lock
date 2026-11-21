using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

// One executable, three roles:
//   SetDeskLock.exe "image path"   applies the image to the desktop and lock screen.
//   SetDeskLock.exe -Embedding     COM server that Explorer starts to show the menu item.
//   SetDeskLock.exe                background Ctrl+B hotkey, started at sign-in.
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

    // Keep in sync with the ItemType list in AppxManifest.xml.
    static readonly string[] ImageExtensions =
        { ".jpg", ".jpeg", ".jfif", ".jpe", ".png", ".bmp", ".gif", ".webp", ".heic", ".heif", ".avif", ".tif", ".tiff" };

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SystemParametersInfo(uint action, uint param, string value, uint flags);

    [DllImport("ole32.dll")]
    static extern int CoRegisterClassObject(ref Guid clsid, [MarshalAs(UnmanagedType.IUnknown)] object factory, uint context, uint flags, out uint cookie);

    [DllImport("ole32.dll")]
    static extern int CoRevokeClassObject(uint cookie);

    static int Main(string[] args)
    {
        if (args.Length == 0)
            return Hotkey.Run();
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

    public static bool IsImage(string path)
    {
        string extension = Path.GetExtension(path);
        return Array.Exists(ImageExtensions, e => e.Equals(extension, StringComparison.OrdinalIgnoreCase));
    }

    static int ApplyImage(string[] args)
    {
        if (args.Length != 1 || !File.Exists(args[0]))
        {
            MessageBox.Show("Image file not found.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        return Apply(Path.GetFullPath(args[0]));
    }

    public static int Apply(string path)
    {
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

// Ctrl+B in File Explorer or on the desktop applies the one selected image, silently like Windows does.
// Ctrl+B in every other window passes through untouched.
static class Hotkey
{
    const int WH_KEYBOARD_LL = 13;
    const int WM_KEYDOWN = 0x0100;
    const int VK_B = 0x42;
    const int VK_SHIFT = 0x10;
    const int VK_CONTROL = 0x11;
    const int VK_MENU = 0x12;
    const int VK_LWIN = 0x5B;
    const int VK_RWIN = 0x5C;
    const int CSIDL_DESKTOP = 0;
    const int SWC_DESKTOP = 8;
    const int SWFO_NEEDDISPATCH = 1;
    static readonly Guid SID_STopLevelBrowser = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
    static readonly Guid IID_IOleWindow = new Guid("00000114-0000-0000-C000-000000000046");
    static readonly Guid CLSID_ShellWindows = new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39");

    delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);

    static readonly HookProc callback = OnKey;
    static IntPtr hook;
    static int busy;

    [StructLayout(LayoutKind.Sequential)]
    struct KeyInfo
    {
        public int VirtualKey;
        public int ScanCode;
        public int Flags;
        public int Time;
        public IntPtr Extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct GuiThreadInfo
    {
        public int Size;
        public int Flags;
        public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public Rect CaretRect;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int id, HookProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string name);

    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr window, System.Text.StringBuilder name, int size);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string title);

    public static int Run()
    {
        bool first;
        using (new Mutex(true, @"Local\SetDeskLockHotkey", out first))
        {
            if (!first)
                return 0;
            hook = SetWindowsHookEx(WH_KEYBOARD_LL, callback, GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero)
                return Marshal.GetLastWin32Error();
            Application.Run();
            return 0;
        }
    }

    static IntPtr OnKey(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && message == (IntPtr)WM_KEYDOWN)
        {
            KeyInfo key = (KeyInfo)Marshal.PtrToStructure(data, typeof(KeyInfo));
            if (key.VirtualKey == VK_B && IsDown(VK_CONTROL) &&
                !IsDown(VK_SHIFT) && !IsDown(VK_MENU) && !IsDown(VK_LWIN) && !IsDown(VK_RWIN))
            {
                IntPtr window = GetForegroundWindow();
                string className = ClassOf(window);
                bool folder = className == "CabinetWClass" || className == "ExploreWClass";
                bool desktop = className == "Progman" || className == "WorkerW";
                if ((folder || desktop) && !IsEditingText(window))
                {
                    // Reading the selection is too slow for a keyboard hook, so it runs on its own thread.
                    if (Interlocked.CompareExchange(ref busy, 1, 0) == 0)
                    {
                        Thread worker = new Thread(() =>
                        {
                            try { ApplySelection(window, desktop); }
                            finally { Interlocked.Exchange(ref busy, 0); }
                        });
                        worker.SetApartmentState(ApartmentState.STA);
                        worker.IsBackground = true;
                        worker.Start();
                    }
                    return (IntPtr)1;
                }
            }
        }
        return CallNextHookEx(hook, code, message, data);
    }

    static bool IsDown(int key)
    {
        return (GetAsyncKeyState(key) & 0x8000) != 0;
    }

    static string ClassOf(IntPtr window)
    {
        var name = new System.Text.StringBuilder(64);
        GetClassName(window, name, name.Capacity);
        return name.ToString();
    }

    // Renaming a file uses an Edit box; Ctrl+B there belongs to the text.
    static bool IsEditingText(IntPtr window)
    {
        uint processId;
        var info = new GuiThreadInfo();
        info.Size = Marshal.SizeOf(typeof(GuiThreadInfo));
        if (!GetGUIThreadInfo(GetWindowThreadProcessId(window, out processId), ref info))
            return false;
        return ClassOf(info.Focus) == "Edit";
    }

    static void ApplySelection(IntPtr window, bool desktop)
    {
        try
        {
            string path = desktop ? DesktopSelection() : FolderSelection(window);
            if (path != null && Program.IsImage(path) && File.Exists(path))
                Program.Apply(path);
        }
        catch (Exception)
        {
            // Explorer windows can close or navigate while we read them; Ctrl+B then does nothing.
        }
    }

    static string FolderSelection(IntPtr window)
    {
        // With tabs, every tab is a separate shell window that shares the frame; the first tab child is the active one.
        IntPtr activeTab = FindWindowEx(window, IntPtr.Zero, "ShellTabWindowClass", null);
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
        foreach (object item in shell.Windows())
        {
            try
            {
                dynamic browser = item;
                if (Convert.ToInt64(browser.HWND) != window.ToInt64())
                    continue;
                if (activeTab != IntPtr.Zero && BrowserWindow(item) != activeTab)
                    continue;
                return SingleSelection(browser.Document);
            }
            catch (Exception)
            {
            }
        }
        return null;
    }

    static string DesktopSelection()
    {
        var windows = (IShellWindows)Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_ShellWindows));
        object location = CSIDL_DESKTOP;
        object root = null;
        int handle;
        dynamic desktop = windows.FindWindowSW(ref location, ref root, SWC_DESKTOP, out handle, SWFO_NEEDDISPATCH);
        return SingleSelection(desktop.Document);
    }

    static string SingleSelection(dynamic view)
    {
        dynamic items = view.SelectedItems();
        if (items.Count != 1)
            return null;
        return items.Item(0).Path as string;
    }

    static IntPtr BrowserWindow(object browser)
    {
        var services = browser as IComServiceProvider;
        if (services == null)
            return IntPtr.Zero;
        Guid service = SID_STopLevelBrowser;
        Guid iid = IID_IOleWindow;
        IntPtr pointer;
        if (services.QueryService(ref service, ref iid, out pointer) != 0)
            return IntPtr.Zero;
        try
        {
            IntPtr handle;
            ((IOleWindow)Marshal.GetObjectForIUnknown(pointer)).GetWindow(out handle);
            return handle;
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }
}

[ComImport, Guid("6d5140c1-7436-11ce-8034-00aa006009fa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IComServiceProvider
{
    [PreserveSig]
    int QueryService(ref Guid service, ref Guid iid, out IntPtr instance);
}

[ComImport, Guid("00000114-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IOleWindow
{
    void GetWindow(out IntPtr window);
    void ContextSensitiveHelp(bool enterMode);
}

[ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
interface IShellWindows
{
    // Unused methods keep their vtable slots so FindWindowSW lands in the right place.
    void get_Count();
    void Item();
    void _NewEnum();
    void Register();
    void RegisterPending();
    void Revoke();
    void OnNavigate();
    void OnActivated();
    [return: MarshalAs(UnmanagedType.IDispatch)]
    object FindWindowSW([In, MarshalAs(UnmanagedType.Struct)] ref object location,
        [In, MarshalAs(UnmanagedType.Struct)] ref object root, int windowClass, out int window, int options);
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
