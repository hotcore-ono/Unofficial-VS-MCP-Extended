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
