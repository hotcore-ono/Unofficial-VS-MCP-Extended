using System.Runtime.InteropServices;

namespace WindowListSample
{
    /// <summary>
    /// Win32 ポップアップメニュー（CreatePopupMenu / TrackPopupMenuEx）の最小 P/Invoke（ui_menu_* の検証用）。
    /// WPF の ContextMenu と違い、実体は ClassName "#32768" のトップレベルウィンドウとして現れるため、
    /// win32Menu 分類・HMENU 経由のコマンド ID 取得の fixture として使う。
    /// </summary>
    internal static class Win32MenuInterop
    {
        /// <summary>MF_STRING: 文字列項目として追加する。</summary>
        private const uint _MF_STRING = 0x00000000;

        /// <summary>MF_GRAYED: 灰色表示の無効項目として追加する。</summary>
        private const uint _MF_GRAYED = 0x00000001;

        /// <summary>MF_POPUP: サブメニューを開く項目として追加する（uIDNewItem にサブメニューの HMENU を渡す）。</summary>
        private const uint _MF_POPUP = 0x00000010;

        /// <summary>MF_SEPARATOR: 区切り線を追加する（コマンド ID と文字列は持たない）。</summary>
        private const uint _MF_SEPARATOR = 0x00000800;

        /// <summary>TPM_LEFTALIGN: 指定した X 座標をメニューの左端に合わせる。</summary>
        private const uint _TPM_LEFTALIGN = 0x00000000;

        /// <summary>TPM_TOPALIGN: 指定した Y 座標をメニューの上端に合わせる。</summary>
        private const uint _TPM_TOPALIGN = 0x00000000;

        /// <summary>TPM_RETURNCMD: 選択された項目のコマンド ID を戻り値として返す。</summary>
        private const uint _TPM_RETURNCMD = 0x00000100;

        /// <summary>「Win32 Item A」のコマンド ID。</summary>
        private const int _ITEM_A_COMMAND_ID = 1001;

        /// <summary>「Win32 Item B」のコマンド ID。</summary>
        private const int _ITEM_B_COMMAND_ID = 1002;

        /// <summary>サブメニュー内「Sub Item 1」のコマンド ID。</summary>
        private const int _SUB_ITEM_1_COMMAND_ID = 1101;

        /// <summary>サブメニュー内「Sub Item 2」のコマンド ID。</summary>
        private const int _SUB_ITEM_2_COMMAND_ID = 1102;

        /// <summary>無効項目「Disabled Item」のコマンド ID（MF_GRAYED のため選択されない）。</summary>
        private const int _DISABLED_ITEM_COMMAND_ID = 1201;

        /// <summary>空のポップアップメニューを作成する。</summary>
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CreatePopupMenu();

        /// <summary>メニューの末尾に項目を追加する（uIDNewItem は MF_POPUP のときサブメニューの HMENU、lpNewItem は MF_SEPARATOR のとき null）。</summary>
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, IntPtr uIDNewItem, string? lpNewItem);

        /// <summary>ポップアップメニューを表示し、閉じるまでモーダルループを回す。</summary>
        [DllImport("user32.dll", SetLastError = true)]
        private static extern int TrackPopupMenuEx(IntPtr hMenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

        /// <summary>メニューとそのサブメニューを破棄する。</summary>
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyMenu(IntPtr hMenu);

        /// <summary>指定ウィンドウを前面化する。</summary>
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        /// <summary>
        /// 「Win32 Item A」「Win32 Item B」「セパレータ」「Submenu（Sub Item 1 / Sub Item 2）」「Disabled Item」から成る
        /// ポップアップメニューを指定位置に表示する。TrackPopupMenuEx は呼び出しスレッド（UI スレッド）で
        /// メニューが閉じるまでモーダルループを回すため、戻るのは選択・キャンセルの後になる。
        /// </summary>
        /// <param name="InOwner">メニューを所有するウィンドウの HWND（TrackPopupMenuEx の hwnd）。</param>
        /// <param name="InX">メニュー左端のスクリーン物理 X 座標。</param>
        /// <param name="InY">メニュー上端のスクリーン物理 Y 座標。</param>
        /// <returns>選択された項目のコマンド ID。キャンセルされた場合は 0。</returns>
        public static int ShowPopupMenu(IntPtr InOwner, int InX, int InY)
        {
            IntPtr TheMenu = CreatePopupMenu();
            IntPtr TheSubmenu = CreatePopupMenu();
            try
            {
                AppendMenuW(TheMenu, _MF_STRING, (IntPtr)_ITEM_A_COMMAND_ID, "Win32 Item A");
                AppendMenuW(TheMenu, _MF_STRING, (IntPtr)_ITEM_B_COMMAND_ID, "Win32 Item B");
                // セパレータはコマンド ID を持たないため、UIA の項目列と Win32 の項目列で index がずれる。
                AppendMenuW(TheMenu, _MF_SEPARATOR, IntPtr.Zero, null);
                AppendMenuW(TheSubmenu, _MF_STRING, (IntPtr)_SUB_ITEM_1_COMMAND_ID, "Sub Item 1");
                AppendMenuW(TheSubmenu, _MF_STRING, (IntPtr)_SUB_ITEM_2_COMMAND_ID, "Sub Item 2");
                AppendMenuW(TheMenu, _MF_POPUP, TheSubmenu, "Submenu");
                AppendMenuW(TheMenu, _MF_STRING | _MF_GRAYED, (IntPtr)_DISABLED_ITEM_COMMAND_ID, "Disabled Item");

                // 所有ウィンドウが前面でないとメニューが正しく閉じない（TrackPopupMenuEx の要件）。
                SetForegroundWindow(InOwner);
                return TrackPopupMenuEx(TheMenu, _TPM_RETURNCMD | _TPM_LEFTALIGN | _TPM_TOPALIGN, InX, InY, InOwner, IntPtr.Zero);
            }
            finally
            {
                // DestroyMenu はサブメニューも再帰的に破棄するため、サブメニューを個別に破棄しない。
                DestroyMenu(TheMenu);
            }
        }
    }
}
