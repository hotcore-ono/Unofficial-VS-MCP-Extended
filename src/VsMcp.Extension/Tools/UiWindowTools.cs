using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using EnvDTE80;
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
        }

        /// <summary>
        /// ui_capture_window_by_handle の本体。HWND を検証・正規化し、デバッグ対象プロセスに属することを再照合してから
        /// 既存の UiTools キャプチャ経路（WGC → PrintWindow → PNG/JPEG）へ渡す。キャプチャ本体は再実装しない。
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

            IntPtr TheRequested = new IntPtr(TheRequestedHandle.Value);

            // 1. 有効性: ui_list_windows 取得後に閉じられた／再利用された HWND を弾く
            if (!NativeMethods.IsWindow(TheRequested))
                return McpToolResult.Error($"Window handle {TheRequestedHandle.Value} is not a valid window.");

            // 2. 子 HWND が渡された場合はトップレベルへ正規化する
            IntPtr TheNormalized = NativeMethods.GetAncestor(TheRequested, NativeMethods.GA_ROOT);
            if (TheNormalized == IntPtr.Zero)
                TheNormalized = TheRequested;
            long TheNormalizedHandle = TheNormalized.ToInt64();

            // 3. 正規化後の HWND がデバッグ対象プロセスに属することを再照合する（他アプリ・VS 本体の撮影を防ぐ安全境界）
            HashSet<uint> TheProcessIds = await InAccessor.RunOnUIThreadAsync(() =>
            {
                DTE2 TheDte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => InAccessor.GetDteAsync());
                return DebuggeeWindowEnumerator.GetDebuggedProcessIds(TheDte);
            });
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

                string TheText = Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    requestedHandle = TheRequestedHandle.Value,
                    normalizedHandle = TheNormalizedHandle,
                    window = TheWindow,
                    originalWidth = TheOriginalWidth,
                    originalHeight = TheOriginalHeight,
                    mimeType = TheMimeType,
                }, Newtonsoft.Json.Formatting.Indented);

                McpToolResult TheResult = McpToolResult.Success(TheText);
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

            // DTE は UI スレッドでのみ触る（既存 UiTools.GetDebuggeeProcessId と同じ流儀）
            HashSet<uint> TheProcessIds = await InAccessor.RunOnUIThreadAsync(() =>
            {
                DTE2 TheDte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory
                    .Run(() => InAccessor.GetDteAsync());
                return DebuggeeWindowEnumerator.GetDebuggedProcessIds(TheDte);
            });

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
