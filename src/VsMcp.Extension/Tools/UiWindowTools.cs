using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using EnvDTE80;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VsMcp.Extension.McpServer;
using VsMcp.Extension.Services;
using VsMcp.Shared;
using VsMcp.Shared.Protocol;

namespace VsMcp.Extension.Tools
{
    /// <summary>
    /// Extended 独自の「デバッグ対象のトップレベルウィンドウ」を扱う MCP ツール群。
    /// upstream の UiTools（メインウィンドウ固定）とは別ファイルに分離し、差分を最小化する。
    /// 探索・選択・検証の判定は <see cref="DebuggeeWindowResolver"/> に置き、ここでは引数の解釈・DTE アクセス・
    /// ポーリング・戻り値の組み立てだけを行う。MCP ツールハンドラ同士は直接呼ばず、private helper を共有する。
    /// </summary>
    public static class UiWindowTools
    {
        /// <summary>wait 系の既定タイムアウト（既存 ui_wait_for_element と同じ）。</summary>
        private const int DefaultWaitTimeoutMs = 10000;

        /// <summary>wait 系の上限タイムアウト。Router がツール呼び出しを 60 秒で打ち切るため、それより短くする。</summary>
        private const int MaxWaitTimeoutMs = 55000;

        /// <summary>wait 系の既定ポーリング間隔（既存 ui_wait_for_element と同じ）。</summary>
        private const int DefaultPollIntervalMs = 200;

        /// <summary>wait 系の最小ポーリング間隔（既存 ui_wait_for_element と同じ）。</summary>
        private const int MinPollIntervalMs = 50;

        /// <summary>ui_wait_for_window で handle 指定により即時成立したときの resolutionReason。</summary>
        private const string ReasonHandle = "handle";

