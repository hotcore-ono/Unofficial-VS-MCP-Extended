using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows.Automation;

using static VsMcp.Extension.Tools.NativeMethods;

namespace VsMcp.Extension.Tools
{
    /// <summary>ダイアログの子コントロール 1 個の観測値（EnumChildWindows で収集）。</summary>
    internal sealed class DialogChildControl
    {
        public IntPtr Handle { get; set; }
        public IntPtr Parent { get; set; }
        public string ClassName { get; set; }
        public int ControlId { get; set; }
        public string Text { get; set; }
        public bool IsEnabled { get; set; }
        public bool IsVisible { get; set; }
        public int Style { get; set; }

        /// <summary>Button クラスのときの BS_* 種別（BS_PUSHBUTTON=0 / BS_DEFPUSHBUTTON=1 / BS_AUTORADIOBUTTON=9 など）。</summary>
        public int ButtonStyleType => Style & BS_TYPEMASK;
    }

    /// <summary>ダイアログ 1 個の観測構造。Win32 の子コントロール一覧と、必要になったときだけ作る UIA ルート要素を持つ。</summary>
    internal sealed class StandardDialogStructure
    {
        private AutomationElement _Root;

        /// <summary>ダイアログのトップレベル HWND。</summary>
        public IntPtr Handle { get; set; }

        /// <summary>全子孫コントロール（EnumChildWindows の順序）。</summary>
        public List<DialogChildControl> Children { get; set; } = new List<DialogChildControl>();

        /// <summary>ダイアログ直下の子だけ。</summary>
        public IEnumerable<DialogChildControl> DirectChildren => Children.Where(TheChild => TheChild.Parent == Handle);

        /// <summary>UIA のルート要素（遅延生成）。</summary>
        public AutomationElement Root => _Root ?? (_Root = AutomationElement.FromHandle(Handle));

        /// <summary>指定クラス名の子孫があるか。</summary>
        /// <param name="InClassName">クラス名（大小文字区別）。</param>
        /// <returns>あれば true。</returns>
        public bool HasChildOfClass(string InClassName)
        {
            return Children.Any(TheChild => string.Equals(TheChild.ClassName, InClassName, StringComparison.Ordinal));
        }

        /// <summary>HWND から子コントロールを引く。</summary>
        /// <param name="InHandle">子コントロールの HWND。</param>
        /// <returns>該当があればその観測値、無ければ null。</returns>
        public DialogChildControl FindByHandle(long InHandle)
        {
            return Children.FirstOrDefault(TheChild => TheChild.Handle.ToInt64() == InHandle);
        }
    }

    /// <summary>
    /// 標準ダイアログの識別・分類・共通 Win32/UIA 操作。Adapter の選択と、Adapter 間で共有する低レベル処理を持つ。
    /// クラス名 #32770 は手掛かりに過ぎず、種類の確定は各 Adapter の構造判定に委ねる。
    /// </summary>
    internal static class StandardDialogResolver
    {
        /// <summary>Win32 ダイアログのウィンドウクラス名。</summary>
        public const string DialogClassName = "#32770";

        public const string TypeMessageBox = "messageBox";
        public const string TypeTaskDialog = "taskDialog";
        public const string TypeUnknown = "unknownStandardDialog";

        /// <summary>SendMessageTimeoutW の待ち時間（ミリ秒）。</summary>
        private const uint SendMessageTimeoutMs = 3000;

        /// <summary>GetClassNameW / GetWindowTextW のバッファ長。</summary>
        private const int TextBufferLength = 1024;

        /// <summary>判定順。TaskDialog は DirectUIHWND という固有構造を持つので先に判定する。</summary>
        private static readonly IStandardDialogAdapter[] Adapters = { new TaskDialogAdapter(), new MessageBoxAdapter() };

