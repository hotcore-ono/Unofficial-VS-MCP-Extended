using System;
using System.IO;
using Newtonsoft.Json;

namespace VsMcp.Extension.Tools
{
    /// <summary>
    /// Extended (Phase 10): 診断基盤の設定（指示書 §9）。正本は
    /// %LOCALAPPDATA%\Unofficial-VS-MCP-Extended\Config\diagnostics.json で、未知のキーは無視し、欠けたキーは既定値を使う。
    /// 設定ファイルが壊れていても VS / MCP server を失敗させず、`.invalid-<timestamp>` へ退避して既定値で続行する。
    /// </summary>
    internal sealed class DiagnosticSettings
    {
        /// <summary>ログのルートフォルダー名（%LOCALAPPDATA% 配下）。</summary>
        private const string _CONFIG_FOLDER_NAME = "Config";

        /// <summary>設定ファイル名。</summary>
        private const string _CONFIG_FILE_NAME = "diagnostics.json";

        /// <summary>設定ファイルの中身が JSON として壊れていたことを表す理由。</summary>
        public const string CONFIG_FAILURE_PARSE = "parse";

        /// <summary>設定ファイルを読めなかった（権限・ロック・I/O）ことを表す理由。</summary>
        public const string CONFIG_FAILURE_IO = "io";

        /// <summary>診断全体の有効・無効。false のときはラッパーも計装も素通しする。</summary>
        [JsonProperty("enabled")]
        public bool IsEnabled { get; set; } = true;

        /// <summary>記録する水準（off / error / warning / info / verbose / trace）。</summary>
        [JsonProperty("level")]
        public string Level { get; set; } = "info";

        /// <summary>JSONL ファイルへ書くか。</summary>
        [JsonProperty("writeJsonl")]
        public bool IsJsonlWritten { get; set; } = true;

        /// <summary>Visual Studio の Output pane へ 1 行形式で書くか（Info 以下のみ）。</summary>
        [JsonProperty("writeOutputPane")]
        public bool IsOutputPaneWritten { get; set; } = true;

        /// <summary>1 ファイルの上限サイズ（MB）。超えたら part-NNN へローテーションする。</summary>
        [JsonProperty("maxFileSizeMb")]
        public int MaxFileSizeMb { get; set; } = 16;

        /// <summary>ログを保持する日数。これより古い日付フォルダーは起動時に削除する。</summary>
        [JsonProperty("retentionDays")]
        public int RetentionDays { get; set; } = 30;

        /// <summary>保持する session ファイルの最大数。超えた分は古いものから削除する。</summary>
        [JsonProperty("maxSessionFiles")]
        public int MaxSessionFiles { get; set; } = 100;

        /// <summary>Error と選択した Warning のときに詳細状態ダンプを追加記録するか。</summary>
        [JsonProperty("errorDetailDump")]
        public bool IsErrorDetailDumpEnabled { get; set; } = true;

        /// <summary>Error のときに対象ウィンドウのスクリーンショットを保存するか（既定は無効）。</summary>
        [JsonProperty("captureScreenshotOnError")]
        public bool IsScreenshotCapturedOnError { get; set; } = false;

        /// <summary>UI のテキスト（UIA Name / Title / Value）を本文のまま記録してよいか。</summary>
        [JsonProperty("includeUiText")]
        public bool IsUiTextIncluded { get; set; } = false;

        /// <summary>ファイル名・パスを本文のまま記録してよいか。</summary>
        [JsonProperty("includeFilePaths")]
        public bool IsFilePathsIncluded { get; set; } = false;

        /// <summary>記録する文字列 1 件の最大長。超えた分は切って "…" を付ける。</summary>
        [JsonProperty("maxStringLength")]
        public int MaxStringLength { get; set; } = 200;

        /// <summary>詳細状態ダンプ 1 件の最大バイト数。</summary>
        [JsonProperty("maxDumpBytes")]
        public int MaxDumpBytes { get; set; } = 65536;

        /// <summary>詳細状態ダンプに載せるウィンドウの最大数。</summary>
        [JsonProperty("maxWindows")]
        public int MaxWindows { get; set; } = 20;

        /// <summary>詳細状態ダンプに載せる UI 候補の最大数。</summary>
        [JsonProperty("maxUiCandidates")]
        public int MaxUiCandidates { get; set; } = 10;

        /// <summary>書き込みキューの容量（件）。満杯のときは trace / verbose から捨てる。</summary>
        [JsonProperty("queueCapacity")]
        public int QueueCapacity { get; set; } = 4096;

        /// <summary>読み込み時に設定ファイルが壊れていた場合 true（Hub が warning を 1 件出すために使う）。</summary>
        [JsonIgnore]
        public bool IsConfigInvalid { get; set; }

        /// <summary>壊れた設定ファイルの退避先。退避していない場合は null。</summary>
        [JsonIgnore]
        public string InvalidConfigPath { get; set; }

        /// <summary>既定値で続行した理由（<see cref="CONFIG_FAILURE_PARSE"/> / <see cref="CONFIG_FAILURE_IO"/>）。正常に読めた場合は null。</summary>
        [JsonIgnore]
        public string ConfigFailureKind { get; set; }

        /// <summary>設定ファイルの絶対パス。</summary>
        [JsonIgnore]
        public string ConfigFilePath { get { return GetConfigFilePath(); } }