        /// <summary>ツールをレジストリへ登録する。VsMcpPackage.RegisterTools から呼ばれる。</summary>
        /// <param name="InRegistry">登録先レジストリ。</param>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        public static void Register(McpToolRegistry InRegistry, VsServiceAccessor InAccessor)
        {
            InRegistry.Register(
                new McpToolDefinition(
                    "ui_list_windows",
                    "[Windows UIA — desktop app being debugged] List the top-level windows of every process currently being debugged: " +
                    "the main window plus modal/modeless dialogs (WPF ShowDialog, MessageBox, TaskDialog, file dialogs) and other owned windows. " +
                    "Child windows are never included. Each entry has the HWND (handle, decimal) and handleHex, title, className, processId, " +
                    "isVisible / isEnabled / isMinimized / isForeground, ownerHandle (0 if none), a heuristic isModalCandidate " +
                    "(owner exists and is disabled — a guess, not a guarantee), bounds as 'x,y,width,height' in screen physical pixels " +
                    "(same coordinate space as the bounds returned by ui_find_elements / ui_snapshot), dpi, and the monitor device name. " +
                    "Windows are returned in Z-order (frontmost first). Use the handle to identify a specific window, e.g. a modal dialog, in later calls. " +
                    "For browser pages use web_* tools instead.",
                    SchemaBuilder.Create()
                        .AddBoolean("includeInvisible", "Include hidden top-level windows (IsWindowVisible == false). Default: false")
                        .AddString("titleFilter", "Only return windows whose title matches this value (see titleMatch)")
                        .AddEnum("titleMatch", "Match mode for 'titleFilter': 'exact' (default, case-sensitive), 'contains' (case-insensitive substring), 'regex' (case-insensitive)",
                            new[] { "exact", "contains", "regex" })
                        .Build()),
                InArgs => UiListWindowsAsync(InAccessor, InArgs));

            InRegistry.Register(
                new McpToolDefinition(
                    "ui_capture_window_by_handle",
                    "[Windows UIA — desktop app being debugged] Capture a screenshot of one specific top-level window of the debugged application by its HWND " +
                    "(the 'handle' value returned by ui_list_windows), e.g. a modal dialog, MessageBox or any owned window — not just the main window. " +
                    "A child HWND is normalized to its top-level window via GetAncestor(GA_ROOT). The window must belong to a process currently being debugged " +
                    "(other processes are refused), must still exist, and must not be minimized. Uses the same Windows.Graphics.Capture / PrintWindow pipeline " +
                    "and PNG/JPEG size limits as ui_capture_window. Returns a text content (requestedHandle, normalizedHandle, window info incl. isModalCandidate, " +
                    "originalWidth/originalHeight in physical pixels before any downscaling, mimeType) followed by the image content. " +
                    "For the main window only, ui_capture_window still works; for web pages use web_screenshot.",
                    SchemaBuilder.Create()
                        .AddInteger("handle", "HWND of the window to capture (decimal, as returned by ui_list_windows 'handle')", required: true)
                        .Build()),
                InArgs => UiCaptureWindowByHandleAsync(InAccessor, InArgs));

            InRegistry.Register(
                new McpToolDefinition(
                    "ui_capture_window_by_title",
                    "[Windows UIA — desktop app being debugged] Capture a screenshot of one visible top-level window of the debugged application located by its title " +
                    "(e.g. a modal dialog, MessageBox or any owned window). Searches the visible top-level windows of every process being debugged with the same " +
                    "title matching as ui_list_windows, then resolves ambiguity deterministically: a single match is used; if several windows match, the foreground " +
                    "window wins; otherwise the only visible-and-enabled one wins; if it is still ambiguous nothing is captured and the error text lists the candidates " +
                    "(with their HWNDs) so you can pick one with ui_capture_window_by_handle. The resolved window then passes the same safety checks " +
                    "(IsWindow, GetAncestor(GA_ROOT), debugged-process re-check, not minimized) and the same capture pipeline as ui_capture_window_by_handle. " +
                    "Returns a text content (requestedTitle, titleMatch, resolvedHandle, resolutionReason, normalizedHandle, window info incl. isModalCandidate, " +
                    "originalWidth/originalHeight, mimeType) followed by the image content. For web pages use web_screenshot.",
                    SchemaBuilder.Create()
                        .AddString("title", "Window title to look for (compared with the 'title' value returned by ui_list_windows)", required: true)
                        .AddEnum("titleMatch", "Match mode for 'title': 'exact' (default, case-sensitive), 'contains' (case-insensitive substring), 'regex' (case-insensitive)",
                            new[] { "exact", "contains", "regex" })
                        .Build()),
                InArgs => UiCaptureWindowByTitleAsync(InAccessor, InArgs));

            InRegistry.Register(
                new McpToolDefinition(
                    "ui_get_active_window",
                    "[Windows UIA — desktop app being debugged] Return the top-level window of the debugged application that is effectively active, without capturing it. " +
                    "Resolution order: (1) the system foreground window if it belongs to a debugged process (source 'foreground'); (2) otherwise — e.g. while Visual Studio " +
                    "itself has the foreground — the active window of each debuggee GUI thread from GetGUIThreadInfo, if that yields exactly one window (source 'guiThreadActive'); " +
                    "(3) otherwise the only visible-and-enabled window that is a modal candidate ('modalCandidate'), else the only visible-and-enabled ownerless window " +
                    "('visibleEnabledOwnerless'), else the only visible-and-enabled window ('visibleEnabled'). If it is still ambiguous nothing is chosen and the error " +
                    "text lists the candidates with their HWNDs. Returns text: source, foregroundHandle (system foreground HWND, for diagnostics) and window " +
                    "(same fields as ui_list_windows). No arguments. Use ui_capture_active_window to get the screenshot in one call.",
                    SchemaBuilder.Create().Build()),
                InArgs => UiGetActiveWindowAsync(InAccessor, InArgs));

            InRegistry.Register(
                new McpToolDefinition(
                    "ui_capture_active_window",
                    "[Windows UIA — desktop app being debugged] Capture a screenshot of the effectively active top-level window of the debugged application. " +
                    "The window is resolved exactly like ui_get_active_window (foreground → GUI-thread active window → unique visible/enabled fallback; ambiguous " +
                    "cases return an error listing the candidates) and then captured through the same safety checks and Windows.Graphics.Capture / PrintWindow pipeline " +
                    "as ui_capture_window_by_handle, so a debuggee dialog can be captured even while Visual Studio has the foreground. Returns a text content " +
                    "(source, resolvedHandle, normalizedHandle, window, originalWidth, originalHeight, mimeType) followed by the image content. No arguments.",
                    SchemaBuilder.Create().Build()),
                InArgs => UiCaptureActiveWindowAsync(InAccessor, InArgs));

            InRegistry.Register(
                new McpToolDefinition(
                    "ui_wait_for_window",
                    "[Windows UIA — desktop app being debugged] Wait until a top-level window of the debugged application appears. Give either 'handle' or 'title'. " +
                    "With 'title' (+ optional titleMatch / className) the visible top-level windows are polled with the same matching and the same ambiguity rules as " +
                    "ui_capture_window_by_title (single match, else the foreground one, else the only visible-and-enabled one; still ambiguous → error listing the candidates). " +
                    "With 'handle' the call only checks that this HWND exists now and belongs to a debugged process and returns immediately — an HWND cannot be known " +
                    "before the window exists and may be reused by the OS, so an invalid handle is an error rather than something to wait for; 'title' is ignored then. " +
                    "Returns text { found, elapsedMs, resolutionReason, window }; found=false with window=null on timeout. timeoutMs default 10000, max 55000 " +
                    "(the server aborts tool calls at 60 s); pollIntervalMs default 200, min 50. Use this instead of sleeping after triggering a dialog.",
                    SchemaBuilder.Create()
                        .AddInteger("handle", "HWND to check (decimal). When given, title/titleMatch/className are ignored")
                        .AddString("title", "Window title to wait for (compared with the 'title' value returned by ui_list_windows)")
                        .AddEnum("titleMatch", "Match mode for 'title': 'exact' (default, case-sensitive), 'contains' (case-insensitive substring), 'regex' (case-insensitive)",
                            new[] { "exact", "contains", "regex" })
                        .AddString("className", "Optional additional filter: window class name (case-insensitive exact match, e.g. '#32770' for Win32 dialogs)")
                        .AddInteger("timeoutMs", "Maximum time to wait in milliseconds (default: 10000, max: 55000)")
                        .AddInteger("pollIntervalMs", "Polling interval in milliseconds (default: 200, min: 50)")
                        .Build()),
                InArgs => UiWaitForWindowAsync(InAccessor, InArgs));

            InRegistry.Register(
                new McpToolDefinition(
                    "ui_wait_for_window_closed",
                    "[Windows UIA — desktop app being debugged] Wait until a top-level window of the debugged application is closed. Prefer 'handle' (the value from " +
                    "ui_list_windows / ui_wait_for_window / ui_get_active_window): it is exact — the window counts as closed when the HWND no longer exists or has been " +
                    "reused by a different process. 'title' (+ optional titleMatch / className) is also accepted and reports closed when no visible window matches any more, " +
                    "but it is less strict because a window with the same title may be re-created; 'title' is ignored when 'handle' is given. " +
                    "Returns text { closed, elapsedMs, handle, initialTitle, reason }; reason is 'windowDestroyed', 'handleReused', 'alreadyClosed', 'noMatchingWindow' or " +
                    "'debuggingStopped', and closed=false with reason=null on timeout. timeoutMs default 10000, max 55000; pollIntervalMs default 200, min 50.",
                    SchemaBuilder.Create()
                        .AddInteger("handle", "HWND of the window to wait for (decimal). Recommended. When given, title/titleMatch/className are ignored")
                        .AddString("title", "Window title to wait for (less strict than handle; see description)")
                        .AddEnum("titleMatch", "Match mode for 'title': 'exact' (default, case-sensitive), 'contains' (case-insensitive substring), 'regex' (case-insensitive)",
                            new[] { "exact", "contains", "regex" })
                        .AddString("className", "Optional additional filter: window class name (case-insensitive exact match)")
                        .AddInteger("timeoutMs", "Maximum time to wait in milliseconds (default: 10000, max: 55000)")
                        .AddInteger("pollIntervalMs", "Polling interval in milliseconds (default: 200, min: 50)")
                        .Build()),
                InArgs => UiWaitForWindowClosedAsync(InAccessor, InArgs));
        }

