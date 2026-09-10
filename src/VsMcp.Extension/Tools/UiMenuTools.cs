using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using Newtonsoft.Json.Linq;
using VsMcp.Extension.McpServer;
using VsMcp.Extension.Services;
using VsMcp.Shared;
using VsMcp.Shared.Protocol;
using static VsMcp.Extension.Tools.NativeMethods;

namespace VsMcp.Extension.Tools
{
    /// <summary>
    /// Extended 独自の「デバッグ対象アプリのポップアップメニュー（Win32 #32768 / WPF ContextMenu）」自動化ツール群。
    /// 分類・項目一覧・項目解決・fingerprint は <see cref="UiPopupMenuResolver"/> に置き、ここでは引数の解釈・安全境界の適用・
    /// ポーリング・戻り値の組み立てだけを行う。実行順は Phase 7 と同じ安全境界
    /// （範囲 → IsWindow → GA_ROOT → デバッグ対象 PID → 再列挙と状態判定 → 項目解決 → 所属 HWND 一致 → enabled / offscreen / bounds → Invoke / 入力）。
    /// AutomationElement はツール呼び出し間で保持せず、1 回の STA 呼び出しの中だけで使う。
    /// </summary>
    public static class UiMenuTools
    {
        /// <summary>ui_menu_select が実行後にメニューの閉鎖を確認する既定時間（ミリ秒）。</summary>
        private const int _DEFAULT_CLOSE_WAIT_MS = 1000;

        /// <summary>ui_menu_select の closeWaitMs の上限（ミリ秒）。</summary>
        private const int _MAX_CLOSE_WAIT_MS = 10000;

        /// <summary>閉鎖確認のポーリング間隔（ミリ秒）。</summary>
        private const int _CLOSE_POLL_MS = 100;

        /// <summary>サブメニューが現れるのを待つ上限時間（ミリ秒）。</summary>
        private const int _SUBMENU_WAIT_TIMEOUT_MS = 1500;

        /// <summary>サブメニュー出現確認のポーリング間隔（ミリ秒）。</summary>
        private const int _SUBMENU_POLL_MS = 100;

        /// <summary>Extended (Phase 9b): サブメニュー候補を「展開の前には開いていなかった HWND」で見つけたことを表す検出方式。</summary>
        private const string _SUBMENU_DETECTION_NEW_HANDLE = "newHandle";

        /// <summary>
        /// Extended (Phase 9b): サブメニュー候補を「Owner が展開元のメニュー」で見つけたことを表す検出方式。
        /// WPF は 2 回目以降のサブメニューに前回と同じ HWND を再利用するため、新規 HWND の差分では見つからない。
        /// </summary>
        private const string _SUBMENU_DETECTION_OWNER_MATCH = "ownerMatch";

        /// <summary>1 回の検出で内容分類（UIA 照会）を試みるウィンドウ数の上限。前フィルタを通過した候補にだけ適用する。</summary>
        private const int _MAX_MENU_CANDIDATES = 20;

        /// <summary>実行方式: UIA InvokePattern。</summary>
        private const string _METHOD_INVOKE = "invokePattern";

        /// <summary>実行方式: UIA ExpandCollapsePattern（サブメニューを開く）。</summary>
        private const string _METHOD_EXPAND_COLLAPSE = "expandCollapse";

        /// <summary>実行方式: LegacyIAccessiblePattern.DoDefaultAction。</summary>
        private const string _METHOD_LEGACY = "legacyDoDefaultAction";

        /// <summary>実行方式: 座標ゲートを通した物理クリック。</summary>
        private const string _METHOD_PHYSICAL_CLICK = "physicalClick";

        /// <summary>Extended (Phase 9): 実行方式: UIA 要素が無い Win32 メニュー項目を GetMenuItemRect の矩形中心で物理クリック。</summary>
        private const string _METHOD_PHYSICAL_CLICK_WIN32_RECT = "physicalClickWin32Rect";

        /// <summary>Extended (Phase 9): ui_menu_close の method: ESC → 効かなければ outside click。</summary>
        private const string _CLOSE_METHOD_AUTO = "auto";

        /// <summary>Extended (Phase 9): ui_menu_close の method: 物理 ESC のみ。</summary>
        private const string _CLOSE_METHOD_ESCAPE = "escape";

        /// <summary>Extended (Phase 9): ui_menu_close の method: Owner 上の安全な点をクリックするのみ。</summary>
        private const string _CLOSE_METHOD_OUTSIDE_CLICK = "outsideClick";

        /// <summary>Extended (Phase 9): ui_menu_close の戻り値: どの方式でも閉じなかった（または呼び出し時点で既に閉じていた）。</summary>
        private const string _CLOSE_METHOD_NONE = "none";

        /// <summary>Extended (Phase 9): ui_menu_close の timeoutMs の既定値（ミリ秒）。</summary>
        private const int _DEFAULT_CLOSE_TIMEOUT_MS = 3000;

        /// <summary>Extended (Phase 9): ui_menu_select_path の timeoutMs の既定値（ミリ秒）。各段の submenu 待ちと最終の閉鎖確認に使う。</summary>
        private const int _DEFAULT_PATH_TIMEOUT_MS = 5000;

        /// <summary>Extended (Phase 9): timeoutMs の上限（ミリ秒。Phase 4 の wait 系と同じ）。</summary>
        private const int _MAX_WAIT_TIMEOUT_MS = 55000;

        /// <summary>Extended (Phase 9): pollIntervalMs の既定値（ミリ秒）。</summary>
        private const int _DEFAULT_MENU_POLL_MS = 100;

        /// <summary>Extended (Phase 9): pollIntervalMs の下限（ミリ秒。Phase 4 の wait 系と同じ）。</summary>
        private const int _MIN_POLL_INTERVAL_MS = 50;

        /// <summary>Extended (Phase 9): ui_menu_select_path が受け付ける path の段数の上限。</summary>
        private const int _MAX_PATH_SEGMENTS = 10;

        /// <summary>
        /// Extended (Phase 9b): ESC を 1 回送るごとに閉鎖を観測する時間（ミリ秒）。1 回の ESC は最前面のサブメニューだけを
        /// 閉じることがあるため、この時間だけ見てから再送する。
        /// </summary>
        private const int _ESCAPE_OBSERVE_MS = 400;

        /// <summary>Extended (Phase 9b): ESC を再送する回数の上限（初回の 1 回は含まない。入れ子のサブメニューを 1 段ずつ閉じるため）。</summary>
        private const int _MAX_ESCAPE_RESEND_COUNT = 3;

        /// <summary>
        /// Extended (Phase 9): 開いているメニューを列挙しきれなかったため outsideClick を行わないときの理由。
        /// 見えていないメニューの上をクリックして項目を選んでしまう危険があるので、この場合は点を決めずに拒否する。
        /// </summary>
        private const string _NOTE_MENUS_TRUNCATED = "menu candidates were truncated; outside click skipped";

        /// <summary>閉鎖理由: 呼び出し時点で既に HWND が無い。</summary>
        private const string _REASON_ALREADY_CLOSED = "alreadyClosed";

        /// <summary>閉鎖理由: HWND が破棄された。</summary>
        private const string _REASON_WINDOW_DESTROYED = "windowDestroyed";

        /// <summary>閉鎖理由: 同じ HWND 値が別プロセスに再利用された。</summary>
        private const string _REASON_HANDLE_REUSED = "handleReused";

        /// <summary>閉鎖理由: ウィンドウが非表示になった。</summary>
        private const string _REASON_HIDDEN = "hidden";

        /// <summary>閉鎖理由: 同じ HWND だが内容が別のメニューになった（メニューではなくなった場合を含む）。</summary>
        private const string _REASON_CONTENT_CHANGED = "contentChanged";

        /// <summary>ツールをレジストリへ登録する。VsMcpPackage.RegisterTools から呼ばれる。</summary>
        /// <param name="InRegistry">登録先レジストリ。</param>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        public static void Register(McpToolRegistry InRegistry, VsServiceAccessor InAccessor)
        {
            const string TheClassificationDescription =
                "A window counts as a menu by CONTENT, never by class name alone: 'win32Menu' is class '#32768' whose UI Automation root is a Menu or has MenuItem " +
                "children, 'wpfContextMenu' is a titleless 'HwndWrapper[...]' popup whose UIA subtree contains at least one MenuItem (a WPF submenu has no Menu " +
                "element at all: its UIA root is a Window with ClassName 'Popup'), and 'unknownPopupMenu' is any other visible owned/titleless tool window " +
                "(returned only with includeUnknown=true, with an empty 'items'). For a 'wpfContextMenu' only the first level of MenuItems is listed — while a " +
                "submenu is expanded its items also appear underneath the parent MenuItem in the parent popup's subtree, and those duplicates are excluded. " +
                "When the UIA root of a submenu popup exposes no items at all (WPF reuses the popup HWND from the second expansion on, and that root then reports " +
                "zero MenuItems), the items are resolved from the OWNER menu's subtree instead. That fallback is used only when the owner is itself a popup (a " +
                "titleless 'HwndWrapper[...]' window that has an owner of its own, so an overlay owned by the main window — e.g. the XAML runtime tools overlay shown " +
                "while debugging — is never taken for a menu) and the popup's rectangle is no wider and no taller than the owner's; otherwise the window stays " +
                "'unknownPopupMenu'. Only MenuItems that are not offscreen and whose bounding rectangle centre lies inside the popup's window rectangle are taken, the " +
                "owner must belong to the same process, and an item that repeats one already taken (same runtime id, or same automationId, name and bounds) is dropped " +
                "so a selector still matches a single item. 'itemsSource' reports where items[] came " +
                "from ('self' = the menu window's own subtree, 'ownerSubtree' = resolved from the owner menu), and in the latter case items[i].rootWindowHandle is the " +
                "owner menu, not 'handle'. ";

            // Extended (Phase 10): 登録は DiagnosticToolRunner を通し、tool.start / tool.end と相関 ID を付ける（schema・戻り値・エラー文は不変）
            DiagnosticToolRunner.Register(InRegistry,
                new McpToolDefinition(
                    "ui_menu_detect",
                    "[Windows UIA — desktop app being debugged] List the popup menus (context menus) currently open in the debugged processes. Every visible top-level " +
                    "window of the debugged processes is inspected by content on each call — the before/after HWND difference of ui_window_right_click is NOT used, " +
                    "because Windows reuses the HWND of a context menu that was just closed. " + TheClassificationDescription +
                    "Returns text { count, includeUnknown, inspectedCount, truncated, menus[] } where each menu has menuType, handle, handleHex, processId, " +
                    "ownerHandle, className, bounds, isVisible, isEnabled, isForeground, uiaRootName, uiaRootControlType, win32MenuHandle (HMENU of a '#32768' menu, " +
                    "0 when unknown), itemsSource ('self' or 'ownerSubtree') and items[] (id, name, automationId, controlType, isEnabled, isOffscreen, isChecked, " +
                    "hasSubmenu, bounds, rootWindowHandle, patterns, index). 'inspectedCount' is the number of windows that passed the cheap pre-filter (menu class, " +
                    "owned, or titleless) and were actually classified, and 'truncated' is true when more candidates were open than the inspection limit (20) so " +
                    "some were not classified. " +
                    "Values that cannot be observed are null and are never guessed. Use menus[i].handle with ui_menu_get_info / capture / select / wait_closed.",
                    SchemaBuilder.Create()
                        .AddInteger("ownerWindowHandle", "Only return menus owned by this HWND (decimal), or ownerless popups created by the same GUI thread")
                        .AddBoolean("includeUnknown", "Also return popups whose menu structure could not be identified (menuType 'unknownPopupMenu'). Default: false")
                        .Build()),
                InArgs => UiMenuDetectAsync(InAccessor, InArgs));

            DiagnosticToolRunner.Register(InRegistry,
                new McpToolDefinition(
                    "ui_menu_get_info",
                    "[Windows UIA — desktop app being debugged] Return the structured content of one popup menu of the debugged application by its HWND (from " +
                    "ui_menu_detect / ui_menu_wait / ui_window_right_click 'menuHandles'). The handle is re-validated on every call (range, IsWindow, normalization to " +
                    "the top-level window, debugged-process check) and the window is classified again from its current content. " + TheClassificationDescription +
                    "A window that is not a popup menu is an error. Same fields as one entry of ui_menu_detect 'menus'.",
                    SchemaBuilder.Create()
                        .AddInteger("handle", "HWND of the popup menu (decimal, as returned by ui_menu_detect 'handle')", required: true)
                        .Build()),
                InArgs => UiMenuGetInfoAsync(InAccessor, InArgs));

            DiagnosticToolRunner.Register(InRegistry,
                new McpToolDefinition(
                    "ui_menu_select",
                    "[Windows UIA — desktop app being debugged] Click one item of a popup menu of the debugged application. The item is selected by 'itemId' " +
                    "(Win32 command ID), 'automationId', 'name' (exact, also compared with the accelerator '&' removed) and/or 'index'. 'index' always means the " +
                    "'index' field of items[] (the position in menu order), never an ordinal inside the filtered candidates, so combining it with another criterion " +
                    "keeps the same meaning. At least one selector is required and several matches fail with the candidate list. Safety boundary (always enforced): " +
                    "the handle is re-validated and re-classified, the menu window must belong to a debugged process and must not be blocked by a modal dialog, the " +
                    "item must belong to that menu (an item whose top-level window cannot be resolved is refused) and a disabled item (isEnabled=false) is always " +
                    "refused — it never falls back to a physical click. Execution order: ExpandCollapsePattern for an item with a submenu, then InvokePattern, then " +
                    "LegacyIAccessible DoDefaultAction, then a physical click at the item center which is performed only if the point lies inside the menu rectangle " +
                    "and the window under it is the menu itself (the menu is never brought to the front, because that would dismiss it). When the item has a submenu, " +
                    "the menus are re-detected every 100 ms for up to 1500 ms and a visible menu with at least one item is returned as 'submenu' when its HWND was not " +
                    "open before the click (submenuDetection='newHandle') or when it is owned by the expanded menu although that HWND was already open " +
                    "(submenuDetection='ownerMatch') — WPF reuses the popup HWND of a submenu that was opened once, so from the second expansion on no new HWND appears " +
                    "at all; menus owned by the expanded menu are preferred and every candidate is listed in 'submenuCandidates'. If none appears, " +
                    "submenuOpened=false with a 'note' instead of an error. Before an item with a " +
                    "submenu is expanded the mouse cursor is moved onto that item (no click, the position is not restored) because a WPF menu follows the hover and " +
                    "would close the submenu again; the move is skipped — and reported in 'note' — when the window under that point is not the menu itself. Returns " +
                    "text { message, method, menuHandle, item, cursorMoved, menuClosed, closeWaitMs (time actually spent waiting), submenuOpened, submenu, " +
                    "submenuCandidates, submenuDetection (ownerMatch / newHandle / null), note }. For a nested path prefer ui_menu_select_path.",
                    SchemaBuilder.Create()
                        .AddInteger("handle", "HWND of the popup menu (decimal)", required: true)
                        .AddInteger("itemId", "Win32 command ID of the item (from items[].id; null — and therefore never matched — for WPF menus and for any item " +
                            "that opens a submenu, whose Win32 wID holds the submenu handle instead of a command ID)")
                        .AddString("automationId", "AutomationId of the item (exact)")
                        .AddString("name", "Name of the item (exact; the accelerator '&' is ignored)")
                        .AddInteger("index", "0-based position of the item in menu order (the 'index' field of items[]); usable alone or together with the other " +
                            "selectors, where it filters the candidates by items[].index instead of numbering them again")
                        .AddInteger("closeWaitMs", "How long to wait for the menu to close after the click (default: 1000, max: 10000)")
                        .Build()),
                InArgs => UiMenuSelectAsync(InAccessor, InArgs));

            DiagnosticToolRunner.Register(InRegistry,
                new McpToolDefinition(
                    "ui_menu_capture",
                    "[Windows UIA — desktop app being debugged] Capture a screenshot of one popup menu of the debugged application by its HWND, together with its " +
                    "structured content. Same validation and classification as ui_menu_get_info, then the same Windows.Graphics.Capture / PrintWindow pipeline as " +
                    "ui_capture_window_by_handle (works while Visual Studio has the foreground). Returns a text content { menu: <menu info>, normalizedHandle, window, " +
                    "originalWidth, originalHeight, mimeType } followed by the image content.",
                    SchemaBuilder.Create()
                        .AddInteger("handle", "HWND of the popup menu (decimal)", required: true)
                        .Build()),
                InArgs => UiMenuCaptureAsync(InAccessor, InArgs));

            DiagnosticToolRunner.Register(InRegistry,
                new McpToolDefinition(
                    "ui_menu_wait",
                    "[Windows UIA — desktop app being debugged] Wait until a popup menu of the debugged application is open. Polls the visible top-level windows and " +
                    "classifies them by content on every round (the same detection as ui_menu_detect, so a reused context-menu HWND is still found); " +
                    "'unknownPopupMenu' popups are ignored. Returns text { found, elapsedMs, menu, count, inspectedCount, truncated } where 'menu' is the frontmost " +
                    "menu (Z-order), 'count' the number of menus open and inspectedCount/truncated describe the last polling round (same meaning as in " +
                    "ui_menu_detect); found=false with menu=null on timeout. timeoutMs default 10000, max 55000; pollIntervalMs default 200, min 50. " +
                    "Use it after ui_window_right_click instead of sleeping, then continue with ui_menu_get_info / ui_menu_select.",
                    SchemaBuilder.Create()
                        .AddInteger("ownerWindowHandle", "Only wait for menus owned by this HWND (decimal), or ownerless popups created by the same GUI thread")
                        .AddInteger("timeoutMs", "Maximum time to wait in milliseconds (default: 10000, max: 55000)")
                        .AddInteger("pollIntervalMs", "Polling interval in milliseconds (default: 200, min: 50)")
                        .Build()),
                InArgs => UiMenuWaitAsync(InAccessor, InArgs));

            DiagnosticToolRunner.Register(InRegistry,
                new McpToolDefinition(
                    "ui_menu_wait_closed",
                    "[Windows UIA — desktop app being debugged] Wait until the popup menu with the given HWND is closed. Because Windows reuses the HWND of a menu that " +
                    "was just closed, IsWindow alone is not trusted: the menu is fingerprinted at the start (processId, className, bounds, uiaRootName, item count, " +
                    "first item id/name) and every round reports it as closed when the HWND no longer exists ('windowDestroyed'), belongs to another process " +
                    "('handleReused'), is no longer visible ('hidden') or now shows different content ('contentChanged'); a handle that is already invalid returns " +
                    "closed=true immediately ('alreadyClosed'). Returns text { closed, elapsedMs, handle, reason, fingerprint }; closed=false with reason=null on " +
                    "timeout. timeoutMs default 10000, max 55000; pollIntervalMs default 200, min 50.",
                    SchemaBuilder.Create()
                        .AddInteger("handle", "HWND of the popup menu to wait for (decimal)", required: true)
                        .AddInteger("timeoutMs", "Maximum time to wait in milliseconds (default: 10000, max: 55000)")
                        .AddInteger("pollIntervalMs", "Polling interval in milliseconds (default: 200, min: 50)")
                        .Build()),
                InArgs => UiMenuWaitClosedAsync(InAccessor, InArgs));

            DiagnosticToolRunner.Register(InRegistry,
                new McpToolDefinition(
                    "ui_menu_close",
                    "[Windows UIA — desktop app being debugged] Close (dismiss) one open popup menu of the debugged application without selecting any item. Do NOT use " +
                    "ui_send_keys ESC for this: it brings the main window to the front, which does not reach a WPF ContextMenu (measured in Phase 8). 'method' selects " +
                    "how the menu is dismissed: 'escape' sends a physical ESC with SendInput WITHOUT changing the foreground window (an open menu owns the input queue, " +
                    "so bringing another window to the front would dismiss it in an uncontrolled way; because one ESC only closes the frontmost level of a nested menu, " +
                    "it is re-sent up to 3 times — each send is observed for up to 400 ms and the number of menus owned by the target is recorded before and after it), " +
                    "'outsideClick' clicks one safe point of the menu's owner window " +
                    "(by default the center of the owner's title bar, which is non-client area and can hold no control; only when the owner has no WS_CAPTION a " +
                    "Text / Pane / Group / Window leaf element that supports no Invoke / Toggle / SelectionItem / Value pattern, is on screen, does not overlap any " +
                    "open menu and whose point resolves to the owner's own root element is used instead) and 'auto' (default) tries ESC first and then the outside " +
                    "click. Safety boundary (always enforced): the handle is re-validated and re-classified as a menu, the menu and its owner must belong to a debugged " +
                    "process, ESC is not sent at all when the foreground window (or the focused/active window of the foreground thread) belongs to another process, the " +
                    "outside click is skipped when the open menus could not be listed completely, no menu item is ever clicked, and the click point is only used when " +
                    "the window under it is the owner itself. Closing is confirmed by content, not by IsWindow alone (same fingerprint check as ui_menu_wait_closed). " +
                    "Returns text { closed, handle, method (escape / outsideClick / none), attempts[] (method, elapsedMs, closed, reason, and for 'escape' a 'detail' " +
                    "with escapeCount and observations[] (escape, elapsedMs, closed, reason, childMenusBefore, childMenusAfter, childMenusClosed)), elapsedMs, reason " +
                    "(windowDestroyed / hidden / contentChanged / handleReused / alreadyClosed / null), clickPoint, clickTarget (titleBar / rootElement / null), " +
                    "note }; a menu that is still open is returned with closed=false instead of an error. timeoutMs default 3000, max 55000 (each attempt gets its own " +
                    "share of it, measured from the moment the attempt starts); pollIntervalMs default 100, min 50.",
                    SchemaBuilder.Create()
                        .AddInteger("handle", "HWND of the popup menu to close (decimal, as returned by ui_menu_detect 'handle')", required: true)
                        .AddEnum("method", "How to dismiss the menu: 'auto' (default: escape, then outsideClick), 'escape', 'outsideClick'",
                            new[] { _CLOSE_METHOD_AUTO, _CLOSE_METHOD_ESCAPE, _CLOSE_METHOD_OUTSIDE_CLICK })
                        .AddInteger("timeoutMs", "Maximum total time to wait for the menu to close in milliseconds (default: 3000, max: 55000)")
                        .AddInteger("pollIntervalMs", "Polling interval in milliseconds (default: 100, min: 50)")
                        .Build()),
                InArgs => UiMenuCloseAsync(InAccessor, InArgs));

            DiagnosticToolRunner.Register(InRegistry,
                new McpToolDefinition(
                    "ui_menu_select_path",
                    "[Windows UIA — desktop app being debugged] Walk a nested popup menu of the debugged application and invoke the item at the end of 'path' " +
                    "(e.g. [\"Submenu\", \"Sub Item 2\"]). Each segment is matched against the item names of the menu that is currently open, by exact Name (also " +
                    "compared with the accelerator '&' removed) — partial matches are never used and several matching items fail with the candidate list. Every segment " +
                    "re-runs the full safety boundary of ui_menu_select on the menu that is open at that moment (range, IsWindow, debugged-process check, " +
                    "re-classification by content, item ownership, disabled items refused) so a stale handle or a submenu that disappeared is reported instead of " +
                    "clicking blindly. An intermediate segment must have a submenu (ExpandCollapse / Win32 hSubMenu); after expanding it, the submenu is detected by " +
                    "content — a visible menu with at least one item whose HWND was not open before the expansion (submenuDetection='newHandle') or that is owned by " +
                    "the current menu although its HWND was already open (submenuDetection='ownerMatch', the case where WPF reuses the popup HWND of a submenu that " +
                    "was opened once), menus owned by the current one being preferred — and becomes the next level. The " +
                    "last segment is invoked like ui_menu_select, including the cursor move onto an item that is about to be expanded (reported per step as " +
                    "'cursorMoved'). Returns text { completed, handle, path, steps[] (segment, menuHandle, item, method, cursorMoved, submenuHandle, " +
                    "submenuDetection), menuClosed, elapsedMs }. " +
                    "timeoutMs (default 5000, max 55000) is the budget for each submenu to appear and for the final close check; pollIntervalMs default 100, min 50.",
                    BuildSelectPathSchema()),
                InArgs => UiMenuSelectPathAsync(InAccessor, InArgs));
        }

