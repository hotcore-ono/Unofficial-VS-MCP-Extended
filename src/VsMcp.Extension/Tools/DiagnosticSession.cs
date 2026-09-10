using System;
using System.Threading;

namespace VsMcp.Extension.Tools
{
    /// <summary>
    /// Extended (Phase 10): Visual Studio Extension / MCP server の 1 起動を表すセッション（指示書 §7）。
    /// sessionId はログファイル名にも使うため、生成後は変わらない。sequence はセッション内で単調増加し、
    /// timestamp だけに頼らずイベント順を復元できるようにする。
    /// </summary>
    internal sealed class DiagnosticSession
    {
        /// <summary>次に払い出す sequence。<see cref="Interlocked"/> で加算するため参照型のフィールドにする。</summary>
        private long _Sequence;

        /// <summary>writer が捨てた event の累計。</summary>
        private long _DroppedEventCount;

        /// <summary>このセッションの ID（"s-" + 開始時刻 + Guid 先頭 8 桁）。</summary>
        public string SessionId { get; private set; }

        /// <summary>セッションの開始時刻（UTC）。</summary>
        public DateTime StartedUtc { get; private set; }

        /// <summary>セッションの開始時刻（ローカル。ログの日付フォルダーとファイル名に使う）。</summary>
        public DateTime StartedLocal { get; private set; }

        /// <summary>捨てられた event の累計。</summary>
        public long DroppedEventCount { get { return Interlocked.Read(ref _DroppedEventCount); } }

        /// <summary>直近に払い出した sequence。</summary>
        public long CurrentSequence { get { return Interlocked.Read(ref _Sequence); } }

        /// <summary>新しいセッションを開始する。</summary>
        public DiagnosticSession()
        {
            StartedUtc = DateTime.UtcNow;
            StartedLocal = DateTime.Now;
            SessionId = "s-" + StartedLocal.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        /// <summary>次の sequence を払い出す（スレッド安全）。</summary>
        /// <returns>1 から始まる単調増加の値。</returns>
        public long NextSequence()
        {
            return Interlocked.Increment(ref _Sequence);
        }

        /// <summary>捨てた event を数える（スレッド安全）。</summary>
        /// <param name="InCount">捨てた件数。</param>
        /// <returns>加算後の累計。</returns>
        public long AddDropped(long InCount)
        {
            return Interlocked.Add(ref _DroppedEventCount, InCount);
        }
    }
}
