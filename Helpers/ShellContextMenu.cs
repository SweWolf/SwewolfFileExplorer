using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace SwewolfFileExplorer.Helpers;

/// <summary>
/// The real Windows context menu of files and folders (what Explorer shows with "Show more options"), with every shell extension
/// the computer has: 7-Zip, PowerRename, Open with, Send to ... Windows builds the menu (<c>IContextMenu</c>) and runs the chosen
/// command; this class only asks for it, takes out the items the app has its own versions of (the hide list in the settings:
/// Cut, Copy, Delete, Rename, Properties ...), shows it at the mouse and passes the click back.
///
/// It all happens on a thread of its own (STA, with a hidden window as the owner of the menu, which Windows sends the messages of
/// the lazily filled submenus to): a slow shell extension can make the menu late, never the window of the app freeze.
/// Files inside a compressed file are not on the disk and have no Windows menu.
///
/// The app never asks for Explorer's Shift menu (<c>extended</c>, <c>CMF_EXTENDEDVERBS</c>): for a folder it makes Send to list every folder of the
/// user profile too (.nuget, .claude ... 23 lines on this PC), and the lines it adds (Copy as path) the app has itself. The parameter is for tests.
/// </summary>
public static class ShellContextMenu
{
    /// <summary>One line of the menu, as the tests and the hide list see it.</summary>
    public readonly record struct Entry(string Text, string Verb, bool HasSubmenu);

    // ── What the app shows ───────────────────────────────────────────────────

    /// <summary>
    /// Shows the Windows menu of <paramref name="paths"/> (all in the same folder) at the mouse. <paramref name="finished"/> is
    /// called on the UI thread when the menu is closed and the chosen command has been started: null if all went well,
    /// otherwise what went wrong in words.
    /// </summary>
    public static void ShowFor(IReadOnlyList<string> paths, Action<string?>? finished = null, bool extended = false) =>
        Run(() => MenuSession.ForItems(paths), extended, finished);

    /// <summary>Shows the Windows menu of the empty part of <paramref name="folder"/>: New, and what the shell extensions add there.</summary>
    public static void ShowForBackground(string folder, Action<string?>? finished = null, bool extended = false) =>
        Run(() => MenuSession.ForBackground(folder), extended, finished);

    private static int _open;   // one Windows menu at a time: a second right-click while one is open is ignored

    private static void Run(Func<MenuSession> create, bool extended, Action<string?>? finished)
    {
        if (Interlocked.CompareExchange(ref _open, 1, 0) != 0) return;

        GetCursorPos(out var point);
        var hidden = ParseHidden(AppSettingsStore.Current.HiddenShellMenuItems);
        bool dark = ThemeService.IsDark;
        var dispatcher = Application.Current.Dispatcher;

        var thread = new Thread(() =>
        {
            string? error = null;
            try
            {
                using var session = create();
                SetMenuTheme(dark);
                session.Build(hidden, extended);
                session.ShowAndRun(point);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Debug.WriteLine(ex);
            }
            Volatile.Write(ref _open, 0);
            if (finished != null) dispatcher.BeginInvoke(() => finished(error));
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
    }

    // ── For tests ────────────────────────────────────────────────────────────

    /// <summary>The top-level lines of the menu of <paramref name="paths"/>, without showing it. With <paramref name="applyHideList"/> as the app shows it.</summary>
    public static IReadOnlyList<Entry> Describe(IReadOnlyList<string> paths, bool applyHideList, bool extended = false) =>
        OnSta(() =>
        {
            using var session = MenuSession.ForItems(paths);
            session.Build(applyHideList ? ParseHidden(AppSettingsStore.Current.HiddenShellMenuItems) : new HashSet<string>(), extended);
            return session.Entries();
        });

    /// <summary>The lines of one submenu ("Send to", "Open with") of the menu of <paramref name="paths"/>, filled the way Windows fills it when it opens.</summary>
    public static IReadOnlyList<string> Submenu(IReadOnlyList<string> paths, string menuText, bool extended) =>
        OnSta(() =>
        {
            using var session = MenuSession.ForItems(paths);
            session.Build(new HashSet<string>(), extended);
            return session.SubmenuTexts(menuText);
        });

    /// <summary>Same for the menu of the empty part of a folder.</summary>
    public static IReadOnlyList<Entry> DescribeBackground(string folder, bool applyHideList) =>
        OnSta(() =>
        {
            using var session = MenuSession.ForBackground(folder);
            session.Build(applyHideList ? ParseHidden(AppSettingsStore.Current.HiddenShellMenuItems) : new HashSet<string>(), false);
            return session.Entries();
        });

    /// <summary>Runs one command of the menu of <paramref name="paths"/> by its verb ("link", "open" ...) without showing the menu. False if there is none.</summary>
    public static bool InvokeVerb(IReadOnlyList<string> paths, string verb) =>
        OnSta(() =>
        {
            using var session = MenuSession.ForItems(paths);
            session.Build(new HashSet<string>(), false);
            return session.InvokeByVerb(verb);
        });

    private static T OnSta<T>(Func<T> work)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null) throw new InvalidOperationException(error.Message, error);
        return result;
    }

