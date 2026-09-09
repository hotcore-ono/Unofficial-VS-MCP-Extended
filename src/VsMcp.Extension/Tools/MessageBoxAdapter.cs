using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Automation;

using static VsMcp.Extension.Tools.NativeMethods;

namespace VsMcp.Extension.Tools
{
    /// <summary>
    /// Win32 MessageBox（user32）の Adapter。実測した構造:
    ///  #32770 直下に Button（ctrlId = 標準 ID 1/2/4/6/7 …、既定ボタンは BS_DEFPUSHBUTTON）と
    ///  Static（ID 20 = アイコン、ID 0xFFFF = 本文）だけが並び、DirectUIHWND は無い。DM_GETDEFID が既定 ID を返す。
    ///  MB_OK では唯一のボタンが ID 2（IDCANCEL）になる（Esc / 閉じるで戻れるようにする Win32 の仕様）。
    /// </summary>
    internal sealed class MessageBoxAdapter : IStandardDialogAdapter
    {
        /// <summary>標準コントロール ID の範囲（IDOK=1 〜 IDCONTINUE=11）。</summary>
        private const int MaxStandardControlId = 11;

        /// <summary>WM_COMMAND の通知コード BN_CLICKED。</summary>
        private const int ButtonClickedNotification = 0;

        public string DialogType => StandardDialogResolver.TypeMessageBox;

        /// <summary>直下が Button（標準 ID）と Static だけで、Button が 1 個以上あれば MessageBox とみなす。</summary>
        /// <param name="InStructure">観測構造。</param>
        /// <returns>MessageBox なら true。</returns>
        public bool CanHandle(StandardDialogStructure InStructure)
        {
            if (InStructure.HasChildOfClass(TaskDialogAdapter.DirectUiClassName))
                return false;

            List<DialogChildControl> TheDirect = InStructure.DirectChildren.ToList();
            if (TheDirect.Count == 0)
                return false;

            bool TheHasButton = false;
            foreach (DialogChildControl TheChild in TheDirect)
            {
                if (string.Equals(TheChild.ClassName, "Button", StringComparison.Ordinal))
                {
                    if (TheChild.ControlId < 1 || TheChild.ControlId > MaxStandardControlId || TheChild.ButtonStyleType > BS_DEFPUSHBUTTON)
                        return false;
                    TheHasButton = true;
                }
                else if (!string.Equals(TheChild.ClassName, "Static", StringComparison.Ordinal))
                {
                    return false;
                }
            }
            return TheHasButton;
        }

        /// <summary>本文・ボタン・既定ボタンを Win32 の観測値から組み立てる。</summary>
        /// <param name="InWindow">ダイアログのウィンドウ情報。</param>
        /// <param name="InStructure">観測構造。</param>
        /// <returns>構造化情報。</returns>
        public StandardDialogInfo GetInfo(WindowInfo InWindow, StandardDialogStructure InStructure)
        {
            StandardDialogInfo TheInfo = StandardDialogInfo.FromWindow(DialogType, InWindow);
            TheInfo.Buttons = StandardDialogResolver.CollectWin32Buttons(InStructure);
            TheInfo.Message = StandardDialogResolver.FindStaticText(InStructure);
            TheInfo.DefaultButton = StandardDialogResolver.ResolveDefaultButtonId(InStructure, TheInfo.Buttons);

            // MB_OK: 唯一のボタンが IDCANCEL(2) のときは論理的に "ok"（構造に基づく Win32 仕様であり表示文字列は見ない）
            if (TheInfo.Buttons.Count == 1 && TheInfo.Buttons[0].Id == 2)
                TheInfo.Buttons[0].Action = "ok";
            return TheInfo;
        }

        /// <summary>UIA InvokePattern → BM_CLICK → WM_COMMAND(BN_CLICKED) の順に試す。</summary>
        /// <param name="InInfo">構造化情報。</param>
        /// <param name="InButton">押すボタン。</param>
        /// <param name="InStructure">観測構造。</param>
        /// <returns>成功した方式名。全滅なら null。</returns>
        public string Execute(StandardDialogInfo InInfo, StandardDialogButtonInfo InButton, StandardDialogStructure InStructure)
        {
            IntPtr TheButtonHandle = new IntPtr(InButton.Handle);

            // 1. UIA: 標準 ID を持つ Button コントロールの HWND で要素を特定して Invoke
            AutomationElement TheElement = StandardDialogResolver.FindUiaElement(
                InStructure.Root, new PropertyCondition(AutomationElement.NativeWindowHandleProperty, (int)InButton.Handle));
            if (StandardDialogResolver.TryUiaInvoke(TheElement))
                return "uiaInvoke";

            // 2. BM_CLICK をボタン自身へ
            if (StandardDialogResolver.TrySendMessage(TheButtonHandle, BM_CLICK, IntPtr.Zero, IntPtr.Zero, out IntPtr TheUnused))
                return "bmClick";

            // 3. WM_COMMAND(BN_CLICKED, id) をダイアログへ
            IntPtr TheWParam = new IntPtr((ButtonClickedNotification << 16) | (InButton.Id & 0xFFFF));
            if (StandardDialogResolver.TrySendMessage(InStructure.Handle, WM_COMMAND, TheWParam, TheButtonHandle, out TheUnused))
                return "wmCommand";

            return null;
        }
    }
}
