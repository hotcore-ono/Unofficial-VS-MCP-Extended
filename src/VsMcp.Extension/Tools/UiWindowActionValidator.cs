using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using static VsMcp.Extension.Tools.NativeMethods;

namespace VsMcp.Extension.Tools
{
    /// <summary>ui_window_get_info の modalState と、Action 実行前のモーダル判定の結果。</summary>
    internal sealed class UiWindowModalState
    {
        /// <summary>対象ウィンドウがモーダルウィンドウによって無効化されていると判定した場合 true。</summary>
        [JsonProperty("isBlockedByModal")]
        public bool IsBlockedByModal { get; set; }

        /// <summary>対象を無効化しているモーダル候補ウィンドウの HWND（10 進）。特定できない場合は 0。</summary>
        [JsonProperty("blockingWindowHandle")]
        public long BlockingWindowHandle { get; set; }

        /// <summary>対象を無効化しているモーダル候補ウィンドウのタイトル。特定できない場合は null。</summary>
        [JsonProperty("blockingWindowTitle")]
        public string BlockingWindowTitle { get; set; }

        /// <summary>モーダル候補と判定した根拠（ownerDisabled / siblingDisabled）。特定できない場合は null。</summary>
        [JsonProperty("blockingModalCandidateReason")]
        public string BlockingModalCandidateReason { get; set; }

        /// <summary>対象ウィンドウ自身の IsWindowEnabled。</summary>
        [JsonProperty("isWindowEnabled")]
        public bool IsWindowEnabled { get; set; }

        /// <summary>対象ウィンドウ自身の IsWindowVisible。</summary>
        [JsonProperty("isWindowVisible")]
        public bool IsWindowVisible { get; set; }

        /// <summary>対象ウィンドウ自身がモーダル候補（ダイアログ側）である場合 true。</summary>
        [JsonProperty("isWindowModalCandidate")]
        public bool IsWindowModalCandidate { get; set; }

        /// <summary>
        /// 操作可否の判定理由: "interactable"（操作可）、"blockedByModal"（モーダルで無効化）、
        /// "windowDisabled"（無効だがモーダル候補を特定できない）、"windowHidden"（非表示）。
        /// </summary>
        [JsonProperty("reason")]
        public string Reason { get; set; }

        /// <summary>操作してよい状態か（reason == interactable）。</summary>
        [JsonIgnore]
        public bool IsInteractable { get { return Reason == UiWindowActionValidator.ReasonInteractable; } }
    }

    /// <summary>
    /// ui_window_* の Action 系ツールが実行直前に通す安全境界。ウィンドウ状態を再列挙し、Phase 1 の IsModalCandidate 判定を再利用して
    /// 「対象ウィンドウが今操作可能か」を決める。UIA Invoke が技術的に発火できても、モーダルで無効化された owner への操作は拒否する。
    /// </summary>
    internal static class UiWindowActionValidator
    {
        /// <summary>操作可。</summary>
        public const string ReasonInteractable = "interactable";

        /// <summary>モーダル候補ウィンドウにより無効化されている。</summary>
        public const string ReasonBlockedByModal = "blockedByModal";

        /// <summary>無効化されているがモーダル候補を特定できない。</summary>
        public const string ReasonWindowDisabled = "windowDisabled";

        /// <summary>非表示。</summary>
        public const string ReasonWindowHidden = "windowHidden";

        /// <summary>Owner 連鎖を遡る最大段数（Owner の Owner … を追う）。</summary>
        private const int _MAX_OWNER_CHAIN = 8;

