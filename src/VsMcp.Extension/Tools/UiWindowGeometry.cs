using System;
using Newtonsoft.Json;
using static VsMcp.Extension.Tools.NativeMethods;

namespace VsMcp.Extension.Tools
{
    /// <summary>
    /// Extended: ウィンドウの物理座標・DPI・モニター情報。座標はすべてスクリーンの物理ピクセルで、
    /// マルチモニターの負座標もそのまま返す（96 DPI 固定換算はしない）。取得できなかった項目は null / 0 のままにして推測しない。
    /// </summary>
    internal sealed class UiWindowGeometry
    {
        /// <summary>対象のトップレベル HWND（10 進）。</summary>
        [JsonProperty("windowHandle")]
        public long WindowHandle { get; set; }

        /// <summary>ウィンドウ矩形 "x,y,width,height"（スクリーン物理 px）。取得できない場合は null。</summary>
        [JsonProperty("windowBoundsPhysical")]
        public string WindowBoundsPhysical { get; set; }

        /// <summary>GetDpiForWindow の値。取得できない環境では 96。</summary>
        [JsonProperty("dpi")]
        public int Dpi { get; set; }

        /// <summary>モニターのデバイス名（例: \\.\DISPLAY1）。取得できない場合は null。</summary>
        [JsonProperty("monitorName")]
        public string MonitorName { get; set; }

        /// <summary>モニター全体の矩形 "x,y,width,height"（物理 px、負座標あり）。取得できない場合は null。</summary>
        [JsonProperty("monitorBounds")]
        public string MonitorBounds { get; set; }

        /// <summary>モニターの作業領域の矩形 "x,y,width,height"（物理 px）。取得できない場合は null。</summary>
        [JsonProperty("monitorWorkArea")]
        public string MonitorWorkArea { get; set; }

        /// <summary>プライマリモニター上にあるか（MONITORINFOF_PRIMARY）。判定できない場合は false。</summary>
        [JsonProperty("isPrimaryMonitor")]
        public bool IsPrimaryMonitor { get; set; }
    }

    /// <summary>
    /// Extended: <see cref="UiWindowGeometry"/> の解決器。Action の直前に毎回取り直す前提でキャッシュを持たない
    /// （ウィンドウが別 DPI のモニターへ移動しても追従するため）。
    /// </summary>
    internal static class UiWindowGeometryResolver
    {
        /// <summary>GetDpiForWindow が使えない環境（Windows 10 1607 未満）で返す既定 DPI。</summary>
        private const int _DEFAULT_DPI = 96;

        /// <summary>
        /// ウィンドウの物理矩形・DPI・モニター情報を取得する。取得できない項目は null / 0 のままにする。
        /// </summary>
        /// <param name="InWindow">対象のトップレベル HWND。</param>
        /// <returns>解決結果。</returns>
        public static UiWindowGeometry Resolve(IntPtr InWindow)
        {
            UiWindowGeometry TheGeometry = new UiWindowGeometry
            {
                WindowHandle = InWindow.ToInt64(),
                WindowBoundsPhysical = ResolveWindowBounds(InWindow),
                Dpi = ResolveDpi(InWindow),
            };

            IntPtr TheMonitor = MonitorFromWindow(InWindow, MONITOR_DEFAULTTONEAREST);
            FillMonitorInfo(TheMonitor, TheGeometry);
            return TheGeometry;
        }

        /// <summary>GetWindowRect を Per-Monitor DPI Awareness V2 の文脈で呼び、物理 px の矩形文字列を作る。</summary>
        /// <param name="InWindow">対象のトップレベル HWND。</param>
        /// <returns>"x,y,width,height"。取得できない場合は null。</returns>
        private static string ResolveWindowBounds(IntPtr InWindow)
        {
            return UiTools.WithDpiAwareness(() =>
            {
                if (!GetWindowRect(InWindow, out RECT TheRect))
                {
                    return (string)null;
                }
                return FormatRect(TheRect);
            });
        }

        /// <summary>GetDpiForWindow でウィンドウの DPI を返す。API が無い環境や 0 が返る場合は 96 にフォールバックする。</summary>
        /// <param name="InWindow">対象のトップレベル HWND。</param>
        /// <returns>DPI 値（96 / 120 / 144 / 192 など）。</returns>
        private static int ResolveDpi(IntPtr InWindow)
        {
            try
            {
                uint TheDpi = GetDpiForWindow(InWindow);
                return TheDpi == 0 ? _DEFAULT_DPI : (int)TheDpi;
            }
            catch (EntryPointNotFoundException)
            {
                return _DEFAULT_DPI;
            }
        }

        /// <summary>モニターハンドルから名前・矩形・作業領域・プライマリ判定を埋める。取得できない場合は何も設定しない。</summary>
        /// <param name="InMonitor">MonitorFrom* が返したモニターハンドル。</param>
        /// <param name="InOutGeometry">結果を書き込む Geometry。</param>
        private static void FillMonitorInfo(IntPtr InMonitor, UiWindowGeometry InOutGeometry)
        {
            if (InMonitor == IntPtr.Zero)
            {
                return;
            }

            MONITORINFOEX TheInfo = new MONITORINFOEX();
            TheInfo.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(MONITORINFOEX));
            if (!GetMonitorInfoW(InMonitor, ref TheInfo))
            {
                return;
            }

            InOutGeometry.MonitorName = string.IsNullOrEmpty(TheInfo.szDevice) ? null : TheInfo.szDevice;
            InOutGeometry.MonitorBounds = FormatRect(TheInfo.rcMonitor);
            InOutGeometry.MonitorWorkArea = FormatRect(TheInfo.rcWork);
            InOutGeometry.IsPrimaryMonitor = (TheInfo.dwFlags & MONITORINFOF_PRIMARY) != 0;
        }

        /// <summary>RECT を "x,y,width,height" 形式にする（負座標はそのまま）。</summary>
        /// <param name="InRect">対象の矩形。</param>
        /// <returns>矩形文字列。</returns>
        private static string FormatRect(in RECT InRect)
        {
            return $"{InRect.Left},{InRect.Top},{InRect.Right - InRect.Left},{InRect.Bottom - InRect.Top}";
        }
    }
}
