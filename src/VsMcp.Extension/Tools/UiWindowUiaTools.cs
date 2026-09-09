using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using Newtonsoft.Json.Linq;
using VsMcp.Extension.McpServer;
using VsMcp.Extension.Services;
using VsMcp.Shared;
using VsMcp.Shared.Protocol;

namespace VsMcp.Extension.Tools
{
    /// <summary>
    /// Extended 独自の「任意のデバッグ対象トップレベル HWND を root にした UI Automation / スクリーンショット」ツール群。
    /// upstream の ui_get_tree / ui_snapshot / ui_capture_region はメインウィンドウ固定なので、その本体（UiTools の internal メソッド）を
    /// そのまま再利用し、root だけを Phase 4 の HWND 検証（IsWindow → GA_ROOT → デバッグ対象 PID 再照合）を通した任意ウィンドウに差し替える。
    /// Phase 7 で ui_window_get_info（Win32 WindowInfo と UIA root / focused / modalState の統合）と
    /// ui_window_find_elements（指定 HWND 配下だけを検索）を追加。upstream の public Tool schema は変更しない。
    /// </summary>
    public static class UiWindowUiaTools
    {
        /// <summary>ツールをレジストリへ登録する。VsMcpPackage.RegisterTools から呼ばれる。</summary>
        /// <param name="InRegistry">登録先レジストリ。</param>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        public static void Register(McpToolRegistry InRegistry, VsServiceAccessor InAccessor)
        {
            InRegistry.Register(
                new McpToolDefinition(
                    "ui_window_get_tree",
                    "[Windows UIA — desktop app being debugged] Get the raw UI element tree of ANY top-level window of the debugged application (modal dialog, MessageBox, " +
                    "TaskDialog, file dialog, owned window) identified by its HWND from ui_list_windows / standard_dialog_detect / standard_file_dialog_detect. " +
                    "Same output and options as ui_get_tree, which is fixed to the main window. The handle is validated first (must exist, is normalized to its top-level " +
                    "window, and must belong to a debugged process — other applications and Visual Studio itself are refused). Prefer ui_window_snapshot unless you need the unpruned tree.",
                    SchemaBuilder.Create()
                        .AddInteger("windowHandle", "HWND of the window to use as the tree root (decimal)", required: true)
                        .AddInteger("depth", "Maximum depth of the tree (default: 3)")
                        .AddInteger("maxChildren", "Maximum number of child elements to enumerate per node (default: 50)")
                        .AddInteger("maxElements", "Maximum total number of elements in the tree (default: 500)")
                        .Build()),
                InArgs => UiWindowGetTreeAsync(InAccessor, InArgs));

            InRegistry.Register(
                new McpToolDefinition(
                    "ui_window_snapshot",
                    "[Windows UIA — desktop app being debugged] Capture a compact semantic snapshot (pruned UI Automation tree with actionable patterns, state flags, rect and " +
                    "focused element, plus an optional screenshot) of ANY top-level window of the debugged application identified by its HWND — the same output as ui_snapshot, " +
                    "which is fixed to the main window. Use it to inspect modal dialogs, MessageBox / TaskDialog / file dialogs or owned windows. The handle is validated first " +
                    "(exists, normalized to its top-level window, belongs to a debugged process).",
                    SchemaBuilder.Create()
                        .AddInteger("windowHandle", "HWND of the window to snapshot (decimal)", required: true)
                        .AddInteger("depth", "Maximum tree depth (default: 8)")
                        .AddInteger("maxElements", "Maximum total elements in the tree (default: 300)")
                        .AddBoolean("includeScreenshot", "Include a screenshot of the window (default: true)")
                        .AddBoolean("includeOffscreen", "Include elements marked IsOffscreen (default: false)")
                        .AddString("ancestorAutomationId", "Limit the snapshot to the subtree rooted at this AutomationId")
                        .Build()),
                InArgs => UiWindowSnapshotAsync(InAccessor, InArgs));

            InRegistry.Register(
                new McpToolDefinition(
                    "ui_window_capture_region",
                    "[Windows UIA — desktop app being debugged] Capture a screenshot of a region of ANY top-level window of the debugged application identified by its HWND. " +
                    "Identical semantics to ui_capture_region (x/y are relative to the captured window image in physical pixels, the region is clamped to the window, " +
                    "same PNG/JPEG size limits) but the window is chosen by 'windowHandle' instead of being fixed to the main window. The handle is validated first " +
                    "(exists, normalized to its top-level window, belongs to a debugged process).",
                    SchemaBuilder.Create()
                        .AddInteger("windowHandle", "HWND of the window to capture from (decimal)", required: true)
                        .AddInteger("x", "X coordinate of the region (relative to window)", required: true)
                        .AddInteger("y", "Y coordinate of the region (relative to window)", required: true)
                        .AddInteger("width", "Width of the region", required: true)
                        .AddInteger("height", "Height of the region", required: true)
                        .Build()),
                InArgs => UiWindowCaptureRegionAsync(InAccessor, InArgs));

            InRegistry.Register(
                new McpToolDefinition(
                    "ui_window_get_info",
                    "[Windows UIA — desktop app being debugged] Get the Win32 window info (same fields as ui_list_windows), the UI Automation root element, the currently focused " +
                    "element (when it belongs to this window) and the modal state of ANY top-level window of the debugged application identified by its HWND, in one structure. " +
                    "'modalState.isBlockedByModal' tells whether the window is currently disabled by a modal dialog (with the blocking window's handle / title / " +
                    "modalCandidateReason); the ui_window_* action tools refuse to act on such a window. 'uiaRoot.nativeWindowHandle' and 'window.handle' are both returned " +
                    "so any difference is visible rather than guessed. The handle is validated first (exists, normalized to its top-level window, belongs to a debugged process).",
                    SchemaBuilder.Create()
                        .AddInteger("windowHandle", "HWND of the window (decimal)", required: true)
                        .Build()),
                InArgs => UiWindowGetInfoAsync(InAccessor, InArgs));

            InRegistry.Register(
                new McpToolDefinition(
                    "ui_window_find_elements",
                    "[Windows UIA — desktop app being debugged] Find UI elements matching criteria (Name / AutomationId / ClassName / ControlType / hasPattern) inside ONE top-level " +
                    "window of the debugged application identified by its HWND — only the descendants of that window are searched (never the whole desktop or other windows); " +
                    "elements hosted by another top-level window (e.g. the Visual Studio in-app toolbar) are skipped and counted in 'skippedOtherWindows'. " +
                    "It works for modal dialogs, MessageBox / TaskDialog / file dialogs and owned windows. Same match modes as ui_find_elements ('exact' default, 'contains', " +
                    "'regex'). Each result carries nativeWindowHandle, isOffscreen, rootWindowHandle and the supported patterns (invoke, value, selectionItem, toggle, " +
                    "expandCollapse, scroll) so it can be passed straight to ui_window_click / double_click / right_click / drag / mouse_wheel. For the buttons of a MessageBox / " +
                    "TaskDialog / file dialog prefer standard_dialog_execute / standard_file_dialog_* over generic clicks. The handle is validated first.",
                    SchemaBuilder.Create()
                        .AddInteger("windowHandle", "HWND of the window whose descendants are searched (decimal)", required: true)
                        .AddString("name", "Name of the UI element to find")
                        .AddString("nameMatch", "Match mode for 'name': 'exact' (default), 'contains', 'regex'")
                        .AddString("automationId", "AutomationId of the UI element to find")
                        .AddString("automationIdMatch", "Match mode for 'automationId': 'exact' (default), 'contains', 'regex'")
                        .AddString("className", "ClassName of the UI element to find")
                        .AddString("classNameMatch", "Match mode for 'className': 'exact' (default), 'contains', 'regex'")
                        .AddString("controlType", "ControlType programmatic name (e.g. 'ControlType.Button' or 'Button')")
                        .AddString("hasPattern", "Comma-separated list of required UIA patterns: invoke, toggle, select, setvalue, expand")
                        .AddString("ancestorAutomationId", "Limit the search to descendants of the element(s) with this AutomationId inside the window")
                        .AddBoolean("includeOffscreen", "Include elements marked IsOffscreen (default: false)")
                        .AddInteger("maxResults", "Maximum number of elements to return (default: 50, max: 1000)")
                        .Build()),
                InArgs => UiWindowFindElementsAsync(InAccessor, InArgs));
        }