        /// <summary>
        /// ui_menu_select_path の入力スキーマを組み立てる。SchemaBuilder は文字列配列を扱えないので、path だけ JSON Schema を直接足す。
        /// </summary>
        /// <returns>入力スキーマ。</returns>
        private static JObject BuildSelectPathSchema()
        {
            JObject TheSchema = SchemaBuilder.Create()
                .AddInteger("handle", "HWND of the popup menu to start from (decimal, as returned by ui_menu_detect 'handle')", required: true)
                .AddInteger("timeoutMs", "Time budget in milliseconds for each submenu to appear and for the final close check (default: 5000, max: 55000)")
                .AddInteger("pollIntervalMs", "Polling interval in milliseconds (default: 100, min: 50)")
                .Build();

            ((JObject)TheSchema["properties"])["path"] = new JObject
            {
                ["type"] = "array",
                ["items"] = new JObject { ["type"] = "string" },
                ["description"] = "Menu item names from the given menu down to the item to invoke, e.g. [\"Submenu\", \"Sub Item 2\"] (exact Name match, the "
                    + "accelerator '&' is ignored; at least one and at most 10 segments)",
            };
            JArray TheRequired = TheSchema["required"] as JArray;
            if (TheRequired == null)
            {
                TheRequired = new JArray();
                TheSchema["required"] = TheRequired;
            }
            TheRequired.Add("path");
            return TheSchema;
        }

        // ------------------------------------------------------------------
        // ツールハンドラ
        // ------------------------------------------------------------------

        /// <summary>ui_menu_detect の本体。可視トップレベルウィンドウを毎回内容検査してメニューを返す。</summary>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        /// <param name="InArgs">ツール引数（ownerWindowHandle / includeUnknown）。</param>
        /// <returns>text（count / includeUnknown / inspectedCount / truncated / menus）、またはエラー。</returns>
        private static async Task<McpToolResult> UiMenuDetectAsync(VsServiceAccessor InAccessor, JObject InArgs)
        {
            long? TheOwnerHandle = InArgs.Value<long?>("ownerWindowHandle");
            if (TheOwnerHandle.HasValue && !UiWindowTools.IsHandleInRange(TheOwnerHandle.Value))
            {
                return McpToolResult.Error($"Window handle {TheOwnerHandle.Value} is out of range for a window handle.");
            }
            bool IsUnknownIncluded = InArgs.Value<bool?>("includeUnknown") ?? false;

            HashSet<uint> TheProcessIds = await UiWindowTools.GetDebuggedProcessIdsAsync(InAccessor);
            if (TheProcessIds.Count == 0)
            {
                return McpToolResult.Error(DebuggeeWindowResolver.NoDebuggedProcessMessage);
            }

            try
            {
                MenuDetection TheDetection = await UiTools.RunUiaWithTimeoutAsync(() => DetectMenus(TheProcessIds, TheOwnerHandle, IsUnknownIncluded));
                return McpToolResult.Success(new
                {
                    count = TheDetection.Menus.Count,
                    includeUnknown = IsUnknownIncluded,
                    inspectedCount = TheDetection.InspectedCount,
                    truncated = TheDetection.IsTruncated,
                    menus = TheDetection.Menus,
                });
            }
            catch (TimeoutException TheException)
            {
                return McpToolResult.Error(TheException.Message);
            }
            catch (Exception TheException)
            {
                return McpToolResult.Error($"ui_menu_detect failed: {TheException.Message}");
            }
        }

        /// <summary>ui_menu_get_info の本体。</summary>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        /// <param name="InArgs">ツール引数（handle）。</param>
        /// <returns>メニュー情報、またはエラー。</returns>
        private static async Task<McpToolResult> UiMenuGetInfoAsync(VsServiceAccessor InAccessor, JObject InArgs)
        {
            ResolvedMenu TheResolved = await ResolveMenuAsync(InAccessor, InArgs);
            if (TheResolved.Error != null)
            {
                return TheResolved.Error;
            }
            return McpToolResult.Success(TheResolved.Menu);
        }

        /// <summary>ui_menu_capture の本体。構造化情報を取得したうえで共通キャプチャ経路へ渡す。</summary>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        /// <param name="InArgs">ツール引数（handle）。</param>
        /// <returns>text（menu + 画像メタ情報）+ image、またはエラー。</returns>
        private static async Task<McpToolResult> UiMenuCaptureAsync(VsServiceAccessor InAccessor, JObject InArgs)
        {
            ResolvedMenu TheResolved = await ResolveMenuAsync(InAccessor, InArgs);
            if (TheResolved.Error != null)
            {
                return TheResolved.Error;
            }

            JObject ThePrefix = new JObject
            {
                ["menu"] = JObject.FromObject(TheResolved.Menu),
            };
            // 共通キャプチャ経路が IsWindow / PID 再照合 / IsIconic をやり直す（取得からキャプチャまでに閉じられた場合の競合対策）
            return await UiWindowTools.CaptureTopLevelWindowAsync(InAccessor, TheResolved.Menu.Handle, ThePrefix);
        }

        /// <summary>
        /// ui_menu_select の本体。引数を解釈してから、1 回の STA 呼び出しの中で安全境界・項目解決・実行・閉鎖確認までを行う。
        /// </summary>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        /// <param name="InArgs">ツール引数（handle / itemId / automationId / name / index / closeWaitMs）。</param>
        /// <returns>text（message / method / menuHandle / item / menuClosed / closeWaitMs / submenuOpened / submenu / submenuCandidates / note）、またはエラー。</returns>
        private static async Task<McpToolResult> UiMenuSelectAsync(VsServiceAccessor InAccessor, JObject InArgs)
        {
            UiMenuItemSelector TheSelector = new UiMenuItemSelector
            {
                ItemId = InArgs.Value<int?>("itemId"),
                AutomationId = InArgs.Value<string>("automationId"),
                Name = InArgs.Value<string>("name"),
                Index = InArgs.Value<int?>("index"),
            };
            if (!TheSelector.HasCriteria)
            {
                return McpToolResult.Error("At least one menu item selector must be provided (itemId, automationId, name, or index)");
            }
            if (TheSelector.Index.HasValue && TheSelector.Index.Value < 0)
            {
                return McpToolResult.Error("Parameter 'index' must be 0 or greater");
            }

            int TheCloseWaitMs = InArgs.Value<int?>("closeWaitMs") ?? _DEFAULT_CLOSE_WAIT_MS;
            if (TheCloseWaitMs < 0)
            {
                TheCloseWaitMs = 0;
            }
            if (TheCloseWaitMs > _MAX_CLOSE_WAIT_MS)
            {
                TheCloseWaitMs = _MAX_CLOSE_WAIT_MS;
            }

            ResolvedMenu TheResolved = await ResolveMenuWindowAsync(InAccessor, InArgs);
            if (TheResolved.Error != null)
            {
                return TheResolved.Error;
            }

            try
            {
                return await UiTools.RunUiaWithTimeoutAsync(() => SelectMenuItem(TheResolved, TheSelector, TheCloseWaitMs));
            }
            catch (TimeoutException TheException)
            {
                return McpToolResult.Error(TheException.Message);
            }
            catch (Exception TheException)
            {
                return McpToolResult.Error($"ui_menu_select failed in menu {TheResolved.Window.ToInt64()}: {TheException.Message}");
            }
        }

        /// <summary>ui_menu_wait の本体。Phase 4〜7 と同じポーリング・タイムアウト形式でポップアップメニューの出現を待つ。</summary>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        /// <param name="InArgs">ツール引数（ownerWindowHandle / timeoutMs / pollIntervalMs）。</param>
        /// <returns>text（found / elapsedMs / menu / count / inspectedCount / truncated）、またはエラー。</returns>
        private static async Task<McpToolResult> UiMenuWaitAsync(VsServiceAccessor InAccessor, JObject InArgs)
        {
            long? TheOwnerHandle = InArgs.Value<long?>("ownerWindowHandle");
            if (TheOwnerHandle.HasValue && !UiWindowTools.IsHandleInRange(TheOwnerHandle.Value))
            {
                return McpToolResult.Error($"Window handle {TheOwnerHandle.Value} is out of range for a window handle.");
            }
            UiWindowTools.ParseWaitOptions(InArgs, out int TheTimeoutMs, out int ThePollIntervalMs);

            Stopwatch TheStopwatch = Stopwatch.StartNew();
            HashSet<uint> TheProcessIds = await UiWindowTools.GetDebuggedProcessIdsAsync(InAccessor);
            if (TheProcessIds.Count == 0)
            {
                return McpToolResult.Error(DebuggeeWindowResolver.NoDebuggedProcessMessage);
            }

            MenuDetection TheLastDetection = new MenuDetection();
            while (true)
            {
                try
                {
                    HashSet<uint> TheCurrentProcessIds = TheProcessIds;
                    TheLastDetection = await UiTools.RunUiaWithTimeoutAsync(() => DetectMenus(TheCurrentProcessIds, TheOwnerHandle, false));
                }
                catch (TimeoutException TheException)
                {
                    return McpToolResult.Error(TheException.Message);
                }
                catch (Exception TheException)
                {
                    return McpToolResult.Error($"ui_menu_wait failed: {TheException.Message}");
                }

                if (TheLastDetection.Menus.Count > 0)
                {
                    return McpToolResult.Success(new
                    {
                        found = true,
                        elapsedMs = TheStopwatch.ElapsedMilliseconds,
                        menu = TheLastDetection.Menus[0],
                        count = TheLastDetection.Menus.Count,
                        inspectedCount = TheLastDetection.InspectedCount,
                        truncated = TheLastDetection.IsTruncated,
                    });
                }

                if (TheStopwatch.ElapsedMilliseconds >= TheTimeoutMs)
                {
                    break;
                }
                await Task.Delay(ThePollIntervalMs);

                // デバッグ終了で対象プロセスが消えた場合は無限に待たず打ち切る
                TheProcessIds = await UiWindowTools.GetDebuggedProcessIdsAsync(InAccessor);
                if (TheProcessIds.Count == 0)
                {
                    return McpToolResult.Error("Debugging stopped while waiting for the popup menu.");
                }
            }

            return McpToolResult.Success(new
            {
                found = false,
                elapsedMs = TheStopwatch.ElapsedMilliseconds,
                menu = (UiMenuInfo)null,
                count = 0,
                inspectedCount = TheLastDetection.InspectedCount,
                truncated = TheLastDetection.IsTruncated,
            });
        }

