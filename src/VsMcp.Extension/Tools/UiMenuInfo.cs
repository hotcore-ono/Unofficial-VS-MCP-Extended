using System.Collections.Generic;
using Newtonsoft.Json;

namespace VsMcp.Extension.Tools
{
    /// <summary>
    /// Extended: ポップアップメニューの項目 1 個の観測情報。取得できなかった値は null のままにして推測しない
    /// （Win32 メニューの id、WPF のチェック状態など、そもそも公開されない項目がある）。
    /// </summary>
    internal sealed class UiMenuItemInfo
    {
        /// <summary>Win32 メニューのコマンド ID（GetMenuItemInfoW の wID）。取得できない場合や WPF の項目では null。</summary>
        [JsonProperty("id")]
        public int? Id { get; set; }

        /// <summary>表示名（UIA の Name、無ければ Win32 のメニュー文字列）。取得できない場合は null。</summary>
        [JsonProperty("name")]
        public string Name { get; set; }

        /// <summary>UIA の AutomationId。無い場合は空文字または null。</summary>
        [JsonProperty("automationId")]
        public string AutomationId { get; set; }

        /// <summary>UIA の ControlType（例: ControlType.MenuItem）。取得できない場合は null。</summary>
        [JsonProperty("controlType")]
        public string ControlType { get; set; }

        /// <summary>操作できる状態か（UIA の IsEnabled、Win32 の MFS_DISABLED も反映）。</summary>
        [JsonProperty("isEnabled")]
        public bool IsEnabled { get; set; }

        /// <summary>画面外か（UIA の IsOffscreen）。</summary>
        [JsonProperty("isOffscreen")]
        public bool IsOffscreen { get; set; }

        /// <summary>チェック状態。TogglePattern も Win32 の MFS_CHECKED も判定できない場合は null。</summary>
        [JsonProperty("isChecked")]
        public bool? IsChecked { get; set; }

        /// <summary>サブメニューを持つか（ExpandCollapsePattern または Win32 の hSubMenu）。</summary>
        [JsonProperty("hasSubmenu")]
        public bool HasSubmenu { get; set; }

        /// <summary>項目の矩形 "x,y,width,height"（スクリーン物理 px）。取得できない場合は null。</summary>
        [JsonProperty("bounds")]
        public string Bounds { get; set; }

        /// <summary>項目が属するトップレベル HWND（10 進）。特定できない場合は 0。</summary>
        [JsonProperty("rootWindowHandle")]
        public long RootWindowHandle { get; set; }

        /// <summary>対応する UIA パターン名（invoke / expandCollapse / toggle など）。</summary>
        [JsonProperty("patterns")]
        public List<string> Patterns { get; set; } = new List<string>();

        /// <summary>
        /// メニュー内での 0 始まりの並び順。ui_menu_select の index 引数はこの値と一致する項目を選ぶ
        /// （他のセレクターと併用しても、絞り込んだ後の序数ではない）。
        /// </summary>
        [JsonProperty("index")]
        public int Index { get; set; }
    }

    /// <summary>
    /// Extended: 1 つのポップアップメニュー（Win32 #32768 / WPF ContextMenu / 不明なポップアップ）の観測情報。
    /// ウィンドウ項目は <see cref="WindowInfo"/> と同じ観測値で、メニュー固有の項目だけを追加する。
    /// </summary>
    internal sealed class UiMenuInfo
    {
        /// <summary>wpfContextMenu / win32Menu / unknownPopupMenu。</summary>
        [JsonProperty("menuType")]
        public string MenuType { get; set; }

        /// <summary>メニューウィンドウの HWND（10 進）。</summary>
        [JsonProperty("handle")]
        public long Handle { get; set; }

        /// <summary>HWND の 16 進表現。</summary>
        [JsonProperty("handleHex")]
        public string HandleHex { get; set; }

        /// <summary>メニューウィンドウを所有するプロセス ID。</summary>
        [JsonProperty("processId")]
        public uint ProcessId { get; set; }

        /// <summary>Owner ウィンドウの HWND（10 進）。Owner が無い場合は 0。</summary>
        [JsonProperty("ownerHandle")]
        public long OwnerHandle { get; set; }

        /// <summary>ウィンドウクラス名（Win32 メニューは #32768、WPF は HwndWrapper[...]）。</summary>
        [JsonProperty("className")]
        public string ClassName { get; set; }

        /// <summary>メニューウィンドウの矩形 "x,y,width,height"（スクリーン物理 px）。</summary>
        [JsonProperty("bounds")]
        public string Bounds { get; set; }

        /// <summary>IsWindowVisible の結果。</summary>
        [JsonProperty("isVisible")]
        public bool IsVisible { get; set; }

        /// <summary>IsWindowEnabled の結果。</summary>
        [JsonProperty("isEnabled")]
        public bool IsEnabled { get; set; }

        /// <summary>Windows 全体のフォアグラウンドウィンドウと一致する場合 true。</summary>
        [JsonProperty("isForeground")]
        public bool IsForeground { get; set; }

        /// <summary>メニュー項目の一覧（表示順）。</summary>
        [JsonProperty("items")]
        public List<UiMenuItemInfo> Items { get; set; } = new List<UiMenuItemInfo>();

        /// <summary>UIA ルート要素の Name。取得できない場合は null。</summary>
        [JsonProperty("uiaRootName")]
        public string UiaRootName { get; set; }

        /// <summary>UIA ルート要素の ControlType。取得できない場合は null。</summary>
        [JsonProperty("uiaRootControlType")]
        public string UiaRootControlType { get; set; }

        /// <summary>Win32 メニューの HMENU（MN_GETHMENU の結果）。取得できない場合は 0。</summary>
        [JsonProperty("win32MenuHandle")]
        public long Win32MenuHandle { get; set; }

        /// <summary>補足（項目を取得できなかった理由など）。無ければ出力しない。</summary>
        [JsonProperty("note", NullValueHandling = NullValueHandling.Ignore)]
        public string Note { get; set; }

        /// <summary>WindowInfo の観測値をコピーして骨格を作る。</summary>
        /// <param name="InMenuType">分類結果。</param>
        /// <param name="InWindow">元のウィンドウ情報。</param>
        /// <returns>項目未設定の UiMenuInfo。</returns>
        public static UiMenuInfo FromWindow(string InMenuType, WindowInfo InWindow)
        {
            return new UiMenuInfo
            {
                MenuType = InMenuType,
                Handle = InWindow.Handle,
                HandleHex = InWindow.HandleHex,
                ProcessId = InWindow.ProcessId,
                OwnerHandle = InWindow.OwnerHandle,
                ClassName = InWindow.ClassName,
                Bounds = InWindow.Bounds,
                IsVisible = InWindow.IsVisible,
                IsEnabled = InWindow.IsEnabled,
                IsForeground = InWindow.IsForeground,
            };
        }
    }
}
