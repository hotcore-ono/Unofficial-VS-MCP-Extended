using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Automation;

using static VsMcp.Extension.Tools.NativeMethods;

namespace VsMcp.Extension.Tools
{
    /// <summary>
    /// 標準 File Dialog（IFileDialog）の識別・モード分類と、Adapter 間で共通の情報取得・操作。
    /// 構造の取得は Phase 5 の StandardDialogResolver.Inspect（Win32 子コントロール一覧 + 遅延 UIA ルート）を再利用する。
    /// 操作の正本は UIA（ValuePattern / SelectionItemPattern / InvokePattern）と Control ID（確定 = 1、キャンセル = 2）で、
    /// 表示文字列や画面座標には依存しない。
    /// </summary>
    internal static class StandardFileDialogResolver
    {
        public const string TypeOpen = "openFileDialog";
        public const string TypeSave = "saveFileDialog";
        public const string TypeFolder = "folderDialog";
        public const string TypeUnknown = "unknownFileDialog";

        public const string ModeOpen = "open";
        public const string ModeSave = "save";
        public const string ModeFolder = "folder";
        public const string ModeUnknown = "unknown";
        public const string ModeAny = "any";

        /// <summary>確定ボタン（開く / 保存 / フォルダーの選択）の標準コントロール ID。</summary>
        public const int ConfirmControlId = 1;

        /// <summary>キャンセルボタンの標準コントロール ID。</summary>
        public const int CancelControlId = 2;

        /// <summary>IFileDialog のビューを格納する DirectUI ホスト（Phase 6 実測。TaskDialog には無い）。</summary>
        private const string ViewHostClassName = "DUIViewWndClassName";

        /// <summary>アドレスバーのルート（Phase 6 実測）。</summary>
        private const string AddressBandClassName = "Address Band Root";

        /// <summary>シェルビュー（Phase 6 実測、ctrlId 1121）。</summary>
        private const string ShellViewClassName = "SHELLDLL_DefView";

        /// <summary>アドレスバーの文字列を持つ UIA 要素のクラス名（Name = "アドレス: C:\..."）。</summary>
        private const string AddressToolbarClassName = "ToolbarWindow32";

        /// <summary>一覧の項目の UIA クラス名（DirectUI の UIItemsView 配下）。</summary>
        private const string ListItemClassName = "UIItem";

        /// <summary>SendMessageTimeout の待ち時間（ミリ秒）。</summary>
        private const uint SendMessageTimeoutMs = 3000;

        /// <summary>判定順。構造が一意なので順序に依存はない。</summary>
        private static readonly IStandardFileDialogAdapter[] Adapters = { new FileOpenDialogAdapter(), new FolderDialogAdapter(), new FileSaveDialogAdapter() };

        /// <summary>
        /// #32770 の観測構造が IFileDialog 系か。DUIViewWndClassName（ビュー）と アドレスバーまたはシェルビュー、
        /// および Control ID 1 / 2 の Button が揃っていることを条件にする（クラス名 1 つだけでは判定しない）。
        /// </summary>
        /// <param name="InStructure">観測構造。</param>
        /// <returns>File Dialog なら true。</returns>
        public static bool IsFileDialogStructure(StandardDialogStructure InStructure)
        {
            return InStructure.HasChildOfClass(ViewHostClassName)
                && (InStructure.HasChildOfClass(AddressBandClassName) || InStructure.HasChildOfClass(ShellViewClassName))
                && FindButton(InStructure, ConfirmControlId) != null
                && FindButton(InStructure, CancelControlId) != null;
        }

        /// <summary>モードを担当する Adapter を選ぶ。</summary>
        /// <param name="InStructure">観測構造。</param>
        /// <returns>Adapter。判別できなければ null（unknownFileDialog）。</returns>
        public static IStandardFileDialogAdapter SelectAdapter(StandardDialogStructure InStructure)
        {
            return Adapters.FirstOrDefault(TheAdapter => TheAdapter.CanHandle(InStructure));
        }

