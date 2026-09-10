using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using static VsMcp.Extension.Tools.NativeMethods;

namespace VsMcp.Extension.Tools
{
    /// <summary>
    /// Extended (Phase 10): Error と選択した Warning のときに、同じ correlationId で「そのときの画面の状態」を 1 件だけ残す
    /// （指示書 §26・§27）。可視ウィンドウ一覧・フォアグラウンド / アクティブ / フォーカス / キャプチャの HWND・
    /// 対象ウィンドウ・モーダル・メニュー候補・Geometry を上限つきで集める。
    /// 収集は必ず背景で行い、Tool の戻り値も所要時間も変えない。収集自体が失敗しても何も起きない。
    /// </summary>
    internal static class DiagnosticErrorDump
    {
        /// <summary>ダンプの収集に使ってよい最大時間（ミリ秒）。</summary>
        private const int _MAX_COLLECT_MS = 500;

        /// <summary>デバッグ中プロセス ID の取得を待つ最大時間（ミリ秒）。</summary>
        private const int _PROCESS_ID_TIMEOUT_MS = 300;

        /// <summary>Win32 のポップアップメニューのクラス名。</summary>
        private const string _WIN32_MENU_CLASS_NAME = "#32768";

        /// <summary>WPF のウィンドウクラス名の接頭辞。</summary>
        private const string _WPF_WINDOW_CLASS_PREFIX = "HwndWrapper[";

        /// <summary>スクリーンショットの保存先（Diagnostics\screenshots）。</summary>
        private static string ScreenshotFolderPath
        {
            get { return DiagnosticSettings.GetFolder(Path.Combine("Diagnostics", "screenshots")); }
        }

        /// <summary>
        /// 詳細状態ダンプを背景で収集して記録する。errorDetailDump=false のとき、または診断が無効のときは何もしない。
        /// </summary>
        /// <param name="InScope">記録元の相関スコープ。null なら何もしない。</param>
        /// <param name="InToolName">Tool 名。null / 空ならスコープの Tool 名で補う。</param>
        /// <param name="InLevel">記録する水準（error / warning）。</param>
        /// <param name="InReason">ダンプを取る理由（"tool.error" / "modal.block" / "menu.fallback" / "interaction.fallback"）。</param>
        public static void Schedule(DiagnosticScope InScope, string InToolName, DiagnosticLevel InLevel, string InReason)
        {
            Schedule(InScope, InToolName, InLevel, InReason, 0);
        }

        /// <summary>
        /// 詳細状態ダンプを背景で収集して記録する（対象ウィンドウを指定する版）。
        /// 同じ相関スコープでは 1 回しかスケジュールしない（先に来た具体的な理由を残し、後続の tool.error / tool.exception は捨てる）。
        /// </summary>
        /// <param name="InScope">記録元の相関スコープ。null なら何もしない。</param>
        /// <param name="InToolName">Tool 名。null / 空ならスコープの Tool 名で補う。</param>
        /// <param name="InLevel">記録する水準（error / warning）。</param>
        /// <param name="InReason">ダンプを取る理由。</param>
        /// <param name="InTargetWindowHandle">対象のトップレベル HWND（10 進）。不明なら 0。</param>
        public static void Schedule(DiagnosticScope InScope, string InToolName, DiagnosticLevel InLevel, string InReason, long InTargetWindowHandle)
        {
            try
            {
                DiagnosticSettings TheSettings = DiagnosticHub.Settings;
                if (InScope == null || TheSettings == null || !TheSettings.IsErrorDetailDumpEnabled || !DiagnosticHub.IsEnabled(InLevel))
                {
                    return;
                }

                if (InScope.IsErrorDumpScheduled)
                {
                    // 同じ correlationId のダンプは 1 件だけにする（modal.block / menu.fallback の直後に来る tool.error で二重に撮らない）
                    return;
                }
                InScope.IsErrorDumpScheduled = true;

                // modal.block / menu.fallback の経路は Tool 名を渡してこないので、スコープの Tool 名で補う
                string TheToolName = string.IsNullOrEmpty(InToolName) ? InScope.ToolName : InToolName;
                string TheCorrelationId = InScope.CorrelationId;
                Task.Run(() => CollectAndEmitAsync(TheCorrelationId, TheToolName, InLevel, InReason, InTargetWindowHandle, TheSettings));
            }
            catch (Exception)
            {
                // ダンプの起動に失敗しても Tool には影響させない
            }
        }