        /// <summary>
        /// 対象ウィンドウのモーダル状態を評価する。デバッグ対象の全トップレベルウィンドウ（非表示含む）を列挙し直し、
        /// 対象が無効なら「可視・有効・モーダル候補で、Owner 連鎖が対象に達する（ownerDisabled）か、同一スレッドの Owner なし候補（siblingDisabled）」を
        /// 無効化元として特定する。
        /// </summary>
        /// <param name="InWindowHandle">正規化済みのトップレベル HWND（10 進）。</param>
        /// <param name="InProcessIds">デバッグ中プロセス ID の集合。</param>
        /// <param name="OutState">評価結果。ウィンドウが列挙に無い場合は null。</param>
        /// <param name="OutWindow">対象の WindowInfo。列挙に無い場合は null。</param>
        /// <returns>エラーメッセージ（ウィンドウが消えている等）。正常なら null。</returns>
        public static string EvaluateModalState(long InWindowHandle, HashSet<uint> InProcessIds, out UiWindowModalState OutState, out WindowInfo OutWindow)
        {
            OutState = null;
            OutWindow = null;

            List<WindowInfo> TheWindows = DebuggeeWindowEnumerator.EnumerateTopLevelWindows(InProcessIds, true);
            WindowInfo TheTarget = TheWindows.FirstOrDefault(TheCandidate => TheCandidate.Handle == InWindowHandle);
            if (TheTarget == null)
            {
                return $"Window handle {InWindowHandle} no longer exists in the debugged process (it may have been closed).";
            }

            UiWindowModalState TheState = new UiWindowModalState
            {
                IsWindowEnabled = TheTarget.IsEnabled,
                IsWindowVisible = TheTarget.IsVisible,
                IsWindowModalCandidate = TheTarget.IsModalCandidate,
                Reason = ReasonInteractable,
            };

            if (!TheTarget.IsEnabled)
            {
                WindowInfo TheBlocker = FindBlockingModalCandidate(TheTarget, TheWindows);
                if (TheBlocker != null)
                {
                    TheState.IsBlockedByModal = true;
                    TheState.BlockingWindowHandle = TheBlocker.Handle;
                    TheState.BlockingWindowTitle = TheBlocker.Title;
                    TheState.BlockingModalCandidateReason = TheBlocker.ModalCandidateReason;
                    TheState.Reason = ReasonBlockedByModal;
                }
                else
                {
                    TheState.Reason = ReasonWindowDisabled;
                }
            }
            else if (!TheTarget.IsVisible)
            {
                TheState.Reason = ReasonWindowHidden;
            }

            OutState = TheState;
            OutWindow = TheTarget;
            return null;
        }

        /// <summary>
        /// 無効化された対象ウィンドウを塞いでいるモーダル候補を探す。A: Owner 連鎖に対象を含む候補（ownerDisabled）、
        /// B: 対象と同一プロセス・同一スレッドで Owner なしの候補（siblingDisabled）。A を優先する。
        /// </summary>
        /// <param name="InTarget">無効化されている対象ウィンドウ。</param>
        /// <param name="InWindows">列挙済みの全ウィンドウ。</param>
        /// <returns>塞いでいる候補。見つからなければ null。</returns>
        private static WindowInfo FindBlockingModalCandidate(WindowInfo InTarget, List<WindowInfo> InWindows)
        {
            List<WindowInfo> TheCandidates = InWindows
                .Where(TheWindow => TheWindow.IsVisible && TheWindow.IsEnabled && TheWindow.IsModalCandidate)
                .ToList();

            foreach (WindowInfo TheCandidate in TheCandidates)
            {
                if (IsOwnedBy(TheCandidate, InTarget.Handle, InWindows))
                {
                    return TheCandidate;
                }
            }

            return TheCandidates.FirstOrDefault(TheCandidate =>
                TheCandidate.OwnerHandle == 0
                && TheCandidate.ProcessId == InTarget.ProcessId
                && TheCandidate.ThreadId == InTarget.ThreadId);
        }

