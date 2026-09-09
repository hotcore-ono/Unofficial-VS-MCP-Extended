using System;
using System.Linq;

namespace VsMcp.Extension.Tools
{
    /// <summary>
    /// IFileOpenDialog（Open File）の Adapter。実測した構造:
    ///  ダイアログ直下に ComboBoxEx32(1148) → ComboBox(1148) → Edit(1148) のファイル名欄と、ComboBox(1136) のファイルの種類、
    ///  Button(1) "開く" / Button(2) "キャンセル"。UIA でも AutomationId '1148' / '1136' / '1' / '2'。
    /// </summary>
    internal sealed class FileOpenDialogAdapter : IStandardFileDialogAdapter
    {
        /// <summary>ファイル名 Edit の標準コントロール ID（cmb13 / edt1 相当）。</summary>
        private const int FileNameControlId = 1148;

        /// <summary>ファイルの種類 ComboBox の標準コントロール ID（cmb1）。</summary>
        private const int FileTypeControlId = 1136;

        public string Mode => StandardFileDialogResolver.ModeOpen;

        public string DialogType => StandardFileDialogResolver.TypeOpen;

        /// <summary>Edit(1148) があれば Open。</summary>
        /// <param name="InStructure">観測構造。</param>
        /// <returns>Open なら true。</returns>
        public bool CanHandle(StandardDialogStructure InStructure)
        {
            return FindFileNameEdit(InStructure) != null;
        }

        /// <summary>クラス Edit・ID 1148 のコントロール。</summary>
        /// <param name="InStructure">観測構造。</param>
        /// <returns>Edit の観測値。無ければ null。</returns>
        public DialogChildControl FindFileNameEdit(StandardDialogStructure InStructure)
        {
            return InStructure.Children.FirstOrDefault(TheChild =>
                TheChild.ControlId == FileNameControlId && string.Equals(TheChild.ClassName, "Edit", StringComparison.Ordinal));
        }

        /// <summary>クラス ComboBox・ID 1136 のコントロール。</summary>
        /// <param name="InStructure">観測構造。</param>
        /// <returns>ComboBox の観測値。無ければ null。</returns>
        public DialogChildControl FindFileTypeCombo(StandardDialogStructure InStructure)
        {
            return InStructure.Children.FirstOrDefault(TheChild =>
                TheChild.ControlId == FileTypeControlId && string.Equals(TheChild.ClassName, "ComboBox", StringComparison.Ordinal));
        }
    }
}