        /// <summary>mode 引数（open / save / folder / any）の妥当性。</summary>
        /// <param name="InMode">引数値。</param>
        /// <returns>受け付ける値なら true。</returns>
        public static bool IsKnownModeFilter(string InMode)
        {
            return string.Equals(InMode, ModeAny, StringComparison.OrdinalIgnoreCase)
                || string.Equals(InMode, ModeOpen, StringComparison.OrdinalIgnoreCase)
                || string.Equals(InMode, ModeSave, StringComparison.OrdinalIgnoreCase)
                || string.Equals(InMode, ModeFolder, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>構造化情報を組み立てる。Adapter が無い（unknown）場合も共通部分（アドレス・一覧・ボタン）は観測できる範囲で返す。</summary>
        /// <param name="InWindow">ダイアログのウィンドウ情報。</param>
        /// <param name="InStructure">観測構造。</param>
        /// <param name="OutAdapter">選ばれた Adapter。unknown なら null。</param>
        /// <returns>構造化情報。</returns>
        public static StandardFileDialogInfo BuildInfo(WindowInfo InWindow, StandardDialogStructure InStructure, out IStandardFileDialogAdapter OutAdapter)
        {
            OutAdapter = SelectAdapter(InStructure);
            StandardFileDialogInfo TheInfo = StandardFileDialogInfo.FromWindow(
                OutAdapter?.DialogType ?? TypeUnknown, OutAdapter?.Mode ?? ModeUnknown, InWindow);

            TheInfo.CurrentFolderDisplay = ReadCurrentFolderDisplay(InStructure);
            TheInfo.CurrentFolderPath = ExtractRootedPath(TheInfo.CurrentFolderDisplay);

            DialogChildControl TheEdit = OutAdapter?.FindFileNameEdit(InStructure);
            if (TheEdit != null)
            {
                TheInfo.FileNameEditHandle = TheEdit.Handle.ToInt64();
                TheInfo.FileName = ReadControlText(TheEdit.Handle);
            }

            DialogChildControl TheCombo = OutAdapter?.FindFileTypeCombo(InStructure);
            if (TheCombo != null)
            {
                TheInfo.FileTypeFilterIndex = ReadComboSelectedIndex(TheCombo.Handle);
                if (TheInfo.FileTypeFilterIndex.HasValue)
                    TheInfo.FileTypeFilter = ReadComboItemText(TheCombo.Handle, TheInfo.FileTypeFilterIndex.Value);
            }

            TheInfo.ConfirmButton = BuildButton(FindButton(InStructure, ConfirmControlId), "ok");
            TheInfo.CancelButton = BuildButton(FindButton(InStructure, CancelControlId), "cancel");
            TheInfo.SelectedItems = ReadItems(InStructure, TheInfo.CurrentFolderPath)
                .Where(TheItem => TheItem.IsSelected == true)
                .ToList();
            return TheInfo;
        }

        /// <summary>ダイアログ直下の指定 Control ID の Button。</summary>
        /// <param name="InStructure">観測構造。</param>
        /// <param name="InControlId">コントロール ID。</param>
        /// <returns>Button の観測値。無ければ null。</returns>
        public static DialogChildControl FindButton(StandardDialogStructure InStructure, int InControlId)
        {
            return InStructure.DirectChildren.FirstOrDefault(TheChild =>
                TheChild.ControlId == InControlId && string.Equals(TheChild.ClassName, "Button", StringComparison.Ordinal));
        }

        /// <summary>一覧（シェルビュー）の項目を UIA から読む。currentFolderPath が判明していればパスとフォルダー判定も付ける。</summary>
        /// <param name="InStructure">観測構造。</param>
        /// <param name="InCurrentFolderPath">現在フォルダーの絶対パス。不明なら null。</param>
        /// <returns>項目一覧（表示順）。UIA 不可なら空。</returns>
        public static List<StandardFileDialogItemInfo> ReadItems(StandardDialogStructure InStructure, string InCurrentFolderPath)
        {
            List<StandardFileDialogItemInfo> TheItems = new List<StandardFileDialogItemInfo>();
            foreach (AutomationElement TheElement in FindItemElements(InStructure))
            {
                try
                {
                    AutomationElement.AutomationElementInformation TheCurrent = TheElement.Current;
                    string ThePath = InCurrentFolderPath != null && !string.IsNullOrEmpty(TheCurrent.Name)
                        ? Path.Combine(InCurrentFolderPath, TheCurrent.Name)
                        : null;
                    TheItems.Add(new StandardFileDialogItemInfo
                    {
                        Name = TheCurrent.Name,
                        AutomationId = TheCurrent.AutomationId,
                        Path = ThePath,
                        IsFolder = ThePath == null ? (bool?)null : Directory.Exists(ThePath) ? true : File.Exists(ThePath) ? false : (bool?)null,
                        IsSelected = GetIsSelected(TheElement),
                    });
                }
                catch
                {
                    // 列挙中に消えた項目は読み飛ばす
                }
            }
            return TheItems;
        }

        /// <summary>一覧項目の UIA 要素（クラス UIItem の ListItem）を表示順で返す。</summary>
        /// <param name="InStructure">観測構造。</param>
        /// <returns>要素一覧。UIA 不可なら空。</returns>
        public static List<AutomationElement> FindItemElements(StandardDialogStructure InStructure)
        {
            List<AutomationElement> TheElements = new List<AutomationElement>();
            try
            {
                Condition TheCondition = new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem),
                    new PropertyCondition(AutomationElement.ClassNameProperty, ListItemClassName));
                foreach (AutomationElement TheElement in InStructure.Root.FindAll(TreeScope.Descendants, TheCondition))
                    TheElements.Add(TheElement);
            }
            catch
            {
                // UIA 不可
            }
            return TheElements;
        }