        /// <summary>候補の Owner 連鎖（Owner、Owner の Owner …）に指定 HWND が含まれるか。</summary>
        /// <param name="InCandidate">起点のウィンドウ。</param>
        /// <param name="InOwnerHandle">探す HWND。</param>
        /// <param name="InWindows">列挙済みの全ウィンドウ（Owner の Owner を引くため）。</param>
        /// <returns>含まれれば true。</returns>
        private static bool IsOwnedBy(WindowInfo InCandidate, long InOwnerHandle, List<WindowInfo> InWindows)
        {
            long TheOwner = InCandidate.OwnerHandle;
            for (int TheDepth = 0; TheOwner != 0 && TheDepth < _MAX_OWNER_CHAIN; TheDepth++)
            {
                if (TheOwner == InOwnerHandle)
                {
                    return true;
                }
                WindowInfo TheOwnerWindow = InWindows.FirstOrDefault(TheWindow => TheWindow.Handle == TheOwner);
                TheOwner = TheOwnerWindow == null ? 0 : TheOwnerWindow.OwnerHandle;
            }
            return false;
        }

        /// <summary>
        /// Action 実行直前の安全境界をまとめて通す: IsWindow → PID 再照合（ValidateAndNormalizeWindowHandle）→ モーダル状態。
        /// 操作不可（モーダルで無効化・無効・非表示）の場合は diagnostics 付きのエラーメッセージを返す。
        /// </summary>
        /// <param name="InWindow">正規化済みのトップレベル HWND。</param>
        /// <param name="InProcessIds">デバッグ中プロセス ID の集合。</param>
        /// <param name="OutWindow">対象の WindowInfo。エラー時は null のことがある。</param>
        /// <param name="OutState">モーダル状態。エラー時は null のことがある。</param>
        /// <returns>エラーメッセージ。操作してよければ null。</returns>
        public static string ValidateActionTarget(IntPtr InWindow, HashSet<uint> InProcessIds, out WindowInfo OutWindow, out UiWindowModalState OutState)
        {
            OutWindow = null;
            OutState = null;

            string TheHandleError = DebuggeeWindowResolver.ValidateAndNormalizeWindowHandle(InWindow.ToInt64(), InProcessIds, out IntPtr TheNormalized);
            if (TheHandleError != null)
            {
                return TheHandleError;
            }
            if (TheNormalized != InWindow)
            {
                return $"Window handle {InWindow.ToInt64()} is no longer a top-level window (it now resolves to {TheNormalized.ToInt64()}).";
            }

            string TheStateError = EvaluateModalState(InWindow.ToInt64(), InProcessIds, out OutState, out OutWindow);
            if (TheStateError != null)
            {
                return TheStateError;
            }

            if (OutState.IsInteractable)
            {
                return null;
            }
            return DescribeNotInteractable(OutWindow, OutState);
        }

        /// <summary>操作不可の理由をエラーメッセージ（diagnostics JSON 付き）にする。</summary>
        /// <param name="InTarget">対象ウィンドウ。</param>
        /// <param name="InState">モーダル状態。</param>
        /// <returns>エラーメッセージ。</returns>
        public static string DescribeNotInteractable(WindowInfo InTarget, UiWindowModalState InState)
        {
            string TheDiagnostics = JsonConvert.SerializeObject(new
            {
                targetWindow = new { handle = InTarget.Handle, title = InTarget.Title, isEnabled = InTarget.IsEnabled, isVisible = InTarget.IsVisible },
                blockingWindow = InState.IsBlockedByModal
                    ? new { handle = InState.BlockingWindowHandle, title = InState.BlockingWindowTitle }
                    : null,
                modalCandidateReason = InState.BlockingModalCandidateReason,
                reason = InState.Reason,
            }, Formatting.Indented);

            if (InState.IsBlockedByModal)
            {
                return $"Window {InTarget.Handle} ('{InTarget.Title}') is blocked by modal window {InState.BlockingWindowHandle} " +
                    $"('{InState.BlockingWindowTitle}', modalCandidateReason={InState.BlockingModalCandidateReason}). " +
                    "Interact with the modal window first. diagnostics:\n" + TheDiagnostics;
            }
            if (InState.Reason == ReasonWindowHidden)
            {
                return $"Window {InTarget.Handle} ('{InTarget.Title}') is not visible; refusing to interact with it. diagnostics:\n" + TheDiagnostics;
            }
            return $"Window {InTarget.Handle} ('{InTarget.Title}') is disabled and no modal candidate window was identified as the cause; " +
                "refusing to interact with it. diagnostics:\n" + TheDiagnostics;
        }