    // ── The hide list ────────────────────────────────────────────────────────

    /// <summary>The list of the settings ("cut;copy;Add to Favorites"): each is a verb or the text of an item, in any case.</summary>
    public static HashSet<string> ParseHidden(string? list) =>
        new((list ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);

    /// <summary>What the menu says without the shortcut key "&amp;", the key hint after a tab and a closing "...".</summary>
    private static string Plain(string text)
    {
        int tab = text.IndexOf('\t');
        if (tab >= 0) text = text[..tab];
        return text.Replace("&", "").TrimEnd('.', ' ', '…').Trim();
    }

    // ── Dark menu ────────────────────────────────────────────────────────────

    /// <summary>With a dark style the Windows menu is dark too (an undocumented function of uxtheme.dll, the one Explorer uses).</summary>
    private static void SetMenuTheme(bool dark)
    {
        try
        {
            var module = LoadLibrary("uxtheme.dll");
            if (module == IntPtr.Zero) return;
            var setMode = GetProcAddress(module, (IntPtr)135);   // SetPreferredAppMode(0 default, 1 allow dark, 2 force dark, 3 force light)
            var flush   = GetProcAddress(module, (IntPtr)136);   // FlushMenuThemes()
            if (setMode == IntPtr.Zero) return;

            Marshal.GetDelegateForFunctionPointer<SetPreferredAppModeFn>(setMode)(dark ? 2 : 3);
            if (flush != IntPtr.Zero) Marshal.GetDelegateForFunctionPointer<FlushMenuThemesFn>(flush)();
        }
        catch { /* a light menu is fine */ }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int SetPreferredAppModeFn(int mode);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void FlushMenuThemesFn();

    // ── One menu ─────────────────────────────────────────────────────────────

    /// <summary>The COM objects and the menu of one use. Lives and dies on the menu's own thread.</summary>
    private sealed class MenuSession : IDisposable
    {
        private const uint IdFirst = 1, IdLast = 0x7FFF;
        private const uint CmfNormal = 0, CmfExtendedVerbs = 0x100;

        private readonly List<IntPtr> _pidlsToFree = new();
        private readonly HwndSource _host;
        private readonly string _directory;
        private IContextMenu? _menu;
        private IContextMenu2? _menu2;
        private IContextMenu3? _menu3;
        private IntPtr _hmenu;
        private object? _folder;       // the IShellFolder the menu came from (kept alive as long as the menu)

        private MenuSession(string directory)
        {
            _directory = directory;

            // The window that owns the menu: hidden, off screen. Windows sends the menu messages of the extensions to it.
            _host = new HwndSource(new HwndSourceParameters("SweWolfShellMenuHost")
            {
                WindowStyle = unchecked((int)0x80000000),   // WS_POPUP
                Width = 0, Height = 0, PositionX = -32000, PositionY = -32000,
            });
            _host.AddHook(WndProc);
        }

        public static MenuSession ForItems(IReadOnlyList<string> paths)
        {
            if (paths.Count == 0) throw new ArgumentException("Nothing is selected.");

            var session = new MenuSession(System.IO.Path.GetDirectoryName(paths[0].TrimEnd('\\', '/')) ?? "");
            try
            {
                var childPidls = new List<IntPtr>();
                IShellFolder? parent = null;
                foreach (var path in paths)
                {
                    var full = session.Parse(path);
                    var iid = typeof(IShellFolder).GUID;
                    Check(SHBindToParent(full, ref iid, out var folderPtr, out var last), "bind to the folder of " + path);
                    var folder = (IShellFolder)Marshal.GetObjectForIUnknown(folderPtr);
                    Marshal.Release(folderPtr);

                    if (parent == null) { parent = folder; session._folder = folder; }
                    else Marshal.ReleaseComObject(folder);   // the first item's folder is the parent of all: they share one folder
                    childPidls.Add(last);   // points into "full", which lives until the session ends
                }

                var cmIid = typeof(IContextMenu).GUID;
                Check(parent!.GetUIObjectOf(IntPtr.Zero, (uint)childPidls.Count, childPidls.ToArray(), ref cmIid, IntPtr.Zero, out var cmPtr),
                      "get the menu from Windows");
                session.Take(cmPtr);
                return session;
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }

        public static MenuSession ForBackground(string folder)
        {
            var session = new MenuSession(folder);
            try
            {
                var full = session.Parse(folder);
                var folderIid = typeof(IShellFolder).GUID;
                Check(SHBindToParent(full, ref folderIid, out var parentPtr, out var last), "bind to the folder");
                var parent = (IShellFolder)Marshal.GetObjectForIUnknown(parentPtr);
                Marshal.Release(parentPtr);

                // The folder itself (a drive has no parent shown the same way, but the same call works for it).
                Check(parent.BindToObject(last, IntPtr.Zero, ref folderIid, out var selfPtr), "open the folder");
                var self = (IShellFolder)Marshal.GetObjectForIUnknown(selfPtr);
                Marshal.Release(selfPtr);
                Marshal.ReleaseComObject(parent);
                session._folder = self;

                var cmIid = typeof(IContextMenu).GUID;
                Check(self.CreateViewObject(session._host.Handle, ref cmIid, out var cmPtr), "get the menu from Windows");
                session.Take(cmPtr);
                return session;
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }

        private IntPtr Parse(string path)
        {
            Check(SHParseDisplayName(path, IntPtr.Zero, out var pidl, 0, out _), "find " + path);
            _pidlsToFree.Add(pidl);
            return pidl;
        }

        private void Take(IntPtr menuPtr)
        {
            var obj = Marshal.GetObjectForIUnknown(menuPtr);
            Marshal.Release(menuPtr);
            _menu  = (IContextMenu)obj;
            _menu2 = obj as IContextMenu2;
            _menu3 = obj as IContextMenu3;
        }

        // ── Filling and cleaning the menu ────────────────────────────────────

        public void Build(HashSet<string> hidden, bool extended)
        {
            _hmenu = CreatePopupMenu();
            int hr = _menu!.QueryContextMenu(_hmenu, 0, IdFirst, IdLast, CmfNormal | (extended ? CmfExtendedVerbs : 0));
            Check(hr, "fill the menu");

            // Take out what the app has its own versions of (or what the user does not want to see).
            for (int i = GetMenuItemCount(_hmenu) - 1; i >= 0; i--)
            {
                var item = Read(i);
                if (item.IsSeparator || item.Id < IdFirst) continue;

                // A submenu line carries its menu handle in the id, not a command id: asking an extension for the verb of it
                // crashed the whole program (Google Drive's "Offline access", 2026-10-29).
                string verb = item.Submenu != IntPtr.Zero ? "" : VerbOf(item.Id);
                string text = Plain(item.Text);
                if ((verb.Length > 0 && hidden.Contains(verb)) || (text.Length > 0 && hidden.Contains(text)))
                    DeleteMenu(_hmenu, (uint)i, MfByPosition);
            }

            // No leading, trailing or double separators where lines were removed.
            bool previousSeparator = true;
            for (int i = 0; i < GetMenuItemCount(_hmenu); i++)
            {
                bool separator = Read(i).IsSeparator;
                if (separator && previousSeparator) { DeleteMenu(_hmenu, (uint)i, MfByPosition); i--; continue; }
                previousSeparator = separator;
            }
            while (GetMenuItemCount(_hmenu) > 0 && Read(GetMenuItemCount(_hmenu) - 1).IsSeparator)
                DeleteMenu(_hmenu, (uint)(GetMenuItemCount(_hmenu) - 1), MfByPosition);
        }

        public IReadOnlyList<Entry> Entries()
        {
            var list = new List<Entry>();
            for (int i = 0; i < GetMenuItemCount(_hmenu); i++)
            {
                var item = Read(i);
                if (item.IsSeparator) continue;
                list.Add(new Entry(Plain(item.Text), item.Id >= IdFirst && item.Submenu == IntPtr.Zero ? VerbOf(item.Id) : "", item.Submenu != IntPtr.Zero));
            }
            return list;
        }

        public IReadOnlyList<string> SubmenuTexts(string menuText)
        {
            const uint WM_INITMENUPOPUP = 0x117;
            for (int i = 0; i < GetMenuItemCount(_hmenu); i++)
            {
                var item = Read(i);
                if (item.Submenu == IntPtr.Zero || !string.Equals(Plain(item.Text), menuText, StringComparison.OrdinalIgnoreCase)) continue;

                // What Windows sends when the submenu opens: the extension fills it then.
                if (_menu3 != null) _menu3.HandleMenuMsg2(WM_INITMENUPOPUP, item.Submenu, (IntPtr)i, out _);
                else _menu2?.HandleMenuMsg(WM_INITMENUPOPUP, item.Submenu, (IntPtr)i);

                // Send to fills itself in the background: give it a moment, with the messages flowing.
                var frame = new System.Windows.Threading.DispatcherFrame();
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
                timer.Start();
                System.Windows.Threading.Dispatcher.PushFrame(frame);

                var texts = new List<string>();
                for (int j = 0; j < GetMenuItemCount(item.Submenu); j++)
                {
                    var sub = Read(item.Submenu, j);
                    if (!sub.IsSeparator) texts.Add(Plain(sub.Text));
                }
                return texts;
            }
            return Array.Empty<string>();
        }

        // ── Showing and running ──────────────────────────────────────────────

        public void ShowAndRun(POINT point)
        {
            if (GetMenuItemCount(_hmenu) == 0) return;

            SetForegroundWindow(_host.Handle);   // or the menu would not close when the user clicks elsewhere
            uint chosen = TrackPopupMenuEx(_hmenu, TpmReturnCmd | TpmRightButton, point.X, point.Y, _host.Handle, IntPtr.Zero);
            PostMessage(_host.Handle, 0, IntPtr.Zero, IntPtr.Zero);   // WM_NULL: the usual end of a popup menu

            if (chosen >= IdFirst) Invoke(chosen - IdFirst, point);
        }

        public bool InvokeByVerb(string verb)
        {
            for (int i = 0; i < GetMenuItemCount(_hmenu); i++)
            {
                var item = Read(i);
                if (item.IsSeparator || item.Id < IdFirst || item.Submenu != IntPtr.Zero) continue;
                if (!string.Equals(VerbOf(item.Id), verb, StringComparison.OrdinalIgnoreCase)) continue;

                Invoke(item.Id - IdFirst, new POINT());
                return true;
            }
            return false;
        }

        private void Invoke(uint offset, POINT point)
        {
            var directory = Marshal.StringToHGlobalUni(_directory);
            try
            {
                var info = new CMINVOKECOMMANDINFOEX
                {
                    cbSize       = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                    fMask        = CmicMaskUnicode | CmicMaskPtInvoke,
                    hwnd         = _host.Handle,
                    lpVerb       = (IntPtr)offset,    // MAKEINTRESOURCE: the number of the command, not a name
                    lpVerbW      = (IntPtr)offset,
                    lpDirectoryW = directory,
                    nShow        = 1,                 // SW_SHOWNORMAL
                    ptInvoke     = point,
                };
                Check(_menu!.InvokeCommand(ref info), "run the command");
            }
            finally
            {
                Marshal.FreeHGlobal(directory);
            }
        }

        // ── Messages of owner-drawn items and lazily filled submenus (Send to, Open with ...) ──

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_DRAWITEM = 0x2B, WM_MEASUREITEM = 0x2C, WM_INITMENUPOPUP = 0x117, WM_MENUCHAR = 0x120;
            if (msg is not (WM_DRAWITEM or WM_MEASUREITEM or WM_INITMENUPOPUP or WM_MENUCHAR)) return IntPtr.Zero;

            try
            {
                if (_menu3 != null)
                {
                    _menu3.HandleMenuMsg2((uint)msg, wParam, lParam, out var result);
                    handled = true;
                    return result;
                }
                if (_menu2 != null)
                {
                    _menu2.HandleMenuMsg((uint)msg, wParam, lParam);
                    handled = true;
                    return msg == WM_INITMENUPOPUP ? IntPtr.Zero : (IntPtr)1;
                }
            }
            catch { /* an extension that fails in a message must not end the menu */ }
            return IntPtr.Zero;
        }

        // ── Reading the menu ─────────────────────────────────────────────────

        private readonly record struct Item(bool IsSeparator, uint Id, IntPtr Submenu, string Text);

        private Item Read(int position) => Read(_hmenu, position);

        private static Item Read(IntPtr menu, int position)
        {
            const uint MIIM_ID = 0x2, MIIM_SUBMENU = 0x4, MIIM_STRING = 0x40, MIIM_FTYPE = 0x100;
            const uint MFT_SEPARATOR = 0x800;

            var buffer = Marshal.AllocHGlobal(1024 * 2);
            try
            {
                var info = new MENUITEMINFO
                {
                    cbSize     = (uint)Marshal.SizeOf<MENUITEMINFO>(),
                    fMask      = MIIM_ID | MIIM_SUBMENU | MIIM_STRING | MIIM_FTYPE,
                    dwTypeData = buffer,
                    cch        = 1023,
                };
                if (!GetMenuItemInfo(menu, (uint)position, true, ref info))
                    return new Item(false, 0, IntPtr.Zero, "");

                string text = (info.fType & MFT_SEPARATOR) != 0 ? "" : Marshal.PtrToStringUni(info.dwTypeData) ?? "";
                return new Item((info.fType & MFT_SEPARATOR) != 0, info.wID, info.hSubMenu, text);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private string VerbOf(uint id)
        {
            const uint GCS_VERBW = 4;
            var buffer = Marshal.AllocHGlobal(512 * 2);
            try
            {
                Marshal.WriteInt16(buffer, 0);
                int hr = _menu!.GetCommandString((UIntPtr)(id - IdFirst), GCS_VERBW, IntPtr.Zero, buffer, 512);
                return hr >= 0 ? Marshal.PtrToStringUni(buffer) ?? "" : "";
            }
            catch
            {
                return "";
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public void Dispose()
        {
            if (_hmenu != IntPtr.Zero) { DestroyMenu(_hmenu); _hmenu = IntPtr.Zero; }
            if (_menu != null) { Marshal.ReleaseComObject(_menu); _menu = null; _menu2 = null; _menu3 = null; }
            if (_folder != null) { Marshal.ReleaseComObject(_folder); _folder = null; }
            foreach (var pidl in _pidlsToFree) Marshal.FreeCoTaskMem(pidl);
            _pidlsToFree.Clear();
            _host.Dispose();
        }
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new InvalidOperationException($"Windows could not {what} (error 0x{hr:X8}).");
    }

    // ── Windows ──────────────────────────────────────────────────────────────

    private const uint MfByPosition = 0x400, TpmReturnCmd = 0x100, TpmRightButton = 0x2;
    private const int CmicMaskUnicode = 0x4000, CmicMaskPtInvoke = 0x20000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize, fMask;
        public IntPtr hwnd, lpVerb, lpParameters, lpDirectory;
        public int nShow, dwHotKey;
        public IntPtr hIcon, lpTitle, lpVerbW, lpParametersW, lpDirectoryW, lpTitleW;
        public POINT ptInvoke;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MENUITEMINFO
    {
        public uint cbSize, fMask, fType, fState, wID;
        public IntPtr hSubMenu, hbmpChecked, hbmpUnchecked;
        public UIntPtr dwItemData;
        public IntPtr dwTypeData;
        public uint cch;
        public IntPtr hbmpItem;
    }

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        [PreserveSig] int ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string name, out uint eaten, out IntPtr pidl, ref uint attributes);
        [PreserveSig] int EnumObjects(IntPtr hwnd, uint flags, out IntPtr enumerator);
        [PreserveSig] int BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr result);
        [PreserveSig] int BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr result);
        [PreserveSig] int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        [PreserveSig] int CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr result);
        [PreserveSig] int GetAttributesOf(uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IntPtr[] pidls, ref uint attributes);
        [PreserveSig] int GetUIObjectOf(IntPtr hwndOwner, uint count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] IntPtr[] pidls, ref Guid riid, IntPtr reserved, out IntPtr result);
        [PreserveSig] int GetDisplayNameOf(IntPtr pidl, uint flags, IntPtr name);
        [PreserveSig] int SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string name, uint flags, out IntPtr newPidl);
    }

