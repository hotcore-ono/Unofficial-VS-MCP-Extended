using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using static VsMcp.Extension.Tools.NativeMethods;

namespace VsMcp.Extension.Tools
{
    /// <summary>
    /// ui_window_* の Action 系ツールが共通で使う要素セレクター。automationId を第一候補とし、name / controlType / className で絞り込み、
    /// index は明示指定時だけ使う。文字列は完全一致（upstream ui_click の PropertyCondition と同じ）。
    /// </summary>
    internal sealed class UiWindowElementSelector
    {
        /// <summary>AutomationId（完全一致）。</summary>
        public string AutomationId { get; set; }

        /// <summary>Name（完全一致）。</summary>
        public string Name { get; set; }

        /// <summary>ControlType の指定文字列（"Button" / "ControlType.Button"）。</summary>
        public string ControlTypeName { get; set; }

        /// <summary>解析済み ControlType。未指定なら null。</summary>
        public ControlType ControlType { get; set; }

        /// <summary>ClassName（完全一致）。</summary>
        public string ClassName { get; set; }

        /// <summary>候補が複数残ったときに選ぶ 0 始まりの序数（ツリー順）。明示指定時のみ使う。</summary>
        public int? Index { get; set; }

        /// <summary>検索条件が 1 つでも指定されているか。</summary>
        public bool HasCriteria
        {
            get
            {
                return !string.IsNullOrEmpty(AutomationId) || !string.IsNullOrEmpty(Name)
                    || ControlType != null || !string.IsNullOrEmpty(ClassName);
            }
        }

        /// <summary>エラーメッセージ用の説明文を組み立てる。</summary>
        /// <returns>例: AutomationId 'ClickTestButton', ControlType 'Button'。</returns>
        public string Describe()
        {
            List<string> TheParts = new List<string>();
            if (!string.IsNullOrEmpty(AutomationId))
            {
                TheParts.Add($"AutomationId '{AutomationId}'");
            }
            if (!string.IsNullOrEmpty(Name))
            {
                TheParts.Add($"Name '{Name}'");
            }
            if (ControlType != null)
            {
                TheParts.Add($"ControlType '{ControlType.ProgrammaticName}'");
            }
            if (!string.IsNullOrEmpty(ClassName))
            {
                TheParts.Add($"ClassName '{ClassName}'");
            }
            if (Index.HasValue)
            {
                TheParts.Add($"index {Index.Value}");
            }
            return string.Join(", ", TheParts);
        }
    }

    /// <summary>指定ウィンドウ配下で一意に解決した UIA 要素と、Action 実行の判定に必要な情報。</summary>
    internal sealed class UiWindowResolvedElement
    {
        /// <summary>解決した要素。STA スレッド内でのみ使う（保持しない）。</summary>
        public AutomationElement Element { get; set; }

        /// <summary>要素が属するトップレベル HWND（10 進）。</summary>
        public long RootWindowHandle { get; set; }

        /// <summary>戻り値に含める要素情報（BuildElementInfo）。</summary>
        public Dictionary<string, object> Info { get; set; }

        /// <summary>BoundingRectangle（スクリーン物理 px）。</summary>
        public Rect Bounds { get; set; }

        /// <summary>IsEnabled。</summary>
        public bool IsEnabled { get; set; }

        /// <summary>IsOffscreen。</summary>
        public bool IsOffscreen { get; set; }

        /// <summary>矩形の中心 X（物理 px）。</summary>
        public int CenterX { get { return (int)(Bounds.X + Bounds.Width / 2); } }

        /// <summary>矩形の中心 Y（物理 px）。</summary>
        public int CenterY { get { return (int)(Bounds.Y + Bounds.Height / 2); } }
    }

    /// <summary>
    /// TryResolveSingle が要素を解決できなかった理由。呼び出し側がエラー文言の部分一致に頼らず分岐できるようにするための区分。
    /// </summary>
    internal enum UiWindowResolveFailure
    {
        /// <summary>失敗していない（解決できた）。</summary>
        None,

        /// <summary>セレクターの条件が 1 つも指定されていない。</summary>
        NoCriteria,

        /// <summary>指定ウィンドウを UI Automation の root として開けなかった。</summary>
        RootUnavailable,

        /// <summary>条件に合う要素が指定ウィンドウ配下に見つからなかった。</summary>
        NotFound,