        /// <summary>ui_window_get_tree の本体。HWND を検証してから upstream の tree 構築（UiTools.BuildTreeFromWindowAsync）へ渡す。</summary>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        /// <param name="InArgs">ツール引数。</param>
        /// <returns>ui_get_tree と同形式の結果、またはエラー。</returns>
        private static async Task<McpToolResult> UiWindowGetTreeAsync(VsServiceAccessor InAccessor, JObject InArgs)
        {
            (IntPtr TheWindow, McpToolResult TheError) = await ResolveWindowAsync(InAccessor, InArgs);
            if (TheError != null)
            {
                return TheError;
            }

            int TheMaxDepth = InArgs.Value<int?>("depth") ?? 3;
            int TheMaxChildren = InArgs.Value<int?>("maxChildren") ?? 50;
            int TheMaxElements = InArgs.Value<int?>("maxElements") ?? 500;
            return await UiTools.BuildTreeFromWindowAsync(TheWindow, TheMaxDepth, TheMaxChildren, TheMaxElements);
        }

        /// <summary>ui_window_snapshot の本体。HWND を検証してから upstream のスナップショット構築（UiTools.BuildSnapshotFromWindowAsync）へ渡す。</summary>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        /// <param name="InArgs">ツール引数。</param>
        /// <returns>ui_snapshot と同形式の結果、またはエラー。</returns>
        private static async Task<McpToolResult> UiWindowSnapshotAsync(VsServiceAccessor InAccessor, JObject InArgs)
        {
            (IntPtr TheWindow, McpToolResult TheError) = await ResolveWindowAsync(InAccessor, InArgs);
            if (TheError != null)
            {
                return TheError;
            }

            int TheDepth = InArgs.Value<int?>("depth") ?? 8;
            int TheMaxElements = InArgs.Value<int?>("maxElements") ?? 300;
            bool TheIncludeScreenshot = InArgs.Value<bool?>("includeScreenshot") ?? true;
            bool TheIncludeOffscreen = InArgs.Value<bool?>("includeOffscreen") ?? false;
            string TheAncestorAutomationId = InArgs.Value<string>("ancestorAutomationId");
            return await UiTools.BuildSnapshotFromWindowAsync(TheWindow, TheDepth, TheMaxElements, TheIncludeScreenshot, TheIncludeOffscreen, TheAncestorAutomationId);
        }

