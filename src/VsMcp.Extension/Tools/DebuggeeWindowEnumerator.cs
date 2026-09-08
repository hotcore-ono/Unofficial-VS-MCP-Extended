using System;
using System.Collections.Generic;
using System.Text;
using EnvDTE80;

using static VsMcp.Extension.Tools.NativeMethods;

namespace VsMcp.Extension.Tools
{
    /// <summary>
    /// Visual Studio がデバッグ中の全プロセスに属するトップレベルウィンドウを列挙し、<see cref="WindowInfo"/> を組み立てる。
    /// 既存 UiTools.GetDebuggeeWindowHandle（先頭プロセスの MainWindowHandle 固定）とは独立した Extended 側の実装で、
    /// メインウィンドウ以外（ShowDialog のダイアログ、MessageBox、Owner 付きツールウィンドウ等）も対象にする。
    /// Win32 API は NativeMethods のみを使い、DebuggerTools の private 宣言には依存しない。
    /// </summary>
    internal static class DebuggeeWindowEnumerator
    {
        /// <summary>GetDpiForWindow が使えない環境（Windows 10 1607 未満）で返す既定 DPI。</summary>
        private const int DefaultDpi = 96;

        /// <summary>GetClassNameW に渡すバッファ長。クラス名は最大 256 文字。</summary>
        private const int ClassNameBufferLength = 256;

        /// <summary>
        /// Visual Studio が現在デバッグしている全プロセスの PID を返す。
        /// DTE の COM オブジェクトへ触るため、呼び出し側が UI スレッド上で実行すること。
        /// </summary>
        /// <param name="InDte">DTE2 インスタンス。</param>
        /// <returns>PID の集合。デザインモード時やプロセスが無い場合は空集合。</returns>
        public static HashSet<uint> GetDebuggedProcessIds(DTE2 InDte)
        {
            HashSet<uint> TheProcessIds = new HashSet<uint>();
            try
            {
                if (InDte?.Debugger == null || InDte.Debugger.CurrentMode == EnvDTE.dbgDebugMode.dbgDesignMode)
                    return TheProcessIds;

                foreach (EnvDTE.Process TheProcess in InDte.Debugger.DebuggedProcesses)
                {
                    try
                    {
                        TheProcessIds.Add((uint)TheProcess.ProcessID);
                    }
                    catch
                    {
                        // 終了直後のプロセスは ProcessID 取得に失敗することがあるため読み飛ばす
                    }
                }
            }
            catch
            {
                // デバッガ状態遷移中の COM 例外は「プロセス無し」として扱う
            }
            return TheProcessIds;
        }

        /// <summary>
        /// 指定 PID 群に属するトップレベルウィンドウを EnumWindows の順序（Z オーダー、前面が先）で列挙する。
        /// 子ウィンドウは EnumWindows の対象外なので含まれない。Owner 付きウィンドウ（ダイアログ等）はトップレベルとして含まれる。
        /// </summary>
        /// <param name="InProcessIds">対象プロセス ID の集合。</param>
        /// <param name="InIsInvisibleIncluded">true の場合、非表示（IsWindowVisible=false）のウィンドウも含める。</param>
        /// <returns>列挙結果。該当が無ければ空リスト。</returns>
        public static List<WindowInfo> EnumerateTopLevelWindows(HashSet<uint> InProcessIds, bool InIsInvisibleIncluded)
        {
            List<WindowInfo> TheWindows = new List<WindowInfo>();
            if (InProcessIds == null || InProcessIds.Count == 0)
                return TheWindows;

            IntPtr TheForegroundHandle = GetForegroundWindow();

            EnumWindows((TheHandle, TheLParam) =>
            {
                try
                {
                    GetWindowThreadProcessId(TheHandle, out uint TheProcessId);
                    if (!InProcessIds.Contains(TheProcessId))
                        return true;

                    bool TheIsVisible = IsWindowVisible(TheHandle);
                    if (!TheIsVisible && !InIsInvisibleIncluded)
                        return true;

                    TheWindows.Add(BuildWindowInfo(TheHandle, TheProcessId, TheIsVisible, TheForegroundHandle));
                }
                catch
                {
                    // 列挙中にウィンドウが破棄された場合などは、そのウィンドウだけ読み飛ばして続行する
                }
                return true;
            }, IntPtr.Zero);

            return TheWindows;
        }

