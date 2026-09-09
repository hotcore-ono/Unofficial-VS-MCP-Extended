using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Automation;

using static VsMcp.Extension.Tools.NativeMethods;

namespace VsMcp.Extension.Tools
{
    /// <summary>
    /// comctl32 TaskDialog / TaskDialogIndirect の Adapter。実測した構造:
    ///  #32770 直下に DirectUIHWND があり、その配下の CtrlNotifySink に Button（ctrlId は全て 0）が入る。
    ///  UIA では AutomationId が安定している: MainInstruction / ContentText / FootnoteText（Text）、
    ///  CommandButton_&lt;id&gt;（押しボタン、NativeWindowHandle あり）、RadioButton_&lt;id&gt;、VerificationCheckBox、ExpandoButton。
    ///  DM_GETDEFID は常に 1 を返して信用できないため、既定ボタンは BS_DEFPUSHBUTTON スタイルで判定する。
    ///  操作は TaskDialog 自身の TDM_CLICK_BUTTON（ボタン ID 指定）を第一候補にする。
    /// </summary>
    internal sealed class TaskDialogAdapter : IStandardDialogAdapter
    {
        /// <summary>TaskDialog の DirectUI ホストのクラス名。</summary>
        public const string DirectUiClassName = "DirectUIHWND";

        private const string CommandButtonPrefix = "CommandButton_";
        private const string RadioButtonPrefix = "RadioButton_";
        private const string MainInstructionId = "MainInstruction";
        private const string ContentId = "ContentText";
        private const string FooterId = "FootnoteText";
        private const string VerificationId = "VerificationCheckBox";
        private const string ExpandedInformationHint = "Expanded";

        /// <summary>TaskDialog 固有の UIA ルート要素（ルート直下の Pane）の AutomationId / ClassName（Phase 5 実測）。</summary>
        private const string TaskDialogRootAutomationId = "Window";
        private const string TaskDialogRootClassName = "TaskDialog";

        /// <summary>
        /// IFileDialog（Open / Save / Folder）に現れ、TaskDialog には現れない Win32 クラス（Phase 6 実測）。
        /// 最新の File Dialog も #32770 + DirectUIHWND なので、これらが 1 つでもあれば TaskDialog ではないと判定する。
        /// </summary>
        private static readonly string[] FileDialogClassNames = { "DUIViewWndClassName", "ComboBoxEx32", "ToolbarWindow32", "SHELLDLL_DefView", "SysListView32", "Address Band Root" };

        public string DialogType => StandardDialogResolver.TypeTaskDialog;

        /// <summary>
        /// TaskDialog 判定: #32770 の子孫に DirectUIHWND があり、File Dialog 固有クラスが無く、
        /// かつ UIA ルート直下に AutomationId="Window" / ClassName="TaskDialog" の Pane がある（Phase 5 実測の固有構造）。
        /// </summary>
        /// <param name="InStructure">観測構造。</param>
        /// <returns>TaskDialog なら true。</returns>
        public bool CanHandle(StandardDialogStructure InStructure)
        {
            if (!InStructure.HasChildOfClass(DirectUiClassName))
                return false;
            foreach (string TheClassName in FileDialogClassNames)
            {
                if (InStructure.HasChildOfClass(TheClassName))
                    return false;
            }
            return HasTaskDialogUiaRoot(InStructure);
        }