        /// <summary>index が候補数の範囲外。</summary>
        IndexOutOfRange,

        /// <summary>候補が複数あり、index の指定も無いので一意に決められない。</summary>
        Ambiguous,

        /// <summary>解決した要素が別のトップレベルウィンドウに属している（所属を特定できない場合も含む）。</summary>
        OtherWindow,

        /// <summary>解決の途中で要素が消えた。</summary>
        Disappeared,

        /// <summary>Extended (Phase 9): 訪問要素数の内部上限に達し、候補をすべて見終わる前に走査を打ち切った。</summary>
        SearchAborted,
    }

    /// <summary>
    /// 任意のデバッグ対象トップレベル HWND を root にした UIA 要素の検索・一意解決・所属 HWND 判定。
    /// upstream UiTools の FindCriteria / MatchesCriteria / ParseControlType（internal 化済み）を再利用し、Desktop RootElement 全体は検索しない。
    /// </summary>
    internal static class UiWindowElementResolver
    {
        /// <summary>曖昧エラーに載せる候補の最大数。</summary>
        private const int _MAX_AMBIGUOUS_CANDIDATES = 20;

        /// <summary>index 指定時に走査する候補の上限。</summary>
        private const int _MAX_INDEXED_CANDIDATES = 500;

        /// <summary>要素が NativeWindowHandle=0 のとき、HWND を持つ祖先を探す最大段数。</summary>
        private const int _MAX_ANCESTOR_DEPTH = 64;

        /// <summary>
        /// Extended (Phase 9): Action 系ツールの要素解決（<see cref="TryResolveSingle"/>）が訪問する要素数の内部上限。
        /// 30 秒のタイムアウトだけに頼ると、巨大な UIA ツリーで待たされたうえに部分的な候補で一意性を判断してしまうため。
        /// </summary>
        private const int _MAX_VISITED_ELEMENTS = 10000;

        /// <summary>
        /// 引数からセレクターを読む。prefix が空なら automationId / name / controlType / className / index、
        /// "source" なら sourceAutomationId / sourceName / … のように先頭を大文字にして連結したキーを読む。
        /// </summary>
        /// <param name="InArgs">ツール引数。</param>
        /// <param name="InPrefix">キーの接頭辞（"" / "source" / "target"）。</param>
        /// <param name="OutSelector">読み取ったセレクター。</param>
        /// <returns>エラーメッセージ。正常なら null。</returns>
        public static string TryParseSelector(JObject InArgs, string InPrefix, out UiWindowElementSelector OutSelector)
        {
            OutSelector = new UiWindowElementSelector
            {
                AutomationId = InArgs.Value<string>(BuildKey(InPrefix, "automationId")),
                Name = InArgs.Value<string>(BuildKey(InPrefix, "name")),
                ControlTypeName = InArgs.Value<string>(BuildKey(InPrefix, "controlType")),
                ClassName = InArgs.Value<string>(BuildKey(InPrefix, "className")),
                Index = InArgs.Value<int?>(BuildKey(InPrefix, "index")),
            };

            if (!string.IsNullOrEmpty(OutSelector.ControlTypeName))
            {
                OutSelector.ControlType = UiTools.ParseControlType(OutSelector.ControlTypeName);
                if (OutSelector.ControlType == null)
                {
                    return $"Unknown ControlType: '{OutSelector.ControlTypeName}'";
                }
            }

            if (OutSelector.Index.HasValue && OutSelector.Index.Value < 0)
            {
                return $"Parameter '{BuildKey(InPrefix, "index")}' must be 0 or greater";
            }

            return null;
        }

        /// <summary>接頭辞付きの引数キーを組み立てる。</summary>
        /// <param name="InPrefix">接頭辞。</param>
        /// <param name="InKey">接頭辞なしのキー（camelCase）。</param>
        /// <returns>例: ("source", "automationId") → "sourceAutomationId"。</returns>
        public static string BuildKey(string InPrefix, string InKey)
        {
            if (string.IsNullOrEmpty(InPrefix))
            {
                return InKey;
            }
            return InPrefix + char.ToUpperInvariant(InKey[0]) + InKey.Substring(1);
        }