        /// <summary>ダンプを組み立てて記録する（背景スレッドで動く）。</summary>
        /// <param name="InCorrelationId">記録元と同じ相関 ID。</param>
        /// <param name="InToolName">Tool 名。</param>
        /// <param name="InLevel">記録する水準。</param>
        /// <param name="InReason">ダンプを取る理由。</param>
        /// <param name="InTargetWindowHandle">対象のトップレベル HWND。不明なら 0。</param>
        /// <param name="InSettings">診断設定。</param>
        private static async Task CollectAndEmitAsync(string InCorrelationId, string InToolName, DiagnosticLevel InLevel, string InReason,
            long InTargetWindowHandle, DiagnosticSettings InSettings)
        {
            try
            {
                Stopwatch TheStopwatch = Stopwatch.StartNew();
                JObject TheDump = new JObject
                {
                    ["reason"] = InReason,
                    ["targetWindowHandle"] = InTargetWindowHandle,
                };

                FillFocusState(TheDump);
                List<WindowInfo> TheWindows = await CollectWindowsAsync(TheStopwatch);
                bool IsTruncated = FillWindows(TheDump, TheWindows, InTargetWindowHandle, InSettings, TheStopwatch);
                IsTruncated = FillGeometry(TheDump, InTargetWindowHandle, TheStopwatch) || IsTruncated;
                TheDump["collectElapsedMs"] = TheStopwatch.ElapsedMilliseconds;
                TheDump["truncated"] = TrimToLimit(TheDump, InSettings) || IsTruncated;

                string TheScreenshotPath = await TryCaptureScreenshotAsync(InSettings, InCorrelationId, InToolName, InTargetWindowHandle);
                if (TheScreenshotPath != null)
                {
                    TheDump["screenshotFile"] = Path.GetFileName(TheScreenshotPath);
                }

                DiagnosticHub.EmitCore(InLevel, DiagnosticCategory.DIAGNOSTICS, "diagnostics.errorDump", InToolName,
                    InCorrelationId, null, null, null, InOutData =>
                    {
                        foreach (KeyValuePair<string, JToken> ThePair in TheDump)
                        {
                            InOutData[ThePair.Key] = ThePair.Value;
                        }
                    }, null);
            }
            catch (Exception)
            {
                // 収集の失敗は握り潰す（ダンプが取れないこと自体は Tool の結果に影響しない）
            }
        }

        /// <summary>フォアグラウンド / アクティブ / フォーカス / キャプチャの HWND を埋める。</summary>
        /// <param name="InOutDump">書き込み先のダンプ。</param>
        private static void FillFocusState(JObject InOutDump)
        {
            try
            {
                IntPtr TheForeground = GetForegroundWindow();
                InOutDump["foregroundHandle"] = TheForeground.ToInt64();

                uint TheThreadId = GetWindowThreadProcessId(TheForeground, out uint TheProcessId);
                InOutDump["foregroundProcessId"] = TheProcessId;

                GUITHREADINFO TheInfo = new GUITHREADINFO();
                TheInfo.cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(GUITHREADINFO));
                if (GetGUIThreadInfo(TheThreadId, ref TheInfo))
                {
                    InOutDump["activeHandle"] = TheInfo.hwndActive.ToInt64();
                    InOutDump["focusHandle"] = TheInfo.hwndFocus.ToInt64();
                    InOutDump["captureHandle"] = TheInfo.hwndCapture.ToInt64();
                    InOutDump["menuOwnerHandle"] = TheInfo.hwndMenuOwner.ToInt64();
                }
            }
            catch (Exception)
            {
                // 取れない項目は入れない（推測しない）
            }
        }

        /// <summary>デバッグ対象プロセスのトップレベルウィンドウを、上限つきの時間内で列挙する。</summary>
        /// <param name="InStopwatch">収集全体の経過時間。</param>
        /// <returns>列挙できたウィンドウ。取得できなければ空のリスト。</returns>
        private static async Task<List<WindowInfo>> CollectWindowsAsync(Stopwatch InStopwatch)
        {
            try
            {
                if (DiagnosticHub.Accessor == null || InStopwatch.ElapsedMilliseconds > _MAX_COLLECT_MS)
                {
                    return new List<WindowInfo>();
                }
                Task<HashSet<uint>> TheProcessTask = UiWindowTools.GetDebuggedProcessIdsAsync(DiagnosticHub.Accessor);
                if (await Task.WhenAny(TheProcessTask, Task.Delay(_PROCESS_ID_TIMEOUT_MS)) != TheProcessTask)
                {
                    return new List<WindowInfo>();
                }
                HashSet<uint> TheProcessIds = await TheProcessTask;
                if (TheProcessIds == null || TheProcessIds.Count == 0 || InStopwatch.ElapsedMilliseconds > _MAX_COLLECT_MS)
                {
                    return new List<WindowInfo>();
                }
                return DebuggeeWindowEnumerator.EnumerateTopLevelWindows(TheProcessIds, false);
            }
            catch (Exception)
            {
                return new List<WindowInfo>();
            }
        }

