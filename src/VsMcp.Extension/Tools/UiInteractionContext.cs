using System;
using System.Collections.Generic;
using static VsMcp.Extension.Tools.NativeMethods;

namespace VsMcp.Extension.Tools
{
    /// <summary>
    /// Extended: Action 系ツール（ui_window_* / ui_menu_*）が入力注入の直前に必ず通す共通パイプライン。
    /// Phase 7 で ui_window_* が個別に持っていた「ウィンドウ状態の再評価 → 要素解決と所属 HWND 検証 → enabled / offscreen / bounds →
    /// 前面化と WindowFromPoint の座標ゲート → カーソル復元」をここへ集約する。Geometry（DPI / モニター）は生成時と
    /// <see cref="RefreshGeometry"/> で取り直し、長時間キャッシュしない。
    /// AutomationElement はツール呼び出し間で保持しないため、この Context も 1 回の STA 呼び出しの中だけで使う。
    /// </summary>
    internal sealed class UiInteractionContext
    {
        /// <summary>SetForegroundWindow 後に描画・Z 順の反映を待つ時間（upstream / Phase 7 と同じ 100 ms）。</summary>
        private const int _FOREGROUND_SETTLE_MS = 100;

        /// <summary>操作対象の正規化済みトップレベル HWND。</summary>
        public IntPtr Window { get; private set; }

        /// <summary>デバッグ中プロセス ID の集合。</summary>
        public HashSet<uint> ProcessIds { get; private set; }

        /// <summary>対象ウィンドウの観測情報（再列挙した結果）。</summary>
        public WindowInfo WindowInfo { get; private set; }

        /// <summary>対象ウィンドウのモーダル状態（生成時に interactable であることを確認済み）。</summary>
        public UiWindowModalState ModalState { get; private set; }

        /// <summary>生成時に取得した Geometry（物理矩形 / DPI / モニター）。操作直前には RefreshGeometry で取り直す。</summary>
        public UiWindowGeometry Geometry { get; private set; }

        /// <summary>
        /// 物理入力の直前に WindowFromPoint の GA_ROOT として許容するトップレベル HWND の集合。
        /// 既定は対象ウィンドウのみ。ポップアップメニュー操作のように、対象以外の HWND が最前面でも正当な場合だけ追加する。
        /// </summary>
        public HashSet<long> AllowedPointRoots { get; private set; }

        /// <summary>
        /// 物理入力の直前に対象ウィンドウを SetForegroundWindow で前面化するか。既定は true（Phase 7 の ui_window_* と同じ挙動）。
        /// 既に最前面にあり、前面化すると取り下げられてしまう対象（開いているポップアップメニュー）を操作するときだけ false にする。
        /// false にしても WindowFromPoint による座標ゲートは必ず行う。
        /// </summary>
        public bool IsForegroundEnsured { get; set; } = true;

        /// <summary>外部からの直接生成を禁止する（TryCreate で安全境界を通したものだけを使う）。</summary>
        private UiInteractionContext()
        {
        }

        /// <summary>
        /// 安全境界（IsWindow → PID 再照合 → ウィンドウ再列挙 → モーダル状態の判定）を通してから Context を作る。
        /// 範囲検証と HWND の正規化は呼び出し側（ResolveWindowWithProcessesAsync）で済んでいる前提。STA スレッドで呼ぶこと。
        /// </summary>
        /// <param name="InWindow">正規化済みのトップレベル HWND。</param>
        /// <param name="InProcessIds">デバッグ中プロセス ID の集合。</param>
        /// <param name="OutContext">生成した Context。エラー時は null。</param>
        /// <returns>エラーメッセージ（Phase 7 の ValidateActionTarget と同文）。正常なら null。</returns>
        public static string TryCreate(IntPtr InWindow, HashSet<uint> InProcessIds, out UiInteractionContext OutContext)
        {
            OutContext = null;
            string TheTargetError = UiWindowActionValidator.ValidateActionTarget(InWindow, InProcessIds, out WindowInfo TheWindowInfo, out UiWindowModalState TheState);
            if (TheTargetError != null)
            {
                return TheTargetError;
            }

            OutContext = new UiInteractionContext
            {
                Window = InWindow,
                ProcessIds = InProcessIds,
                WindowInfo = TheWindowInfo,
                ModalState = TheState,
                Geometry = UiWindowGeometryResolver.Resolve(InWindow),
                AllowedPointRoots = new HashSet<long> { InWindow.ToInt64() },
            };
            return null;
        }

