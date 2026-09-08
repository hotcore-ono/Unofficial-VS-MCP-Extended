using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
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
    /// </summary>
    public static class UiWindowTools
    {
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

            // HWND は 0 より大きく、実行中プロセスの IntPtr に収まる値でなければならない
            if (TheRequestedHandle.Value <= 0
                || (IntPtr.Size == 4 && TheRequestedHandle.Value > int.MaxValue)
                || TheRequestedHandle.Value > uint.MaxValue)
            {
                return McpToolResult.Error($"Window handle {TheRequestedHandle.Value} is out of range for a window handle.");
            }

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

            string TheTitleMatch = (InArgs.Value<string>("titleMatch") ?? "exact").ToLowerInvariant();
            if (TheTitleMatch != "exact" && TheTitleMatch != "contains" && TheTitleMatch != "regex")
                return McpToolResult.Error($"Unknown titleMatch: '{TheTitleMatch}'. Expected one of: exact, contains, regex");

            Regex TheTitleRegex = null;
            if (TheTitleMatch == "regex")
            {
                try
                {
                    TheTitleRegex = new Regex(TheTitle, RegexOptions.IgnoreCase);
                }
                catch (ArgumentException TheException)
                {
                    return McpToolResult.Error($"Invalid regex: {TheException.Message}");
                }
            }

            HashSet<uint> TheProcessIds = await GetDebuggedProcessIdsAsync(InAccessor);
            if (TheProcessIds.Count == 0)
                return McpToolResult.Error("No debugged process found. Make sure debugging is active.");

            // キャプチャ対象は画面表示中のウィンドウなので、非表示ウィンドウは検索対象に含めない
            List<WindowInfo> TheCandidates = await Task.Run(() =>
                DebuggeeWindowEnumerator.EnumerateTopLevelWindows(TheProcessIds, false)
                    .Where(TheWindow => IsTitleMatched(TheWindow.Title, TheTitle, TheTitleMatch, TheTitleRegex))
                    .ToList());

            string TheMatchDescription = TheTitleMatch == "exact"
                ? $"title \"{TheTitle}\""
                : $"title \"{TheTitle}\" (titleMatch: {TheTitleMatch})";
            if (TheCandidates.Count == 0)
                return McpToolResult.Error($"No debugged window matched {TheMatchDescription}.");

            WindowInfo TheResolved;
            string TheResolutionReason;
            if (TheCandidates.Count == 1)
            {
                TheResolved = TheCandidates[0];
                TheResolutionReason = "singleMatch";
            }
            else
            {
                // 複数一致: フォアグラウンド → 可視かつ有効 の順で 1 件に絞れた場合だけ採用し、それ以外は自動選択しない
                List<WindowInfo> TheForegroundCandidates = TheCandidates.Where(TheWindow => TheWindow.IsForeground).ToList();
                List<WindowInfo> TheEnabledVisibleCandidates = TheCandidates.Where(TheWindow => TheWindow.IsVisible && TheWindow.IsEnabled).ToList();
                if (TheForegroundCandidates.Count == 1)
                {
                    TheResolved = TheForegroundCandidates[0];
                    TheResolutionReason = "foregroundMatch";
                }
                else if (TheEnabledVisibleCandidates.Count == 1)
                {
                    TheResolved = TheEnabledVisibleCandidates[0];
                    TheResolutionReason = "enabledVisibleMatch";
                }
                else
                {
                    return McpToolResult.Error(
                        $"{TheCandidates.Count} debugged windows matched {TheMatchDescription} and none could be selected unambiguously " +
                        $"(foreground: {TheForegroundCandidates.Count}, visible and enabled: {TheEnabledVisibleCandidates.Count}). " +
                        "Pick one and call ui_capture_window_by_handle with its 'handle'. Candidates:\n" +
                        JsonConvert.SerializeObject(TheCandidates, Formatting.Indented));
                }
            }

            JObject TheResolution = new JObject
            {
                ["requestedTitle"] = TheTitle,
                ["titleMatch"] = TheTitleMatch,
                ["resolvedHandle"] = TheResolved.Handle,
                ["resolutionReason"] = TheResolutionReason,
            };
            // 検索からキャプチャまでの間にウィンドウが閉じられる競合に備え、共通経路で IsWindow / PID 再照合をやり直す
            return await CaptureTopLevelWindowAsync(InAccessor, TheResolved.Handle, TheResolution);
        }

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

        /// <summary>
        /// by_handle / by_title 共通のキャプチャ経路。IsWindow → GetAncestor(GA_ROOT) 正規化 → デバッグ対象 PID 再照合 → IsIconic →
        /// WindowInfo 取得 → 既存 UiTools キャプチャ（WGC → PrintWindow → PNG/JPEG）の順に処理し、text + image を返す。
        /// キャプチャ本体は再実装しない。
        /// </summary>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        /// <param name="InHandle">キャプチャ対象の HWND（範囲検証済み。子 HWND でもよい）。</param>
        /// <param name="InResolution">text の先頭に置く解決情報（requestedHandle、または requestedTitle / titleMatch / resolvedHandle / resolutionReason）。</param>
        /// <returns>text（解決情報 + normalizedHandle + window + 画像メタ情報）+ image、またはエラー。</returns>
        private static async Task<McpToolResult> CaptureTopLevelWindowAsync(VsServiceAccessor InAccessor, long InHandle, JObject InResolution)
        {
            IntPtr TheRequested = new IntPtr(InHandle);

            // 1. 有効性: ui_list_windows やタイトル検索の後に閉じられた／再利用された HWND を弾く
            if (!NativeMethods.IsWindow(TheRequested))
                return McpToolResult.Error($"Window handle {InHandle} is not a valid window.");

            // 2. 子 HWND が渡された場合はトップレベルへ正規化する
            IntPtr TheNormalized = NativeMethods.GetAncestor(TheRequested, NativeMethods.GA_ROOT);
            if (TheNormalized == IntPtr.Zero)
                TheNormalized = TheRequested;
            long TheNormalizedHandle = TheNormalized.ToInt64();

            // 3. 正規化後の HWND がデバッグ対象プロセスに属することを再照合する（他アプリ・VS 本体の撮影を防ぐ安全境界）
            HashSet<uint> TheProcessIds = await GetDebuggedProcessIdsAsync(InAccessor);
            if (TheProcessIds.Count == 0)
                return McpToolResult.Error("No debugged process found. Make sure debugging is active.");

            NativeMethods.GetWindowThreadProcessId(TheNormalized, out uint TheProcessId);
            if (!TheProcessIds.Contains(TheProcessId))
                return McpToolResult.Error($"Window handle {TheNormalizedHandle} does not belong to a currently debugged process.");

            // 4. 最小化中は WGC が空フレーム待ちになり PrintWindow でも空白になるため、キャプチャせずに明示エラーにする
            if (NativeMethods.IsIconic(TheNormalized))
                return McpToolResult.Error($"Window handle {TheNormalizedHandle} is minimized. Restore the window before capturing it.");

            // 5. WindowInfo は Phase 1 の列挙を再利用する（同一プロセスの全ウィンドウを見渡してモーダル候補を判定するため）
            WindowInfo TheWindow = await Task.Run(() =>
                DebuggeeWindowEnumerator.EnumerateTopLevelWindows(TheProcessIds, true)
                    .FirstOrDefault(TheCandidate => TheCandidate.Handle == TheNormalizedHandle));
            if (TheWindow == null)
                return McpToolResult.Error($"Window handle {TheNormalizedHandle} disappeared before it could be captured.");

            // 6. 既存の upstream キャプチャ経路をそのまま使う（WGC → PrintWindow フォールバック → PNG/JPEG・縮小）
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
            string TheTitleMatch = (InArgs.Value<string>("titleMatch") ?? "exact").ToLowerInvariant();

            Regex TheTitleRegex = null;
            if (!string.IsNullOrEmpty(TheTitleFilter))
            {
                if (TheTitleMatch != "exact" && TheTitleMatch != "contains" && TheTitleMatch != "regex")
                    return McpToolResult.Error($"Unknown titleMatch: '{TheTitleMatch}'. Expected one of: exact, contains, regex");

                if (TheTitleMatch == "regex")
                {
                    try
                    {
                        TheTitleRegex = new Regex(TheTitleFilter, RegexOptions.IgnoreCase);
                    }
                    catch (ArgumentException TheException)
                    {
                        return McpToolResult.Error($"Invalid regex: {TheException.Message}");
                    }
                }
            }

            HashSet<uint> TheProcessIds = await GetDebuggedProcessIdsAsync(InAccessor);
            if (TheProcessIds.Count == 0)
                return McpToolResult.Error("No debugged process found. Make sure debugging is active.");

            // Win32 列挙は UI スレッドを塞がないようバックグラウンドで実行する
            List<WindowInfo> TheWindows = await Task.Run(
                () => DebuggeeWindowEnumerator.EnumerateTopLevelWindows(TheProcessIds, TheIsInvisibleIncluded));

            if (!string.IsNullOrEmpty(TheTitleFilter))
            {
                TheWindows = TheWindows
                    .Where(TheWindow => IsTitleMatched(TheWindow.Title, TheTitleFilter, TheTitleMatch, TheTitleRegex))
                    .ToList();
            }

            return McpToolResult.Success(new
            {
                count = TheWindows.Count,
                processIds = TheProcessIds.OrderBy(TheProcessId => TheProcessId).ToList(),
                windows = TheWindows,
            });
        }

        /// <summary>タイトルがフィルターに一致するか判定する。既存 ui_find_elements の MatchString と同じ規則。</summary>
        /// <param name="InTitle">判定対象のタイトル。</param>
        /// <param name="InFilter">フィルター文字列。</param>
        /// <param name="InMode">exact / contains / regex。</param>
        /// <param name="InRegex">regex モード時のコンパイル済み正規表現。</param>
        /// <returns>一致すれば true。</returns>
        private static bool IsTitleMatched(string InTitle, string InFilter, string InMode, Regex InRegex)
        {
            string TheTitle = InTitle ?? string.Empty;
            switch (InMode)
            {
                case "contains":
                    return TheTitle.IndexOf(InFilter, StringComparison.OrdinalIgnoreCase) >= 0;
                case "regex":
                    return InRegex != null && InRegex.IsMatch(TheTitle);
                default:
                    return TheTitle == InFilter;
            }
        }
    }
}
