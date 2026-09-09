using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace VsMcp.Extension.Tools;

internal static class NativeMethods
{
    // Struct types used as P/Invoke parameters. Kata の Extract Class (v2.1.6) は
    // nested type 移動をサポートしないので手動で UiTools から移設。 private → internal
    // に昇格しないと同一 assembly でも UiTools 側から使えない。
    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT
    {
        public int type;
        public INPUTUNION union;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct INPUTUNION
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll")]
internal static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);
    [DllImport("user32.dll")]
internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")]
internal static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")]
internal static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")]
internal static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")]
internal static extern bool BlockInput(bool fBlockIt);
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
internal static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
internal static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
internal static extern IntPtr WindowFromPoint(POINT Point);
    [DllImport("user32.dll")]
internal static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);
    [DllImport("user32.dll", SetLastError = true)]
internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")]
internal static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);
    [DllImport("user32.dll")]
internal static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);
    [DllImport("user32.dll")]
internal static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)]
internal static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")]
internal static extern short VkKeyScan(char ch);
    [DllImport("user32.dll")]
internal static extern uint MapVirtualKey(uint uCode, uint uMapType);

    // Phase 2 (manual): Win32 constants and lookup tables moved from UiTools.
    // internal 昇格 + using static VsMcp.Extension.Tools.NativeMethods; で UiTools 側の
    // 呼び出し (WM_MOUSEWHEEL, MOUSEEVENTF_LEFTDOWN, VK_RETURN, NamedKeys など) は無改変で通る。

    // Windows messages
    internal const uint WM_MOUSEWHEEL = 0x020A;
    internal const uint WM_MOUSEHWHEEL = 0x020E;
    internal const uint WM_LBUTTONDOWN = 0x0201;
    internal const uint WM_LBUTTONUP = 0x0202;
    internal const uint WM_RBUTTONDOWN = 0x0204;
    internal const uint WM_RBUTTONUP = 0x0205;
    internal const uint WM_LBUTTONDBLCLK = 0x0203;

    // Mouse button flags for wParam
    internal const ushort MK_LBUTTON = 0x0001;
    internal const ushort MK_RBUTTON = 0x0002;

    // DPI awareness context (Per-Monitor v2)
    internal static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = (IntPtr)(-4);

    // mouse_event flags
    internal const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    internal const uint MOUSEEVENTF_LEFTUP = 0x0004;
    internal const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    internal const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    internal const uint MOUSEEVENTF_WHEEL = 0x0800;
    internal const uint MOUSEEVENTF_HWHEEL = 0x01000;
    internal const int WHEEL_DELTA = 120;
    internal const uint PW_RENDERFULLCONTENT = 0x00000002;

    // SendInput / keyboard event flags
    internal const int INPUT_KEYBOARD = 1;
    internal const uint KEYEVENTF_KEYUP = 0x0002;
    internal const uint KEYEVENTF_SCANCODE = 0x0008;
    internal const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    internal const uint MAPVK_VK_TO_VSC = 0;

    // Virtual key codes
    internal const ushort VK_SHIFT = 0x10;
    internal const ushort VK_CONTROL = 0x11;
    internal const ushort VK_MENU = 0x12; // Alt
    internal const ushort VK_LWIN = 0x5B;
    internal const ushort VK_RETURN = 0x0D;
    internal const ushort VK_ESCAPE = 0x1B;
    internal const ushort VK_TAB = 0x09;
    internal const ushort VK_BACK = 0x08;
    internal const ushort VK_DELETE = 0x2E;
    internal const ushort VK_INSERT = 0x2D;
    internal const ushort VK_HOME = 0x24;
    internal const ushort VK_END = 0x23;
    internal const ushort VK_PRIOR = 0x21; // PageUp
    internal const ushort VK_NEXT = 0x22;  // PageDown
    internal const ushort VK_UP = 0x26;
    internal const ushort VK_DOWN = 0x28;
    internal const ushort VK_LEFT = 0x25;
    internal const ushort VK_RIGHT = 0x27;
    internal const ushort VK_SPACE = 0x20;
    internal const ushort VK_F1 = 0x70;

    // Named-key lookup table (input string → VK code). Case-insensitive.
    internal static readonly Dictionary<string, ushort> NamedKeys = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
    {
        { "enter", VK_RETURN }, { "return", VK_RETURN },
        { "esc", VK_ESCAPE }, { "escape", VK_ESCAPE },
        { "tab", VK_TAB },
        { "backspace", VK_BACK }, { "bs", VK_BACK },
        { "delete", VK_DELETE }, { "del", VK_DELETE },
        { "insert", VK_INSERT }, { "ins", VK_INSERT },
        { "home", VK_HOME }, { "end", VK_END },
        { "pageup", VK_PRIOR }, { "pgup", VK_PRIOR },
        { "pagedown", VK_NEXT }, { "pgdn", VK_NEXT },
        { "up", VK_UP }, { "down", VK_DOWN },
        { "left", VK_LEFT }, { "right", VK_RIGHT },
        { "space", VK_SPACE },
        { "f1", VK_F1 }, { "f2", (ushort)(VK_F1 + 1) },
        { "f3", (ushort)(VK_F1 + 2) }, { "f4", (ushort)(VK_F1 + 3) },
        { "f5", (ushort)(VK_F1 + 4) }, { "f6", (ushort)(VK_F1 + 5) },
        { "f7", (ushort)(VK_F1 + 6) }, { "f8", (ushort)(VK_F1 + 7) },
        { "f9", (ushort)(VK_F1 + 8) }, { "f10", (ushort)(VK_F1 + 9) },
        { "f11", (ushort)(VK_F1 + 10) }, { "f12", (ushort)(VK_F1 + 11) },
    };
    // ------------------------------------------------------------------
    // Extended additions (feature/modal-window-capture):
    // デバッグ対象プロセスのトップレベルウィンドウ列挙（DebuggeeWindowEnumerator）で使う Win32 API。
    // upstream 由来の既存宣言（GetWindowRect / GetWindowThreadProcessId / GetDpiForWindow /
    // SetThreadDpiAwarenessContext）はそのまま再利用し、ここでは重複宣言しない。
    // ------------------------------------------------------------------

    /// <summary>EnumWindows のコールバック。false を返すと列挙を打ち切る。</summary>
    internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowEnabled(IntPtr hWnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetWindowTextLengthW(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetClassNameW(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    internal static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);
    [DllImport("user32.dll")]
    internal static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    /// <summary>GetMonitorInfoW 用。szDevice にモニターのデバイス名（例: \\.\DISPLAY1）が入る。</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    // GetWindow / GetAncestor / MonitorFromWindow の引数定数
    internal const uint GW_OWNER = 4;
    internal const uint GA_ROOT = 2;
    internal const uint MONITOR_DEFAULTTONEAREST = 2;

    // ------------------------------------------------------------------
    // Extended additions (Phase 4): アクティブウィンドウ解決（DebuggeeWindowResolver）で使う GetGUIThreadInfo。
    // フォアグラウンドが Visual Studio 等に移っていても、デバッグ対象 GUI スレッドのアクティブウィンドウを得るために使う。
    // ------------------------------------------------------------------

    /// <summary>GetGUIThreadInfo の出力。hwndActive がそのスレッドのアクティブウィンドウ（スレッドがフォアグラウンドでなくても保持される）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct GUITHREADINFO
    {
        public uint cbSize;
        public uint flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    // ------------------------------------------------------------------
    // Extended additions (Phase 5): 標準ダイアログ（MessageBox / TaskDialog）の子コントロール構造の取得と、
    // 標準コントロール ID による操作（StandardDialogResolver / MessageBoxAdapter / TaskDialogAdapter）。
    // SendMessageTimeoutW は、ブレークポイントで停止中のデバッグ対象へ送っても Router のタイムアウトまで固まらないために使う。
    // ------------------------------------------------------------------

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")]
    internal static extern int GetDlgCtrlID(IntPtr hWnd);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetWindowLongW(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    internal const uint GA_PARENT = 1;
    internal const int GWL_STYLE = -16;
    internal const int BS_TYPEMASK = 0x0F;
    internal const int BS_DEFPUSHBUTTON = 0x01;
    internal const uint BM_GETCHECK = 0x00F0;
    internal const uint BM_CLICK = 0x00F5;
    internal const uint BST_CHECKED = 0x0001;
    internal const uint WM_COMMAND = 0x0111;
    internal const uint DM_GETDEFID = 0x0400;
    internal const uint DC_HASDEFID = 0x534B;
    internal const uint TDM_CLICK_BUTTON = 0x0400 + 102;
    internal const uint SMTO_BLOCK = 0x0001;
    internal const uint SMTO_ABORTIFHUNG = 0x0002;

    // ------------------------------------------------------------------
    // Extended additions (Phase 6): 標準 File Dialog（IFileDialog）のファイル名 Edit / 種類 ComboBox を
    // 他プロセスから読み書きするための WM_GETTEXT / WM_SETTEXT / CB_* 用 SendMessageTimeoutW オーバーロード。
    // ------------------------------------------------------------------

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SendMessageTimeoutW")]
    internal static extern IntPtr SendMessageTimeoutText(IntPtr hWnd, uint Msg, IntPtr wParam, System.Text.StringBuilder lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SendMessageTimeoutW")]
    internal static extern IntPtr SendMessageTimeoutSetText(IntPtr hWnd, uint Msg, IntPtr wParam, string lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    internal const uint WM_SETTEXT = 0x000C;
    internal const uint WM_GETTEXT = 0x000D;
    internal const uint WM_GETTEXTLENGTH = 0x000E;
    internal const uint CB_GETCURSEL = 0x0147;
    internal const uint CB_GETLBTEXT = 0x0148;
    internal const uint CB_GETLBTEXTLEN = 0x0149;

    // ------------------------------------------------------------------
    // Extended additions (Phase 8): ポップアップメニュー（Win32 #32768 / WPF ContextMenu）の構造取得
    // （UiPopupMenuResolver）で使う Win32 API。
    // 既存宣言（MonitorFromWindow / GetMonitorInfoW / GetDpiForWindow / WindowFromPoint / GetCursorPos /
    // SendMessageTimeoutW / GetWindowLongW）はそのまま再利用し、ここでは重複宣言しない。
    // メニューの作成系（CreatePopupMenu / AppendMenuW / TrackPopupMenuEx / DestroyMenu）は拡張には入れない
    // （検証アプリ側の fixture が持つ）。
    // ------------------------------------------------------------------

    [DllImport("user32.dll")]
    internal static extern int GetMenuItemCount(IntPtr hMenu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMenuItemInfoW(IntPtr hMenu, uint uItem, [MarshalAs(UnmanagedType.Bool)] bool fByPosition, ref MENUITEMINFOW lpmii);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetMenuStringW(IntPtr hMenu, uint uIDItem, System.Text.StringBuilder lpString, int cchMax, uint flags);

    /// <summary>
    /// GetMenuItemInfoW 用。fMask で要求した項目だけが設定される。
    /// dwTypeData は文字列を受け取らない使い方（MIIM_STRING で長さのみ）を想定して IntPtr で宣言し、
    /// 表示文字列は GetMenuStringW で取得する。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MENUITEMINFOW
    {
        public uint cbSize;
        public uint fMask;
        public uint fType;
        public uint fState;
        public uint wID;
        public IntPtr hSubMenu;
        public IntPtr hbmpChecked;
        public IntPtr hbmpUnchecked;
        public IntPtr dwItemData;
        public IntPtr dwTypeData;
        public uint cch;
        public IntPtr hbmpItem;
    }

    // ポップアップメニューウィンドウ（#32768）から HMENU を得るメッセージ
    internal const uint MN_GETHMENU = 0x01E1;

    // GetMenuItemInfoW の fMask
    internal const uint MIIM_STATE = 0x00000001;
    internal const uint MIIM_ID = 0x00000002;
    internal const uint MIIM_SUBMENU = 0x00000004;
    internal const uint MIIM_STRING = 0x00000040;
    internal const uint MIIM_FTYPE = 0x00000100;

    // GetMenuItemInfoW の fType: セパレータ（UIA の MenuItem には現れない）
    internal const uint MFT_SEPARATOR = 0x00000800;

    // GetMenuItemInfoW の fState（MFS_DISABLED は MFS_GRAYED を含む）
    internal const uint MFS_DISABLED = 0x00000003;
    internal const uint MFS_CHECKED = 0x00000008;

    // GetMenuStringW の flags（位置指定）
    internal const uint MF_BYPOSITION = 0x00000400;

    // MONITORINFOEX.dwFlags: プライマリモニター
    internal const uint MONITORINFOF_PRIMARY = 0x00000001;

    // GetWindowLongW の nIndex と拡張スタイル
    internal const int GWL_EXSTYLE = -20;
    internal const int WS_EX_TOOLWINDOW = 0x00000080;
}
