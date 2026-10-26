# WindowListSample — Extended UI ツール回帰確認用 WPF サンプル

`Unofficial-VS-MCP-Extended` の Extended 独自ツール（`ui_list_windows` ほか）を実機で回帰確認するための最小 WPF アプリ。
本体ソリューション `Unoffcial-VS-MCP.sln` には含めず、独立した `WindowListSample.slnx` として扱う。

## 画面

- `MainWindow`（Title: `WindowListSample Main`）
  - `OpenOwnedDialogButton`: `Owner = this` を設定して `ShowDialog()`
  - `OpenOwnerlessDialogButton`: Owner を設定せずに `ShowDialog()`
- `SampleDialog`（Title: `Sample Dialog (Owned)` / `Sample Dialog (Ownerless)`）
  - `CloseDialogButton`: `DialogResult = true` で閉じる

ボタンには `AutomationProperties.AutomationId` を付けてあり、`ui_click` の `automationId` で操作できる。

## ui_window_* Action ツール検証用 UI（MainWindow 3 列目）

| 操作 | 操作対象 AutomationId | 結果表示 AutomationId | 期待する表示 |
|---|---|---|---|
| クリック | `ClickTestButton` | `ClickCountText` | `Click: N`（初期 `Click: 0`） |
| ダブルクリック | `DoubleClickTestArea` | `DoubleClickCountText` | `DoubleClick: N / SingleClick: M`（ダブルクリック 1 回で N=1, M=1） |
| 右クリック | `RightClickTestArea` → `ContextMenuItemA` / `ContextMenuItemB` | `ContextMenuCountText` | `ContextMenu: opened N` → 選択後 `ContextMenu: opened N, selected A`（または B） |
| ドラッグ | `DragSource` → `DragTarget` | `DragResultText` | 矩形内で離すと `Drag: dropped on target (N)`、矩形外なら `Drag: released outside target` |
| スクロール | `ScrollableArea`（`ScrollItem01`〜`ScrollItem40`） | `ScrollOffsetText` | `Scroll: VerticalOffset=N` |
| 待機（進行中→完了） | `BusyStartButton`（300ms × 5 回） | `BusyStatusText` | `Busy: running k/5` → `Busy: done`（初期 `Busy: idle`） |
| コンテキストメニュー サブメニュー | `RightClickTestArea` → `ContextMenuSubmenu` → `ContextMenuSubItem1` / `ContextMenuSubItem2` | `ContextMenuCountText` | `ContextMenu: opened N, selected Sub Item 1`（または `Sub Item 2`） |
| コンテキストメニュー 無効項目 | `RightClickTestArea` → `ContextMenuDisabledItem`（`IsEnabled=False`） | `ContextMenuCountText` | 変化しないこと（誤って起動された場合だけ `ContextMenu: opened N, selected Disabled Item` になる） |
| Win32 ポップアップメニュー（右クリック） | `Win32MenuTestArea` | `Win32MenuResultText` | カーソル位置に `#32768` が開き、選択後 `Win32Menu: selected 1001`（キャンセルは `Win32Menu: cancelled`、初期 `Win32Menu: (none)`） |
| Win32 ポップアップメニュー（ボタン） | `ShowWin32MenuButton` | `Win32MenuResultText` | ボタン直下に同じ `#32768` が開く（右クリック不要の経路）。結果表示は上と同じ |

Win32 ポップアップメニュー（`Win32MenuInterop.ShowPopupMenu`）の項目とコマンド ID:

| 項目 | コマンド ID | 備考 |
|---|---|---|
| `Win32 Item A` | 1001 | MF_STRING |
| `Win32 Item B` | 1002 | MF_STRING |
| `Submenu` | （なし） | MF_POPUP のためコマンド ID を持たない |
| `Submenu` → `Sub Item 1` | 1101 | MF_STRING |
| `Submenu` → `Sub Item 2` | 1102 | MF_STRING |
| `Disabled Item` | 1201 | MF_GRAYED（選択できない） |

