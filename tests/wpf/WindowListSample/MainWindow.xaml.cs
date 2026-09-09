using System;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace WindowListSample
{
    /// <summary>ダイアログを 2 通り（Owner あり／なし）の ShowDialog で開くだけのメインウィンドウ。</summary>
    public partial class MainWindow : Window
    {
        /// <summary>遅延表示・自動 Close の待ち時間（ui_wait_for_window / ui_wait_for_window_closed の検証用）。</summary>
        private const int TimerDelayMs = 1000;

        /// <summary>Busy シミュレーションの 1 ステップあたりの待ち時間（ミリ秒）。</summary>
        private const int _BUSY_INTERVAL_MS = 300;

        /// <summary>Busy シミュレーションのステップ数。</summary>
        private const int _BUSY_STEP_COUNT = 5;

        /// <summary>スクロール検証用に生成する項目数。</summary>
        private const int _SCROLL_ITEM_COUNT = 40;

        /// <summary>ClickTestButton がクリックされた回数。</summary>
        private int _ClickCount;

        /// <summary>DoubleClickTestArea で単クリックされた回数（ダブルクリックの 1 打目も含む）。</summary>
        private int _SingleClickCount;

        /// <summary>DoubleClickTestArea でダブルクリックされた回数。</summary>
        private int _DoubleClickCount;

        /// <summary>RightClickTestArea のコンテキストメニューが開かれた回数。</summary>
        private int _ContextMenuOpenCount;

        /// <summary>DragTarget の矩形内でマウスボタンが離された回数。</summary>
        private int _DropCount;

        /// <summary>DragSource 上で左ボタンが押され、ドラッグ追跡中であるかどうか。</summary>
        private bool _IsDragArmed;

        /// <summary>コンストラクタ。XAML を読み込む。</summary>
        public MainWindow()
        {
            InitializeComponent();
            CreateScrollItems();
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

        /// <summary>File Dialog 検証用の fixture（%TEMP%\VsMcpExtendedFileDialogTest\alpha.txt, beta.txt, SampleFolder\）を用意してそのパスを返す。</summary>
        /// <returns>fixture フォルダーの絶対パス。</returns>
        private static string EnsureFileDialogFixture()
        {
            string TheRoot = Path.Combine(Path.GetTempPath(), "VsMcpExtendedFileDialogTest");
            Directory.CreateDirectory(Path.Combine(TheRoot, "SampleFolder"));
            foreach (string TheName in new[] { "alpha.txt", "beta.txt" })
            {
                string ThePath = Path.Combine(TheRoot, TheName);
                if (!File.Exists(ThePath))
                    File.WriteAllText(ThePath, TheName);
            }
            return TheRoot;
        }

        /// <summary>Microsoft.Win32.OpenFileDialog（IFileOpenDialog）を fixture フォルダーで開く。</summary>
        /// <param name="InSender">イベント送信元。</param>
        /// <param name="InArgs">イベント引数。</param>
        private void OnShowOpenFileDialogClick(object InSender, RoutedEventArgs InArgs)
        {
            Microsoft.Win32.OpenFileDialog TheDialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Open File (Test)",
                InitialDirectory = EnsureFileDialogFixture(),
                Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            };
            bool? TheResult = TheDialog.ShowDialog(this);
            LastDialogResultText.Text = $"最後の結果: OpenFileDialog -> {TheResult} {TheDialog.FileName}";
        }

        /// <summary>Microsoft.Win32.SaveFileDialog（IFileSaveDialog）を fixture フォルダーで開く。</summary>
        /// <param name="InSender">イベント送信元。</param>
        /// <param name="InArgs">イベント引数。</param>
        private void OnShowSaveFileDialogClick(object InSender, RoutedEventArgs InArgs)
        {
            Microsoft.Win32.SaveFileDialog TheDialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save File (Test)",
                InitialDirectory = EnsureFileDialogFixture(),
                Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            };
            bool? TheResult = TheDialog.ShowDialog(this);
            LastDialogResultText.Text = $"最後の結果: SaveFileDialog -> {TheResult} {TheDialog.FileName}";
        }

        /// <summary>Microsoft.Win32.OpenFolderDialog（IFileOpenDialog + FOS_PICKFOLDERS）を fixture フォルダーで開く。</summary>
        /// <param name="InSender">イベント送信元。</param>
        /// <param name="InArgs">イベント引数。</param>
        private void OnShowOpenFolderDialogClick(object InSender, RoutedEventArgs InArgs)
        {
            Microsoft.Win32.OpenFolderDialog TheDialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Open Folder (Test)",
                InitialDirectory = EnsureFileDialogFixture(),
            };
            bool? TheResult = TheDialog.ShowDialog(this);
            LastDialogResultText.Text = $"最後の結果: OpenFolderDialog -> {TheResult} {TheDialog.FolderName}";
        }

        /// <summary>スクロール検証用の項目（Item 01〜Item 40）を ScrollItemsPanel に生成する。</summary>
        private void CreateScrollItems()
        {
            for (int TheIndex = 1; TheIndex <= _SCROLL_ITEM_COUNT; TheIndex++)
            {
                string TheItemNumber = TheIndex.ToString("00");
                TextBlock TheItem = new TextBlock { Text = $"Item {TheItemNumber}" };
                AutomationProperties.SetAutomationId(TheItem, $"ScrollItem{TheItemNumber}");
                ScrollItemsPanel.Children.Add(TheItem);
            }
        }

        /// <summary>ClickTestButton のクリック回数を数えて表示する（単一クリック操作の検証用）。</summary>
        /// <param name="InSender">イベント送信元。</param>
        /// <param name="InArgs">イベント引数。</param>
        private void OnClickTestClick(object InSender, RoutedEventArgs InArgs)
        {
            _ClickCount++;
            ClickCountText.Text = $"Click: {_ClickCount}";
        }

        /// <summary>DoubleClickTestArea の単クリック回数を数える（ダブルクリックの 1 打目のみ数えるため ClickCount が 1 のときに限定する）。</summary>
        /// <param name="InSender">イベント送信元。</param>
        /// <param name="InArgs">マウスボタンイベント引数。</param>
        private void OnDoubleClickAreaPreviewMouseLeftButtonDown(object InSender, MouseButtonEventArgs InArgs)
        {
            if (InArgs.ClickCount == 1)
            {
                _SingleClickCount++;
                UpdateDoubleClickCountText();
            }
        }

        /// <summary>DoubleClickTestArea のダブルクリック回数を数える（ダブルクリック操作の検証用）。</summary>
        /// <param name="InSender">イベント送信元。</param>
        /// <param name="InArgs">マウスボタンイベント引数。</param>
        private void OnDoubleClickAreaMouseDoubleClick(object InSender, MouseButtonEventArgs InArgs)
        {
            _DoubleClickCount++;
            UpdateDoubleClickCountText();
        }

        /// <summary>ダブルクリック回数と単クリック回数の表示を更新する。</summary>
        private void UpdateDoubleClickCountText()
        {
            DoubleClickCountText.Text = $"DoubleClick: {_DoubleClickCount} / SingleClick: {_SingleClickCount}";
        }

        /// <summary>RightClickTestArea のコンテキストメニューが開かれた回数を数える（右クリック操作の検証用）。</summary>
        /// <param name="InSender">イベント送信元。</param>
        /// <param name="InArgs">コンテキストメニューイベント引数。</param>
        private void OnRightClickAreaContextMenuOpening(object InSender, ContextMenuEventArgs InArgs)
        {
            _ContextMenuOpenCount++;
            ContextMenuCountText.Text = $"ContextMenu: opened {_ContextMenuOpenCount}";
        }

        /// <summary>コンテキストメニューの Context Item A が選択されたことを表示する。</summary>
        /// <param name="InSender">イベント送信元。</param>
        /// <param name="InArgs">イベント引数。</param>
        private void OnContextMenuItemAClick(object InSender, RoutedEventArgs InArgs)
        {
            ContextMenuCountText.Text = $"ContextMenu: opened {_ContextMenuOpenCount}, selected A";
        }

        /// <summary>コンテキストメニューの Context Item B が選択されたことを表示する。</summary>
        /// <param name="InSender">イベント送信元。</param>
        /// <param name="InArgs">イベント引数。</param>
        private void OnContextMenuItemBClick(object InSender, RoutedEventArgs InArgs)
        {
            ContextMenuCountText.Text = $"ContextMenu: opened {_ContextMenuOpenCount}, selected B";
        }

        /// <summary>DragSource 上での左ボタン押下でドラッグ追跡を開始し、マウスをキャプチャする（OLE の DoDragDrop は使わず手動で追跡する）。</summary>
        /// <param name="InSender">イベント送信元。</param>
        /// <param name="InArgs">マウスボタンイベント引数。</param>
        private void OnDragSourcePreviewMouseLeftButtonDown(object InSender, MouseButtonEventArgs InArgs)
        {
            _IsDragArmed = true;
            Mouse.Capture(DragSource);
        }

        /// <summary>マウスキャプチャを解除し、離した位置が DragTarget の矩形内かどうかで結果を表示する（ドラッグ操作の検証用）。</summary>
        /// <param name="InSender">イベント送信元。</param>
        /// <param name="InArgs">マウスボタンイベント引数。</param>
        private void OnDragSourcePreviewMouseLeftButtonUp(object InSender, MouseButtonEventArgs InArgs)
        {
            if (!_IsDragArmed)
            {
                return;
            }
            _IsDragArmed = false;
            Mouse.Capture(null);
            Point TheReleasePosition = InArgs.GetPosition(DragTarget);
            bool IsInsideTarget = TheReleasePosition.X >= 0.0
                && TheReleasePosition.Y >= 0.0
                && TheReleasePosition.X <= DragTarget.ActualWidth
                && TheReleasePosition.Y <= DragTarget.ActualHeight;
            if (IsInsideTarget)
            {
                _DropCount++;
                DragResultText.Text = $"Drag: dropped on target ({_DropCount})";
            }
            else
            {
                DragResultText.Text = "Drag: released outside target";
            }
        }

        /// <summary>ScrollableArea の垂直スクロール位置を整数で表示する（スクロール操作の検証用）。</summary>
        /// <param name="InSender">イベント送信元。</param>
        /// <param name="InArgs">スクロール変更イベント引数。</param>
        private void OnScrollableAreaScrollChanged(object InSender, ScrollChangedEventArgs InArgs)
        {
            ScrollOffsetText.Text = $"Scroll: VerticalOffset={(int)InArgs.VerticalOffset}";
        }

        /// <summary>_BUSY_INTERVAL_MS 間隔で _BUSY_STEP_COUNT 回だけ項目を追加する（処理中の状態変化・待機の検証用）。</summary>
        /// <param name="InSender">イベント送信元。</param>
        /// <param name="InArgs">イベント引数。</param>
        private void OnBusyStartClick(object InSender, RoutedEventArgs InArgs)
        {
            BusyItemsPanel.Children.Clear();
            int TheStepIndex = 0;
            BusyStatusText.Text = $"Busy: running {TheStepIndex}/{_BUSY_STEP_COUNT}";
            DispatcherTimer TheTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(_BUSY_INTERVAL_MS) };
            TheTimer.Tick += (InTimerSender, InTimerArgs) =>
            {
                TheStepIndex++;
                BusyItemsPanel.Children.Add(new TextBlock { Text = $"Busy item {TheStepIndex}" });
                if (TheStepIndex >= _BUSY_STEP_COUNT)
                {
                    TheTimer.Stop();
                    BusyStatusText.Text = "Busy: done";
                }
                else
                {
                    BusyStatusText.Text = $"Busy: running {TheStepIndex}/{_BUSY_STEP_COUNT}";
                }
            };
            TheTimer.Start();
        }
    }
}