        /// <summary>UIA ルート直下に TaskDialog 固有の Pane（AutomationId "Window"、ClassName "TaskDialog"）があるか。UIA 不可なら false。</summary>
        /// <param name="InStructure">観測構造。</param>
        /// <returns>あれば true。</returns>
        private static bool HasTaskDialogUiaRoot(StandardDialogStructure InStructure)
        {
            try
            {
                // この Pane は IsControlElement=false なので FindFirst（コントロールビュー）では見つからない。RawView で直下の子を走査する
                TreeWalker TheWalker = TreeWalker.RawViewWalker;
                for (AutomationElement TheChild = TheWalker.GetFirstChild(InStructure.Root); TheChild != null; TheChild = TheWalker.GetNextSibling(TheChild))
                {
                    AutomationElement.AutomationElementInformation TheCurrent = TheChild.Current;
                    if (string.Equals(TheCurrent.AutomationId, TaskDialogRootAutomationId, StringComparison.Ordinal)
                        && string.Equals(TheCurrent.ClassName, TaskDialogRootClassName, StringComparison.Ordinal))
                        return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>UIA の AutomationId を正本にして本文・ボタン・ラジオ・検証チェック・フッターを取得する。</summary>
        /// <param name="InWindow">ダイアログのウィンドウ情報。</param>
        /// <param name="InStructure">観測構造。</param>
        /// <returns>構造化情報。UIA が使えない場合は取得できた範囲だけ。</returns>
        public StandardDialogInfo GetInfo(WindowInfo InWindow, StandardDialogStructure InStructure)
        {
            StandardDialogInfo TheInfo = StandardDialogInfo.FromWindow(DialogType, InWindow);
            AutomationElementCollection TheElements;
            try
            {
                TheElements = InStructure.Root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
            }
            catch
            {
                return TheInfo; // UIA 不可: 推測で埋めずに骨格だけ返す
            }

            List<StandardDialogRadioButtonInfo> TheRadios = new List<StandardDialogRadioButtonInfo>();
            foreach (AutomationElement TheElement in TheElements)
            {
                AutomationElement.AutomationElementInformation TheCurrent;
                try
                {
                    TheCurrent = TheElement.Current;
                }
                catch
                {
                    continue; // 取得中に消えた要素
                }

                string TheAutomationId = TheCurrent.AutomationId ?? string.Empty;
                if (TheAutomationId == MainInstructionId)
                    TheInfo.MainInstruction = TheCurrent.Name;
                else if (TheAutomationId == ContentId)
                    TheInfo.Content = TheCurrent.Name;
                else if (TheAutomationId == FooterId)
                    TheInfo.Footer = TheCurrent.Name;
                else if (TheAutomationId == VerificationId)
                {
                    TheInfo.VerificationText = TheCurrent.Name;
                    TheInfo.VerificationChecked = GetToggleState(TheElement);
                }
                else if (TheAutomationId.StartsWith(CommandButtonPrefix, StringComparison.Ordinal))
                {
                    if (!TryParseId(TheAutomationId, CommandButtonPrefix, out int TheId))
                        continue;
                    DialogChildControl TheControl = InStructure.FindByHandle(TheCurrent.NativeWindowHandle);
                    TheInfo.Buttons.Add(new StandardDialogButtonInfo
                    {
                        Id = TheId,
                        Action = StandardDialogResolver.ActionFromId(TheId),
                        Text = StandardDialogResolver.StripAccelerator(TheCurrent.Name),
                        IsDefault = TheControl != null && TheControl.ButtonStyleType == BS_DEFPUSHBUTTON,
                        IsEnabled = TheCurrent.IsEnabled,
                        Handle = TheCurrent.NativeWindowHandle,
                        AutomationId = TheAutomationId,
                    });
                }
                else if (TheAutomationId.StartsWith(RadioButtonPrefix, StringComparison.Ordinal))
                {
                    if (!TryParseId(TheAutomationId, RadioButtonPrefix, out int TheId))
                        continue;
                    TheRadios.Add(new StandardDialogRadioButtonInfo
                    {
                        Id = TheId,
                        Text = StandardDialogResolver.StripAccelerator(TheCurrent.Name),
                        IsChecked = GetRadioChecked(TheCurrent.NativeWindowHandle),
                        IsEnabled = TheCurrent.IsEnabled,
                    });
                }
                else if (TheAutomationId.IndexOf(ExpandedInformationHint, StringComparison.Ordinal) >= 0
                         && TheCurrent.ControlType == ControlType.Text)
                {
                    TheInfo.ExpandedInformation = TheCurrent.Name;
                }
            }

            if (TheRadios.Count > 0)
                TheInfo.RadioButtons = TheRadios;
            TheInfo.DefaultButton = TheInfo.Buttons.FirstOrDefault(TheButton => TheButton.IsDefault)?.Id;
            return TheInfo;
        }

        /// <summary>TDM_CLICK_BUTTON → UIA InvokePattern → BM_CLICK の順に試す。</summary>
        /// <param name="InInfo">構造化情報。</param>
        /// <param name="InButton">押すボタン。</param>
        /// <param name="InStructure">観測構造。</param>
        /// <returns>成功した方式名。全滅なら null。</returns>
        public string Execute(StandardDialogInfo InInfo, StandardDialogButtonInfo InButton, StandardDialogStructure InStructure)
        {
            // 1. TaskDialog 自身のメッセージ（ボタン ID を正本にする API 定義の方法。フォアグラウンド不要）
            if (StandardDialogResolver.TrySendMessage(InStructure.Handle, TDM_CLICK_BUTTON, new IntPtr(InButton.Id), IntPtr.Zero, out IntPtr TheUnused))
                return "tdmClickButton";

            // 2. UIA: AutomationId "CommandButton_<id>" の要素を Invoke
            AutomationElement TheElement = StandardDialogResolver.FindUiaElement(
                InStructure.Root, new PropertyCondition(AutomationElement.AutomationIdProperty, CommandButtonPrefix + InButton.Id.ToString(CultureInfo.InvariantCulture)));
            if (StandardDialogResolver.TryUiaInvoke(TheElement))
                return "uiaInvoke";

            // 3. BM_CLICK をボタン HWND へ
            if (InButton.Handle != 0
                && StandardDialogResolver.TrySendMessage(new IntPtr(InButton.Handle), BM_CLICK, IntPtr.Zero, IntPtr.Zero, out TheUnused))
                return "bmClick";

            return null;
        }

        /// <summary>"CommandButton_101" のような AutomationId から ID を取り出す。</summary>
        /// <param name="InAutomationId">AutomationId。</param>
        /// <param name="InPrefix">接頭辞。</param>
        /// <param name="OutId">取り出した ID。</param>
        /// <returns>数値として解釈できたら true。</returns>
        private static bool TryParseId(string InAutomationId, string InPrefix, out int OutId)
        {
            return int.TryParse(InAutomationId.Substring(InPrefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out OutId);
        }

        /// <summary>TogglePattern からチェック状態を読む。取れなければ null。</summary>
        /// <param name="InElement">チェックボックス要素。</param>
        /// <returns>チェック状態。</returns>
        private static bool? GetToggleState(AutomationElement InElement)
        {
            try
            {
                if (!InElement.TryGetCurrentPattern(TogglePattern.Pattern, out object ThePattern))
                    return null;
                ToggleState TheState = ((TogglePattern)ThePattern).Current.ToggleState;
                return TheState == ToggleState.Indeterminate ? (bool?)null : TheState == ToggleState.On;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>ラジオボタンの HWND へ BM_GETCHECK を送って選択状態を読む。取れなければ null。</summary>
        /// <param name="InHandle">ラジオボタンの HWND（0 なら不明）。</param>
        /// <returns>選択状態。</returns>
        private static bool? GetRadioChecked(long InHandle)
        {
            if (InHandle == 0)
                return null;
            if (!StandardDialogResolver.TrySendMessage(new IntPtr(InHandle), BM_GETCHECK, IntPtr.Zero, IntPtr.Zero, out IntPtr TheResult))
                return null;
            return (TheResult.ToInt64() & BST_CHECKED) == BST_CHECKED;
        }
    }
}
