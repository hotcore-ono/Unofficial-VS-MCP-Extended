using System;
using System.Diagnostics;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace VsMcp.Extension.Tools
{
    /// <summary>
    /// Extended (Phase 10): MCP Tool 1 呼び出しを表す相関スコープ（指示書 §7）。
    /// 同じ Tool call の中で出た window / uia / modal / geometry / input / wait / capture / fallback / exception の各 event が
    /// 同じ correlationId を持つようにする。
    /// スコープは <see cref="AsyncLocal{T}"/> に置く。AsyncLocal は ExecutionContext ごと流れるため、
    /// <c>Task.Run</c> はもちろん <c>UiTools.RunOnBackgroundSTAAsync</c> が作る <c>Thread.Start</c> の STA スレッドへも伝播する
    /// （ExecutionContext の抑止をしていないため）。スコープが無い実行文脈では <see cref="Current"/> が null になり、Emit は何もしない。
    /// </summary>
    internal sealed class DiagnosticScope
    {
        /// <summary>非同期の流れに沿って伝播する現在のスコープ。</summary>
        private static readonly AsyncLocal<DiagnosticScope> _AsyncCurrent = new AsyncLocal<DiagnosticScope>();

        /// <summary>スコープ開始からの経過時間。</summary>
        private readonly Stopwatch _Stopwatch;

        /// <summary>この Tool 呼び出しの相関 ID（"c-" + Guid 先頭 8 桁）。</summary>
        public string CorrelationId { get; private set; }

        /// <summary>この呼び出しの Tool 名。</summary>
        public string ToolName { get; private set; }

        /// <summary>スコープ開始からの経過ミリ秒。</summary>
        public long ElapsedMs { get { return _Stopwatch.ElapsedMilliseconds; } }

        /// <summary>
        /// この Tool 呼び出しが待機系か（<c>UiWindowTools.ParseWaitOptions</c> が wait.start を出したときに true になる）。
        /// true のとき <see cref="DiagnosticToolRunner"/> が tool.end と同時に wait.end を出す。
        /// </summary>
        public bool IsWaitStarted { get; set; }

        /// <summary>待機系ツールの正規化後タイムアウト（ミリ秒）。wait.end に載せる。</summary>
        public int WaitTimeoutMs { get; set; }

        /// <summary>待機系ツールの正規化後ポーリング間隔（ミリ秒）。wait.end に載せる。</summary>
        public int WaitPollIntervalMs { get; set; }

        /// <summary>
        /// この Tool 呼び出しで詳細状態ダンプを既にスケジュール済みか。
        /// <see cref="DiagnosticErrorDump"/> は同じ correlationId につきダンプを 1 回だけ残すため、
        /// 先に来た具体的な理由（modal.block / menu.fallback）を採用し、後続の tool.error / tool.exception を捨てる。
        /// </summary>
        public bool IsErrorDumpScheduled { get; set; }

        /// <summary>現在のスコープ（非同期の流れ・派生スレッドへ伝播したもの）。無ければ null。</summary>
        public static DiagnosticScope Current
        {
            get { return _AsyncCurrent.Value; }
        }

        /// <summary>スコープを作る（開始時刻の計測もここから始まる）。</summary>
        /// <param name="InToolName">Tool 名。</param>
        private DiagnosticScope(string InToolName)
        {
            ToolName = InToolName;
            CorrelationId = "c-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            _Stopwatch = Stopwatch.StartNew();
        }

        /// <summary>新しい相関スコープを開始し、非同期の流れへ設定する。</summary>
        /// <param name="InToolName">Tool 名。</param>
        /// <returns>開始したスコープ。</returns>
        public static DiagnosticScope Begin(string InToolName)
        {
            DiagnosticScope TheScope = new DiagnosticScope(InToolName);
            _AsyncCurrent.Value = TheScope;
            return TheScope;
        }

        /// <summary>スコープを終了し、非同期の流れの設定を外す。</summary>
        public static void End()
        {
            _AsyncCurrent.Value = null;
        }

        /// <summary>
        /// このスコープの相関 ID で event を記録する。診断が無効・水準未満なら何もしない。例外は投げない。
        /// </summary>
        /// <param name="InLevel">重要度。</param>
        /// <param name="InCategory">カテゴリ。</param>
        /// <param name="InEventName">event 名。</param>
        /// <param name="InFill">data を組み立てるコールバック（記録しない水準では呼ばれない）。null 可。</param>
        public void Emit(DiagnosticLevel InLevel, string InCategory, string InEventName, Action<JObject> InFill)
        {
            DiagnosticHub.EmitCore(InLevel, InCategory, InEventName, ToolName, CorrelationId, null, null, null, InFill, null);
        }
    }
}