        /// <summary>ui_window_capture_region の本体。引数検証は upstream ui_capture_region と同じ順序・同じ文言で行い、HWND 検証後に同じ切り出し処理へ渡す。</summary>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        /// <param name="InArgs">ツール引数。</param>
        /// <returns>image、またはエラー。</returns>
        private static async Task<McpToolResult> UiWindowCaptureRegionAsync(VsServiceAccessor InAccessor, JObject InArgs)
        {
            int TheX = InArgs.Value<int>("x");
            int TheY = InArgs.Value<int>("y");
            int TheWidth = InArgs.Value<int>("width");
            int TheHeight = InArgs.Value<int>("height");
            if (TheWidth <= 0 || TheHeight <= 0)
            {
                return McpToolResult.Error("Width and height must be positive values");
            }

            (IntPtr TheWindow, McpToolResult TheError) = await ResolveWindowAsync(InAccessor, InArgs);
            if (TheError != null)
            {
                return TheError;
            }

            return await UiTools.CaptureRegionFromWindowAsync(TheWindow, TheX, TheY, TheWidth, TheHeight);
        }

        /// <summary>
        /// ui_window_get_info の本体。WindowInfo（Phase 1 の列挙結果）、UIA root（AutomationElement.FromHandle）、フォーカス要素（この HWND 配下のときだけ）、
        /// モーダル状態（UiWindowActionValidator）を 1 つの構造で返す。root の NativeWindowHandle と WindowInfo.Handle の一致は判定して両方返す。
        /// </summary>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        /// <param name="InArgs">ツール引数。</param>
        /// <returns>text（window / uiaRoot / uiaRootMatchesHandle / focused / focusedWindowHandle / modalState）、またはエラー。</returns>
        private static async Task<McpToolResult> UiWindowGetInfoAsync(VsServiceAccessor InAccessor, JObject InArgs)
        {
            (IntPtr TheWindow, HashSet<uint> TheProcessIds, McpToolResult TheError) = await ResolveWindowWithProcessesAsync(InAccessor, InArgs);
            if (TheError != null)
            {
                return TheError;
            }

            (string TheStateError, UiWindowModalState TheState, WindowInfo TheWindowInfo) = await Task.Run(() =>
            {
                string TheEvaluateError = UiWindowActionValidator.EvaluateModalState(TheWindow.ToInt64(), TheProcessIds,
                    out UiWindowModalState TheEvaluatedState, out WindowInfo TheEvaluatedWindow);
                return (TheEvaluateError, TheEvaluatedState, TheEvaluatedWindow);
            });
            if (TheStateError != null)
            {
                return McpToolResult.Error(TheStateError);
            }

            try
            {
                return await UiTools.RunUiaWithTimeoutAsync(() =>
                {
                    AutomationElement TheRoot = AutomationElement.FromHandle(TheWindow);
                    Dictionary<string, object> TheRootInfo = UiWindowElementResolver.BuildElementInfo(TheRoot, UiWindowElementResolver.ResolveTopLevelHandle(TheRoot));

                    Dictionary<string, object> TheFocusedInfo = null;
                    long TheFocusedWindowHandle = 0;
                    try
                    {
                        AutomationElement TheFocused = AutomationElement.FocusedElement;
                        if (TheFocused != null)
                        {
                            TheFocusedWindowHandle = UiWindowElementResolver.ResolveTopLevelHandle(TheFocused);
                            if (TheFocusedWindowHandle == TheWindow.ToInt64())
                            {
                                TheFocusedInfo = UiWindowElementResolver.BuildElementInfo(TheFocused, TheFocusedWindowHandle);
                            }
                        }
                    }
                    catch
                    {
                        // フォーカス要素が取れない（フォーカスなし・消えた）場合は null のまま
                    }

                    return McpToolResult.Success(new
                    {
                        window = TheWindowInfo,
                        uiaRoot = TheRootInfo,
                        uiaRootMatchesHandle = Convert.ToInt64(TheRootInfo["nativeWindowHandle"]) == TheWindowInfo.Handle,
                        focused = TheFocusedInfo,
                        focusedWindowHandle = TheFocusedWindowHandle,
                        modalState = TheState,
                    });
                });
            }
            catch (TimeoutException TheException)
            {
                return McpToolResult.Error(TheException.Message);
            }
            catch (Exception TheException)
            {
                return McpToolResult.Error($"Failed to read UI Automation information of window {TheWindow.ToInt64()}: {TheException.Message}");
            }
        }