        /// <summary>
        /// ui_menu_wait_closed の本体。開始時の fingerprint と毎周の観測を突き合わせ、HWND の再利用や内容の入れ替わりも閉鎖として扱う。
        /// </summary>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        /// <param name="InArgs">ツール引数（handle / timeoutMs / pollIntervalMs）。</param>
        /// <returns>text（closed / elapsedMs / handle / reason / fingerprint）、またはエラー。</returns>
        private static async Task<McpToolResult> UiMenuWaitClosedAsync(VsServiceAccessor InAccessor, JObject InArgs)
        {
            long? TheHandle = InArgs.Value<long?>("handle");
            if (!TheHandle.HasValue)
            {
                return McpToolResult.Error("Parameter 'handle' is required (use the 'handle' value returned by ui_menu_detect)");
            }
            if (!UiWindowTools.IsHandleInRange(TheHandle.Value))
            {
                return McpToolResult.Error($"Window handle {TheHandle.Value} is out of range for a window handle.");
            }
            UiWindowTools.ParseWaitOptions(InArgs, out int TheTimeoutMs, out int ThePollIntervalMs);

            Stopwatch TheStopwatch = Stopwatch.StartNew();
            // 既に存在しない HWND は待つまでもなく closed
            if (!IsWindow(new IntPtr(TheHandle.Value)))
            {
                return BuildClosedResult(true, TheStopwatch, TheHandle.Value, _REASON_ALREADY_CLOSED, null);
            }

            ResolvedMenu TheResolved = await ResolveMenuWindowAsync(InAccessor, InArgs);
            if (TheResolved.Error != null)
            {
                // 検証の途中でウィンドウが消えた場合は closed として返す（範囲外・別プロセスの handle はエラーのまま）
                if (!IsWindow(new IntPtr(TheHandle.Value)))
                {
                    return BuildClosedResult(true, TheStopwatch, TheHandle.Value, _REASON_ALREADY_CLOSED, null);
                }
                return TheResolved.Error;
            }

            WindowInfo TheWindowInfo = TheResolved.WindowInfo;
            UiMenuInfo TheStartMenu;
            try
            {
                TheStartMenu = await UiTools.RunUiaWithTimeoutAsync(() =>
                    UiPopupMenuResolver.Classify(TheWindowInfo, out UiMenuInfo TheClassified, out _) ? TheClassified : null);
            }
            catch (TimeoutException TheException)
            {
                return McpToolResult.Error(TheException.Message);
            }
            catch (Exception TheException)
            {
                return McpToolResult.Error($"Failed to inspect popup menu {TheWindowInfo.Handle}: {TheException.Message}");
            }

            if (TheStartMenu == null || !UiPopupMenuResolver.IsKnownMenuType(TheStartMenu.MenuType))
            {
                // 開始時点で既にメニューとして観測できない＝監視対象のメニューは閉じている
                return BuildClosedResult(true, TheStopwatch, TheWindowInfo.Handle, _REASON_CONTENT_CHANGED, null);
            }

            UiMenuFingerprint TheFingerprint = UiPopupMenuResolver.BuildFingerprint(TheStartMenu);
            long TheMenuHandle = TheStartMenu.Handle;
            uint TheInitialProcessId = TheStartMenu.ProcessId;
            HashSet<uint> TheProcessIds = TheResolved.ProcessIds;

            while (true)
            {
                string TheReason;
                try
                {
                    TheReason = await UiTools.RunUiaWithTimeoutAsync(() => DescribeMenuClosed(TheProcessIds, TheMenuHandle, TheInitialProcessId, TheFingerprint));
                }
                catch (TimeoutException TheException)
                {
                    return McpToolResult.Error(TheException.Message);
                }
                catch (Exception TheException)
                {
                    return McpToolResult.Error($"ui_menu_wait_closed failed for menu {TheMenuHandle}: {TheException.Message}");
                }

                if (TheReason != null)
                {
                    return BuildClosedResult(true, TheStopwatch, TheMenuHandle, TheReason, TheFingerprint);
                }
                if (TheStopwatch.ElapsedMilliseconds >= TheTimeoutMs)
                {
                    return BuildClosedResult(false, TheStopwatch, TheMenuHandle, null, TheFingerprint);
                }
                await Task.Delay(ThePollIntervalMs);
            }
        }

        /// <summary>
        /// Extended (Phase 9): ui_menu_close の本体。分類・Owner 解決・fingerprint を取ってから、method に従って
        /// 「物理 ESC」→「Owner 上の安全な点のクリック」の順に試し、毎回 fingerprint で閉鎖を確認する。
        /// 閉じなかった場合はエラーではなく closed=false の成功として返す（呼び出し側が次の手を選べるようにするため）。
        /// </summary>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        /// <param name="InArgs">ツール引数（handle / method / timeoutMs / pollIntervalMs）。</param>
        /// <returns>text（closed / handle / method / attempts / elapsedMs / reason / clickPoint / note）、またはエラー。</returns>
        private static async Task<McpToolResult> UiMenuCloseAsync(VsServiceAccessor InAccessor, JObject InArgs)
        {
            string TheRequestedMethod = InArgs.Value<string>("method") ?? _CLOSE_METHOD_AUTO;
            if (!string.Equals(TheRequestedMethod, _CLOSE_METHOD_AUTO, StringComparison.Ordinal)
                && !string.Equals(TheRequestedMethod, _CLOSE_METHOD_ESCAPE, StringComparison.Ordinal)
                && !string.Equals(TheRequestedMethod, _CLOSE_METHOD_OUTSIDE_CLICK, StringComparison.Ordinal))
            {
                return McpToolResult.Error(
                    $"Unknown method: '{TheRequestedMethod}'. Expected one of: {_CLOSE_METHOD_AUTO}, {_CLOSE_METHOD_ESCAPE}, {_CLOSE_METHOD_OUTSIDE_CLICK}");
            }
            ParseMenuWaitOptions(InArgs, _DEFAULT_CLOSE_TIMEOUT_MS, out int TheTimeoutMs, out int ThePollIntervalMs);

            Stopwatch TheStopwatch = Stopwatch.StartNew();
            List<object> TheAttempts = new List<object>();
            long? TheRequestedHandle = InArgs.Value<long?>("handle");
            if (TheRequestedHandle.HasValue && UiWindowTools.IsHandleInRange(TheRequestedHandle.Value) && !IsWindow(new IntPtr(TheRequestedHandle.Value)))
            {
                // 既に存在しない HWND は試すまでもなく closed（ui_menu_wait_closed と同じ扱い）
                return BuildCloseResult(true, TheStopwatch, TheRequestedHandle.Value, _CLOSE_METHOD_NONE, _REASON_ALREADY_CLOSED, TheAttempts, 0,
                    null, null, null);
            }

            ResolvedMenu TheResolved = await ResolveMenuWindowAsync(InAccessor, InArgs);
            if (TheResolved.Error != null)
            {
                return TheResolved.Error;
            }

            MenuCloseTarget TheTarget;
            try
            {
                TheTarget = await UiTools.RunUiaWithTimeoutAsync(() => PrepareMenuClose(TheResolved));
            }
            catch (TimeoutException TheException)
            {
                return McpToolResult.Error(TheException.Message);
            }
            catch (Exception TheException)
            {
                return McpToolResult.Error($"ui_menu_close failed for menu {TheResolved.Window.ToInt64()}: {TheException.Message}");
            }
            if (TheTarget.Error != null)
            {
                return McpToolResult.Error(TheTarget.Error);
            }

            UiMenuInfo TheMenu = TheTarget.Menu;
            bool IsAutoMethod = string.Equals(TheRequestedMethod, _CLOSE_METHOD_AUTO, StringComparison.Ordinal);
            string TheClosedReason = null;
            string TheUsedMethod = _CLOSE_METHOD_NONE;
            object TheClickPoint = null;
            string TheClickTarget = null;
            string TheNote = null;
            // Extended (Phase 10): 実際に送った ESC の回数を診断へ渡すため、試行のループの外で数える
            int TheEscapeCount = 0;

            try
            {
                if (IsAutoMethod || string.Equals(TheRequestedMethod, _CLOSE_METHOD_ESCAPE, StringComparison.Ordinal))
                {
                    long TheAttemptStartMs = TheStopwatch.ElapsedMilliseconds;
                    // 物理 ESC はフォアグラウンドのキューへ届くので、入力先がデバッグ対象でなければ 1 キーも送らない
                    string TheEscapeRejection = DescribeEscapeTargetRejection(TheResolved.ProcessIds, TheMenu.Handle, TheTarget.OwnerHandle);
                    if (TheEscapeRejection != null)
                    {
                        if (!IsAutoMethod)
                        {
                            return McpToolResult.Error(TheEscapeRejection);
                        }
                        TheAttempts.Add(new
                        {
                            method = _CLOSE_METHOD_ESCAPE,
                            elapsedMs = TheStopwatch.ElapsedMilliseconds - TheAttemptStartMs,
                            closed = false,
                            reason = TheEscapeRejection,
                        });
                        TheNote = TheEscapeRejection;
                    }
                    else
                    {
                        // 待ち時間は「この試行を始めた時点」から数える（解決に時間がかかっても ESC の待ちが削られないようにする）
                        long TheDeadlineMs = TheAttemptStartMs + (IsAutoMethod ? TheTimeoutMs / 2 : TheTimeoutMs);
                        HashSet<uint> TheEscapeProcessIds = TheResolved.ProcessIds;
                        long TheEscapeMenuHandle = TheMenu.Handle;
                        List<object> TheEscapeObservations = new List<object>();
                        // 「その ESC が対象ではなく対象の子（サブメニュー）を閉じた」ことが後から分かるように、子の数を毎回控える
                        int TheChildMenuCount = await UiTools.RunUiaWithTimeoutAsync(() => CountChildMenus(TheEscapeProcessIds, TheEscapeMenuHandle));
                        while (true)
                        {
                            long TheEscapeStartMs = TheStopwatch.ElapsedMilliseconds;
                            int TheChildMenuCountBefore = TheChildMenuCount;
                            await Task.Run(() => UiKeyboardInput.SendEscape());
                            TheEscapeCount++;
                            // 1 回の ESC は最前面のサブメニューだけを閉じることがある（Phase 9b 実測）ので、短い観測窓で見てから再送する
                            long TheObserveDeadlineMs = Math.Min(TheDeadlineMs, TheStopwatch.ElapsedMilliseconds + _ESCAPE_OBSERVE_MS);
                            TheClosedReason = await PollMenuClosedAsync(TheResolved.ProcessIds, TheMenu.Handle, TheMenu.ProcessId, TheTarget.Fingerprint,
                                TheStopwatch, TheObserveDeadlineMs, ThePollIntervalMs);
                            TheChildMenuCount = await UiTools.RunUiaWithTimeoutAsync(() => CountChildMenus(TheEscapeProcessIds, TheEscapeMenuHandle));
                            TheEscapeObservations.Add(new
                            {
                                escape = TheEscapeCount,
                                elapsedMs = TheStopwatch.ElapsedMilliseconds - TheEscapeStartMs,
                                closed = TheClosedReason != null,
                                reason = TheClosedReason,
                                childMenusBefore = TheChildMenuCountBefore,
                                childMenusAfter = TheChildMenuCount,
                                childMenusClosed = TheChildMenuCount < TheChildMenuCountBefore,
                            });
                            if (TheClosedReason != null || TheEscapeCount > _MAX_ESCAPE_RESEND_COUNT
                                || TheStopwatch.ElapsedMilliseconds >= TheDeadlineMs)
                            {
                                break;
                            }
                        }
                        TheAttempts.Add(new
                        {
                            method = _CLOSE_METHOD_ESCAPE,
                            elapsedMs = TheStopwatch.ElapsedMilliseconds - TheAttemptStartMs,
                            closed = TheClosedReason != null,
                            reason = TheClosedReason,
                            detail = new
                            {
                                escapeCount = TheEscapeCount,
                                observations = TheEscapeObservations,
                            },
                        });
                        if (TheClosedReason != null)
                        {
                            TheUsedMethod = _CLOSE_METHOD_ESCAPE;
                        }
                    }
                }

                if (TheClosedReason == null && (IsAutoMethod || string.Equals(TheRequestedMethod, _CLOSE_METHOD_OUTSIDE_CLICK, StringComparison.Ordinal)))
                {
                    long TheAttemptStartMs = TheStopwatch.ElapsedMilliseconds;
                    HashSet<uint> TheProcessIds = TheResolved.ProcessIds;
                    long TheOwnerHandle = TheTarget.OwnerHandle;
                    MenuOutsideClick TheOutsideClick = await UiTools.RunUiaWithTimeoutAsync(() => PerformOutsideClick(TheProcessIds, TheMenu, TheOwnerHandle));
                    if (TheOutsideClick.Error != null)
                    {
                        if (!IsAutoMethod)
                        {
                            return McpToolResult.Error(TheOutsideClick.Error);
                        }
                        // auto では「ESC が効かず outside click も打てなかった」ことを結果として返す（呼び出し側が次の手を選べるように）
                        TheAttempts.Add(new
                        {
                            method = _CLOSE_METHOD_OUTSIDE_CLICK,
                            elapsedMs = TheStopwatch.ElapsedMilliseconds - TheAttemptStartMs,
                            closed = false,
                            reason = TheOutsideClick.Error,
                        });
                        TheNote = TheOutsideClick.Error;
                    }
                    else
                    {
                        TheClickPoint = new { x = TheOutsideClick.X, y = TheOutsideClick.Y };
                        TheClickTarget = TheOutsideClick.ClickTarget;
                        TheNote = $"outside click at ({TheOutsideClick.X}, {TheOutsideClick.Y}) on owner {TheTarget.OwnerHandle} ({TheOutsideClick.Source})";
                        string TheCandidateNote = DescribeOutsideClickCandidates(TheOutsideClick.Diagnostics);
                        if (TheCandidateNote != null)
                        {
                            // 中央が覆われて別の候補点を採ったことが後から分かるように残す
                            TheNote += $"; {TheCandidateNote}";
                        }
                        // ESC と同じく、待ち時間はこの試行の開始時点から数えた残り時間にする
                        long TheDeadlineMs = TheAttemptStartMs + Math.Max(0, TheTimeoutMs - TheAttemptStartMs);
                        TheClosedReason = await PollMenuClosedAsync(TheResolved.ProcessIds, TheMenu.Handle, TheMenu.ProcessId, TheTarget.Fingerprint,
                            TheStopwatch, TheDeadlineMs, ThePollIntervalMs);
                        TheAttempts.Add(new
                        {
                            method = _CLOSE_METHOD_OUTSIDE_CLICK,
                            elapsedMs = TheStopwatch.ElapsedMilliseconds - TheAttemptStartMs,
                            closed = TheClosedReason != null,
                            reason = TheClosedReason,
                        });
                        if (TheClosedReason != null)
                        {
                            TheUsedMethod = _CLOSE_METHOD_OUTSIDE_CLICK;
                        }
                    }
                }
            }
            catch (TimeoutException TheException)
            {
                return McpToolResult.Error(TheException.Message);
            }
            catch (Exception TheException)
            {
                return McpToolResult.Error($"ui_menu_close failed for menu {TheMenu.Handle}: {TheException.Message}");
            }

            return BuildCloseResult(TheClosedReason != null, TheStopwatch, TheMenu.Handle, TheUsedMethod, TheClosedReason, TheAttempts, TheEscapeCount,
                TheClickPoint, TheClickTarget, TheNote);
        }