補足:

- ドラッグは OLE の `DoDragDrop` ではなく `Mouse.Capture` による手動追跡で判定する（`DragSource` の `PreviewMouseLeftButtonUp` の座標が `DragTarget` の矩形内かどうか）。
- Busy の追加項目は `BusyItemsPanel`（`StackPanel`）の子として `Busy item k` という `TextBlock` で積まれる。WPF の `Panel` は AutomationPeer を持たないため `BusyItemsPanel` 自体は UIA ツリーに現れない。進捗の確認は `BusyStatusText` か `Busy item k` という Name の Text 要素で行う。
- Win32 ポップアップメニューは `CreatePopupMenu` + `TrackPopupMenuEx(TPM_RETURNCMD | TPM_LEFTALIGN | TPM_TOPALIGN)` で表示する。WPF の `ContextMenu`（`HwndWrapper[...]` 内の Popup）と違い、ClassName `#32768` の独立したトップレベルウィンドウとして現れる。
- `TrackPopupMenuEx` はメニューが閉じるまで UI スレッドでモーダルループを回す。そのため `Win32MenuResultText` の更新はメニューが閉じた後になる（表示中は WPF のイベントハンドラーから戻らない）。
- メニューの表示座標はスクリーン物理 px で指定する。`Visual.PointToScreen` は内部で `CompositionTarget.TransformToDevice` を適用済みのため、コード側で DPI 換算を重ねていない（DPI 125% の実機で、ボタン直下・カーソル位置に開くことを確認済み。fixture 作成時に単体起動 + UIA クライアントで確認。Exp 経由の検証は verify8）。

## safety / 多数ウィンドウ検証用 UI（MainWindow 4 列目）

| 操作 | 操作対象 AutomationId | 結果表示 AutomationId | 期待する表示 |
|---|---|---|---|
| 無効ボタンへのクリック | `DisabledTestButton`（`IsEnabled=False`） | `DisabledClickCountText` | 変化しないこと（初期 `DisabledClick: 0`。誤って起動された場合だけ `DisabledClick: N` になる） |
| 多数ウィンドウを開く | `OpenManyWindowsButton` | `ManyWindowsCountText` | `ManyWindows: 22`（初期 `ManyWindows: 0`） |
| 多数ウィンドウを閉じる | `CloseManyWindowsButton` | `ManyWindowsCountText` | `ManyWindows: 0` |

補足:

- `OpenManyWindowsButton` は `Many Window 1`〜`Many Window 22` というタイトルの `SampleDialog` を `Show()` で開く（`Owner` = MainWindow、`ShowInTaskbar=false`、200 × 100、左上を 20 px ずつずらす）。可視トップレベルウィンドウが 22 個増えるため、20 件を超える列挙・打ち切りの確認に使う。
- `Many Window k` は 200 × 100 に縮めてあるため `CloseDialogButton` が枠内に収まらない。個別に閉じずに `CloseManyWindowsButton` でまとめて閉じる（MainWindow を閉じた場合も Owner 付きのため一緒に閉じる）。
- オフスクリーン要素の確認には既存 `ScrollableArea` の `ScrollItem40` を使う（スクロールしない限り `IsOffscreen=true`）。

## 診断ログ修正の実機確認用 UI（MainWindow 4 列目の下部）

feature/diagnostics-log-fixes（機密マスクの引用符対応・例外メッセージへの HRESULT 付加）を実機で確認するための UI。既存の画面・AutomationId は変えていない。