        /// <summary>
        /// 可視ウィンドウ・対象ウィンドウ・モーダル・メニュー候補をダンプへ書き込む。
        /// 収集全体で 500 ms を超えたらその時点で要素の追加をやめる（ダンプのために Tool の後始末を長引かせない）。
        /// </summary>
        /// <param name="InOutDump">書き込み先のダンプ。</param>
        /// <param name="InWindows">列挙したウィンドウ。</param>
        /// <param name="InTargetWindowHandle">対象のトップレベル HWND。</param>
        /// <param name="InSettings">診断設定。</param>
        /// <param name="InStopwatch">収集全体の経過時間。</param>
        /// <returns>時間切れで要素の追加を打ち切った場合は true。</returns>
        private static bool FillWindows(JObject InOutDump, List<WindowInfo> InWindows, long InTargetWindowHandle, DiagnosticSettings InSettings,
            Stopwatch InStopwatch)
        {
            JArray TheVisibleWindows = new JArray();
            JArray TheMenuCandidates = new JArray();
            bool IsTruncated = false;
            foreach (WindowInfo TheWindow in InWindows)
            {
                if (InStopwatch.ElapsedMilliseconds > _MAX_COLLECT_MS)
                {
                    IsTruncated = true;
                    break;
                }
                if (TheWindow.IsVisible && TheVisibleWindows.Count < InSettings.MaxWindows)
                {
                    TheVisibleWindows.Add(DescribeWindow(TheWindow, InSettings));
                }
                if (TheMenuCandidates.Count < InSettings.MaxUiCandidates && IsMenuCandidate(TheWindow))
                {
                    TheMenuCandidates.Add(DescribeWindow(TheWindow, InSettings));
                }
                if (TheVisibleWindows.Count >= InSettings.MaxWindows && TheMenuCandidates.Count >= InSettings.MaxUiCandidates)
                {
                    // 双方が上限に達したらこれ以上走査しない（設定どおりの打ち切りなので truncated にはしない）
                    break;
                }
            }

            InOutDump["visibleWindows"] = TheVisibleWindows;
            InOutDump["visibleWindowCount"] = TheVisibleWindows.Count;
            InOutDump["menuCandidates"] = TheMenuCandidates;

            WindowInfo TheTarget = InWindows.FirstOrDefault(TheCandidate => TheCandidate.Handle == InTargetWindowHandle);
            if (TheTarget != null)
            {
                InOutDump["targetWindow"] = DescribeWindow(TheTarget, InSettings);
            }

            WindowInfo TheBlockingModal = InWindows.FirstOrDefault(
                TheCandidate => TheCandidate.IsVisible && TheCandidate.IsEnabled && TheCandidate.IsModalCandidate);
            if (TheBlockingModal != null)
            {
                InOutDump["blockingModal"] = DescribeWindow(TheBlockingModal, InSettings);
            }
            return IsTruncated;
        }

        /// <summary>ポップアップ / メニューらしいウィンドウか判定する（Win32 メニューか、タイトル空の WPF ポップアップ）。</summary>
        /// <param name="InWindow">判定するウィンドウ。</param>
        /// <returns>メニュー候補なら true。</returns>
        private static bool IsMenuCandidate(WindowInfo InWindow)
        {
            if (!InWindow.IsVisible || InWindow.ClassName == null)
            {
                return false;
            }
            if (string.Equals(InWindow.ClassName, _WIN32_MENU_CLASS_NAME, StringComparison.Ordinal))
            {
                return true;
            }
            return InWindow.ClassName.StartsWith(_WPF_WINDOW_CLASS_PREFIX, StringComparison.Ordinal)
                && string.IsNullOrEmpty(InWindow.Title)
                && InWindow.OwnerHandle != 0;
        }

        /// <summary>ウィンドウ 1 件を、機密ポリシーに従った項目だけで表現する（Title は includeUiText のときのみ）。</summary>
        /// <param name="InWindow">対象のウィンドウ。</param>
        /// <param name="InSettings">診断設定。</param>
        /// <returns>表現した JSON。</returns>
        private static JObject DescribeWindow(WindowInfo InWindow, DiagnosticSettings InSettings)
        {
            JObject TheObject = new JObject
            {
                ["handle"] = InWindow.Handle,
                ["className"] = InWindow.ClassName,
                ["isVisible"] = InWindow.IsVisible,
                ["isEnabled"] = InWindow.IsEnabled,
                ["isMinimized"] = InWindow.IsMinimized,
                ["ownerHandle"] = InWindow.OwnerHandle,
                ["bounds"] = InWindow.Bounds,
                ["isModalCandidate"] = InWindow.IsModalCandidate,
                ["modalCandidateReason"] = InWindow.ModalCandidateReason,
                ["processId"] = InWindow.ProcessId,
                ["titleLength"] = InWindow.Title == null ? 0 : InWindow.Title.Length,
            };
            string TheTitle = DiagnosticSanitizer.SanitizeUiText(InWindow.Title, InSettings);
            if (TheTitle != null)
            {
                TheObject["title"] = TheTitle;
            }
            return TheObject;
        }