        /// <summary>SelectionItemPattern.Select で項目を選択し、選択状態を読み返す。</summary>
        /// <param name="InElement">項目要素。</param>
        /// <returns>選択できたら true。</returns>
        public static bool TrySelectItem(AutomationElement InElement)
        {
            try
            {
                if (!InElement.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object ThePattern))
                    return false;
                ((SelectionItemPattern)ThePattern).Select();
                return GetIsSelected(InElement) != false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// ファイル名 Edit へ文字列を設定する。UIA ValuePattern.SetValue → WM_SETTEXT の順に試し、設定後に読み返して一致を確認する。
        /// </summary>
        /// <param name="InStructure">観測構造。</param>
        /// <param name="InEditHandle">Edit の HWND。</param>
        /// <param name="InText">設定する文字列。</param>
        /// <param name="OutReadBack">設定後に読み返した文字列。</param>
        /// <returns>成功した方式名（uiaSetValue / wmSetText）。失敗なら null。</returns>
        public static string TrySetFileName(StandardDialogStructure InStructure, IntPtr InEditHandle, string InText, out string OutReadBack)
        {
            string TheMethod = null;
            AutomationElement TheElement = StandardDialogResolver.FindUiaElement(
                InStructure.Root, new PropertyCondition(AutomationElement.NativeWindowHandleProperty, (int)InEditHandle.ToInt64()));
            try
            {
                if (TheElement != null && TheElement.TryGetCurrentPattern(ValuePattern.Pattern, out object ThePattern))
                {
                    ((ValuePattern)ThePattern).SetValue(InText);
                    TheMethod = "uiaSetValue";
                }
            }
            catch
            {
                TheMethod = null;
            }

            if (TheMethod == null)
            {
                IntPtr TheReturn = SendMessageTimeoutSetText(InEditHandle, WM_SETTEXT, IntPtr.Zero, InText, SMTO_BLOCK | SMTO_ABORTIFHUNG, SendMessageTimeoutMs, out IntPtr TheResult);
                if (TheReturn != IntPtr.Zero && TheResult != IntPtr.Zero)
                    TheMethod = "wmSetText";
            }

            OutReadBack = ReadControlText(InEditHandle);
            if (TheMethod != null && !string.Equals(OutReadBack, InText, StringComparison.Ordinal))
                return null; // 設定したはずの値が反映されていない
            return TheMethod;
        }

        /// <summary>ボタンを押す。UIA InvokePattern（HWND で特定）→ BM_CLICK の順。</summary>
        /// <param name="InStructure">観測構造。</param>
        /// <param name="InButton">押すボタン。</param>
        /// <returns>成功した方式名（uiaInvoke / bmClick）。失敗なら null。</returns>
        public static string TryPressButton(StandardDialogStructure InStructure, DialogChildControl InButton)
        {
            AutomationElement TheElement = StandardDialogResolver.FindUiaElement(
                InStructure.Root, new PropertyCondition(AutomationElement.NativeWindowHandleProperty, (int)InButton.Handle.ToInt64()));
            if (StandardDialogResolver.TryUiaInvoke(TheElement))
                return "uiaInvoke";
            if (StandardDialogResolver.TrySendMessage(InButton.Handle, BM_CLICK, IntPtr.Zero, IntPtr.Zero, out IntPtr TheUnused))
                return "bmClick";
            return null;
        }

        /// <summary>WM_GETTEXTLENGTH / WM_GETTEXT で他プロセスのコントロール文字列を読む（Edit は GetWindowText では取れない）。</summary>
        /// <param name="InHandle">対象 HWND。</param>
        /// <returns>文字列。取れなければ null。</returns>
        public static string ReadControlText(IntPtr InHandle)
        {
            if (!StandardDialogResolver.TrySendMessage(InHandle, WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero, out IntPtr TheLength))
                return null;
            int TheCapacity = (int)TheLength.ToInt64() + 1;
            StringBuilder TheBuffer = new StringBuilder(TheCapacity);
            IntPtr TheReturn = SendMessageTimeoutText(InHandle, WM_GETTEXT, new IntPtr(TheCapacity), TheBuffer, SMTO_BLOCK | SMTO_ABORTIFHUNG, SendMessageTimeoutMs, out IntPtr TheCopied);
            return TheReturn == IntPtr.Zero ? null : TheBuffer.ToString();
        }

        /// <summary>アドレスバー（Address Band Root 配下の ToolbarWindow32）の UIA Name。無ければ null。</summary>
        /// <param name="InStructure">観測構造。</param>
        /// <returns>表示文字列（例: "アドレス: C:\..."）。</returns>
        private static string ReadCurrentFolderDisplay(StandardDialogStructure InStructure)
        {
            try
            {
                Condition TheCondition = new PropertyCondition(AutomationElement.ClassNameProperty, AddressToolbarClassName);
                foreach (AutomationElement TheElement in InStructure.Root.FindAll(TreeScope.Descendants, TheCondition))
                {
                    string TheName = TheElement.Current.Name;
                    if (!string.IsNullOrEmpty(TheName) && ExtractRootedPath(TheName) != null)
                        return TheName;
                }
            }
            catch
            {
                // UIA 不可
            }
            return null;
        }

        /// <summary>
        /// "アドレス: C:\..." のような表示文字列から、区切り以降がルート付きパスとして妥当な場合だけパスを取り出す。
        /// 表示言語の接頭辞は解釈せず、Path.IsPathRooted を満たす末尾部分だけを採用する。
        /// </summary>
        /// <param name="InDisplay">表示文字列。</param>
        /// <returns>絶対パス。妥当でなければ null。</returns>
        private static string ExtractRootedPath(string InDisplay)
        {
            if (string.IsNullOrEmpty(InDisplay))
                return null;
            int TheSeparator = InDisplay.IndexOf(": ", StringComparison.Ordinal);
            string TheCandidate = (TheSeparator >= 0 ? InDisplay.Substring(TheSeparator + 2) : InDisplay).Trim();
            try
            {
                return TheCandidate.Length > 0 && Path.IsPathRooted(TheCandidate) && TheCandidate.IndexOfAny(Path.GetInvalidPathChars()) < 0
                    ? TheCandidate
                    : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>CB_GETCURSEL。</summary>
        /// <param name="InCombo">ComboBox の HWND。</param>
        /// <returns>選択インデックス。無選択・失敗なら null。</returns>
        private static int? ReadComboSelectedIndex(IntPtr InCombo)
        {
            if (!StandardDialogResolver.TrySendMessage(InCombo, CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero, out IntPtr TheResult))
                return null;
            int TheIndex = (int)TheResult.ToInt64();
            return TheIndex < 0 ? (int?)null : TheIndex;
        }

        /// <summary>CB_GETLBTEXTLEN / CB_GETLBTEXT。</summary>
        /// <param name="InCombo">ComboBox の HWND。</param>
        /// <param name="InIndex">項目インデックス。</param>
        /// <returns>項目文字列。失敗なら null。</returns>
        private static string ReadComboItemText(IntPtr InCombo, int InIndex)
        {
            if (!StandardDialogResolver.TrySendMessage(InCombo, CB_GETLBTEXTLEN, new IntPtr(InIndex), IntPtr.Zero, out IntPtr TheLength) || TheLength.ToInt64() < 0)
                return null;
            StringBuilder TheBuffer = new StringBuilder((int)TheLength.ToInt64() + 1);
            IntPtr TheReturn = SendMessageTimeoutText(InCombo, CB_GETLBTEXT, new IntPtr(InIndex), TheBuffer, SMTO_BLOCK | SMTO_ABORTIFHUNG, SendMessageTimeoutMs, out IntPtr TheUnused);
            return TheReturn == IntPtr.Zero ? null : TheBuffer.ToString();
        }

        /// <summary>Button の観測値から StandardDialogButtonInfo を作る。</summary>
        /// <param name="InButton">Button の観測値。null なら null。</param>
        /// <param name="InAction">論理 action 名。</param>
        /// <returns>ボタン情報。</returns>
        private static StandardDialogButtonInfo BuildButton(DialogChildControl InButton, string InAction)
        {
            if (InButton == null)
                return null;
            return new StandardDialogButtonInfo
            {
                Id = InButton.ControlId,
                Action = InAction,
                Text = StandardDialogResolver.StripAccelerator(InButton.Text),
                IsDefault = InButton.ButtonStyleType == BS_DEFPUSHBUTTON,
                IsEnabled = InButton.IsEnabled,
                Handle = InButton.Handle.ToInt64(),
            };
        }

        /// <summary>SelectionItemPattern.IsSelected。取れなければ null。</summary>
        /// <param name="InElement">項目要素。</param>
        /// <returns>選択状態。</returns>
        private static bool? GetIsSelected(AutomationElement InElement)
        {
            try
            {
                if (!InElement.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object ThePattern))
                    return null;
                return ((SelectionItemPattern)ThePattern).Current.IsSelected;
            }
            catch
            {
                return null;
            }
        }
    }
}