        /// <summary>
        /// ui_window_find_elements の本体。検索条件の解釈は upstream ui_find_elements と同じ（match モード、hasPattern の検証、maxResults の丸め）で、
        /// 検索 root だけを AutomationElement.FromHandle(検証済み HWND) にする。走査中に見つかった別ウィンドウ所属の要素は結果から除外し件数だけ返す。
        /// タイムアウト時は部分結果を返す。
        /// </summary>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        /// <param name="InArgs">ツール引数。</param>
        /// <returns>text（windowHandle / count / elements / skippedOtherWindows / timedOut / maxResults）、またはエラー。</returns>
        private static async Task<McpToolResult> UiWindowFindElementsAsync(VsServiceAccessor InAccessor, JObject InArgs)
        {
            string TheName = InArgs.Value<string>("name");
            string TheAutomationId = InArgs.Value<string>("automationId");
            string TheClassName = InArgs.Value<string>("className");
            string TheControlTypeName = InArgs.Value<string>("controlType");
            string TheHasPattern = InArgs.Value<string>("hasPattern");
            string TheAncestorAutomationId = InArgs.Value<string>("ancestorAutomationId");
            bool TheIsOffscreenIncluded = InArgs.Value<bool?>("includeOffscreen") ?? false;
            int TheMaxResults = InArgs.Value<int?>("maxResults") ?? 50;
            if (TheMaxResults < 1)
            {
                TheMaxResults = 1;
            }
            if (TheMaxResults > 1000)
            {
                TheMaxResults = 1000;
            }

            if (string.IsNullOrEmpty(TheName) && string.IsNullOrEmpty(TheAutomationId)
                && string.IsNullOrEmpty(TheClassName) && string.IsNullOrEmpty(TheControlTypeName)
                && string.IsNullOrEmpty(TheHasPattern))
            {
                return McpToolResult.Error("At least one search criterion must be provided (name, automationId, className, controlType, or hasPattern)");
            }

            ControlType TheControlType = null;
            if (!string.IsNullOrEmpty(TheControlTypeName))
            {
                TheControlType = UiTools.ParseControlType(TheControlTypeName);
                if (TheControlType == null)
                {
                    return McpToolResult.Error($"Unknown ControlType: '{TheControlTypeName}'");
                }
            }

            UiTools.FindCriteria TheCriteria = new UiTools.FindCriteria
            {
                Name = TheName,
                NameMode = string.IsNullOrEmpty(TheName) ? UiTools.StringMatchMode.Any : UiTools.ParseMatchMode(InArgs.Value<string>("nameMatch"), UiTools.StringMatchMode.Exact),
                AutomationId = TheAutomationId,
                AutomationIdMode = string.IsNullOrEmpty(TheAutomationId) ? UiTools.StringMatchMode.Any : UiTools.ParseMatchMode(InArgs.Value<string>("automationIdMatch"), UiTools.StringMatchMode.Exact),
                ClassName = TheClassName,
                ClassNameMode = string.IsNullOrEmpty(TheClassName) ? UiTools.StringMatchMode.Any : UiTools.ParseMatchMode(InArgs.Value<string>("classNameMatch"), UiTools.StringMatchMode.Exact),
                ControlType = TheControlType,
            };

            try
            {
                if (TheCriteria.NameMode == UiTools.StringMatchMode.Regex)
                {
                    TheCriteria.NameRegex = new Regex(TheName, RegexOptions.IgnoreCase);
                }
                if (TheCriteria.AutomationIdMode == UiTools.StringMatchMode.Regex)
                {
                    TheCriteria.AutomationIdRegex = new Regex(TheAutomationId, RegexOptions.IgnoreCase);
                }
                if (TheCriteria.ClassNameMode == UiTools.StringMatchMode.Regex)
                {
                    TheCriteria.ClassNameRegex = new Regex(TheClassName, RegexOptions.IgnoreCase);
                }
            }
            catch (ArgumentException TheException)
            {
                return McpToolResult.Error($"Invalid regex: {TheException.Message}");
            }

            if (!string.IsNullOrEmpty(TheHasPattern))
            {
                TheCriteria.RequiredPatterns = new HashSet<string>(
                    TheHasPattern.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(ThePattern => ThePattern.Trim().ToLowerInvariant()),
                    StringComparer.OrdinalIgnoreCase);
                foreach (string ThePattern in TheCriteria.RequiredPatterns)
                {
                    if (ThePattern != "invoke" && ThePattern != "toggle" && ThePattern != "select" && ThePattern != "setvalue" && ThePattern != "value" && ThePattern != "expand")
                    {
                        return McpToolResult.Error($"Unknown pattern: '{ThePattern}'. Expected: invoke, toggle, select, setvalue, expand");
                    }
                }
            }

            (IntPtr TheWindow, McpToolResult TheError) = await ResolveWindowAsync(InAccessor, InArgs);
            if (TheError != null)
            {
                return TheError;
            }

            List<Dictionary<string, object>> TheResults = new List<Dictionary<string, object>>();
            object TheResultsLock = new object();
            int TheSkippedOtherWindows = 0;
            long TheRequiredRootHandle = TheWindow.ToInt64();

            using (CancellationTokenSource TheCancellation = new CancellationTokenSource())
            {
                Task<string> TheSearchTask = UiTools.RunOnBackgroundSTAAsync(() =>
                {
                    AutomationElement TheRoot = AutomationElement.FromHandle(TheWindow);
                    List<AutomationElement> TheRoots = new List<AutomationElement>();
                    if (!string.IsNullOrEmpty(TheAncestorAutomationId))
                    {
                        // upstream と同じく、AutomationId が複数要素に付いていることがあるので全部を root にする（root 自身も対象）
                        if (string.Equals(TheRoot.Current.AutomationId ?? string.Empty, TheAncestorAutomationId, StringComparison.Ordinal))
                        {
                            TheRoots.Add(TheRoot);
                        }
                        AutomationElementCollection TheAncestors = TheRoot.FindAll(TreeScope.Descendants,
                            new PropertyCondition(AutomationElement.AutomationIdProperty, TheAncestorAutomationId));
                        foreach (AutomationElement TheAncestor in TheAncestors)
                        {
                            TheRoots.Add(TheAncestor);
                        }
                        if (TheRoots.Count == 0)
                        {
                            return $"ancestorAutomationId '{TheAncestorAutomationId}' was not found in window {TheWindow.ToInt64()}";
                        }
                    }
                    else
                    {
                        TheRoots.Add(TheRoot);
                    }

                    // AutomationElement は STA スレッドの外へ持ち出さない（辞書化した結果だけを共有する）
                    List<AutomationElement> TheMatches = new List<AutomationElement>();
                    int TheSkippedInWalk = 0;
                    foreach (AutomationElement TheSearchRoot in TheRoots)
                    {
                        if (TheCancellation.Token.IsCancellationRequested || TheMatches.Count >= TheMaxResults)
                        {
                            break;
                        }
                        UiWindowElementResolver.CollectMatches(TheSearchRoot, TheCriteria, TheIsOffscreenIncluded, TheMaxResults, TheRequiredRootHandle,
                            TheMatches, ref TheSkippedInWalk, TheCancellation.Token);

                        // 集めた分だけ順次情報化する（タイムアウト時に部分結果を返すため）
                        lock (TheResultsLock)
                        {
                            TheSkippedOtherWindows = TheSkippedInWalk;
                            while (TheResults.Count < TheMatches.Count)
                            {
                                AutomationElement TheMatch = TheMatches[TheResults.Count];
                                try
                                {
                                    TheResults.Add(UiWindowElementResolver.BuildElementInfo(TheMatch, UiWindowElementResolver.ResolveTopLevelHandle(TheMatch)));
                                }
                                catch
                                {
                                    TheResults.Add(new Dictionary<string, object> { ["error"] = "element disappeared while being read" });
                                }
                            }
                        }
                    }
                    return null;
                });

                bool IsTimedOut = false;
                if (await Task.WhenAny(TheSearchTask, Task.Delay(TimeSpan.FromSeconds(UiTools.UiaTimeoutSeconds))) != TheSearchTask)
                {
                    TheCancellation.Cancel();
                    IsTimedOut = true;
                }
                else
                {
                    string TheSearchError;
                    try
                    {
                        TheSearchError = await TheSearchTask;
                    }
                    catch (Exception TheException)
                    {
                        return McpToolResult.Error($"UI Automation search failed in window {TheWindow.ToInt64()}: {TheException.Message}");
                    }
                    if (TheSearchError != null)
                    {
                        return McpToolResult.Error(TheSearchError);
                    }
                }

                List<Dictionary<string, object>> TheSnapshot;
                int TheSkippedSnapshot;
                lock (TheResultsLock)
                {
                    TheSnapshot = new List<Dictionary<string, object>>(TheResults);
                    TheSkippedSnapshot = TheSkippedOtherWindows;
                }

                if (IsTimedOut && TheSnapshot.Count == 0)
                {
                    return McpToolResult.Error(
                        $"UI Automation timed out after {UiTools.UiaTimeoutSeconds} seconds with no results found in window {TheWindow.ToInt64()}. The target window may have a complex UI tree.");
                }

                return McpToolResult.Success(new
                {
                    windowHandle = TheWindow.ToInt64(),
                    count = TheSnapshot.Count,
                    elements = TheSnapshot,
                    skippedOtherWindows = TheSkippedSnapshot,
                    timedOut = IsTimedOut,
                    maxResults = TheMaxResults,
                });
            }
        }

