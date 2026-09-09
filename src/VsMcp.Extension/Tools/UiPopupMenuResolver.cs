using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using Newtonsoft.Json;
using static VsMcp.Extension.Tools.NativeMethods;

namespace VsMcp.Extension.Tools
{
    /// <summary>Extended: ui_menu_select がメニュー項目を一意に決めるためのセレクター。指定した条件を優先順に絞り込みへ使う。</summary>
    internal sealed class UiMenuItemSelector
    {
        /// <summary>Win32 メニューのコマンド ID。</summary>
        public int? ItemId { get; set; }

        /// <summary>UIA の AutomationId（完全一致）。</summary>
        public string AutomationId { get; set; }

        /// <summary>表示名（完全一致。アクセラレータの &amp; を除いた比較も行う）。</summary>
        public string Name { get; set; }

        /// <summary>メニュー内の 0 始まりの並び順（items[].index と同じ値）。他の条件と併用しても、絞り込み後の序数にはしない。</summary>
        public int? Index { get; set; }

        /// <summary>条件が 1 つでも指定されているか。</summary>
        public bool HasCriteria
        {
            get { return ItemId.HasValue || !string.IsNullOrEmpty(AutomationId) || !string.IsNullOrEmpty(Name) || Index.HasValue; }
        }

        /// <summary>エラーメッセージ用の説明文を組み立てる。</summary>
        /// <returns>例: itemId 1001, Name 'Win32 Item A'。</returns>
        public string Describe()
        {
            List<string> TheParts = new List<string>();
            if (ItemId.HasValue)
            {
                TheParts.Add($"itemId {ItemId.Value}");
            }
            if (!string.IsNullOrEmpty(AutomationId))
            {
                TheParts.Add($"AutomationId '{AutomationId}'");
            }
            if (!string.IsNullOrEmpty(Name))
            {
                TheParts.Add($"Name '{Name}'");
            }
            if (Index.HasValue)
            {
                TheParts.Add($"index {Index.Value}");
            }
            return string.Join(", ", TheParts);
        }
    }

    /// <summary>
    /// Extended: ui_menu_wait_closed が「同じ HWND が別のメニューに再利用された」ことを内容で見分けるための開始時スナップショット。
    /// HWND は閉じた直後に再利用されるため（Phase 7 実測）、IsWindow だけでは閉鎖を判定しない。
    /// </summary>
    internal sealed class UiMenuFingerprint
    {
        /// <summary>開始時のプロセス ID。</summary>
        [JsonProperty("processId")]
        public uint ProcessId { get; set; }

        /// <summary>開始時のウィンドウクラス名。</summary>
        [JsonProperty("className")]
        public string ClassName { get; set; }

        /// <summary>開始時の矩形 "x,y,width,height"。</summary>
        [JsonProperty("bounds")]
        public string Bounds { get; set; }

        /// <summary>開始時の UIA ルート名。</summary>
        [JsonProperty("uiaRootName")]
        public string UiaRootName { get; set; }

        /// <summary>開始時の項目数。</summary>
        [JsonProperty("itemCount")]
        public int ItemCount { get; set; }

        /// <summary>開始時の先頭項目の ID。無ければ null。</summary>
        [JsonProperty("firstItemId")]
        public int? FirstItemId { get; set; }

        /// <summary>開始時の先頭項目の表示名。無ければ null。</summary>
        [JsonProperty("firstItemName")]
        public string FirstItemName { get; set; }
    }

    /// <summary>
    /// Extended: デバッグ対象のトップレベルウィンドウが「ポップアップメニューか」を内容で分類し、項目一覧・項目解決・
    /// 閉鎖判定用の fingerprint を作る。クラス名だけで断定せず、UIA の Menu / MenuItem 構造を強い条件として使う。
    /// UIA を触るメソッドは STA スレッドで呼ぶこと。AutomationElement はツール呼び出し間で保持しない。
    /// </summary>
    internal static class UiPopupMenuResolver
    {
        /// <summary>WPF の ContextMenu（別トップレベル HWND のポップアップ）。</summary>
        public const string TypeWpfContextMenu = "wpfContextMenu";

        /// <summary>Win32 のポップアップメニュー（クラス #32768）。</summary>
        public const string TypeWin32Menu = "win32Menu";

        /// <summary>メニュー構造を特定できないポップアップ（includeUnknown=true のときだけ返す）。</summary>
        public const string TypeUnknownPopupMenu = "unknownPopupMenu";

        /// <summary>Win32 のポップアップメニューのウィンドウクラス名。</summary>
        public const string Win32MenuClassName = "#32768";

        /// <summary>WPF がホストするウィンドウのクラス名の接頭辞。</summary>
        public const string WpfWindowClassPrefix = "HwndWrapper[";

        /// <summary>分類できなかった理由: ウィンドウが可視でない。</summary>
        public const string ReasonWindowNotVisible = "windowNotVisible";

        /// <summary>分類できなかった理由: UI Automation のルートを開けない。</summary>
        public const string ReasonUiaRootUnavailable = "uiaRootUnavailable";

        /// <summary>分類できなかった理由: Menu / MenuItem が見つからず、ポップアップの特徴も無い。</summary>
        public const string ReasonNoMenuElements = "noMenuElements";

        /// <summary>項目が取得できなかったときに UiMenuInfo.Note へ入れる補足。</summary>
        public const string NoteNoItems = "menu items not found";

        /// <summary>LegacyIAccessiblePattern のパターン ID（managed UIA クライアントには型が無いため ID で引く）。</summary>
        private const int _LEGACY_IACCESSIBLE_PATTERN_ID = 10018;

        /// <summary>patterns へ載せる LegacyIAccessible の名前。</summary>
        private const string _PATTERN_LEGACY_IACCESSIBLE = "legacyIAccessible";

        /// <summary>1 つのメニューから集める項目数の上限。</summary>
        private const int _MAX_MENU_ITEMS = 200;

        /// <summary>#32768 の項目を UIA で照会する最大回数（初回照会が 0 件になる実測への対処）。</summary>
        private const int _MAX_UIA_MENU_ATTEMPTS = 3;

