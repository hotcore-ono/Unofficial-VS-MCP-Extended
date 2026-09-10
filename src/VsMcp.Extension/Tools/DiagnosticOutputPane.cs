using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using VsMcp.Extension.Services;

namespace VsMcp.Extension.Tools
{
    /// <summary>
    /// Extended (Phase 10): Visual Studio の Output pane "Unofficial VS MCP Extended" への人間向け 1 行出力（指示書 §13）。
    /// 正本は JSONL なので、ここは短い 1 行だけを Info 以下の水準で流す。UI スレッドを長く占有しないよう、
    /// 出力は容量上限つきキュー + 背景スレッドでまとめてから 1 回で書き込む。
    /// 書き込みが連続 3 回失敗した環境では pane 出力だけを無効化し（そのことは JSONL へ 1 回だけ残す）、ファイル出力は続ける。
    /// </summary>
    internal sealed class DiagnosticOutputPane
    {
        /// <summary>pane 出力キューの容量。</summary>
        private const int _QUEUE_CAPACITY = 1024;

        /// <summary>まとめて書き出すまでの待ち時間（ミリ秒）。</summary>
        private const int _BATCH_INTERVAL_MS = 250;

        /// <summary>1 回で書き出す最大行数。</summary>
        private const int _BATCH_LINE_COUNT = 50;

        /// <summary>キューから 1 行取り出すときの待ち時間（ミリ秒）。</summary>
        private const int _TAKE_TIMEOUT_MS = 100;

        /// <summary>この回数だけ連続して書き込みに失敗したら pane 出力を無効化する。</summary>
        private const int _MAX_CONSECUTIVE_FAILURES = 3;

        /// <summary>DTE / UI スレッドアクセサ。</summary>
        private readonly VsServiceAccessor _Accessor;

        /// <summary>書き出し待ちの行。</summary>
        private readonly BlockingCollection<string> _Queue = new BlockingCollection<string>(_QUEUE_CAPACITY);

        /// <summary>背景のバッチスレッド。</summary>
        private System.Threading.Thread _PaneThread;

        /// <summary>取得済みの pane。取得に失敗した場合は null のまま。</summary>
        private OutputWindowPane _Pane;

        /// <summary>pane 出力が使えるか（連続して失敗したら false）。</summary>
        private volatile bool _IsPaneAvailable = true;

        /// <summary>連続した書き込み失敗の回数。</summary>
        private int _ConsecutiveFailureCount;

        /// <summary>無効化の警告を JSONL へ記録したか（0 = 未記録。1 回だけ記録する）。</summary>
        private int _IsFailureReported;

        /// <summary>pane 出力が使える状態か。</summary>
        public bool IsAvailable { get { return _IsPaneAvailable; } }

        /// <summary>pane を作る（この時点では VS には触れない）。</summary>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        public DiagnosticOutputPane(VsServiceAccessor InAccessor)
        {
            _Accessor = InAccessor;
        }

        /// <summary>背景スレッドを開始する。多重に呼んでも 1 本しか起動しない。</summary>
        public void Start()
        {
            if (_PaneThread != null)
            {
                return;
            }
            _PaneThread = new System.Threading.Thread(RunPaneLoop)
            {
                IsBackground = true,
                Name = "VsMcpExtendedDiagnosticsPane",
            };
            _PaneThread.Start();
        }

        /// <summary>
        /// event を 1 行形式にして pane へ流す。Verbose / Trace は pane に出さない（JSONL が正本）。
        /// 例外は投げない。
        /// </summary>
        /// <param name="InEvent">記録する event。</param>
        public void Enqueue(DiagnosticEvent InEvent)
        {
            if (InEvent == null || !_IsPaneAvailable)
            {
                return;
            }
            if (DiagnosticConstants.ParseLevel(InEvent.Level, DiagnosticLevel.Info) > DiagnosticLevel.Info)
            {
                return;
            }
            WriteLine(FormatEvent(InEvent));
        }

