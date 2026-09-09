using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using VsMcp.Extension.McpServer;
using VsMcp.Extension.Services;
using VsMcp.Shared;
using VsMcp.Shared.Protocol;
using static VsMcp.Extension.Tools.NativeMethods;

namespace VsMcp.Extension.Tools
{
    /// <summary>
    /// Extended 独自の「任意のデバッグ対象トップレベル HWND を送信先として確定させてからキーを送る」ツール（ui_window_send_keys）。
    /// upstream の ui_send_keys はメインウィンドウを無条件に前面化して送るため、モーダルダイアログ表示中でも無効化された owner を
    /// 前面化しようとする。ここでは Phase 7 と同じ安全境界（HWND 検証 → デバッグ対象 PID → モーダル判定 → Geometry）を通し、
    /// フォアグラウンドの扱いを mode で明示してから、入力注入だけを共有層（<see cref="UiKeyboardInput"/>）へ委ねる。
    /// AutomationElement はツール呼び出し間で保持しない（この ツールは UIA 要素を使わない）。
    /// </summary>
    public static class UiWindowKeyboardTools
    {
        /// <summary>waitMs の上限（upstream ui_send_keys / Phase 7 と同じ）。</summary>
        private const int _MAX_WAIT_MS = 10000;

        /// <summary>SetForegroundWindow 後に Z 順の反映を待つ時間（upstream ui_send_keys と同じ 100 ms）。</summary>
        private const int _FOREGROUND_SETTLE_MS = 100;

        /// <summary>mode: 既に最前面ならそのまま、そうでなければデバッグ対象のアクティブウィンドウであるときだけ前面化する。</summary>
        private const string _MODE_AUTO = "auto";

        /// <summary>mode: 対象ウィンドウを前面化してから送る。</summary>
        private const string _MODE_FOREGROUND = "foreground";

        /// <summary>mode: 対象が既に最前面のときだけ送る（前面化しない）。</summary>
        private const string _MODE_NO_FOREGROUND = "noForeground";

        /// <summary>ツールをレジストリへ登録する。VsMcpPackage.RegisterTools から呼ばれる。</summary>
        /// <param name="InRegistry">登録先レジストリ。</param>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        public static void Register(McpToolRegistry InRegistry, VsServiceAccessor InAccessor)
        {
            InRegistry.Register(
                new McpToolDefinition(
                    "ui_window_send_keys",
                    "[Windows UIA — desktop app being debugged] Send keystrokes to ONE top-level window of the debugged application identified by its HWND (modal dialog, " +
                    "owned window, main window), instead of always to the main window like ui_send_keys. The key syntax is exactly the one of ui_send_keys: 'keys' is a " +
                    "space-separated sequence of combinations ('escape', 'ctrl+s', 'tab tab enter'; modifiers ctrl/control, shift, alt, win plus a named key or a single " +
                    "character) and the optional 'text' is typed character by character afterwards. Safety boundary (always enforced): the handle is re-validated " +
                    "(IsWindow, normalization to the top-level window, debugged-process check), the window must not be disabled by a modal dialog — a blocked owner is " +
                    "refused with the blocking window in the error — and it must be visible. Keys are never sent to a window that is not the confirmed input target: " +
                    "with mode 'noForeground' the call fails unless the window already is the foreground window, with 'foreground' the window is brought to the front " +
                    "and the call fails if that did not work, and with 'auto' (default) the window is left alone when it already is the foreground window and is only " +
                    "brought to the front when the debugged application's active window is this window (or a popup owned by it). A handle that is itself an open popup " +
                    "menu is refused: to dismiss an open popup or context menu use ui_menu_close instead — a menu has no foreground window of its own and bringing " +
                    "another window to the front dismisses it in an uncontrolled way. The keys are injected with SendInput and the call fails when Windows did not " +
                    "accept every event. Returns text { sent, windowHandle, keys, text, mode, foregroundBefore, foregroundAfter, changedForeground, insertedEvents " +
                    "(number of key events actually injected), geometry (re-read after the window was brought to the front) }.",
                    SchemaBuilder.Create()
                        .AddInteger("windowHandle", "HWND of the window that receives the keystrokes (decimal)", required: true)
                        .AddString("keys", "Key sequence to send, space-separated (e.g. 'escape', 'ctrl+s', 'tab tab enter'); same syntax as ui_send_keys", required: true)
                        .AddString("text", "Text typed character by character after 'keys' (optional)")
                        .AddEnum("mode", "How the foreground window is handled: 'auto' (default), 'foreground', 'noForeground'",
                            new[] { _MODE_AUTO, _MODE_FOREGROUND, _MODE_NO_FOREGROUND })
                        .AddInteger("waitMs", "Milliseconds to wait after sending (default: 0, max: 10000)")
                        .Build()),
                InArgs => UiWindowSendKeysAsync(InAccessor, InArgs));
        }

