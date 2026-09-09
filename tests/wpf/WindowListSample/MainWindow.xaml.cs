using System;
using System.Windows;
using System.Windows.Threading;

namespace WindowListSample
{
    /// <summary>ダイアログを 2 通り（Owner あり／なし）の ShowDialog で開くだけのメインウィンドウ。</summary>
    public partial class MainWindow : Window
    {
        /// <summary>遅延表示・自動 Close の待ち時間（ui_wait_for_window / ui_wait_for_window_closed の検証用）。</summary>
        private const int TimerDelayMs = 1000;

        /// <summary>コンストラクタ。XAML を読み込む。</summary>
        public MainWindow()
        {
            InitializeComponent();
        }

        /// <summary>Owner を明示して ShowDialog する。</summary>
        /// <param name="InSender">イベント送信元。</param>
        /// <param name="InArgs">イベント引数。</param>
        private void OnOpenOwnedDialogClick(object InSender, RoutedEventArgs InArgs)
        {
            SampleDialog TheDialog = new SampleDialog("Sample Dialog (Owned)");
            TheDialog.Owner = this;
            TheDialog.ShowDialog();
        }

        /// <summary>Owner を設定せずに ShowDialog する。</summary>
        /// <param name="InSender">イベント送信元。</param>
        /// <param name="InArgs">イベント引数。</param>
        private void OnOpenOwnerlessDialogClick(object InSender, RoutedEventArgs InArgs)
        {
            SampleDialog TheDialog = new SampleDialog("Sample Dialog (Ownerless)");
            TheDialog.ShowDialog();
        }

        /// <summary>同じタイトルの非モーダル Window を 2 つ Show() で開く（ui_capture_window_by_title の複数候補解決の検証用）。</summary>
        /// <param name="InSender">イベント送信元。</param>
        /// <param name="InArgs">イベント引数。</param>
        private void OnOpenDuplicateWindowsClick(object InSender, RoutedEventArgs InArgs)
        {
            for (int TheIndex = 0; TheIndex < 2; TheIndex++)
            {
                SampleDialog TheWindow = new SampleDialog("Duplicate Window");
                TheWindow.Show();
            }
        }

