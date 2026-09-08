using Newtonsoft.Json;

namespace VsMcp.Extension.Tools
{
    /// <summary>
    /// デバッグ対象プロセスに属するトップレベルウィンドウ 1 件の情報。
    /// HWND を識別子の正本とし、後続ツール（任意ウィンドウのキャプチャ等）へ引き渡す。
    /// JSON プロパティ名は既存 ui_* ツールの戻り値（camelCase）に合わせる。
    /// </summary>
    internal sealed class WindowInfo
    {
        /// <summary>HWND の 10 進値。後続ツールへ渡す際の識別子。</summary>
        [JsonProperty("handle")]
        public long Handle { get; set; }

        /// <summary>HWND の 16 進表現（例: 0x0001E240）。人間が確認しやすい補助表記。</summary>
        [JsonProperty("handleHex")]
        public string HandleHex { get; set; }

        /// <summary>ウィンドウタイトル（GetWindowTextW）。取得できない場合は空文字。</summary>
        [JsonProperty("title")]
        public string Title { get; set; }

        /// <summary>ウィンドウクラス名（GetClassNameW）。WPF は HwndWrapper[...]、標準ダイアログは #32770 など。</summary>
        [JsonProperty("className")]
        public string ClassName { get; set; }

        /// <summary>ウィンドウを所有するプロセス ID。</summary>
        [JsonProperty("processId")]
        public uint ProcessId { get; set; }

        /// <summary>IsWindowVisible の結果。最小化中でも可視扱いになる点に注意。</summary>
        [JsonProperty("isVisible")]
        public bool IsVisible { get; set; }

        /// <summary>IsWindowEnabled の結果。モーダルダイアログの Owner は false になる。</summary>
        [JsonProperty("isEnabled")]
        public bool IsEnabled { get; set; }

        /// <summary>IsIconic の結果（最小化中なら true）。</summary>
        [JsonProperty("isMinimized")]
        public bool IsMinimized { get; set; }

        /// <summary>Windows 全体のフォアグラウンドウィンドウと一致する場合 true。</summary>
        [JsonProperty("isForeground")]
        public bool IsForeground { get; set; }

        /// <summary>Owner ウィンドウの HWND（10 進）。Owner が無い場合は 0。</summary>
        [JsonProperty("ownerHandle")]
        public long OwnerHandle { get; set; }

        /// <summary>
        /// モーダルダイアログ「候補」の推定値。推測であり、モーダルであることを断定する値ではない。
        /// 根拠は <see cref="ModalCandidateReason"/> を参照。
        /// </summary>
        [JsonProperty("isModalCandidate")]
        public bool IsModalCandidate { get; set; }

        /// <summary>
        /// IsModalCandidate の根拠。"ownerDisabled"（Owner が無効化されている）、
        /// "siblingDisabled"（同一スレッドの Owner なし可視ウィンドウが無効化されている）、候補でない場合は null。
        /// </summary>
        [JsonProperty("modalCandidateReason")]
        public string ModalCandidateReason { get; set; }

        /// <summary>ウィンドウを作成したスレッド ID。モーダル候補判定（同一スレッド判定）専用で JSON には出さない。</summary>
        [JsonIgnore]
        public uint ThreadId { get; set; }

        /// <summary>
        /// ウィンドウ矩形 "x,y,width,height"。スクリーン座標系の物理ピクセルで、
        /// 既存 UI Automation の bounds（BuildElementInfo）と同じ座標空間。取得できない場合は null。
        /// </summary>
        [JsonProperty("bounds")]
        public string Bounds { get; set; }

        /// <summary>ウィンドウの DPI（GetDpiForWindow）。取得できない環境では 96。</summary>
        [JsonProperty("dpi")]
        public int Dpi { get; set; }

        /// <summary>ウィンドウが主に表示されているモニターのデバイス名（例: \\.\DISPLAY1）。取得できない場合は null。</summary>
        [JsonProperty("monitor")]
        public string Monitor { get; set; }
    }
}