        /// <summary>
        /// Extended (Phase 9): 標準ダイアログ（MessageBox / TaskDialog / File Dialog）用の Context 生成。
        /// 安全境界は <see cref="TryCreate"/> とまったく同じ（IsWindow → PID 再照合 → 再列挙 → モーダル判定）で、
        /// ダイアログ自身はモーダル候補（IsWindowModalCandidate）であっても interactable として通る。
        /// EvaluateModalState は「他のモーダルによって塞がれているか」を対象自身の IsEnabled / IsVisible だけで判定するため、
        /// 可視かつ有効なダイアログ自身が「モーダルで塞がれている」と判定されることはない（Phase 9 §21）。
        /// 呼び出し側の意図（ダイアログ自身を操作する）を型で示すために別名で残している。
        /// Win32 API だけを使い UI Automation を使わないため、任意のスレッドから呼べる。
        /// </summary>
        /// <param name="InWindow">正規化済みのトップレベル HWND（ダイアログ自身）。</param>
        /// <param name="InProcessIds">デバッグ中プロセス ID の集合。</param>
        /// <param name="OutContext">生成した Context。エラー時は null。</param>
        /// <returns>エラーメッセージ（Phase 7 の ValidateActionTarget と同文）。正常なら null。</returns>
        public static string TryCreateForDialog(IntPtr InWindow, HashSet<uint> InProcessIds, out UiInteractionContext OutContext)
        {
            return TryCreate(InWindow, InProcessIds, out OutContext);
        }

        /// <summary>
        /// Extended (Phase 9): 操作を発行する直前に、対象ウィンドウが解決時と同じものであることを最小コストで確かめる
        /// （IsWindow → デバッグ対象 PID → トップレベルのまま → 可視 → 有効）。分類（ダイアログ種別 / メニュー種別）の
        /// 再判定は高価なので行わず、解決時の 1 回を正本にする。
        /// Win32 API だけを使い UI Automation を使わないため、任意のスレッドから呼べる。
        /// </summary>
        /// <param name="InExpectedClassification">エラー文に載せる対象の呼び名（例: "messageBox"、"file dialog"）。空なら付けない。</param>
        /// <returns>エラーメッセージ。操作してよければ null。</returns>
        public string ReverifyBeforeAction(string InExpectedClassification)
        {
            long TheHandle = Window.ToInt64();
            if (!IsWindow(Window))
            {
                return DescribeChangedWindow(TheHandle, "the window no longer exists", InExpectedClassification);
            }

            GetWindowThreadProcessId(Window, out uint TheProcessId);
            if (ProcessIds == null || !ProcessIds.Contains(TheProcessId))
            {
                return DescribeChangedWindow(TheHandle, $"it no longer belongs to a debugged process (processId {TheProcessId})", InExpectedClassification);
            }

            IntPtr TheRoot = GetAncestor(Window, GA_ROOT);
            if (TheRoot != IntPtr.Zero && TheRoot != Window)
            {
                return DescribeChangedWindow(TheHandle, $"it is no longer a top-level window (it now resolves to {TheRoot.ToInt64()})", InExpectedClassification);
            }
            if (!IsWindowVisible(Window))
            {
                return DescribeChangedWindow(TheHandle, "it is no longer visible", InExpectedClassification);
            }
            if (!IsWindowEnabled(Window))
            {
                return DescribeChangedWindow(TheHandle, "it is no longer enabled", InExpectedClassification);
            }
            return null;
        }

        /// <summary>操作直前の再検証に失敗したときのエラー文を組み立てる。</summary>
        /// <param name="InHandle">対象のトップレベル HWND（10 進）。</param>
        /// <param name="InDetail">変化した内容。</param>
        /// <param name="InExpectedClassification">対象の呼び名。空なら再取得の案内を付けない。</param>
        /// <returns>エラーメッセージ。</returns>
        private static string DescribeChangedWindow(long InHandle, string InDetail, string InExpectedClassification)
        {
            string TheMessage = $"Window {InHandle} changed before the action could run ({InDetail}); nothing was done.";
            if (string.IsNullOrEmpty(InExpectedClassification))
            {
                return TheMessage;
            }
            return TheMessage + $" Re-detect the {InExpectedClassification} and retry.";
        }

        /// <summary>
        /// 対象ウィンドウ配下で要素を一意に解決し、有効性（物理入力を伴う場合は IsOffscreen と bounds も）を確認する。
        /// Phase 7 の PrepareElementAction と同じ順序・同じ文言。STA スレッドで呼ぶこと。
        /// </summary>
        /// <param name="InSelector">要素セレクター。</param>
        /// <param name="InIsPhysical">物理入力（座標）を伴う操作か。</param>
        /// <param name="InRole">エラーメッセージの接頭辞（"Drag source" 等）。空なら付けない。</param>
        /// <param name="OutElement">解決した要素。エラー時は null。</param>
        /// <param name="OutFailure">要素を解決できなかった理由。正常時は None。</param>
        /// <returns>エラーメッセージ。正常なら null。</returns>
        public string ResolveElement(UiWindowElementSelector InSelector, bool InIsPhysical, string InRole,
            out UiWindowResolvedElement OutElement, out UiWindowResolveFailure OutFailure)
        {
            string ThePrefix = string.IsNullOrEmpty(InRole) ? string.Empty : InRole + ": ";
            string TheResolveError = UiWindowElementResolver.TryResolveSingle(Window, InSelector, out OutElement, out OutFailure);
            if (TheResolveError != null)
            {
                return ThePrefix + TheResolveError;
            }
            if (!OutElement.IsEnabled)
            {
                return $"{ThePrefix}Element with {InSelector.Describe()} is disabled (IsEnabled=false); refusing to act on it";
            }
            if (InIsPhysical)
            {
                string TheBoundsError = DescribePhysicalPrerequisite(OutElement, InSelector);
                if (TheBoundsError != null)
                {
                    return ThePrefix + TheBoundsError;
                }
            }
            return null;
        }

