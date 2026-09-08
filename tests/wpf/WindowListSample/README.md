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