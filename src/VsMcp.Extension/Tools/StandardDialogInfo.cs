using System.Collections.Generic;
using Newtonsoft.Json;

namespace VsMcp.Extension.Tools
{
    /// <summary>標準ダイアログの押しボタン 1 個の観測情報。</summary>
    internal sealed class StandardDialogButtonInfo
    {
        /// <summary>コントロール ID（MessageBox は標準 ID、TaskDialog は標準 ID または custom button ID）。</summary>
        [JsonProperty("id")]
        public int Id { get; set; }

        /// <summary>ID に対応する論理 action（ok / cancel / yes / no など）。標準 ID でなければ null。</summary>
        [JsonProperty("action")]
        public string Action { get; set; }

        /// <summary>表示文字列（アクセラレータの & を除いた観測値。操作の正本にはしない）。</summary>
        [JsonProperty("text")]
        public string Text { get; set; }

        /// <summary>既定ボタンか（観測値）。</summary>
        [JsonProperty("isDefault")]
        public bool IsDefault { get; set; }

        /// <summary>有効か。</summary>
        [JsonProperty("isEnabled")]
        public bool IsEnabled { get; set; }

        /// <summary>ボタンの HWND（Win32 の Button コントロール）。無ければ 0。JSON には出さない。</summary>
        [JsonIgnore]
        public long Handle { get; set; }

        /// <summary>UIA の AutomationId（TaskDialog は "CommandButton_&lt;id&gt;"）。JSON には出さない。</summary>
        [JsonIgnore]
        public string AutomationId { get; set; }
    }

    /// <summary>TaskDialog のラジオボタン 1 個の観測情報。</summary>
    internal sealed class StandardDialogRadioButtonInfo
    {
        /// <summary>ラジオボタン ID。</summary>
        [JsonProperty("id")]
        public int Id { get; set; }

        /// <summary>表示文字列。</summary>
        [JsonProperty("text")]
        public string Text { get; set; }

        /// <summary>選択されているか。判定できなければ null。</summary>
        [JsonProperty("isChecked")]
        public bool? IsChecked { get; set; }

        /// <summary>有効か。</summary>
        [JsonProperty("isEnabled")]
        public bool IsEnabled { get; set; }
    }

    /// <summary>
    /// 標準ダイアログの構造化情報。前半はウィンドウ情報（WindowInfo と同じ観測値）、後半は Adapter が取得した内容。
    /// 表示後のウィンドウから観測できる値だけを持ち、元 API の flags（MB_* など）は推測しない。
    /// TaskDialog 固有の項目は取得できたときだけ JSON に出す。
    /// </summary>
    internal sealed class StandardDialogInfo
    {
        /// <summary>messageBox / taskDialog / unknownStandardDialog。</summary>
        [JsonProperty("dialogType")]
        public string DialogType { get; set; }

        [JsonProperty("handle")]
        public long Handle { get; set; }

        [JsonProperty("handleHex")]
        public string HandleHex { get; set; }

        [JsonProperty("title")]
        public string Title { get; set; }

        [JsonProperty("className")]
        public string ClassName { get; set; }

        [JsonProperty("processId")]
        public uint ProcessId { get; set; }

        [JsonProperty("ownerHandle")]
        public long OwnerHandle { get; set; }

        [JsonProperty("isVisible")]
        public bool IsVisible { get; set; }

        [JsonProperty("isEnabled")]
        public bool IsEnabled { get; set; }

        [JsonProperty("isForeground")]
        public bool IsForeground { get; set; }

        [JsonProperty("isModalCandidate")]
        public bool IsModalCandidate { get; set; }

        [JsonProperty("modalCandidateReason")]
        public string ModalCandidateReason { get; set; }

        [JsonProperty("bounds")]
        public string Bounds { get; set; }

        [JsonProperty("dpi")]
        public int Dpi { get; set; }

        [JsonProperty("monitor")]
        public string Monitor { get; set; }

        /// <summary>MessageBox の本文（Static ID 0xFFFF）。TaskDialog では null。</summary>
        [JsonProperty("message")]
        public string Message { get; set; }

        /// <summary>TaskDialog のメインインストラクション。MessageBox では null。</summary>
        [JsonProperty("mainInstruction")]
        public string MainInstruction { get; set; }

        /// <summary>TaskDialog の本文。MessageBox では null。</summary>
        [JsonProperty("content")]
        public string Content { get; set; }

        /// <summary>押しボタン一覧（表示順）。</summary>
        [JsonProperty("buttons")]
        public List<StandardDialogButtonInfo> Buttons { get; set; } = new List<StandardDialogButtonInfo>();

        /// <summary>既定ボタンの ID。判定できなければ null。</summary>
        [JsonProperty("defaultButton")]
        public int? DefaultButton { get; set; }

        /// <summary>TaskDialog の検証チェックボックスの文字列。無ければ出力しない。</summary>
        [JsonProperty("verificationText", NullValueHandling = NullValueHandling.Ignore)]
        public string VerificationText { get; set; }

        /// <summary>TaskDialog の検証チェックボックスの状態。取得できなければ出力しない。</summary>
        [JsonProperty("verificationChecked", NullValueHandling = NullValueHandling.Ignore)]
        public bool? VerificationChecked { get; set; }

        /// <summary>TaskDialog のフッター。無ければ出力しない。</summary>
        [JsonProperty("footer", NullValueHandling = NullValueHandling.Ignore)]
        public string Footer { get; set; }

        /// <summary>TaskDialog の展開情報（展開表示中に UIA から取れた場合のみ）。無ければ出力しない。</summary>
        [JsonProperty("expandedInformation", NullValueHandling = NullValueHandling.Ignore)]
        public string ExpandedInformation { get; set; }

        /// <summary>TaskDialog のラジオボタン一覧。無ければ出力しない。</summary>
        [JsonProperty("radioButtons", NullValueHandling = NullValueHandling.Ignore)]
        public List<StandardDialogRadioButtonInfo> RadioButtons { get; set; }

        /// <summary>WindowInfo の観測値をコピーして骨格を作る。</summary>
        /// <param name="InDialogType">分類結果。</param>
        /// <param name="InWindow">元のウィンドウ情報。</param>
        /// <returns>内容未設定の StandardDialogInfo。</returns>
        public static StandardDialogInfo FromWindow(string InDialogType, WindowInfo InWindow)
        {
            return new StandardDialogInfo
            {
                DialogType = InDialogType,
                Handle = InWindow.Handle,
                HandleHex = InWindow.HandleHex,
                Title = InWindow.Title,
                ClassName = InWindow.ClassName,
                ProcessId = InWindow.ProcessId,
                OwnerHandle = InWindow.OwnerHandle,
                IsVisible = InWindow.IsVisible,
                IsEnabled = InWindow.IsEnabled,
                IsForeground = InWindow.IsForeground,
                IsModalCandidate = InWindow.IsModalCandidate,
                ModalCandidateReason = InWindow.ModalCandidateReason,
                Bounds = InWindow.Bounds,
                Dpi = InWindow.Dpi,
                Monitor = InWindow.Monitor,
            };
        }
    }
}
