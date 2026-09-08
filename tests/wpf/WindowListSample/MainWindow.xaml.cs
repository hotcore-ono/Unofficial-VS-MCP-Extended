using System.Windows;

namespace WindowListSample
{
    /// <summary>ダイアログを 2 通り（Owner あり／なし）の ShowDialog で開くだけのメインウィンドウ。</summary>
    public partial class MainWindow : Window
    {
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
    }
}