        /// <summary>
        /// windowHandle 引数を Phase 4 の検証（範囲 → IsWindow → GA_ROOT 正規化 → デバッグ対象 PID 再照合）に通す。
        /// 最小化中でも UIA ツリーは取れるので IsIconic は確認しない（スクリーンショットは upstream と同じく失敗し得る）。
        /// </summary>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        /// <param name="InArgs">ツール引数（windowHandle）。</param>
        /// <returns>正規化済み HWND と、失敗時のエラー結果（成功時は null）。</returns>
        internal static async Task<(IntPtr Window, McpToolResult Error)> ResolveWindowAsync(VsServiceAccessor InAccessor, JObject InArgs)
        {
            (IntPtr TheWindow, HashSet<uint> TheProcessIds, McpToolResult TheError) = await ResolveWindowWithProcessesAsync(InAccessor, InArgs);
            return (TheWindow, TheError);
        }

        /// <summary>ResolveWindowAsync と同じ検証を行い、デバッグ中プロセス ID の集合も返す（Action 系ツールが実行直前の再照合に使う）。</summary>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        /// <param name="InArgs">ツール引数（windowHandle）。</param>
        /// <returns>正規化済み HWND、プロセス ID 集合、失敗時のエラー結果（成功時は null）。</returns>
        internal static async Task<(IntPtr Window, HashSet<uint> ProcessIds, McpToolResult Error)> ResolveWindowWithProcessesAsync(VsServiceAccessor InAccessor, JObject InArgs)
        {
            long? TheHandle = InArgs.Value<long?>("windowHandle");
            if (!TheHandle.HasValue)
            {
                return (IntPtr.Zero, null, McpToolResult.Error("Parameter 'windowHandle' is required (use the 'handle' value returned by ui_list_windows)"));
            }
            if (!UiWindowTools.IsHandleInRange(TheHandle.Value))
            {
                return (IntPtr.Zero, null, McpToolResult.Error($"Window handle {TheHandle.Value} is out of range for a window handle."));
            }

            HashSet<uint> TheProcessIds = await UiWindowTools.GetDebuggedProcessIdsAsync(InAccessor);
            string TheHandleError = DebuggeeWindowResolver.ValidateAndNormalizeWindowHandle(TheHandle.Value, TheProcessIds, out IntPtr TheNormalized);
            if (TheHandleError != null)
            {
                return (IntPtr.Zero, TheProcessIds, McpToolResult.Error(TheHandleError));
            }
            return (TheNormalized, TheProcessIds, null);
        }
    }
}