    [ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint type, IntPtr reserved, IntPtr name, uint cchMax);
    }

    [ComImport, Guid("000214F4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu2
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint type, IntPtr reserved, IntPtr name, uint cchMax);
        [PreserveSig] int HandleMenuMsg(uint msg, IntPtr wParam, IntPtr lParam);
    }

    [ComImport, Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu3
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint flags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint type, IntPtr reserved, IntPtr name, uint cchMax);
        [PreserveSig] int HandleMenuMsg(uint msg, IntPtr wParam, IntPtr lParam);
        [PreserveSig] int HandleMenuMsg2(uint msg, IntPtr wParam, IntPtr lParam, out IntPtr result);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string name, IntPtr bindingContext, out IntPtr pidl, uint attributesIn, out uint attributesOut);

    [DllImport("shell32.dll")]
    private static extern int SHBindToParent(IntPtr pidl, ref Guid riid, out IntPtr parent, out IntPtr lastItem);

    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] private static extern int GetMenuItemCount(IntPtr menu);
    [DllImport("user32.dll")] private static extern bool DeleteMenu(IntPtr menu, uint position, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMenuItemInfoW")]
    private static extern bool GetMenuItemInfo(IntPtr menu, uint item, [MarshalAs(UnmanagedType.Bool)] bool byPosition, ref MENUITEMINFO info);
    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr owner, IntPtr parameters);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadLibrary(string name);
    [DllImport("kernel32.dll")] private static extern IntPtr GetProcAddress(IntPtr module, IntPtr ordinal);
}
