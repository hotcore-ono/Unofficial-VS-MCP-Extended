# 04. Extended 初期機能スコープ

## Phase 1 — Window discovery

- デバッグ対象プロセスのトップレベルウィンドウ列挙
- HWND
- Title
- ClassName
- PID
- Visible
- Enabled
- Owner

## Phase 2 — Arbitrary window capture

- HWND指定
- タイトル指定
- アクティブウィンドウ
- モーダルダイアログ
- 既存キャプチャ経路再利用
- DPI / マルチモニター検証

## Phase 3 — Window synchronization

- 出現待ち
- 消滅待ち
- タイムアウト
- 複数候補の扱い

## Phase 4 — Standard dialog recognition

- MessageBox
- TaskDialog
- File Open
- File Save
- Folder Select

## Phase 5 — Standard dialog execution

- OK / Cancel
- Yes / No
- Retry / Continue
- Open / Save
- ファイル名設定
- ファイル／フォルダ選択

## 将来候補

- PrintDialog
- ColorDialog
- FontDialog
- PageSetupDialog
- Visual Studio本体の標準ダイアログ対応
- より汎用的なWindow Semantic Adapter