        /// <summary>セレクターを upstream の FindCriteria（完全一致）へ変換する。</summary>
        /// <param name="InSelector">セレクター。</param>
        /// <returns>検索条件。</returns>
        public static UiTools.FindCriteria ToCriteria(UiWindowElementSelector InSelector)
        {
            return new UiTools.FindCriteria
            {
                Name = InSelector.Name,
                NameMode = string.IsNullOrEmpty(InSelector.Name) ? UiTools.StringMatchMode.Any : UiTools.StringMatchMode.Exact,
                AutomationId = InSelector.AutomationId,
                AutomationIdMode = string.IsNullOrEmpty(InSelector.AutomationId) ? UiTools.StringMatchMode.Any : UiTools.StringMatchMode.Exact,
                ClassName = InSelector.ClassName,
                ClassNameMode = string.IsNullOrEmpty(InSelector.ClassName) ? UiTools.StringMatchMode.Any : UiTools.StringMatchMode.Exact,
                ControlType = InSelector.ControlType,
            };
        }

        /// <summary>
        /// root（自身を含む）とその子孫をコントロールビューでツリー順に走査し、条件に合う要素を集める。
        /// upstream WalkAndFindElements と同じ順序だが、辞書ではなく AutomationElement を集める（Extended の情報組み立てと index 指定のため）。
        /// </summary>
        /// <param name="InRoot">走査の起点。</param>
        /// <param name="InCriteria">検索条件。</param>
        /// <param name="InIsOffscreenIncluded">IsOffscreen の要素も含めるか。</param>
        /// <param name="InMaxResults">集める上限。</param>
        /// <param name="InOutResults">結果を追加するリスト。</param>
        /// <param name="InToken">中断トークン。</param>
        /// <summary>
        /// root（自身を含む）とその子孫をコントロールビューでツリー順に走査し、条件に合う要素を集める。
        /// upstream WalkAndFindElements と同じ順序だが、辞書ではなく AutomationElement を集める（Extended の情報組み立てと index 指定のため）。
        /// InRequiredRootHandle が 0 以外のときは、条件に合っても所属トップレベル HWND が異なる要素（VS のアプリ内ツールバー等、
        /// 別ウィンドウがホストする要素）を結果に入れず件数だけ数える。子孫の走査自体は続ける。
        /// </summary>
        /// <param name="InRoot">走査の起点。</param>
        /// <param name="InCriteria">検索条件。</param>
        /// <param name="InIsOffscreenIncluded">IsOffscreen の要素も含めるか。</param>
        /// <param name="InMaxResults">集める上限。</param>
        /// <param name="InRequiredRootHandle">結果に含める要素の所属トップレベル HWND（10 進）。0 なら所属を判定しない。</param>
        /// <param name="InWalker">走査に使うビュー（TreeWalker.ControlViewWalker / TreeWalker.RawViewWalker）。</param>
        /// <param name="InMaxVisited">訪問する要素数の上限（Extended Phase 9: 30 秒タイムアウトとは別の内部上限）。</param>
        /// <param name="InOutResults">結果を追加するリスト。</param>
        /// <param name="InOutSkippedOtherWindows">別ウィンドウ所属として除外した要素数の加算先。</param>
        /// <param name="InOutVisitedCount">訪問した要素数の加算先（上限に達したら走査を打ち切る）。</param>
        /// <param name="InToken">中断トークン。</param>
        public static void CollectMatches(AutomationElement InRoot, UiTools.FindCriteria InCriteria, bool InIsOffscreenIncluded,
            int InMaxResults, long InRequiredRootHandle, TreeWalker InWalker, int InMaxVisited, List<AutomationElement> InOutResults,
            ref int InOutSkippedOtherWindows, ref int InOutVisitedCount, CancellationToken InToken)
        {
            if (InToken.IsCancellationRequested || InOutResults.Count >= InMaxResults || InOutVisitedCount >= InMaxVisited)
            {
                return;
            }
            InOutVisitedCount++;

            if (UiTools.MatchesCriteria(InRoot, InCriteria))
            {
                bool IsAccepted = true;
                if (!InIsOffscreenIncluded)
                {
                    try
                    {
                        IsAccepted = !InRoot.Current.IsOffscreen;
                    }
                    catch
                    {
                        IsAccepted = false;
                    }
                }
                if (IsAccepted && InRequiredRootHandle != 0 && ResolveTopLevelHandle(InRoot) != InRequiredRootHandle)
                {
                    // 別のトップレベルウィンドウがホストする要素（所属不明の 0 を含む）は結果に入れない
                    IsAccepted = false;
                    InOutSkippedOtherWindows++;
                }
                if (IsAccepted)
                {
                    InOutResults.Add(InRoot);
                    if (InOutResults.Count >= InMaxResults)
                    {
                        return;
                    }
                }
            }

            try
            {
                AutomationElement TheChild = InWalker.GetFirstChild(InRoot);
                while (TheChild != null)
                {
                    if (InToken.IsCancellationRequested || InOutResults.Count >= InMaxResults || InOutVisitedCount >= InMaxVisited)
                    {
                        return;
                    }
                    CollectMatches(TheChild, InCriteria, InIsOffscreenIncluded, InMaxResults, InRequiredRootHandle, InWalker, InMaxVisited,
                        InOutResults, ref InOutSkippedOtherWindows, ref InOutVisitedCount, InToken);
                    TheChild = InWalker.GetNextSibling(TheChild);
                }
            }
            catch
            {
                // 走査中に消えた要素は無視する（upstream と同じ）
            }
        }

