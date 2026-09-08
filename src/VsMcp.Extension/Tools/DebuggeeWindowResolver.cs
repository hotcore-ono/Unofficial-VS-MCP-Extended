using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

using static VsMcp.Extension.Tools.NativeMethods;

namespace VsMcp.Extension.Tools
{
    /// <summary>タイトル検索条件。ui_list_windows / ui_capture_window_by_title / ui_wait_for_window 系で共通に使う。</summary>
    internal sealed class WindowTitleQuery
    {
        /// <summary>検索するタイトル（exact / contains の比較対象、regex ではパターン）。</summary>
        public string Title { get; set; }

        /// <summary>一致方式。exact（既定・大小文字区別）/ contains（非区別）/ regex（非区別）。</summary>
        public string TitleMatch { get; set; }

        /// <summary>regex 方式のときのコンパイル済み正規表現。他方式では null。</summary>
        public Regex TitleRegex { get; set; }

        /// <summary>任意のクラス名フィルタ（大小文字非区別の完全一致）。未指定なら null。</summary>
        public string ClassName { get; set; }

        /// <summary>エラーメッセージ用の条件説明。exact かつ className 無しなら title "X"、それ以外は括弧で補足を付ける。</summary>
        /// <returns>条件の説明文。</returns>
        public string Describe()
        {
            List<string> TheExtras = new List<string>();
            if (TitleMatch != "exact")
                TheExtras.Add("titleMatch: " + TitleMatch);
            if (!string.IsNullOrEmpty(ClassName))
                TheExtras.Add("className: " + ClassName);

            string TheBase = $"title \"{Title}\"";
            return TheExtras.Count == 0 ? TheBase : $"{TheBase} ({string.Join(", ", TheExtras)})";
        }
    }

    /// <summary>タイトル一致候補の解決結果。</summary>
    internal sealed class CandidateResolution
    {
        /// <summary>一意に解決できたウィンドウ。解決できなければ null。</summary>
        public WindowInfo Resolved { get; set; }

        /// <summary>解決根拠（singleMatch / foregroundMatch / enabledVisibleMatch）。未解決なら null。</summary>
        public string Reason { get; set; }

        /// <summary>一致した全候補。</summary>
        public List<WindowInfo> Candidates { get; set; }

        /// <summary>候補のうちフォアグラウンドだった件数（複数一致時のみ計算）。</summary>
        public int ForegroundCount { get; set; }

        /// <summary>候補のうち可視かつ有効だった件数（複数一致時のみ計算）。</summary>
        public int EnabledVisibleCount { get; set; }

        /// <summary>一意に解決できたか。</summary>
        public bool IsResolved => Resolved != null;
    }

    /// <summary>アクティブウィンドウの解決結果。</summary>
    internal sealed class ActiveWindowResolution
    {
        /// <summary>解決されたウィンドウ。解決できなければ null。</summary>
        public WindowInfo Resolved { get; set; }

        /// <summary>解決根拠（foreground / guiThreadActive / modalCandidate / visibleEnabledOwnerless / visibleEnabled）。</summary>
        public string Source { get; set; }

        /// <summary>未解決時の候補一覧（最後まで絞り込んだ段階の集合）。</summary>
        public List<WindowInfo> Candidates { get; set; }

        /// <summary>診断用: 解決時点の GetForegroundWindow の値。</summary>
        public long ForegroundHandle { get; set; }

        /// <summary>診断用: デバッグ対象 GUI スレッドごとの hwndActive をトップレベルへ正規化し重複排除したもの。</summary>
        public List<long> GuiThreadActiveHandles { get; set; }

        /// <summary>解決できたか。</summary>
        public bool IsResolved => Resolved != null;
    }

    /// <summary>
    /// デバッグ対象トップレベルウィンドウの「探す・選ぶ・検証する」を担う Extended 側の共通ロジック。
    /// by_handle / by_title / active / wait 系の MCP ツールが同じ判定を共有し、ツールごとに重複実装しないためのクラス。
    /// Win32 と WindowInfo だけに依存し、DTE や MCP の型には触れない（UI スレッド要件を持ち込まないため）。
    /// </summary>
    internal static class DebuggeeWindowResolver
    {
        /// <summary>解決根拠: 一致が 1 件だけだった。</summary>
        public const string ReasonSingleMatch = "singleMatch";