| 操作 | 操作対象 AutomationId | 結果表示 AutomationId | 期待する表示 |
|---|---|---|---|
| Faulty Peer の有効化 | `EnableFaultyPeerCheckBox`（既定 OFF） | なし | OFF の間は `FaultyPeerButton` が通常のボタンとして動く |
| Invoke が例外を投げるボタン | `FaultyPeerButton` | `LastDialogResultText` | OFF: `最後の結果: FaultyPeerButton -> clicked N`。ON: `InvokePattern.Invoke` が `COMException`（HRESULT `0x80041234`）を投げ、`ui_window_click` がエラーを返す（表示は変わらない） |
| 機密マスク対象語を Name に含むボタン | `MaskSampleButton`（Name は `token=visible`） | `LastDialogResultText` | `最後の結果: MaskSampleButton -> clicked N` |
| ExpandCollapsePattern で開閉する ComboBox | `ExpandCollapseTestComboBox`（項目は `Combo Item A` / `B` / `C`。初期は未選択） | `LastDialogResultText` | `ui_window_click` は Invoke / SelectionItem / Toggle を持たないため `method=expandCollapsePattern`（閉じていれば `action=expand`）。項目を選ぶと `最後の結果: ExpandCollapseTestComboBox -> Combo Item X` |
| 行（セル）を画面外に持つ読み取り専用 DataGrid | `ScrollItemTestDataGrid`（Height 80、行 `GridScrollRow01`〜`GridScrollRow40`、列は `Name`） | `LastDialogResultText` | 行を選ぶと `最後の結果: ScrollItemTestDataGrid -> GridScrollRowNN`。セルのパターンは実機で `ui_window_find_elements` の `patterns` を見て確認する |
| ScrollItemPattern と SelectionItemPattern を持つ項目を画面外に持つ ListBox | `ScrollItemTestListBox`（Height 80、仮想化なし）の `ListScrollItem01`〜`ListScrollItem40` | `LastDialogResultText` | 項目を選ぶと `最後の結果: ScrollItemTestListBox -> ListScrollItemNN`。枠に収まらない項目は `IsOffscreen=true` で、`patterns` に `selectionItem` と `scrollItem` が出る |

補足:

- `FaultyPeerButton` は `AutomationPeer`（`FaultyPeerButtonAutomationPeer`）を差し替えており、例外を投げるのは `IInvokeProvider.Invoke` だけ。`Name` などのプロパティ取得では投げない（検索走査の途中で投げると、走査側が例外を握りつぶして兄弟要素が見えなくなるため）。
- 機密マスクの確認は、`ui_window_click` に存在しない名前（例 `token=abc`）を渡し、`diagnostics_export` の tool.end の errorMessage が `'token=***' not found …` になることを見る（Tool の応答そのものは伏せられない。伏せられるのは診断ログだけ）。`ui_window_find_elements` は該当なしのときエラーではなく success（count=0）を返すので、この確認には使えない。
- 引用符まわりの確認例として、値の途中に単引用符を含む `password=ab'cd` も渡す。errorMessage が `Element with Name 'password=***' not found …` になり、`cd` のような後半が平文で残らないこと。
- `ScrollItemTestDataGrid` は読み取り専用の `DataGrid`（`SelectionUnit=FullRow`、`SelectionMode=Single`、行の仮想化なし、列は `Name` の 1 つ、Height 80）で、行は `GridScrollRow01`〜`GridScrollRow40`。「`SelectionItemPattern` を持たず `ScrollItemPattern` だけを持つセル」に対して、`ui_window_click` が `ScrollIntoView` → 物理クリックへ進む経路を確認する狙い。ただし WPF のセルが実際にどのパターンを持つか（`patterns` に何が出るか）は実機で確認する前提で、ここでは断定しない。行が選ばれると結果表示欄が `ScrollItemTestDataGrid -> GridScrollRowNN` になる。`ScrollIntoView` を経由して物理クリックが成功したときは、`ui_window_click` の戻り値に `scrolledIntoView: true` が付く。スクロール直後に座標が落ち着かなかったときは、物理クリックせず `geometry did not settle within 200 ms after ScrollIntoView` のエラーになる。
- `ScrollItemTestListBox` の項目は `ListBoxItem` で `SelectionItemPattern` も持つため、`ui_window_click` は（画面外の項目でも）先に `selectionItemPattern` で選ぶ。物理クリックの前の `ScrollIntoView` に入るのは、SelectionItem で選べなかった（パターン非対応・選択が変わらない）画面外の要素だけ。
- 既存の `ScrollItem40`（`TextBlock`）は `ScrollItemPattern` を持たない。クリックしても `ScrollIntoView` されず、従来どおり offscreen のエラー（`... is offscreen (IsOffscreen=true); scroll it into view first`）になる。「ScrollItemPattern を持たない要素では従来のエラーのまま」の失敗系の確認に使う。

