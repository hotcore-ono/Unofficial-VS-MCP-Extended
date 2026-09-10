using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace VsMcp.Extension.Tools
{
    /// <summary>
    /// Extended (Phase 10): 診断ログの重要度。数値が大きいほど詳細で、設定された水準以下の event だけを記録する
    /// （Off はいっさい記録しない）。既定は Info。
    /// </summary>
    internal enum DiagnosticLevel
    {
        /// <summary>診断を完全に止める。</summary>
        Off = 0,

        /// <summary>Tool の失敗・捕捉した例外・SendInput 失敗・writer 障害。</summary>
        Error = 1,

        /// <summary>fallback の使用・timeout による正常 false・打ち切られた検索・UIA の異常。</summary>
        Warning = 2,

        /// <summary>tool.start / tool.end・解決結果・操作方式・待機結果・メニュー / ダイアログの分類。</summary>
        Info = 3,

        /// <summary>候補数・選ばれた候補の要約・geometry・フォアグラウンド / フォーカス / モーダルの詳細。</summary>
        Verbose = 4,

        /// <summary>ポーリング 1 回ごと・walker の進行・候補 1 件ごとの診断。通常運用では使わない。</summary>
        Trace = 5,
    }

    /// <summary>
    /// Extended (Phase 10): 診断 event のカテゴリ（JSONL の "category"）。指示書 §14 の候補をそのまま定数化したもの。
    /// 文字列の直書きを避け、解析側が既知の集合として扱えるようにする。
    /// </summary>
    internal static class DiagnosticCategory
    {
        /// <summary>セッションの開始 / 終了。</summary>
        public const string SESSION = "session";

        /// <summary>MCP Tool の呼び出し境界（tool.start / tool.end）。</summary>
        public const string TOOL = "tool";

        /// <summary>ウィンドウ解決（HWND 検証・アクティブウィンドウ解決）。</summary>
        public const string WINDOW = "window";

        /// <summary>UI Automation の検索・走査。</summary>
        public const string UIA = "uia";

        /// <summary>モーダル状態の評価と操作の拒否。</summary>
        public const string MODAL = "modal";

        /// <summary>物理矩形 / DPI / モニターの解決。</summary>
        public const string GEOMETRY = "geometry";

        /// <summary>要素の解決・操作の準備と実行。</summary>
        public const string INTERACTION = "interaction";

        /// <summary>キーボード / マウスの合成入力。</summary>
        public const string INPUT = "input";

        /// <summary>待機系ツールのポーリング。</summary>
        public const string WAIT = "wait";

        /// <summary>ウィンドウ / 領域のキャプチャ。</summary>
        public const string CAPTURE = "capture";

        /// <summary>ポップアップ / コンテキストメニューの検出・分類・選択・クローズ。</summary>
        public const string MENU = "menu";

        /// <summary>Windows 標準ダイアログ（MessageBox / TaskDialog）。</summary>
        public const string DIALOG = "dialog";

        /// <summary>Windows 標準ファイルダイアログ。</summary>
        public const string FILE_DIALOG = "fileDialog";

        /// <summary>診断基盤自身（設定・writer・export・marker）。</summary>
        public const string DIAGNOSTICS = "diagnostics";

        /// <summary>捕捉した例外。</summary>
        public const string EXCEPTION = "exception";

        /// <summary>所要時間の計測。</summary>
        public const string PERFORMANCE = "performance";
    }

    /// <summary>
    /// Extended (Phase 10): tool.end の result（指示書 §15）。Tool の戻り値そのものは変えず、
    /// 「成功」「エラー」「正常な false」「タイムアウト」「キャンセル」を後から区別できるようにするための区分。
    /// </summary>
    internal static class DiagnosticToolOutcome
    {
        /// <summary>正常終了（本文に false 系のトップレベルキーが無い）。</summary>
        public const string SUCCESS = "success";

        /// <summary>McpToolResult.IsError または例外。</summary>
        public const string ERROR = "error";

        /// <summary>エラーではないが found / closed / idle / completed が false。</summary>
        public const string NORMAL_FALSE = "normalFalse";

        /// <summary>本文の timedOut が true。</summary>
        public const string TIMEOUT = "timeout";

        /// <summary>OperationCanceledException で中断した。</summary>
        public const string CANCELLED = "cancelled";
    }

    /// <summary>
    /// Extended (Phase 10): 診断 event に載せる例外情報。message は sanitize 済み、stackTrace は上限で切る。
    /// </summary>
    internal sealed class DiagnosticExceptionInfo
    {
        /// <summary>stackTrace として保持する最大文字数。</summary>
        private const int _MAX_STACK_TRACE_LENGTH = 4000;

        /// <summary>例外の型名（完全修飾）。</summary>
        [JsonProperty("type")]
        public string TypeName { get; set; }

        /// <summary>sanitize 済みの例外メッセージ。</summary>
        [JsonProperty("message")]
        public string Message { get; set; }

        /// <summary>COM 例外等の HRESULT。取得できない場合は null。</summary>
        [JsonProperty("hresult", NullValueHandling = NullValueHandling.Ignore)]
        public int? HResult { get; set; }

        /// <summary>スタックトレース（最大 4000 字）。</summary>
        [JsonProperty("stackTrace", NullValueHandling = NullValueHandling.Ignore)]
        public string StackTrace { get; set; }

        /// <summary>内部例外の型名。無い場合は null。</summary>
        [JsonProperty("innerType", NullValueHandling = NullValueHandling.Ignore)]
        public string InnerTypeName { get; set; }

        /// <summary>sanitize 済みの内部例外メッセージ。無い場合は null。</summary>
        [JsonProperty("innerMessage", NullValueHandling = NullValueHandling.Ignore)]
        public string InnerMessage { get; set; }

        /// <summary>
        /// 例外から診断用の情報を組み立てる。メッセージは必ず sanitizer を通し、スタックトレースは上限で切る。
        /// </summary>
        /// <param name="InException">対象の例外。null なら null を返す。</param>
        /// <param name="InSettings">機密ポリシーの設定。</param>
        /// <returns>組み立てた例外情報。</returns>
        public static DiagnosticExceptionInfo FromException(Exception InException, DiagnosticSettings InSettings)
        {
            if (InException == null)
            {
                return null;
            }

            string TheStackTrace = InException.StackTrace;
            if (TheStackTrace != null && TheStackTrace.Length > _MAX_STACK_TRACE_LENGTH)
            {
                TheStackTrace = TheStackTrace.Substring(0, _MAX_STACK_TRACE_LENGTH);
            }

            return new DiagnosticExceptionInfo
            {
                TypeName = InException.GetType().FullName,
                Message = DiagnosticSanitizer.SanitizeText(InException.Message, InSettings),
                HResult = InException.HResult,
                StackTrace = TheStackTrace,
                InnerTypeName = InException.InnerException == null ? null : InException.InnerException.GetType().FullName,
                InnerMessage = InException.InnerException == null
                    ? null
                    : DiagnosticSanitizer.SanitizeText(InException.InnerException.Message, InSettings),
            };
        }
    }

    /// <summary>
    /// Extended (Phase 10): JSONL の 1 行に対応する診断 event（指示書 §6・§34）。
    /// schemaVersion / timestampUtc / sessionId / correlationId / sequence / level / category / event / tool は常に出力し、
    /// それ以外は値があるときだけ出力する（解析側が項目の追加に耐えられるようにするため）。
    /// </summary>
    internal sealed class DiagnosticEvent
    {
        /// <summary>ログ schema のバージョン。項目を追加しても解析側が判別できるようにする。</summary>
        [JsonProperty("schemaVersion")]
        public int SchemaVersion { get; set; } = DiagnosticConstants.SCHEMA_VERSION;

        /// <summary>UTC のタイムスタンプ（ISO 8601 "o" 形式の Z）。</summary>
        [JsonProperty("timestampUtc")]
        public string TimestampUtc { get; set; }

        /// <summary>この VS / MCP server 起動を表すセッション ID。</summary>
        [JsonProperty("sessionId")]
        public string SessionId { get; set; }

        /// <summary>MCP Tool 1 呼び出しを表す相関 ID。呼び出しの外で出た event は "c-none"。</summary>
        [JsonProperty("correlationId")]
        public string CorrelationId { get; set; }

        /// <summary>セッション内で単調増加する順序番号。</summary>
        [JsonProperty("sequence")]
        public long Sequence { get; set; }

        /// <summary>重要度（"error" / "warning" / "info" / "verbose" / "trace"）。</summary>
        [JsonProperty("level")]
        public string Level { get; set; }

        /// <summary>カテゴリ（<see cref="DiagnosticCategory"/>）。</summary>
        [JsonProperty("category")]
        public string Category { get; set; }

        /// <summary>event 名（例: "tool.start"）。</summary>
        [JsonProperty("event")]
        public string EventName { get; set; }

        /// <summary>関連する MCP Tool 名。Tool 呼び出しの外なら null。</summary>
        [JsonProperty("tool")]
        public string Tool { get; set; }

        /// <summary>所要時間（ミリ秒）。計測していない event では null。</summary>
        [JsonProperty("elapsedMs", NullValueHandling = NullValueHandling.Ignore)]
        public long? ElapsedMs { get; set; }

        /// <summary>結果の区分（tool.end なら <see cref="DiagnosticToolOutcome"/>）。</summary>
        [JsonProperty("result", NullValueHandling = NullValueHandling.Ignore)]
        public string Result { get; set; }

        /// <summary>sanitize 済みの短い説明。</summary>
        [JsonProperty("message", NullValueHandling = NullValueHandling.Ignore)]
        public string Message { get; set; }

        /// <summary>event 固有の項目（camelCase のキー）。</summary>
        [JsonProperty("data", NullValueHandling = NullValueHandling.Ignore)]
        public JObject Data { get; set; }

        /// <summary>例外情報。例外を伴わない event では null。</summary>
        [JsonProperty("exception", NullValueHandling = NullValueHandling.Ignore)]
        public DiagnosticExceptionInfo Exception { get; set; }

        /// <summary>この event より前に writer が捨てた件数（捨てていなければ null）。</summary>
        [JsonProperty("dropped", NullValueHandling = NullValueHandling.Ignore)]
        public long? Dropped { get; set; }
    }

    /// <summary>
    /// Extended (Phase 10): 診断基盤の固定値。log schema とディレクトリ構成のバージョンを 1 か所に集める。
    /// </summary>
    internal static class DiagnosticConstants
    {
        /// <summary>JSONL の schemaVersion。</summary>
        public const int SCHEMA_VERSION = 1;

        /// <summary>診断基盤自身のバージョン（manifest / status に載せる）。</summary>
        public const string DIAGNOSTICS_VERSION = "1.0";

        /// <summary>相関 ID が無い（Tool 呼び出しの外）ことを示す値。</summary>
        public const string NO_CORRELATION_ID = "c-none";

        /// <summary>Output pane の名前。</summary>
        public const string OUTPUT_PANE_NAME = "Unofficial VS MCP Extended";

        /// <summary>%LOCALAPPDATA% 配下のルートフォルダー名。</summary>
        public const string ROOT_FOLDER_NAME = "Unofficial-VS-MCP-Extended";

        /// <summary><see cref="DiagnosticLevel"/> を JSONL 用の文字列にする。</summary>
        /// <param name="InLevel">対象の水準。</param>
        /// <returns>小文字の水準名。</returns>
        public static string ToText(DiagnosticLevel InLevel)
        {
            switch (InLevel)
            {
                case DiagnosticLevel.Off: return "off";
                case DiagnosticLevel.Error: return "error";
                case DiagnosticLevel.Warning: return "warning";
                case DiagnosticLevel.Info: return "info";
                case DiagnosticLevel.Verbose: return "verbose";
                case DiagnosticLevel.Trace: return "trace";
                default: return "info";
            }
        }

        /// <summary>水準名を <see cref="DiagnosticLevel"/> に戻す。未知の値は既定値を返す。</summary>
        /// <param name="InText">水準名（大小は無視する）。</param>
        /// <param name="InFallback">解釈できなかったときに返す値。</param>
        /// <returns>解釈した水準。</returns>
        public static DiagnosticLevel ParseLevel(string InText, DiagnosticLevel InFallback)
        {
            if (string.IsNullOrEmpty(InText))
            {
                return InFallback;
            }
            switch (InText.Trim().ToLowerInvariant())
            {
                case "off": return DiagnosticLevel.Off;
                case "error": return DiagnosticLevel.Error;
                case "warning": return DiagnosticLevel.Warning;
                case "info": return DiagnosticLevel.Info;
                case "verbose": return DiagnosticLevel.Verbose;
                case "trace": return DiagnosticLevel.Trace;
                default: return InFallback;
            }
        }
    }
}
