using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace WindowListSample
{
    /// <summary>
    /// ui_capture_window_by_handle の「子 HWND → トップレベル正規化」検証用に、Win32 の STATIC コントロールを
    /// 子ウィンドウとして生成するホスト。WPF は通常ボタン等に個別の HWND を持たないため、意図的に子 HWND を作る。
    /// </summary>
    public sealed class ChildWindowHost : HwndHost
    {
        private const int WS_CHILD = 0x40000000;
        private const int WS_VISIBLE = 0x10000000;
        private const int SS_CENTER = 0x00000001;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateWindowExW(int InExStyle, string InClassName, string InWindowName, int InStyle,
            int InX, int InY, int InWidth, int InHeight, IntPtr InParent, IntPtr InMenu, IntPtr InInstance, IntPtr InParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyWindow(IntPtr InHandle);

        /// <summary>親 HWND の子として STATIC コントロールを生成する。</summary>
        /// <param name="InParent">WPF が用意する親 HWND。</param>
        /// <returns>生成した子ウィンドウ。</returns>
        protected override HandleRef BuildWindowCore(HandleRef InParent)
        {
            IntPtr TheHandle = CreateWindowExW(0, "STATIC", "Win32 child (STATIC)", WS_CHILD | WS_VISIBLE | SS_CENTER,
                0, 0, 280, 24, InParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            return new HandleRef(this, TheHandle);
        }

        /// <summary>子ウィンドウを破棄する。</summary>
        /// <param name="InChild">生成した子ウィンドウ。</param>
        protected override void DestroyWindowCore(HandleRef InChild)
        {
            DestroyWindow(InChild.Handle);
        }
    }
}