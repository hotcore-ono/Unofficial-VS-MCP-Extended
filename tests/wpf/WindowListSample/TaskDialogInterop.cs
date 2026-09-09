using System.Runtime.InteropServices;

namespace WindowListSample
{
    /// <summary>
    /// Win32 TaskDialog / TaskDialogIndirect の最小 P/Invoke（standard_dialog_* の検証用）。
    /// comctl32 v6 が必要なため app.manifest で依存を宣言している。
    /// </summary>
    internal static class TaskDialogInterop
    {
        /// <summary>TASKDIALOG_COMMON_BUTTON_FLAGS。</summary>
        public const uint TDCBF_OK_BUTTON = 0x0001;
        public const uint TDCBF_YES_BUTTON = 0x0002;
        public const uint TDCBF_NO_BUTTON = 0x0004;
        public const uint TDCBF_CANCEL_BUTTON = 0x0008;

        /// <summary>TASKDIALOG_FLAGS。</summary>
        private const uint TDF_ALLOW_DIALOG_CANCELLATION = 0x0008;
        private const uint TDF_EXPAND_FOOTER_AREA = 0x0040;

        /// <summary>TASKDIALOG_BUTTON（Pack=1）。</summary>
        [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
        private struct TASKDIALOG_BUTTON
        {
            public int nButtonID;
            [MarshalAs(UnmanagedType.LPWStr)] public string pszButtonText;
        }

        /// <summary>TASKDIALOGCONFIG（Pack=1）。アイコンの union は IntPtr で表現する。</summary>
        [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
        private struct TASKDIALOGCONFIG
        {
            public uint cbSize;
            public IntPtr hwndParent;
            public IntPtr hInstance;
            public uint dwFlags;
            public uint dwCommonButtons;
            [MarshalAs(UnmanagedType.LPWStr)] public string pszWindowTitle;
            public IntPtr mainIcon;
            [MarshalAs(UnmanagedType.LPWStr)] public string pszMainInstruction;
            [MarshalAs(UnmanagedType.LPWStr)] public string pszContent;
            public uint cButtons;
            public IntPtr pButtons;
            public int nDefaultButton;
            public uint cRadioButtons;
            public IntPtr pRadioButtons;
            public int nDefaultRadioButton;
            [MarshalAs(UnmanagedType.LPWStr)] public string pszVerificationText;
            [MarshalAs(UnmanagedType.LPWStr)] public string pszExpandedInformation;
            [MarshalAs(UnmanagedType.LPWStr)] public string pszExpandedControlText;
            [MarshalAs(UnmanagedType.LPWStr)] public string pszCollapsedControlText;
            public IntPtr footerIcon;
            [MarshalAs(UnmanagedType.LPWStr)] public string pszFooter;
            public IntPtr pfCallback;
            public IntPtr lpCallbackData;
            public uint cxWidth;
        }

        [DllImport("comctl32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int TaskDialog(IntPtr hwndOwner, IntPtr hInstance, string pszWindowTitle, string pszMainInstruction,
            string pszContent, uint dwCommonButtons, IntPtr pszIcon, out int pnButton);

        [DllImport("comctl32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int TaskDialogIndirect(ref TASKDIALOGCONFIG pTaskConfig, out int pnButton, out int pnRadioButton, out bool pfVerificationFlagChecked);

        /// <summary>MB_RETRYCANCEL | MB_ICONQUESTION（WPF の MessageBoxButton には RetryCancel が無い）。</summary>
        private const uint MB_RETRYCANCEL_QUESTION = 0x0005 | 0x0020;

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int MessageBoxW(IntPtr hWnd, string lpText, string lpCaption, uint uType);

        /// <summary>Win32 MessageBoxW を RetryCancel 構成で表示する。</summary>
        /// <param name="InOwner">Owner の HWND。</param>
        /// <param name="InText">本文。</param>
        /// <param name="InCaption">タイトル。</param>
        /// <returns>押されたボタン ID（IDRETRY=4, IDCANCEL=2）。</returns>
        public static int ShowMessageBoxRetryCancel(IntPtr InOwner, string InText, string InCaption)
        {
            return MessageBoxW(InOwner, InText, InCaption, MB_RETRYCANCEL_QUESTION);
        }

        /// <summary>標準ボタンだけの TaskDialog を表示する。</summary>
        /// <param name="InOwner">Owner の HWND。</param>
        /// <param name="InTitle">ウィンドウタイトル。</param>
        /// <param name="InMainInstruction">メインインストラクション。</param>
        /// <param name="InContent">本文。</param>
        /// <param name="InCommonButtons">TDCBF_* の組み合わせ。</param>
        /// <returns>押されたボタン ID（IDOK=1, IDCANCEL=2, IDYES=6, IDNO=7）。</returns>
        public static int ShowStandard(IntPtr InOwner, string InTitle, string InMainInstruction, string InContent, uint InCommonButtons)
        {
            TaskDialog(InOwner, IntPtr.Zero, InTitle, InMainInstruction, InContent, InCommonButtons, IntPtr.Zero, out int TheButton);
            return TheButton;
        }

        /// <summary>
        /// カスタムボタン（ID 101 / 102）・ラジオボタン（201 / 202）・検証チェックボックス・フッターを持つ TaskDialog を
        /// TaskDialogIndirect で表示する。既定ボタンは 102。
        /// </summary>
        /// <param name="InOwner">Owner の HWND。</param>
        /// <returns>押されたボタン ID。</returns>
        public static int ShowCustom(IntPtr InOwner)
        {
            TASKDIALOG_BUTTON[] TheButtons =
            {
                new TASKDIALOG_BUTTON { nButtonID = 101, pszButtonText = "カスタム A" },
                new TASKDIALOG_BUTTON { nButtonID = 102, pszButtonText = "カスタム B" },
            };
            TASKDIALOG_BUTTON[] TheRadioButtons =
            {
                new TASKDIALOG_BUTTON { nButtonID = 201, pszButtonText = "ラジオ 1" },
                new TASKDIALOG_BUTTON { nButtonID = 202, pszButtonText = "ラジオ 2" },
            };

            int TheButtonSize = Marshal.SizeOf<TASKDIALOG_BUTTON>();
            IntPtr TheButtonsPtr = Marshal.AllocHGlobal(TheButtonSize * TheButtons.Length);
            IntPtr TheRadioPtr = Marshal.AllocHGlobal(TheButtonSize * TheRadioButtons.Length);
            try
            {
                for (int TheIndex = 0; TheIndex < TheButtons.Length; TheIndex++)
                    Marshal.StructureToPtr(TheButtons[TheIndex], TheButtonsPtr + TheButtonSize * TheIndex, false);
                for (int TheIndex = 0; TheIndex < TheRadioButtons.Length; TheIndex++)
                    Marshal.StructureToPtr(TheRadioButtons[TheIndex], TheRadioPtr + TheButtonSize * TheIndex, false);

                TASKDIALOGCONFIG TheConfig = new TASKDIALOGCONFIG
                {
                    cbSize = (uint)Marshal.SizeOf<TASKDIALOGCONFIG>(),
                    hwndParent = InOwner,
                    dwFlags = TDF_ALLOW_DIALOG_CANCELLATION | TDF_EXPAND_FOOTER_AREA,
                    dwCommonButtons = TDCBF_CANCEL_BUTTON,
                    pszWindowTitle = "TaskDialog (Custom)",
                    pszMainInstruction = "メインインストラクション",
                    pszContent = "本文コンテンツ",
                    cButtons = (uint)TheButtons.Length,
                    pButtons = TheButtonsPtr,
                    nDefaultButton = 102,
                    cRadioButtons = (uint)TheRadioButtons.Length,
                    pRadioButtons = TheRadioPtr,
                    nDefaultRadioButton = 201,
                    pszVerificationText = "次回から表示しない",
                    pszExpandedInformation = "展開情報",
                    pszFooter = "フッター",
                };
                TaskDialogIndirect(ref TheConfig, out int TheButton, out int _, out bool _);
                return TheButton;
            }
            finally
            {
                for (int TheIndex = 0; TheIndex < TheButtons.Length; TheIndex++)
                    Marshal.DestroyStructure<TASKDIALOG_BUTTON>(TheButtonsPtr + TheButtonSize * TheIndex);
                for (int TheIndex = 0; TheIndex < TheRadioButtons.Length; TheIndex++)
                    Marshal.DestroyStructure<TASKDIALOG_BUTTON>(TheRadioPtr + TheButtonSize * TheIndex);
                Marshal.FreeHGlobal(TheButtonsPtr);
                Marshal.FreeHGlobal(TheRadioPtr);
            }
        }
    }
}