        // ------------------------------------------------------------------
        // ツールハンドラ
        // ------------------------------------------------------------------

        /// <summary>
        /// ui_list_windows の本体。UI スレッドでデバッグ中 PID を集め、バックグラウンドで EnumWindows を実行して JSON を返す。
        /// </summary>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        /// <param name="InArgs">ツール引数。</param>
        /// <returns>ウィンドウ一覧、またはエラー。</returns>
        private static async Task<McpToolResult> UiListWindowsAsync(VsServiceAccessor InAccessor, JObject InArgs)
        {
            bool TheIsInvisibleIncluded = InArgs.Value<bool?>("includeInvisible") ?? false;
            string TheTitleFilter = InArgs.Value<string>("titleFilter");

            WindowTitleQuery TheQuery = null;
            if (!string.IsNullOrEmpty(TheTitleFilter))
            {
                string TheQueryError = DebuggeeWindowResolver.TryParseTitleQuery(TheTitleFilter, InArgs.Value<string>("titleMatch"), null, out TheQuery);
                if (TheQueryError != null)
                    return McpToolResult.Error(TheQueryError);
            }

            HashSet<uint> TheProcessIds = await GetDebuggedProcessIdsAsync(InAccessor);
            if (TheProcessIds.Count == 0)
                return McpToolResult.Error(DebuggeeWindowResolver.NoDebuggedProcessMessage);

            // Win32 列挙は UI スレッドを塞がないようバックグラウンドで実行する
            List<WindowInfo> TheWindows = await Task.Run(
                () => DebuggeeWindowEnumerator.EnumerateTopLevelWindows(TheProcessIds, TheIsInvisibleIncluded));

            if (TheQuery != null)
                TheWindows = DebuggeeWindowResolver.Filter(TheWindows, TheQuery);

            return McpToolResult.Success(new
            {
                count = TheWindows.Count,
                processIds = TheProcessIds.OrderBy(TheProcessId => TheProcessId).ToList(),
                windows = TheWindows,
            });
        }