        /// <summary>
        /// 要素が属するトップレベル HWND を確定する。NativeWindowHandle が 0 の要素（WPF の大半）は RawView の親を root 方向へ遡り、
        /// HWND を持つ最初の祖先から GetAncestor(GA_ROOT) を取る。
        /// </summary>
        /// <param name="InElement">対象要素。</param>
        /// <returns>トップレベル HWND（10 進）。特定できなければ 0。</returns>
        public static long ResolveTopLevelHandle(AutomationElement InElement)
        {
            try
            {
                AutomationElement TheCurrent = InElement;
                for (int TheDepth = 0; TheCurrent != null && TheDepth < _MAX_ANCESTOR_DEPTH; TheDepth++)
                {
                    int TheNativeHandle = TheCurrent.Current.NativeWindowHandle;
                    if (TheNativeHandle != 0)
                    {
                        IntPtr TheRoot = GetAncestor(new IntPtr(TheNativeHandle), GA_ROOT);
                        return (TheRoot == IntPtr.Zero ? new IntPtr(TheNativeHandle) : TheRoot).ToInt64();
                    }
                    TheCurrent = TreeWalker.RawViewWalker.GetParent(TheCurrent);
                }
            }
            catch
            {
                // 要素が消えた場合は 0（呼び出し側で「所属不明」として拒否する）
            }
            return 0;
        }

        /// <summary>要素が対応する UIA パターン名（invoke / value / selectionItem / toggle / expandCollapse / scroll）を列挙する。</summary>
        /// <param name="InElement">対象要素。</param>
        /// <returns>対応するパターン名の一覧。</returns>
        public static List<string> GetPatternNames(AutomationElement InElement)
        {
            List<string> ThePatterns = new List<string>();
            AddPatternIfSupported(InElement, InvokePattern.Pattern, "invoke", ThePatterns);
            AddPatternIfSupported(InElement, ValuePattern.Pattern, "value", ThePatterns);
            AddPatternIfSupported(InElement, SelectionItemPattern.Pattern, "selectionItem", ThePatterns);
            AddPatternIfSupported(InElement, TogglePattern.Pattern, "toggle", ThePatterns);
            AddPatternIfSupported(InElement, ExpandCollapsePattern.Pattern, "expandCollapse", ThePatterns);
            AddPatternIfSupported(InElement, ScrollPattern.Pattern, "scroll", ThePatterns);
            return ThePatterns;
        }

        /// <summary>パターンに対応していれば名前を一覧へ追加する。</summary>
        /// <param name="InElement">対象要素。</param>
        /// <param name="InPattern">確認するパターン。</param>
        /// <param name="InName">戻り値で使う名前。</param>
        /// <param name="InOutPatterns">追加先。</param>
        private static void AddPatternIfSupported(AutomationElement InElement, AutomationPattern InPattern, string InName, List<string> InOutPatterns)
        {
            try
            {
                if (InElement.TryGetCurrentPattern(InPattern, out object ThePattern))
                {
                    InOutPatterns.Add(InName);
                }
            }
            catch
            {
                // 取得できないパターンは載せない
            }
        }