        /// <summary>対象ウィンドウの Geometry（物理矩形 / DPI / モニター）をダンプへ書き込む。</summary>
        /// <param name="InOutDump">書き込み先のダンプ。</param>
        /// <param name="InTargetWindowHandle">対象のトップレベル HWND。0 なら何もしない。</param>
        /// <param name="InStopwatch">収集全体の経過時間。500 ms を超えていれば取得しない。</param>
        /// <returns>時間切れで取得を諦めた場合は true。</returns>
        private static bool FillGeometry(JObject InOutDump, long InTargetWindowHandle, Stopwatch InStopwatch)
        {
            try
            {
                if (InTargetWindowHandle == 0)
                {
                    return false;
                }
                if (InStopwatch.ElapsedMilliseconds > _MAX_COLLECT_MS)
                {
                    return true;
                }
                IntPtr TheWindow = new IntPtr(InTargetWindowHandle);
                if (!IsWindow(TheWindow))
                {
                    return false;
                }
                InOutDump["geometry"] = JObject.FromObject(UiWindowGeometryResolver.Resolve(TheWindow));
            }
            catch (Exception)
            {
                // Geometry が取れない場合は載せない
            }
            return false;
        }

        /// <summary>
        /// ダンプ全体が maxDumpBytes に収まるよう、可視ウィンドウとメニュー候補を後ろから削る。
        /// </summary>
        /// <param name="InOutDump">対象のダンプ。</param>
        /// <param name="InSettings">診断設定。</param>
        /// <returns>削った場合は true。</returns>
        private static bool TrimToLimit(JObject InOutDump, DiagnosticSettings InSettings)
        {
            bool IsTrimmed = false;
            try
            {
                while (Encoding.UTF8.GetByteCount(InOutDump.ToString(Formatting.None)) > InSettings.MaxDumpBytes)
                {
                    JArray TheWindows = InOutDump["visibleWindows"] as JArray;
                    JArray TheCandidates = InOutDump["menuCandidates"] as JArray;
                    if (TheWindows != null && TheWindows.Count > 0)
                    {
                        TheWindows.RemoveAt(TheWindows.Count - 1);
                    }
                    else if (TheCandidates != null && TheCandidates.Count > 0)
                    {
                        TheCandidates.RemoveAt(TheCandidates.Count - 1);
                    }
                    else
                    {
                        break;
                    }
                    IsTrimmed = true;
                }
            }
            catch (Exception)
            {
                // 測れない場合はそのまま出す（writer 側で 1 行として書ける）
            }
            return IsTrimmed;
        }

        /// <summary>
        /// captureScreenshotOnError=true のときだけ、対象ウィンドウの PNG を保存する。
        /// 失敗しても Tool の結果は変わらない。
        /// </summary>
        /// <param name="InSettings">診断設定。</param>
        /// <param name="InCorrelationId">相関 ID。</param>
        /// <param name="InToolName">Tool 名。</param>
        /// <param name="InTargetWindowHandle">対象のトップレベル HWND。0 ならスキップ。</param>
        /// <returns>保存したファイルの絶対パス。保存しなかった場合は null。</returns>
        private static async Task<string> TryCaptureScreenshotAsync(DiagnosticSettings InSettings, string InCorrelationId, string InToolName, long InTargetWindowHandle)
        {
            try
            {
                if (!InSettings.IsScreenshotCapturedOnError || InTargetWindowHandle == 0)
                {
                    return null;
                }
                IntPtr TheWindow = new IntPtr(InTargetWindowHandle);
                if (!IsWindow(TheWindow) || IsIconic(TheWindow))
                {
                    return null;
                }

                using (Bitmap TheBitmap = await UiTools.CaptureWindowBitmapAsync(TheWindow))
                {
                    if (TheBitmap == null)
                    {
                        return null;
                    }
                    Directory.CreateDirectory(ScreenshotFolderPath);
                    // Tool 名が分からない経路でもファイル名の Tool 名部分を空にしない
                    string TheFileName = string.Format("{0}-{1}-{2}-{3}.png",
                        DateTime.Now.ToString("yyyyMMdd-HHmmss"),
                        DiagnosticHub.Session.SessionId,
                        InCorrelationId,
                        string.IsNullOrEmpty(InToolName) ? "unknown" : InToolName);
                    string ThePath = Path.Combine(ScreenshotFolderPath, TheFileName);
                    TheBitmap.Save(ThePath, ImageFormat.Png);
                    return ThePath;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
