using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Automation;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VsMcp.Extension.McpServer;
using VsMcp.Extension.Services;
using VsMcp.Shared;
using VsMcp.Shared.Protocol;
using static VsMcp.Extension.Tools.NativeMethods;

namespace VsMcp.Extension.Tools
{
    /// <summary>
    /// Extended 独自の ui_window_wait_idle。idle の定義は upstream ui_wait_idle と同じ（UIA 要素数が quietMs の間変化しない）で、
    /// そのループ本体 UiTools.WaitForStableElementCountAsync を再利用し、数える対象だけを「指定 HWND」「解決したアクティブウィンドウ」
    /// 「操作可能な可視トップレベルウィンドウ全部」に差し替える。
    /// </summary>
    public static class UiWindowIdleTools
    {
        /// <summary>mode: 指定 windowHandle のみ。</summary>
        private const string _MODE_SINGLE = "single";

        /// <summary>mode: ResolveActiveWindow で解決したウィンドウ。</summary>
        private const string _MODE_ACTIVE = "active";

        /// <summary>mode: 可視・有効・非最小化のデバッグ対象トップレベルウィンドウ全部。</summary>
        private const string _MODE_ALL = "all";

        /// <summary>quietMs の既定（upstream ui_wait_idle と同じ）。</summary>
        private const int _DEFAULT_QUIET_MS = 500;

        /// <summary>quietMs の下限（upstream ui_wait_idle と同じ）。</summary>
        private const int _MIN_QUIET_MS = 50;

        /// <summary>監視対象ウィンドウ一覧の排他制御。ポーリング（別スレッド）の書き込みと戻り値組み立ての読み出しを同じロックで囲む。</summary>
        private static readonly object _WatchedWindowsLock = new object();

        /// <summary>ツールをレジストリへ登録する。VsMcpPackage.RegisterTools から呼ばれる。</summary>
        /// <param name="InRegistry">登録先レジストリ。</param>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        public static void Register(McpToolRegistry InRegistry, VsServiceAccessor InAccessor)
        {
            // Extended (Phase 10): 登録は DiagnosticToolRunner を通し、tool.start / tool.end と相関 ID を付ける（schema・戻り値・エラー文は不変）
            DiagnosticToolRunner.Register(InRegistry,
                new McpToolDefinition(
                    "ui_window_wait_idle",
                    "[Windows UIA — desktop app being debugged] Wait until the UI Automation tree of one or more top-level windows of the debugged application stops " +
                    "changing for a quiet period (same idle definition as ui_wait_idle, which only watches the first window of the process). 'mode': 'single' (default) " +
                    "waits for the window given by 'windowHandle'; 'active' resolves the currently active debuggee window like ui_get_active_window (a modal dialog " +
                    "wins over its disabled owner) and waits for it; 'all' waits for every visible, enabled, non-minimized top-level window of the debugged process " +
                    "together (disabled owners of modal dialogs are excluded so they cannot block forever). Returns text { idle, mode, elapsedMs, finalElementCount, " +
                    "windowHandle, windows, resolutionSource, quietMs, timeoutMs }; idle=false on timeout. timeoutMs default 10000, max 55000; pollIntervalMs min 50.",
                    SchemaBuilder.Create()
                        .AddInteger("windowHandle", "HWND of the window to watch (decimal); required for mode 'single', ignored otherwise")
                        .AddEnum("mode", "Which windows to watch: 'single' (default), 'active', 'all'", new[] { _MODE_SINGLE, _MODE_ACTIVE, _MODE_ALL })
                        .AddInteger("quietMs", "How long the tree must be stable to be considered idle (default: 500, min: 50)")
                        .AddInteger("timeoutMs", "Maximum time to wait in milliseconds (default: 10000, max: 55000)")
                        .AddInteger("pollIntervalMs", "Polling interval in milliseconds (default: 200, min: 50)")
                        .Build()),
                InArgs => UiWindowWaitIdleAsync(InAccessor, InArgs));
        }