        /// <summary>1 つの HWND から <see cref="WindowInfo"/> を組み立てる。</summary>
        /// <param name="InHandle">対象ウィンドウの HWND。</param>
        /// <param name="InProcessId">対象ウィンドウのプロセス ID（取得済みの値を再利用する）。</param>
        /// <param name="InIsVisible">IsWindowVisible の結果（取得済みの値を再利用する）。</param>
        /// <param name="InForegroundHandle">列挙開始時点のフォアグラウンドウィンドウ。</param>
        /// <returns>組み立てた WindowInfo。</returns>
        private static WindowInfo BuildWindowInfo(IntPtr InHandle, uint InProcessId, bool InIsVisible, IntPtr InForegroundHandle)
        {
            IntPtr TheOwnerHandle = GetWindow(InHandle, GW_OWNER);
            bool TheIsEnabled = IsWindowEnabled(InHandle);

            return new WindowInfo
            {
                Handle = InHandle.ToInt64(),
                HandleHex = FormatHandleHex(InHandle),
                Title = GetWindowTitle(InHandle),
                ClassName = GetWindowClassName(InHandle),
                ProcessId = InProcessId,
                IsVisible = InIsVisible,
                IsEnabled = TheIsEnabled,
                IsMinimized = IsIconic(InHandle),
                IsForeground = InHandle == InForegroundHandle,
                OwnerHandle = TheOwnerHandle.ToInt64(),
                IsModalCandidate = DetermineIsModalCandidate(TheOwnerHandle, InIsVisible, TheIsEnabled),
                Bounds = GetBoundsText(InHandle),
                Dpi = GetWindowDpi(InHandle),
                Monitor = GetMonitorDeviceName(InHandle),
            };
        }

        /// <summary>
        /// モーダルダイアログ候補かどうかを推定する。断定ではなく候補判定。
        /// 主条件: Owner が存在し、その Owner が無効化（IsWindowEnabled=false）されている（WPF ShowDialog / MessageBox / TaskDialog の典型）。
        /// 併せて対象自身が可視かつ有効であることを要求し、Owner 側や隠しウィンドウを候補から外す。
        /// クラス名 #32770 や WS_EX_DLGMODALFRAME は単独では判定に使わない（モードレスの標準ダイアログも同じ特徴を持つため）。
        /// </summary>
        /// <param name="InOwnerHandle">Owner ウィンドウの HWND。無い場合は IntPtr.Zero。</param>
        /// <param name="InIsVisible">対象ウィンドウが可視か。</param>
        /// <param name="InIsEnabled">対象ウィンドウが有効か。</param>
        /// <returns>候補なら true。</returns>
        private static bool DetermineIsModalCandidate(IntPtr InOwnerHandle, bool InIsVisible, bool InIsEnabled)
        {
            if (InOwnerHandle == IntPtr.Zero || !InIsVisible || !InIsEnabled)
                return false;

            // Owner が無効化されていることが主条件。Owner が有効ならモードレス（Show + Owner）とみなす。
            return !IsWindowEnabled(InOwnerHandle);
        }

        /// <summary>HWND を "0x" 付き 8 桁の 16 進文字列にする（32bit を超える値の場合は 16 桁）。</summary>
        /// <param name="InHandle">対象 HWND。</param>
        /// <returns>16 進表現。</returns>
        private static string FormatHandleHex(IntPtr InHandle)
        {
            long TheValue = InHandle.ToInt64();
            return TheValue <= uint.MaxValue
                ? "0x" + TheValue.ToString("X8")
                : "0x" + TheValue.ToString("X16");
        }

        /// <summary>GetWindowTextW でウィンドウタイトルを取得する。取得できない場合は空文字。</summary>
        /// <param name="InHandle">対象 HWND。</param>
        /// <returns>タイトル文字列。</returns>
        private static string GetWindowTitle(IntPtr InHandle)
        {
            int TheLength = GetWindowTextLengthW(InHandle);
            if (TheLength <= 0)
                return string.Empty;

            StringBuilder TheBuffer = new StringBuilder(TheLength + 1);
            int TheCopied = GetWindowTextW(InHandle, TheBuffer, TheBuffer.Capacity);
            return TheCopied > 0 ? TheBuffer.ToString() : string.Empty;
        }