        /// <summary>座標を使う操作の前提（画面上にあり、bounds がある）を確認する。Phase 7 と同文言。</summary>
        /// <param name="InElement">解決済み要素。</param>
        /// <param name="InSelector">セレクター（メッセージ用）。</param>
        /// <returns>エラーメッセージ。正常なら null。</returns>
        public static string DescribePhysicalPrerequisite(UiWindowResolvedElement InElement, UiWindowElementSelector InSelector)
        {
            if (InElement.IsOffscreen)
            {
                return $"Element with {InSelector.Describe()} is offscreen (IsOffscreen=true); scroll it into view first";
            }
            if (InElement.Bounds.IsEmpty || InElement.Bounds.Width <= 0 || InElement.Bounds.Height <= 0)
            {
                return $"Element with {InSelector.Describe()} has no bounding rectangle; cannot perform a physical mouse action on it";
            }
            return null;
        }

        /// <summary>
        /// 物理入力の直前の座標ゲート: 対象ウィンドウの矩形内 →（<see cref="IsForegroundEnsured"/> が true なら）対象ウィンドウを
        /// 前面化して描画を待つ → その点の最前面（WindowFromPoint の GA_ROOT）が <see cref="AllowedPointRoots"/> のいずれかであること。
        /// 許可集合が対象ウィンドウだけのときは Phase 7 と完全に同じ判定・同じ文言になる。WindowFromPoint は 1 回だけ評価する。
        /// </summary>
        /// <param name="InX">スクリーン X（物理 px。負値も可）。</param>
        /// <param name="InY">スクリーン Y（物理 px。負値も可）。</param>
        /// <param name="InWhat">メッセージ用の点の呼び名。</param>
        /// <returns>エラーメッセージ。正常なら null。</returns>
        public string PreparePhysicalPoint(int InX, int InY, string InWhat)
        {
            string TheRectError = UiTools.ValidateCoordinatesInWindow(Window, InX, InY);
            if (TheRectError != null)
            {
                return TheRectError;
            }
            if (IsForegroundEnsured)
            {
                SetForegroundWindow(Window);
                System.Threading.Thread.Sleep(_FOREGROUND_SETTLE_MS);
            }

            long TheTopmost = UiWindowActionValidator.ResolveTopmostRoot(InX, InY);
            if (TheTopmost == Window.ToInt64() || AllowedPointRoots.Contains(TheTopmost))
            {
                // 対象ウィンドウ自身か、明示的に許可した別ウィンドウ（開いているポップアップメニュー等）が最前面なら通す
                return null;
            }
            return UiWindowActionValidator.DescribeCoveredPoint(Window, InX, InY, InWhat, TheTopmost);
        }

        /// <summary>物理入力の座標ゲートで最前面として許容する HWND を追加する。</summary>
        /// <param name="InHandle">許容するトップレベル HWND（10 進）。</param>
        public void AllowPointRoot(long InHandle)
        {
            if (InHandle != 0)
            {
                AllowedPointRoots.Add(InHandle);
            }
        }

        /// <summary>操作直前に Geometry（物理矩形 / DPI / モニター）を取り直す。別 DPI のモニターへ移動した場合に追従するため。</summary>
        /// <returns>取り直した Geometry。</returns>
        public UiWindowGeometry RefreshGeometry()
        {
            Geometry = UiWindowGeometryResolver.Resolve(Window);
            return Geometry;
        }

        /// <summary>現在のカーソル位置を保存する（物理 px）。</summary>
        /// <param name="OutPoint">保存したカーソル位置。取得できない場合は既定値。</param>
        /// <returns>取得できたら true。</returns>
        public bool TrySaveCursor(out POINT OutPoint)
        {
            POINT ThePoint = new POINT();
            bool IsSaved = UiTools.WithDpiAwareness(() => GetCursorPos(out ThePoint));
            OutPoint = ThePoint;
            return IsSaved;
        }

        /// <summary>保存しておいたカーソル位置へ戻す。</summary>
        /// <param name="InPoint">戻す位置（物理 px）。</param>
        public void RestoreCursor(POINT InPoint)
        {
            UiTools.WithDpiAwareness(() => SetCursorPos(InPoint.X, InPoint.Y));
        }
    }
}