        /// <summary>
        /// 戻り値用の要素情報を組み立てる。upstream BuildElementInfo の項目（name / automationId / className / controlType / bounds / isEnabled）に
        /// nativeWindowHandle / isOffscreen / rootWindowHandle / patterns を加える。
        /// </summary>
        /// <param name="InElement">対象要素。</param>
        /// <param name="InRootWindowHandle">要素が属するトップレベル HWND（ResolveTopLevelHandle の結果）。</param>
        /// <returns>要素情報。</returns>
        public static Dictionary<string, object> BuildElementInfo(AutomationElement InElement, long InRootWindowHandle)
        {
            Rect TheRect = InElement.Current.BoundingRectangle;
            return new Dictionary<string, object>
            {
                ["name"] = InElement.Current.Name,
                ["automationId"] = InElement.Current.AutomationId,
                ["controlType"] = InElement.Current.ControlType.ProgrammaticName,
                ["className"] = InElement.Current.ClassName,
                ["nativeWindowHandle"] = InElement.Current.NativeWindowHandle,
                ["isEnabled"] = InElement.Current.IsEnabled,
                ["isOffscreen"] = InElement.Current.IsOffscreen,
                ["bounds"] = TheRect.IsEmpty ? null : $"{(int)TheRect.X},{(int)TheRect.Y},{(int)TheRect.Width},{(int)TheRect.Height}",
                ["rootWindowHandle"] = InRootWindowHandle,
                ["patterns"] = GetPatternNames(InElement),
            };
        }

