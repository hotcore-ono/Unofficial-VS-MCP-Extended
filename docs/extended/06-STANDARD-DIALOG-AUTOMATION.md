# 06. Windows標準ダイアログ自動化

## 基本思想

Windows標準ダイアログについて、毎回UIツリーを探索して意味を推測するのではなく、
既知の標準UIとして構造化された情報を返す。

## 対象

### MessageBox
取得候補:
- Title
- Message
- Icon
- Buttons
- DefaultButton
- Owner
- IsModal

操作:
- OK
- Cancel
- Yes
- No
- Abort
- Retry
- Ignore
- TryAgain
- Continue

可能であれば文字列ではなく標準コントロールIDを優先する。

### TaskDialog
取得候補:
- WindowTitle
- MainInstruction
- Content
- Icon
- Buttons
- CommandLinks
- RadioButtons
- VerificationCheckbox

### File Open / Save
取得候補:
- DialogType
- CurrentFolder
- FileName
- SelectedItems
- FileTypeFilter
- ConfirmAction
- CanCancel

操作:
- FileName設定
- 選択
- Open
- Save
- Cancel

## 「観測結果」と「元APIフラグ」を分ける

ダイアログ表示後に観測できる内容から、
ボタン構成やデフォルトボタン等を高精度で判断できる場合がある。

ただし呼び出し元が指定した全フラグを、
表示済みウィンドウから100%復元できるとは限らない。

したがって返却モデルでは例として:

```text
Observed:
  ButtonMode: OKCancel
  Icon: Warning
  DefaultButton: Cancel
  IsModal: true

OriginalFlags:
  Known: false
```

のように分離する。

推測値を元APIフラグとして断定しない。

## Adapter構造候補

```text
StandardDialogAdapter
├─ MessageBoxAdapter
├─ TaskDialogAdapter
├─ FileOpenDialogAdapter
├─ FileSaveDialogAdapter
└─ FolderDialogAdapter
```

## MCP Tool候補

```text
standard_dialog_detect
standard_dialog_get_info
standard_dialog_execute
standard_dialog_capture
standard_dialog_wait
standard_dialog_wait_closed

standard_file_dialog_get_info
standard_file_dialog_set_filename
standard_file_dialog_select
standard_file_dialog_confirm
standard_file_dialog_cancel
```

## フォールバック順序

1. Win32 / UI Automationの構造情報
2. 標準コントロールID
3. AutomationId / ControlType
4. Window class / style
5. 表示文字列
6. 画像認識

可能な限り言語非依存にする。
