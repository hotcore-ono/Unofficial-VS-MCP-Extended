using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
                "submenu is expanded its items also appear underneath the parent MenuItem in the parent popup's subtree, and those duplicates are excluded. ";

            InRegistry.Register(
                new McpToolDefinition(
                    "ui_menu_detect",
                    "[Windows UIA — desktop app being debugged] List the popup menus (context menus) currently open in the debugged processes. Every visible top-level " +
                    "window of the debugged processes is inspected by content on each call — the before/after HWND difference of ui_window_right_click is NOT used, " +
                    "because Windows reuses the HWND of a context menu that was just closed. " + TheClassificationDescription +
                    "Returns text { count, includeUnknown, inspectedCount, truncated, menus[] } where each menu has menuType, handle, handleHex, processId, " +
                    "ownerHandle, className, bounds, isVisible, isEnabled, isForeground, uiaRootName, uiaRootControlType, win32MenuHandle (HMENU of a '#32768' menu, " +
                    "0 when unknown) and items[] (id, name, automationId, controlType, isEnabled, isOffscreen, isChecked, hasSubmenu, bounds, rootWindowHandle, " +
                    "patterns, index). 'inspectedCount' is the number of windows that passed the cheap pre-filter (menu class, owned, or titleless) and were actually " +
                    "classified, and 'truncated' is true when more candidates were open than the inspection limit (20) so some were not classified. " +
                    "Values that cannot be observed are null and are never guessed. Use menus[i].handle with ui_menu_get_info / capture / select / wait_closed.",
                    SchemaBuilder.Create()
                        .AddInteger("ownerWindowHandle", "Only return menus owned by this HWND (decimal), or ownerless popups created by the same GUI thread")
                        .AddBoolean("includeUnknown", "Also return popups whose menu structure could not be identified (menuType 'unknownPopupMenu'). Default: false")
                        .Build()),
                InArgs => UiMenuDetectAsync(InAccessor, InArgs));

            InRegistry.Register(
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

            InRegistry.Register(
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
                    "the menus are re-detected every 100 ms for up to 1500 ms and only a menu that was NOT open before the click is returned as 'submenu' (all of them " +
                    "in 'submenuCandidates'); if none appears, submenuOpened=false with a 'note' instead of an error. Returns text { message, method, menuHandle, item, " +
                    "menuClosed, closeWaitMs (time actually spent waiting), submenuOpened, submenu, submenuCandidates, note }.",
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

            InRegistry.Register(
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

            InRegistry.Register(
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

            InRegistry.Register(
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
            return TheDetection;
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

            if (!UiPopupMenuResolver.Classify(TheContext.WindowInfo, out UiMenuInfo TheMenu, out string TheReason, out List<AutomationElement> TheItemElements))
            {
                return McpToolResult.Error(DescribeNotAMenu(TheContext.WindowInfo, TheReason));
            }
            if (!UiPopupMenuResolver.IsKnownMenuType(TheMenu.MenuType))
            {
                return McpToolResult.Error(
                    $"Window handle {TheMenu.Handle} is an unknown popup menu (menuType={TheMenu.MenuType}); ui_menu_select only acts on menus classified as " +
                    "win32Menu or wpfContextMenu.");
            }

            string TheSelectError = UiPopupMenuResolver.TrySelectItem(TheMenu, InSelector, out UiMenuItemInfo TheItem);
            if (TheSelectError != null)
            {
                return McpToolResult.Error(TheSelectError);
            }
            if (TheItem.RootWindowHandle == 0)
            {
                // Phase 7 の要素解決と同じく、所属するトップレベルウィンドウが分からない項目は操作しない
                return McpToolResult.Error(
                    $"Menu item '{TheItem.Name}' (index {TheItem.Index}) in menu {TheMenu.Handle} has no resolvable top-level window " +
                    "(no ancestor exposes a native window handle); refusing to act on it");
            }
            if (TheItem.RootWindowHandle != TheMenu.Handle)
            {
                return McpToolResult.Error(
                    $"Menu item '{TheItem.Name}' (index {TheItem.Index}) belongs to window {TheItem.RootWindowHandle}, not to the requested menu {TheMenu.Handle}; " +
                    "refusing to act on it");
            }
            if (!TheItem.IsEnabled)
            {
                return McpToolResult.Error($"Menu item '{TheItem.Name}' (index {TheItem.Index}) in menu {TheMenu.Handle} is disabled (IsEnabled=false); refusing to act on it");
            }
            if (TheItem.Index >= TheItemElements.Count)
            {
                // 項目が Win32 のメニュー API だけから組み立てられた場合（UIA が項目を返さない）は、押せる UIA 要素が無い
                string TheMissingElementReason = string.IsNullOrEmpty(TheMenu.Note)
                    ? "the element disappeared while it was being resolved"
                    : TheMenu.Note;
                return McpToolResult.Error(
                    $"Menu item '{TheItem.Name}' (index {TheItem.Index}) in menu {TheMenu.Handle} has no UI Automation element to invoke ({TheMissingElementReason}); " +
                    "call ui_menu_get_info to re-inspect the menu and retry.");
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

            string TheMethod = ExecuteMenuItem(TheContext, TheMenu, TheItem, TheItemElements[TheItem.Index], out string TheExecuteError);
            if (TheMethod == null)
            {
                return McpToolResult.Error(TheExecuteError);
            }

            UiMenuInfo TheSubmenu = null;
            List<long> TheSubmenuCandidates = null;
            string TheNote = null;
            if (TheItem.HasSubmenu)
            {
                List<UiMenuInfo> TheNewMenus = WaitNewMenus(InResolved.ProcessIds, TheMenuHandlesBeforeExecute);
                if (TheNewMenus.Count > 0)
                {
                    // 同時に複数現れた場合は先頭（Z 順で前面）を採用し、判断材料として全 HWND も返す
                    TheSubmenu = TheNewMenus[0];
                    TheSubmenuCandidates = TheNewMenus.ConvertAll(TheNewMenu => TheNewMenu.Handle);
                }
                else
                {
                    // 開かなかったこと自体は失敗にしない（項目の実行は成功している）
                    TheNote = $"submenu did not appear within {_SUBMENU_WAIT_TIMEOUT_MS} ms";
                }
            }

            bool IsMenuClosed = WaitMenuClosed(InResolved.ProcessIds, TheMenu.Handle, TheMenu.ProcessId, TheFingerprint, InCloseWaitMs, out long TheCloseWaitElapsedMs);

            return McpToolResult.Success(new
            {
                message = $"Selected menu item '{TheItem.Name}' (index {TheItem.Index}) in menu {TheMenu.Handle} using {TheMethod}",
                method = TheMethod,
                menuHandle = TheMenu.Handle,
                item = TheItem,
                menuClosed = IsMenuClosed,
                closeWaitMs = TheCloseWaitElapsedMs,
                submenuOpened = TheSubmenu != null,
                submenu = TheSubmenu,
                submenuCandidates = TheSubmenuCandidates,
                note = TheNote,
            });
        }

        /// <summary>
        /// メニュー項目を実行する。サブメニュー付きの項目は ExpandCollapsePattern を先に試し、続いて InvokePattern、
        /// LegacyIAccessible の DoDefaultAction、最後に座標ゲートを通した物理クリックの順に試す。
        /// </summary>
        /// <param name="InContext">対象メニューの Interaction Context。</param>
        /// <param name="InMenu">対象メニュー。</param>
        /// <param name="InItem">対象項目。</param>
        /// <param name="InElement">対象項目の UIA 要素。</param>
        /// <param name="OutError">実行できなかった場合のエラーメッセージ。</param>
        /// <returns>実行方式の名前。実行できなければ null。</returns>
        private static string ExecuteMenuItem(UiInteractionContext InContext, UiMenuInfo InMenu, UiMenuItemInfo InItem, AutomationElement InElement, out string OutError)
        {
            OutError = null;
            if (InItem.HasSubmenu && UiPopupMenuResolver.TryExpand(InElement))
            {
                return _METHOD_EXPAND_COLLAPSE;
            }
            if (UiPopupMenuResolver.TryInvoke(InElement))
            {
                return _METHOD_INVOKE;
            }
            if (UiPopupMenuResolver.TryLegacyDoDefaultAction(InElement))
            {
                return _METHOD_LEGACY;
            }

            string ThePrerequisiteError = UiPopupMenuResolver.DescribePhysicalPrerequisite(InMenu, InItem);
            if (ThePrerequisiteError != null)
            {
                OutError = "The menu item does not support UI Automation invocation and a physical click is not possible: " + ThePrerequisiteError;
                return null;
            }
            UiPopupMenuResolver.TryGetBoundsCenter(InItem.Bounds, out int TheClickX, out int TheClickY);

            string ThePointError = InContext.PreparePhysicalPoint(TheClickX, TheClickY, "menu item click point");
            if (ThePointError != null)
            {
                OutError = ThePointError;
                return null;
            }

            bool HasSavedCursor = InContext.TrySaveCursor(out POINT TheSavedCursor);
            UiTools.PerformClick(TheClickX, TheClickY, false);
            if (HasSavedCursor)
            {
                InContext.RestoreCursor(TheSavedCursor);
            }
            return _METHOD_PHYSICAL_CLICK;
        }

        /// <summary>
        /// Extended: 実行後にサブメニューが現れるのを待つ。WPF のサブメニューは ExpandCollapsePattern.Expand の直後に、
        /// 親ポップアップを Owner とする新しいトップレベル HWND として現れるが、出現までの時間は一定ではない（Phase 8 実測）。
        /// 固定待ちだと取りこぼすため、一定間隔で再検出し、1 件でも現れた時点で打ち切る。STA スレッドで呼ぶこと。
        /// </summary>
        /// <param name="InProcessIds">デバッグ中プロセス ID の集合。</param>
        /// <param name="InMenuHandlesBefore">実行前に開いていたメニューの HWND 集合。</param>
        /// <returns>新しく現れたメニューの一覧（Z 順、前面が先）。上限時間内に現れなければ空。</returns>
        private static List<UiMenuInfo> WaitNewMenus(HashSet<uint> InProcessIds, HashSet<long> InMenuHandlesBefore)
        {
            Stopwatch TheStopwatch = Stopwatch.StartNew();
            while (true)
            {
                Thread.Sleep(_SUBMENU_POLL_MS);
                List<UiMenuInfo> TheNewMenus = FindNewMenus(InProcessIds, InMenuHandlesBefore);
                if (TheNewMenus.Count > 0)
                {
                    return TheNewMenus;
                }
                if (TheStopwatch.ElapsedMilliseconds >= _SUBMENU_WAIT_TIMEOUT_MS)
                {
                    return TheNewMenus;
                }
            }
        }

        /// <summary>
        /// 実行前には開いていなかったメニューだけを列挙する（サブメニューの検出用）。既に開いていた別のメニューを
        /// サブメニューと誤認しないため、「元メニュー以外」ではなく「新しく現れたもの」で判定する。STA スレッドで呼ぶこと。
        /// </summary>
        /// <param name="InProcessIds">デバッグ中プロセス ID の集合。</param>
        /// <param name="InMenuHandlesBefore">実行前に開いていたメニューの HWND 集合。</param>
        /// <returns>新しく現れたメニューの一覧（Z 順、前面が先）。無ければ空。</returns>
        private static List<UiMenuInfo> FindNewMenus(HashSet<uint> InProcessIds, HashSet<long> InMenuHandlesBefore)
        {
            MenuDetection TheDetection = DetectMenus(InProcessIds, null, false);
            return TheDetection.Menus.FindAll(TheMenu => !InMenuHandlesBefore.Contains(TheMenu.Handle));
        }

        /// <summary>
        /// メニューが閉じるのをポーリングで確認する（ui_menu_select の実行後確認）。STA スレッドで呼ぶこと。
        /// </summary>
        /// <param name="InProcessIds">デバッグ中プロセス ID の集合。</param>
        /// <param name="InMenuHandle">監視するメニューの HWND。</param>
        /// <param name="InInitialProcessId">開始時のプロセス ID。</param>
        /// <param name="InFingerprint">開始時の内容スナップショット。</param>
        /// <param name="InCloseWaitMs">確認に使う上限時間（ミリ秒）。</param>
        /// <param name="OutElapsedMs">確認に実際に要した時間（ミリ秒）。</param>
        /// <returns>閉じたと判定したら true。</returns>
        private static bool WaitMenuClosed(HashSet<uint> InProcessIds, long InMenuHandle, uint InInitialProcessId, UiMenuFingerprint InFingerprint,
            int InCloseWaitMs, out long OutElapsedMs)
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
                Thread.Sleep(_CLOSE_POLL_MS);
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