        /// <summary>
        /// 指定ウィンドウ配下でセレクターに合う要素をちょうど 1 件に解決する。候補が複数で index 未指定なら候補一覧付きのエラー、
        /// 解決した要素のトップレベル HWND が指定ウィンドウと異なればエラー。STA スレッドで呼ぶこと。
        /// </summary>
        /// <param name="InWindow">正規化済みのトップレベル HWND。</param>
        /// <param name="InSelector">セレクター。</param>
        /// <param name="OutElement">解決した要素。エラー時は null。</param>
        /// <returns>エラーメッセージ。正常なら null。</returns>
        /// <summary>
        /// 指定ウィンドウ配下でセレクターに合う要素をちょうど 1 件に解決する。候補は収集の時点で「所属トップレベル HWND が指定ウィンドウ」の
        /// ものだけに限定するので、別ウィンドウがホストする要素が曖昧判定や index の採番を汚染しない。STA スレッドで呼ぶこと。
        /// </summary>
        /// <param name="InWindow">正規化済みのトップレベル HWND。</param>
        /// <param name="InSelector">セレクター。</param>
        /// <param name="OutElement">解決した要素。エラー時は null。</param>
        /// <param name="OutFailure">解決できなかった理由。正常なら None。</param>
        /// <returns>エラーメッセージ。正常なら null。</returns>
        public static string TryResolveSingle(IntPtr InWindow, UiWindowElementSelector InSelector, out UiWindowResolvedElement OutElement,
            out UiWindowResolveFailure OutFailure)
        {
            OutElement = null;
            OutFailure = UiWindowResolveFailure.None;
            if (!InSelector.HasCriteria)
            {
                OutFailure = UiWindowResolveFailure.NoCriteria;
                return "At least one element selector must be provided (automationId, name, controlType, or className)";
            }

            AutomationElement TheRoot;
            try
            {
                TheRoot = AutomationElement.FromHandle(InWindow);
            }
            catch (Exception TheException)
            {
                OutFailure = UiWindowResolveFailure.RootUnavailable;
                return $"Window handle {InWindow.ToInt64()} could not be opened as a UI Automation root: {TheException.Message}";
            }

            int TheLimit = InSelector.Index.HasValue ? _MAX_INDEXED_CANDIDATES : _MAX_AMBIGUOUS_CANDIDATES + 1;
            List<AutomationElement> TheMatches = new List<AutomationElement>();
            int TheSkippedOtherWindows = 0;
            int TheVisitedCount = 0;
            CollectMatches(TheRoot, ToCriteria(InSelector), true, TheLimit, InWindow.ToInt64(), TreeWalker.ControlViewWalker, _MAX_VISITED_ELEMENTS,
                TheMatches, ref TheSkippedOtherWindows, ref TheVisitedCount, CancellationToken.None);

            if (TheVisitedCount >= _MAX_VISITED_ELEMENTS)
            {
                // Extended (Phase 9): 走査が内部上限で打ち切られた時点の候補は「全候補」ではないので、一意性を主張せずに拒否する
                OutFailure = UiWindowResolveFailure.SearchAborted;
                return $"Element search in window {InWindow.ToInt64()} was aborted after {_MAX_VISITED_ELEMENTS} elements; narrow the selector " +
                    $"(criteria: {InSelector.Describe()})";
            }
            if (TheMatches.Count == 0)
            {
                OutFailure = UiWindowResolveFailure.NotFound;
                return $"Element with {InSelector.Describe()} not found in window {InWindow.ToInt64()}";
            }

            AutomationElement TheSelected;
            if (InSelector.Index.HasValue)
            {
                if (InSelector.Index.Value >= TheMatches.Count)
                {
                    // 上限まで集めた時点で打ち切っているので、実際の一致数は分からない（「ちょうど 500 件」と誤解させない）
                    string TheMatchCountText = TheMatches.Count >= _MAX_INDEXED_CANDIDATES
                        ? $"at least {_MAX_INDEXED_CANDIDATES}"
                        : TheMatches.Count.ToString();
                    OutFailure = UiWindowResolveFailure.IndexOutOfRange;
                    return $"Element index {InSelector.Index.Value} is out of range: {TheMatchCountText} element(s) match {InSelector.Describe()} in window {InWindow.ToInt64()}";
                }
                TheSelected = TheMatches[InSelector.Index.Value];
            }
            else if (TheMatches.Count > 1)
            {
                List<Dictionary<string, object>> TheCandidates = new List<Dictionary<string, object>>();
                for (int TheIndex = 0; TheIndex < TheMatches.Count && TheIndex < _MAX_AMBIGUOUS_CANDIDATES; TheIndex++)
                {
                    Dictionary<string, object> TheInfo = BuildElementInfo(TheMatches[TheIndex], ResolveTopLevelHandle(TheMatches[TheIndex]));
                    TheInfo["index"] = TheIndex;
                    TheCandidates.Add(TheInfo);
                }
                string TheCountText = TheMatches.Count > _MAX_AMBIGUOUS_CANDIDATES ? $"more than {_MAX_AMBIGUOUS_CANDIDATES}" : TheMatches.Count.ToString();
                OutFailure = UiWindowResolveFailure.Ambiguous;
                return $"{TheCountText} elements match {InSelector.Describe()} in window {InWindow.ToInt64()}. " +
                    "Add more selector criteria (automationId / name / controlType / className) or pass 'index' to pick one. Candidates:\n" +
                    JsonConvert.SerializeObject(TheCandidates, Formatting.Indented);
            }
            else
            {
                TheSelected = TheMatches[0];
            }

            // 収集時に絞り込んでいるので通常は一致するが、走査後に要素が付け替わった場合に備えて実行直前にもう一度確かめる
            long TheRootHandle = ResolveTopLevelHandle(TheSelected);
            if (TheRootHandle == 0)
            {
                OutFailure = UiWindowResolveFailure.OtherWindow;
                return $"Element with {InSelector.Describe()} has no resolvable top-level window (no ancestor exposes a native window handle); refusing to act on it";
            }
            if (TheRootHandle != InWindow.ToInt64())
            {
                OutFailure = UiWindowResolveFailure.OtherWindow;
                return $"Element with {InSelector.Describe()} belongs to window {TheRootHandle}, not to the requested window {InWindow.ToInt64()}; refusing to act on it";
            }

            try
            {
                OutElement = new UiWindowResolvedElement
                {
                    Element = TheSelected,
                    RootWindowHandle = TheRootHandle,
                    Info = BuildElementInfo(TheSelected, TheRootHandle),
                    Bounds = TheSelected.Current.BoundingRectangle,
                    IsEnabled = TheSelected.Current.IsEnabled,
                    IsOffscreen = TheSelected.Current.IsOffscreen,
                };
            }
            catch (ElementNotAvailableException)
            {
                OutFailure = UiWindowResolveFailure.Disappeared;
                return $"Element with {InSelector.Describe()} disappeared while it was being resolved";
            }
            return null;
        }
    }
}