        /// <summary>解決根拠: 複数一致のうちフォアグラウンドが 1 件だけだった。</summary>
        public const string ReasonForegroundMatch = "foregroundMatch";

        /// <summary>解決根拠: 複数一致のうち可視かつ有効が 1 件だけだった。</summary>
        public const string ReasonEnabledVisibleMatch = "enabledVisibleMatch";

        /// <summary>アクティブ解決根拠: システムのフォアグラウンドウィンドウがデバッグ対象だった。</summary>
        public const string SourceForeground = "foreground";

        /// <summary>アクティブ解決根拠: デバッグ対象 GUI スレッドのアクティブウィンドウが一意に決まった。</summary>
        public const string SourceGuiThreadActive = "guiThreadActive";

        /// <summary>アクティブ解決根拠: 可視かつ有効なモーダル候補が 1 件だけだった。</summary>
        public const string SourceModalCandidate = "modalCandidate";

        /// <summary>アクティブ解決根拠: 可視かつ有効で Owner なしのウィンドウが 1 件だけだった。</summary>
        public const string SourceVisibleEnabledOwnerless = "visibleEnabledOwnerless";

        /// <summary>アクティブ解決根拠: 可視かつ有効なウィンドウが 1 件だけだった。</summary>
        public const string SourceVisibleEnabled = "visibleEnabled";

        /// <summary>デバッグ対象プロセスが無いときの共通エラー文。</summary>
        public const string NoDebuggedProcessMessage = "No debugged process found. Make sure debugging is active.";

        /// <summary>
        /// title / titleMatch / className の引数を検証して <see cref="WindowTitleQuery"/> を組み立てる。
        /// titleMatch の既定は exact。regex は IgnoreCase でコンパイルし、不正なら "Invalid regex: ..." を返す（既存 ui_list_windows と同文言）。
        /// </summary>
        /// <param name="InTitle">検索タイトル（呼び出し側で非空を保証する）。</param>
        /// <param name="InTitleMatch">一致方式。null なら exact。</param>
        /// <param name="InClassName">任意のクラス名フィルタ。</param>
        /// <param name="OutQuery">組み立てた条件。エラー時は null。</param>
        /// <returns>エラーメッセージ。正常なら null。</returns>
        public static string TryParseTitleQuery(string InTitle, string InTitleMatch, string InClassName, out WindowTitleQuery OutQuery)
        {
            OutQuery = null;
            string TheTitleMatch = (InTitleMatch ?? "exact").ToLowerInvariant();
            if (TheTitleMatch != "exact" && TheTitleMatch != "contains" && TheTitleMatch != "regex")
                return $"Unknown titleMatch: '{TheTitleMatch}'. Expected one of: exact, contains, regex";

            Regex TheRegex = null;
            if (TheTitleMatch == "regex")
            {
                try
                {
                    TheRegex = new Regex(InTitle, RegexOptions.IgnoreCase);
                }
                catch (ArgumentException TheException)
                {
                    return $"Invalid regex: {TheException.Message}";
                }
            }

            OutQuery = new WindowTitleQuery
            {
                Title = InTitle,
                TitleMatch = TheTitleMatch,
                TitleRegex = TheRegex,
                ClassName = string.IsNullOrEmpty(InClassName) ? null : InClassName,
            };
            return null;
        }