        /// <summary>ui_window_wait_idle の本体。</summary>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        /// <param name="InArgs">ツール引数。</param>
        /// <returns>text（idle / mode / elapsedMs / finalElementCount / windowHandle / windows / resolutionSource / quietMs / timeoutMs）、またはエラー。</returns>
        private static async Task<McpToolResult> UiWindowWaitIdleAsync(VsServiceAccessor InAccessor, JObject InArgs)
        {
            string TheMode = (InArgs.Value<string>("mode") ?? _MODE_SINGLE).Trim().ToLowerInvariant();
            if (TheMode != _MODE_SINGLE && TheMode != _MODE_ACTIVE && TheMode != _MODE_ALL)
            {
                return McpToolResult.Error($"Unknown mode '{TheMode}'. Expected: single, active, all");
            }

            int TheQuietMs = InArgs.Value<int?>("quietMs") ?? _DEFAULT_QUIET_MS;
            if (TheQuietMs < _MIN_QUIET_MS)
            {
                TheQuietMs = _MIN_QUIET_MS;
            }
            UiWindowTools.ParseWaitOptions(InArgs, out int TheTimeoutMs, out int ThePollIntervalMs);
            if (TheTimeoutMs < TheQuietMs)
            {
                TheTimeoutMs = TheQuietMs;
            }

            HashSet<uint> TheProcessIds;
            IntPtr TheWindow = IntPtr.Zero;
            string TheResolutionSource = null;
            if (TheMode == _MODE_SINGLE)
            {
                if (!InArgs.Value<long?>("windowHandle").HasValue)
                {
                    return McpToolResult.Error("Parameter 'windowHandle' is required for mode 'single'");
                }
                (IntPtr TheResolved, HashSet<uint> TheResolvedProcessIds, McpToolResult TheError) = await UiWindowUiaTools.ResolveWindowWithProcessesAsync(InAccessor, InArgs);
                if (TheError != null)
                {
                    return TheError;
                }
                TheWindow = TheResolved;
                TheProcessIds = TheResolvedProcessIds;
            }
            else
            {
                TheProcessIds = await UiWindowTools.GetDebuggedProcessIdsAsync(InAccessor);
                if (TheProcessIds.Count == 0)
                {
                    return McpToolResult.Error(DebuggeeWindowResolver.NoDebuggedProcessMessage);
                }
                if (TheMode == _MODE_ACTIVE)
                {
                    ActiveWindowResolution TheResolution = await Task.Run(() =>
                        DebuggeeWindowResolver.ResolveActiveWindow(TheProcessIds, DebuggeeWindowEnumerator.EnumerateTopLevelWindows(TheProcessIds, false)));
                    if (!TheResolution.IsResolved)
                    {
                        if (TheResolution.Candidates.Count == 0)
                        {
                            return McpToolResult.Error("No visible top-level window of the debugged application was found.");
                        }
                        return McpToolResult.Error(
                            $"{TheResolution.Candidates.Count} visible windows of the debugged application remain after every resolution step; " +
                            "pass one of them as 'windowHandle' with mode 'single'. Candidates:\n" +
                            JsonConvert.SerializeObject(TheResolution.Candidates, Formatting.Indented));
                    }
                    TheWindow = new IntPtr(TheResolution.Resolved.Handle);
                    TheResolutionSource = TheResolution.Source;
                }
            }

            List<long> TheWatchedWindows = new List<long>();
            Func<int> TheCounter;
            if (TheMode == _MODE_ALL)
            {
                TheCounter = () => CountAllInteractableWindows(TheProcessIds, TheWatchedWindows);
            }
            else
            {
                IntPtr TheWatched = TheWindow;
                HashSet<uint> TheWatchedProcessIds = TheProcessIds;
                TheWatchedWindows.Add(TheWatched.ToInt64());
                TheCounter = () => CountSingleWindow(TheWatched, TheWatchedProcessIds);
            }

            try
            {
                (bool IsIdle, long TheElapsedMs, int TheFinalCount) = await UiTools.WaitForStableElementCountAsync(TheCounter, TheQuietMs, TheTimeoutMs, ThePollIntervalMs);
                List<long> TheReportedWindows;
                lock (_WatchedWindowsLock)
                {
                    TheReportedWindows = TheWatchedWindows.ToList();
                }
                return McpToolResult.Success(new
                {
                    idle = IsIdle,
                    mode = TheMode,
                    elapsedMs = TheElapsedMs,
                    finalElementCount = TheFinalCount,
                    windowHandle = TheMode == _MODE_ALL ? (long?)null : TheWindow.ToInt64(),
                    windows = TheReportedWindows,
                    resolutionSource = TheResolutionSource,
                    quietMs = TheQuietMs,
                    timeoutMs = TheTimeoutMs,
                });
            }
            catch (InvalidOperationException TheException)
            {
                return McpToolResult.Error(TheException.Message);
            }
            catch (Exception TheException)
            {
                return McpToolResult.Error($"ui_window_wait_idle failed: {TheException.Message}");
            }
        }

