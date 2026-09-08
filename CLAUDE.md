# CLAUDE.md — Unofficial-VS-MCP-Extended

## Project identity

このリポジトリは `dhq-boiler/Unofficial-VS-MCP` を upstream とする派生版
`Unofficial-VS-MCP-Extended` である。

目的は upstream を継続追従しながら、Visual Studio 2026 / Claude Code での
UI操作、ウィンドウキャプチャ、Windows標準ダイアログ処理を強化することである。

## Git rules

### main
- `main` は upstream 追従専用。
- 独自変更を直接コミットしない。
- upstream/main と可能な限り同一状態を維持する。

### develop
- Extended 独自変更の統合ブランチ。
- featureブランチの完了後に統合する。

### feature/*
- 1機能1ブランチを基本とする。
- 最初の候補:
  - `feature/modal-window-capture`
  - `feature/standard-dialog-automation`

## Upstream update policy

upstream 更新を取り込む場合:

1. `git fetch upstream`
2. `main` を `upstream/main` へ fast-forward
3. `develop` を最新 `main` へ rebase
4. コンフリクトを解析
5. upstream の新設計を尊重しつつ、Extended の機能目的を再適用
6. Build
7. Test
8. MCP tool定義差分確認
9. UI Automation / Capture の回帰確認

コンフリクト解消では「古い独自コードをそのまま残す」ことより、
upstreamの新しい設計へ独自機能の意図を移植することを優先する。

## Compatibility

- 既存MCP Toolの名前・引数・戻り値を安易に変更しない。
- 既存 `ui_capture_window` の互換性を維持する。
- 新機能は原則として追加Toolまたは後方互換な拡張で実装する。
- upstreamに同等機能が追加された場合、自前実装を重複させず統合・廃止を検討する。

## Feature group 1: Window management / capture

実装候補:

- `ui_list_windows`
- `ui_get_active_window`
- `ui_capture_window_by_handle`
- `ui_capture_window_by_title`
- `ui_capture_active_window`
- `ui_wait_for_window`
- `ui_wait_for_window_closed`

取得候補:
- HWND
- Title
- ClassName
- ProcessId
- IsVisible
- IsEnabled
- OwnerHandle
- IsModalCandidate

方針:
- デバッグ対象プロセスに属するトップレベルウィンドウを対象とする。
- 既存キャプチャ実装を再利用し、MainWindowHandle固定部分を任意HWND対応へ一般化する。
- タイトル重複時はHWND指定を優先する。
- UI Automationの対象ウィンドウとキャプチャ対象の識別子を可能な限り統一する。

## Feature group 2: Windows standard dialog automation

Windows標準ダイアログを既知パターンとして認識し、
アプリ固有UIの事前探索なしで情報取得・操作できるようにする。

候補Adapter:
- `MessageBoxAdapter`
- `TaskDialogAdapter`
- `FileOpenDialogAdapter`
- `FileSaveDialogAdapter`
- `FolderDialogAdapter`

候補MCP Tool:
- `standard_dialog_detect`
- `standard_dialog_get_info`
- `standard_dialog_execute`
- `standard_dialog_capture`
- `standard_dialog_wait`
- `standard_dialog_wait_closed`
- `standard_file_dialog_get_info`
- `standard_file_dialog_set_filename`
- `standard_file_dialog_select`
- `standard_file_dialog_confirm`
- `standard_file_dialog_cancel`

重要:
- 表示後に確実に観測できる情報と、呼び出し元が指定した元フラグを分離する。
- `OriginalFlags` を推測で断定しない。
- MessageBoxの標準コントロールIDが取得できる場合は、表示言語の文字列よりIDを優先する。
- 文字列解析や画像認識はフォールバックとする。

## Testing

最低限:
- WPF MainWindow + ShowDialog
- MessageBox
- TaskDialog
- FileOpenDialog
- FileSaveDialog
- 複数トップレベルウィンドウ
- 同一タイトルの複数ウィンドウ
- Ownerあり／なし
- DPI 100% / 150% / 200%
- マルチモニター
- 日本語Windows / 英語Windowsを想定した言語非依存性

## C# coding convention for new Extended code

ユーザーのC#命名規約に従うこと。

- 関数名は先頭大文字のキャメルケース相当
- クラス名は先頭大文字
- プライベート変数は `_` + 大文字開始
- ローカル変数は `The` + 大文字開始
- 入力引数は `In` + 大文字開始
- 出力引数は `Out` + 大文字開始
- 入出力引数は `InOut` + 大文字開始
- 不要なコピーを避けられる入力引数では `in` 修飾子を積極利用
- ローカル変数に `var` を使わない
- boolは Is / Has / Can で開始
- 略称を避ける
- 識別子は英語
- すべてのスコープの関数と引数にコメントを付ける

ただし upstream の既存コードを機械的に全面改名しないこと。
Extendedとして新規追加・大規模改修する範囲で適用し、差分を不必要に増やさない。
