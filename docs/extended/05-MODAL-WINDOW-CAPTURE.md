# 05. モーダルウィンドウキャプチャ設計

## 現状認識

既存のキャプチャ機能がデバッグ対象アプリのメインウィンドウを中心に設計されている場合、
WPF `ShowDialog()` 等で表示された別トップレベルウィンドウを直接指定しにくい。

## 目標

任意のトップレベルHWNDをキャプチャ対象にできるようにする。

## 提案Tool

```text
ui_list_windows
ui_get_active_window
ui_capture_window_by_handle
ui_capture_window_by_title
ui_capture_active_window
ui_wait_for_window
ui_wait_for_window_closed
```

## WindowInfo候補

```text
Handle
Title
ClassName
ProcessId
IsVisible
IsEnabled
OwnerHandle
IsModalCandidate
```

## モーダル候補判定

単一条件で断定せず、複数情報から候補判定する。

- Owner relationship
- OwnerのEnabled状態
- Window styles
- Foreground/active状態
- UI Automation metadata

戻り値に `IsModalCandidate` を持たせる場合、推測であることを明示する。

## キャプチャ

既存Windows Graphics Capture等の処理を再利用し、
「MainWindowHandleを内部で取得する」責務と
「指定HWNDを実際にキャプチャする」責務を分離する。

## 互換性

既存:

```text
ui_capture_window
```

は削除しない。

新しい任意HWNDキャプチャを内部共通処理として使い、
既存Toolは従来と同じ意味でメインウィンドウを渡すラッパーにできると望ましい。