        /// <summary>論理 action と標準コントロール ID の対応表（表示文字列には依存しない）。</summary>
        private static readonly KeyValuePair<string, int>[] ActionTable =
        {
            new KeyValuePair<string, int>("ok", 1),
            new KeyValuePair<string, int>("cancel", 2),
            new KeyValuePair<string, int>("abort", 3),
            new KeyValuePair<string, int>("retry", 4),
            new KeyValuePair<string, int>("ignore", 5),
            new KeyValuePair<string, int>("yes", 6),
            new KeyValuePair<string, int>("no", 7),
            new KeyValuePair<string, int>("close", 8),
            new KeyValuePair<string, int>("help", 9),
            new KeyValuePair<string, int>("tryAgain", 10),
            new KeyValuePair<string, int>("continue", 11),
        };

        /// <summary>schema の enum に使う action 名一覧。</summary>
        public static string[] StandardActions => ActionTable.Select(TheEntry => TheEntry.Key).ToArray();

        /// <summary>ウィンドウが Win32 ダイアログクラスか。</summary>
        /// <param name="InWindow">判定対象。</param>
        /// <returns>#32770 なら true。</returns>
        public static bool IsDialogClass(WindowInfo InWindow)
        {
            return string.Equals(InWindow.ClassName, DialogClassName, StringComparison.Ordinal);
        }

        /// <summary>論理 action を標準 ID へ解決する（大小文字非区別）。</summary>
        /// <param name="InAction">action 名。</param>
        /// <returns>標準 ID。未知なら null。</returns>
        public static int? ResolveActionId(string InAction)
        {
            foreach (KeyValuePair<string, int> TheEntry in ActionTable)
            {
                if (string.Equals(TheEntry.Key, InAction, StringComparison.OrdinalIgnoreCase))
                    return TheEntry.Value;
            }
            return null;
        }

        /// <summary>標準 ID を論理 action へ変換する。</summary>
        /// <param name="InId">コントロール ID。</param>
        /// <returns>action 名。標準 ID でなければ null。</returns>
        public static string ActionFromId(int InId)
        {
            foreach (KeyValuePair<string, int> TheEntry in ActionTable)
            {
                if (TheEntry.Value == InId)
                    return TheEntry.Key;
            }
            return null;
        }

        /// <summary>ダイアログの子コントロールを列挙して構造を作る（Win32 のみ。UIA は Adapter が必要時に使う）。</summary>
        /// <param name="InHandle">ダイアログの HWND。</param>
        /// <returns>観測構造。</returns>
        public static StandardDialogStructure Inspect(IntPtr InHandle)
        {
            StandardDialogStructure TheStructure = new StandardDialogStructure { Handle = InHandle };
            EnumChildWindows(InHandle, (TheChild, TheLParam) =>
            {
                try
                {
                    TheStructure.Children.Add(new DialogChildControl
                    {
                        Handle = TheChild,
                        Parent = GetAncestor(TheChild, GA_PARENT),
                        ClassName = GetClassName(TheChild),
                        ControlId = GetDlgCtrlID(TheChild),
                        Text = GetWindowText(TheChild),
                        IsEnabled = IsWindowEnabled(TheChild),
                        IsVisible = IsWindowVisible(TheChild),
                        Style = GetWindowLongW(TheChild, GWL_STYLE),
                    });
                }
                catch
                {
                    // 列挙中に破棄されたコントロールは読み飛ばす
                }
                return true;
            }, IntPtr.Zero);
            return TheStructure;
        }

        /// <summary>構造から Adapter を選ぶ。</summary>
        /// <param name="InStructure">観測構造。</param>
        /// <returns>扱える Adapter。無ければ null（unknownStandardDialog）。</returns>
        public static IStandardDialogAdapter SelectAdapter(StandardDialogStructure InStructure)
        {
            return Adapters.FirstOrDefault(TheAdapter => TheAdapter.CanHandle(InStructure));
        }