        /// <summary>
        /// ui_capture_window_by_handle の本体。引数の HWND を範囲検証したうえで、共通キャプチャ経路
        /// <see cref="CaptureTopLevelWindowAsync"/> へ渡す。
        /// </summary>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        /// <param name="InArgs">ツール引数（handle）。</param>
        /// <returns>text（ウィンドウ情報と画像メタ情報）+ image、またはエラー。</returns>
        private static async Task<McpToolResult> UiCaptureWindowByHandleAsync(VsServiceAccessor InAccessor, JObject InArgs)
        {
            long? TheRequestedHandle = InArgs.Value<long?>("handle");
            if (!TheRequestedHandle.HasValue)
                return McpToolResult.Error("Parameter 'handle' is required (use the 'handle' value returned by ui_list_windows)");
            if (!IsHandleInRange(TheRequestedHandle.Value))
                return McpToolResult.Error($"Window handle {TheRequestedHandle.Value} is out of range for a window handle.");

            JObject TheResolution = new JObject
            {
                ["requestedHandle"] = TheRequestedHandle.Value,
            };
            return await CaptureTopLevelWindowAsync(InAccessor, TheRequestedHandle.Value, TheResolution);
        }

        /// <summary>
        /// ui_capture_window_by_title の本体。デバッグ対象の可視トップレベルウィンドウをタイトルで検索し、
        /// 候補を「単一一致 → フォアグラウンド → 可視かつ有効」の順で一意に解決できた場合だけ共通キャプチャ経路へ渡す。
        /// 一意に決められない場合は候補一覧付きのエラーを返し、ui_capture_window_by_handle での明示指定へ誘導する。
        /// </summary>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        /// <param name="InArgs">ツール引数（title, titleMatch）。</param>
        /// <returns>text（タイトル解決情報・ウィンドウ情報・画像メタ情報）+ image、またはエラー。</returns>
        private static async Task<McpToolResult> UiCaptureWindowByTitleAsync(VsServiceAccessor InAccessor, JObject InArgs)
        {
            string TheTitle = InArgs.Value<string>("title");
            if (string.IsNullOrEmpty(TheTitle))
                return McpToolResult.Error("Parameter 'title' is required (the window title as returned by ui_list_windows)");

            string TheQueryError = DebuggeeWindowResolver.TryParseTitleQuery(TheTitle, InArgs.Value<string>("titleMatch"), null, out WindowTitleQuery TheQuery);
            if (TheQueryError != null)
                return McpToolResult.Error(TheQueryError);

            HashSet<uint> TheProcessIds = await GetDebuggedProcessIdsAsync(InAccessor);
            if (TheProcessIds.Count == 0)
                return McpToolResult.Error(DebuggeeWindowResolver.NoDebuggedProcessMessage);

            CandidateResolution TheResolution = await FindWindowsByQueryAsync(TheProcessIds, TheQuery);
            if (TheResolution.Candidates.Count == 0)
                return McpToolResult.Error($"No debugged window matched {TheQuery.Describe()}.");
            if (!TheResolution.IsResolved)
            {
                return McpToolResult.Error(DebuggeeWindowResolver.DescribeAmbiguity(
                    TheResolution, TheQuery.Describe(), "Pick one and call ui_capture_window_by_handle with its 'handle'."));
            }

            JObject TheText = new JObject
            {
                ["requestedTitle"] = TheTitle,
                ["titleMatch"] = TheQuery.TitleMatch,
                ["resolvedHandle"] = TheResolution.Resolved.Handle,
                ["resolutionReason"] = TheResolution.Reason,
            };
            // 検索からキャプチャまでの間にウィンドウが閉じられる競合に備え、共通経路で IsWindow / PID 再照合をやり直す
            return await CaptureTopLevelWindowAsync(InAccessor, TheResolution.Resolved.Handle, TheText);
        }

        /// <summary>ui_get_active_window の本体。アクティブウィンドウを解決して WindowInfo を返す（キャプチャはしない）。</summary>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        /// <param name="InArgs">ツール引数（なし）。</param>
        /// <returns>text（source / foregroundHandle / window）、またはエラー。</returns>
        private static async Task<McpToolResult> UiGetActiveWindowAsync(VsServiceAccessor InAccessor, JObject InArgs)
        {
            (ActiveWindowResolution TheResolution, McpToolResult TheError) = await ResolveActiveDebuggeeWindowAsync(InAccessor);
            if (TheError != null)
                return TheError;

            return McpToolResult.Success(new
            {
                source = TheResolution.Source,
                foregroundHandle = TheResolution.ForegroundHandle,
                window = TheResolution.Resolved,
            });
        }

        /// <summary>ui_capture_active_window の本体。アクティブウィンドウを解決し、共通キャプチャ経路へ渡す。</summary>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        /// <param name="InArgs">ツール引数（なし）。</param>
        /// <returns>text（source / resolvedHandle / ウィンドウ情報 / 画像メタ情報）+ image、またはエラー。</returns>
        private static async Task<McpToolResult> UiCaptureActiveWindowAsync(VsServiceAccessor InAccessor, JObject InArgs)
        {
            (ActiveWindowResolution TheResolution, McpToolResult TheError) = await ResolveActiveDebuggeeWindowAsync(InAccessor);
            if (TheError != null)
                return TheError;

            JObject TheText = new JObject
            {
                ["source"] = TheResolution.Source,
                ["resolvedHandle"] = TheResolution.Resolved.Handle,
            };
            return await CaptureTopLevelWindowAsync(InAccessor, TheResolution.Resolved.Handle, TheText);
        }