## 画面外の同名ウィンドウの確認用 UI（MainWindow 4 列目の下部）

`ui_wait_for_window` / `ui_capture_window_by_title` がタイトル一致の候補から画面外の窓を除いて解決することを実機で確認するための UI。既存の画面・AutomationId は変えていない。

| 操作 | 操作対象 AutomationId | 結果表示 AutomationId | 期待する表示 |
|---|---|---|---|
| メインと同じタイトルの窓を画面外に開く | `OpenOffscreenTwinButton` | `LastDialogResultText` | `最後の結果: OffscreenTwin -> opened N`（N は開いた窓の HWND の 10 進値）。押すたびに 1 つ増える |
| 画面外の窓を閉じる | Twin 窓の中の `CloseOffscreenTwinButton`（`ui_list_windows` で Twin の handle を取り、`ui_window_click` で操作） | なし | その Twin だけが閉じる |

補足:

- Twin は Title が `WindowListSample Main`（メインと同じ）、`Left = Top = -32768`、`WindowStartupLocation = Manual`、`ShowActivated = false`、Owner なしの `Window`（300 × 120）で、メインの手前には出ない。どのモニターにも属さないため `MonitorFromWindow(MONITOR_DEFAULTTONULL)` が 0 を返す。
- `ui_list_windows` には Twin も出る（一覧は絞り込まない）。`ui_wait_for_window(title="WindowListSample Main")` や `ui_capture_window_by_title` は、画面内のメインウィンドウ 1 件に解決する（Twin を開く前と同じ結果）。
- Twin は画面外にあるため `CloseOffscreenTwinButton` を物理クリックできない。`ui_window_click` は InvokePattern で閉じる（座標を使わない）。
- メインウィンドウを閉じると Twin も一緒に閉じる（画面外の窓だけが残ってプロセスが終了しなくなるのを防ぐため）。

## 使い方（Experimental Instance での検証手順）

```text
1. VSIXInstaller.exe /quiet /rootSuffix:Exp src\VsMcp.Extension\VsMcp.Extension.vsix
2. devenv.exe /rootsuffix Exp /updateconfiguration   （初回インストール直後は必須）
3. devenv.exe /rootsuffix Exp tests\wpf\WindowListSample\WindowListSample.slnx
4. %LOCALAPPDATA%\VsMcp\server.<pid>.port のポートへ POST http://localhost:<port>/mcp
5. debug_start → ui_list_windows → ui_click(OpenOwnedDialogButton) → ui_list_windows → ui_click(CloseDialogButton) → …
```

## 確認観点

| ケース | MainWindow | Dialog |
|---|---|---|
| Owner あり ShowDialog | isEnabled=false, ownerHandle=0, isModalCandidate=false | isEnabled=true, ownerHandle=Main, isModalCandidate=true |
| Owner なし ShowDialog | isEnabled=false, ownerHandle=0 | isEnabled=true, ownerHandle=0, isModalCandidate=true |

補足: Visual Studio のデバッガ配下では XAML ランタイムツールのオーバーレイ（タイトルなし・Owner 付き・可視）が各ウィンドウに 1 つ列挙される。これは仕様どおり除外しない。

## 今後の利用候補

`ui_capture_window_by_handle` / `ui_capture_window_by_title` / `ui_capture_active_window` / ウィンドウ待機ツール / MessageBox・TaskDialog・標準ファイルダイアログの検証に拡張する。
