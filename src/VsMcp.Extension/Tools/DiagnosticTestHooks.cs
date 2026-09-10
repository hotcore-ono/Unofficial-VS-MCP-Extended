using System;
using System.Globalization;
using System.Threading;

namespace VsMcp.Extension.Tools
{
#if DEBUG
    /// <summary>
    /// Extended (Phase 12): DEBUG ビルド限定の fault injection seam（指示書 §30 / §31）。
    /// 診断基盤の「遅い writer」「pane 書き込みの失敗」をストレス検証から意図的に起こすためだけに存在する。
    /// 値は環境変数からプロセス起動時に 1 回だけ読み、実行中の変更は反映しない。MCP Tool としては公開しない。
    /// Release ビルドでは同名クラスの固定値スタブ（0 / false を返すプロパティ）へ切り替わるため、呼び出し側に #if を書く必要がない。
    /// Release では常に 0 / false を返すプロパティなので、インライン後に分岐は定数化される
    /// （net48 の JIT は static readonly フィールドを定数として畳み込まないため、フィールドではなくプロパティにしてある）。
    /// </summary>
    internal static class DiagnosticTestHooks
    {
        /// <summary>writer の遅延（ミリ秒）を指定する環境変数の名前。</summary>
        private const string _WRITER_DELAY_VARIABLE_NAME = "VSMCP_EXT_TEST_DIAG_WRITER_DELAY_MS";

        /// <summary>pane 書き込みを失敗させる回数を指定する環境変数の名前。</summary>
        private const string _PANE_FAIL_COUNT_VARIABLE_NAME = "VSMCP_EXT_TEST_PANE_FAIL_COUNT";

        /// <summary>writer 遅延として受け付ける最小値（ミリ秒）。</summary>
        private const int _MIN_WRITER_DELAY_MS = 1;

        /// <summary>writer 遅延として受け付ける最大値（ミリ秒）。これを超える指定はここまで丸める。</summary>
        private const int _MAX_WRITER_DELAY_MS = 5000;

        /// <summary>JSONL を 1 件書く直前に挟む遅延（ミリ秒）。プロセス起動時に環境変数から 1 回だけ読む。0 なら遅延しない。</summary>
        private static readonly int _WriterDelayMs = ReadWriterDelayMs();

        /// <summary>pane 書き込みを意図的に失敗させる回数（起動時の指定値）。0 なら失敗させない。</summary>
        private static readonly int _PaneFailCount = ReadPositiveCount(_PANE_FAIL_COUNT_VARIABLE_NAME);

        /// <summary>pane 書き込みを失敗させる残り回数。TryConsumePaneFailure が 1 回ずつ減らす。</summary>
        private static int _RemainingPaneFailureCount = _PaneFailCount;

        /// <summary>JSONL を 1 件書く直前に挟む遅延（ミリ秒）。0 なら遅延しない。</summary>
        internal static int WriterDelayMs
        {
            get { return _WriterDelayMs; }
        }

        /// <summary>pane 書き込みを意図的に失敗させる回数（起動時の指定値）。0 なら失敗させない。</summary>
        internal static int PaneFailCount
        {
            get { return _PaneFailCount; }
        }

        /// <summary>いずれかの注入が有効かどうか（session.start へ testHooks を出すかの判断に使う）。</summary>
        internal static bool IsActive
        {
            get { return _WriterDelayMs > 0 || _PaneFailCount > 0; }
        }

        /// <summary>
        /// pane 書き込みを 1 回分だけ意図的に失敗させるかを判定し、失敗させる場合は残り回数を 1 減らす。
        /// 複数スレッドが同時に呼んでも残り回数が負に落ちることはない（Decrement の結果が負になった場合は
        /// Interlocked.Increment で 0 まで戻し、その呼び出しは失敗させない扱いにする）。
        /// </summary>
        /// <returns>この呼び出しで失敗させる場合は true。</returns>
        internal static bool TryConsumePaneFailure()
        {
            if (Volatile.Read(ref _RemainingPaneFailureCount) <= 0)
            {
                return false;
            }
            int TheRemainingCount = Interlocked.Decrement(ref _RemainingPaneFailureCount);
            if (TheRemainingCount < 0)
            {
                // 他スレッドと競合して残り回数を使い切った後だったので、負に落ちた分を戻して失敗させない扱いにする
                Interlocked.Increment(ref _RemainingPaneFailureCount);
                return false;
            }
            return true;
        }

        /// <summary>writer 遅延の環境変数を読み、指定があれば 1〜5000 ミリ秒へ丸める。</summary>
        /// <returns>丸めた遅延（ミリ秒）。指定が無い・0 以下・数値でない場合は 0。</returns>
        private static int ReadWriterDelayMs()
        {
            int TheValue = ReadPositiveCount(_WRITER_DELAY_VARIABLE_NAME);
            if (TheValue <= 0)
            {
                return 0;
            }
            return Math.Min(Math.Max(TheValue, _MIN_WRITER_DELAY_MS), _MAX_WRITER_DELAY_MS);
        }

        /// <summary>環境変数を正の整数として読む。読めない値は「注入なし」として扱う。</summary>
        /// <param name="InVariableName">読み取る環境変数の名前。</param>
        /// <returns>読み取った値。指定が無い・数値でない・0 以下の場合は 0。</returns>
        private static int ReadPositiveCount(string InVariableName)
        {
            try
            {
                string TheText = Environment.GetEnvironmentVariable(InVariableName);
                if (string.IsNullOrWhiteSpace(TheText))
                {
                    return 0;
                }
                if (!int.TryParse(TheText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int TheValue) || TheValue <= 0)
                {
                    return 0;
                }
                return TheValue;
            }
            catch (Exception)
            {
                // 環境変数が読めない環境でも「注入なし」で動き続ける（検証用の seam が製品動作を壊さない）
                return 0;
            }
        }
    }
#else
    /// <summary>
    /// Extended (Phase 12): Release ビルド用の fault injection seam スタブ（指示書 §30 / §31）。
    /// DEBUG 版と同じ名前・同じ使い方で、値は固定の 0 / false になる。
    /// Release では常に 0 / false を返すプロパティなので、インライン後に分岐は定数化される
    /// （net48 の JIT は static readonly フィールドを定数として畳み込まないため、フィールドではなくプロパティにしてある）。
    /// </summary>
    internal static class DiagnosticTestHooks
    {
        /// <summary>JSONL を 1 件書く直前に挟む遅延（ミリ秒）。Release では常に 0。</summary>
        internal static int WriterDelayMs
        {
            get { return 0; }
        }

        /// <summary>pane 書き込みを意図的に失敗させる回数。Release では常に 0。</summary>
        internal static int PaneFailCount
        {
            get { return 0; }
        }

        /// <summary>いずれかの注入が有効かどうか。Release では常に false。</summary>
        internal static bool IsActive
        {
            get { return false; }
        }

        /// <summary>pane 書き込みを 1 回分だけ意図的に失敗させるかを判定する。Release では常に失敗させない。</summary>
        /// <returns>常に false。</returns>
        internal static bool TryConsumePaneFailure()
        {
            return false;
        }
    }
#endif
}
