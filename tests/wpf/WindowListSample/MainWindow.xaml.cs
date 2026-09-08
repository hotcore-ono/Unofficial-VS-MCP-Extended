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
    }
}