        /// <summary>
        /// ui_window_send_keys の本体。引数を解釈してから、1 回の呼び出しの中で安全境界 → フォアグラウンド確定 → 入力注入を行う。
        /// </summary>
        /// <param name="InAccessor">DTE / UI スレッドアクセサ。</param>
        /// <param name="InArgs">ツール引数（windowHandle / keys / text / mode / waitMs）。</param>
        /// <returns>
        /// text（sent / windowHandle / keys / text / mode / foregroundBefore / foregroundAfter / changedForeground / insertedEvents / geometry）、またはエラー。
        /// </returns>
        private static async Task<McpToolResult> UiWindowSendKeysAsync(VsServiceAccessor InAccessor, JObject InArgs)
        {
            string TheKeys = InArgs.Value<string>("keys");
            if (string.IsNullOrEmpty(TheKeys))
            {
                return McpToolResult.Error("Parameter 'keys' is required (e.g. 'escape', 'ctrl+s', 'tab tab enter'); same syntax as ui_send_keys");
            }
            string TheText = InArgs.Value<string>("text");

            string TheMode = InArgs.Value<string>("mode") ?? _MODE_AUTO;
            if (!string.Equals(TheMode, _MODE_AUTO, StringComparison.Ordinal)
                && !string.Equals(TheMode, _MODE_FOREGROUND, StringComparison.Ordinal)
                && !string.Equals(TheMode, _MODE_NO_FOREGROUND, StringComparison.Ordinal))
            {
                return McpToolResult.Error($"Unknown mode: '{TheMode}'. Expected one of: {_MODE_AUTO}, {_MODE_FOREGROUND}, {_MODE_NO_FOREGROUND}");
            }

            int TheWaitMs = InArgs.Value<int?>("waitMs") ?? 0;
            if (TheWaitMs < 0)
            {
                TheWaitMs = 0;
            }
            if (TheWaitMs > _MAX_WAIT_MS)
            {
                TheWaitMs = _MAX_WAIT_MS;
            }

            (IntPtr TheWindow, HashSet<uint> TheProcessIds, McpToolResult TheError) = await UiWindowUiaTools.ResolveWindowWithProcessesAsync(InAccessor, InArgs);
            if (TheError != null)
            {
                return TheError;
            }

            // Extended (Phase 9): 開いているポップアップメニューへはキーを送らない（メニューは自前のフォアグラウンドを持たず、
            // 前面化やキー送信で不確実に取り下げられるため、専用の ui_menu_close / ui_menu_select を使わせる）
            McpToolResult TheMenuRejection = await DescribeMenuTargetAsync(TheWindow, TheProcessIds);
            if (TheMenuRejection != null)
            {
                return TheMenuRejection;
            }

            McpToolResult TheResult;
            try
            {
                TheResult = await UiTools.RunUiaWithTimeoutAsync(() => SendKeysToWindow(TheWindow, TheProcessIds, TheKeys, TheText, TheMode));
            }
            catch (TimeoutException TheException)
            {
                return McpToolResult.Error(TheException.Message);
            }
            catch (Exception TheException)
            {
                return McpToolResult.Error($"ui_window_send_keys failed in window {TheWindow.ToInt64()}: {TheException.Message}");
            }

            if (!TheResult.IsError && TheWaitMs > 0)
            {
                await Task.Delay(TheWaitMs);
            }
            return TheResult;
        }

