namespace VsMcp.Extension.Tools
{
    /// <summary>
    /// 標準 File Dialog 1 モード（open / save / folder）の判定と、モードごとに位置が違うコントロールの特定を担う Adapter。
    /// 情報取得・操作の共通部分（アドレスバー・一覧・確定/キャンセルボタン）は StandardFileDialogResolver が持つ。
    /// 判定は表示後の Win32 子コントロール構造だけを根拠にし、タイトルや表示文字列は使わない。
    /// </summary>
    internal interface IStandardFileDialogAdapter
    {
        /// <summary>この Adapter のモード（open / save / folder）。</summary>
        string Mode { get; }

        /// <summary>この Adapter が返す dialogType（openFileDialog / saveFileDialog / folderDialog）。</summary>
        string DialogType { get; }

        /// <summary>観測した構造がこのモードの File Dialog か判定する（File Dialog であること自体は Resolver が先に確認する）。</summary>
        /// <param name="InStructure">子コントロール構造。</param>
        /// <returns>扱える場合 true。</returns>
        bool CanHandle(StandardDialogStructure InStructure);

        /// <summary>ファイル名（フォルダー名）入力の Edit コントロールを返す。無ければ null。</summary>
        /// <param name="InStructure">子コントロール構造。</param>
        /// <returns>Edit の観測値。</returns>
        DialogChildControl FindFileNameEdit(StandardDialogStructure InStructure);

        /// <summary>ファイルの種類 ComboBox を返す。無いモード（folder）では null。</summary>
        /// <param name="InStructure">子コントロール構造。</param>
        /// <returns>ComboBox の観測値。</returns>
        DialogChildControl FindFileTypeCombo(StandardDialogStructure InStructure);
    }
}
