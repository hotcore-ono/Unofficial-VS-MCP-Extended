using System;
using System.Linq;

namespace VsMcp.Extension.Tools
{
    /// <summary>
    /// IFileOpenDialog + FOS_PICKFOLDERS（Folder picker、.NET の OpenFolderDialog）の Adapter。実測した構造:
    ///  ダイアログ直下に Static(1090) "フォルダー:" と プレーンな Edit(1152) のフォルダー名欄、ファイルの種類コンボは無し、
    ///  Button(1) "フォルダーの選択" / Button(2) "キャンセル"。一覧にはフォルダーだけが表示される。
    ///  legacy SHBrowseForFolder（SysTreeView32 だけで DUIViewWndClassName を持たない）は File Dialog と判定されず unknown 扱いになる。
    /// </summary>
    internal sealed class FolderDialogAdapter : IStandardFileDialogAdapter
    {
        /// <summary>フォルダー名 Edit のコントロール ID。</summary>
        private const int FolderNameControlId = 1152;

        public string Mode => StandardFileDialogResolver.ModeFolder;

        public string DialogType => StandardFileDialogResolver.TypeFolder;

        /// <summary>直下に Edit(1152) があれば Folder。</summary>
        /// <param name="InStructure">観測構造。</param>
        /// <returns>Folder なら true。</returns>
        public bool CanHandle(StandardDialogStructure InStructure)
        {
            return FindFileNameEdit(InStructure) != null;
        }

        /// <summary>クラス Edit・ID 1152 の直下コントロール。</summary>
        /// <param name="InStructure">観測構造。</param>
        /// <returns>Edit の観測値。無ければ null。</returns>
        public DialogChildControl FindFileNameEdit(StandardDialogStructure InStructure)
        {
            return InStructure.DirectChildren.FirstOrDefault(TheChild =>
                TheChild.ControlId == FolderNameControlId && string.Equals(TheChild.ClassName, "Edit", StringComparison.Ordinal));
        }

        /// <summary>Folder picker にファイルの種類コンボは無い。</summary>
        /// <param name="InStructure">観測構造。</param>
        /// <returns>常に null。</returns>
        public DialogChildControl FindFileTypeCombo(StandardDialogStructure InStructure)
        {
            return null;
        }
    }
}