        /// <summary>
        /// 送信の本体。安全境界（Context 生成 = IsWindow → PID → モーダル判定 → 可視）→ mode に従ったフォアグラウンドの確定 →
        /// 送信直前の再検証と Geometry 取り直し → 入力注入。送信先が確定できない場合は 1 キーも送らずにエラーを返す。
        /// STA スレッドで呼ぶこと。
        /// </summary>
        /// <param name="InWindow">正規化済みのトップレベル HWND。</param>
        /// <param name="InProcessIds">デバッグ中プロセス ID の集合。</param>
        /// <param name="InKeys">送るキーの並び。</param>
        /// <param name="InText">送る文字列（省略可）。</param>
        /// <param name="InMode">auto / foreground / noForeground。</param>
        /// <returns>送信結果、またはエラー。</returns>
        private static McpToolResult SendKeysToWindow(IntPtr InWindow, HashSet<uint> InProcessIds, string InKeys, string InText, string InMode)
        {
            string TheContextError = UiInteractionContext.TryCreate(InWindow, InProcessIds, out UiInteractionContext TheContext);
            if (TheContextError != null)
            {
                return McpToolResult.Error(TheContextError);
            }

            long TheWindowHandle = InWindow.ToInt64();
            long TheForegroundBefore = ResolveForegroundRoot();
            string TheForegroundError = EnsureForegroundTarget(InWindow, InProcessIds, InMode, TheForegroundBefore);
            if (TheForegroundError != null)
            {
                return McpToolResult.Error(TheForegroundError);
            }

            // 前面化で対象が変わっていないことを送信直前に確かめ、Geometry も前面化後の値にする
            string TheReverifyError = TheContext.ReverifyBeforeAction("window");
            if (TheReverifyError != null)
            {
                return McpToolResult.Error(TheReverifyError);
            }
            TheContext.RefreshGeometry();

            int TheInsertedEvents;
            try
            {
                TheInsertedEvents = UiKeyboardInput.SendKeys(InKeys, InText);
            }
            catch (InvalidOperationException TheException)
            {
                return McpToolResult.Error(
                    $"ui_window_send_keys could not inject the keystrokes into window {TheWindowHandle} ({TheException.Message}); no keys were sent");
            }

            long TheForegroundAfter = ResolveForegroundRoot();
            return McpToolResult.Success(new
            {
                sent = true,
                windowHandle = TheWindowHandle,
                keys = InKeys,
                text = InText,
                mode = InMode,
                foregroundBefore = TheForegroundBefore,
                foregroundAfter = TheForegroundAfter,
                changedForeground = TheForegroundBefore != TheForegroundAfter,
                // Extended (Phase 9): SendInput が実際に注入したイベント数（キーの down / up がそれぞれ 1 件）
                insertedEvents = TheInsertedEvents,
                geometry = TheContext.Geometry,
            });
        }

        /// <summary>
        /// Extended (Phase 9): 送信先がポップアップメニューなら送らせない。Win32 メニュークラス（#32768）は即座に拒否し、
        /// それ以外は「Owner 付きかタイトルが空」というメニュー候補の条件を満たす場合だけ内容分類を試す（UIA 照会は高価なため）。
        /// </summary>
        /// <param name="InWindow">正規化済みのトップレベル HWND。</param>
        /// <param name="InProcessIds">デバッグ中プロセス ID の集合。</param>
        /// <returns>メニューだった場合のエラー結果。メニューでなければ null。</returns>
        private static async Task<McpToolResult> DescribeMenuTargetAsync(IntPtr InWindow, HashSet<uint> InProcessIds)
        {
            long TheWindowHandle = InWindow.ToInt64();
            WindowInfo TheWindowInfo = await UiWindowTools.FindWindowInfoAsync(InProcessIds, TheWindowHandle);
            if (TheWindowInfo == null)
            {
                // 観測できないウィンドウはここでは判定せず、後続の安全境界（Context 生成）に委ねる
                return null;
            }
            if (string.Equals(TheWindowInfo.ClassName, UiPopupMenuResolver.Win32MenuClassName, StringComparison.Ordinal))
            {
                return McpToolResult.Error(DescribePopupMenuTarget(TheWindowHandle));
            }
            if (TheWindowInfo.OwnerHandle == 0 && !string.IsNullOrEmpty(TheWindowInfo.Title))
            {
                // Owner もタイトルもある通常のウィンドウはポップアップの候補ではない
                return null;
            }

            bool IsMenu;
            try
            {
                IsMenu = await UiTools.RunUiaWithTimeoutAsync(() =>
                    UiPopupMenuResolver.Classify(TheWindowInfo, false, out UiMenuInfo TheMenu, out _)
                    && UiPopupMenuResolver.IsKnownMenuType(TheMenu.MenuType));
            }
            catch (TimeoutException TheException)
            {
                return McpToolResult.Error(TheException.Message);
            }
            catch (Exception)
            {
                // 分類できなかった場合はメニューと断定せず、後続の安全境界に委ねる
                return null;
            }
            return IsMenu ? McpToolResult.Error(DescribePopupMenuTarget(TheWindowHandle)) : null;
        }

        /// <summary>Extended (Phase 9): 送信先がポップアップメニューだったときのエラー文。</summary>
        /// <param name="InWindowHandle">対象のトップレベル HWND（10 進）。</param>
        /// <returns>エラーメッセージ。</returns>
        private static string DescribePopupMenuTarget(long InWindowHandle)
        {
            return $"Window {InWindowHandle} is a popup menu; use ui_menu_close / ui_menu_select instead of sending keys to it";
        }

