using System;
using System.Collections.Generic;
using System.Linq;
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
    /// Extended 独自の「任意のデバッグ対象トップレベル HWND に対する modal-safe な UI 操作」ツール群
    /// （ui_window_click / double_click / right_click / drag / mouse_wheel）。
    /// 実行順は必ず: 要素を指定ウィンドウ配下で一意に解決 → 要素のトップレベル HWND が指定ウィンドウと一致することを確認 →
    /// ウィンドウ状態を再列挙してモーダル判定（無効化された owner は拒否）→ はじめて Invoke / 入力注入。
    /// 入力注入・座標検証・ScrollPattern は upstream UiTools の internal ヘルパーをそのまま使い、同等ロジックを複製しない。
    /// Extended (Phase 8): 共通の前段（ウィンドウ状態の再評価・要素解決・座標ゲート・カーソル復元・Geometry の取り直し）は
    /// <see cref="UiInteractionContext"/> へ移し、ui_menu_* と共用する。戻り値の JSON キー・method 名・エラー文は Phase 7 と同じ。
    /// upstream の public Tool schema は変更しない。
    /// </summary>
    public static class UiWindowActionTools
    {
        /// <summary>waitMs の上限（upstream ui_click と同じ）。</summary>
        private const int _MAX_WAIT_MS = 10000;

        /// <summary>right_click 後にポップアップ／コンテキストメニューの出現を観測する既定の待ち時間。</summary>
        private const int _DEFAULT_OBSERVE_MS = 300;

        /// <summary>right_click の観測待ち時間の上限。</summary>
        private const int _MAX_OBSERVE_MS = 5000;

        /// <summary>PostMessage の WM_MOUSEWHEEL がスクロールへ反映されるのを待つ時間（効果の検証用）。</summary>
        private const int _POST_MESSAGE_SETTLE_MS = 200;

        /// <summary>Extended: right_click 後にポップアップかどうかを内容で調べるウィンドウ数の上限。</summary>
        private const int _MAX_POPUP_CANDIDATES = 10;

        /// <summary>Extended: 座標指定の ScrollPattern 提供元探索でたどる要素数の上限。</summary>
        private const int _MAX_SCROLL_SEARCH_ELEMENTS = 2000;

        /// <summary>ツールをレジストリへ登録する。VsMcpPackage.RegisterTools から呼ばれる。</summary>
        /// <param name="InRegistry">登録先レジストリ。</param>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        public static void Register(McpToolRegistry InRegistry, VsServiceAccessor InAccessor)
        {
            const string TheSelectorDescription =
                "The element is selected by 'automationId' (preferred) optionally narrowed by 'name', 'controlType', 'className'; when several elements still match, " +
                "the call fails with the candidate list unless 'index' picks one. ";
            const string TheSafetyDescription =
                "Safety boundary (always enforced): the element must be a descendant of the given window (its top-level HWND is verified, also for elements with " +
                "NativeWindowHandle=0), the window must belong to a debugged process, and the window must not be disabled by a modal dialog — an owner window blocked " +
                "by an owned or ownerless modal dialog is refused with the blocking window in the error, even though UIA InvokePattern could technically fire. ";

            // Extended (Phase 10): 登録は DiagnosticToolRunner を通し、tool.start / tool.end と相関 ID を付ける（schema・戻り値・エラー文は不変）
            DiagnosticToolRunner.Register(InRegistry,
                new McpToolDefinition(
                    "ui_window_click",
                    "[Windows UIA — desktop app being debugged] Click a UI element inside ANY top-level window of the debugged application identified by its HWND " +
                    "(modal dialog, owned window, main window). " + TheSelectorDescription +
                    "Execution order is the same as ui_click: UIA InvokePattern first (no cursor movement), " +
                    "then SelectionItemPattern.Select for selectable items such as TabItem / ListItem (no cursor movement; reports method 'selectionItemPattern' " +
                    "with 'alreadySelected' and 'verified' after reading the selection state back), then a physical click at the element center, which is " +
                    "performed only if the point lies inside the target window's rectangle and is not covered by another window. " + TheSafetyDescription +
                    "For MessageBox / TaskDialog / file dialog buttons prefer standard_dialog_execute / standard_file_dialog_*. " +
                    "For a popup / context menu item prefer ui_menu_select; for keyboard input prefer ui_window_send_keys.",
                    SchemaBuilder.Create()
                        .AddInteger("windowHandle", "HWND of the window that contains the element (decimal)", required: true)
                        .AddString("automationId", "AutomationId of the element (exact)")
                        .AddString("name", "Name of the element (exact)")
                        .AddString("controlType", "ControlType programmatic name (e.g. 'Button' or 'ControlType.Button')")
                        .AddString("className", "ClassName of the element (exact)")
                        .AddInteger("index", "0-based index (tree order) to pick one element when several match; only used when given")
                        .AddInteger("waitMs", "Milliseconds to wait after clicking (default: 0, max: 10000)")
                        .AddBoolean("restoreCursor", "Restore cursor to its previous position after a physical click (default: true)")
                        .AddBoolean("blockInput", "Block user input during a physical click. Requires admin (default: false)")
                        .Build()),
                InArgs => UiWindowClickAsync(InAccessor, InArgs));

            DiagnosticToolRunner.Register(InRegistry,
                new McpToolDefinition(
                    "ui_window_double_click",
                    "[Windows UIA — desktop app being debugged] Double-click a UI element inside ANY top-level window of the debugged application identified by its HWND. " +
                    TheSelectorDescription + "Always uses the physical double-click of ui_double_click (two left clicks without delay) at the element center; the element must " +
                    "be enabled, on screen and the point must lie inside the target window and not be covered by another window. " + TheSafetyDescription,
                    SchemaBuilder.Create()
                        .AddInteger("windowHandle", "HWND of the window that contains the element (decimal)", required: true)
                        .AddString("automationId", "AutomationId of the element (exact)")
                        .AddString("name", "Name of the element (exact)")
                        .AddString("controlType", "ControlType programmatic name (e.g. 'Text' or 'ControlType.Text')")
                        .AddString("className", "ClassName of the element (exact)")
                        .AddInteger("index", "0-based index (tree order) to pick one element when several match; only used when given")
                        .AddInteger("waitMs", "Milliseconds to wait after double-clicking (default: 0, max: 10000)")
                        .AddBoolean("restoreCursor", "Restore cursor to its previous position after the click (default: true)")
                        .AddBoolean("blockInput", "Block user input during the click. Requires admin (default: false)")
                        .Build()),
                InArgs => UiWindowDoubleClickAsync(InAccessor, InArgs));

            DiagnosticToolRunner.Register(InRegistry,
                new McpToolDefinition(
                    "ui_window_right_click",
                    "[Windows UIA — desktop app being debugged] Right-click a UI element inside ANY top-level window of the debugged application identified by its HWND, " +
                    "to open a context menu. " + TheSelectorDescription + "Uses the physical right click of ui_right_click at the element center (same point checks as " +
                    "ui_window_double_click). After the click it waits 'observeMs' and reports the top-level windows of the debugged process that appeared " +
                    "('newWindows': popup / context-menu HWNDs, usable with ui_list_windows / ui_window_get_tree). Because Windows reuses the HWND of a context " +
                    "menu that was just closed, 'newWindows' can be empty even though the menu is open; therefore 'popupWindows' additionally reports the visible " +
                    "top-level windows (other than the target window, at most 10 inspected) that are menus by CONTENT — window class '#32768' or a UIA subtree " +
                    "containing Menu / MenuItem elements — each with 'isNew' (whether it is also in 'newWindows') and 'menuItemCount' (number of MenuItem elements " +
                    "in its subtree, null for a '#32768' window). 'contextMenuObserved' is true when 'popupWindows' is not empty. 'menuHandles' additionally lists the " +
                    "HWNDs of those popups that ui_menu_detect classifies as a real menu (win32Menu / wpfContextMenu) — pass one of them to ui_menu_get_info / " +
                    "ui_menu_select. Pass popupWindows[i].handle as 'windowHandle' to ui_window_click to invoke a MenuItem of that menu by 'automationId' / 'name'. " +
                    TheSafetyDescription,
                    SchemaBuilder.Create()
                        .AddInteger("windowHandle", "HWND of the window that contains the element (decimal)", required: true)
                        .AddString("automationId", "AutomationId of the element (exact)")
                        .AddString("name", "Name of the element (exact)")
                        .AddString("controlType", "ControlType programmatic name (e.g. 'Text' or 'ControlType.Text')")
                        .AddString("className", "ClassName of the element (exact)")
                        .AddInteger("index", "0-based index (tree order) to pick one element when several match; only used when given")
                        .AddInteger("observeMs", "Milliseconds to wait before enumerating new popup windows (default: 300, max: 5000)")
                        .AddInteger("waitMs", "Additional milliseconds to wait after the observation (default: 0, max: 10000)")
                        .AddBoolean("restoreCursor", "Restore cursor to its previous position after the click (default: true)")
                        .AddBoolean("blockInput", "Block user input during the click. Requires admin (default: false)")
                        .Build()),
                InArgs => UiWindowRightClickAsync(InAccessor, InArgs));

            DiagnosticToolRunner.Register(InRegistry,
                new McpToolDefinition(
                    "ui_window_drag",
                    "[Windows UIA — desktop app being debugged] Drag from one UI element to another inside ONE top-level window of the debugged application identified by " +
                    "its HWND. Source and target are each selected like ui_window_click but with the prefixes 'source' / 'target' (sourceAutomationId, sourceName, " +
                    "sourceControlType, sourceClassName, sourceIndex / targetAutomationId, ...). Both must be enabled, on screen, inside the same requested window " +
                    "(cross-window drag is refused) and not covered by another window. Uses the physical drag of ui_drag (press at the source center, move in 'steps', " +
                    "release at the target center) in physical pixels. " + TheSafetyDescription,
                    SchemaBuilder.Create()
                        .AddInteger("windowHandle", "HWND of the window that contains both elements (decimal)", required: true)
                        .AddString("sourceAutomationId", "AutomationId of the drag source (exact)")
                        .AddString("sourceName", "Name of the drag source (exact)")
                        .AddString("sourceControlType", "ControlType of the drag source")
                        .AddString("sourceClassName", "ClassName of the drag source (exact)")
                        .AddInteger("sourceIndex", "0-based index to pick one source when several match")
                        .AddString("targetAutomationId", "AutomationId of the drop target (exact)")
                        .AddString("targetName", "Name of the drop target (exact)")
                        .AddString("targetControlType", "ControlType of the drop target")
                        .AddString("targetClassName", "ClassName of the drop target (exact)")
                        .AddInteger("targetIndex", "0-based index to pick one target when several match")
                        .AddInteger("steps", "Number of intermediate move steps (default: 10, 1-100)")
                        .AddInteger("delayMs", "Milliseconds between steps (default: 10, 1-1000); ignored when durationMs is given")
                        .AddInteger("durationMs", "Total move duration in milliseconds; sets delayMs = durationMs / steps")
                        .AddBoolean("restoreCursor", "Restore cursor to its previous position after the drag (default: true)")
                        .AddBoolean("blockInput", "Block user input during the drag. Requires admin (default: false)")
                        .Build()),
                InArgs => UiWindowDragAsync(InAccessor, InArgs));

            DiagnosticToolRunner.Register(InRegistry,
                new McpToolDefinition(
                    "ui_window_mouse_wheel",
                    "[Windows UIA — desktop app being debugged] Scroll the mouse wheel over a UI element (selected like ui_window_click) or over screen coordinates 'x'/'y' " +
                    "inside ANY top-level window of the debugged application identified by its HWND. 'clicks' is the number of wheel notches (WHEEL_DELTA=120 each): " +
                    "positive scrolls up/left, negative scrolls down/right. Same method order as ui_mouse_wheel: ScrollPattern when an element is given and 'usePattern' " +
                    "is true (no cursor movement), then WM_MOUSEWHEEL posted to the window under the point (only if that window belongs to the target window), then " +
                    "physical wheel events after bringing the target window to the front. A posted wheel is only reported as method 'PostMessageWheel' when the " +
                    "scroll position of the ScrollPattern at that point actually changed ('verified': true with 'scrollPercentBefore' / 'scrollPercentAfter'), or " +
                    "when no ScrollPattern is available to verify it ('verified': false with a 'note'); if it can be verified but nothing moved, the physical wheel " +
                    "is used instead. The point must lie inside the target window. " + TheSafetyDescription,
                    SchemaBuilder.Create()
                        .AddInteger("windowHandle", "HWND of the window to scroll in (decimal)", required: true)
                        .AddInteger("clicks", "Number of wheel notches. Positive = up/left, negative = down/right (non-zero)", required: true)
                        .AddString("automationId", "AutomationId of the element to scroll over (exact)")
                        .AddString("name", "Name of the element to scroll over (exact)")
                        .AddString("controlType", "ControlType of the element to scroll over")
                        .AddString("className", "ClassName of the element to scroll over (exact)")
                        .AddInteger("index", "0-based index to pick one element when several match")
                        .AddInteger("x", "Screen X coordinate to scroll at (physical pixels; used when no element selector is given)")
                        .AddInteger("y", "Screen Y coordinate to scroll at (physical pixels; used when no element selector is given)")
                        .AddBoolean("horizontal", "Send a horizontal wheel event instead of vertical (default: false)")
                        .AddBoolean("usePattern", "Try ScrollPattern first when an element is given (default: true)")
                        .AddBoolean("restoreCursor", "Restore cursor to its previous position after physical wheel events (default: true)")
                        .AddBoolean("blockInput", "Block user input during physical wheel events. Requires admin (default: false)")
                        .AddInteger("waitMs", "Milliseconds to wait after scrolling (default: 0, max: 10000)")
                        .Build()),
                InArgs => UiWindowMouseWheelAsync(InAccessor, InArgs));
        }

        /// <summary>
        /// Action 共通の前段: <see cref="UiInteractionContext"/> の生成（ウィンドウ状態の再列挙とモーダル判定、Geometry の取得）→
        /// 要素の一意解決と所属 HWND 検証 → 要素の有効性。物理入力を伴う場合は IsOffscreen と bounds の有無も確認する。
        /// Phase 7 と同じ順序・同じ文言（実装だけを Context へ移した）。STA スレッドで呼ぶこと。
        /// </summary>
        /// <param name="InWindow">正規化済みのトップレベル HWND。</param>
        /// <param name="InProcessIds">デバッグ中プロセス ID の集合。</param>
        /// <param name="InSelector">要素セレクター。</param>
        /// <param name="InIsPhysical">物理入力（座標）を伴う操作か。</param>
        /// <param name="InRole">エラーメッセージの接頭辞（"Drag source" 等）。空なら付けない。</param>
        /// <param name="OutContext">生成した Interaction Context。エラー時は null のことがある。</param>
        /// <param name="OutElement">解決した要素。</param>
        /// <param name="OutFailure">要素を解決できなかった理由。要素解決まで到達しなかった場合と正常時は None。</param>
        /// <returns>エラーメッセージ。正常なら null。</returns>
        private static string PrepareElementAction(IntPtr InWindow, HashSet<uint> InProcessIds, UiWindowElementSelector InSelector, bool InIsPhysical, string InRole,
            out UiInteractionContext OutContext, out UiWindowResolvedElement OutElement, out UiWindowResolveFailure OutFailure)
        {
            OutElement = null;
            OutFailure = UiWindowResolveFailure.None;
            string TheTargetError = UiInteractionContext.TryCreate(InWindow, InProcessIds, out OutContext);
            if (TheTargetError != null)
            {
                return TheTargetError;
            }
            return OutContext.ResolveElement(InSelector, InIsPhysical, InRole, out OutElement, out OutFailure);
        }

        /// <summary>戻り値用のウィンドウ要約。</summary>
        /// <param name="InWindowInfo">WindowInfo。</param>
        /// <returns>handle / title。</returns>
        private static object DescribeWindow(WindowInfo InWindowInfo)
        {
            return new { handle = InWindowInfo.Handle, title = InWindowInfo.Title };
        }

        /// <summary>waitMs を上限で丸めて待つ。</summary>
        /// <param name="InArgs">ツール引数。</param>
        /// <returns>待機タスク。</returns>
        private static Task DelayAfterActionAsync(JObject InArgs)
        {
            int TheWaitMs = InArgs.Value<int?>("waitMs") ?? 0;
            return TheWaitMs > 0 ? Task.Delay(Math.Min(TheWaitMs, _MAX_WAIT_MS)) : Task.CompletedTask;
        }

        /// <summary>
        /// ui_window_click の本体。InvokePattern → SelectionItemPattern → 物理クリック（座標ゲート付き）の順。
        /// Extended: Invoke を持たない選択項目（TabItem / ListItem など）は物理クリックの前に SelectionItemPattern で選ぶ
        /// （物理クリックはヒットテストの都合で「成功したのに選択が変わらない」ことがあるため）。
        /// </summary>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        /// <param name="InArgs">ツール引数。</param>
        /// <returns>text（message / method / window / element / clickX / clickY、SelectionItemPattern のときは alreadySelected / verified）、またはエラー。</returns>
        private static async Task<McpToolResult> UiWindowClickAsync(VsServiceAccessor InAccessor, JObject InArgs)
        {
            (IntPtr TheWindow, HashSet<uint> TheProcessIds, McpToolResult TheError) = await UiWindowUiaTools.ResolveWindowWithProcessesAsync(InAccessor, InArgs);
            if (TheError != null)
            {
                return TheError;
            }
            string TheSelectorError = UiWindowElementResolver.TryParseSelector(InArgs, string.Empty, out UiWindowElementSelector TheSelector);
            if (TheSelectorError != null)
            {
                return McpToolResult.Error(TheSelectorError);
            }
            bool IsCursorRestored = InArgs.Value<bool?>("restoreCursor") ?? true;
            bool IsInputBlocked = InArgs.Value<bool?>("blockInput") ?? false;

            McpToolResult TheResult;
            try
            {
                TheResult = await UiTools.RunUiaWithTimeoutAsync(() =>
                {
                    string TheActionError = PrepareElementAction(TheWindow, TheProcessIds, TheSelector, false, null,
                        out UiInteractionContext TheContext, out UiWindowResolvedElement TheElement, out _);
                    if (TheActionError != null)
                    {
                        return McpToolResult.Error(TheActionError);
                    }
                    WindowInfo TheWindowInfo = TheContext.WindowInfo;

                    if (TheElement.Element.TryGetCurrentPattern(InvokePattern.Pattern, out object TheInvoke))
                    {
                        ((InvokePattern)TheInvoke).Invoke();
                        return McpToolResult.Success(new
                        {
                            message = $"Clicked element with {TheSelector.Describe()} in window {TheWindowInfo.Handle} using InvokePattern",
                            method = "invokePattern",
                            window = DescribeWindow(TheWindowInfo),
                            element = TheElement.Info,
                        });
                    }

                    // Extended: Invoke を持たない選択項目は物理クリックより先に SelectionItemPattern で選ぶ（カーソルを動かさない）
                    McpToolResult TheSelectionResult = TrySelectElementWithSelectionItemPattern(TheElement, TheSelector, TheWindowInfo);
                    if (TheSelectionResult != null)
                    {
                        return TheSelectionResult;
                    }

                    string TheBoundsError = UiInteractionContext.DescribePhysicalPrerequisite(TheElement, TheSelector);
                    if (TheBoundsError != null)
                    {
                        return McpToolResult.Error("Element does not support InvokePattern and a physical click is not possible: " + TheBoundsError);
                    }
                    int TheClickX = TheElement.CenterX;
                    int TheClickY = TheElement.CenterY;
                    TheContext.RefreshGeometry();
                    string ThePointError = TheContext.PreparePhysicalPoint(TheClickX, TheClickY, "click point");
                    if (ThePointError != null)
                    {
                        return McpToolResult.Error(ThePointError);
                    }

                    PerformMouseInput("click", TheClickX, TheClickY, IsInputBlocked, () => UiTools.PerformClick(TheClickX, TheClickY, IsCursorRestored));
                    return McpToolResult.Success(new
                    {
                        message = $"Clicked element with {TheSelector.Describe()} in window {TheWindowInfo.Handle} at ({TheClickX}, {TheClickY})",
                        method = "physicalClick",
                        window = DescribeWindow(TheWindowInfo),
                        element = TheElement.Info,
                        clickX = TheClickX,
                        clickY = TheClickY,
                    });
                });
            }
            catch (TimeoutException TheException)
            {
                return McpToolResult.Error(TheException.Message);
            }
            catch (Exception TheException)
            {
                return McpToolResult.Error($"ui_window_click failed in window {TheWindow.ToInt64()}: {TheException.Message}");
            }

            if (!TheResult.IsError)
            {
                await DelayAfterActionAsync(InArgs);
            }
            return TheResult;
        }

        /// <summary>
        /// Extended: Invoke を持たない選択項目（TabItem / ListItem など）を SelectionItemPattern.Select で選ぶ。
        /// 既に選択済みなら Select を呼ばずに成功扱いとし（alreadySelected = true）、Select したときは IsSelected を読み直して
        /// 選択が切り替わったことを確認する（verified = true）。パターン非対応・例外・選択状態が変わらない場合は null を返し、
        /// 呼び出し元は従来どおり物理クリックへ落ちる。
        /// </summary>
        /// <param name="InElement">解決済みの要素（安全境界の確認後）。</param>
        /// <param name="InSelector">要素セレクター（メッセージ用）。</param>
        /// <param name="InWindowInfo">対象ウィンドウの情報（メッセージ・戻り値用）。</param>
        /// <returns>選択できた場合の成功結果。物理クリックへ落ちる場合は null。</returns>
        private static McpToolResult TrySelectElementWithSelectionItemPattern(UiWindowResolvedElement InElement, UiWindowElementSelector InSelector,
            WindowInfo InWindowInfo)
        {
            bool IsAlreadySelected;
            try
            {
                if (!InElement.Element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object ThePattern))
                {
                    return null;
                }
                SelectionItemPattern TheSelectionItem = (SelectionItemPattern)ThePattern;
                IsAlreadySelected = TheSelectionItem.Current.IsSelected;
                if (!IsAlreadySelected)
                {
                    TheSelectionItem.Select();
                    if (!TheSelectionItem.Current.IsSelected)
                    {
                        // 選択が切り替わらなかった場合は物理クリックへ落ちる
                        return null;
                    }
                }
            }
            catch
            {
                // パターンが使えない・要素が消えた・Select が拒否された場合は物理クリックへ落ちる
                return null;
            }

            return McpToolResult.Success(new
            {
                message = IsAlreadySelected
                    ? $"Element with {InSelector.Describe()} in window {InWindowInfo.Handle} was already selected (SelectionItemPattern)"
                    : $"Selected element with {InSelector.Describe()} in window {InWindowInfo.Handle} using SelectionItemPattern",
                method = "selectionItemPattern",
                window = DescribeWindow(InWindowInfo),
                element = InElement.Info,
                alreadySelected = IsAlreadySelected,
                verified = true,
            });
        }

        /// <summary>ui_window_double_click の本体。物理ダブルクリック（upstream PerformDoubleClick）のみ。</summary>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        /// <param name="InArgs">ツール引数。</param>
        /// <returns>text（message / method / window / element / clickX / clickY）、またはエラー。</returns>
        private static Task<McpToolResult> UiWindowDoubleClickAsync(VsServiceAccessor InAccessor, JObject InArgs)
        {
            return ExecutePhysicalPointActionAsync(InAccessor, InArgs, "ui_window_double_click", "physicalDoubleClick", "double-click point",
                (InX, InY, InIsCursorRestored) => UiTools.PerformDoubleClick(InX, InY, InIsCursorRestored), null);
        }

        /// <summary>
        /// ui_window_right_click の本体。物理右クリック後に observeMs 待ち、新しく現れたトップレベルウィンドウ（newWindows）と、
        /// 内容がメニューであるトップレベルウィンドウ（popupWindows）を報告する。
        /// </summary>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        /// <param name="InArgs">ツール引数。</param>
        /// <returns>text（message / method / window / element / clickX / clickY / newWindows / popupWindows / contextMenuObserved）、またはエラー。</returns>
        private static Task<McpToolResult> UiWindowRightClickAsync(VsServiceAccessor InAccessor, JObject InArgs)
        {
            int TheObserveMs = InArgs.Value<int?>("observeMs") ?? _DEFAULT_OBSERVE_MS;
            if (TheObserveMs < 0)
            {
                TheObserveMs = 0;
            }
            if (TheObserveMs > _MAX_OBSERVE_MS)
            {
                TheObserveMs = _MAX_OBSERVE_MS;
            }
            return ExecutePhysicalPointActionAsync(InAccessor, InArgs, "ui_window_right_click", "physicalRightClick", "right-click point",
                (InX, InY, InIsCursorRestored) => UiTools.PerformRightClick(InX, InY, InIsCursorRestored), TheObserveMs);
        }

        /// <summary>
        /// double_click / right_click 共通の本体。要素の一意解決とモーダル判定 → 座標ゲート → 入力注入。
        /// InObserveMs が指定された場合は注入後にその時間待ち、デバッグ対象プロセスに新しく現れた可視トップレベルウィンドウを newWindows として、
        /// 内容がメニューである可視トップレベルウィンドウを popupWindows として返す。
        /// </summary>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        /// <param name="InArgs">ツール引数。</param>
        /// <param name="InToolName">エラーメッセージ用のツール名。</param>
        /// <param name="InMethodName">戻り値の method。</param>
        /// <param name="InPointName">メッセージ用の点の呼び名。</param>
        /// <param name="InPerform">入力注入（x, y, restoreCursor）。</param>
        /// <param name="InObserveMs">ポップアップ観測の待ち時間。観測しない場合は null。</param>
        /// <returns>結果、またはエラー。</returns>
        private static async Task<McpToolResult> ExecutePhysicalPointActionAsync(VsServiceAccessor InAccessor, JObject InArgs, string InToolName, string InMethodName,
            string InPointName, Action<int, int, bool> InPerform, int? InObserveMs)
        {
            (IntPtr TheWindow, HashSet<uint> TheProcessIds, McpToolResult TheError) = await UiWindowUiaTools.ResolveWindowWithProcessesAsync(InAccessor, InArgs);
            if (TheError != null)
            {
                return TheError;
            }
            string TheSelectorError = UiWindowElementResolver.TryParseSelector(InArgs, string.Empty, out UiWindowElementSelector TheSelector);
            if (TheSelectorError != null)
            {
                return McpToolResult.Error(TheSelectorError);
            }
            bool IsCursorRestored = InArgs.Value<bool?>("restoreCursor") ?? true;
            bool IsInputBlocked = InArgs.Value<bool?>("blockInput") ?? false;

            McpToolResult TheResult;
            try
            {
                TheResult = await UiTools.RunUiaWithTimeoutAsync(() =>
                {
                    string TheActionError = PrepareElementAction(TheWindow, TheProcessIds, TheSelector, true, null,
                        out UiInteractionContext TheContext, out UiWindowResolvedElement TheElement, out _);
                    if (TheActionError != null)
                    {
                        return McpToolResult.Error(TheActionError);
                    }
                    WindowInfo TheWindowInfo = TheContext.WindowInfo;

                    int TheX = TheElement.CenterX;
                    int TheY = TheElement.CenterY;
                    TheContext.RefreshGeometry();
                    string ThePointError = TheContext.PreparePhysicalPoint(TheX, TheY, InPointName);
                    if (ThePointError != null)
                    {
                        return McpToolResult.Error(ThePointError);
                    }

                    HashSet<long> TheWindowsBefore = InObserveMs.HasValue
                        ? new HashSet<long>(DebuggeeWindowEnumerator.EnumerateTopLevelWindows(TheProcessIds, false).Select(TheCandidate => TheCandidate.Handle))
                        : null;

                    // 観測付き（right_click）のときは、カーソル復元を観測待ちの後まで遅らせる。RIGHTUP 直後に復元すると WPF が
                    // コンテキストメニューを開く時点のカーソル位置が要素外になり、メニューが開かない（Phase 7 実測）。
                    bool IsRestoredAfterObservation = IsCursorRestored && InObserveMs.HasValue;
                    POINT TheSavedCursor = new POINT();
                    bool HasSavedCursor = IsRestoredAfterObservation && TheContext.TrySaveCursor(out TheSavedCursor);

                    PerformMouseInput(InMethodName, TheX, TheY, IsInputBlocked, () => InPerform(TheX, TheY, IsCursorRestored && !IsRestoredAfterObservation));

                    List<object> TheNewWindows = null;
                    List<object> ThePopupWindows = null;
                    List<long> TheMenuHandles = null;
                    bool? IsContextMenuObserved = null;
                    if (InObserveMs.HasValue)
                    {
                        System.Threading.Thread.Sleep(InObserveMs.Value);
                        if (HasSavedCursor)
                        {
                            TheContext.RestoreCursor(TheSavedCursor);
                        }
                        List<WindowInfo> TheWindowsAfter = DebuggeeWindowEnumerator.EnumerateTopLevelWindows(TheProcessIds, false);
                        TheNewWindows = TheWindowsAfter
                            .Where(TheCandidate => !TheWindowsBefore.Contains(TheCandidate.Handle))
                            .Select(TheCandidate => (object)new
                            {
                                handle = TheCandidate.Handle,
                                className = TheCandidate.ClassName,
                                title = TheCandidate.Title,
                                bounds = TheCandidate.Bounds,
                                ownerHandle = TheCandidate.OwnerHandle,
                                isEnabled = TheCandidate.IsEnabled,
                            })
                            .ToList();
                        // Extended: 差分（newWindows）は閉じたばかりのメニューと同じ HWND が再利用されると 0 件になるため、内容でも検出する
                        ThePopupWindows = CollectPopupWindows(TheWindowsAfter, TheWindow, TheWindowsBefore, out TheMenuHandles);
                        IsContextMenuObserved = ThePopupWindows.Count > 0;
                    }

                    return McpToolResult.Success(new
                    {
                        message = $"{InMethodName} on element with {TheSelector.Describe()} in window {TheWindowInfo.Handle} at ({TheX}, {TheY})",
                        method = InMethodName,
                        window = DescribeWindow(TheWindowInfo),
                        element = TheElement.Info,
                        clickX = TheX,
                        clickY = TheY,
                        newWindows = TheNewWindows,
                        popupWindows = ThePopupWindows,
                        menuHandles = TheMenuHandles,
                        contextMenuObserved = IsContextMenuObserved,
                        observeMs = InObserveMs,
                    });
                });
            }
            catch (TimeoutException TheException)
            {
                return McpToolResult.Error(TheException.Message);
            }
            catch (Exception TheException)
            {
                return McpToolResult.Error($"{InToolName} failed in window {TheWindow.ToInt64()}: {TheException.Message}");
            }

            if (!TheResult.IsError)
            {
                await DelayAfterActionAsync(InArgs);
            }
            return TheResult;
        }

        /// <summary>
        /// Extended: 右クリック後に開いているポップアップ（コンテキストメニュー）を「内容」で検出する。可視トップレベルの差分だけでは、
        /// 直前に閉じたメニューと同じ HWND が再利用されたときに検出できない（Phase 7 実測）。対象ウィンドウ自身は候補から除き、
        /// クラス名が Win32 メニュー（#32768）か、UIA 部分木に Menu / MenuItem を含むウィンドウをポップアップとみなす。STA スレッドで呼ぶこと。
        /// </summary>
        /// <param name="InWindows">観測待ちの後に列挙した可視トップレベルウィンドウ。</param>
        /// <param name="InWindow">右クリックした対象のトップレベル HWND。</param>
        /// <param name="InWindowsBefore">クリック前に存在した可視トップレベル HWND の集合（isNew の判定用）。</param>
        /// <param name="OutMenuHandles">ポップアップのうち win32Menu / wpfContextMenu として分類できたものの HWND（ui_menu_* へ渡せる）。</param>
        /// <returns>ポップアップと判定したウィンドウの一覧（handle / className / title / bounds / ownerHandle / isEnabled / isNew / menuItemCount）。</returns>
        private static List<object> CollectPopupWindows(List<WindowInfo> InWindows, IntPtr InWindow, HashSet<long> InWindowsBefore, out List<long> OutMenuHandles)
        {
            List<object> ThePopupWindows = new List<object>();
            OutMenuHandles = new List<long>();
            long TheTargetHandle = InWindow.ToInt64();
            int TheInspectedCount = 0;
            foreach (WindowInfo TheCandidate in InWindows)
            {
                if (TheCandidate.Handle == TheTargetHandle)
                {
                    continue;
                }
                if (TheInspectedCount >= _MAX_POPUP_CANDIDATES)
                {
                    break;
                }
                TheInspectedCount++;

                bool IsWin32Menu = string.Equals(TheCandidate.ClassName, UiPopupMenuResolver.Win32MenuClassName, StringComparison.Ordinal);
                int TheMenuItemCount = 0;
                if (!IsWin32Menu && !UiPopupMenuResolver.HasMenuElements(new IntPtr(TheCandidate.Handle), out TheMenuItemCount))
                {
                    continue;
                }
                ThePopupWindows.Add(new
                {
                    handle = TheCandidate.Handle,
                    className = TheCandidate.ClassName,
                    title = TheCandidate.Title,
                    bounds = TheCandidate.Bounds,
                    ownerHandle = TheCandidate.OwnerHandle,
                    isEnabled = TheCandidate.IsEnabled,
                    isNew = !InWindowsBefore.Contains(TheCandidate.Handle),
                    menuItemCount = IsWin32Menu ? (int?)null : TheMenuItemCount,
                });

                // Extended (Phase 8): 内容分類まで通ったものだけを menuHandles として返す（popupWindows の判定は Phase 7 のまま）。
                // ここは「メニューかどうか」だけが要るので項目照会のリトライは無効化する（候補ごとに数百 ms の待ちが積み上がるため）
                if (UiPopupMenuResolver.Classify(TheCandidate, false, out UiMenuInfo TheMenu, out _) && UiPopupMenuResolver.IsKnownMenuType(TheMenu.MenuType))
                {
                    OutMenuHandles.Add(TheCandidate.Handle);
                }
            }
            return ThePopupWindows;
        }

        /// <summary>ui_window_drag の本体。source / target を同じウィンドウ配下で解決し、両方の座標ゲートを通してから upstream PerformDrag を実行する。</summary>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        /// <param name="InArgs">ツール引数。</param>
        /// <returns>text（message / method / window / source / target / startX / startY / endX / endY / steps / delayMs）、またはエラー。</returns>
        private static async Task<McpToolResult> UiWindowDragAsync(VsServiceAccessor InAccessor, JObject InArgs)
        {
            (IntPtr TheWindow, HashSet<uint> TheProcessIds, McpToolResult TheError) = await UiWindowUiaTools.ResolveWindowWithProcessesAsync(InAccessor, InArgs);
            if (TheError != null)
            {
                return TheError;
            }
            string TheSourceError = UiWindowElementResolver.TryParseSelector(InArgs, "source", out UiWindowElementSelector TheSourceSelector);
            if (TheSourceError != null)
            {
                return McpToolResult.Error("Drag source: " + TheSourceError);
            }
            string TheTargetError = UiWindowElementResolver.TryParseSelector(InArgs, "target", out UiWindowElementSelector TheTargetSelector);
            if (TheTargetError != null)
            {
                return McpToolResult.Error("Drag target: " + TheTargetError);
            }
            if (!TheSourceSelector.HasCriteria)
            {
                return McpToolResult.Error("Drag source: at least one selector must be provided (sourceAutomationId, sourceName, sourceControlType, or sourceClassName)");
            }
            if (!TheTargetSelector.HasCriteria)
            {
                return McpToolResult.Error("Drag target: at least one selector must be provided (targetAutomationId, targetName, targetControlType, or targetClassName)");
            }

            int TheSteps = InArgs.Value<int?>("steps") ?? 10;
            int TheDelayMs = InArgs.Value<int?>("delayMs") ?? 10;
            int? TheDurationMs = InArgs.Value<int?>("durationMs");
            if (TheSteps < 1)
            {
                TheSteps = 1;
            }
            if (TheSteps > 100)
            {
                TheSteps = 100;
            }
            if (TheDurationMs.HasValue)
            {
                TheDelayMs = TheDurationMs.Value / TheSteps;
            }
            if (TheDelayMs < 1)
            {
                TheDelayMs = 1;
            }
            if (TheDelayMs > 1000)
            {
                TheDelayMs = 1000;
            }
            bool IsCursorRestored = InArgs.Value<bool?>("restoreCursor") ?? true;
            bool IsInputBlocked = InArgs.Value<bool?>("blockInput") ?? false;

            try
            {
                return await UiTools.RunUiaWithTimeoutAsync(() =>
                {
                    string TheSourceActionError = PrepareElementAction(TheWindow, TheProcessIds, TheSourceSelector, true, "Drag source",
                        out UiInteractionContext TheContext, out UiWindowResolvedElement TheSource, out _);
                    if (TheSourceActionError != null)
                    {
                        return McpToolResult.Error(TheSourceActionError);
                    }
                    WindowInfo TheWindowInfo = TheContext.WindowInfo;
                    string TheTargetResolveError = UiWindowElementResolver.TryResolveSingle(TheWindow, TheTargetSelector, out UiWindowResolvedElement TheTarget,
                        out UiWindowResolveFailure TheTargetFailure);
                    if (TheTargetResolveError != null)
                    {
                        return McpToolResult.Error("Drag target: " + TheTargetResolveError + DescribeCrossWindowHint(TheTargetFailure));
                    }
                    if (!TheTarget.IsEnabled)
                    {
                        return McpToolResult.Error($"Drag target: Element with {TheTargetSelector.Describe()} is disabled (IsEnabled=false); refusing to act on it");
                    }
                    string TheTargetBoundsError = UiInteractionContext.DescribePhysicalPrerequisite(TheTarget, TheTargetSelector);
                    if (TheTargetBoundsError != null)
                    {
                        return McpToolResult.Error("Drag target: " + TheTargetBoundsError);
                    }

                    int TheStartX = TheSource.CenterX;
                    int TheStartY = TheSource.CenterY;
                    int TheEndX = TheTarget.CenterX;
                    int TheEndY = TheTarget.CenterY;
                    TheContext.RefreshGeometry();
                    string TheStartError = TheContext.PreparePhysicalPoint(TheStartX, TheStartY, "drag start point");
                    if (TheStartError != null)
                    {
                        return McpToolResult.Error(TheStartError);
                    }
                    string TheEndError = UiWindowActionValidator.ValidatePhysicalPoint(TheWindow, TheEndX, TheEndY, "drag end point");
                    if (TheEndError != null)
                    {
                        return McpToolResult.Error(TheEndError);
                    }

                    PerformMouseInput("drag", TheStartX, TheStartY, IsInputBlocked, () => UiTools.PerformDrag(TheStartX, TheStartY, TheEndX, TheEndY, TheSteps, TheDelayMs, IsCursorRestored));
                    return McpToolResult.Success(new
                    {
                        message = $"Dragged from element with {TheSourceSelector.Describe()} ({TheStartX}, {TheStartY}) to element with {TheTargetSelector.Describe()} ({TheEndX}, {TheEndY}) in window {TheWindowInfo.Handle}",
                        method = "physicalDrag",
                        window = DescribeWindow(TheWindowInfo),
                        source = TheSource.Info,
                        target = TheTarget.Info,
                        startX = TheStartX,
                        startY = TheStartY,
                        endX = TheEndX,
                        endY = TheEndY,
                        steps = TheSteps,
                        delayMs = TheDelayMs,
                    });
                });
            }
            catch (TimeoutException TheException)
            {
                return McpToolResult.Error(TheException.Message);
            }
            catch (Exception TheException)
            {
                return McpToolResult.Error($"ui_window_drag failed in window {TheWindow.ToInt64()}: {TheException.Message}");
            }
        }

        /// <summary>
        /// drag の target を解決できなかったときに「cross-window drag は非対応」の補足を返す。指定ウィンドウ配下に候補が無い（NotFound）ときは
        /// 別ウィンドウの要素を狙っている可能性が高いので補足を付ける。エラー文言の部分一致ではなく解決失敗の種別で判定する。
        /// </summary>
        /// <param name="InFailure">TryResolveSingle が返した解決失敗の種別。</param>
        /// <returns>補足文。付けない場合は空文字。</returns>
        private static string DescribeCrossWindowHint(UiWindowResolveFailure InFailure)
        {
            if (InFailure == UiWindowResolveFailure.NotFound)
            {
                return " (cross-window drag is not supported: source and target must both be inside the requested window)";
            }
            return string.Empty;
        }

        /// <summary>
        /// PostMessage WM_MOUSEWHEEL の効果を検証するための ScrollPattern 提供元を決める。要素指定ならその要素から、座標指定なら点の直下の要素から
        /// 親方向へ ScrollPattern を持つ要素を探す（upstream UiTools.FindScrollPatternProvider）。
        /// 座標指定で見つからなかったときだけ、対象ウィンドウの UIA 部分木から点を含む提供元を探す（Extended: AutomationElement.FromPoint が
        /// WPF の ScrollViewer へ届かず検証できないまま成功を返す実測への対処）。それでも見つからなければ検証できないので null。
        /// </summary>
        /// <param name="InElement">解決済み要素。座標指定のときは null。</param>
        /// <param name="InWindow">対象のトップレベル HWND（座標指定のフォールバック探索の起点）。</param>
        /// <param name="InX">スクリーン X（物理 px）。</param>
        /// <param name="InY">スクリーン Y（物理 px）。</param>
        /// <returns>ScrollPattern を持つ要素。見つからなければ null。</returns>
        private static AutomationElement FindScrollVerificationProvider(UiWindowResolvedElement InElement, IntPtr InWindow, int InX, int InY)
        {
            AutomationElement TheProvider = null;
            try
            {
                AutomationElement TheOrigin = InElement != null ? InElement.Element : AutomationElement.FromPoint(new System.Windows.Point(InX, InY));
                if (TheOrigin != null)
                {
                    TheProvider = UiTools.FindScrollPatternProvider(TheOrigin);
                }
            }
            catch
            {
                // 点の上に UIA 要素が無い・提供元が消えた場合はフォールバックへ
                TheProvider = null;
            }
            if (TheProvider == null && InElement == null)
            {
                TheProvider = FindScrollProviderAtPointInWindow(InWindow, InX, InY);   // Extended: 座標指定のみ
            }
            return TheProvider;
        }

        /// <summary>
        /// Extended: 対象ウィンドウの UIA root からコントロールビューを走査し、点 (InX, InY) を BoundingRectangle に含み ScrollPattern を持つ要素のうち
        /// 最も深いものを返す。走査は要素数の上限で打ち切る。STA スレッドで呼ぶこと。
        /// </summary>
        /// <param name="InWindow">対象のトップレベル HWND。</param>
        /// <param name="InX">スクリーン X（物理 px）。</param>
        /// <param name="InY">スクリーン Y（物理 px）。</param>
        /// <returns>見つかった要素。見つからなければ null。</returns>
        private static AutomationElement FindScrollProviderAtPointInWindow(IntPtr InWindow, int InX, int InY)
        {
            try
            {
                AutomationElement TheRoot = AutomationElement.FromHandle(InWindow);
                if (TheRoot == null)
                {
                    return null;
                }
                AutomationElement TheDeepestProvider = null;
                int TheDeepestDepth = -1;
                int TheVisitedCount = 0;
                SearchScrollProviderAtPoint(TheRoot, InX, InY, 0, ref TheVisitedCount, ref TheDeepestProvider, ref TheDeepestDepth);
                return TheDeepestProvider;
            }
            catch
            {
                // ウィンドウが閉じた・UIA が使えない場合は検証しない
                return null;
            }
        }

        /// <summary>
        /// Extended: FindScrollProviderAtPointInWindow の再帰本体。要素自身を判定してからコントロールビューの子をツリー順にたどる。
        /// </summary>
        /// <param name="InElement">走査中の要素。</param>
        /// <param name="InX">スクリーン X（物理 px）。</param>
        /// <param name="InY">スクリーン Y（物理 px）。</param>
        /// <param name="InDepth">root からの深さ（root が 0）。</param>
        /// <param name="InOutVisitedCount">走査済み要素数（上限判定用）。</param>
        /// <param name="InOutDeepestProvider">これまでに見つかった最も深い提供元。</param>
        /// <param name="InOutDeepestDepth">InOutDeepestProvider の深さ。未発見なら -1。</param>
        private static void SearchScrollProviderAtPoint(AutomationElement InElement, int InX, int InY, int InDepth, ref int InOutVisitedCount,
            ref AutomationElement InOutDeepestProvider, ref int InOutDeepestDepth)
        {
            if (InOutVisitedCount >= _MAX_SCROLL_SEARCH_ELEMENTS)
            {
                return;
            }
            InOutVisitedCount++;

            try
            {
                System.Windows.Rect TheRect = InElement.Current.BoundingRectangle;
                if (InDepth > InOutDeepestDepth && !TheRect.IsEmpty && TheRect.Contains(new System.Windows.Point(InX, InY))
                    && InElement.TryGetCurrentPattern(ScrollPattern.Pattern, out object ThePattern))
                {
                    InOutDeepestProvider = InElement;
                    InOutDeepestDepth = InDepth;
                }
            }
            catch
            {
                // 消えた要素は判定できないので飛ばす（子の走査は続ける）
            }

            try
            {
                AutomationElement TheChild = TreeWalker.ControlViewWalker.GetFirstChild(InElement);
                while (TheChild != null)
                {
                    if (InOutVisitedCount >= _MAX_SCROLL_SEARCH_ELEMENTS)
                    {
                        return;
                    }
                    SearchScrollProviderAtPoint(TheChild, InX, InY, InDepth + 1, ref InOutVisitedCount, ref InOutDeepestProvider, ref InOutDeepestDepth);
                    TheChild = TreeWalker.ControlViewWalker.GetNextSibling(TheChild);
                }
            }
            catch
            {
                // 走査中に消えた要素は無視する（upstream の走査と同じ）
            }
        }

        /// <summary>ScrollPattern 提供元の現在のスクロール位置（％）を読む。</summary>
        /// <param name="InProvider">ScrollPattern を持つ要素。null なら検証しない。</param>
        /// <param name="InIsHorizontal">水平方向を読むか（false なら垂直）。</param>
        /// <returns>スクロール位置（％）。読めなければ double.NaN。</returns>
        private static double ReadScrollPercent(AutomationElement InProvider, bool InIsHorizontal)
        {
            if (InProvider == null)
            {
                return double.NaN;
            }
            try
            {
                if (!InProvider.TryGetCurrentPattern(ScrollPattern.Pattern, out object ThePattern))
                {
                    return double.NaN;
                }
                ScrollPattern TheScrollPattern = (ScrollPattern)ThePattern;
                return InIsHorizontal ? TheScrollPattern.Current.HorizontalScrollPercent : TheScrollPattern.Current.VerticalScrollPercent;
            }
            catch
            {
                // 要素が消えた・パターンが取れない場合は検証できない
                return double.NaN;
            }
        }

        /// <summary>
        /// ui_window_mouse_wheel の本体。要素指定なら ScrollPattern → WM_MOUSEWHEEL の PostMessage（点の直下 HWND が対象ウィンドウ配下のときだけ）→ 物理ホイール。
        /// 座標指定（x / y）なら PostMessage → 物理ホイール。
        /// </summary>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        /// <param name="InArgs">ツール引数。</param>
        /// <returns>text（message / method / window / element / x / y / clicks / horizontal）、またはエラー。</returns>
        private static async Task<McpToolResult> UiWindowMouseWheelAsync(VsServiceAccessor InAccessor, JObject InArgs)
        {
            int? TheClicks = InArgs.Value<int?>("clicks");
            if (!TheClicks.HasValue)
            {
                return McpToolResult.Error("Parameter 'clicks' is required (number of wheel notches, non-zero)");
            }
            if (TheClicks.Value == 0)
            {
                return McpToolResult.Error("Parameter 'clicks' must be non-zero");
            }
            bool IsHorizontal = InArgs.Value<bool?>("horizontal") ?? false;
            bool IsPatternUsed = InArgs.Value<bool?>("usePattern") ?? true;
            bool IsCursorRestored = InArgs.Value<bool?>("restoreCursor") ?? true;
            bool IsInputBlocked = InArgs.Value<bool?>("blockInput") ?? false;
            int? TheArgX = InArgs.Value<int?>("x");
            int? TheArgY = InArgs.Value<int?>("y");

            (IntPtr TheWindow, HashSet<uint> TheProcessIds, McpToolResult TheError) = await UiWindowUiaTools.ResolveWindowWithProcessesAsync(InAccessor, InArgs);
            if (TheError != null)
            {
                return TheError;
            }
            string TheSelectorError = UiWindowElementResolver.TryParseSelector(InArgs, string.Empty, out UiWindowElementSelector TheSelector);
            if (TheSelectorError != null)
            {
                return McpToolResult.Error(TheSelectorError);
            }
            if (!TheSelector.HasCriteria && (!TheArgX.HasValue || !TheArgY.HasValue))
            {
                return McpToolResult.Error("Either an element selector (automationId, name, controlType, className) or both 'x' and 'y' coordinates are required");
            }

            McpToolResult TheResult;
            try
            {
                TheResult = await UiTools.RunUiaWithTimeoutAsync(() =>
                {
                    UiInteractionContext TheContext;
                    WindowInfo TheWindowInfo;
                    UiWindowResolvedElement TheElement = null;
                    int TheX;
                    int TheY;
                    string TheAxis = IsHorizontal ? "horizontal" : "vertical";

                    if (TheSelector.HasCriteria)
                    {
                        string TheActionError = PrepareElementAction(TheWindow, TheProcessIds, TheSelector, false, null, out TheContext, out TheElement, out _);
                        if (TheActionError != null)
                        {
                            return McpToolResult.Error(TheActionError);
                        }
                        TheWindowInfo = TheContext.WindowInfo;

                        if (IsPatternUsed && UiTools.TryScrollWithPattern(TheElement.Element, TheClicks.Value, IsHorizontal))
                        {
                            return McpToolResult.Success(new
                            {
                                message = $"Scrolled {TheAxis} {TheClicks.Value} click(s) on element with {TheSelector.Describe()} in window {TheWindowInfo.Handle} via ScrollPattern (cursor not moved)",
                                method = "ScrollPattern",
                                window = DescribeWindow(TheWindowInfo),
                                element = TheElement.Info,
                                clicks = TheClicks.Value,
                                horizontal = IsHorizontal,
                            });
                        }

                        string TheBoundsError = UiInteractionContext.DescribePhysicalPrerequisite(TheElement, TheSelector);
                        if (TheBoundsError != null)
                        {
                            return McpToolResult.Error(TheBoundsError);
                        }
                        TheX = TheElement.CenterX;
                        TheY = TheElement.CenterY;
                    }
                    else
                    {
                        string TheTargetError = UiInteractionContext.TryCreate(TheWindow, TheProcessIds, out TheContext);
                        if (TheTargetError != null)
                        {
                            return McpToolResult.Error(TheTargetError);
                        }
                        TheWindowInfo = TheContext.WindowInfo;
                        TheX = TheArgX.Value;
                        TheY = TheArgY.Value;
                    }

                    string TheRectError = UiTools.ValidateCoordinatesInWindow(TheWindow, TheX, TheY);
                    if (TheRectError != null)
                    {
                        return McpToolResult.Error(TheRectError);
                    }

                    // PostMessage WM_MOUSEWHEEL: 点の直下の HWND がデバッグ対象で、かつそのトップレベルが対象ウィンドウのときだけ（カーソルを動かさない）
                    IntPtr TheHwndAtPoint = UiTools.WithDpiAwareness(() => UiTools.FindHwndAtPoint(TheX, TheY, (int)TheWindowInfo.ProcessId));
                    if (TheHwndAtPoint != IntPtr.Zero)
                    {
                        IntPtr TheRootAtPoint = GetAncestor(TheHwndAtPoint, GA_ROOT);
                        if ((TheRootAtPoint == IntPtr.Zero ? TheHwndAtPoint : TheRootAtPoint) != TheWindow)
                        {
                            TheHwndAtPoint = IntPtr.Zero;
                        }
                    }
                    if (TheHwndAtPoint != IntPtr.Zero)
                    {
                        // PostMessage は「送れた」だけで実際にスクロールしたとは限らない（WPF は無視することがある）。
                        // 送信前に検証用の ScrollPattern 提供元とスクロール位置を控えておき、送信後の変化で成否を判定する。
                        AutomationElement TheScrollProvider = FindScrollVerificationProvider(TheElement, TheWindow, TheX, TheY);
                        double TheScrollPercentBefore = ReadScrollPercent(TheScrollProvider, IsHorizontal);
                        bool IsScrollVerifiable = !double.IsNaN(TheScrollPercentBefore);

                        int TheStep = TheClicks.Value > 0 ? 1 : -1;
                        int ThePosted = 0;
                        for (int TheIndex = 0; TheIndex < Math.Abs(TheClicks.Value); TheIndex++)
                        {
                            if (!UiTools.TryPostWheelMessage(TheHwndAtPoint, TheX, TheY, TheStep, IsHorizontal))
                            {
                                break;
                            }
                            ThePosted++;
                        }
                        if (ThePosted > 0)
                        {
                            double TheScrollPercentAfter = double.NaN;
                            bool IsScrollChanged = false;
                            if (IsScrollVerifiable)
                            {
                                System.Threading.Thread.Sleep(_POST_MESSAGE_SETTLE_MS);
                                TheScrollPercentAfter = ReadScrollPercent(TheScrollProvider, IsHorizontal);
                                IsScrollChanged = !double.IsNaN(TheScrollPercentAfter) && TheScrollPercentAfter != TheScrollPercentBefore;
                            }
                            if (!IsScrollVerifiable || IsScrollChanged)
                            {
                                return McpToolResult.Success(new
                                {
                                    message = $"Scrolled {TheAxis} wheel {TheClicks.Value} click(s) at ({TheX}, {TheY}) in window {TheWindowInfo.Handle} via PostMessage (cursor not moved)",
                                    method = "PostMessageWheel",
                                    window = DescribeWindow(TheWindowInfo),
                                    element = TheElement == null ? null : TheElement.Info,
                                    x = TheX,
                                    y = TheY,
                                    clicks = TheClicks.Value,
                                    horizontal = IsHorizontal,
                                    verified = IsScrollChanged,
                                    note = IsScrollVerifiable ? null : "scroll effect could not be verified (no ScrollPattern provider at the point)",
                                    scrollPercentBefore = IsScrollVerifiable ? (double?)TheScrollPercentBefore : null,
                                    scrollPercentAfter = IsScrollChanged ? (double?)TheScrollPercentAfter : null,
                                });
                            }
                            // 検証できて変化が無い＝PostMessage は効いていないので、物理ホイールで全 clicks を送り直す。
                            // PostMessage はカーソルを動かさないため、ここで二重入力になっても実害は無い。
                        }
                    }

                    TheContext.RefreshGeometry();
                    string ThePointError = TheContext.PreparePhysicalPoint(TheX, TheY, "wheel point");
                    if (ThePointError != null)
                    {
                        return McpToolResult.Error(ThePointError);
                    }
                    PerformMouseInput("wheel", TheX, TheY, IsInputBlocked, () => UiTools.PerformWheel(TheX, TheY, TheClicks.Value, IsHorizontal, IsCursorRestored));
                    return McpToolResult.Success(new
                    {
                        message = $"Scrolled {TheAxis} wheel {TheClicks.Value} click(s) at ({TheX}, {TheY}) in window {TheWindowInfo.Handle} (physical events)",
                        method = "PhysicalWheel",
                        window = DescribeWindow(TheWindowInfo),
                        element = TheElement == null ? null : TheElement.Info,
                        x = TheX,
                        y = TheY,
                        clicks = TheClicks.Value,
                        horizontal = IsHorizontal,
                        cursorRestored = IsCursorRestored,
                    });
                });
            }
            catch (TimeoutException TheException)
            {
                return McpToolResult.Error(TheException.Message);
            }
            catch (Exception TheException)
            {
                return McpToolResult.Error($"ui_window_mouse_wheel failed in window {TheWindow.ToInt64()}: {TheException.Message}");
            }

            if (!TheResult.IsError)
            {
                await DelayAfterActionAsync(InArgs);
            }
            return TheResult;
        }

        /// <summary>
        /// Extended (Phase 10): 物理マウス入力を upstream の <c>UiTools.WithBlockedInput</c> 経由でそのまま実行し、
        /// 前後のフォアグラウンドと注入位置を診断へ残す。入力の内容・順序・カーソル復元の扱いは一切変えない。
        /// </summary>
        /// <param name="InKind">入力の種類（click / doubleClick / rightClick / drag / wheel）。</param>
        /// <param name="InX">注入位置のスクリーン X（物理 px）。</param>
        /// <param name="InY">注入位置のスクリーン Y（物理 px）。</param>
        /// <param name="InIsInputBlocked">実行中にユーザー入力を遮断するか。</param>
        /// <param name="InPerform">実際の入力注入。</param>
        private static void PerformMouseInput(string InKind, int InX, int InY, bool InIsInputBlocked, Action InPerform)
        {
            long TheForegroundBefore = GetForegroundWindow().ToInt64();
            UiTools.WithBlockedInput(InIsInputBlocked, InPerform);
            if (!DiagnosticHub.IsEnabled(DiagnosticLevel.Verbose))
            {
                return;
            }
            long TheForegroundAfter = GetForegroundWindow().ToInt64();
            DiagnosticHub.Emit(DiagnosticLevel.Verbose, DiagnosticCategory.INPUT, "input.mouse", InData =>
            {
                InData["kind"] = InKind;
                InData["point"] = $"{InX},{InY}";
                InData["blockInput"] = InIsInputBlocked;
                InData["foregroundBefore"] = TheForegroundBefore;
                InData["foregroundAfter"] = TheForegroundAfter;
                InData["changedForeground"] = TheForegroundAfter != TheForegroundBefore;
            });
        }
    }
}
