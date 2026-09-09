using System;
using System.Linq;

namespace VsMcp.Extension.Tools
{
    /// <summary>
    /// IFileSaveDialog（Save File）の Adapter。実測した構造:
    ///  Open と違い ID 1148 / 1136 は無く、DirectUI 内の FloatNotifySink → ComboBox(0) → Edit(1001) がファイル名欄、
    ///  FloatNotifySink → ComboBox(0)（Edit 子なし）がファイルの種類。UIA では FileNameControlHost / FileTypeControlHost 配下。
    ///  Button(1) "保存" / Button(2) "キャンセル"。
    /// </summary>
    internal sealed class FileSaveDialogAdapter : IStandardFileDialogAdapter
    {
        /// <summary>Save のファイル名 Edit のコントロール ID。</summary>
        private const int FileNameControlId = 1001;

        /// <summary>DirectUI がホストするコントロールの親クラス名。</summary>
        private const string HostClassName = "FloatNotifySink";

        public string Mode => StandardFileDialogResolver.ModeSave;

        public string DialogType => StandardFileDialogResolver.TypeSave;

        /// <summary>Open / Folder の Edit（1148 / 1152）が無く、ComboBox 配下の Edit(1001) があれば Save。</summary>
        /// <param name="InStructure">観測構造。</param>
        /// <returns>Save なら true。</returns>
        public bool CanHandle(StandardDialogStructure InStructure)
        {
            bool TheHasOtherEdit = InStructure.Children.Any(TheChild =>
                string.Equals(TheChild.ClassName, "Edit", StringComparison.Ordinal) && (TheChild.ControlId == 1148 || TheChild.ControlId == 1152));
            return !TheHasOtherEdit && FindFileNameEdit(InStructure) != null;
        }

        /// <summary>クラス Edit・ID 1001 で、親が ComboBox のコントロール（アドレスバーの ToolbarWindow32(1001) と区別する）。</summary>
        /// <param name="InStructure">観測構造。</param>
        /// <returns>Edit の観測値。無ければ null。</returns>
        public DialogChildControl FindFileNameEdit(StandardDialogStructure InStructure)
        {
            return InStructure.Children.FirstOrDefault(TheChild =>
                TheChild.ControlId == FileNameControlId
                && string.Equals(TheChild.ClassName, "Edit", StringComparison.Ordinal)
                && IsClass(InStructure, TheChild.Parent, "ComboBox"));
        }

        /// <summary>FloatNotifySink 配下の ComboBox のうち、Edit 子を持たないもの（ファイルの種類）。</summary>
        /// <param name="InStructure">観測構造。</param>
        /// <returns>ComboBox の観測値。無ければ null。</returns>
        public DialogChildControl FindFileTypeCombo(StandardDialogStructure InStructure)
        {
            return InStructure.Children.FirstOrDefault(TheChild =>
                string.Equals(TheChild.ClassName, "ComboBox", StringComparison.Ordinal)
                && IsClass(InStructure, TheChild.Parent, HostClassName)
                && !InStructure.Children.Any(TheGrandChild => TheGrandChild.Parent == TheChild.Handle && string.Equals(TheGrandChild.ClassName, "Edit", StringComparison.Ordinal)));
        }

        /// <summary>HWND の観測クラス名が指定値か。</summary>
        /// <param name="InStructure">観測構造。</param>
        /// <param name="InHandle">対象 HWND。</param>
        /// <param name="InClassName">期待するクラス名。</param>
        /// <returns>一致すれば true。</returns>
        private static bool IsClass(StandardDialogStructure InStructure, IntPtr InHandle, string InClassName)
        {
            DialogChildControl TheControl = InStructure.FindByHandle(InHandle.ToInt64());
            return TheControl != null && string.Equals(TheControl.ClassName, InClassName, StringComparison.Ordinal);
        }
    }
}