        /// <summary>ウィンドウが検索条件に一致するか判定する。タイトル規則は既存 ui_find_elements の MatchString と同じ。</summary>
        /// <param name="InWindow">判定対象。</param>
        /// <param name="InQuery">検索条件。</param>
        /// <returns>一致すれば true。</returns>
        public static bool IsMatched(WindowInfo InWindow, WindowTitleQuery InQuery)
        {
            string TheTitle = InWindow.Title ?? string.Empty;
            bool TheIsTitleMatched;
            switch (InQuery.TitleMatch)
            {
                case "contains":
                    TheIsTitleMatched = TheTitle.IndexOf(InQuery.Title, StringComparison.OrdinalIgnoreCase) >= 0;
                    break;
                case "regex":
                    TheIsTitleMatched = InQuery.TitleRegex != null && InQuery.TitleRegex.IsMatch(TheTitle);
                    break;
                default:
                    TheIsTitleMatched = TheTitle == InQuery.Title;
                    break;
            }
            if (!TheIsTitleMatched)
                return false;

            if (InQuery.ClassName == null)
                return true;
            return string.Equals(InWindow.ClassName ?? string.Empty, InQuery.ClassName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>検索条件に一致するウィンドウだけを列挙順のまま返す。</summary>
        /// <param name="InWindows">列挙済みウィンドウ。</param>
        /// <param name="InQuery">検索条件。</param>
        /// <returns>一致したウィンドウ。</returns>
        public static List<WindowInfo> Filter(IEnumerable<WindowInfo> InWindows, WindowTitleQuery InQuery)
        {
            return InWindows.Where(TheWindow => IsMatched(TheWindow, InQuery)).ToList();
        }

        /// <summary>
        /// 一致候補を「単一一致 → フォアグラウンドが 1 件 → 可視かつ有効が 1 件」の順で一意に絞る。
        /// どの段階でも 1 件に絞れなければ未解決として返し、Z 順先頭などの自動選択はしない。
        /// </summary>
        /// <param name="InCandidates">一致した候補（0 件でもよい）。</param>
        /// <returns>解決結果。</returns>
        public static CandidateResolution ResolveCandidates(List<WindowInfo> InCandidates)
        {
            CandidateResolution TheResult = new CandidateResolution { Candidates = InCandidates };
            if (InCandidates.Count == 0)
                return TheResult;

            if (InCandidates.Count == 1)
            {
                TheResult.Resolved = InCandidates[0];
                TheResult.Reason = ReasonSingleMatch;
                return TheResult;
            }

            List<WindowInfo> TheForegroundCandidates = InCandidates.Where(TheWindow => TheWindow.IsForeground).ToList();
            List<WindowInfo> TheEnabledVisibleCandidates = InCandidates.Where(TheWindow => TheWindow.IsVisible && TheWindow.IsEnabled).ToList();
            TheResult.ForegroundCount = TheForegroundCandidates.Count;
            TheResult.EnabledVisibleCount = TheEnabledVisibleCandidates.Count;

            if (TheForegroundCandidates.Count == 1)
            {
                TheResult.Resolved = TheForegroundCandidates[0];
                TheResult.Reason = ReasonForegroundMatch;
            }
            else if (TheEnabledVisibleCandidates.Count == 1)
            {
                TheResult.Resolved = TheEnabledVisibleCandidates[0];
                TheResult.Reason = ReasonEnabledVisibleMatch;
            }
            return TheResult;
        }

        /// <summary>複数候補を一意に絞れなかったときのエラー文（候補一覧の JSON 付き）を組み立てる。</summary>
        /// <param name="InResolution">未解決の解決結果。</param>
        /// <param name="InQueryDescription">検索条件の説明（<see cref="WindowTitleQuery.Describe"/>）。</param>
        /// <param name="InHint">呼び出し側ツールに応じた次の行動の案内文。</param>
        /// <returns>エラー文。</returns>
        public static string DescribeAmbiguity(CandidateResolution InResolution, string InQueryDescription, string InHint)
        {
            return $"{InResolution.Candidates.Count} debugged windows matched {InQueryDescription} and none could be selected unambiguously " +
                   $"(foreground: {InResolution.ForegroundCount}, visible and enabled: {InResolution.EnabledVisibleCount}). " +
                   InHint + " Candidates:\n" +
                   JsonConvert.SerializeObject(InResolution.Candidates, Formatting.Indented);
        }

        /// <summary>
        /// HWND を検証してトップレベルへ正規化する共通処理。IsWindow → GetAncestor(GA_ROOT) → デバッグ対象 PID との再照合の順。
        /// 検証順は Phase 2 の ui_capture_window_by_handle と同じ（無効 HWND はデバッグ状態に関わらず "not a valid window"）。
        /// </summary>
        /// <param name="InHandle">検証する HWND（範囲検証済み）。子 HWND でもよい。</param>
        /// <param name="InProcessIds">現在デバッグ中のプロセス ID 集合。</param>
        /// <param name="OutNormalized">正規化後のトップレベル HWND。エラー時は IntPtr.Zero。</param>
        /// <returns>エラーメッセージ。正常なら null。</returns>
        public static string ValidateAndNormalizeWindowHandle(long InHandle, HashSet<uint> InProcessIds, out IntPtr OutNormalized)
        {
            OutNormalized = IntPtr.Zero;
            IntPtr TheRequested = new IntPtr(InHandle);

            // 1. 有効性: 取得後に閉じられた／再利用された HWND を弾く
            if (!IsWindow(TheRequested))
                return $"Window handle {InHandle} is not a valid window.";

            // 2. 子 HWND が渡された場合はトップレベルへ正規化する
            IntPtr TheNormalized = GetAncestor(TheRequested, GA_ROOT);
            if (TheNormalized == IntPtr.Zero)
                TheNormalized = TheRequested;

            // 3. 正規化後の HWND がデバッグ対象プロセスに属することを再照合する（他アプリ・VS 本体を対象にしない安全境界）
            if (InProcessIds == null || InProcessIds.Count == 0)
                return NoDebuggedProcessMessage;

            GetWindowThreadProcessId(TheNormalized, out uint TheProcessId);
            if (!InProcessIds.Contains(TheProcessId))
                return $"Window handle {TheNormalized.ToInt64()} does not belong to a currently debugged process.";

            OutNormalized = TheNormalized;
            return null;
        }

        /// <summary>
        /// デバッグ対象アプリで実質的にアクティブなトップレベルウィンドウを解決する。
        ///  1. GetForegroundWindow がデバッグ対象のトップレベル（可視）ならそれ（foreground）
        ///  2. そうでなければ（VS 等がフォアグラウンド）、デバッグ対象 GUI スレッドごとの GetGUIThreadInfo.hwndActive を
        ///     トップレベルへ正規化・重複排除し、1 件なら採用（guiThreadActive）
        ///  3. それでも決まらなければ可視かつ有効なウィンドウを「モーダル候補 → Owner なし → 全部」の順に絞り、各段階で 1 件なら採用
        /// どの段階でも一意にならなければ未解決とし、候補一覧を返す。Z 順先頭は選ばない。
        /// </summary>
        /// <param name="InProcessIds">現在デバッグ中のプロセス ID 集合。</param>
        /// <param name="InVisibleWindows">デバッグ対象の可視トップレベルウィンドウ一覧（EnumerateTopLevelWindows(includeInvisible=false)）。</param>
        /// <returns>解決結果。</returns>
        public static ActiveWindowResolution ResolveActiveWindow(HashSet<uint> InProcessIds, List<WindowInfo> InVisibleWindows)
        {
            ActiveWindowResolution TheResult = new ActiveWindowResolution { GuiThreadActiveHandles = new List<long>() };

            // Step 1: システムのフォアグラウンド
            IntPtr TheForeground = GetForegroundWindow();
            TheResult.ForegroundHandle = TheForeground.ToInt64();
            WindowInfo TheForegroundWindow = FindDebuggeeTopLevel(TheForeground, InProcessIds, InVisibleWindows);
            if (TheForegroundWindow != null)
            {
                TheResult.Resolved = TheForegroundWindow;
                TheResult.Source = SourceForeground;
                return TheResult;
            }

            // Step 2: デバッグ対象 GUI スレッドごとのアクティブウィンドウ
            foreach (uint TheThreadId in InVisibleWindows.Select(TheWindow => TheWindow.ThreadId).Distinct())
            {
                GUITHREADINFO TheInfo = new GUITHREADINFO { cbSize = (uint)Marshal.SizeOf(typeof(GUITHREADINFO)) };
                if (!GetGUIThreadInfo(TheThreadId, ref TheInfo) || TheInfo.hwndActive == IntPtr.Zero)
                    continue;

                WindowInfo TheActiveWindow = FindDebuggeeTopLevel(TheInfo.hwndActive, InProcessIds, InVisibleWindows);
                if (TheActiveWindow != null && !TheResult.GuiThreadActiveHandles.Contains(TheActiveWindow.Handle))
                    TheResult.GuiThreadActiveHandles.Add(TheActiveWindow.Handle);
            }
            if (TheResult.GuiThreadActiveHandles.Count == 1)
            {
                long TheActiveHandle = TheResult.GuiThreadActiveHandles[0];
                TheResult.Resolved = InVisibleWindows.First(TheWindow => TheWindow.Handle == TheActiveHandle);
                TheResult.Source = SourceGuiThreadActive;
                return TheResult;
            }

            // Step 3: 列挙結果からの絞り込み（各段階で 1 件なら採用）
            List<WindowInfo> TheVisibleEnabled = InVisibleWindows.Where(TheWindow => TheWindow.IsVisible && TheWindow.IsEnabled).ToList();
            if (TrySelectUnique(TheVisibleEnabled.Where(TheWindow => TheWindow.IsModalCandidate).ToList(), SourceModalCandidate, TheResult))
                return TheResult;
            if (TrySelectUnique(TheVisibleEnabled.Where(TheWindow => TheWindow.OwnerHandle == 0).ToList(), SourceVisibleEnabledOwnerless, TheResult))
                return TheResult;
            if (TrySelectUnique(TheVisibleEnabled, SourceVisibleEnabled, TheResult))
                return TheResult;

            TheResult.Candidates = TheVisibleEnabled.Count > 0 ? TheVisibleEnabled : InVisibleWindows;
            return TheResult;
        }

        /// <summary>候補がちょうど 1 件なら解決結果に採用する。</summary>
        /// <param name="InCandidates">絞り込んだ候補。</param>
        /// <param name="InSource">採用時に記録する解決根拠。</param>
        /// <param name="InOutResult">更新する解決結果。</param>
        /// <returns>採用した場合 true。</returns>
        private static bool TrySelectUnique(List<WindowInfo> InCandidates, string InSource, ActiveWindowResolution InOutResult)
        {
            if (InCandidates.Count != 1)
                return false;

            InOutResult.Resolved = InCandidates[0];
            InOutResult.Source = InSource;
            return true;
        }

        /// <summary>
        /// 任意の HWND をトップレベルへ正規化し、デバッグ対象プロセスに属し、かつ列挙済み一覧に含まれる WindowInfo を返す。
        /// 無効 HWND・他プロセス・一覧外（非表示など）なら null。
        /// </summary>
        /// <param name="InHandle">任意の HWND（子ウィンドウでもよい）。</param>
        /// <param name="InProcessIds">現在デバッグ中のプロセス ID 集合。</param>
        /// <param name="InWindows">列挙済みウィンドウ一覧。</param>
        /// <returns>該当する WindowInfo。無ければ null。</returns>
        private static WindowInfo FindDebuggeeTopLevel(IntPtr InHandle, HashSet<uint> InProcessIds, List<WindowInfo> InWindows)
        {
            if (InHandle == IntPtr.Zero || !IsWindow(InHandle))
                return null;

            IntPtr TheRoot = GetAncestor(InHandle, GA_ROOT);
            if (TheRoot == IntPtr.Zero)
                TheRoot = InHandle;

            GetWindowThreadProcessId(TheRoot, out uint TheProcessId);
            if (!InProcessIds.Contains(TheProcessId))
                return null;

            long TheRootHandle = TheRoot.ToInt64();
            return InWindows.FirstOrDefault(TheWindow => TheWindow.Handle == TheRootHandle);
        }
    }
}
