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

## Phase 11（Claude ガイダンスと診断の検証）

- Skill self-use test: 配布先の SKILL.md だけを渡し、ツール順序を指示せずに 9 課題（ボタン / MessageBox /
  ファイルダイアログ cancel / ContextMenu / 入れ子メニュー / メニューを閉じる / キーボード / モーダルにブロックされた owner /
  意図した Error 後の diagnostics）を実行させ、JSONL の tool.start 順で優先順位違反の件数を数える。
  違反 = generic ui_click / ui_send_keys、曖昧な index=0、閉じた HWND の再利用、ブロックされた owner への操作。
- 診断検証: queue overflow（error / warning を極力残すこと: 満杯時は先頭 8 件から trace / verbose を 1 件だけ
  捨てて空きを作り、先頭 8 件に捨てられる event が無い場合に限り新しい event 側を諦める）、retention、large export、shutdown 予算、
  エラー時スクリーンショット、壊れた config の退避と既定値再生成。Output pane の障害は外から起こせないため未確認。
- 回帰: Phase 10 の検証スクリプトを再実行し、Phase 7〜10 と標準／ファイルダイアログ・upstream の動作を確認する。

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