        /// <summary>%LOCALAPPDATA%\Unofficial-VS-MCP-Extended\ の絶対パス。</summary>
        /// <returns>ルートフォルダーの絶対パス。</returns>
        public static string GetRootFolder()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                DiagnosticConstants.ROOT_FOLDER_NAME);
        }

        /// <summary>ルート配下のサブフォルダーの絶対パスを組み立てる（作成はしない）。</summary>
        /// <param name="InRelativePath">"Logs" のような相対パス。</param>
        /// <returns>絶対パス。</returns>
        public static string GetFolder(string InRelativePath)
        {
            return Path.Combine(GetRootFolder(), InRelativePath);
        }

        /// <summary>設定ファイルの絶対パスを組み立てる。</summary>
        /// <returns>diagnostics.json の絶対パス。</returns>
        public static string GetConfigFilePath()
        {
            return Path.Combine(GetFolder(_CONFIG_FOLDER_NAME), _CONFIG_FILE_NAME);
        }

        /// <summary>
        /// 設定ファイルを読み込む。無ければ既定値で新規作成し、中身が壊れていれば `.invalid-<yyyyMMdd-HHmmss>` へ退避して
        /// 既定値の設定ファイルを作り直す。読み取り自体が失敗した場合（権限・ロック・I/O）は、次回読めるかもしれない
        /// 設定を壊さないよう退避せず、既定値のまま続行する。どの失敗でも例外を投げず、必ず使える設定を返す。
        /// </summary>
        /// <returns>読み込んだ設定（失敗時は既定値）。</returns>
        public static DiagnosticSettings Load()
        {
            DiagnosticSettings TheSettings = new DiagnosticSettings();
            string ThePath = GetConfigFilePath();
            try
            {
                if (!File.Exists(ThePath))
                {
                    TheSettings.TrySave();
                    return TheSettings;
                }

                string TheJson = File.ReadAllText(ThePath);
                DiagnosticSettings TheLoaded = JsonConvert.DeserializeObject<DiagnosticSettings>(TheJson);
                if (TheLoaded == null)
                {
                    throw new JsonException("diagnostics.json is empty or null");
                }
                TheLoaded.Normalize();
                return TheLoaded;
            }
            catch (JsonException)
            {
                // JSON として壊れている場合だけ退避する（JsonReaderException も JsonException の派生なのでここで受かる）
                TheSettings.IsConfigInvalid = true;
                TheSettings.ConfigFailureKind = CONFIG_FAILURE_PARSE;
                TheSettings.InvalidConfigPath = TryQuarantineInvalidConfig(ThePath);
                TheSettings.TrySave();
                return TheSettings;
            }
            catch (Exception)
            {
                // 読めなかっただけの場合は退避しない（正しい設定を `.invalid-*` にして失わないため）
                TheSettings.IsConfigInvalid = true;
                TheSettings.ConfigFailureKind = CONFIG_FAILURE_IO;
                return TheSettings;
            }
        }

        /// <summary>壊れた設定ファイルを `.invalid-<yyyyMMdd-HHmmss>` へ退避する。失敗しても無視する。</summary>
        /// <param name="InPath">壊れた設定ファイルの絶対パス。</param>
        /// <returns>退避先の絶対パス。退避できなかった場合は null。</returns>
        private static string TryQuarantineInvalidConfig(string InPath)
        {
            try
            {
                if (!File.Exists(InPath))
                {
                    return null;
                }
                string TheTarget = InPath + ".invalid-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Move(InPath, TheTarget);
                return TheTarget;
            }
            catch (Exception)
            {
                // 退避できなくても既定値で続行する（VS を失敗させない）
                return null;
            }
        }

        /// <summary>
        /// 現在の設定を diagnostics.json へ書き出す。diagnostics_set_level persist=true と、設定ファイルが無いときの自動作成で使う。
        /// </summary>
        /// <returns>書き出せたら true。</returns>
        public bool TrySave()
        {
            try
            {
                string ThePath = GetConfigFilePath();
                Directory.CreateDirectory(Path.GetDirectoryName(ThePath));
                File.WriteAllText(ThePath, JsonConvert.SerializeObject(this, Formatting.Indented));
                return true;
            }
            catch (Exception)
            {
                // 書けなくても診断は続行する（保存できないことは status の writerHealthy とは別問題）
                return false;
            }
        }

        /// <summary>読み込んだ値が範囲外・無意味な場合に安全な範囲へ丸める。</summary>
        public void Normalize()
        {
            if (MaxFileSizeMb < 1)
            {
                MaxFileSizeMb = 1;
            }
            if (MaxFileSizeMb > 1024)
            {
                MaxFileSizeMb = 1024;
            }
            if (RetentionDays < 1)
            {
                RetentionDays = 1;
            }
            if (MaxSessionFiles < 1)
            {
                MaxSessionFiles = 1;
            }
            if (MaxStringLength < 16)
            {
                MaxStringLength = 16;
            }
            if (MaxStringLength > 8192)
            {
                MaxStringLength = 8192;
            }
            if (MaxDumpBytes < 4096)
            {
                MaxDumpBytes = 4096;
            }
            if (MaxWindows < 1)
            {
                MaxWindows = 1;
            }
            if (MaxUiCandidates < 1)
            {
                MaxUiCandidates = 1;
            }
            if (QueueCapacity < 64)
            {
                QueueCapacity = 64;
            }
            if (QueueCapacity > 65536)
            {
                QueueCapacity = 65536;
            }
            if (DiagnosticConstants.ParseLevel(Level, DiagnosticLevel.Info) == DiagnosticLevel.Info
                && !string.Equals(Level, "info", StringComparison.OrdinalIgnoreCase))
            {
                // 未知の水準名は既定へ落とす（解析側が読めない値を残さない）
                Level = "info";
            }
        }
    }
}