        /// <summary>ダイアログを分類し、構造化情報を組み立てる。unknown の場合は観測できる範囲だけ埋める。</summary>
        /// <param name="InWindow">ダイアログのウィンドウ情報。</param>
        /// <param name="InStructure">観測構造。</param>
        /// <param name="OutAdapter">選ばれた Adapter。unknown なら null。</param>
        /// <returns>構造化情報。</returns>
        public static StandardDialogInfo BuildInfo(WindowInfo InWindow, StandardDialogStructure InStructure, out IStandardDialogAdapter OutAdapter)
        {
            OutAdapter = SelectAdapter(InStructure);
            if (OutAdapter != null)
                return OutAdapter.GetInfo(InWindow, InStructure);

            // unknown: Win32 の Button 子コントロールと本文 Static だけを観測値として返す（操作はしない）
            StandardDialogInfo TheInfo = StandardDialogInfo.FromWindow(TypeUnknown, InWindow);
            TheInfo.Buttons = CollectWin32Buttons(InStructure);
            TheInfo.Message = FindStaticText(InStructure);
            TheInfo.DefaultButton = ResolveDefaultButtonId(InStructure, TheInfo.Buttons);
            return TheInfo;
        }

        /// <summary>ダイアログ直下の Button クラス（押しボタン種別）を標準 ID 付きで集める。</summary>
        /// <param name="InStructure">観測構造。</param>
        /// <returns>ボタン一覧（表示順）。</returns>
        public static List<StandardDialogButtonInfo> CollectWin32Buttons(StandardDialogStructure InStructure)
        {
            List<StandardDialogButtonInfo> TheButtons = new List<StandardDialogButtonInfo>();
            foreach (DialogChildControl TheChild in InStructure.DirectChildren)
            {
                if (!string.Equals(TheChild.ClassName, "Button", StringComparison.Ordinal) || TheChild.ControlId <= 0)
                    continue;
                if (TheChild.ButtonStyleType > BS_DEFPUSHBUTTON)
                    continue; // チェックボックス・ラジオ・グループボックスは押しボタンではない

                TheButtons.Add(new StandardDialogButtonInfo
                {
                    Id = TheChild.ControlId,
                    Action = ActionFromId(TheChild.ControlId),
                    Text = StripAccelerator(TheChild.Text),
                    IsDefault = TheChild.ButtonStyleType == BS_DEFPUSHBUTTON,
                    IsEnabled = TheChild.IsEnabled,
                    Handle = TheChild.Handle.ToInt64(),
                });
            }
            return TheButtons;
        }

        /// <summary>本文の Static コントロール（MessageBox は ID 0xFFFF）の文字列。無ければ空でない最初の Static。</summary>
        /// <param name="InStructure">観測構造。</param>
        /// <returns>本文。無ければ null。</returns>
        public static string FindStaticText(StandardDialogStructure InStructure)
        {
            List<DialogChildControl> TheStatics = InStructure.DirectChildren
                .Where(TheChild => string.Equals(TheChild.ClassName, "Static", StringComparison.Ordinal) && !string.IsNullOrEmpty(TheChild.Text))
                .ToList();
            DialogChildControl TheText = TheStatics.FirstOrDefault(TheChild => TheChild.ControlId == 0xFFFF) ?? TheStatics.FirstOrDefault();
            return TheText?.Text;
        }

        /// <summary>
        /// 既定ボタンの ID を決める。DM_GETDEFID（DC_HASDEFID 付き）が返す ID が一覧に存在すればそれ、
        /// 無ければ BS_DEFPUSHBUTTON スタイルのボタン。どちらも無ければ null（推測しない）。
        /// </summary>
        /// <param name="InStructure">観測構造。</param>
        /// <param name="InButtons">ボタン一覧（IsDefault を更新する）。</param>
        /// <returns>既定ボタン ID。</returns>
        public static int? ResolveDefaultButtonId(StandardDialogStructure InStructure, List<StandardDialogButtonInfo> InButtons)
        {
            int? TheDefaultId = null;
            if (TrySendMessage(InStructure.Handle, DM_GETDEFID, IntPtr.Zero, IntPtr.Zero, out IntPtr TheResult))
            {
                long TheValue = TheResult.ToInt64();
                if (((TheValue >> 16) & 0xFFFF) == DC_HASDEFID)
                {
                    int TheId = (int)(TheValue & 0xFFFF);
                    if (InButtons.Any(TheButton => TheButton.Id == TheId))
                        TheDefaultId = TheId;
                }
            }
            if (!TheDefaultId.HasValue)
                TheDefaultId = InButtons.FirstOrDefault(TheButton => TheButton.IsDefault)?.Id;

            foreach (StandardDialogButtonInfo TheButton in InButtons)
                TheButton.IsDefault = TheDefaultId.HasValue && TheButton.Id == TheDefaultId.Value;
            return TheDefaultId;
        }