        /// <summary>1 行をそのままキューへ入れる（writer 障害の通知などに使う）。例外は投げない。</summary>
        /// <param name="InLine">出力する 1 行。</param>
        public void WriteLine(string InLine)
        {
            if (string.IsNullOrEmpty(InLine) || !_IsPaneAvailable)
            {
                return;
            }
            try
            {
                if (!_Queue.IsAddingCompleted)
                {
                    // 溢れた行は捨てる（pane はあくまで人間向けの補助）
                    _Queue.TryAdd(InLine, 0);
                }
            }
            catch (Exception)
            {
                // pane 出力の失敗で Tool を止めない
            }
        }

        /// <summary>
        /// キューを締め切り、背景スレッドの終了を上限つきで待つ。
        /// 待ち時間は呼び出し側（Hub）が writer と分け合った残り予算として渡す。
        /// </summary>
        /// <param name="InTimeoutMs">背景スレッドの終了を待つ最大時間（ミリ秒）。0 以下なら待たない。</param>
        public void Shutdown(int InTimeoutMs)
        {
            try
            {
                _Queue.CompleteAdding();
            }
            catch (Exception)
            {
                // 既に締め切られている場合は無視する
            }
            try
            {
                if (InTimeoutMs > 0 && _PaneThread != null && _PaneThread.IsAlive)
                {
                    _PaneThread.Join(InTimeoutMs);
                }
            }
            catch (Exception)
            {
                // shutdown を長時間止めない
            }
        }

        /// <summary>
        /// pane への書き込み失敗を数え、連続 3 回で pane 出力を無効化する。無効化したことは JSONL へ 1 回だけ残す
        /// （pane は既に使えないので、人が後から気づけるのは JSONL だけになる）。
        /// </summary>
        /// <param name="InReason">失敗の内訳（uiThread / dteUnavailable / write）。</param>
        private void CountFailure(string InReason)
        {
            int TheFailureCount = Interlocked.Increment(ref _ConsecutiveFailureCount);
            if (TheFailureCount < _MAX_CONSECUTIVE_FAILURES)
            {
                return;
            }
            _IsPaneAvailable = false;
            if (Interlocked.CompareExchange(ref _IsFailureReported, 1, 0) != 0)
            {
                return;
            }
            DiagnosticHub.Emit(DiagnosticLevel.Warning, DiagnosticCategory.DIAGNOSTICS, "diagnostics.pane.failure",
                $"Output pane logging was disabled after {_MAX_CONSECUTIVE_FAILURES} consecutive failures", InData =>
                {
                    InData["reason"] = InReason;
                    InData["consecutiveFailureCount"] = TheFailureCount;
                });
        }

        /// <summary>pane へ書き込めたので連続失敗の数を戻す。</summary>
        private void CountSuccess()
        {
            Interlocked.Exchange(ref _ConsecutiveFailureCount, 0);
        }

        /// <summary>event を "[HH:mm:ss.fff][correlationId][tool][LEVEL] message" の 1 行にする。</summary>
        /// <param name="InEvent">対象の event。</param>
        /// <returns>1 行の文字列。</returns>
        private static string FormatEvent(DiagnosticEvent InEvent)
        {
            StringBuilder TheBuilder = new StringBuilder();
            TheBuilder.Append('[').Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append(']');
            TheBuilder.Append('[').Append(InEvent.CorrelationId ?? DiagnosticConstants.NO_CORRELATION_ID).Append(']');
            TheBuilder.Append('[').Append(InEvent.Tool ?? "-").Append(']');
            TheBuilder.Append('[').Append((InEvent.Level ?? "info").ToUpperInvariant()).Append("] ");
            TheBuilder.Append(InEvent.EventName);
            if (!string.IsNullOrEmpty(InEvent.Message))
            {
                TheBuilder.Append(' ').Append(InEvent.Message);
            }
            if (!string.IsNullOrEmpty(InEvent.Result))
            {
                TheBuilder.Append(" -> ").Append(InEvent.Result);
            }
            if (InEvent.ElapsedMs.HasValue)
            {
                TheBuilder.Append(" (").Append(InEvent.ElapsedMs.Value).Append(" ms)");
            }
            return TheBuilder.ToString();
        }