        /// <summary>
        /// 物理入力（座標クリック等）の直前に、点が対象ウィンドウの矩形内にあり、かつその点の最前面ウィンドウ（WindowFromPoint の GA_ROOT）が
        /// 対象ウィンドウであることを確認する。矩形の正本は指定された対象ウィンドウ。
        /// </summary>
        /// <param name="InWindow">対象のトップレベル HWND。</param>
        /// <param name="InX">スクリーン X（物理 px）。</param>
        /// <param name="InY">スクリーン Y（物理 px）。</param>
        /// <param name="InWhat">エラーメッセージで対象を示す語（例: "click point"）。</param>
        /// <returns>エラーメッセージ。正常なら null。</returns>
        public static string ValidatePhysicalPoint(IntPtr InWindow, int InX, int InY, string InWhat)
        {
            string TheBoundsError = UiTools.ValidateCoordinatesInWindow(InWindow, InX, InY);
            if (TheBoundsError != null)
            {
                return TheBoundsError;
            }

            long TheTopmost = ResolveTopmostRoot(InX, InY);
            if (TheTopmost != InWindow.ToInt64())
            {
                return DescribeCoveredPoint(InWindow, InX, InY, InWhat, TheTopmost);
            }
            return null;
        }

        /// <summary>
        /// 点の最前面ウィンドウのトップレベル HWND（WindowFromPoint の GA_ROOT）を返す。
        /// 座標ゲートの判定元はここ 1 か所にまとめ、<see cref="UiInteractionContext.PreparePhysicalPoint"/> からも使う。
        /// </summary>
        /// <param name="InX">スクリーン X（物理 px。負値も可）。</param>
        /// <param name="InY">スクリーン Y（物理 px。負値も可）。</param>
        /// <returns>トップレベル HWND（10 進）。取得できなければ 0。</returns>
        internal static long ResolveTopmostRoot(int InX, int InY)
        {
            return UiTools.WithDpiAwareness(() =>
            {
                IntPtr TheHit = WindowFromPoint(new POINT { X = InX, Y = InY });
                if (TheHit == IntPtr.Zero)
                {
                    return 0L;
                }
                IntPtr TheRoot = GetAncestor(TheHit, GA_ROOT);
                return (TheRoot == IntPtr.Zero ? TheHit : TheRoot).ToInt64();
            });
        }

        /// <summary>座標ゲートで「点が別のウィンドウに覆われている」ときのエラー文を組み立てる（Phase 7 と同文）。</summary>
        /// <param name="InWindow">対象のトップレベル HWND。</param>
        /// <param name="InX">スクリーン X（物理 px）。</param>
        /// <param name="InY">スクリーン Y（物理 px）。</param>
        /// <param name="InWhat">エラーメッセージで対象を示す語（例: "click point"）。</param>
        /// <param name="InTopmostHandle">その点の最前面のトップレベル HWND（10 進）。</param>
        /// <returns>エラーメッセージ。</returns>
        internal static string DescribeCoveredPoint(IntPtr InWindow, int InX, int InY, string InWhat, long InTopmostHandle)
        {
            return $"The {InWhat} ({InX}, {InY}) is covered by another window ({InTopmostHandle}), not by the target window {InWindow.ToInt64()}. " +
                "Bring the target window to the front or close the covering window (a popup or dialog) first.";
        }
    }
}