        /// <summary>
        /// ui_wait_for_window の本体。handle 指定なら現在の存在とデバッグ対象所属を検証して即時返し、
        /// title 指定なら一致ウィンドウが出現するまでポーリングする（複数一致は Phase 3 と同じ候補解決）。
        /// </summary>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        /// <param name="InArgs">ツール引数（handle / title / titleMatch / className / timeoutMs / pollIntervalMs）。</param>
        /// <returns>text（found / elapsedMs / resolutionReason / window）、またはエラー。</returns>
        private static async Task<McpToolResult> UiWaitForWindowAsync(VsServiceAccessor InAccessor, JObject InArgs)
        {
            string TheSelectorError = TryParseWaitSelector(InArgs, out long? TheHandle, out WindowTitleQuery TheQuery);
            if (TheSelectorError != null)
                return McpToolResult.Error(TheSelectorError);
            ParseWaitOptions(InArgs, out int TheTimeoutMs, out int ThePollIntervalMs);

            Stopwatch TheStopwatch = Stopwatch.StartNew();
            HashSet<uint> TheProcessIds = await GetDebuggedProcessIdsAsync(InAccessor);
            if (TheProcessIds.Count == 0)
                return McpToolResult.Error(DebuggeeWindowResolver.NoDebuggedProcessMessage);

            if (TheHandle.HasValue)
            {
                // HWND は事前に予測できず OS に再利用されるため「無効な handle が有効になるまで待つ」ことはせず、現在の状態だけを検証する
                string TheHandleError = DebuggeeWindowResolver.ValidateAndNormalizeWindowHandle(TheHandle.Value, TheProcessIds, out IntPtr TheNormalized);
                if (TheHandleError != null)
                    return McpToolResult.Error(TheHandleError);

                WindowInfo TheWindow = await FindWindowInfoAsync(TheProcessIds, TheNormalized.ToInt64());
                if (TheWindow == null)
                    return McpToolResult.Error($"Window handle {TheNormalized.ToInt64()} disappeared before it could be inspected.");

                return McpToolResult.Success(new
                {
                    found = true,
                    elapsedMs = TheStopwatch.ElapsedMilliseconds,
                    resolutionReason = ReasonHandle,
                    window = TheWindow,
                });
            }

            while (true)
            {
                CandidateResolution TheResolution = await FindWindowsByQueryAsync(TheProcessIds, TheQuery);
                if (TheResolution.Candidates.Count > 0)
                {
                    if (!TheResolution.IsResolved)
                    {
                        return McpToolResult.Error(DebuggeeWindowResolver.DescribeAmbiguity(
                            TheResolution, TheQuery.Describe(),
                            "Pick one and call ui_wait_for_window with its 'handle', or ui_capture_window_by_handle to capture it."));
                    }

                    return McpToolResult.Success(new
                    {
                        found = true,
                        elapsedMs = TheStopwatch.ElapsedMilliseconds,
                        resolutionReason = TheResolution.Reason,
                        window = TheResolution.Resolved,
                    });
                }

                if (TheStopwatch.ElapsedMilliseconds >= TheTimeoutMs)
                    break;
                await Task.Delay(ThePollIntervalMs);

                // デバッグ終了で対象プロセスが消えた場合は無限に待たず打ち切る
                TheProcessIds = await GetDebuggedProcessIdsAsync(InAccessor);
                if (TheProcessIds.Count == 0)
                    return McpToolResult.Error("Debugging stopped while waiting for the window.");
            }

            return McpToolResult.Success(new
            {
                found = false,
                elapsedMs = TheStopwatch.ElapsedMilliseconds,
                resolutionReason = (string)null,
                window = (WindowInfo)null,
            });
        }