        /// <summary>GetClassNameW でウィンドウクラス名を取得する。取得できない場合は空文字。</summary>
        /// <param name="InHandle">対象 HWND。</param>
        /// <returns>クラス名。</returns>
        private static string GetWindowClassName(IntPtr InHandle)
        {
            StringBuilder TheBuffer = new StringBuilder(ClassNameBufferLength);
            int TheCopied = GetClassNameW(InHandle, TheBuffer, TheBuffer.Capacity);
            return TheCopied > 0 ? TheBuffer.ToString() : string.Empty;
        }

        /// <summary>
        /// ウィンドウ矩形を "x,y,width,height" 形式で返す。
        /// Per-Monitor DPI Awareness V2 のスレッド文脈で GetWindowRect を呼び、スクリーン座標の物理ピクセルを得る
        /// （既存 UiTools.CaptureWithPrintWindow / ValidateCoordinatesInWindow と同じ扱い）。
        /// </summary>
        /// <param name="InHandle">対象 HWND。</param>
        /// <returns>矩形文字列。取得失敗時は null。</returns>
        private static string GetBoundsText(IntPtr InHandle)
        {
            return RunWithDpiAwareness(() =>
            {
                if (!GetWindowRect(InHandle, out RECT TheRect))
                    return (string)null;

                int TheWidth = TheRect.Right - TheRect.Left;
                int TheHeight = TheRect.Bottom - TheRect.Top;
                return $"{TheRect.Left},{TheRect.Top},{TheWidth},{TheHeight}";
            });
        }

        /// <summary>GetDpiForWindow でウィンドウの DPI を返す。API が無い環境や 0 が返る場合は 96 にフォールバックする。</summary>
        /// <param name="InHandle">対象 HWND。</param>
        /// <returns>DPI 値（96 / 120 / 144 / 192 など）。</returns>
        private static int GetWindowDpi(IntPtr InHandle)
        {
            try
            {
                uint TheDpi = GetDpiForWindow(InHandle);
                return TheDpi == 0 ? DefaultDpi : (int)TheDpi;
            }
            catch (EntryPointNotFoundException)
            {
                return DefaultDpi;
            }
        }

        /// <summary>MonitorFromWindow / GetMonitorInfoW でウィンドウが主に表示されているモニターのデバイス名を返す。</summary>
        /// <param name="InHandle">対象 HWND。</param>
        /// <returns>デバイス名（例: \\.\DISPLAY1）。取得失敗時は null。</returns>
        private static string GetMonitorDeviceName(IntPtr InHandle)
        {
            IntPtr TheMonitor = MonitorFromWindow(InHandle, MONITOR_DEFAULTTONEAREST);
            if (TheMonitor == IntPtr.Zero)
                return null;

            MONITORINFOEX TheInfo = new MONITORINFOEX();
            TheInfo.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(MONITORINFOEX));
            if (!GetMonitorInfoW(TheMonitor, ref TheInfo))
                return null;

            return string.IsNullOrEmpty(TheInfo.szDevice) ? null : TheInfo.szDevice;
        }

        /// <summary>
        /// Per-Monitor DPI Awareness V2 のスレッド文脈で処理を実行する。
        /// UiTools.WithDpiAwareness と同等だが、upstream 由来の UiTools を変更しない方針のため Extended 側で保持する。
        /// </summary>
        /// <typeparam name="T">戻り値の型。</typeparam>
        /// <param name="InAction">実行する処理。</param>
        /// <returns>処理の戻り値。</returns>
        private static T RunWithDpiAwareness<T>(Func<T> InAction)
        {
            IntPtr ThePrevious = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            try
            {
                return InAction();
            }
            finally
            {
                if (ThePrevious != IntPtr.Zero)
                    SetThreadDpiAwarenessContext(ThePrevious);
            }
        }
    }
}