        /// <summary>UIA の再照会までの待ち時間（ミリ秒）。</summary>
        private const int _UIA_MENU_RETRY_MS = 150;

        /// <summary>bounds 文字列 "x,y,width,height" の要素数。</summary>
        private const int _BOUNDS_PART_COUNT = 4;

        /// <summary>GetMenuStringW に渡すバッファ長。</summary>
        private const int _MENU_STRING_BUFFER_LENGTH = 512;

        /// <summary>曖昧エラーに載せる候補の最大数。</summary>
        private const int _MAX_AMBIGUOUS_CANDIDATES = 20;

        /// <summary>分類結果が「操作できるメニュー」（win32Menu / wpfContextMenu）か。</summary>
        /// <param name="InMenuType">分類結果。</param>
        /// <returns>操作対象にしてよいメニューなら true。</returns>
        public static bool IsKnownMenuType(string InMenuType)
        {
            return string.Equals(InMenuType, TypeWin32Menu, StringComparison.Ordinal)
                || string.Equals(InMenuType, TypeWpfContextMenu, StringComparison.Ordinal);
        }

        /// <summary>
        /// ウィンドウの UIA 部分木（コントロールビュー）に Menu / MenuItem があるかを調べる。
        /// 要素が消えた・アクセスできない等の失敗は「該当なし」として扱う。ui_window_right_click の popupWindows と共有する。
        /// </summary>
        /// <param name="InHandle">調べるトップレベル HWND。</param>
        /// <param name="OutMenuItemCount">部分木に含まれる MenuItem の数。該当なしのときは 0。</param>
        /// <returns>Menu または MenuItem が 1 つ以上あれば true。</returns>
        public static bool HasMenuElements(IntPtr InHandle, out int OutMenuItemCount)
        {
            OutMenuItemCount = 0;
            try
            {
                AutomationElement TheRoot = AutomationElement.FromHandle(InHandle);
                if (TheRoot == null)
                {
                    return false;
                }
                Condition TheCondition = new AndCondition(
                    new PropertyCondition(AutomationElement.IsControlElementProperty, true),
                    new OrCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Menu),
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem)));
                AutomationElementCollection TheMatches = TheRoot.FindAll(TreeScope.Subtree, TheCondition);
                if (TheMatches == null || TheMatches.Count == 0)
                {
                    return false;
                }
                foreach (AutomationElement TheMatch in TheMatches)
                {
                    if (Equals(TheMatch.Current.ControlType, ControlType.MenuItem))
                    {
                        OutMenuItemCount++;
                    }
                }
                return true;
            }
            catch
            {
                // 消えたポップアップ・UIA が使えないウィンドウは「メニューではない」として扱う
                OutMenuItemCount = 0;
                return false;
            }
        }

        /// <summary>
        /// ウィンドウをポップアップメニューとして分類する（項目の AutomationElement は返さない版）。
        /// </summary>
        /// <param name="InWindow">分類するトップレベルウィンドウ。</param>
        /// <param name="OutMenu">分類できた場合のメニュー情報。できなければ null。</param>
        /// <param name="OutReason">分類できなかった理由。できた場合は null。</param>
        /// <returns>メニュー（unknownPopupMenu を含む）と判定できたら true。</returns>
        public static bool Classify(WindowInfo InWindow, out UiMenuInfo OutMenu, out string OutReason)
        {
            return Classify(InWindow, true, out OutMenu, out OutReason, out _);
        }

        /// <summary>
        /// ウィンドウをポップアップメニューとして分類する（項目照会のリトライ有無を指定できる版）。
        /// 「メニューかどうか」だけを知りたい大量判定（ui_window_right_click の menuHandles など）では
        /// InIsItemRetryEnabled=false にして、#32768 の項目照会リトライ（待ち時間）を省く。
        /// </summary>
        /// <param name="InWindow">分類するトップレベルウィンドウ。</param>
        /// <param name="InIsItemRetryEnabled">#32768 の項目照会が 0 件のときに待って再照会するか。</param>
        /// <param name="OutMenu">分類できた場合のメニュー情報。できなければ null。</param>
        /// <param name="OutReason">分類できなかった理由。できた場合は null。</param>
        /// <returns>メニュー（unknownPopupMenu を含む）と判定できたら true。</returns>
        public static bool Classify(WindowInfo InWindow, bool InIsItemRetryEnabled, out UiMenuInfo OutMenu, out string OutReason)
        {
            return Classify(InWindow, InIsItemRetryEnabled, out OutMenu, out OutReason, out _);
        }

        /// <summary>
        /// ウィンドウをポップアップメニューとして分類する（項目の AutomationElement も返す版）。
        /// </summary>
        /// <param name="InWindow">分類するトップレベルウィンドウ。</param>
        /// <param name="OutMenu">分類できた場合のメニュー情報。できなければ null。</param>
        /// <param name="OutReason">分類できなかった理由。できた場合は null。</param>
        /// <param name="OutItemElements">項目の AutomationElement（Items と同じ並び）。この呼び出しの中だけで使うこと。</param>
        /// <returns>メニュー（unknownPopupMenu を含む）と判定できたら true。</returns>
        public static bool Classify(WindowInfo InWindow, out UiMenuInfo OutMenu, out string OutReason, out List<AutomationElement> OutItemElements)
        {
            return Classify(InWindow, true, out OutMenu, out OutReason, out OutItemElements);
        }

        /// <summary>
        /// ウィンドウをポップアップメニューとして分類する。クラス名だけで断定せず、
        /// win32Menu は「#32768 かつ UIA ルートが Menu または直下に MenuItem がある」、
        /// wpfContextMenu は「HwndWrapper[... かつタイトルが空で、UIA 部分木に MenuItem が 1 件以上ある」を条件にする。
        /// どちらでもないが Owner 付き / タイトル空のツールウィンドウ（＝ポップアップの特徴）なら unknownPopupMenu とする。
        /// STA スレッドで呼ぶこと。
        /// </summary>
        /// <param name="InWindow">分類するトップレベルウィンドウ。</param>
        /// <param name="InIsItemRetryEnabled">#32768 の項目照会が 0 件のときに待って再照会するか。</param>
        /// <param name="OutMenu">分類できた場合のメニュー情報。できなければ null。</param>
        /// <param name="OutReason">分類できなかった理由。できた場合は null。</param>
        /// <param name="OutItemElements">項目の AutomationElement（Items と同じ並び）。この呼び出しの中だけで使うこと。</param>
        /// <returns>メニュー（unknownPopupMenu を含む）と判定できたら true。</returns>
        public static bool Classify(WindowInfo InWindow, bool InIsItemRetryEnabled, out UiMenuInfo OutMenu, out string OutReason,
            out List<AutomationElement> OutItemElements)
        {
            OutMenu = null;
            OutReason = null;
            OutItemElements = new List<AutomationElement>();

            if (!InWindow.IsVisible)
            {
                OutReason = ReasonWindowNotVisible;
                return false;
            }

            IntPtr TheHandle = new IntPtr(InWindow.Handle);
            AutomationElement TheRoot = TryGetRoot(TheHandle);

            string TheMenuType = null;
            string TheNote = null;
            List<AutomationElement> TheItemElements = new List<AutomationElement>();
            List<UiMenuItemInfo> TheWin32OnlyItems = null;
            IntPtr TheWin32MenuHandle = IntPtr.Zero;

            // クラス名の判定は必ず Win32 の GetClassName（WindowInfo.ClassName）を使う。
            // WPF の ContextMenu ポップアップは Win32 では HwndWrapper[...] だが UIA の Current.ClassName は "Popup" になるため、
            // UIA 側の ClassName は分類に使わない（実測）。
            if (string.Equals(InWindow.ClassName, Win32MenuClassName, StringComparison.Ordinal))
            {
                // #32768 は AutomationElement.FromHandle の直後、初回照会で root が ControlType.Window / MenuItem 0 件に
                // なることがある（実測。再照会すると ControlType.Menu / 項目あり）ため、0 件なら待って照会し直す。
                TheItemElements = CollectWin32MenuItemsWithRetry(TheHandle, ref TheRoot,
                    InIsItemRetryEnabled ? _MAX_UIA_MENU_ATTEMPTS : 1, out int TheAttemptCount);
                TheWin32MenuHandle = ReadWin32MenuHandle(TheHandle);
                if (TheItemElements.Count > 0)
                {
                    TheMenuType = TypeWin32Menu;
                }
                else
                {
                    // UIA から項目が取れないときは Win32 のメニュー API だけで項目を組み立て、その経緯を Note に残す
                    TheWin32OnlyItems = BuildItemsFromWin32(TheWin32MenuHandle, InWindow.Handle);
                    if (TheWin32OnlyItems.Count > 0)
                    {
                        TheMenuType = TypeWin32Menu;
                        TheNote = $"items from win32 menu api (uia returned 0 items after {TheAttemptCount} attempts)";
                    }
                    else if (string.Equals(TryReadRootControlType(TheRoot), ControlType.Menu.ProgrammaticName, StringComparison.Ordinal))
                    {
                        // 項目が 1 つも無い空のメニュー
                        TheMenuType = TypeWin32Menu;
                    }
                }
            }
            else if (TheRoot != null
                && InWindow.ClassName != null && InWindow.ClassName.StartsWith(WpfWindowClassPrefix, StringComparison.Ordinal)
                && string.IsNullOrEmpty(InWindow.Title))
            {
                // タイトルを持つ WPF ウィンドウ（メニューバーを持つメインウィンドウ等）をコンテキストメニューと誤認しないため、
                // タイトルが空であることも条件にする。
                // Extended: WPF のサブメニューは ControlType.Menu を持たず、UIA ルートが ControlType.Window / ClassName "Popup" の
                // 別トップレベル HWND として現れる（Phase 8 実測）ため、Menu 要素の存在は条件にしない
                List<AutomationElement> TheFirstLevelItems = CollectFirstLevelMenuItems(TheRoot);
                if (TheFirstLevelItems.Count > 0)
                {
                    TheMenuType = TypeWpfContextMenu;
                    TheItemElements = TheFirstLevelItems;
                }
            }

            if (TheMenuType == null)
            {
                if (!IsPopupWindowCandidate(InWindow))
                {
                    OutReason = TheRoot == null ? ReasonUiaRootUnavailable : ReasonNoMenuElements;
                    return false;
                }
                TheMenuType = TypeUnknownPopupMenu;
                TheItemElements = new List<AutomationElement>();
            }

            UiMenuInfo TheMenu = UiMenuInfo.FromWindow(TheMenuType, InWindow);
            TheMenu.UiaRootName = TryReadRootName(TheRoot);
            TheMenu.UiaRootControlType = TryReadRootControlType(TheRoot);
            TheMenu.Items = TheWin32OnlyItems ?? BuildItems(TheItemElements);
            if (string.Equals(TheMenuType, TypeWin32Menu, StringComparison.Ordinal))
            {
                TheMenu.Win32MenuHandle = TheWin32MenuHandle.ToInt64();
                if (TheWin32OnlyItems == null)
                {
                    // UIA から取れた項目に、UIA では分からない値（コマンド ID / 無効・チェック状態 / サブメニュー）だけを Win32 で補う
                    ApplyWin32MenuDetails(TheWin32MenuHandle, TheMenu);
                }
            }
            TheMenu.Note = TheNote ?? (TheMenu.Items.Count == 0 ? NoteNoItems : null);

            OutMenu = TheMenu;
            OutItemElements = TheItemElements;
            return true;
        }

        /// <summary>
        /// セレクターに合うメニュー項目を 1 件に決める。優先順は itemId → automationId → name → index で、
        /// 指定された条件をすべて満たす候補を絞り込む。複数残って index の指定も無ければ候補一覧付きのエラーにする。
        /// </summary>
        /// <param name="InMenu">対象メニュー。</param>
        /// <param name="InSelector">セレクター。</param>
        /// <param name="OutItem">決まった項目。エラー時は null。</param>
        /// <returns>エラーメッセージ。正常なら null。</returns>
        /// <summary>
        /// セレクターに合うメニュー項目を 1 件に決める。itemId → automationId → name → index の順に、指定された条件を
        /// すべて満たす候補へ絞り込む。index は常に items[].index（メニュー内の並び順）との一致で絞るので、
        /// 他の条件と併用しても「絞り込み後の序数」にはならない。複数残ったままなら候補一覧付きのエラーにする。
        /// itemId は id を観測できた項目にだけ一致させる（id が null の項目＝サブメニューを開く項目や WPF の項目は、
        /// itemId 指定では決して選ばれない）。
        /// </summary>
        /// <param name="InMenu">対象メニュー。</param>
        /// <param name="InSelector">セレクター。</param>
        /// <param name="OutItem">決まった項目。エラー時は null。</param>
        /// <returns>エラーメッセージ。正常なら null。</returns>
        public static string TrySelectItem(UiMenuInfo InMenu, UiMenuItemSelector InSelector, out UiMenuItemInfo OutItem)
        {
            OutItem = null;
            if (!InSelector.HasCriteria)
            {
                return "At least one menu item selector must be provided (itemId, automationId, name, or index)";
            }

            List<UiMenuItemInfo> TheCandidates = new List<UiMenuItemInfo>(InMenu.Items);
            if (InSelector.ItemId.HasValue)
            {
                TheCandidates = TheCandidates.FindAll(TheItem => TheItem.Id.HasValue && TheItem.Id.Value == InSelector.ItemId.Value);
            }
            if (!string.IsNullOrEmpty(InSelector.AutomationId))
            {
                TheCandidates = TheCandidates.FindAll(TheItem => string.Equals(TheItem.AutomationId, InSelector.AutomationId, StringComparison.Ordinal));
            }
            if (!string.IsNullOrEmpty(InSelector.Name))
            {
                TheCandidates = TheCandidates.FindAll(TheItem => IsNameMatched(TheItem.Name, InSelector.Name));
            }
            if (InSelector.Index.HasValue)
            {
                TheCandidates = TheCandidates.FindAll(TheItem => TheItem.Index == InSelector.Index.Value);
            }

            if (TheCandidates.Count == 0)
            {
                return $"Menu item with {InSelector.Describe()} not found in menu {InMenu.Handle}. Items:\n" + DescribeItems(InMenu.Items);
            }

            if (TheCandidates.Count > 1)
            {
                return $"{TheCandidates.Count} menu items match {InSelector.Describe()} in menu {InMenu.Handle}. " +
                    "Add more selector criteria (itemId / automationId / name) or pass 'index' to pick one. Candidates:\n" + DescribeItems(TheCandidates);
            }

            OutItem = TheCandidates[0];
            return null;
        }

        /// <summary>閉鎖判定用の開始時スナップショットを作る。</summary>
        /// <param name="InMenu">開始時のメニュー情報。</param>
        /// <returns>fingerprint。</returns>
        public static UiMenuFingerprint BuildFingerprint(UiMenuInfo InMenu)
        {
            UiMenuItemInfo TheFirstItem = InMenu.Items.Count > 0 ? InMenu.Items[0] : null;
            return new UiMenuFingerprint
            {
                ProcessId = InMenu.ProcessId,
                ClassName = InMenu.ClassName,
                Bounds = InMenu.Bounds,
                UiaRootName = InMenu.UiaRootName,
                ItemCount = InMenu.Items.Count,
                FirstItemId = TheFirstItem?.Id,
                FirstItemName = TheFirstItem?.Name,
            };
        }

        /// <summary>現在のメニューが開始時と同じ内容か（項目数と先頭項目で判定する）。</summary>
        /// <param name="InFingerprint">開始時のスナップショット。</param>
        /// <param name="InMenu">現在のメニュー情報。</param>
        /// <returns>同じ内容とみなせるなら true。</returns>
        /// <summary>
        /// 現在のメニューが開始時と同じ内容か。クラス名・矩形・項目数・先頭項目で判定する。
        /// 表示中のポップアップメニューは移動もリサイズもしないため、矩形が変われば同じ HWND で別のメニューが開き直されたとみなす。
        /// </summary>
        /// <param name="InFingerprint">開始時のスナップショット。</param>
        /// <param name="InMenu">現在のメニュー情報。</param>
        /// <returns>同じ内容とみなせるなら true。</returns>
        public static bool IsSameMenu(UiMenuFingerprint InFingerprint, UiMenuInfo InMenu)
        {
            if (!string.Equals(InFingerprint.ClassName, InMenu.ClassName, StringComparison.Ordinal))
            {
                return false;
            }
            if (!string.Equals(InFingerprint.Bounds, InMenu.Bounds, StringComparison.Ordinal))
            {
                return false;
            }
            if (InFingerprint.ItemCount != InMenu.Items.Count)
            {
                return false;
            }
            UiMenuItemInfo TheFirstItem = InMenu.Items.Count > 0 ? InMenu.Items[0] : null;
            int? TheFirstId = TheFirstItem?.Id;
            string TheFirstName = TheFirstItem?.Name;
            return TheFirstId == InFingerprint.FirstItemId && string.Equals(TheFirstName, InFingerprint.FirstItemName, StringComparison.Ordinal);
        }

        /// <summary>物理クリックの前提（画面上にあり、bounds がある）を確認する。</summary>
        /// <param name="InMenu">対象メニュー。</param>
        /// <param name="InItem">対象項目。</param>
        /// <returns>エラーメッセージ。正常なら null。</returns>
        public static string DescribePhysicalPrerequisite(UiMenuInfo InMenu, UiMenuItemInfo InItem)
        {
            if (InItem.IsOffscreen)
            {
                return $"Menu item '{InItem.Name}' in menu {InMenu.Handle} is offscreen (IsOffscreen=true); cannot click it";
            }
            if (!TryGetBoundsCenter(InItem.Bounds, out _, out _))
            {
                return $"Menu item '{InItem.Name}' in menu {InMenu.Handle} has no bounding rectangle; cannot perform a physical mouse action on it";
            }
            return null;
        }

        /// <summary>"x,y,width,height" 形式の矩形文字列から中心座標（物理 px）を求める。</summary>
        /// <param name="InBounds">矩形文字列。</param>
        /// <param name="OutX">中心 X。</param>
        /// <param name="OutY">中心 Y。</param>
        /// <returns>幅・高さが正の矩形として解釈できたら true。</returns>
        public static bool TryGetBoundsCenter(string InBounds, out int OutX, out int OutY)
        {
            OutX = 0;
            OutY = 0;
            if (string.IsNullOrEmpty(InBounds))
            {
                return false;
            }
            string[] TheParts = InBounds.Split(',');
            if (TheParts.Length != _BOUNDS_PART_COUNT)
            {
                return false;
            }
            if (!int.TryParse(TheParts[0], out int TheLeft) || !int.TryParse(TheParts[1], out int TheTop)
                || !int.TryParse(TheParts[2], out int TheWidth) || !int.TryParse(TheParts[3], out int TheHeight))
            {
                return false;
            }
            if (TheWidth <= 0 || TheHeight <= 0)
            {
                return false;
            }
            OutX = TheLeft + TheWidth / 2;
            OutY = TheTop + TheHeight / 2;
            return true;
        }

        /// <summary>InvokePattern で項目を実行する。</summary>
        /// <param name="InElement">項目の要素。</param>
        /// <returns>実行できたら true。</returns>
        public static bool TryInvoke(AutomationElement InElement)
        {
            try
            {
                if (!InElement.TryGetCurrentPattern(InvokePattern.Pattern, out object ThePattern))
                {
                    return false;
                }
                ((InvokePattern)ThePattern).Invoke();
                return true;
            }
            catch
            {
                // 実行中に項目が消えた場合は次の方式へフォールバックする
                return false;
            }
        }

        /// <summary>ExpandCollapsePattern でサブメニューを開く。</summary>
        /// <param name="InElement">項目の要素。</param>
        /// <returns>開けたら true。</returns>
        public static bool TryExpand(AutomationElement InElement)
        {
            try
            {
                if (!InElement.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out object ThePattern))
                {
                    return false;
                }
                ((ExpandCollapsePattern)ThePattern).Expand();
                return true;
            }
            catch
            {
                // 展開できない項目は次の方式へフォールバックする
                return false;
            }
        }

        /// <summary>
        /// LegacyIAccessiblePattern.DoDefaultAction で項目を実行する。net48 の managed UI Automation クライアントは
        /// このパターンの型を公開していないため、パターン ID で引けなければ何もせず false を返し、物理クリックへフォールバックする。
        /// </summary>
        /// <param name="InElement">項目の要素。</param>
        /// <returns>実行できたら true。</returns>
        public static bool TryLegacyDoDefaultAction(AutomationElement InElement)
        {
            if (!TryGetLegacyIAccessiblePattern(InElement, out object ThePattern))
            {
                return false;
            }
            try
            {
                ThePattern.GetType().InvokeMember("DoDefaultAction", BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance,
                    null, ThePattern, null);
                return true;
            }
            catch
            {
                // パターンはあるが呼べない場合は物理クリックへフォールバックする
                return false;
            }
        }

        // ------------------------------------------------------------------
        // 内部 helper
        // ------------------------------------------------------------------

        /// <summary>HWND を UI Automation のルート要素として開く。開けなければ null。</summary>
        /// <param name="InHandle">対象のトップレベル HWND。</param>
        /// <returns>ルート要素。開けなければ null。</returns>
        private static AutomationElement TryGetRoot(IntPtr InHandle)
        {
            try
            {
                return AutomationElement.FromHandle(InHandle);
            }
            catch
            {
                // 閉じた直後のポップアップは UIA ルートを開けない
                return null;
            }
        }

        /// <summary>ルート要素の Name を読む。読めなければ null。</summary>
        /// <param name="InRoot">ルート要素。</param>
        /// <returns>Name。</returns>
        private static string TryReadRootName(AutomationElement InRoot)
        {
            try
            {
                return InRoot?.Current.Name;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>ルート要素の ControlType を読む。読めなければ null。</summary>
        /// <param name="InRoot">ルート要素。</param>
        /// <returns>ControlType のプログラム名。</returns>
        private static string TryReadRootControlType(AutomationElement InRoot)
        {
            try
            {
                return InRoot?.Current.ControlType?.ProgrammaticName;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>部分木から最初の Menu 要素を探す（ルート自身が Menu ならそれを返す）。</summary>
        /// <param name="InRoot">探索の起点。</param>
        /// <returns>Menu 要素。無ければ null。</returns>
        private static AutomationElement FindMenuElement(AutomationElement InRoot)
        {
            try
            {
                if (Equals(InRoot.Current.ControlType, ControlType.Menu))
                {
                    return InRoot;
                }
                Condition TheCondition = new AndCondition(
                    new PropertyCondition(AutomationElement.IsControlElementProperty, true),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Menu));
                return InRoot.FindFirst(TreeScope.Descendants, TheCondition);
            }
            catch
            {
                // 消えた要素・UIA が使えないウィンドウは「Menu なし」として扱う
                return null;
            }
        }

        /// <summary>指定ビューで直下の MenuItem を並び順に集める。</summary>
        /// <param name="InParent">親要素（null なら空）。</param>
        /// <param name="InWalker">走査に使うビュー（ControlView / RawView）。</param>
        /// <returns>MenuItem の一覧。</returns>
        private static List<AutomationElement> CollectMenuItemChildren(AutomationElement InParent, TreeWalker InWalker)
        {
            List<AutomationElement> TheItems = new List<AutomationElement>();
            if (InParent == null)
            {
                return TheItems;
            }
            try
            {
                AutomationElement TheChild = InWalker.GetFirstChild(InParent);
                while (TheChild != null && TheItems.Count < _MAX_MENU_ITEMS)
                {
                    if (Equals(TheChild.Current.ControlType, ControlType.MenuItem))
                    {
                        TheItems.Add(TheChild);
                    }
                    TheChild = InWalker.GetNextSibling(TheChild);
                }
            }
            catch
            {
                // 走査中に閉じたメニューは、そこまでに集めた分を返す
            }
            return TheItems;
        }

        /// <summary>
        /// Extended: ポップアップのルート配下から「そのポップアップの第 1 階層の MenuItem」を並び順に集める。
        /// 親メニューがサブメニューを展開している間は、サブメニューの項目が親の MenuItem の子としても部分木に現れ、
        /// 項目数が一時的に倍増する（Phase 8 実測: 4 → 8）ため、祖先に MenuItem を持つ要素は除外して二重に数えない。
        /// </summary>
        /// <param name="InParent">ポップアップの UIA ルート要素（null なら空）。</param>
        /// <returns>第 1 階層の MenuItem の一覧。無ければ空。</returns>
        private static List<AutomationElement> CollectFirstLevelMenuItems(AutomationElement InParent)
        {
            List<AutomationElement> TheItems = new List<AutomationElement>();
            if (InParent == null)
            {
                return TheItems;
            }
            try
            {
                Condition TheCondition = new AndCondition(
                    new PropertyCondition(AutomationElement.IsControlElementProperty, true),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem));
                AutomationElementCollection TheMatches = InParent.FindAll(TreeScope.Subtree, TheCondition);
                if (TheMatches == null)
                {
                    return TheItems;
                }
                foreach (AutomationElement TheMatch in TheMatches)
                {
                    if (TheItems.Count >= _MAX_MENU_ITEMS)
                    {
                        break;
                    }
                    if (HasMenuItemAncestor(TheMatch, InParent))
                    {
                        continue;
                    }
                    TheItems.Add(TheMatch);
                }
            }
            catch
            {
                // 走査中に閉じたポップアップは、そこまでに集めた分を返す
            }
            return TheItems;
        }

        /// <summary>Extended: 要素の祖先（ポップアップのルートまで）に MenuItem があるか調べる。</summary>
        /// <param name="InElement">調べる要素。</param>
        /// <param name="InRoot">走査を打ち切るポップアップのルート要素。</param>
        /// <returns>祖先に MenuItem があれば true。</returns>
        private static bool HasMenuItemAncestor(AutomationElement InElement, AutomationElement InRoot)
        {
            try
            {
                AutomationElement TheAncestor = TreeWalker.ControlViewWalker.GetParent(InElement);
                while (TheAncestor != null && !Automation.Compare(TheAncestor, InRoot))
                {
                    if (Equals(TheAncestor.Current.ControlType, ControlType.MenuItem))
                    {
                        return true;
                    }
                    TheAncestor = TreeWalker.ControlViewWalker.GetParent(TheAncestor);
                }
                return false;
            }
            catch
            {
                // 祖先をたどれない要素は第 1 階層と断定できないので、項目には採用しない
                return true;
            }
        }

        /// <summary>
        /// 「メニュー構造は特定できないが、ポップアップらしい」ウィンドウか判定する。
        /// Owner 付きまたはタイトル空で、拡張スタイルに WS_EX_TOOLWINDOW を持つ可視ウィンドウを候補とする。
        /// </summary>
        /// <param name="InWindow">判定するウィンドウ。</param>
        /// <returns>候補なら true。</returns>
        private static bool IsPopupWindowCandidate(WindowInfo InWindow)
        {
            if (!InWindow.IsVisible)
            {
                return false;
            }
            if (InWindow.OwnerHandle == 0 && !string.IsNullOrEmpty(InWindow.Title))
            {
                return false;
            }
            int TheExStyle = GetWindowLongW(new IntPtr(InWindow.Handle), GWL_EXSTYLE);
            return (TheExStyle & WS_EX_TOOLWINDOW) != 0;
        }

        /// <summary>UIA の項目要素から戻り値用の項目情報を組み立てる。取得できない値は null のままにする。</summary>
        /// <param name="InElements">項目要素の一覧（並び順）。</param>
        /// <returns>項目情報の一覧。</returns>
        private static List<UiMenuItemInfo> BuildItems(List<AutomationElement> InElements)
        {
            List<UiMenuItemInfo> TheItems = new List<UiMenuItemInfo>();
            for (int TheIndex = 0; TheIndex < InElements.Count; TheIndex++)
            {
                UiMenuItemInfo TheItem = new UiMenuItemInfo { Index = TheIndex };
                AutomationElement TheElement = InElements[TheIndex];
                try
                {
                    TheItem.Name = TheElement.Current.Name;
                    TheItem.AutomationId = TheElement.Current.AutomationId;
                    TheItem.ControlType = TheElement.Current.ControlType?.ProgrammaticName;
                    TheItem.IsEnabled = TheElement.Current.IsEnabled;
                    TheItem.IsOffscreen = TheElement.Current.IsOffscreen;
                    Rect TheRect = TheElement.Current.BoundingRectangle;
                    TheItem.Bounds = TheRect.IsEmpty ? null : $"{(int)TheRect.X},{(int)TheRect.Y},{(int)TheRect.Width},{(int)TheRect.Height}";
                    TheItem.RootWindowHandle = UiWindowElementResolver.ResolveTopLevelHandle(TheElement);
                    TheItem.Patterns = BuildPatternNames(TheElement);
                    TheItem.IsChecked = ReadToggleState(TheElement);
                    TheItem.HasSubmenu = TheElement.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out _);
                }
                catch
                {
                    // 読み取り中に消えた項目は、取得できた分だけを返す（推測で埋めない）
                }
                TheItems.Add(TheItem);
            }
            return TheItems;
        }

        /// <summary>項目が対応するパターン名を集める（LegacyIAccessible が使える場合はそれも載せる）。</summary>
        /// <param name="InElement">項目要素。</param>
        /// <returns>パターン名の一覧。</returns>
        private static List<string> BuildPatternNames(AutomationElement InElement)
        {
            List<string> ThePatterns = UiWindowElementResolver.GetPatternNames(InElement);
            if (TryGetLegacyIAccessiblePattern(InElement, out _))
            {
                ThePatterns.Add(_PATTERN_LEGACY_IACCESSIBLE);
            }
            return ThePatterns;
        }

        /// <summary>TogglePattern からチェック状態を読む。パターンが無ければ null（推測しない）。</summary>
        /// <param name="InElement">項目要素。</param>
        /// <returns>チェック状態。判定できなければ null。</returns>
        private static bool? ReadToggleState(AutomationElement InElement)
        {
            try
            {
                if (!InElement.TryGetCurrentPattern(TogglePattern.Pattern, out object ThePattern))
                {
                    return null;
                }
                return ((TogglePattern)ThePattern).Current.ToggleState == ToggleState.On;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>LegacyIAccessiblePattern をパターン ID で取得する。managed UIA クライアントが公開していなければ false。</summary>
        /// <param name="InElement">項目要素。</param>
        /// <param name="OutPattern">取得したパターンオブジェクト。</param>
        /// <returns>取得できたら true。</returns>
        private static bool TryGetLegacyIAccessiblePattern(AutomationElement InElement, out object OutPattern)
        {
            OutPattern = null;
            try
            {
                AutomationPattern ThePattern = AutomationPattern.LookupById(_LEGACY_IACCESSIBLE_PATTERN_ID);
                if (ThePattern == null)
                {
                    return false;
                }
                return InElement.TryGetCurrentPattern(ThePattern, out OutPattern);
            }
            catch
            {
                // 未登録のパターン ID / 消えた要素は「使えない」として扱う
                OutPattern = null;
                return false;
            }
        }

        /// <summary>
        /// #32768 の項目を UIA で集める。初回照会で 0 件になることがある（実測）ため、0 件なら待って
        /// AutomationElement.FromHandle からやり直す。ルート要素も取り直した値へ更新する。
        /// </summary>
        /// <param name="InHandle">メニューウィンドウの HWND。</param>
        /// <param name="InOutRoot">UIA ルート要素。取り直せた場合は新しい要素で置き換える。</param>
        /// <param name="InMaxAttemptCount">照会の最大回数。1 なら待ち時間なしで 1 回だけ照会する。</param>
        /// <param name="OutAttemptCount">実際に照会した回数。</param>
        /// <returns>項目要素の一覧。取れなければ空。</returns>
        private static List<AutomationElement> CollectWin32MenuItemsWithRetry(IntPtr InHandle, ref AutomationElement InOutRoot, int InMaxAttemptCount,
            out int OutAttemptCount)
        {
            List<AutomationElement> TheItems = new List<AutomationElement>();
            OutAttemptCount = 0;
            for (int TheAttempt = 0; TheAttempt < InMaxAttemptCount; TheAttempt++)
            {
                if (TheAttempt > 0)
                {
                    Thread.Sleep(_UIA_MENU_RETRY_MS);
                }
                OutAttemptCount = TheAttempt + 1;

                AutomationElement TheRoot = TryGetRoot(InHandle);
                if (TheRoot == null)
                {
                    continue;
                }
                InOutRoot = TheRoot;

                AutomationElement TheMenuElement = FindMenuElement(TheRoot) ?? TheRoot;
                TheItems = CollectMenuItemChildren(TheMenuElement, TreeWalker.ControlViewWalker);
                if (TheItems.Count == 0)
                {
                    // MSAA ブリッジ経由の #32768 は、項目がコントロールビューに出ないことがあるので RawView も見る
                    TheItems = CollectMenuItemChildren(TheMenuElement, TreeWalker.RawViewWalker);
                }
                if (TheItems.Count > 0)
                {
                    break;
                }
            }
            return TheItems;
        }

        /// <summary>ポップアップメニューウィンドウ（#32768）から HMENU を取得する。</summary>
        /// <param name="InMenuWindow">メニューウィンドウの HWND。</param>
        /// <returns>HMENU。取得できなければ IntPtr.Zero。</returns>
        private static IntPtr ReadWin32MenuHandle(IntPtr InMenuWindow)
        {
            if (!StandardDialogResolver.TrySendMessage(InMenuWindow, MN_GETHMENU, IntPtr.Zero, IntPtr.Zero, out IntPtr TheMenuHandle))
            {
                return IntPtr.Zero;
            }
            return TheMenuHandle;
        }

        /// <summary>
        /// UIA から項目が取れないときに、HMENU（GetMenuItemCount / GetMenuItemInfoW / GetMenuStringW）だけで項目一覧を組み立てる。
        /// UIA でしか分からない値（AutomationId / ControlType / bounds / patterns）は null のままにする。セパレータは項目に含めない。
        /// </summary>
        /// <param name="InMenuHandle">HMENU。</param>
        /// <param name="InWindowHandle">メニューウィンドウの HWND（10 進）。</param>
        /// <returns>項目一覧。取得できなければ空。</returns>
        private static List<UiMenuItemInfo> BuildItemsFromWin32(IntPtr InMenuHandle, long InWindowHandle)
        {
            List<UiMenuItemInfo> TheItems = new List<UiMenuItemInfo>();
            if (InMenuHandle == IntPtr.Zero)
            {
                return TheItems;
            }

            int TheCount = GetMenuItemCount(InMenuHandle);
            for (int ThePosition = 0; ThePosition < TheCount && TheItems.Count < _MAX_MENU_ITEMS; ThePosition++)
            {
                if (!TryReadMenuItemInfo(InMenuHandle, ThePosition, out MENUITEMINFOW TheInfo))
                {
                    continue;
                }
                if ((TheInfo.fType & MFT_SEPARATOR) != 0)
                {
                    continue;
                }
                bool HasSubmenu = TheInfo.hSubMenu != IntPtr.Zero;
                TheItems.Add(new UiMenuItemInfo
                {
                    Index = TheItems.Count,
                    // Extended: サブメニューを持つ項目の wID には hSubMenu の値が入る（Phase 8 実測）ため、コマンド ID として採用しない
                    Id = HasSubmenu ? (int?)null : (int)TheInfo.wID,
                    Name = ReadMenuItemText(InMenuHandle, ThePosition),
                    IsEnabled = (TheInfo.fState & MFS_DISABLED) == 0,
                    IsChecked = (TheInfo.fState & MFS_CHECKED) != 0,
                    HasSubmenu = HasSubmenu,
                    RootWindowHandle = InWindowHandle,
                });
            }
            return TheItems;
        }

        /// <summary>
        /// Win32 メニュー（#32768）の HMENU から、UIA では取れない情報（コマンド ID / 無効・チェック状態 / サブメニュー）を補う。
        /// UIA 側で取得できている値は上書きしない。セパレータは UIA の項目に現れないので読み飛ばし、
        /// 双方に表示名がある項目は名前が一致する場合だけ値を補う（並びがずれたまま ID を付けないため）。
        /// </summary>
        /// <param name="InMenuHandle">HMENU。IntPtr.Zero なら何もしない。</param>
        /// <param name="InOutMenu">補完対象のメニュー情報。</param>
        private static void ApplyWin32MenuDetails(IntPtr InMenuHandle, UiMenuInfo InOutMenu)
        {
            if (InMenuHandle == IntPtr.Zero)
            {
                return;
            }
            int TheCount = GetMenuItemCount(InMenuHandle);
            if (TheCount <= 0)
            {
                return;
            }

            int TheItemIndex = 0;
            for (int ThePosition = 0; ThePosition < TheCount && TheItemIndex < InOutMenu.Items.Count; ThePosition++)
            {
                if (!TryReadMenuItemInfo(InMenuHandle, ThePosition, out MENUITEMINFOW TheInfo))
                {
                    continue;
                }
                if ((TheInfo.fType & MFT_SEPARATOR) != 0)
                {
                    continue;
                }

                UiMenuItemInfo TheItem = InOutMenu.Items[TheItemIndex];
                TheItemIndex++;

                string TheWin32Text = ReadMenuItemText(InMenuHandle, ThePosition);
                if (!IsWin32ItemAligned(TheItem.Name, TheWin32Text))
                {
                    continue;
                }

                bool HasSubmenu = TheInfo.hSubMenu != IntPtr.Zero;
                // Extended: サブメニューを持つ項目の wID には hSubMenu の値が入る（Phase 8 実測）ため、コマンド ID として採用しない
                if (!TheItem.Id.HasValue && !HasSubmenu)
                {
                    TheItem.Id = (int)TheInfo.wID;
                }
                if (string.IsNullOrEmpty(TheItem.Name))
                {
                    TheItem.Name = TheWin32Text;
                }
                if (!TheItem.IsChecked.HasValue)
                {
                    TheItem.IsChecked = (TheInfo.fState & MFS_CHECKED) != 0;
                }
                if (!TheItem.HasSubmenu)
                {
                    TheItem.HasSubmenu = HasSubmenu;
                }
                if (TheItem.IsEnabled && (TheInfo.fState & MFS_DISABLED) != 0)
                {
                    TheItem.IsEnabled = false;
                }
            }
        }

        /// <summary>指定位置のメニュー項目情報（ID / 状態 / サブメニュー / 種別）を読む。</summary>
        /// <param name="InMenuHandle">HMENU。</param>
        /// <param name="InPosition">0 始まりの位置。</param>
        /// <param name="OutInfo">読み取った情報。</param>
        /// <returns>読めたら true。</returns>
        private static bool TryReadMenuItemInfo(IntPtr InMenuHandle, int InPosition, out MENUITEMINFOW OutInfo)
        {
            OutInfo = new MENUITEMINFOW();
            OutInfo.cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(MENUITEMINFOW));
            OutInfo.fMask = MIIM_ID | MIIM_STATE | MIIM_SUBMENU | MIIM_STRING | MIIM_FTYPE;
            return GetMenuItemInfoW(InMenuHandle, (uint)InPosition, true, ref OutInfo);
        }

        /// <summary>UIA と Win32 の項目が同じものとみなせるか（どちらかの表示名が無ければ位置だけで対応させる）。</summary>
        /// <param name="InUiaName">UIA 側の表示名。</param>
        /// <param name="InWin32Text">Win32 側の表示名。</param>
        /// <returns>対応させてよければ true。</returns>
        private static bool IsWin32ItemAligned(string InUiaName, string InWin32Text)
        {
            if (string.IsNullOrEmpty(InUiaName) || string.IsNullOrEmpty(InWin32Text))
            {
                return true;
            }
            return IsNameMatched(InUiaName, InWin32Text);
        }

        /// <summary>GetMenuStringW で位置指定のメニュー文字列を読む。読めなければ null。</summary>
        /// <param name="InMenuHandle">HMENU。</param>
        /// <param name="InIndex">0 始まりの位置。</param>
        /// <returns>アクセラレータの &amp; を除いた文字列。読めなければ null。</returns>
        private static string ReadMenuItemText(IntPtr InMenuHandle, int InIndex)
        {
            StringBuilder TheBuffer = new StringBuilder(_MENU_STRING_BUFFER_LENGTH);
            int TheCopied = GetMenuStringW(InMenuHandle, (uint)InIndex, TheBuffer, TheBuffer.Capacity, MF_BYPOSITION);
            return TheCopied > 0 ? StandardDialogResolver.StripAccelerator(TheBuffer.ToString()) : null;
        }

        /// <summary>表示名の比較。完全一致に加えて、アクセラレータの &amp; を除いた比較も行う。</summary>
        /// <param name="InItemName">項目の表示名。</param>
        /// <param name="InExpectedName">期待する表示名。</param>
        /// <returns>一致すれば true。</returns>
        private static bool IsNameMatched(string InItemName, string InExpectedName)
        {
            if (string.Equals(InItemName, InExpectedName, StringComparison.Ordinal))
            {
                return true;
            }
            return string.Equals(StandardDialogResolver.StripAccelerator(InItemName ?? string.Empty),
                StandardDialogResolver.StripAccelerator(InExpectedName ?? string.Empty), StringComparison.Ordinal);
        }

        /// <summary>エラー文用に項目一覧を JSON で整形する（上限を超える分は載せない）。</summary>
        /// <param name="InItems">項目一覧。</param>
        /// <returns>JSON 文字列。</returns>
        private static string DescribeItems(List<UiMenuItemInfo> InItems)
        {
            List<UiMenuItemInfo> TheShown = InItems.Count > _MAX_AMBIGUOUS_CANDIDATES
                ? InItems.GetRange(0, _MAX_AMBIGUOUS_CANDIDATES)
                : InItems;
            return JsonConvert.SerializeObject(TheShown, Formatting.Indented);
        }
    }
}