        /// <summary>
        /// ui_wait_for_window_closed の本体。handle 指定なら IsWindow の失敗または PID の変化（HWND 再利用）を closed とみなし、
        /// title 指定なら一致する可視ウィンドウが 0 件になった時点で closed とみなす。
        /// </summary>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        /// <param name="InArgs">ツール引数（handle / title / titleMatch / className / timeoutMs / pollIntervalMs）。</param>
        /// <returns>text（closed / elapsedMs / handle / initialTitle / reason）、またはエラー。</returns>
        private static async Task<McpToolResult> UiWaitForWindowClosedAsync(VsServiceAccessor InAccessor, JObject InArgs)
        {
            string TheSelectorError = TryParseWaitSelector(InArgs, out long? TheHandle, out WindowTitleQuery TheQuery);
            if (TheSelectorError != null)
                return McpToolResult.Error(TheSelectorError);
            ParseWaitOptions(InArgs, out int TheTimeoutMs, out int ThePollIntervalMs);

            Stopwatch TheStopwatch = Stopwatch.StartNew();

            if (TheHandle.HasValue)
            {
                // 既に存在しない HWND は待つまでもなく closed
                if (!NativeMethods.IsWindow(new IntPtr(TheHandle.Value)))
                    return BuildClosedResult(true, TheStopwatch, TheHandle.Value, null, "alreadyClosed");

                HashSet<uint> TheProcessIds = await GetDebuggedProcessIdsAsync(InAccessor);
                string TheHandleError = DebuggeeWindowResolver.ValidateAndNormalizeWindowHandle(TheHandle.Value, TheProcessIds, out IntPtr TheNormalized);
                if (TheHandleError != null)
                    return McpToolResult.Error(TheHandleError);

                // 開始時点の状態を記録し、HWND 再利用（同じ値が別プロセスのウィンドウになる）を closed と区別できるようにする
                long TheNormalizedHandle = TheNormalized.ToInt64();
                NativeMethods.GetWindowThreadProcessId(TheNormalized, out uint TheInitialProcessId);
                WindowInfo TheInitialWindow = await FindWindowInfoAsync(TheProcessIds, TheNormalizedHandle);
                string TheInitialTitle = TheInitialWindow?.Title;

                while (true)
                {
                    if (!NativeMethods.IsWindow(TheNormalized))
                        return BuildClosedResult(true, TheStopwatch, TheNormalizedHandle, TheInitialTitle, "windowDestroyed");

                    NativeMethods.GetWindowThreadProcessId(TheNormalized, out uint TheCurrentProcessId);
                    if (TheCurrentProcessId != TheInitialProcessId)
                        return BuildClosedResult(true, TheStopwatch, TheNormalizedHandle, TheInitialTitle, "handleReused");

                    if (TheStopwatch.ElapsedMilliseconds >= TheTimeoutMs)
                        return BuildClosedResult(false, TheStopwatch, TheNormalizedHandle, TheInitialTitle, null);
                    await Task.Delay(ThePollIntervalMs);
                }
            }

            HashSet<uint> TheTitleProcessIds = await GetDebuggedProcessIdsAsync(InAccessor);
            if (TheTitleProcessIds.Count == 0)
                return McpToolResult.Error(DebuggeeWindowResolver.NoDebuggedProcessMessage);

            while (true)
            {
                CandidateResolution TheResolution = await FindWindowsByQueryAsync(TheTitleProcessIds, TheQuery);
                if (TheResolution.Candidates.Count == 0)
                    return BuildClosedResult(true, TheStopwatch, null, TheQuery.Title, "noMatchingWindow");

                if (TheStopwatch.ElapsedMilliseconds >= TheTimeoutMs)
                    return BuildClosedResult(false, TheStopwatch, null, TheQuery.Title, null);
                await Task.Delay(ThePollIntervalMs);

                // デバッグ終了で対象プロセスが消えた場合、そのウィンドウも消えているので closed として返す
                TheTitleProcessIds = await GetDebuggedProcessIdsAsync(InAccessor);
                if (TheTitleProcessIds.Count == 0)
                    return BuildClosedResult(true, TheStopwatch, null, TheQuery.Title, "debuggingStopped");
            }
        }

        // ------------------------------------------------------------------
        // 共通 helper（MCP ツールハンドラ同士は直接呼ばず、ここを共有する）
        // ------------------------------------------------------------------

        /// <summary>デバッグ中の全プロセス ID を取得する。DTE は UI スレッドでのみ触る（既存 UiTools.GetDebuggeeProcessId と同じ流儀）。</summary>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        /// <returns>デバッグ中プロセス ID の集合。デバッグ中でなければ空。</returns>
        private static Task<HashSet<uint>> GetDebuggedProcessIdsAsync(VsServiceAccessor InAccessor)
        {
            return InAccessor.RunOnUIThreadAsync(() =>
            {
                DTE2 TheDte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => InAccessor.GetDteAsync());
                return DebuggeeWindowEnumerator.GetDebuggedProcessIds(TheDte);
            });
        }

        /// <summary>可視トップレベルウィンドウをバックグラウンドで列挙し、検索条件で絞って候補解決まで行う。</summary>
        /// <param name="InProcessIds">デバッグ中プロセス ID の集合。</param>
        /// <param name="InQuery">検索条件。</param>
        /// <returns>候補と解決結果。</returns>
        private static Task<CandidateResolution> FindWindowsByQueryAsync(HashSet<uint> InProcessIds, WindowTitleQuery InQuery)
        {
            return Task.Run(() =>
            {
                List<WindowInfo> TheCandidates = DebuggeeWindowResolver.Filter(
                    DebuggeeWindowEnumerator.EnumerateTopLevelWindows(InProcessIds, false), InQuery);
                return DebuggeeWindowResolver.ResolveCandidates(TheCandidates);
            });
        }

