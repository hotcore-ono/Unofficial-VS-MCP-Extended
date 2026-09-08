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