        /// <summary>
        /// 1 つのウィンドウの UIA 要素数を数える（upstream CountWalk と同じ走査・同じ上限）。ウィンドウが消えていれば例外。
        /// HWND は閉じた後に別プロセスへ再利用され得るので、毎回 PID も照合してデバッグ対象以外のウィンドウを数え続けないようにする。
        /// </summary>
        /// <param name="InWindow">対象のトップレベル HWND。</param>
        /// <param name="InProcessIds">デバッグ中プロセス ID の集合。</param>
        /// <returns>要素数。</returns>
        private static int CountSingleWindow(IntPtr InWindow, HashSet<uint> InProcessIds)
        {
            if (!IsWindow(InWindow))
            {
                throw new InvalidOperationException($"Window handle {InWindow.ToInt64()} was closed while waiting for it to become idle.");
            }
            GetWindowThreadProcessId(InWindow, out uint TheProcessId);
            if (!InProcessIds.Contains(TheProcessId))
            {
                throw new InvalidOperationException($"Window handle {InWindow.ToInt64()} was closed or reused by another process while waiting for it to become idle.");
            }
            int TheCount = 0;
            UiTools.CountWalk(AutomationElement.FromHandle(InWindow), ref TheCount);
            return TheCount;
        }

        /// <summary>
        /// 可視・有効・非最小化のデバッグ対象トップレベルウィンドウ全部の UIA 要素数の合計にウィンドウ数を加えて返す（出現・消滅も変化として扱う）。
        /// モーダルで無効化された owner は対象外なので永久待機にならない。デバッグ対象プロセスが全て終了していれば例外。
        /// </summary>
        /// <param name="InProcessIds">デバッグ中プロセス ID の集合。</param>
        /// <param name="InOutWatchedWindows">最新の対象ウィンドウ一覧を書き戻すリスト。</param>
        /// <returns>合計値。</returns>
        private static int CountAllInteractableWindows(HashSet<uint> InProcessIds, List<long> InOutWatchedWindows)
        {
            bool IsAnyProcessAlive = InProcessIds.Any(TheProcessId =>
            {
                try
                {
                    using (System.Diagnostics.Process TheProcess = System.Diagnostics.Process.GetProcessById((int)TheProcessId))
                    {
                        return !TheProcess.HasExited;
                    }
                }
                catch
                {
                    return false;
                }
            });
            if (!IsAnyProcessAlive)
            {
                throw new InvalidOperationException("Debugging stopped while waiting for the debugged application to become idle.");
            }

            List<WindowInfo> TheWindows = DebuggeeWindowEnumerator.EnumerateTopLevelWindows(InProcessIds, false)
                .Where(TheWindow => TheWindow.IsVisible && TheWindow.IsEnabled && !TheWindow.IsMinimized)
                .ToList();

            int TheTotal = TheWindows.Count;
            List<long> TheHandles = new List<long>();
            foreach (WindowInfo TheWindow in TheWindows)
            {
                TheHandles.Add(TheWindow.Handle);
                try
                {
                    int TheCount = 0;
                    UiTools.CountWalk(AutomationElement.FromHandle(new IntPtr(TheWindow.Handle)), ref TheCount);
                    TheTotal += TheCount;
                }
                catch
                {
                    // 数えている間に閉じたウィンドウは 0 扱い（次のポーリングで一覧から消える）
                }
            }

            lock (_WatchedWindowsLock)
            {
                InOutWatchedWindows.Clear();
                InOutWatchedWindows.AddRange(TheHandles);
            }
            return TheTotal;
        }
    }
}