        /// <summary>
        /// 指定 HWND の WindowInfo を列挙結果から取得する。非表示も含めて同一プロセスの全ウィンドウを見渡すことで、
        /// Phase 1 のモーダル候補判定をそのまま再利用する（判定ロジックの複製をしない）。
        /// </summary>
        /// <param name="InProcessIds">デバッグ中プロセス ID の集合。</param>
        /// <param name="InHandle">対象のトップレベル HWND。</param>
        /// <returns>WindowInfo。列挙に無ければ null。</returns>
        private static Task<WindowInfo> FindWindowInfoAsync(HashSet<uint> InProcessIds, long InHandle)
        {
            return Task.Run(() =>
                DebuggeeWindowEnumerator.EnumerateTopLevelWindows(InProcessIds, true)
                    .FirstOrDefault(TheCandidate => TheCandidate.Handle == InHandle));
        }

        /// <summary>アクティブウィンドウを解決する共通処理。ui_get_active_window / ui_capture_active_window が共有する。</summary>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        /// <returns>解決結果と、失敗時のエラー結果（成功時は null）。</returns>
        private static async Task<(ActiveWindowResolution Resolution, McpToolResult Error)> ResolveActiveDebuggeeWindowAsync(VsServiceAccessor InAccessor)
        {
            HashSet<uint> TheProcessIds = await GetDebuggedProcessIdsAsync(InAccessor);
            if (TheProcessIds.Count == 0)
                return (null, McpToolResult.Error(DebuggeeWindowResolver.NoDebuggedProcessMessage));

            ActiveWindowResolution TheResolution = await Task.Run(() =>
                DebuggeeWindowResolver.ResolveActiveWindow(
                    TheProcessIds, DebuggeeWindowEnumerator.EnumerateTopLevelWindows(TheProcessIds, false)));
            if (TheResolution.IsResolved)
                return (TheResolution, null);

            if (TheResolution.Candidates.Count == 0)
                return (null, McpToolResult.Error("No visible top-level window of the debugged application was found."));

            string TheMessage =
                $"{TheResolution.Candidates.Count} visible windows of the debugged application remain after every resolution step " +
                $"(system foreground window {TheResolution.ForegroundHandle} is not a debuggee window; GUI-thread active windows: " +
                $"[{string.Join(", ", TheResolution.GuiThreadActiveHandles)}]). " +
                "Pick one and call ui_capture_window_by_handle with its 'handle'. Candidates:\n" +
                JsonConvert.SerializeObject(TheResolution.Candidates, Formatting.Indented);
            return (null, McpToolResult.Error(TheMessage));
        }

        /// <summary>
        /// by_handle / by_title / active 共通のキャプチャ経路。検証・正規化（<see cref="DebuggeeWindowResolver.ValidateAndNormalizeWindowHandle"/>）→
        /// IsIconic → WindowInfo 取得 → 既存 UiTools キャプチャ（WGC → PrintWindow → PNG/JPEG）の順に処理し、text + image を返す。
        /// キャプチャ本体は再実装しない。
        /// </summary>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        /// <param name="InHandle">キャプチャ対象の HWND（範囲検証済み。子 HWND でもよい）。</param>
        /// <param name="InResolution">text の先頭に置く解決情報（requestedHandle、requestedTitle 系、source 系）。</param>
        /// <returns>text（解決情報 + normalizedHandle + window + 画像メタ情報）+ image、またはエラー。</returns>
        private static async Task<McpToolResult> CaptureTopLevelWindowAsync(VsServiceAccessor InAccessor, long InHandle, JObject InResolution)
        {
            HashSet<uint> TheProcessIds = await GetDebuggedProcessIdsAsync(InAccessor);
            string TheHandleError = DebuggeeWindowResolver.ValidateAndNormalizeWindowHandle(InHandle, TheProcessIds, out IntPtr TheNormalized);
            if (TheHandleError != null)
                return McpToolResult.Error(TheHandleError);
            long TheNormalizedHandle = TheNormalized.ToInt64();

            // 最小化中は WGC が空フレーム待ちになり PrintWindow でも空白になるため、キャプチャせずに明示エラーにする
            if (NativeMethods.IsIconic(TheNormalized))
                return McpToolResult.Error($"Window handle {TheNormalizedHandle} is minimized. Restore the window before capturing it.");

            WindowInfo TheWindow = await FindWindowInfoAsync(TheProcessIds, TheNormalizedHandle);
            if (TheWindow == null)
                return McpToolResult.Error($"Window handle {TheNormalizedHandle} disappeared before it could be captured.");

            // 既存の upstream キャプチャ経路をそのまま使う（WGC → PrintWindow フォールバック → PNG/JPEG・縮小）
            System.Drawing.Bitmap TheBitmap = await UiTools.CaptureWindowBitmapAsync(TheNormalized);
            if (TheBitmap == null)
                return McpToolResult.Error($"Failed to capture window handle {TheNormalizedHandle}.");

            using (TheBitmap)
            {
                int TheOriginalWidth = TheBitmap.Width;
                int TheOriginalHeight = TheBitmap.Height;
                (string TheBase64, string TheMimeType) = UiTools.BitmapToBase64WithMime(TheBitmap);

                JObject TheText = new JObject(InResolution)
                {
                    ["normalizedHandle"] = TheNormalizedHandle,
                    ["window"] = JObject.FromObject(TheWindow),
                    ["originalWidth"] = TheOriginalWidth,
                    ["originalHeight"] = TheOriginalHeight,
                    ["mimeType"] = TheMimeType,
                };

                McpToolResult TheResult = McpToolResult.Success(TheText.ToString(Formatting.Indented));
                TheResult.Content.Add(new McpContent { Type = "image", Data = TheBase64, MimeType = TheMimeType });
                return TheResult;
            }
        }