        /// <summary>背景スレッドの本体。行数か経過時間でまとめて UI スレッドへ 1 回だけ書き込む。</summary>
        private void RunPaneLoop()
        {
            List<string> TheBatch = new List<string>();
            Stopwatch TheTimer = Stopwatch.StartNew();
            try
            {
                while (!_Queue.IsCompleted)
                {
                    if (_Queue.TryTake(out string TheLine, _TAKE_TIMEOUT_MS) && TheLine != null)
                    {
                        TheBatch.Add(TheLine);
                    }
                    if (TheBatch.Count == 0)
                    {
                        continue;
                    }
                    if (TheBatch.Count >= _BATCH_LINE_COUNT || TheTimer.ElapsedMilliseconds >= _BATCH_INTERVAL_MS)
                    {
                        PostBatch(TheBatch);
                        TheBatch = new List<string>();
                        TheTimer.Restart();
                    }
                }
            }
            catch (Exception)
            {
                // 背景スレッドの例外で VS を巻き込まない
            }
            finally
            {
                if (TheBatch.Count > 0)
                {
                    PostBatch(TheBatch);
                }
            }
        }

        /// <summary>まとめた行を UI スレッドで pane へ書き込む（結果は待たない）。</summary>
        /// <param name="InLines">書き込む行。</param>
        private void PostBatch(List<string> InLines)
        {
            if (_Accessor == null || !_IsPaneAvailable || InLines.Count == 0)
            {
                return;
            }

            StringBuilder TheBuilder = new StringBuilder();
            foreach (string TheLine in InLines)
            {
                TheBuilder.Append(TheLine).Append(Environment.NewLine);
            }
            string TheText = TheBuilder.ToString();

            try
            {
                Task TheTask = _Accessor.RunOnUIThreadAsync(() => WriteToPaneOnUiThread(TheText));
                TheTask.ContinueWith(
                    InCompletedTask =>
                    {
                        // UI スレッドが 10 秒以内に取れなかった場合などの例外は観測だけして捨てる
                        AggregateException TheIgnoredException = InCompletedTask.Exception;
                        if (TheIgnoredException != null)
                        {
                            CountFailure("uiThread");
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
            }
            catch (Exception)
            {
                CountFailure("post");
            }
        }

        /// <summary>UI スレッド上で pane を確保して書き込む。失敗したら pane 出力を無効化する。</summary>
        /// <param name="InText">書き込むテキスト（複数行）。</param>
        private void WriteToPaneOnUiThread(string InText)
        {
            try
            {
                if (_Pane == null)
                {
                    EnvDTE80.DTE2 TheDte = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.Run(() => _Accessor.GetDteAsync());
                    if (TheDte == null)
                    {
                        CountFailure("dteUnavailable");
                        return;
                    }
                    OutputWindow TheOutputWindow = TheDte.ToolWindows.OutputWindow;
                    foreach (OutputWindowPane TheCandidate in TheOutputWindow.OutputWindowPanes)
                    {
                        if (string.Equals(TheCandidate.Name, DiagnosticConstants.OUTPUT_PANE_NAME, StringComparison.Ordinal))
                        {
                            _Pane = TheCandidate;
                            break;
                        }
                    }
                    if (_Pane == null)
                    {
                        _Pane = TheOutputWindow.OutputWindowPanes.Add(DiagnosticConstants.OUTPUT_PANE_NAME);
                    }
                }
                // pane は Activate しない（ユーザーの作業中の Output pane を奪わないため）
                // Extended (Phase 12): DEBUG 限定の失敗注入（Release では常に false のため JIT で消える）
                if (DiagnosticTestHooks.TryConsumePaneFailure())
                {
                    throw new InvalidOperationException("test-only pane failure");
                }
                _Pane.OutputString(InText);
                CountSuccess();
            }
            catch (Exception)
            {
                _Pane = null;
                CountFailure("write");
            }
        }
    }
}
