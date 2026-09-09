namespace VsMcp.Extension.Tools
{
    /// <summary>
    /// 標準ダイアログ 1 種類（MessageBox / TaskDialog）の判定・情報取得・操作を担う Adapter。
    /// 判定は表示後のウィンドウ構造（子コントロール・UIA）だけを根拠にし、操作は標準コントロール ID を正本にする。
    /// </summary>
    internal interface IStandardDialogAdapter
    {
        /// <summary>この Adapter が返す dialogType（messageBox / taskDialog）。</summary>
        string DialogType { get; }

        /// <summary>観測した構造がこの種類のダイアログか判定する。</summary>
        /// <param name="InStructure">子コントロール構造。</param>
        /// <returns>扱える場合 true。</returns>
        bool CanHandle(StandardDialogStructure InStructure);

        /// <summary>構造化情報を取得する。取得できない項目は null のままにし、推測で埋めない。</summary>
        /// <param name="InWindow">ダイアログのウィンドウ情報。</param>
        /// <param name="InStructure">子コントロール構造。</param>
        /// <returns>構造化情報。</returns>
        StandardDialogInfo GetInfo(WindowInfo InWindow, StandardDialogStructure InStructure);

        /// <summary>ボタンを押す。方式を順に試し、成功した方式名を返す。全て失敗なら null（例外は投げない）。</summary>
        /// <param name="InInfo">直前に取得した構造化情報。</param>
        /// <param name="InButton">押すボタン（存在・有効性は呼び出し側で検証済み）。</param>
        /// <param name="InStructure">子コントロール構造。</param>
        /// <returns>使用した方式名（uiaInvoke / bmClick / wmCommand / tdmClickButton）。失敗なら null。</returns>
        string Execute(StandardDialogInfo InInfo, StandardDialogButtonInfo InButton, StandardDialogStructure InStructure);
    }
}
