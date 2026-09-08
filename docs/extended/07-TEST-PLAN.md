# 07. テスト計画

## Window Capture

- MainWindow
- WPF ShowDialog
- Ownerありダイアログ
- Ownerなしトップレベルウィンドウ
- 複数ウィンドウ
- 同一タイトル
- 非アクティブウィンドウ
- 最小化状態
- DPI 100%
- DPI 150%
- DPI 200%
- マルチモニター

## MessageBox

- OK
- OKCancel
- YesNo
- YesNoCancel
- RetryCancel
- Warning
- Error
- Information
- Question相当
- DefaultButton変更
- 日本語OS
- 英語OS相当の文字列依存性チェック

## TaskDialog

- Standard buttons
- Custom buttons
- Command links
- Verification checkbox
- Radio buttons

## File Dialog

- Open
- Save
- Folder select
- Initial folder
- File name
- File type filter
- Cancel
- 複数選択可能ケース

## 回帰

既存の以下を壊していないこと:
- UI Automation
- クリック
- キー入力
- Invoke
- 既存capture
- Debug
- Build
- Test
- その他公開MCP Tool

## 完了条件

- Build成功
- 既存テスト成功
- 新規テスト成功
- 手動実機確認
- MCP Toolの説明文確認
- Claude Codeからの実操作確認