        /// <summary>
        /// mode に従って「送信先が対象ウィンドウであること」を確定させる。noForeground は前面化せず、foreground は前面化して確認し、
        /// auto は既に最前面ならそのまま、そうでなければデバッグ対象のアクティブウィンドウが対象（または対象を Owner に持つポップアップ）の
        /// ときだけ前面化する。確定できない場合はエラーを返し、呼び出し側はキーを送らない。STA スレッドで呼ぶこと。
        /// </summary>
        /// <param name="InWindow">対象のトップレベル HWND。</param>
        /// <param name="InProcessIds">デバッグ中プロセス ID の集合。</param>
        /// <param name="InMode">auto / foreground / noForeground。</param>
        /// <param name="InForegroundBefore">送信前のフォアグラウンド（トップレベル HWND、10 進）。</param>
        /// <returns>エラーメッセージ。送ってよければ null。</returns>
        private static string EnsureForegroundTarget(IntPtr InWindow, HashSet<uint> InProcessIds, string InMode, long InForegroundBefore)
        {
            long TheWindowHandle = InWindow.ToInt64();
            if (InForegroundBefore == TheWindowHandle)
            {
                // 既に最前面なら、どの mode でもフォアグラウンドを動かさない（前面化はポップアップを取り下げる副作用がある）
                return null;
            }

            if (string.Equals(InMode, _MODE_NO_FOREGROUND, StringComparison.Ordinal))
            {
                return DescribeNotForeground(TheWindowHandle, InForegroundBefore);
            }
            if (string.Equals(InMode, _MODE_AUTO, StringComparison.Ordinal) && !IsDebuggeeActiveWindow(InWindow, InProcessIds))
            {
                return DescribeNotForeground(TheWindowHandle, InForegroundBefore);
            }

            SetForegroundWindow(InWindow);
            Thread.Sleep(_FOREGROUND_SETTLE_MS);
            long TheForegroundNow = ResolveForegroundRoot();
            if (TheForegroundNow != TheWindowHandle)
            {
                return $"ui_window_send_keys could not bring window {TheWindowHandle} to the foreground (foreground is {TheForegroundNow}); no keys were sent";
            }
            return null;
        }

        /// <summary>
        /// デバッグ対象アプリのアクティブウィンドウが対象ウィンドウ自身か、対象を Owner に持つポップアップかを判定する
        /// （Visual Studio が最前面でも、対象がデバッグ対象内で入力を受け取る側であることを確かめるため）。STA スレッドで呼ぶこと。
        /// </summary>
        /// <param name="InWindow">対象のトップレベル HWND。</param>
        /// <param name="InProcessIds">デバッグ中プロセス ID の集合。</param>
        /// <returns>対象がデバッグ対象のアクティブウィンドウなら true。</returns>
        private static bool IsDebuggeeActiveWindow(IntPtr InWindow, HashSet<uint> InProcessIds)
        {
            List<WindowInfo> TheVisibleWindows = DebuggeeWindowEnumerator.EnumerateTopLevelWindows(InProcessIds, false);
            ActiveWindowResolution TheResolution = DebuggeeWindowResolver.ResolveActiveWindow(InProcessIds, TheVisibleWindows);
            if (!TheResolution.IsResolved)
            {
                return false;
            }

            long TheWindowHandle = InWindow.ToInt64();
            if (TheResolution.Resolved.Handle == TheWindowHandle)
            {
                return true;
            }
            return TheResolution.Resolved.OwnerHandle == TheWindowHandle;
        }

        /// <summary>対象が最前面ではないため送信を拒否するときのエラー文。</summary>
        /// <param name="InWindowHandle">対象のトップレベル HWND（10 進）。</param>
        /// <param name="InForegroundHandle">現在のフォアグラウンド（トップレベル HWND、10 進）。</param>
        /// <returns>エラーメッセージ。</returns>
        private static string DescribeNotForeground(long InWindowHandle, long InForegroundHandle)
        {
            return $"Window {InWindowHandle} is not the foreground window (foreground is {InForegroundHandle}); use mode=foreground or bring it to front first";
        }

        /// <summary>現在のフォアグラウンドウィンドウをトップレベル（GA_ROOT）へ正規化して返す。</summary>
        /// <returns>トップレベル HWND（10 進）。取得できなければ 0。</returns>
        private static long ResolveForegroundRoot()
        {
            IntPtr TheForeground = GetForegroundWindow();
            if (TheForeground == IntPtr.Zero)
            {
                return 0;
            }
            IntPtr TheRoot = GetAncestor(TheForeground, GA_ROOT);
            return (TheRoot == IntPtr.Zero ? TheForeground : TheRoot).ToInt64();
        }
    }
}
