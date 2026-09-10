using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace VsMcp.Extension.Tools
{
    /// <summary>
    /// Extended (Phase 10): JSONL を非同期に書き出す writer（指示書 §10〜§12・§36〜§37）。
    /// Tool の応答をファイル I/O でブロックしないよう、書き込みは容量上限つきのキューと背景スレッド 1 本に閉じ込める。
    /// キューが満杯のときは trace / verbose から捨て、error / warning は極力保持する。書き込みが連続して失敗した場合は
    /// ファイル出力そのものを止め（無限再試行はしない）、Tool の実行には影響させない。
    /// </summary>
    internal sealed class DiagnosticWriter
    {
        /// <summary>ログのサブフォルダー名。</summary>
        private const string _LOGS_FOLDER_NAME = "Logs";

        /// <summary>まとめて書いたあとに Flush する行数。</summary>
        private const int _FLUSH_LINE_COUNT = 64;

        /// <summary>Flush する間隔（ミリ秒）。</summary>
        private const int _FLUSH_INTERVAL_MS = 100;

        /// <summary>キューから 1 件取り出すときの待ち時間（ミリ秒）。</summary>
        private const int _TAKE_TIMEOUT_MS = 100;

        /// <summary>この回数だけ連続して書き込みに失敗したらファイル出力を止める。</summary>
        private const int _MAX_CONSECUTIVE_FAILURES = 5;

        /// <summary>キュー満杯時に「捨ててよい event」を探すために一時退避する最大件数。</summary>
        private const int _MAX_OVERFLOW_INSPECT_COUNT = 8;

        /// <summary>この件数を捨てるごとに diagnostics.dropped を 1 件記録する。</summary>
        private const int _DROPPED_REPORT_INTERVAL = 1000;

        /// <summary>書き込み待ちの event。容量上限つき。</summary>
        private readonly BlockingCollection<DiagnosticEvent> _Queue;

        /// <summary>診断設定。</summary>
        private readonly DiagnosticSettings _Settings;

        /// <summary>セッション（sessionId / sequence / dropped の集計）。</summary>
        private readonly DiagnosticSession _Session;

        /// <summary>writer 障害を 1 回だけ人に見せるためのコールバック（Output pane への 1 行）。</summary>
        private readonly Action<string> _WarningCallback;

        /// <summary>背景の書き込みスレッド。</summary>
        private Thread _WriterThread;

        /// <summary>現在の出力先ストリーム。ファイル出力を止めた場合は null。</summary>
        private StreamWriter _CurrentWriter;

        /// <summary>現在の出力先ファイルの絶対パス。</summary>
        private string _CurrentFilePath;

        /// <summary>現在のパート番号（1 始まり。2 以上で -part-NNN が付く）。</summary>
        private int _CurrentPartNumber = 1;

        /// <summary>現在のファイルへ書いたバイト数の概算。</summary>
        private long _CurrentFileBytes;

        /// <summary>連続した書き込み失敗の回数。</summary>
        private int _ConsecutiveFailureCount;

        /// <summary>ファイル出力が健全か（false ならファイルへは書かない）。</summary>
        private volatile bool _IsHealthy = true;

        /// <summary>障害の警告を出したか（1 回だけ出す）。</summary>
        private bool _IsFailureReported;

        /// <summary>次に書ける event へ付ける「捨てた件数」。</summary>
        private long _PendingDroppedCount;

        /// <summary>diagnostics.dropped を最後に記録した時点の累計。</summary>
        private long _LastReportedDroppedCount;

        /// <summary>
        /// diagnostics.dropped として報告すべき累計。0 なら報告不要。
        /// 満杯のキューへ入れ直すと報告自体が捨てられるので、writer ループが直接書き出すためのフィールドにする。
        /// </summary>
        private long _PendingDroppedReportTotal;

        /// <summary>diagnostics_flush からの同期要求（背景スレッドが Flush したら 0 へ戻す）。</summary>
        private int _FlushRequestCount;

        /// <summary>ファイル出力が健全か。</summary>
        public bool IsHealthy { get { return _IsHealthy; } }

        /// <summary>現在の出力先ファイルの絶対パス。まだ開いていなければ null。</summary>
        public string CurrentFilePath { get { return _CurrentFilePath; } }

        /// <summary>書き込み待ちの件数。</summary>
        public int QueueLength { get { return _Queue.Count; } }

        /// <summary>ログのルートフォルダー（Logs）の絶対パス。</summary>
        public static string LogFolderPath { get { return DiagnosticSettings.GetFolder(_LOGS_FOLDER_NAME); } }

        /// <summary>writer を作る（この時点ではファイルもスレッドも作らない）。</summary>
        /// <param name="InSettings">診断設定。</param>
        /// <param name="InSession">セッション。</param>
        /// <param name="InWarningCallback">障害を 1 回だけ通知するコールバック。null 可。</param>
        public DiagnosticWriter(DiagnosticSettings InSettings, DiagnosticSession InSession, Action<string> InWarningCallback)
        {
            _Settings = InSettings;
            _Session = InSession;
            _WarningCallback = InWarningCallback;
            _Queue = new BlockingCollection<DiagnosticEvent>(InSettings.QueueCapacity);
        }

        /// <summary>背景スレッドを開始し、古いログの整理も背景で行う。多重に呼んでも 1 本しか起動しない。</summary>
        public void Start()
        {
            if (_WriterThread != null)
            {
                return;
            }
            _WriterThread = new Thread(RunWriterLoop)
            {
                IsBackground = true,
                Name = "VsMcpExtendedDiagnosticsWriter",
            };
            _WriterThread.Start();
        }

        /// <summary>
        /// event をキューへ入れる。満杯のときは trace / verbose を優先して捨て、それでも入らなければこの event を捨てる。
        /// 例外は投げない（診断が Tool を止めない）。
        /// </summary>
        /// <param name="InEvent">記録する event。</param>
        public void Enqueue(DiagnosticEvent InEvent)
        {
            if (InEvent == null)
            {
                return;
            }
            try
            {
                if (_Queue.IsAddingCompleted)
                {
                    return;
                }
                if (_Queue.TryAdd(InEvent, 0))
                {
                    return;
                }
                if (!TryMakeRoomAndAdd(InEvent))
                {
                    CountDropped(1);
                }
            }
            catch (Exception)
            {
                // CompleteAdding 直後などで例外になっても、診断は静かに諦める
            }
        }

        /// <summary>
        /// キューが満杯のときに空きを作って追加を試みる。先頭から最大 8 件を一時退避し、最初に見つけた trace / verbose を
        /// 1 件だけ捨てて空きを作る。退避した残りは元の順序で戻し、捨ててよい event が 1 件も無いときだけ新しい event を諦める
        /// （error / warning を極力保持するため）。
        /// </summary>
        /// <param name="InEvent">追加したい event。</param>
        /// <returns>追加できたら true。</returns>
        private bool TryMakeRoomAndAdd(DiagnosticEvent InEvent)
        {
            DiagnosticLevel TheNewLevel = DiagnosticConstants.ParseLevel(InEvent.Level, DiagnosticLevel.Info);
            if (TheNewLevel >= DiagnosticLevel.Verbose)
            {
                // 詳細な event は真っ先に捨てる
                return false;
            }

            List<DiagnosticEvent> TheHeldEvents = new List<DiagnosticEvent>(_MAX_OVERFLOW_INSPECT_COUNT);
            for (int TheIndex = 0; TheIndex < _MAX_OVERFLOW_INSPECT_COUNT; TheIndex++)
            {
                if (!_Queue.TryTake(out DiagnosticEvent TheTaken, 0))
                {
                    break;
                }
                if (DiagnosticConstants.ParseLevel(TheTaken.Level, DiagnosticLevel.Info) >= DiagnosticLevel.Verbose)
                {
                    // 捨てるのは最初に見つけた 1 件だけ。空きができたので探索を打ち切る
                    CountDropped(1);
                    break;
                }
                TheHeldEvents.Add(TheTaken);
            }

            foreach (DiagnosticEvent TheHeldEvent in TheHeldEvents)
            {
                if (!_Queue.TryAdd(TheHeldEvent, 0))
                {
                    // 別のスレッドに空きを取られた場合は戻せないので、失った件数として数える
                    CountDropped(1);
                }
            }
            // 空きを作れた場合はここで入る。作れなくても、その間に writer が書き出して空いていれば拾える
            return _Queue.TryAdd(InEvent, 0);
        }

        /// <summary>
        /// 捨てた件数を数え、一定件数ごとに diagnostics.dropped の報告を予約する。
        /// 満杯のキューへ入れ直すと報告自体が捨てられるため、実際の書き出しは writer ループが直接行う。
        /// </summary>
        /// <param name="InCount">捨てた件数。</param>
        private void CountDropped(long InCount)
        {
            Interlocked.Add(ref _PendingDroppedCount, InCount);
            long TheTotal = _Session.AddDropped(InCount);
            if (TheTotal - Interlocked.Read(ref _LastReportedDroppedCount) < _DROPPED_REPORT_INTERVAL)
            {
                return;
            }
            Interlocked.Exchange(ref _LastReportedDroppedCount, TheTotal);
            Interlocked.Exchange(ref _PendingDroppedReportTotal, TheTotal);
        }

        /// <summary>予約されている diagnostics.dropped を、writer ループの中から 1 件だけ直接書き出す。</summary>
        private void WritePendingDroppedReport()
        {
            long TheTotal = Interlocked.Exchange(ref _PendingDroppedReportTotal, 0);
            if (TheTotal <= 0)
            {
                return;
            }
            WriteEvent(BuildInternalEvent(DiagnosticLevel.Warning, "diagnostics.dropped",
                $"diagnostic writer dropped {TheTotal} event(s)", new JObject { ["droppedEventCount"] = TheTotal }));
        }

        /// <summary>診断基盤自身の event を組み立てる（Tool 呼び出しの外で出るもの）。</summary>
        /// <param name="InLevel">重要度。</param>
        /// <param name="InEventName">event 名。</param>
        /// <param name="InMessage">短い説明。</param>
        /// <param name="InData">付随データ。null 可。</param>
        /// <returns>組み立てた event。</returns>
        private DiagnosticEvent BuildInternalEvent(DiagnosticLevel InLevel, string InEventName, string InMessage, JObject InData)
        {
            return new DiagnosticEvent
            {
                TimestampUtc = DateTime.UtcNow.ToString("o"),
                SessionId = _Session.SessionId,
                CorrelationId = DiagnosticConstants.NO_CORRELATION_ID,
                Sequence = _Session.NextSequence(),
                Level = DiagnosticConstants.ToText(InLevel),
                Category = DiagnosticCategory.DIAGNOSTICS,
                EventName = InEventName,
                Tool = null,
                Message = InMessage,
                Data = InData,
            };
        }

        /// <summary>
        /// diagnostics_flush 用に、キューが空になるまで（最大 InTimeoutMs）待ってからファイルを Flush する。
        /// 通常の Tool 呼び出しからは呼ばない。
        /// </summary>
        /// <param name="InTimeoutMs">待つ最大時間（ミリ秒）。</param>
        /// <param name="OutQueueLengthBefore">待つ前のキュー長。</param>
        /// <returns>キューが空になり Flush 要求も処理されたら true。</returns>
        public bool Flush(int InTimeoutMs, out int OutQueueLengthBefore)
        {
            OutQueueLengthBefore = _Queue.Count;
            Stopwatch TheStopwatch = Stopwatch.StartNew();
            while (_Queue.Count > 0 && TheStopwatch.ElapsedMilliseconds < InTimeoutMs)
            {
                Thread.Sleep(10);
            }
            if (_Queue.Count > 0)
            {
                return false;
            }

            Interlocked.Increment(ref _FlushRequestCount);
            while (Interlocked.CompareExchange(ref _FlushRequestCount, 0, 0) > 0 && TheStopwatch.ElapsedMilliseconds < InTimeoutMs)
            {
                Thread.Sleep(10);
            }
            return Interlocked.CompareExchange(ref _FlushRequestCount, 0, 0) == 0;
        }

        /// <summary>
        /// キューへの追加を締め切り、背景スレッドの終了を上限つきで待つ。
        /// 待ち時間は呼び出し側（Hub）が pane と分け合う予算として渡す。
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
                if (InTimeoutMs > 0 && _WriterThread != null && _WriterThread.IsAlive)
                {
                    _WriterThread.Join(InTimeoutMs);
                }
            }
            catch (Exception)
            {
                // shutdown を長時間止めないことを優先する
            }
        }

        /// <summary>背景スレッドの本体。キューから取り出して書き、行数か経過時間で Flush する。</summary>
        private void RunWriterLoop()
        {
            TryCleanupOldLogs();

            int TheLinesSinceFlush = 0;
            Stopwatch TheFlushTimer = Stopwatch.StartNew();
            try
            {
                while (!_Queue.IsCompleted)
                {
                    DiagnosticEvent TheEvent;
                    bool IsTaken;
                    try
                    {
                        IsTaken = _Queue.TryTake(out TheEvent, _TAKE_TIMEOUT_MS);
                    }
                    catch (Exception)
                    {
                        break;
                    }

                    if (IsTaken && TheEvent != null)
                    {
                        WriteEvent(TheEvent);
                        TheLinesSinceFlush++;
                    }

                    // 捨てた件数の報告は、キューを介さずここで直接書く（満杯のときこそ書けなければ意味がない）
                    WritePendingDroppedReport();

                    bool IsFlushRequested = Interlocked.CompareExchange(ref _FlushRequestCount, 0, 0) > 0;
                    if (IsFlushRequested || TheLinesSinceFlush >= _FLUSH_LINE_COUNT || TheFlushTimer.ElapsedMilliseconds >= _FLUSH_INTERVAL_MS)
                    {
                        FlushCurrentWriter();
                        TheLinesSinceFlush = 0;
                        TheFlushTimer.Restart();
                        if (IsFlushRequested)
                        {
                            Interlocked.Exchange(ref _FlushRequestCount, 0);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // 背景スレッドの例外で VS を巻き込まない
            }
            finally
            {
                FlushCurrentWriter();
                CloseCurrentWriter();
            }
        }

        /// <summary>1 件の event を JSONL の 1 行として書く。書けない場合はファイル出力を止める判断をする。</summary>
        /// <param name="InEvent">書き出す event。</param>
        private void WriteEvent(DiagnosticEvent InEvent)
        {
            if (!_Settings.IsJsonlWritten || !_IsHealthy)
            {
                return;
            }

            // Extended (Phase 12): DEBUG 限定の遅延注入。JSONL を 1 件書く直前。Shutdown 中は遅延しない
            if (DiagnosticTestHooks.WriterDelayMs > 0 && !_Queue.IsAddingCompleted)
            {
                Thread.Sleep(DiagnosticTestHooks.WriterDelayMs);
            }

            long ThePending = Interlocked.Exchange(ref _PendingDroppedCount, 0);
            if (ThePending > 0)
            {
                InEvent.Dropped = ThePending;
            }

            string TheLine;
            try
            {
                TheLine = JsonConvert.SerializeObject(InEvent, Formatting.None);
            }
            catch (Exception)
            {
                TheLine = BuildSerializeFailureLine(InEvent);
            }

            try
            {
                EnsureWriter(TheLine.Length);
                if (_CurrentWriter == null)
                {
                    // この行は書けなかったので、捨てた件数は次に書けた event へ持ち越す
                    RestorePendingDropped(ThePending);
                    return;
                }
                _CurrentWriter.Write(TheLine);
                _CurrentWriter.Write('\n');
                _CurrentFileBytes += Encoding.UTF8.GetByteCount(TheLine) + 1;
                _ConsecutiveFailureCount = 0;
            }
            catch (Exception TheException)
            {
                RestorePendingDropped(ThePending);
                HandleWriteFailure(TheException);
            }
        }

        /// <summary>書けなかった event に付けていた「捨てた件数」を、次の event へ持ち越すために戻す。</summary>
        /// <param name="InCount">戻す件数。0 以下なら何もしない。</param>
        private void RestorePendingDropped(long InCount)
        {
            if (InCount <= 0)
            {
                return;
            }
            Interlocked.Add(ref _PendingDroppedCount, InCount);
        }

        /// <summary>シリアライズできなかった event を、最低限の情報だけの 1 行へ置き換える。</summary>
        /// <param name="InEvent">元の event。</param>
        /// <returns>JSONL の 1 行。</returns>
        private string BuildSerializeFailureLine(DiagnosticEvent InEvent)
        {
            JObject TheMinimal = new JObject
            {
                ["schemaVersion"] = DiagnosticConstants.SCHEMA_VERSION,
                ["timestampUtc"] = InEvent.TimestampUtc,
                ["sessionId"] = InEvent.SessionId,
                ["correlationId"] = InEvent.CorrelationId,
                ["sequence"] = InEvent.Sequence,
                ["level"] = DiagnosticConstants.ToText(DiagnosticLevel.Error),
                ["category"] = DiagnosticCategory.DIAGNOSTICS,
                ["event"] = "diagnostics.serialize.failure",
                ["tool"] = InEvent.Tool,
            };
            return TheMinimal.ToString(Formatting.None);
        }

        /// <summary>書き込み先を用意する。未作成なら開き、サイズ上限を超えていれば次のパートへ切り替える。</summary>
        /// <param name="InIncomingLength">これから書く行の長さ（切り替え判定の目安）。</param>
        private void EnsureWriter(int InIncomingLength)
        {
            long TheMaxBytes = (long)_Settings.MaxFileSizeMb * 1024L * 1024L;
            if (_CurrentWriter != null && _CurrentFileBytes + InIncomingLength > TheMaxBytes)
            {
                FlushCurrentWriter();
                CloseCurrentWriter();
                _CurrentPartNumber++;
            }
            if (_CurrentWriter != null)
            {
                return;
            }

            string TheFolder = Path.Combine(LogFolderPath, _Session.StartedLocal.ToString("yyyy-MM-dd"));
            Directory.CreateDirectory(TheFolder);
            _CurrentFilePath = Path.Combine(TheFolder, BuildFileName());
            FileStream TheStream = new FileStream(_CurrentFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            _CurrentWriter = new StreamWriter(TheStream, new UTF8Encoding(false)) { AutoFlush = false };
            _CurrentFileBytes = TheStream.Length;
        }

        /// <summary>現在のパートに応じたファイル名を組み立てる。</summary>
        /// <returns>ファイル名（拡張子込み）。</returns>
        private string BuildFileName()
        {
            string TheBase = "session-" + _Session.StartedLocal.ToString("yyyyMMdd-HHmmss") + "-" + _Session.SessionId;
            if (_CurrentPartNumber <= 1)
            {
                return TheBase + ".jsonl";
            }
            return TheBase + "-part-" + _CurrentPartNumber.ToString("000") + ".jsonl";
        }

        /// <summary>現在のストリームを Flush する（失敗しても止めない）。</summary>
        private void FlushCurrentWriter()
        {
            try
            {
                if (_CurrentWriter != null)
                {
                    _CurrentWriter.Flush();
                }
            }
            catch (Exception TheException)
            {
                HandleWriteFailure(TheException);
            }
        }

        /// <summary>現在のストリームを閉じる（失敗しても止めない）。</summary>
        private void CloseCurrentWriter()
        {
            try
            {
                if (_CurrentWriter != null)
                {
                    _CurrentWriter.Dispose();
                }
            }
            catch (Exception)
            {
                // 閉じられなくても続行する
            }
            finally
            {
                _CurrentWriter = null;
            }
        }

        /// <summary>
        /// 書き込み失敗を数え、連続 5 回でファイル出力を止める（ディスク満杯 / 権限 / ロック / フォルダー作成失敗をここで吸収する）。
        /// 無限再試行はしない。
        /// </summary>
        /// <param name="InException">発生した例外。</param>
        private void HandleWriteFailure(Exception InException)
        {
            _ConsecutiveFailureCount++;
            CloseCurrentWriter();
            if (_ConsecutiveFailureCount < _MAX_CONSECUTIVE_FAILURES)
            {
                return;
            }

            _IsHealthy = false;
            if (_IsFailureReported)
            {
                return;
            }
            _IsFailureReported = true;
            try
            {
                _WarningCallback?.Invoke(
                    $"diagnostics.writer.failure: JSONL output was disabled after {_MAX_CONSECUTIVE_FAILURES} consecutive failures ({InException.GetType().Name}). "
                    + "Tools keep working; call diagnostics_get_status for details.");
            }
            catch (Exception)
            {
                // 通知できなくても続行する
            }
        }

        /// <summary>
        /// 起動時に古いログを整理する。retentionDays より古い日付フォルダーと、maxSessionFiles を超える古いファイルを削除する。
        /// 失敗しても Tool の実行には影響させない（例外を外へ出さない）。
        /// </summary>
        private void TryCleanupOldLogs()
        {
            try
            {
                string TheRoot = LogFolderPath;
                if (!Directory.Exists(TheRoot))
                {
                    return;
                }

                DateTime TheCutoff = DateTime.Now.Date.AddDays(-_Settings.RetentionDays);
                foreach (string TheFolder in Directory.GetDirectories(TheRoot))
                {
                    // 1 フォルダーが消せなくても（他プロセスが開いている等）残りの整理は続ける
                    try
                    {
                        string TheName = Path.GetFileName(TheFolder);
                        if (DateTime.TryParseExact(TheName, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.None, out DateTime TheDate)
                            && TheDate < TheCutoff)
                        {
                            Directory.Delete(TheFolder, true);
                        }
                    }
                    catch (Exception)
                    {
                        // このフォルダーは次回の起動で改めて試す
                    }
                }

                List<FileInfo> TheFiles = Directory.GetFiles(TheRoot, "*.jsonl", SearchOption.AllDirectories)
                    .Select(ThePath => new FileInfo(ThePath))
                    .OrderByDescending(TheFile => TheFile.LastWriteTimeUtc)
                    .ToList();
                for (int TheIndex = _Settings.MaxSessionFiles; TheIndex < TheFiles.Count; TheIndex++)
                {
                    // 1 ファイルが消せなくても（読み取り中・ロック中）残りの整理は続ける
                    try
                    {
                        TheFiles[TheIndex].Delete();
                    }
                    catch (Exception)
                    {
                        // このファイルは次回の起動で改めて試す
                    }
                }
            }
            catch (Exception TheException)
            {
                _Queue.TryAdd(BuildInternalEvent(DiagnosticLevel.Warning, "diagnostics.retention.failure",
                    "old diagnostic logs could not be cleaned up: " + TheException.GetType().Name, null), 0);
            }
        }
    }
}