        /// <summary>アクセラレータ記号を除く（"はい(&amp;Y)" → "はい(Y)"、"&amp;&amp;" → "&amp;"）。</summary>
        /// <param name="InText">元の文字列。</param>
        /// <returns>整形後の文字列。</returns>
        public static string StripAccelerator(string InText)
        {
            if (string.IsNullOrEmpty(InText))
                return InText ?? string.Empty;

            // "&&"（リテラルの &）を私用領域の文字へ退避してから単独の & を除き、最後に & へ戻す
            const string TheLiteralAmpersandPlaceholder = "\uE000";
            return InText.Replace("&&", TheLiteralAmpersandPlaceholder).Replace("&", string.Empty).Replace(TheLiteralAmpersandPlaceholder, "&");
        }

        /// <summary>SendMessageTimeoutW でメッセージを送る。応答が無い（ハング／停止中）場合は false。</summary>
        /// <param name="InHandle">送信先。</param>
        /// <param name="InMessage">メッセージ。</param>
        /// <param name="InWParam">wParam。</param>
        /// <param name="InLParam">lParam。</param>
        /// <param name="OutResult">戻り値。</param>
        /// <returns>送信が完了したら true。</returns>
        public static bool TrySendMessage(IntPtr InHandle, uint InMessage, IntPtr InWParam, IntPtr InLParam, out IntPtr OutResult)
        {
            IntPtr TheReturn = SendMessageTimeoutW(InHandle, InMessage, InWParam, InLParam, SMTO_BLOCK | SMTO_ABORTIFHUNG, SendMessageTimeoutMs, out OutResult);
            return TheReturn != IntPtr.Zero;
        }

        /// <summary>UIA ルートの子孫から条件に合う最初の要素を返す。UIA 例外は null 扱い。</summary>
        /// <param name="InRoot">ルート要素。</param>
        /// <param name="InCondition">検索条件。</param>
        /// <returns>要素。無ければ null。</returns>
        public static AutomationElement FindUiaElement(AutomationElement InRoot, Condition InCondition)
        {
            try
            {
                return InRoot?.FindFirst(TreeScope.Descendants, InCondition);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>UIA 要素の InvokePattern を実行する。パターン非対応・例外なら false。</summary>
        /// <param name="InElement">対象要素。</param>
        /// <returns>Invoke が完了したら true。</returns>
        public static bool TryUiaInvoke(AutomationElement InElement)
        {
            try
            {
                if (InElement == null || !InElement.TryGetCurrentPattern(InvokePattern.Pattern, out object ThePattern))
                    return false;
                ((InvokePattern)ThePattern).Invoke();
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>GetClassNameW。</summary>
        /// <param name="InHandle">対象 HWND。</param>
        /// <returns>クラス名。失敗時は空文字。</returns>
        private static string GetClassName(IntPtr InHandle)
        {
            StringBuilder TheBuffer = new StringBuilder(TextBufferLength);
            return GetClassNameW(InHandle, TheBuffer, TheBuffer.Capacity) > 0 ? TheBuffer.ToString() : string.Empty;
        }

        /// <summary>GetWindowTextW（子コントロールの表示文字列）。</summary>
        /// <param name="InHandle">対象 HWND。</param>
        /// <returns>文字列。無ければ空文字。</returns>
        private static string GetWindowText(IntPtr InHandle)
        {
            StringBuilder TheBuffer = new StringBuilder(TextBufferLength);
            return GetWindowTextW(InHandle, TheBuffer, TheBuffer.Capacity) > 0 ? TheBuffer.ToString() : string.Empty;
        }
    }
}