        /// <summary>HWND が 0 より大きく、実行中プロセスの IntPtr に収まる値か判定する。</summary>
        /// <param name="InHandle">検証する値。</param>
        /// <returns>範囲内なら true。</returns>
        private static bool IsHandleInRange(long InHandle)
        {
            if (InHandle <= 0)
                return false;
            if (IntPtr.Size == 4 && InHandle > int.MaxValue)
                return false;
            return InHandle <= uint.MaxValue;
        }

        /// <summary>
        /// wait 系ツールの対象指定（handle または title 系）を解釈する。handle 指定時は title / titleMatch / className を無視する。
        /// </summary>
        /// <param name="InArgs">ツール引数。</param>
        /// <param name="OutHandle">handle が指定されていればその値。未指定なら null。</param>
        /// <param name="OutQuery">title 指定時の検索条件。handle 指定時やエラー時は null。</param>
        /// <returns>エラーメッセージ。正常なら null。</returns>
        private static string TryParseWaitSelector(JObject InArgs, out long? OutHandle, out WindowTitleQuery OutQuery)
        {
            OutHandle = InArgs.Value<long?>("handle");
            OutQuery = null;
            if (OutHandle.HasValue)
            {
                return IsHandleInRange(OutHandle.Value)
                    ? null
                    : $"Window handle {OutHandle.Value} is out of range for a window handle.";
            }

            string TheTitle = InArgs.Value<string>("title");
            if (string.IsNullOrEmpty(TheTitle))
                return "Either 'handle' or 'title' must be provided.";

            return DebuggeeWindowResolver.TryParseTitleQuery(TheTitle, InArgs.Value<string>("titleMatch"), InArgs.Value<string>("className"), out OutQuery);
        }

        /// <summary>wait 系ツールの timeoutMs / pollIntervalMs を既定値・上下限で正規化する（既存 ui_wait_for_element と同様に黙って丸める）。</summary>
        /// <param name="InArgs">ツール引数。</param>
        /// <param name="OutTimeoutMs">正規化後のタイムアウト。</param>
        /// <param name="OutPollIntervalMs">正規化後のポーリング間隔。</param>
        private static void ParseWaitOptions(JObject InArgs, out int OutTimeoutMs, out int OutPollIntervalMs)
        {
            int TheTimeoutMs = InArgs.Value<int?>("timeoutMs") ?? DefaultWaitTimeoutMs;
            if (TheTimeoutMs < 0)
                TheTimeoutMs = 0;
            if (TheTimeoutMs > MaxWaitTimeoutMs)
                TheTimeoutMs = MaxWaitTimeoutMs;

            int ThePollIntervalMs = InArgs.Value<int?>("pollIntervalMs") ?? DefaultPollIntervalMs;
            if (ThePollIntervalMs < MinPollIntervalMs)
                ThePollIntervalMs = MinPollIntervalMs;

            OutTimeoutMs = TheTimeoutMs;
            OutPollIntervalMs = ThePollIntervalMs;
        }

        /// <summary>ui_wait_for_window_closed の戻り値を組み立てる。</summary>
        /// <param name="InIsClosed">閉じたと判定したか。</param>
        /// <param name="InStopwatch">経過時間の計測。</param>
        /// <param name="InHandle">監視した HWND（title 指定時は null）。</param>
        /// <param name="InInitialTitle">開始時点のタイトル（title 指定時は検索タイトル）。</param>
        /// <param name="InReason">判定根拠。タイムアウト時は null。</param>
        /// <returns>成功結果。</returns>
        private static McpToolResult BuildClosedResult(bool InIsClosed, Stopwatch InStopwatch, long? InHandle, string InInitialTitle, string InReason)
        {
            return McpToolResult.Success(new
            {
                closed = InIsClosed,
                elapsedMs = InStopwatch.ElapsedMilliseconds,
                handle = InHandle,
                initialTitle = InInitialTitle,
                reason = InReason,
            });
        }
    }
}