        /// <summary>
        /// Extended (Phase 9): 物理 ESC を送ってよいかを確認する。SendInput の ESC はフォアグラウンドのキューへ入るため、
        /// フォアグラウンド（GetForegroundWindow の GA_ROOT）と、フォアグラウンドスレッドのアクティブ / フォーカスウィンドウ
        /// （GetGUIThreadInfo(0)）が、メニュー自身・その Owner・デバッグ対象プロセスのいずれかであることを確かめる。
        /// 1 つでも他プロセスのウィンドウなら ESC を送らない（他アプリのウィンドウを閉じてしまわないため）。
        /// </summary>
        /// <param name="InProcessIds">デバッグ中プロセス ID の集合。</param>
        /// <param name="InMenuHandle">閉じたいメニューの HWND。</param>
        /// <param name="InOwnerHandle">メニューの Owner の HWND。0 なら判定に使わない。</param>
        /// <returns>ESC を送ってはいけない理由。送ってよければ null。</returns>
        private static string DescribeEscapeTargetRejection(HashSet<uint> InProcessIds, long InMenuHandle, long InOwnerHandle)
        {
            IntPtr TheForeground = GetForegroundWindow();
            if (TheForeground == IntPtr.Zero)
            {
                return "no foreground window could be determined; ESC was not sent";
            }

            string TheRejection = DescribeForeignInputWindow(TheForeground, InProcessIds, InMenuHandle, InOwnerHandle);
            if (TheRejection != null)
            {
                return TheRejection;
            }

            GUITHREADINFO TheThreadInfo = new GUITHREADINFO
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(GUITHREADINFO)),
            };
            if (!GetGUIThreadInfo(0, ref TheThreadInfo))
            {
                // フォアグラウンドスレッドの情報を取れない場合は、フォアグラウンドウィンドウの判定だけを根拠にする
                return null;
            }
            TheRejection = DescribeForeignInputWindow(TheThreadInfo.hwndActive, InProcessIds, InMenuHandle, InOwnerHandle);
            if (TheRejection != null)
            {
                return TheRejection;
            }
            return DescribeForeignInputWindow(TheThreadInfo.hwndFocus, InProcessIds, InMenuHandle, InOwnerHandle);
        }

        /// <summary>
        /// Extended (Phase 9): 入力が届く 1 つのウィンドウが「デバッグ対象の側」かを判定する。
        /// メニュー自身・その Owner・デバッグ中プロセスのウィンドウなら送ってよい。
        /// </summary>
        /// <param name="InWindow">判定するウィンドウ（0 なら観測できていないので判定に使わない）。</param>
        /// <param name="InProcessIds">デバッグ中プロセス ID の集合。</param>
        /// <param name="InMenuHandle">閉じたいメニューの HWND。</param>
        /// <param name="InOwnerHandle">メニューの Owner の HWND。0 なら判定に使わない。</param>
        /// <returns>他プロセスのウィンドウだった場合の理由。問題なければ null。</returns>
        private static string DescribeForeignInputWindow(IntPtr InWindow, HashSet<uint> InProcessIds, long InMenuHandle, long InOwnerHandle)
        {
            if (InWindow == IntPtr.Zero)
            {
                return null;
            }
            IntPtr TheRoot = GetAncestor(InWindow, GA_ROOT);
            long TheRootHandle = (TheRoot == IntPtr.Zero ? InWindow : TheRoot).ToInt64();
            if (TheRootHandle == InMenuHandle || (InOwnerHandle != 0 && TheRootHandle == InOwnerHandle))
            {
                return null;
            }
            GetWindowThreadProcessId(new IntPtr(TheRootHandle), out uint TheProcessId);
            if (InProcessIds != null && InProcessIds.Contains(TheProcessId))
            {
                return null;
            }
            return $"foreground window {TheRootHandle} belongs to another process";
        }

        /// <summary>
        /// Extended (Phase 9b): 対象メニューを Owner に持つ（＝対象から開かれた）メニューの数を数える。ESC を再送する間に
        /// 「その ESC が対象ではなくサブメニューを閉じた」ことを観測するために使う。STA スレッドで呼ぶこと。
        /// </summary>
        /// <param name="InProcessIds">デバッグ中プロセス ID の集合。</param>
        /// <param name="InMenuHandle">親として数える対象メニューの HWND。</param>
        /// <returns>Owner が対象メニューであるメニューの数。</returns>
        private static int CountChildMenus(HashSet<uint> InProcessIds, long InMenuHandle)
        {
            MenuDetection TheDetection = DetectMenus(InProcessIds, InMenuHandle, false);
            // Owner を持たない同一スレッドのポップアップも DetectMenus は返すので、Owner が対象そのものの分だけを数える
            return TheDetection.Menus.FindAll(TheMenu => TheMenu.OwnerHandle == InMenuHandle).Count;
        }

        /// <summary>
        /// Extended (Phase 9): ui_menu_select_path の本体。path の各段で「今開いているメニュー」を取り直して安全境界を通し、
        /// 中間段はサブメニューを展開して新しく現れたメニューへ進み、最終段で項目を実行する。
        /// 各段の STA 呼び出しは短く保ち、待機はツール側の非同期ポーリングで行う（AutomationElement を呼び出し間で保持しないため）。
        /// </summary>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        /// <param name="InArgs">ツール引数（handle / path / timeoutMs / pollIntervalMs）。</param>
        /// <returns>text（completed / handle / path / steps / menuClosed / elapsedMs）、またはエラー。</returns>
        private static async Task<McpToolResult> UiMenuSelectPathAsync(VsServiceAccessor InAccessor, JObject InArgs)
        {
            List<string> ThePath = ParsePathSegments(InArgs, out string ThePathError);
            if (ThePathError != null)
            {
                return McpToolResult.Error(ThePathError);
            }
            ParseMenuWaitOptions(InArgs, _DEFAULT_PATH_TIMEOUT_MS, out int TheTimeoutMs, out int ThePollIntervalMs);

            ResolvedMenu TheResolved = await ResolveMenuWindowAsync(InAccessor, InArgs);
            if (TheResolved.Error != null)
            {
                return TheResolved.Error;
            }

            Stopwatch TheStopwatch = Stopwatch.StartNew();
            HashSet<uint> TheProcessIds = TheResolved.ProcessIds;
            long TheStartHandle = TheResolved.Window.ToInt64();
            long TheCurrentHandle = TheStartHandle;
            List<object> TheSteps = new List<object>();
            List<MenuCloseWatch> TheWatches = new List<MenuCloseWatch>();

            for (int TheSegmentIndex = 0; TheSegmentIndex < ThePath.Count; TheSegmentIndex++)
            {
                string TheSegment = ThePath[TheSegmentIndex];
                bool IsLastSegment = TheSegmentIndex == ThePath.Count - 1;
                long TheSegmentMenuHandle = TheCurrentHandle;
                int TheSegmentNumber = TheSegmentIndex;

                MenuPathStep TheStep;
                try
                {
                    TheStep = await UiTools.RunUiaWithTimeoutAsync(() =>
                        ExecutePathSegment(TheProcessIds, TheSegmentMenuHandle, TheSegment, TheSegmentNumber, IsLastSegment));
                }
                catch (TimeoutException TheException)
                {
                    return McpToolResult.Error(TheException.Message);
                }
                catch (Exception TheException)
                {
                    return McpToolResult.Error($"ui_menu_select_path failed in menu {TheSegmentMenuHandle}: {TheException.Message}");
                }
                if (TheStep.Error != null)
                {
                    return McpToolResult.Error(TheStep.Error);
                }
                TheWatches.Add(new MenuCloseWatch { Handle = TheStep.MenuHandle, ProcessId = TheStep.ProcessId, Fingerprint = TheStep.Fingerprint });

                long? TheSubmenuHandle = null;
                string TheSubmenuDetection = null;
                if (!IsLastSegment)
                {
                    int TheRemainingMs = (int)Math.Max(0, TheTimeoutMs - TheStopwatch.ElapsedMilliseconds);
                    SubmenuDetection TheCandidates;
                    try
                    {
                        TheCandidates = await WaitNewMenusAsync(TheProcessIds, TheStep.MenuHandlesBefore, TheStep.MenuHandle, TheRemainingMs, ThePollIntervalMs);
                    }
                    catch (TimeoutException TheException)
                    {
                        return McpToolResult.Error(TheException.Message);
                    }
                    catch (Exception TheException)
                    {
                        return McpToolResult.Error($"ui_menu_select_path failed in menu {TheStep.MenuHandle}: {TheException.Message}");
                    }
                    if (TheCandidates.Menus.Count == 0)
                    {
                        return McpToolResult.Error(
                            $"Submenu did not appear within {TheRemainingMs} ms after expanding menu item '{TheSegment}' (segment {TheSegmentNumber}) " +
                            $"in menu {TheStep.MenuHandle}");
                    }
                    TheSubmenuHandle = PickSubmenu(TheCandidates.Menus, TheStep.MenuHandle);
                    TheSubmenuDetection = TheCandidates.Detection;
                    TheCurrentHandle = TheSubmenuHandle.Value;
                }

                TheSteps.Add(new
                {
                    segment = TheSegment,
                    menuHandle = TheStep.MenuHandle,
                    item = TheStep.Item,
                    method = TheStep.Method,
                    // Extended (Phase 9): 展開の直前にカーソルを項目上へ置いたか（WPF のサブメニューが hover で閉じる問題への対処）
                    cursorMoved = TheStep.IsCursorMoved,
                    submenuHandle = TheSubmenuHandle,
                    // Extended (Phase 9b): サブメニューを新規 HWND で見つけたか、Owner 一致（HWND 再利用）で見つけたか
                    submenuDetection = TheSubmenuDetection,
                });
            }

            int TheCloseWaitMs = (int)Math.Max(0, TheTimeoutMs - TheStopwatch.ElapsedMilliseconds);
            bool IsMenuClosed;
            try
            {
                IsMenuClosed = await WaitMenusClosedAsync(TheProcessIds, TheWatches, TheCloseWaitMs, ThePollIntervalMs);
            }
            catch (TimeoutException TheException)
            {
                return McpToolResult.Error(TheException.Message);
            }
            catch (Exception TheException)
            {
                return McpToolResult.Error($"ui_menu_select_path failed in menu {TheCurrentHandle}: {TheException.Message}");
            }
            return McpToolResult.Success(new
            {
                completed = true,
                handle = TheStartHandle,
                path = ThePath,
                steps = TheSteps,
                menuClosed = IsMenuClosed,
                elapsedMs = TheStopwatch.ElapsedMilliseconds,
            });
        }

        // ------------------------------------------------------------------
        // 共通 helper
        // ------------------------------------------------------------------

        /// <summary>handle から解決したメニュー一式。Error が非 null なら他は未設定のことがある。</summary>
        private sealed class ResolvedMenu
        {
            /// <summary>正規化済みのトップレベル HWND。</summary>
            public IntPtr Window { get; set; }

            /// <summary>デバッグ中プロセス ID の集合。</summary>
            public HashSet<uint> ProcessIds { get; set; }

            /// <summary>対象ウィンドウの観測情報。</summary>
            public WindowInfo WindowInfo { get; set; }

            /// <summary>分類済みのメニュー情報。分類前は null。</summary>
            public UiMenuInfo Menu { get; set; }

            /// <summary>失敗時のエラー結果。</summary>
            public McpToolResult Error { get; set; }
        }

        /// <summary>1 回の検出（DetectMenus）の結果。呼び出し側が「見落としの可能性」を判断できるよう検査数も返す。</summary>
        private sealed class MenuDetection
        {
            /// <summary>メニューと分類できたウィンドウ（Z 順、前面が先）。</summary>
            public List<UiMenuInfo> Menus { get; set; } = new List<UiMenuInfo>();

            /// <summary>前フィルタを通過し、実際に内容分類を試みたウィンドウ数。</summary>
            public int InspectedCount { get; set; }

            /// <summary>候補数が上限（_MAX_MENU_CANDIDATES）に達して打ち切ったか。</summary>
            public bool IsTruncated { get; set; }
        }

        /// <summary>Extended (Phase 9): ui_menu_close が閉じにいく対象。Error が非 null なら他は未設定。</summary>
        private sealed class MenuCloseTarget
        {
            /// <summary>分類済みのメニュー情報。</summary>
            public UiMenuInfo Menu { get; set; }

            /// <summary>閉鎖判定に使う開始時のスナップショット。</summary>
            public UiMenuFingerprint Fingerprint { get; set; }

            /// <summary>メニューの Owner（Win32 メニューは GUI スレッドのアクティブウィンドウ）。特定できない場合は 0。</summary>
            public long OwnerHandle { get; set; }

            /// <summary>失敗時のエラーメッセージ。</summary>
            public string Error { get; set; }
        }

        /// <summary>Extended (Phase 9): outside click の実行結果。Error が非 null ならクリックしていない。</summary>
        private sealed class MenuOutsideClick
        {
            /// <summary>クリックした点の X（スクリーン物理 px）。</summary>
            public int X { get; set; }

            /// <summary>クリックした点の Y（スクリーン物理 px）。</summary>
            public int Y { get; set; }

            /// <summary>点を決めた根拠（タイトルバーまたは要素）。</summary>
            public string Source { get; set; }

            /// <summary>押した対象の種別（titleBar / rootElement）。</summary>
            public string ClickTarget { get; set; }

            /// <summary>点の探索で分かったこと（試した候補数と覆っていたウィンドウ）。探索前に失敗した場合は null。</summary>
            public UiOutsideClickDiagnostics Diagnostics { get; set; }

            /// <summary>クリックできなかった理由。</summary>
            public string Error { get; set; }
        }

        /// <summary>Extended (Phase 9): ui_menu_select_path の 1 段分の実行結果。Error が非 null なら他は未設定のことがある。</summary>
        private sealed class MenuPathStep
        {
            /// <summary>この段で操作したメニューの HWND。</summary>
            public long MenuHandle { get; set; }

            /// <summary>この段で操作したメニューのプロセス ID。</summary>
            public uint ProcessId { get; set; }

            /// <summary>この段のメニューの fingerprint（最後に閉鎖を確認するため）。</summary>
            public UiMenuFingerprint Fingerprint { get; set; }

            /// <summary>解決した項目。</summary>
            public UiMenuItemInfo Item { get; set; }

            /// <summary>実行方式の名前。</summary>
            public string Method { get; set; }

            /// <summary>展開の直前にカーソルを項目上へ移動したか。</summary>
            public bool IsCursorMoved { get; set; }

            /// <summary>実行前に開いていたメニューの HWND 集合（中間段でサブメニューの出現を判定するため）。</summary>
            public HashSet<long> MenuHandlesBefore { get; set; }

            /// <summary>失敗時のエラーメッセージ。</summary>
            public string Error { get; set; }
        }

        /// <summary>Extended (Phase 9): メニュー項目の実行結果。Error が非 null なら実行していない。</summary>
        private sealed class MenuExecution
        {
            /// <summary>実行方式の名前。実行できなかった場合は null。</summary>
            public string Method { get; set; }

            /// <summary>展開の直前にカーソルを項目上へ移動したか。</summary>
            public bool IsCursorMoved { get; set; }

            /// <summary>カーソルを移動しなかった場合の理由。移動した場合や関係しない実行方式では null。</summary>
            public string Note { get; set; }

            /// <summary>実行できなかった場合のエラーメッセージ。</summary>
            public string Error { get; set; }
        }

        /// <summary>
        /// Extended (Phase 9b): サブメニュー候補の検出結果。候補が無い場合は Menus が空で Detection は null。
        /// </summary>
        private sealed class SubmenuDetection
        {
            /// <summary>サブメニュー候補（Z 順、前面が先）。</summary>
            public List<UiMenuInfo> Menus { get; set; } = new List<UiMenuInfo>();

            /// <summary>
            /// 先頭の候補の見つけ方。newHandle は展開前に無かった HWND、ownerMatch は既にあった HWND を Owner 一致で採ったこと
            /// （WPF の HWND 再利用）を表す。候補が無ければ null。
            /// </summary>
            public string Detection { get; set; }
        }

        /// <summary>Extended (Phase 9): 閉鎖を確認する対象のメニュー 1 件。</summary>
        private sealed class MenuCloseWatch
        {
            /// <summary>監視するメニューの HWND。</summary>
            public long Handle { get; set; }

            /// <summary>開始時のプロセス ID。</summary>
            public uint ProcessId { get; set; }

            /// <summary>開始時の内容スナップショット。</summary>
            public UiMenuFingerprint Fingerprint { get; set; }
        }

        /// <summary>
        /// handle 引数を Phase 4 の検証（範囲 → IsWindow → GA_ROOT → デバッグ対象 PID 再照合）に通し、WindowInfo を取得する。
        /// 分類はまだ行わない（select は同じ STA 呼び出しの中で分類するため）。
        /// </summary>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        /// <param name="InArgs">ツール引数（handle）。</param>
        /// <returns>解決結果。</returns>
        private static async Task<ResolvedMenu> ResolveMenuWindowAsync(VsServiceAccessor InAccessor, JObject InArgs)
        {
            long? TheHandle = InArgs.Value<long?>("handle");
            if (!TheHandle.HasValue)
            {
                return new ResolvedMenu { Error = McpToolResult.Error("Parameter 'handle' is required (use the 'handle' value returned by ui_menu_detect)") };
            }
            if (!UiWindowTools.IsHandleInRange(TheHandle.Value))
            {
                return new ResolvedMenu { Error = McpToolResult.Error($"Window handle {TheHandle.Value} is out of range for a window handle.") };
            }

            HashSet<uint> TheProcessIds = await UiWindowTools.GetDebuggedProcessIdsAsync(InAccessor);
            if (TheProcessIds.Count == 0)
            {
                return new ResolvedMenu { Error = McpToolResult.Error(DebuggeeWindowResolver.NoDebuggedProcessMessage) };
            }

            string TheHandleError = DebuggeeWindowResolver.ValidateAndNormalizeWindowHandle(TheHandle.Value, TheProcessIds, out IntPtr TheNormalized);
            if (TheHandleError != null)
            {
                return new ResolvedMenu { Error = McpToolResult.Error(TheHandleError) };
            }

            long TheNormalizedHandle = TheNormalized.ToInt64();
            WindowInfo TheWindowInfo = await UiWindowTools.FindWindowInfoAsync(TheProcessIds, TheNormalizedHandle);
            if (TheWindowInfo == null)
            {
                return new ResolvedMenu { Error = McpToolResult.Error($"Window handle {TheNormalizedHandle} disappeared before it could be inspected.") };
            }

            return new ResolvedMenu { Window = TheNormalized, ProcessIds = TheProcessIds, WindowInfo = TheWindowInfo };
        }

        /// <summary>handle を検証してから内容を分類する（get_info / capture / wait_closed の入口）。</summary>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        /// <param name="InArgs">ツール引数（handle）。</param>
        /// <returns>解決結果。</returns>
        private static async Task<ResolvedMenu> ResolveMenuAsync(VsServiceAccessor InAccessor, JObject InArgs)
        {
            ResolvedMenu TheResolved = await ResolveMenuWindowAsync(InAccessor, InArgs);
            if (TheResolved.Error != null)
            {
                return TheResolved;
            }

            WindowInfo TheWindowInfo = TheResolved.WindowInfo;
            try
            {
                (UiMenuInfo TheMenu, string TheReason) = await UiTools.RunUiaWithTimeoutAsync(() =>
                {
                    bool IsMenu = UiPopupMenuResolver.Classify(TheWindowInfo, out UiMenuInfo TheClassified, out string TheClassifyReason);
                    return (IsMenu ? TheClassified : null, TheClassifyReason);
                });
                if (TheMenu == null)
                {
                    TheResolved.Error = McpToolResult.Error(DescribeNotAMenu(TheWindowInfo, TheReason));
                    return TheResolved;
                }
                TheResolved.Menu = TheMenu;
                return TheResolved;
            }
            catch (TimeoutException TheException)
            {
                TheResolved.Error = McpToolResult.Error(TheException.Message);
                return TheResolved;
            }
            catch (Exception TheException)
            {
                TheResolved.Error = McpToolResult.Error($"Failed to inspect popup menu {TheWindowInfo.Handle}: {TheException.Message}");
                return TheResolved;
            }
        }

        /// <summary>メニューとして分類できなかったときのエラー文。</summary>
        /// <param name="InWindow">対象ウィンドウ。</param>
        /// <param name="InReason">分類できなかった理由。</param>
        /// <returns>エラーメッセージ。</returns>
        private static string DescribeNotAMenu(WindowInfo InWindow, string InReason)
        {
            return $"Window handle {InWindow.Handle} is not a popup menu (className='{InWindow.ClassName}', reason={InReason}).";
        }

        /// <summary>
        /// 可視トップレベルウィンドウを内容検査してメニューを集める。ownerWindowHandle 指定時は、その Owner を持つウィンドウと、
        /// Owner を持たない同一 GUI スレッドのポップアップだけを対象にする。UIA 照会は高価なので、その前に安価な前フィルタ
        /// （<see cref="IsMenuWindowCandidate"/>）で明らかにメニューではないウィンドウを落とし、候補数の上限はフィルタ後にだけ適用する。
        /// STA スレッドで呼ぶこと。
        /// </summary>
        /// <param name="InProcessIds">デバッグ中プロセス ID の集合。</param>
        /// <param name="InOwnerWindowHandle">絞り込む Owner の HWND。指定しない場合は null。</param>
        /// <param name="InIsUnknownIncluded">unknownPopupMenu も含めるか。</param>
        /// <returns>検出結果（メニュー一覧は Z 順、前面が先）。</returns>
        private static MenuDetection DetectMenus(HashSet<uint> InProcessIds, long? InOwnerWindowHandle, bool InIsUnknownIncluded)
        {
            MenuDetection TheDetection = new MenuDetection();
            List<WindowInfo> TheWindows = DebuggeeWindowEnumerator.EnumerateTopLevelWindows(InProcessIds, false);

            uint TheOwnerThreadId = 0;
            if (InOwnerWindowHandle.HasValue)
            {
                WindowInfo TheOwnerWindow = TheWindows.FirstOrDefault(TheCandidate => TheCandidate.Handle == InOwnerWindowHandle.Value);
                TheOwnerThreadId = TheOwnerWindow == null ? 0 : TheOwnerWindow.ThreadId;
            }

            foreach (WindowInfo TheCandidate in TheWindows)
            {
                if (InOwnerWindowHandle.HasValue)
                {
                    if (TheCandidate.Handle == InOwnerWindowHandle.Value)
                    {
                        continue;
                    }
                    bool IsOwnedByRequested = TheCandidate.OwnerHandle == InOwnerWindowHandle.Value;
                    bool IsOwnerlessSameThread = TheCandidate.OwnerHandle == 0 && TheOwnerThreadId != 0 && TheCandidate.ThreadId == TheOwnerThreadId;
                    if (!IsOwnedByRequested && !IsOwnerlessSameThread)
                    {
                        continue;
                    }
                }
                if (!IsMenuWindowCandidate(TheCandidate))
                {
                    continue;
                }

                if (TheDetection.InspectedCount >= _MAX_MENU_CANDIDATES)
                {
                    TheDetection.IsTruncated = true;
                    break;
                }
                TheDetection.InspectedCount++;

                if (!UiPopupMenuResolver.Classify(TheCandidate, out UiMenuInfo TheMenu, out _))
                {
                    continue;
                }
                if (!UiPopupMenuResolver.IsKnownMenuType(TheMenu.MenuType) && !InIsUnknownIncluded)
                {
                    continue;
                }
                TheDetection.Menus.Add(TheMenu);
            }

            // Extended (Phase 10): 走査した候補数・打ち切り・採用件数を診断へ残す（検出結果は変えない）
            MenuDetection TheResult = TheDetection;
            DiagnosticHub.Emit(DiagnosticLevel.Info, DiagnosticCategory.MENU, "menu.detect", InData =>
            {
                InData["inspectedCount"] = TheResult.InspectedCount;
                InData["truncated"] = TheResult.IsTruncated;
                InData["menuCount"] = TheResult.Menus.Count;
                InData["ownerWindowHandle"] = InOwnerWindowHandle;
                InData["includeUnknown"] = InIsUnknownIncluded;
            });
            return TheDetection;
        }

        /// <summary>Extended (Phase 10): メニュー項目の実行方式と成否を診断へ残す（項目名の本文は includeUiText のときだけ）。</summary>
        /// <param name="InMenu">対象メニュー。</param>
        /// <param name="InItem">対象項目。</param>
        /// <param name="InExecution">実行結果。</param>
        private static void EmitMenuSelect(UiMenuInfo InMenu, UiMenuItemInfo InItem, MenuExecution InExecution)
        {
            bool HasFailed = InExecution.Method == null;
            DiagnosticHub.Emit(HasFailed ? DiagnosticLevel.Warning : DiagnosticLevel.Info, DiagnosticCategory.MENU, "menu.select", InData =>
            {
                InData["menuHandle"] = InMenu.Handle;
                InData["menuType"] = InMenu.MenuType;
                InData["method"] = InExecution.Method;
                InData["itemId"] = InItem.Id;
                InData["automationId"] = InItem.AutomationId;
                InData["hasSubmenu"] = InItem.HasSubmenu;
                InData["cursorMoved"] = InExecution.IsCursorMoved;
                InData["itemNameLength"] = InItem.Name == null ? 0 : InItem.Name.Length;
                InData["itemName"] = DiagnosticSanitizer.SanitizeUiText(InItem.Name, DiagnosticHub.Settings);
                InData["failed"] = HasFailed;
            });
        }

        /// <summary>Extended (Phase 10): 展開後のサブメニュー検出の結果を診断へ残す。</summary>
        /// <param name="InParentMenuHandle">展開元のメニュー HWND。</param>
        /// <param name="InDetection">検出結果。</param>
        private static void EmitMenuSubmenu(long InParentMenuHandle, SubmenuDetection InDetection)
        {
            DiagnosticHub.Emit(DiagnosticLevel.Info, DiagnosticCategory.MENU, "menu.submenu", InData =>
            {
                InData["parentMenuHandle"] = InParentMenuHandle;
                InData["submenuDetection"] = InDetection.Detection;
                InData["candidateCount"] = InDetection.Menus.Count;
                InData["submenuHandle"] = InDetection.Menus.Count > 0 ? (long?)InDetection.Menus[0].Handle : null;
            });
        }

        /// <summary>
        /// 内容分類（UIA 照会）を試す価値があるウィンドウかを、列挙済みの観測値だけで安価に判定する。
        /// ポップアップメニューは Win32 メニュークラス（#32768）か、Owner を持つか、タイトルが空のいずれかになる
        /// （WPF の ContextMenu は Owner 付き・タイトル空の HwndWrapper[...]）。
        /// </summary>
        /// <param name="InWindow">判定するウィンドウ。</param>
        /// <returns>分類を試みる候補なら true。</returns>
        private static bool IsMenuWindowCandidate(WindowInfo InWindow)
        {
            if (string.Equals(InWindow.ClassName, UiPopupMenuResolver.Win32MenuClassName, StringComparison.Ordinal))
            {
                return true;
            }
            return InWindow.OwnerHandle != 0 || string.IsNullOrEmpty(InWindow.Title);
        }

        /// <summary>
        /// ui_menu_select の STA 本体。安全境界 → 再分類 → 項目解決 → 所属 HWND 一致 → 有効性 → 実行 → サブメニュー再検出 → 閉鎖確認。
        /// </summary>
        /// <param name="InResolved">検証済みのメニューウィンドウ。</param>
        /// <param name="InSelector">項目セレクター。</param>
        /// <param name="InCloseWaitMs">実行後に閉鎖を確認する時間（ミリ秒）。</param>
        /// <returns>実行結果、またはエラー。</returns>
        private static McpToolResult SelectMenuItem(ResolvedMenu InResolved, UiMenuItemSelector InSelector, int InCloseWaitMs)
        {
            string TheContextError = UiInteractionContext.TryCreate(InResolved.Window, InResolved.ProcessIds, out UiInteractionContext TheContext);
            if (TheContextError != null)
            {
                return McpToolResult.Error(TheContextError);
            }

            string TheResolveError = ResolveMenuAction(TheContext, InSelector, "ui_menu_select",
                out UiMenuInfo TheMenu, out UiMenuItemInfo TheItem, out AutomationElement TheElement);
            if (TheResolveError != null)
            {
                return McpToolResult.Error(TheResolveError);
            }

            UiMenuFingerprint TheFingerprint = UiPopupMenuResolver.BuildFingerprint(TheMenu);
            // サブメニューは「実行後に新しく現れたメニュー」だけを採用するため、実行前に開いているメニューの HWND を控える
            HashSet<long> TheMenuHandlesBeforeExecute = TheItem.HasSubmenu
                ? new HashSet<long>(DetectMenus(InResolved.ProcessIds, null, false).Menus.Select(TheOpenMenu => TheOpenMenu.Handle))
                : null;

            // 入力の直前に DPI / モニター情報を取り直し、物理クリックではメニュー自身が最前面であることを許容する。
            // 開いているメニューは既に最前面で、SetForegroundWindow を呼ぶとメニューが取り下げられてしまうため前面化はしない。
            TheContext.RefreshGeometry();
            TheContext.AllowPointRoot(TheMenu.Handle);
            TheContext.IsForegroundEnsured = false;

            MenuExecution TheExecution = ExecuteMenuItem(TheContext, TheMenu, TheItem, TheElement);
            EmitMenuSelect(TheMenu, TheItem, TheExecution); // Extended (Phase 10): 実行方式と成否を診断へ残す
            if (TheExecution.Method == null)
            {
                return McpToolResult.Error(TheExecution.Error);
            }
            string TheMethod = TheExecution.Method;

            UiMenuInfo TheSubmenu = null;
            List<long> TheSubmenuCandidates = null;
            string TheSubmenuDetection = null;
            string TheNote = TheExecution.Note;
            if (TheItem.HasSubmenu)
            {
                SubmenuDetection TheCandidates = WaitNewMenus(InResolved.ProcessIds, TheMenuHandlesBeforeExecute, TheMenu.Handle,
                    _SUBMENU_WAIT_TIMEOUT_MS, _SUBMENU_POLL_MS);
                if (TheCandidates.Menus.Count > 0)
                {
                    // 同時に複数現れた場合は先頭（Z 順で前面）を採用し、判断材料として全 HWND も返す
                    TheSubmenu = TheCandidates.Menus[0];
                    TheSubmenuCandidates = TheCandidates.Menus.ConvertAll(TheNewMenu => TheNewMenu.Handle);
                    TheSubmenuDetection = TheCandidates.Detection;
                }
                else
                {
                    // 開かなかったこと自体は失敗にしない（項目の実行は成功している）
                    string TheSubmenuNote = $"submenu did not appear within {_SUBMENU_WAIT_TIMEOUT_MS} ms";
                    TheNote = string.IsNullOrEmpty(TheNote) ? TheSubmenuNote : TheNote + "; " + TheSubmenuNote;
                }
            }

            bool IsMenuClosed = WaitMenuClosed(InResolved.ProcessIds, TheMenu.Handle, TheMenu.ProcessId, TheFingerprint, InCloseWaitMs, _CLOSE_POLL_MS,
                out long TheCloseWaitElapsedMs);

            return McpToolResult.Success(new
            {
                message = $"Selected menu item '{TheItem.Name}' (index {TheItem.Index}) in menu {TheMenu.Handle} using {TheMethod}",
                method = TheMethod,
                menuHandle = TheMenu.Handle,
                item = TheItem,
                // Extended (Phase 9): 展開の直前にカーソルを項目上へ置いたか（WPF のサブメニューが hover で閉じる問題への対処）
                cursorMoved = TheExecution.IsCursorMoved,
                menuClosed = IsMenuClosed,
                closeWaitMs = TheCloseWaitElapsedMs,
                submenuOpened = TheSubmenu != null,
                submenu = TheSubmenu,
                submenuCandidates = TheSubmenuCandidates,
                // Extended (Phase 9b): サブメニューを新規 HWND で見つけたか、Owner 一致（HWND 再利用）で見つけたか
                submenuDetection = TheSubmenuDetection,
                note = TheNote,
            });
        }

        /// <summary>
        /// Extended (Phase 9): 実行対象のメニュー項目を 1 件に決めるまでの共通処理（再分類 → 種別確認 → 項目解決 → 所属 HWND 一致 → 有効性 →
        /// UIA 要素の対応付け）。ui_menu_select と ui_menu_select_path が同じ順序・同じ文言を共有する。
        /// 項目が Win32 のメニュー API だけから組み立てられている場合は対応する UIA 要素が無いので OutElement は null になり、
        /// 実行方式の決定（Win32 矩形での物理クリック fallback）は <see cref="ExecuteMenuItem"/> が行う。STA スレッドで呼ぶこと。
        /// </summary>
        /// <param name="InContext">対象メニューの Interaction Context。</param>
        /// <param name="InSelector">項目セレクター。</param>
        /// <param name="InToolName">エラー文に載せるツール名（ui_menu_select / ui_menu_select_path）。</param>
        /// <param name="OutMenu">再分類したメニュー情報。分類できなかった場合は null。</param>
        /// <param name="OutItem">決まった項目。エラー時は null。</param>
        /// <param name="OutElement">項目の UIA 要素。UIA 要素が無い項目では null。</param>
        /// <returns>エラーメッセージ。正常なら null。</returns>
        private static string ResolveMenuAction(UiInteractionContext InContext, UiMenuItemSelector InSelector, string InToolName,
            out UiMenuInfo OutMenu, out UiMenuItemInfo OutItem, out AutomationElement OutElement)
        {
            OutMenu = null;
            OutItem = null;
            OutElement = null;

            if (!UiPopupMenuResolver.Classify(InContext.WindowInfo, out UiMenuInfo TheMenu, out string TheReason, out List<AutomationElement> TheItemElements))
            {
                return DescribeNotAMenu(InContext.WindowInfo, TheReason);
            }
            OutMenu = TheMenu;
            if (!UiPopupMenuResolver.IsKnownMenuType(TheMenu.MenuType))
            {
                return $"Window handle {TheMenu.Handle} is an unknown popup menu (menuType={TheMenu.MenuType}); {InToolName} only acts on menus classified as " +
                    "win32Menu or wpfContextMenu.";
            }

            string TheSelectError = UiPopupMenuResolver.TrySelectItem(TheMenu, InSelector, out UiMenuItemInfo TheItem);
            if (TheSelectError != null)
            {
                return TheSelectError;
            }
            if (TheItem.RootWindowHandle == 0)
            {
                // Phase 7 の要素解決と同じく、所属するトップレベルウィンドウが分からない項目は操作しない
                return $"Menu item '{TheItem.Name}' (index {TheItem.Index}) in menu {TheMenu.Handle} has no resolvable top-level window " +
                    "(no ancestor exposes a native window handle); refusing to act on it";
            }
            if (!IsItemInsideMenu(TheMenu, TheItem))
            {
                // Owner の部分木から項目を解決したメニューでは、許可されるトップレベル HWND が 2 つあることをエラー文にも出す
                string TheAllowedWindows = string.Equals(TheMenu.ItemsSource, UiPopupMenuResolver.ItemsSourceOwnerSubtree, StringComparison.Ordinal)
                    ? $"the requested menu {TheMenu.Handle} nor to its owner menu {TheMenu.OwnerHandle}"
                    : $"the requested menu {TheMenu.Handle}";
                return $"Menu item '{TheItem.Name}' (index {TheItem.Index}) belongs to window {TheItem.RootWindowHandle}, not to {TheAllowedWindows}; " +
                    "refusing to act on it";
            }
            if (!TheItem.IsEnabled)
            {
                return $"Menu item '{TheItem.Name}' (index {TheItem.Index}) in menu {TheMenu.Handle} is disabled (IsEnabled=false); refusing to act on it";
            }

            OutItem = TheItem;
            OutElement = TheItem.Index < TheItemElements.Count ? TheItemElements[TheItem.Index] : null;
            return null;
        }

        /// <summary>
        /// Extended (Phase 9c): 解決した項目が操作対象のメニューに属するか判定する。通常は項目のトップレベル HWND がメニュー自身で
        /// あることを求めるが、Owner の部分木から項目を解決したメニュー（itemsSource="ownerSubtree"）では項目の UIA 上のトップレベルが
        /// Owner（親メニュー）になるため、そのメニューに限り Owner も許可する（候補ウィンドウの矩形による絞り込みは分類時に済んでいる）。
        /// </summary>
        /// <param name="InMenu">操作対象のメニュー。</param>
        /// <param name="InItem">解決した項目。</param>
        /// <returns>そのメニューの項目として操作してよいなら true。</returns>
        private static bool IsItemInsideMenu(UiMenuInfo InMenu, UiMenuItemInfo InItem)
        {
            if (InItem.RootWindowHandle == InMenu.Handle)
            {
                return true;
            }
            return string.Equals(InMenu.ItemsSource, UiPopupMenuResolver.ItemsSourceOwnerSubtree, StringComparison.Ordinal)
                && InMenu.OwnerHandle != 0
                && InItem.RootWindowHandle == InMenu.OwnerHandle;
        }

        /// <summary>
        /// メニュー項目を実行する。サブメニュー付きの項目は「カーソルを項目上へ移動 → ExpandCollapsePattern」を先に試し、続いて InvokePattern、
        /// LegacyIAccessible の DoDefaultAction、最後に座標ゲートを通した物理クリックの順に試す。
        /// Extended (Phase 9): UIA 要素が無い項目（Win32 のメニュー API だけから組み立てた項目）は、
        /// GetMenuItemRect の矩形を使った物理クリックへ fallback する。
        /// </summary>
        /// <param name="InContext">対象メニューの Interaction Context。</param>
        /// <param name="InMenu">対象メニュー。</param>
        /// <param name="InItem">対象項目。</param>
        /// <param name="InElement">対象項目の UIA 要素。無い場合は null。</param>
        /// <returns>実行結果（方式・カーソル移動の有無）。実行できなければ Error 付き。</returns>
        private static MenuExecution ExecuteMenuItem(UiInteractionContext InContext, UiMenuInfo InMenu, UiMenuItemInfo InItem, AutomationElement InElement)
        {
            if (InElement == null)
            {
                string TheWin32Method = ExecuteMenuItemByWin32Rect(InContext, InMenu, InItem, out string TheWin32Error);

                // Extended (Phase 10): UIA 要素が無く Win32 の矩形クリックへ落ちた経路を warning として残す
                DiagnosticHub.Emit(DiagnosticLevel.Warning, DiagnosticCategory.MENU, "menu.fallback", InData =>
                {
                    InData["kind"] = "win32Rect";
                    InData["menuHandle"] = InMenu.Handle;
                    InData["itemId"] = InItem.Id;
                    InData["selected"] = TheWin32Method != null;
                    InData["notReached"] = TheWin32Error;
                });
                DiagnosticErrorDump.Schedule(DiagnosticScope.Current, null, DiagnosticLevel.Warning, "menu.fallback", InMenu.Handle);
                return new MenuExecution { Method = TheWin32Method, Error = TheWin32Error };
            }
            if (InItem.HasSubmenu)
            {
                // WPF はカーソルが別項目の上にあると hover で選択が移り、展開したサブメニューがすぐ閉じる（Phase 9 verify 実測）。
                // そのため展開の直前にカーソルを対象項目の中心へ置く（クリックはせず、位置も復元しない）。
                bool IsCursorMoved = TryMoveCursorToMenuItem(InMenu, InItem, out string TheCursorNote);
                if (UiPopupMenuResolver.TryExpand(InElement))
                {
                    return new MenuExecution { Method = _METHOD_EXPAND_COLLAPSE, IsCursorMoved = IsCursorMoved, Note = TheCursorNote };
                }
            }
            if (UiPopupMenuResolver.TryInvoke(InElement))
            {
                return new MenuExecution { Method = _METHOD_INVOKE };
            }
            if (UiPopupMenuResolver.TryLegacyDoDefaultAction(InElement))
            {
                return new MenuExecution { Method = _METHOD_LEGACY };
            }

            string ThePrerequisiteError = UiPopupMenuResolver.DescribePhysicalPrerequisite(InMenu, InItem);
            if (ThePrerequisiteError != null)
            {
                return new MenuExecution
                {
                    Error = "The menu item does not support UI Automation invocation and a physical click is not possible: " + ThePrerequisiteError,
                };
            }
            UiPopupMenuResolver.TryGetBoundsCenter(InItem.Bounds, out int TheClickX, out int TheClickY);

            string ThePointError = InContext.PreparePhysicalPoint(TheClickX, TheClickY, "menu item click point");
            if (ThePointError != null)
            {
                return new MenuExecution { Error = ThePointError };
            }

            bool HasSavedCursor = InContext.TrySaveCursor(out POINT TheSavedCursor);
            UiTools.PerformClick(TheClickX, TheClickY, false);
            if (HasSavedCursor)
            {
                InContext.RestoreCursor(TheSavedCursor);
            }
            return new MenuExecution { Method = _METHOD_PHYSICAL_CLICK };
        }

        /// <summary>
        /// Extended (Phase 9): サブメニューを展開する直前に、カーソルを対象項目の中心（物理 px）へ置く。
        /// WPF のメニューはカーソルの hover でも選択項目を変えるため、別項目の上にカーソルがあると展開したサブメニューが閉じてしまう。
        /// 移動先の最前面がメニュー自身でない場合は移動しない（他ウィンドウの上でカーソルを動かさないため）。クリックはしない。
        /// STA スレッドで呼ぶこと。
        /// </summary>
        /// <param name="InMenu">対象メニュー。</param>
        /// <param name="InItem">対象項目。</param>
        /// <param name="OutNote">移動しなかった場合の理由。移動できた場合は null。</param>
        /// <returns>カーソルを移動したら true。</returns>
        private static bool TryMoveCursorToMenuItem(UiMenuInfo InMenu, UiMenuItemInfo InItem, out string OutNote)
        {
            OutNote = null;
            if (!UiPopupMenuResolver.TryGetBoundsCenter(InItem.Bounds, out int TheX, out int TheY))
            {
                OutNote = $"the cursor was not moved before expanding '{InItem.Name}' (the item has no bounding rectangle)";
                return false;
            }

            long TheTopmost = UiWindowActionValidator.ResolveTopmostRoot(TheX, TheY);
            if (TheTopmost != InMenu.Handle)
            {
                OutNote = $"the cursor was not moved before expanding '{InItem.Name}' (the window at ({TheX}, {TheY}) is {TheTopmost}, not the menu {InMenu.Handle})";
                return false;
            }
            if (!UiTools.WithDpiAwareness(() => SetCursorPos(TheX, TheY)))
            {
                OutNote = $"the cursor could not be moved to ({TheX}, {TheY}) before expanding '{InItem.Name}'";
                return false;
            }
            return true;
        }

        /// <summary>
        /// Extended (Phase 9): UIA 要素を持たない Win32 メニュー項目を、GetMenuItemRect の矩形中心で物理クリックする。
        /// 前提（HMENU 上の位置がある / HMENU が取れている / 項目が有効 / 矩形が空でなくメニューの矩形に収まる / デバッグ対象 PID /
        /// その点の最前面がメニュー自身）がすべて満たされた場合だけ実行し、1 つでも欠ければクリックせずエラーにする。
        /// 通常の環境では UIA から項目が取れるためこの経路には入らない。STA スレッドで呼ぶこと。
        /// </summary>
        /// <param name="InContext">対象メニューの Interaction Context。</param>
        /// <param name="InMenu">対象メニュー。</param>
        /// <param name="InItem">対象項目。</param>
        /// <param name="OutError">実行できなかった場合のエラーメッセージ。</param>
        /// <returns>実行方式の名前。実行できなければ null。</returns>
        private static string ExecuteMenuItemByWin32Rect(UiInteractionContext InContext, UiMenuInfo InMenu, UiMenuItemInfo InItem, out string OutError)
        {
            OutError = null;
            string ThePrerequisiteError = DescribeWin32RectPrerequisite(InContext, InMenu, InItem, out int TheClickX, out int TheClickY);
            if (ThePrerequisiteError != null)
            {
                // 項目が Win32 のメニュー API だけから組み立てられた場合（UIA が項目を返さない）は、押せる UIA 要素が無い
                string TheMissingElementReason = string.IsNullOrEmpty(InMenu.Note)
                    ? "the element disappeared while it was being resolved"
                    : InMenu.Note;
                OutError =
                    $"Menu item '{InItem.Name}' (index {InItem.Index}) in menu {InMenu.Handle} has no UI Automation element to invoke ({TheMissingElementReason}) " +
                    $"and the Win32 menu rectangle cannot be used safely ({ThePrerequisiteError}); refusing physical fallback. " +
                    "Call ui_menu_get_info to re-inspect the menu and retry.";
                return null;
            }

            bool HasSavedCursor = InContext.TrySaveCursor(out POINT TheSavedCursor);
            UiTools.PerformClick(TheClickX, TheClickY, false);
            if (HasSavedCursor)
            {
                InContext.RestoreCursor(TheSavedCursor);
            }
            return _METHOD_PHYSICAL_CLICK_WIN32_RECT;
        }

        /// <summary>
        /// Extended (Phase 9): Win32 矩形による物理クリックの前提を順に確認し、クリックしてよい点を決める。
        /// 座標ゲート（メニューの矩形内 → その点の最前面がメニュー自身）は <see cref="UiInteractionContext.PreparePhysicalPoint"/> に任せる。
        /// </summary>
        /// <param name="InContext">対象メニューの Interaction Context。</param>
        /// <param name="InMenu">対象メニュー。</param>
        /// <param name="InItem">対象項目。</param>
        /// <param name="OutX">クリックする点の X（スクリーン物理 px）。</param>
        /// <param name="OutY">クリックする点の Y（スクリーン物理 px）。</param>
        /// <returns>前提を満たさない理由。満たしていれば null。</returns>
        private static string DescribeWin32RectPrerequisite(UiInteractionContext InContext, UiMenuInfo InMenu, UiMenuItemInfo InItem, out int OutX, out int OutY)
        {
            OutX = 0;
            OutY = 0;
            if (!InItem.Win32Position.HasValue)
            {
                return "the item has no position in the Win32 menu (HMENU)";
            }
            if (InMenu.Win32MenuHandle == 0)
            {
                return "the menu window did not report an HMENU";
            }
            if (!InItem.IsEnabled)
            {
                return "the item is disabled";
            }

            IntPtr TheMenuWindow = new IntPtr(InMenu.Handle);
            // 矩形は物理 px で受け取る（DPI 仮想化された文脈で呼ぶと論理 px に縮約され、クリック点がずれる）
            RECT TheItemRect = new RECT();
            if (!UiTools.WithDpiAwareness(() =>
                GetMenuItemRect(TheMenuWindow, new IntPtr(InMenu.Win32MenuHandle), (uint)InItem.Win32Position.Value, out TheItemRect)))
            {
                return $"GetMenuItemRect failed for position {InItem.Win32Position.Value}";
            }
            int TheWidth = TheItemRect.Right - TheItemRect.Left;
            int TheHeight = TheItemRect.Bottom - TheItemRect.Top;
            if (TheWidth <= 0 || TheHeight <= 0)
            {
                return "the item rectangle is empty";
            }
            if (!UiPopupMenuResolver.TryGetBoundsRect(InMenu.Bounds, out Rect TheMenuRect))
            {
                return "the menu rectangle could not be read";
            }
            Rect TheItemBounds = new Rect(TheItemRect.Left, TheItemRect.Top, TheWidth, TheHeight);
            if (!TheMenuRect.Contains(TheItemBounds))
            {
                return $"the item rectangle ({TheItemRect.Left},{TheItemRect.Top},{TheWidth},{TheHeight}) is not inside the menu rectangle ({InMenu.Bounds})";
            }

            GetWindowThreadProcessId(TheMenuWindow, out uint TheProcessId);
            if (InContext.ProcessIds == null || !InContext.ProcessIds.Contains(TheProcessId))
            {
                return $"the menu window no longer belongs to a debugged process (processId {TheProcessId})";
            }

            int TheClickX = TheItemRect.Left + TheWidth / 2;
            int TheClickY = TheItemRect.Top + TheHeight / 2;
            string ThePointError = InContext.PreparePhysicalPoint(TheClickX, TheClickY, "menu item click point");
            if (ThePointError != null)
            {
                return ThePointError;
            }

            OutX = TheClickX;
            OutY = TheClickY;
            return null;
        }

        /// <summary>
        /// Extended: 実行後にサブメニューが現れるのを待つ。WPF のサブメニューは ExpandCollapsePattern.Expand の直後に、
        /// 親ポップアップを Owner とするトップレベル HWND として現れるが、出現までの時間は一定ではない（Phase 8 実測）。
        /// 固定待ちだと取りこぼすため、一定間隔で再検出し、1 件でも現れた時点で打ち切る。STA スレッドで呼ぶこと。
        /// </summary>
        /// <param name="InProcessIds">デバッグ中プロセス ID の集合。</param>
        /// <param name="InMenuHandlesBefore">実行前に開いていたメニューの HWND 集合。</param>
        /// <param name="InParentMenuHandle">展開元のメニューの HWND（Owner 一致でサブメニューを見つけるため）。</param>
        /// <param name="InTimeoutMs">出現を待つ上限時間（ミリ秒）。</param>
        /// <param name="InPollIntervalMs">再検出の間隔（ミリ秒）。</param>
        /// <returns>サブメニュー候補と、その見つけ方。上限時間内に現れなければ候補は空。</returns>
        private static SubmenuDetection WaitNewMenus(HashSet<uint> InProcessIds, HashSet<long> InMenuHandlesBefore, long InParentMenuHandle,
            int InTimeoutMs, int InPollIntervalMs)
        {
            Stopwatch TheStopwatch = Stopwatch.StartNew();
            while (true)
            {
                Thread.Sleep(InPollIntervalMs);
                SubmenuDetection TheCandidates = FindSubmenuCandidates(InProcessIds, InMenuHandlesBefore, InParentMenuHandle);
                if (TheCandidates.Menus.Count > 0)
                {
                    EmitMenuSubmenu(InParentMenuHandle, TheCandidates); // Extended (Phase 10)
                    return TheCandidates;
                }
                if (TheStopwatch.ElapsedMilliseconds >= InTimeoutMs)
                {
                    EmitMenuSubmenu(InParentMenuHandle, TheCandidates); // Extended (Phase 10): 現れなかったことも残す
                    return TheCandidates;
                }
            }
        }

        /// <summary>
        /// Extended (Phase 9b): 展開したサブメニューの候補を集める。WPF は 2 回目以降のサブメニューに前回と同じ popup HWND を
        /// 再利用するため、「展開前には無かった HWND」の差分だけでは 2 回目以降を検出できない（Phase 9b 実測: 初回のみ検出できていた）。
        /// そこで新規 HWND に加えて「Owner が展開元のメニューであるもの」も候補とし、既に開いていた HWND でも、内容（bounds）が
        /// 変わっていなくても採用する。無関係なメニューを掴まないため、可視かつ項目が 1 件以上のものだけを候補にする。
        /// 候補が複数ある場合は Owner 一致を優先し、その中でも新規 HWND（＝今回開いたことが確実なもの）を先頭に置く。
        /// STA スレッドで呼ぶこと。
        /// </summary>
        /// <param name="InProcessIds">デバッグ中プロセス ID の集合。</param>
        /// <param name="InMenuHandlesBefore">実行前に開いていたメニューの HWND 集合。null なら新規 HWND の判定を行わない。</param>
        /// <param name="InParentMenuHandle">展開元のメニューの HWND。0 なら Owner 一致の判定を行わない。</param>
        /// <returns>サブメニュー候補（Z 順、前面が先）と、その見つけ方。候補が無ければ一覧は空で見つけ方は null。</returns>
        private static SubmenuDetection FindSubmenuCandidates(HashSet<uint> InProcessIds, HashSet<long> InMenuHandlesBefore, long InParentMenuHandle)
        {
            MenuDetection TheDetection = DetectMenus(InProcessIds, null, false);
            List<UiMenuInfo> TheOwnedNewMenus = new List<UiMenuInfo>();
            List<UiMenuInfo> TheOwnedExistingMenus = new List<UiMenuInfo>();
            List<UiMenuInfo> TheNewMenus = new List<UiMenuInfo>();
            foreach (UiMenuInfo TheMenu in TheDetection.Menus)
            {
                if (TheMenu.Handle == InParentMenuHandle)
                {
                    // 展開元そのものはサブメニューではない
                    continue;
                }
                if (!TheMenu.IsVisible || TheMenu.Items.Count < 1)
                {
                    // 非表示の残骸や項目の無いポップアップをサブメニューと誤認しない
                    continue;
                }

                bool IsNewHandle = InMenuHandlesBefore != null && !InMenuHandlesBefore.Contains(TheMenu.Handle);
                bool IsOwnedByParent = InParentMenuHandle != 0 && TheMenu.OwnerHandle == InParentMenuHandle;
                if (IsOwnedByParent && IsNewHandle)
                {
                    TheOwnedNewMenus.Add(TheMenu);
                }
                else if (IsOwnedByParent)
                {
                    // HWND を再利用されたサブメニュー（2 回目以降）。展開前から同じ bounds でも Owner が親なら採る
                    TheOwnedExistingMenus.Add(TheMenu);
                }
                else if (IsNewHandle)
                {
                    // Owner が親ではない新規メニュー（Win32 の #32768 サブメニューなど）
                    TheNewMenus.Add(TheMenu);
                }
            }

            List<UiMenuInfo> TheCandidates = new List<UiMenuInfo>(TheOwnedNewMenus);
            TheCandidates.AddRange(TheOwnedExistingMenus);
            TheCandidates.AddRange(TheNewMenus);
            if (TheCandidates.Count == 0)
            {
                return new SubmenuDetection();
            }

            // 見つけ方は「先頭＝実際に採る候補」がどちらの規則で拾われたかを表す（HWND 再利用のときだけ ownerMatch になる）
            bool IsFirstCandidateNewHandle = TheOwnedNewMenus.Count > 0 || TheOwnedExistingMenus.Count == 0;
            return new SubmenuDetection
            {
                Menus = TheCandidates,
                Detection = IsFirstCandidateNewHandle ? _SUBMENU_DETECTION_NEW_HANDLE : _SUBMENU_DETECTION_OWNER_MATCH,
            };
        }

        /// <summary>
        /// メニューが閉じるのをポーリングで確認する（ui_menu_select の実行後確認）。STA スレッドで呼ぶこと。
        /// </summary>
        /// <param name="InProcessIds">デバッグ中プロセス ID の集合。</param>
        /// <param name="InMenuHandle">監視するメニューの HWND。</param>
        /// <param name="InInitialProcessId">開始時のプロセス ID。</param>
        /// <param name="InFingerprint">開始時の内容スナップショット。</param>
        /// <param name="InCloseWaitMs">確認に使う上限時間（ミリ秒）。</param>
        /// <param name="InPollIntervalMs">確認の間隔（ミリ秒）。</param>
        /// <param name="OutElapsedMs">確認に実際に要した時間（ミリ秒）。</param>
        /// <returns>閉じたと判定したら true。</returns>
        private static bool WaitMenuClosed(HashSet<uint> InProcessIds, long InMenuHandle, uint InInitialProcessId, UiMenuFingerprint InFingerprint,
            int InCloseWaitMs, int InPollIntervalMs, out long OutElapsedMs)
        {
            Stopwatch TheStopwatch = Stopwatch.StartNew();
            while (true)
            {
                if (DescribeMenuClosed(InProcessIds, InMenuHandle, InInitialProcessId, InFingerprint) != null)
                {
                    OutElapsedMs = TheStopwatch.ElapsedMilliseconds;
                    return true;
                }
                if (TheStopwatch.ElapsedMilliseconds >= InCloseWaitMs)
                {
                    OutElapsedMs = TheStopwatch.ElapsedMilliseconds;
                    return false;
                }
                Thread.Sleep(InPollIntervalMs);
            }
        }

        /// <summary>
        /// メニューが閉じたかを内容も含めて判定する。HWND の破棄・別プロセスによる再利用・非表示化・内容の入れ替わりを閉鎖として扱う。
        /// STA スレッドで呼ぶこと。
        /// </summary>
        /// <param name="InProcessIds">デバッグ中プロセス ID の集合。</param>
        /// <param name="InMenuHandle">監視するメニューの HWND。</param>
        /// <param name="InInitialProcessId">開始時のプロセス ID。</param>
        /// <param name="InFingerprint">開始時の内容スナップショット。</param>
        /// <returns>閉鎖理由。まだ開いていれば null。</returns>
        private static string DescribeMenuClosed(HashSet<uint> InProcessIds, long InMenuHandle, uint InInitialProcessId, UiMenuFingerprint InFingerprint)
        {
            IntPtr TheHandle = new IntPtr(InMenuHandle);
            if (!IsWindow(TheHandle))
            {
                return _REASON_WINDOW_DESTROYED;
            }
            GetWindowThreadProcessId(TheHandle, out uint TheCurrentProcessId);
            if (TheCurrentProcessId != InInitialProcessId)
            {
                return _REASON_HANDLE_REUSED;
            }
            if (!IsWindowVisible(TheHandle))
            {
                return _REASON_HIDDEN;
            }

            WindowInfo TheWindow = DebuggeeWindowEnumerator.EnumerateTopLevelWindows(InProcessIds, true)
                .FirstOrDefault(TheCandidate => TheCandidate.Handle == InMenuHandle);
            if (TheWindow == null)
            {
                return _REASON_WINDOW_DESTROYED;
            }
            if (!UiPopupMenuResolver.Classify(TheWindow, out UiMenuInfo TheMenu, out _))
            {
                return _REASON_CONTENT_CHANGED;
            }
            if (!UiPopupMenuResolver.IsKnownMenuType(TheMenu.MenuType) || !UiPopupMenuResolver.IsSameMenu(InFingerprint, TheMenu))
            {
                return _REASON_CONTENT_CHANGED;
            }
            return null;
        }

        /// <summary>
        /// Extended (Phase 9): ui_menu_close / ui_menu_select_path の待機オプションを読む（既定値だけがツールごとに異なる）。
        /// </summary>
        /// <param name="InArgs">ツール引数。</param>
        /// <param name="InDefaultTimeoutMs">timeoutMs の既定値（ミリ秒）。</param>
        /// <param name="OutTimeoutMs">丸めた上限時間（ミリ秒）。</param>
        /// <param name="OutPollIntervalMs">丸めたポーリング間隔（ミリ秒）。</param>
        private static void ParseMenuWaitOptions(JObject InArgs, int InDefaultTimeoutMs, out int OutTimeoutMs, out int OutPollIntervalMs)
        {
            int TheTimeoutMs = InArgs.Value<int?>("timeoutMs") ?? InDefaultTimeoutMs;
            if (TheTimeoutMs < 0)
            {
                TheTimeoutMs = 0;
            }
            if (TheTimeoutMs > _MAX_WAIT_TIMEOUT_MS)
            {
                TheTimeoutMs = _MAX_WAIT_TIMEOUT_MS;
            }

            int ThePollIntervalMs = InArgs.Value<int?>("pollIntervalMs") ?? _DEFAULT_MENU_POLL_MS;
            if (ThePollIntervalMs < _MIN_POLL_INTERVAL_MS)
            {
                ThePollIntervalMs = _MIN_POLL_INTERVAL_MS;
            }

            OutTimeoutMs = TheTimeoutMs;
            OutPollIntervalMs = ThePollIntervalMs;
        }

        /// <summary>Extended (Phase 9): ui_menu_select_path の path 引数（文字列配列）を読む。空要素・空配列は受け付けない。</summary>
        /// <param name="InArgs">ツール引数。</param>
        /// <param name="OutError">読み取れなかった理由。正常なら null。</param>
        /// <returns>段の一覧。エラー時は null。</returns>
        private static List<string> ParsePathSegments(JObject InArgs, out string OutError)
        {
            OutError = null;
            JToken ThePathToken = InArgs["path"];
            if (ThePathToken == null || ThePathToken.Type == JTokenType.Null)
            {
                OutError = "Parameter 'path' is required (an array of menu item names, e.g. [\"Submenu\", \"Sub Item 2\"])";
                return null;
            }
            if (ThePathToken.Type != JTokenType.Array)
            {
                OutError = "Parameter 'path' must be an array of menu item names, e.g. [\"Submenu\", \"Sub Item 2\"]";
                return null;
            }

            List<string> TheSegments = new List<string>();
            foreach (JToken TheToken in (JArray)ThePathToken)
            {
                string TheSegment = TheToken.Type == JTokenType.String ? TheToken.Value<string>() : null;
                if (string.IsNullOrEmpty(TheSegment))
                {
                    OutError = "Parameter 'path' must contain only non-empty menu item names";
                    return null;
                }
                TheSegments.Add(TheSegment);
            }
            if (TheSegments.Count == 0)
            {
                OutError = "Parameter 'path' must contain at least one menu item name";
                return null;
            }
            if (TheSegments.Count > _MAX_PATH_SEGMENTS)
            {
                OutError = $"Parameter 'path' must not contain more than {_MAX_PATH_SEGMENTS} menu item names";
                return null;
            }
            return TheSegments;
        }

        /// <summary>
        /// Extended (Phase 9): ui_menu_close の対象を確定する（内容による再分類 → 操作できるメニュー種別 → Owner の解決と PID 再照合 →
        /// 閉鎖判定用の fingerprint）。STA スレッドで呼ぶこと。
        /// </summary>
        /// <param name="InResolved">検証済みのメニューウィンドウ。</param>
        /// <returns>閉じにいく対象。分類できなければ Error 付き。</returns>
        private static MenuCloseTarget PrepareMenuClose(ResolvedMenu InResolved)
        {
            if (!UiPopupMenuResolver.Classify(InResolved.WindowInfo, out UiMenuInfo TheMenu, out string TheReason))
            {
                return new MenuCloseTarget { Error = DescribeNotAMenu(InResolved.WindowInfo, TheReason) };
            }
            if (!UiPopupMenuResolver.IsKnownMenuType(TheMenu.MenuType))
            {
                return new MenuCloseTarget
                {
                    Error = $"Window handle {TheMenu.Handle} is an unknown popup menu (menuType={TheMenu.MenuType}); ui_menu_close only acts on menus classified as " +
                        "win32Menu or wpfContextMenu.",
                };
            }

            long TheOwnerHandle = ResolveMenuOwnerHandle(TheMenu, InResolved.ProcessIds, out string TheOwnerError);
            if (TheOwnerError != null)
            {
                return new MenuCloseTarget { Error = TheOwnerError };
            }
            return new MenuCloseTarget
            {
                Menu = TheMenu,
                Fingerprint = UiPopupMenuResolver.BuildFingerprint(TheMenu),
                OwnerHandle = TheOwnerHandle,
            };
        }

        /// <summary>
        /// Extended (Phase 9): メニューの Owner を決める。Win32 のポップアップメニュー（#32768）は Owner を持たないので、
        /// メニューを表示している GUI スレッドのアクティブウィンドウ（GetGUIThreadInfo.hwndActive）をトップレベルへ正規化して使う。
        /// 特定できた Owner がデバッグ対象プロセスのものでなければエラーにする（他アプリを触らないため）。
        /// </summary>
        /// <param name="InMenu">対象メニュー。</param>
        /// <param name="InProcessIds">デバッグ中プロセス ID の集合。</param>
        /// <param name="OutError">Owner が他プロセスだった場合のエラーメッセージ。</param>
        /// <returns>Owner のトップレベル HWND（10 進）。特定できなければ 0。</returns>
        private static long ResolveMenuOwnerHandle(UiMenuInfo InMenu, HashSet<uint> InProcessIds, out string OutError)
        {
            OutError = null;
            long TheOwnerHandle = InMenu.OwnerHandle;
            if (TheOwnerHandle == 0)
            {
                GUITHREADINFO TheThreadInfo = new GUITHREADINFO
                {
                    cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(GUITHREADINFO)),
                };
                uint TheThreadId = GetWindowThreadProcessId(new IntPtr(InMenu.Handle), out uint _);
                if (TheThreadId != 0 && GetGUIThreadInfo(TheThreadId, ref TheThreadInfo) && TheThreadInfo.hwndActive != IntPtr.Zero)
                {
                    IntPtr TheRoot = GetAncestor(TheThreadInfo.hwndActive, GA_ROOT);
                    TheOwnerHandle = (TheRoot == IntPtr.Zero ? TheThreadInfo.hwndActive : TheRoot).ToInt64();
                }
            }
            if (TheOwnerHandle == 0)
            {
                return 0;
            }

            GetWindowThreadProcessId(new IntPtr(TheOwnerHandle), out uint TheOwnerProcessId);
            if (!InProcessIds.Contains(TheOwnerProcessId))
            {
                OutError = $"The owner window {TheOwnerHandle} of menu {InMenu.Handle} does not belong to a currently debugged process; refusing to close the menu.";
                return 0;
            }
            return TheOwnerHandle;
        }

        /// <summary>
        /// Extended (Phase 9): Owner ウィンドウ上の安全な点を 1 つ決めてクリックする（メニューの取り下げ用）。
        /// Owner にも Phase 7 と同じ安全境界（IsWindow → PID → モーダル判定）を通し、前面化はせず、
        /// その点の最前面が Owner であることを確認してからクリックする。
        /// 開いているメニューの列挙が上限で打ち切られた場合は、避けるべき矩形が分からないのでクリックしない。STA スレッドで呼ぶこと。
        /// </summary>
        /// <param name="InProcessIds">デバッグ中プロセス ID の集合。</param>
        /// <param name="InMenu">閉じたいメニュー。</param>
        /// <param name="InOwnerHandle">Owner のトップレベル HWND（10 進）。0 なら実行しない。</param>
        /// <returns>クリック結果。クリックしていない場合は Error 付き。</returns>
        private static MenuOutsideClick PerformOutsideClick(HashSet<uint> InProcessIds, UiMenuInfo InMenu, long InOwnerHandle)
        {
            if (InOwnerHandle == 0)
            {
                return new MenuOutsideClick
                {
                    Error = $"The owner window of menu {InMenu.Handle} could not be determined; refusing to click outside the menu.",
                };
            }

            IntPtr TheOwner = new IntPtr(InOwnerHandle);
            string TheContextError = UiInteractionContext.TryCreate(TheOwner, InProcessIds, out UiInteractionContext TheOwnerContext);
            if (TheContextError != null)
            {
                return new MenuOutsideClick { Error = TheContextError };
            }

            // 現在開いている全ポップアップ（unknown も含む）の矩形を避ける＝メニュー項目を誤って選ばない
            MenuDetection TheDetection = DetectMenus(InProcessIds, null, true);
            if (TheDetection.IsTruncated)
            {
                // 列挙しきれていない＝避けるべき矩形が分からないので、点を決めずに拒否する
                return new MenuOutsideClick { Error = _NOTE_MENUS_TRUNCATED };
            }
            if (!UiPopupMenuResolver.FindOutsideClickPoint(TheOwner, TheDetection.Menus, out int TheX, out int TheY, out string TheSource,
                out string TheClickTarget, out UiOutsideClickDiagnostics TheDiagnostics))
            {
                string TheNoPointError = $"No safe outside-click point was found in owner window {InOwnerHandle}";
                if (TheDiagnostics.CoveringHandles.Count > 0)
                {
                    // どのウィンドウが候補点を覆っていたかを残す（呼び出し側がそれを閉じる・移動する判断に使う）
                    TheNoPointError += $" (candidates were covered by {string.Join(", ", TheDiagnostics.CoveringHandles)})";
                }
                return new MenuOutsideClick { Error = TheNoPointError };
            }

            TheOwnerContext.RefreshGeometry();
            // 開いているメニューは最前面なので Owner を前面化しない（前面化はメニューを不確実に取り下げる副作用がある）
            TheOwnerContext.IsForegroundEnsured = false;
            string ThePointError = TheOwnerContext.PreparePhysicalPoint(TheX, TheY, "menu outside-click point");
            if (ThePointError != null)
            {
                return new MenuOutsideClick { Error = ThePointError };
            }

            bool HasSavedCursor = TheOwnerContext.TrySaveCursor(out POINT TheSavedCursor);
            UiTools.PerformClick(TheX, TheY, false);
            if (HasSavedCursor)
            {
                TheOwnerContext.RestoreCursor(TheSavedCursor);
            }
            return new MenuOutsideClick { X = TheX, Y = TheY, Source = TheSource, ClickTarget = TheClickTarget, Diagnostics = TheDiagnostics };
        }

        /// <summary>
        /// Extended (Phase 9): outside click の候補点の探索結果を 1 つの説明文にする（成功時の note の末尾へ付ける）。
        /// </summary>
        /// <param name="InDiagnostics">点の探索で記録した診断情報。null または候補を試していなければ説明を作らない。</param>
        /// <returns>説明文。載せるものが無ければ null。</returns>
        private static string DescribeOutsideClickCandidates(UiOutsideClickDiagnostics InDiagnostics)
        {
            if (InDiagnostics == null || InDiagnostics.TriedCandidateCount == 0)
            {
                return null;
            }
            string TheDescription = $"tried {InDiagnostics.TriedCandidateCount} title-bar candidate point(s)";
            if (InDiagnostics.CoveringHandles.Count > 0)
            {
                TheDescription += $", covered by {string.Join(", ", InDiagnostics.CoveringHandles)}";
            }
            return TheDescription;
        }

        /// <summary>
        /// Extended (Phase 9): メニューが閉じるのを非同期にポーリングする（ui_menu_wait_closed と同じ内容判定を使う）。
        /// </summary>
        /// <param name="InProcessIds">デバッグ中プロセス ID の集合。</param>
        /// <param name="InMenuHandle">監視するメニューの HWND。</param>
        /// <param name="InProcessId">開始時のプロセス ID。</param>
        /// <param name="InFingerprint">開始時の内容スナップショット。</param>
        /// <param name="InStopwatch">ツール全体の経過時間の計測。</param>
        /// <param name="InDeadlineMs">この時刻（全体経過、ミリ秒）まで待つ。</param>
        /// <param name="InPollIntervalMs">ポーリング間隔（ミリ秒）。</param>
        /// <returns>閉鎖理由。期限までに閉じなければ null。</returns>
        private static async Task<string> PollMenuClosedAsync(HashSet<uint> InProcessIds, long InMenuHandle, uint InProcessId, UiMenuFingerprint InFingerprint,
            Stopwatch InStopwatch, long InDeadlineMs, int InPollIntervalMs)
        {
            while (true)
            {
                string TheReason = await UiTools.RunUiaWithTimeoutAsync(() => DescribeMenuClosed(InProcessIds, InMenuHandle, InProcessId, InFingerprint));
                if (TheReason != null)
                {
                    return TheReason;
                }
                if (InStopwatch.ElapsedMilliseconds >= InDeadlineMs)
                {
                    return null;
                }
                await Task.Delay(InPollIntervalMs);
            }
        }

        /// <summary>Extended (Phase 9): ui_menu_close の戻り値を組み立てる。</summary>
        /// <param name="InIsClosed">閉じたと判定したか。</param>
        /// <param name="InStopwatch">経過時間の計測。</param>
        /// <param name="InHandle">対象の HWND。</param>
        /// <param name="InMethod">閉じるのに使われた方式（閉じなかった場合は none）。</param>
        /// <param name="InReason">閉鎖の判定根拠。閉じなかった場合は null。</param>
        /// <param name="InAttempts">試した方式の一覧。</param>
        /// <param name="InEscapeCount">実際に送った ESC の回数。送っていなければ 0。</param>
        /// <param name="InClickPoint">outside click で押した点。押していなければ null。</param>
        /// <param name="InClickTarget">押した対象の種別（titleBar / rootElement）。押していなければ null。</param>
        /// <param name="InNote">補足。無ければ null。</param>
        /// <returns>成功結果。</returns>
        private static McpToolResult BuildCloseResult(bool InIsClosed, Stopwatch InStopwatch, long InHandle, string InMethod, string InReason,
            List<object> InAttempts, int InEscapeCount, object InClickPoint, string InClickTarget, string InNote)
        {
            // Extended (Phase 10): クローズの方式・試行回数・結果を診断へ残す。外側クリックへ落ちた場合は fallback として warning にする
            bool IsOutsideClick = string.Equals(InMethod, _CLOSE_METHOD_OUTSIDE_CLICK, StringComparison.Ordinal);
            int TheAttemptCount = InAttempts == null ? 0 : InAttempts.Count;
            long TheElapsedMs = InStopwatch.ElapsedMilliseconds;
            DiagnosticHub.EmitCore(IsOutsideClick ? DiagnosticLevel.Warning : DiagnosticLevel.Info, DiagnosticCategory.MENU, "menu.close",
                null, null, TheElapsedMs, InIsClosed ? "closed" : "notClosed", null, InData =>
                {
                    InData["menuHandle"] = InHandle;
                    InData["method"] = InMethod;
                    InData["reason"] = InReason;
                    InData["candidatesTried"] = TheAttemptCount;
                    InData["escapeCount"] = InEscapeCount;
                    InData["clickTarget"] = InClickTarget;
                    InData["note"] = InNote;
                }, null);
            if (IsOutsideClick)
            {
                DiagnosticHub.Emit(DiagnosticLevel.Warning, DiagnosticCategory.MENU, "menu.fallback", InData =>
                {
                    InData["kind"] = _CLOSE_METHOD_OUTSIDE_CLICK;
                    InData["menuHandle"] = InHandle;
                    InData["clickTarget"] = InClickTarget;
                    InData["closed"] = InIsClosed;
                });
                DiagnosticErrorDump.Schedule(DiagnosticScope.Current, null, DiagnosticLevel.Warning, "menu.fallback", InHandle);
            }

            return McpToolResult.Success(new
            {
                closed = InIsClosed,
                handle = InHandle,
                method = InMethod,
                attempts = InAttempts,
                elapsedMs = InStopwatch.ElapsedMilliseconds,
                reason = InReason,
                clickPoint = InClickPoint,
                clickTarget = InClickTarget,
                note = InNote,
            });
        }

        /// <summary>
        /// Extended (Phase 9): ui_menu_select_path の 1 段を実行する（範囲 → IsWindow → PID → 再分類 → 項目解決 →
        /// 中間段はサブメニューの有無 → 実行）。STA スレッドで呼ぶこと。
        /// </summary>
        /// <param name="InProcessIds">デバッグ中プロセス ID の集合。</param>
        /// <param name="InMenuHandle">この段で操作するメニューの HWND。</param>
        /// <param name="InSegment">この段の項目名。</param>
        /// <param name="InSegmentIndex">0 始まりの段番号（エラー文用）。</param>
        /// <param name="InIsLastSegment">最終段か。</param>
        /// <returns>実行結果。失敗時は Error 付き。</returns>
        private static MenuPathStep ExecutePathSegment(HashSet<uint> InProcessIds, long InMenuHandle, string InSegment, int InSegmentIndex, bool InIsLastSegment)
        {
            MenuPathStep TheStep = new MenuPathStep { MenuHandle = InMenuHandle };
            IntPtr TheMenuWindow = new IntPtr(InMenuHandle);
            if (!UiWindowTools.IsHandleInRange(InMenuHandle) || !IsWindow(TheMenuWindow))
            {
                TheStep.Error = DescribeMenuGone(InMenuHandle, InSegmentIndex, InSegment);
                return TheStep;
            }
            GetWindowThreadProcessId(TheMenuWindow, out uint TheProcessId);
            if (!InProcessIds.Contains(TheProcessId))
            {
                // HWND が別プロセスへ再利用された場合も「消えた」として扱う（他プロセスのウィンドウは操作しない）
                TheStep.Error = DescribeMenuGone(InMenuHandle, InSegmentIndex, InSegment);
                return TheStep;
            }

            string TheContextError = UiInteractionContext.TryCreate(TheMenuWindow, InProcessIds, out UiInteractionContext TheContext);
            if (TheContextError != null)
            {
                TheStep.Error = TheContextError;
                return TheStep;
            }

            UiMenuItemSelector TheSelector = new UiMenuItemSelector { Name = InSegment };
            string TheResolveError = ResolveMenuAction(TheContext, TheSelector, "ui_menu_select_path",
                out UiMenuInfo TheMenu, out UiMenuItemInfo TheItem, out AutomationElement TheElement);
            if (TheResolveError != null)
            {
                // 分類できない＝メニューが閉じた／別の内容に入れ替わった（stale）
                TheStep.Error = TheMenu == null ? DescribeMenuGone(InMenuHandle, InSegmentIndex, InSegment) : TheResolveError;
                return TheStep;
            }

            TheStep.MenuHandle = TheMenu.Handle;
            TheStep.ProcessId = TheMenu.ProcessId;
            TheStep.Fingerprint = UiPopupMenuResolver.BuildFingerprint(TheMenu);
            if (!InIsLastSegment)
            {
                if (!TheItem.HasSubmenu)
                {
                    TheStep.Error = $"Menu item '{TheItem.Name}' (segment {InSegmentIndex}) has no submenu in menu {TheMenu.Handle}; " +
                        "only the last segment of 'path' may be an item without a submenu";
                    return TheStep;
                }
                // サブメニューは「実行後に新しく現れたメニュー」だけを採用するため、実行前に開いているメニューの HWND を控える
                TheStep.MenuHandlesBefore = new HashSet<long>(DetectMenus(InProcessIds, null, false).Menus.Select(TheOpenMenu => TheOpenMenu.Handle));
            }

            // 入力の直前に DPI / モニター情報を取り直し、物理クリックではメニュー自身が最前面であることを許容する
            TheContext.RefreshGeometry();
            TheContext.AllowPointRoot(TheMenu.Handle);
            TheContext.IsForegroundEnsured = false;

            MenuExecution TheExecution = ExecuteMenuItem(TheContext, TheMenu, TheItem, TheElement);
            EmitMenuSelect(TheMenu, TheItem, TheExecution); // Extended (Phase 10): 実行方式と成否を診断へ残す
            if (TheExecution.Method == null)
            {
                TheStep.Error = TheExecution.Error;
                return TheStep;
            }
            TheStep.Item = TheItem;
            TheStep.Method = TheExecution.Method;
            TheStep.IsCursorMoved = TheExecution.IsCursorMoved;
            return TheStep;
        }

        /// <summary>Extended (Phase 9): 段の解決前にメニューが消えていた（別内容に入れ替わっていた）ときのエラー文。</summary>
        /// <param name="InMenuHandle">対象メニューの HWND。</param>
        /// <param name="InSegmentIndex">0 始まりの段番号。</param>
        /// <param name="InSegment">その段の項目名。</param>
        /// <returns>エラーメッセージ。</returns>
        private static string DescribeMenuGone(long InMenuHandle, int InSegmentIndex, string InSegment)
        {
            return $"Menu {InMenuHandle} disappeared before segment {InSegmentIndex} ('{InSegment}') could be resolved";
        }

        /// <summary>
        /// Extended (Phase 9): サブメニューが現れるのを非同期にポーリングする（1 回の STA 呼び出しを短く保つため、
        /// <see cref="WaitNewMenus"/> ではなくツール側で待つ）。
        /// </summary>
        /// <param name="InProcessIds">デバッグ中プロセス ID の集合。</param>
        /// <param name="InMenuHandlesBefore">実行前に開いていたメニューの HWND 集合。</param>
        /// <param name="InParentMenuHandle">展開元のメニューの HWND（Owner 一致でサブメニューを見つけるため）。</param>
        /// <param name="InTimeoutMs">出現を待つ上限時間（ミリ秒）。</param>
        /// <param name="InPollIntervalMs">再検出の間隔（ミリ秒）。</param>
        /// <returns>サブメニュー候補と、その見つけ方。上限時間内に現れなければ候補は空。</returns>
        private static async Task<SubmenuDetection> WaitNewMenusAsync(HashSet<uint> InProcessIds, HashSet<long> InMenuHandlesBefore,
            long InParentMenuHandle, int InTimeoutMs, int InPollIntervalMs)
        {
            Stopwatch TheStopwatch = Stopwatch.StartNew();
            while (true)
            {
                await Task.Delay(InPollIntervalMs);
                SubmenuDetection TheCandidates = await UiTools.RunUiaWithTimeoutAsync(() =>
                    FindSubmenuCandidates(InProcessIds, InMenuHandlesBefore, InParentMenuHandle));
                if (TheCandidates.Menus.Count > 0 || TheStopwatch.ElapsedMilliseconds >= InTimeoutMs)
                {
                    EmitMenuSubmenu(InParentMenuHandle, TheCandidates); // Extended (Phase 10)
                    return TheCandidates;
                }
            }
        }

        /// <summary>Extended (Phase 9): 新しく現れたメニューから、次の段として進むものを選ぶ（親を Owner に持つものを優先する）。</summary>
        /// <param name="InNewMenus">新しく現れたメニュー（Z 順、前面が先）。</param>
        /// <param name="InParentMenuHandle">展開元のメニューの HWND。</param>
        /// <returns>次の段のメニューの HWND。</returns>
        private static long PickSubmenu(List<UiMenuInfo> InNewMenus, long InParentMenuHandle)
        {
            foreach (UiMenuInfo TheMenu in InNewMenus)
            {
                if (TheMenu.OwnerHandle == InParentMenuHandle)
                {
                    return TheMenu.Handle;
                }
            }
            return InNewMenus[0].Handle;
        }

        /// <summary>
        /// Extended (Phase 9): 経路上のすべてのメニューが閉じるのを非同期にポーリングする（最終段の実行後の確認）。
        /// </summary>
        /// <param name="InProcessIds">デバッグ中プロセス ID の集合。</param>
        /// <param name="InWatches">監視対象のメニュー一覧。</param>
        /// <param name="InTimeoutMs">確認に使う上限時間（ミリ秒）。</param>
        /// <param name="InPollIntervalMs">確認の間隔（ミリ秒）。</param>
        /// <returns>すべて閉じたと判定したら true。</returns>
        private static async Task<bool> WaitMenusClosedAsync(HashSet<uint> InProcessIds, List<MenuCloseWatch> InWatches, int InTimeoutMs, int InPollIntervalMs)
        {
            Stopwatch TheStopwatch = Stopwatch.StartNew();
            while (true)
            {
                bool IsEveryMenuClosed = await UiTools.RunUiaWithTimeoutAsync(() =>
                {
                    foreach (MenuCloseWatch TheWatch in InWatches)
                    {
                        if (DescribeMenuClosed(InProcessIds, TheWatch.Handle, TheWatch.ProcessId, TheWatch.Fingerprint) == null)
                        {
                            return false;
                        }
                    }
                    return true;
                });
                if (IsEveryMenuClosed)
                {
                    return true;
                }
                if (TheStopwatch.ElapsedMilliseconds >= InTimeoutMs)
                {
                    return false;
                }
                await Task.Delay(InPollIntervalMs);
            }
        }

        /// <summary>ui_menu_wait_closed の戻り値を組み立てる。</summary>
        /// <param name="InIsClosed">閉じたと判定したか。</param>
        /// <param name="InStopwatch">経過時間の計測。</param>
        /// <param name="InHandle">監視した HWND。</param>
        /// <param name="InReason">判定根拠。タイムアウト時は null。</param>
        /// <param name="InFingerprint">開始時の内容スナップショット。取得前は null。</param>
        /// <returns>成功結果。</returns>
        private static McpToolResult BuildClosedResult(bool InIsClosed, Stopwatch InStopwatch, long InHandle, string InReason, UiMenuFingerprint InFingerprint)
        {
            return McpToolResult.Success(new
            {
                closed = InIsClosed,
                elapsedMs = InStopwatch.ElapsedMilliseconds,
                handle = InHandle,
                reason = InReason,
                fingerprint = InFingerprint,
            });
        }
    }
}
