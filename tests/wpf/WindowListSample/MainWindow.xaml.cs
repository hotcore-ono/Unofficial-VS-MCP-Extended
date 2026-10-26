using System;
using System.Collections.Generic;
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

        /// <summary>OpenManyWindowsButton で開く Window の数（20 件を超えるウィンドウ列挙・打ち切りの検証用）。</summary>
        private const int _MANY_WINDOW_COUNT = 22;

        /// <summary>OpenManyWindowsButton で開く Window の幅。</summary>
        private const int _MANY_WINDOW_WIDTH = 200;

        /// <summary>OpenManyWindowsButton で開く Window の高さ。</summary>
        private const int _MANY_WINDOW_HEIGHT = 100;

        /// <summary>ScrollItemTestListBox に生成する ListBoxItem の数（Height 80 の枠に収まらず、大半が IsOffscreen になる数）。</summary>
        private const int _LIST_SCROLL_ITEM_COUNT = 40;

        /// <summary>ScrollItemTestDataGrid に生成する行の数（Height 80 の枠に収まらず、大半が IsOffscreen になる数）。</summary>
        private const int _GRID_SCROLL_ROW_COUNT = 40;

        /// <summary>OpenManyWindowsButton で開く Window の 1 つ目をメインウィンドウ左上からずらす量。</summary>
        private const int _MANY_WINDOW_OFFSET_BASE = 40;

        /// <summary>OpenManyWindowsButton で開く Window を 1 つずつ重ならないようにずらす量。</summary>
        private const int _MANY_WINDOW_OFFSET_STEP = 20;

        /// <summary>OpenOffscreenTwinButton で開く Window の左上座標（X・Y 共通）。どのモニターにも属さない退避位置で、実運用ログの画面外に退避された WPF 窓と同じ値。</summary>
        private const int _OFFSCREEN_TWIN_POSITION = -32768;

        /// <summary>OpenOffscreenTwinButton で開く Window の幅。</summary>
        private const int _OFFSCREEN_TWIN_WIDTH = 300;

        /// <summary>OpenOffscreenTwinButton で開く Window の高さ。</summary>
        private const int _OFFSCREEN_TWIN_HEIGHT = 120;

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

        /// <summary>DisabledTestButton の Click が発火した回数（無効要素への操作が拒否される限り 0 のまま）。</summary>
        private int _DisabledClickCount;

        /// <summary>FaultyPeerButton の Click が発火した回数（Invoke 成功時と物理クリック時に増える。EnableFaultyPeerCheckBox が ON で Invoke が失敗したときは増えない）。</summary>
        private int _FaultyPeerClickCount;

        /// <summary>MaskSampleButton がクリックされた回数。</summary>
        private int _MaskSampleClickCount;

        /// <summary>OpenManyWindowsButton で開いた Window の一覧（CloseManyWindowsButton で全て閉じる）。</summary>
        private readonly List<SampleDialog> _ManyWindows = new List<SampleDialog>();

        /// <summary>コンストラクタ。XAML を読み込む。</summary>
        public MainWindow()
        {
            InitializeComponent();
            CreateScrollItems();
            CreateListScrollItems();
            CreateGridScrollRows();
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

        /// <summary>コンテキストメニューのサブメニュー内 Sub Item 1 が選択されたことを表示する（サブメニュー展開の検証用）。</summary>
        /// <param name="InSender">イベント送信元。</param>
        /// <param name="InArgs">イベント引数。</param>
        private void OnContextMenuSubItem1Click(object InSender, RoutedEventArgs InArgs)
        {
            ContextMenuCountText.Text = $"ContextMenu: opened {_ContextMenuOpenCount}, selected Sub Item 1";
        }

        /// <summary>コンテキストメニューのサブメニュー内 Sub Item 2 が選択されたことを表示する（サブメニュー展開の検証用）。</summary>
        /// <param name="InSender">イベント送信元。</param>
        /// <param name="InArgs">イベント引数。</param>
        private void OnContextMenuSubItem2Click(object InSender, RoutedEventArgs InArgs)
        {
            ContextMenuCountText.Text = $"ContextMenu: opened {_ContextMenuOpenCount}, selected Sub Item 2";
        }

        /// <summary>
        /// 無効項目が誤って起動された場合にそれを検出できるようにする（IsEnabled=False のため通常は呼ばれない。
        /// 無効項目の選択が拒否されることを ContextMenuCountText の不変で確認するための検出器）。
        /// </summary>
        /// <param name="InSender">イベント送信元。</param>
        /// <param name="InArgs">イベント引数。</param>
        private void OnContextMenuDisabledItemClick(object InSender, RoutedEventArgs InArgs)
        {
            ContextMenuCountText.Text = $"ContextMenu: opened {_ContextMenuOpenCount}, selected Disabled Item";
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

        /// <summary>
        /// Win32MenuTestArea の右クリックでカーソル位置に Win32 ポップアップメニュー（ClassName "#32768"）を開く。
        /// TrackPopupMenuEx はメニューが閉じるまで UI スレッドでモーダルループを回すため、戻り値が返ってから結果を表示する。
        /// </summary>
        /// <param name="InSender">イベント送信元。</param>
        /// <param name="InArgs">マウスボタンイベント引数。</param>
        private void OnWin32MenuTestAreaMouseRightButtonUp(object InSender, MouseButtonEventArgs InArgs)
        {
            InArgs.Handled = true;
            Point TheScreenPoint = PointToScreen(InArgs.GetPosition(this));
            ShowWin32PopupMenu(TheScreenPoint);
        }

        /// <summary>ShowWin32MenuButton のクリックでボタン直下に同じ Win32 ポップアップメニューを開く（右クリック不要の経路）。</summary>
        /// <param name="InSender">イベント送信元。</param>
        /// <param name="InArgs">イベント引数。</param>
        private void OnShowWin32MenuButtonClick(object InSender, RoutedEventArgs InArgs)
        {
            Point TheScreenPoint = ShowWin32MenuButton.PointToScreen(new Point(0.0, ShowWin32MenuButton.ActualHeight));
            ShowWin32PopupMenu(TheScreenPoint);
        }

        /// <summary>
        /// Win32 ポップアップメニューを表示し、選択結果を Win32MenuResultText に表示する。
        /// 座標はスクリーン物理 px でなければならないが、Visual.PointToScreen は内部で
        /// CompositionTarget.TransformToDevice を適用済みのため、DPI 換算を重ねて行わない。
        /// </summary>
        /// <param name="InScreenPoint">メニュー左上のスクリーン物理座標。</param>
        private void ShowWin32PopupMenu(in Point InScreenPoint)
        {
            IntPtr TheOwner = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int TheCommandId = Win32MenuInterop.ShowPopupMenu(TheOwner, (int)InScreenPoint.X, (int)InScreenPoint.Y);
            if (TheCommandId == 0)
            {
                Win32MenuResultText.Text = "Win32Menu: cancelled";
            }
            else
            {
                Win32MenuResultText.Text = $"Win32Menu: selected {TheCommandId}";
            }
        }

        /// <summary>
        /// DisabledTestButton のクリック回数を数えて表示する。
        /// ボタンは IsEnabled=False のため通常は呼ばれない（無効要素への操作が拒否されることを
        /// DisabledClickCountText が "DisabledClick: 0" のままであることで確認するための検出器）。
        /// </summary>
        /// <param name="InSender">イベント送信元。</param>
        /// <param name="InArgs">イベント引数。</param>
        private void OnDisabledTestClick(object InSender, RoutedEventArgs InArgs)
        {
            _DisabledClickCount++;
            DisabledClickCountText.Text = $"DisabledClick: {_DisabledClickCount}";
        }

        /// <summary>
        /// _MANY_WINDOW_COUNT 個の非モーダル Window を位置をずらしながら開く（20 件を超えるウィンドウ列挙・打ち切りの検証用）。
        /// タスクバーを埋めないように ShowInTaskbar=false とし、Owner をメインウィンドウにして親子関係を明確にする。
        /// </summary>
        /// <param name="InSender">イベント送信元。</param>
        /// <param name="InArgs">イベント引数。</param>
        private void OnOpenManyWindowsClick(object InSender, RoutedEventArgs InArgs)
        {
            for (int TheIndex = 1; TheIndex <= _MANY_WINDOW_COUNT; TheIndex++)
            {
                double TheOffset = _MANY_WINDOW_OFFSET_BASE + ((TheIndex - 1) * _MANY_WINDOW_OFFSET_STEP);
                SampleDialog TheWindow = new SampleDialog($"Many Window {TheIndex}");
                TheWindow.Owner = this;
                TheWindow.ShowInTaskbar = false;
                TheWindow.WindowStartupLocation = WindowStartupLocation.Manual;
                TheWindow.Width = _MANY_WINDOW_WIDTH;
                TheWindow.Height = _MANY_WINDOW_HEIGHT;
                TheWindow.Left = Left + TheOffset;
                TheWindow.Top = Top + TheOffset;
                TheWindow.Closed += OnManyWindowClosed;
                _ManyWindows.Add(TheWindow);
                TheWindow.Show();
            }
            UpdateManyWindowsCountText();
        }

        /// <summary>OpenManyWindowsButton で開いた Window をすべて閉じる（列挙結果が元に戻ることの確認用）。</summary>
        /// <param name="InSender">イベント送信元。</param>
        /// <param name="InArgs">イベント引数。</param>
        private void OnCloseManyWindowsClick(object InSender, RoutedEventArgs InArgs)
        {
            // Close の中で OnManyWindowClosed が _ManyWindows を変更するため、複製を走査する
            foreach (SampleDialog TheWindow in _ManyWindows.ToArray())
            {
                TheWindow.Close();
            }
            UpdateManyWindowsCountText();
        }

        /// <summary>OpenManyWindowsButton で開いた Window が閉じられたときに一覧から取り除いて表示を更新する。</summary>
        /// <param name="InSender">閉じられた Window。</param>
        /// <param name="InArgs">イベント引数。</param>
        private void OnManyWindowClosed(object? InSender, EventArgs InArgs)
        {
            if (InSender is SampleDialog TheWindow)
            {
                TheWindow.Closed -= OnManyWindowClosed;
                _ManyWindows.Remove(TheWindow);
            }
            UpdateManyWindowsCountText();
        }

        /// <summary>OpenManyWindowsButton で開いている Window の数を ManyWindowsCountText に反映する。</summary>
        private void UpdateManyWindowsCountText()
        {
            ManyWindowsCountText.Text = $"ManyWindows: {_ManyWindows.Count}";
        }

        /// <summary>
        /// FaultyPeerButton の Click。EnableFaultyPeerCheckBox が OFF のとき UIA の Invoke / 物理クリックが通ったことを結果表示欄へ書く
        /// （ON のときは Invoke が例外を投げるため呼ばれない。物理クリックなら通常どおり呼ばれる）。
        /// </summary>
        /// <param name="InSender">イベント送信元。</param>
        /// <param name="InArgs">イベント引数。</param>
        private void OnFaultyPeerButtonClick(object InSender, RoutedEventArgs InArgs)
        {
            _FaultyPeerClickCount++;
            LastDialogResultText.Text = $"最後の結果: FaultyPeerButton -> clicked {_FaultyPeerClickCount}";
        }

        /// <summary>
        /// MaskSampleButton の Click。Name が "token=visible"（機密マスクの対象語を含む）のボタンが成功経路で押されたことを結果表示欄へ書く。
        /// Tool の応答（成功メッセージ・要素情報の name）は伏せられず、そのまま返る。診断ログは includeUiText が既定の false なら
        /// 引数 name は長さだけが残り、true なら引数要約の name が token=*** になる（診断ログ側の伏せ方の確認用）。
        /// </summary>
        /// <param name="InSender">イベント送信元。</param>
        /// <param name="InArgs">イベント引数。</param>
        private void OnMaskSampleClick(object InSender, RoutedEventArgs InArgs)
        {
            _MaskSampleClickCount++;
            LastDialogResultText.Text = $"最後の結果: MaskSampleButton -> clicked {_MaskSampleClickCount}";
        }

        /// <summary>
        /// OpenOffscreenTwinButton の Click。メインウィンドウと同じタイトルの Window を画面外（Left = Top = -32768）に開く
        /// （ui_wait_for_window / ui_capture_window_by_title が、同名候補のうち画面外の窓を除いて画面内の 1 件へ解決することの確認用）。
        /// メインウィンドウの手前に出ないよう Manual 配置・非アクティブで表示する。画面外の窓なので閉じる操作は
        /// CloseOffscreenTwinButton への ui_window_click（InvokePattern）で行う。メインウィンドウが閉じたときは一緒に閉じる（画面外の窓でプロセスが残らないように）。
        /// 開いた窓の HWND（10 進）を結果表示欄へ書く。
        /// </summary>
        /// <param name="InSender">イベント送信元。</param>
        /// <param name="InArgs">イベント引数。</param>
        private void OnOpenOffscreenTwinClick(object InSender, RoutedEventArgs InArgs)
        {
            string TheMainTitle = Title;
            Button TheCloseButton = new Button
            {
                Content = "Offscreen Twin を閉じる",
                Width = 200,
                Height = 32,
            };
            AutomationProperties.SetAutomationId(TheCloseButton, "CloseOffscreenTwinButton");
            Window TheTwin = new Window
            {
                Title = TheMainTitle,
                Width = _OFFSCREEN_TWIN_WIDTH,
                Height = _OFFSCREEN_TWIN_HEIGHT,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = _OFFSCREEN_TWIN_POSITION,
                Top = _OFFSCREEN_TWIN_POSITION,
                ShowActivated = false,
                Content = TheCloseButton,
            };
            TheCloseButton.Click += (InCloseSender, InCloseArgs) => TheTwin.Close();

            // メインウィンドウが先に閉じられたときは Twin も閉じる（自分で閉じた後は購読を外す）
            EventHandler TheMainClosedHandler = (InMainSender, InMainArgs) => TheTwin.Close();
            Closed += TheMainClosedHandler;
            TheTwin.Closed += (InTwinSender, InTwinArgs) => Closed -= TheMainClosedHandler;

            TheTwin.Show();
            long TheHandle = new System.Windows.Interop.WindowInteropHelper(TheTwin).Handle.ToInt64();
            LastDialogResultText.Text = $"最後の結果: OffscreenTwin -> opened {TheHandle}";
        }

        /// <summary>
        /// ExpandCollapseTestComboBox の選択が変わったときに、選ばれた項目を結果表示欄へ書く
        /// （ui_window_click が ExpandCollapsePattern で開いた後に項目を選べること、閉じていても選択結果が読めることの確認用）。
        /// </summary>
        /// <param name="InSender">イベント送信元（ExpandCollapseTestComboBox）。</param>
        /// <param name="InArgs">イベント引数。</param>
        private void OnExpandCollapseTestComboBoxSelectionChanged(object InSender, SelectionChangedEventArgs InArgs)
        {
            if (InSender is ComboBox TheComboBox && TheComboBox.SelectedItem is ComboBoxItem TheItem)
            {
                LastDialogResultText.Text = $"最後の結果: ExpandCollapseTestComboBox -> {TheItem.Content}";
            }
        }

        /// <summary>
        /// スクロール検証用の ListBoxItem（ListScrollItem01〜ListScrollItem40）を ScrollItemTestListBox に生成する。
        /// 仮想化を無効にしているため全項目が UIA ツリーに現れ、Height 80 の枠に収まらない項目は IsOffscreen=true になる
        /// （TextBlock の ScrollItemNN と違い、ListBoxItem は ScrollItemPattern と SelectionItemPattern を持つ）。
        /// </summary>
        private void CreateListScrollItems()
        {
            for (int TheIndex = 1; TheIndex <= _LIST_SCROLL_ITEM_COUNT; TheIndex++)
            {
                string TheItemNumber = TheIndex.ToString("00");
                ListBoxItem TheItem = new ListBoxItem { Content = $"List Item {TheItemNumber}" };
                AutomationProperties.SetAutomationId(TheItem, $"ListScrollItem{TheItemNumber}");
                ScrollItemTestListBox.Items.Add(TheItem);
            }
        }

        /// <summary>
        /// ScrollItemTestDataGrid に行（GridScrollRow01〜GridScrollRow40）を生成する。行の仮想化を無効にしているため全行が UIA ツリーに現れ、
        /// Height 80 の枠に収まらない行のセルは IsOffscreen=true になる。ScrollItemPattern だけを持つセルに対する
        /// ui_window_click（ScrollIntoView → 物理クリック）の確認用。セルが実際にどのパターンを持つかは UIA の patterns で確認する。
        /// </summary>
        private void CreateGridScrollRows()
        {
            List<GridScrollRow> TheRows = new List<GridScrollRow>();
            for (int TheIndex = 1; TheIndex <= _GRID_SCROLL_ROW_COUNT; TheIndex++)
            {
                TheRows.Add(new GridScrollRow($"GridScrollRow{TheIndex.ToString("00")}"));
            }
            ScrollItemTestDataGrid.ItemsSource = TheRows;
        }

        /// <summary>ScrollItemTestDataGrid の行選択が変わったときに、選ばれた行の Name を結果表示欄へ書く。</summary>
        /// <param name="InSender">イベント送信元（ScrollItemTestDataGrid）。</param>
        /// <param name="InArgs">イベント引数。</param>
        private void OnScrollItemTestDataGridSelectionChanged(object InSender, SelectionChangedEventArgs InArgs)
        {
            if (ScrollItemTestDataGrid.SelectedItem is GridScrollRow TheRow)
            {
                LastDialogResultText.Text = $"最後の結果: ScrollItemTestDataGrid -> {TheRow.Name}";
            }
        }

        /// <summary>ScrollItemTestListBox の選択が変わったときに、選ばれた項目の AutomationId を結果表示欄へ書く。</summary>
        /// <param name="InSender">イベント送信元（ScrollItemTestListBox）。</param>
        /// <param name="InArgs">イベント引数。</param>
        private void OnScrollItemTestListBoxSelectionChanged(object InSender, SelectionChangedEventArgs InArgs)
        {
            if (ScrollItemTestListBox.SelectedItem is ListBoxItem TheItem)
            {
                LastDialogResultText.Text = $"最後の結果: ScrollItemTestListBox -> {AutomationProperties.GetAutomationId(TheItem)}";
            }
        }
    }

    /// <summary>ScrollItemTestDataGrid の 1 行分のデータ（読み取り専用の Name だけを持つ）。</summary>
    public sealed class GridScrollRow
    {
        /// <summary>行の名前（GridScrollRow01〜GridScrollRow40）。</summary>
        public string Name { get; }

        /// <summary>コンストラクタ。</summary>
        /// <param name="InName">行の名前。</param>
        public GridScrollRow(string InName)
        {
            Name = InName;
        }
    }
}