        /// <summary>TimerDelayMs 後に非モーダル Window を表示する（ui_wait_for_window の出現待機の検証用）。</summary>
        /// <param name="InSender">イベント送信元。</param>
        /// <param name="InArgs">イベント引数。</param>
        private void OnOpenDelayedWindowClick(object InSender, RoutedEventArgs InArgs)
        {
            DispatcherTimer TheTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TimerDelayMs) };
            TheTimer.Tick += (InTimerSender, InTimerArgs) =>
            {
                TheTimer.Stop();
                SampleDialog TheWindow = new SampleDialog("Delayed Window");
                TheWindow.Show();
            };
            TheTimer.Start();
        }

        /// <summary>非モーダル Window を開き、TimerDelayMs 後に自動で閉じる（ui_wait_for_window_closed の検証用）。</summary>
        /// <param name="InSender">イベント送信元。</param>
        /// <param name="InArgs">イベント引数。</param>
        private void OnOpenAutoCloseWindowClick(object InSender, RoutedEventArgs InArgs)
        {
            SampleDialog TheWindow = new SampleDialog("Auto Close Window");
            TheWindow.Show();
            DispatcherTimer TheTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TimerDelayMs) };
            TheTimer.Tick += (InTimerSender, InTimerArgs) =>
            {
                TheTimer.Stop();
                TheWindow.Close();
            };
            TheTimer.Start();
        }

        /// <summary>WPF の MessageBox.Show（内部は Win32 MessageBox）を Owner 付きで表示し、結果を画面に出す。</summary>
        /// <param name="InCaption">タイトル。</param>
        /// <param name="InButton">ボタン構成。</param>
        /// <param name="InDefault">既定ボタン。</param>
        private void ShowMessageBox(string InCaption, MessageBoxButton InButton, MessageBoxResult InDefault)
        {
            MessageBoxResult TheResult = MessageBox.Show(this, "標準ダイアログ検証用のメッセージ本文です。", InCaption, InButton, MessageBoxImage.Question, InDefault);
            LastDialogResultText.Text = $"最後の結果: MessageBox {InButton} -> {TheResult}";
        }

        /// <summary>MessageBox OK。</summary>
        /// <param name="InSender">イベント送信元。</param>
        /// <param name="InArgs">イベント引数。</param>
        private void OnShowMessageBoxOkClick(object InSender, RoutedEventArgs InArgs)
        {
            ShowMessageBox("確認 (OK)", MessageBoxButton.OK, MessageBoxResult.None);
        }

        /// <summary>MessageBox OKCancel。</summary>
        /// <param name="InSender">イベント送信元。</param>
        /// <param name="InArgs">イベント引数。</param>
        private void OnShowMessageBoxOkCancelClick(object InSender, RoutedEventArgs InArgs)
        {
            ShowMessageBox("確認 (OKCancel)", MessageBoxButton.OKCancel, MessageBoxResult.None);
        }

        /// <summary>MessageBox YesNo。</summary>
        /// <param name="InSender">イベント送信元。</param>
        /// <param name="InArgs">イベント引数。</param>
        private void OnShowMessageBoxYesNoClick(object InSender, RoutedEventArgs InArgs)
        {
            ShowMessageBox("確認 (YesNo)", MessageBoxButton.YesNo, MessageBoxResult.None);
        }

        /// <summary>MessageBox YesNoCancel。</summary>
        /// <param name="InSender">イベント送信元。</param>
        /// <param name="InArgs">イベント引数。</param>
        private void OnShowMessageBoxYesNoCancelClick(object InSender, RoutedEventArgs InArgs)
        {
            ShowMessageBox("確認 (YesNoCancel)", MessageBoxButton.YesNoCancel, MessageBoxResult.None);
        }

        /// <summary>MessageBox YesNoCancel、既定ボタン No（MB_DEFBUTTON2 相当）。</summary>
        /// <param name="InSender">イベント送信元。</param>
        /// <param name="InArgs">イベント引数。</param>
        private void OnShowMessageBoxYesNoCancelDefaultNoClick(object InSender, RoutedEventArgs InArgs)
        {
            ShowMessageBox("確認 (YesNoCancel 既定 No)", MessageBoxButton.YesNoCancel, MessageBoxResult.No);
        }

        /// <summary>MessageBox RetryCancel（WPF の MessageBoxButton には無いため Win32 MessageBoxW を直接呼ぶ）。</summary>
        /// <param name="InSender">イベント送信元。</param>
        /// <param name="InArgs">イベント引数。</param>
        private void OnShowMessageBoxRetryCancelClick(object InSender, RoutedEventArgs InArgs)
        {
            IntPtr TheOwner = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int TheResult = TaskDialogInterop.ShowMessageBoxRetryCancel(TheOwner, "標準ダイアログ検証用のメッセージ本文です。", "確認 (RetryCancel)");
            LastDialogResultText.Text = $"最後の結果: MessageBox RetryCancel -> {TheResult}";
        }

        /// <summary>TaskDialog OK / Cancel（標準ボタン）。</summary>
        /// <param name="InSender">イベント送信元。</param>
        /// <param name="InArgs">イベント引数。</param>
        private void OnShowTaskDialogOkCancelClick(object InSender, RoutedEventArgs InArgs)
        {
            IntPtr TheOwner = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int TheResult = TaskDialogInterop.ShowStandard(TheOwner, "TaskDialog (OKCancel)", "メインインストラクション", "本文コンテンツ",
                TaskDialogInterop.TDCBF_OK_BUTTON | TaskDialogInterop.TDCBF_CANCEL_BUTTON);
            LastDialogResultText.Text = $"最後の結果: TaskDialog OKCancel -> {TheResult}";
        }

        /// <summary>TaskDialog Yes / No（標準ボタン）。</summary>
        /// <param name="InSender">イベント送信元。</param>
        /// <param name="InArgs">イベント引数。</param>
        private void OnShowTaskDialogYesNoClick(object InSender, RoutedEventArgs InArgs)
        {
            IntPtr TheOwner = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int TheResult = TaskDialogInterop.ShowStandard(TheOwner, "TaskDialog (YesNo)", "メインインストラクション", "本文コンテンツ",
                TaskDialogInterop.TDCBF_YES_BUTTON | TaskDialogInterop.TDCBF_NO_BUTTON);
            LastDialogResultText.Text = $"最後の結果: TaskDialog YesNo -> {TheResult}";
        }

        /// <summary>TaskDialog Custom（TaskDialogIndirect: カスタムボタン ID / ラジオ / 検証チェック / フッター）。</summary>
        /// <param name="InSender">イベント送信元。</param>
        /// <param name="InArgs">イベント引数。</param>
        private void OnShowTaskDialogCustomClick(object InSender, RoutedEventArgs InArgs)
        {
            IntPtr TheOwner = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int TheResult = TaskDialogInterop.ShowCustom(TheOwner);
            LastDialogResultText.Text = $"最後の結果: TaskDialog Custom -> {TheResult}";
        }
    }
}
