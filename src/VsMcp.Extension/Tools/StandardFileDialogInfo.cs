using System.Collections.Generic;
using Newtonsoft.Json;

namespace VsMcp.Extension.Tools
{
    /// <summary>File Dialog の一覧（シェルビュー）にある項目 1 個の観測情報。</summary>
    internal sealed class StandardFileDialogItemInfo
    {
        /// <summary>表示名（UIA ListItem の Name）。</summary>
        [JsonProperty("name")]
        public string Name { get; set; }

        /// <summary>currentFolderPath が判明している場合だけ組み立てた絶対パス。不明なら null（推測しない）。</summary>
        [JsonProperty("path")]
        public string Path { get; set; }

        /// <summary>path が実在するフォルダーなら true、ファイルなら false、判定できなければ null。</summary>
        [JsonProperty("isFolder")]
        public bool? IsFolder { get; set; }

        /// <summary>UIA の AutomationId（一覧内の連番文字列）。</summary>
        [JsonProperty("automationId")]
        public string AutomationId { get; set; }

        /// <summary>SelectionItemPattern.IsSelected。取れなければ null。</summary>
        [JsonProperty("isSelected")]
        public bool? IsSelected { get; set; }
    }

    /// <summary>
    /// 標準 File Dialog（IFileDialog: Open / Save / Folder）の構造化情報。前半は WindowInfo と同じ観測値、
    /// 後半は Adapter / Resolver が UIA と Win32 から観測した内容。取れない値は null / 空配列にし、推測で埋めない。
    /// </summary>
    internal sealed class StandardFileDialogInfo
    {
        /// <summary>openFileDialog / saveFileDialog / folderDialog / unknownFileDialog。</summary>
        [JsonProperty("dialogType")]
        public string DialogType { get; set; }

        /// <summary>open / save / folder / unknown。</summary>
        [JsonProperty("mode")]
        public string Mode { get; set; }

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

        /// <summary>アドレスバーの表示文字列そのまま（例: "アドレス: C:\..."）。取れなければ null。</summary>
        [JsonProperty("currentFolderDisplay")]
        public string CurrentFolderDisplay { get; set; }

        /// <summary>表示文字列から取り出した絶対パス（ルート付きパスとして妥当なときだけ）。取れなければ null。</summary>
        [JsonProperty("currentFolderPath")]
        public string CurrentFolderPath { get; set; }

        /// <summary>ファイル名 Edit の現在値。Edit が無ければ null。</summary>
        [JsonProperty("fileName")]
        public string FileName { get; set; }

        /// <summary>一覧で選択中の項目。</summary>
        [JsonProperty("selectedItems")]
        public List<StandardFileDialogItemInfo> SelectedItems { get; set; } = new List<StandardFileDialogItemInfo>();

        /// <summary>選択中のファイルの種類フィルタの表示文字列。無ければ null。</summary>
        [JsonProperty("fileTypeFilter")]
        public string FileTypeFilter { get; set; }

        /// <summary>ファイルの種類コンボの選択インデックス（0 始まり）。無ければ null。</summary>
        [JsonProperty("fileTypeFilterIndex")]
        public int? FileTypeFilterIndex { get; set; }

        /// <summary>確定ボタン（Control ID 1）。無ければ null。</summary>
        [JsonProperty("confirmButton")]
        public StandardDialogButtonInfo ConfirmButton { get; set; }

        /// <summary>キャンセルボタン（Control ID 2）。無ければ null。</summary>
        [JsonProperty("cancelButton")]
        public StandardDialogButtonInfo CancelButton { get; set; }

        /// <summary>ファイル名 Edit の HWND（set_filename が使う）。JSON には出さない。</summary>
        [JsonIgnore]
        public long FileNameEditHandle { get; set; }

        /// <summary>WindowInfo の観測値をコピーして骨格を作る。</summary>
        /// <param name="InDialogType">分類結果。</param>
        /// <param name="InMode">モード。</param>
        /// <param name="InWindow">元のウィンドウ情報。</param>
        /// <returns>内容未設定の StandardFileDialogInfo。</returns>
        public static StandardFileDialogInfo FromWindow(string InDialogType, string InMode, WindowInfo InWindow)
        {
            return new StandardFileDialogInfo
            {
                DialogType = InDialogType,
                Mode = InMode,
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
