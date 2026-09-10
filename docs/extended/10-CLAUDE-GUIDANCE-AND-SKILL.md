# 10. Claude 向けガイダンスと Skill

Extended のツールは 47 個あり、用途が近いものが並んでいる。
どれを、どの順番で、どの安全規則のもとで使うかを Claude に伝えるための仕組みを本書にまとめる。

## 役割分担

```text
Skill            = Claude の判断ルール（優先順位・安全規則・手順）
Tool description = 即時用途（そのツールが何をするか、隣接ツールとの役割差）
get_help         = カテゴリ一覧と横断的な注意（guidelines）
docs             = 人間向けの詳細説明（本書と 11-DIAGNOSTICS.md）
```

同じ内容を 4 か所に書かない。手順は Skill、1 文で足りる誘導は description、
仕様の詳細は Tool schema と docs に置く。

## Skill の配布の仕組み

- 正本: `src/VsMcp.Extension/Skills/vs-ui-explore/SKILL.md`。
  `.csproj` の `Content Include="Skills\**\*.*"`（`IncludeInVSIX=true` / `CopyToOutputDirectory=PreserveNewest`）で VSIX に同梱される。
- 配布: `VsMcpPackage.DeploySkills()` が `InitializeAsync` から毎起動時に呼ばれ、
  拡張アセンブリのディレクトリ配下の `Skills\` を `%USERPROFILE%\.claude\skills\` へコピーする。
- SHA256 が同一のファイルはスキップし、異なる場合だけ上書きする。コピーの失敗は `Debug.WriteLine` のみで、
  拡張の初期化は続行する（Skill が配布できなくても MCP は動く）。
- 配布先は rootsuffix を区別しない。**Experimental Instance と通常の Visual Studio が同じ
  `%USERPROFILE%\.claude\skills\vs-ui-explore\SKILL.md` を書く**ため、新しい VSIX を Exp に入れて確認した後に
  古い VSIX の通常 VS を起動すると、配布先が旧版で上書きされる。これは upstream の設計であり Extended では変更していない。
- 更新の確認: リポジトリ版と配布先の SHA256 を比較する。本文 2 行目の `Revision:` 行でも版を判別できる。

## Tool 選択の優先順位

```text
MessageBox / TaskDialog          → standard_dialog_*
Open / Save / Folder ダイアログ  → standard_file_dialog_*
Popup / ContextMenu / Win32 Menu → ui_menu_*
その他のデバッグ対象ウィンドウ   → ui_window_*
Extended に専用ツールが無い場合のみ upstream の generic ui_*
```

```text
standard_dialog_*      > generic click
standard_file_dialog_* > generic click
ui_menu_*              > generic click
ui_window_*            > upstream generic ui_*
ui_window_send_keys    > upstream ui_send_keys
```

専用ツールは対象の意味づけ・安全判定（HWND 再検証・モーダル拒否・所属ウィンドウ確認）・診断イベントを持つ。
upstream の generic ツールはメインウィンドウ固定なので、モーダルダイアログ・所有ウィンドウ・ポップアップには届かない。

## ワークフロー 5 種

| 対象 | 流れ |
|---|---|
| 通常ウィンドウ | `ui_list_windows` / `ui_get_active_window` → `ui_window_get_info`（`modalState` を先に見る）→ `ui_window_find_elements` → `ui_window_click` ほか → `ui_window_wait_idle` と状態確認 |
| 標準ダイアログ | `standard_dialog_detect` / `_wait` → `_get_info` →（必要なら `_capture`）→ `_execute`（`action` または `buttonId`）→ `_wait_closed` |
| ファイルダイアログ | `standard_file_dialog_detect` / `_wait` → `_get_info` → `_set_filename` または `_select` → `_confirm` / `_cancel` → `_wait_closed` |
| ポップアップメニュー | `ui_window_right_click`（戻り値の `menuHandles`）→ `ui_menu_wait` → `ui_menu_get_info` → `ui_menu_select`。入れ子は `ui_menu_select_path`、閉じるだけは `ui_menu_close` |
| 診断 | `diagnostics_get_status` → `diagnostics_mark`（再現の前） → 再現 → `diagnostics_mark`（再現の後） → `diagnostics_export`（`sessionId` 優先。mark の `correlationId` では mark 1 件しか出ない） |

補足:

- 上書き確認は別ダイアログである。`standard_file_dialog_confirm` は最初の確定だけを行い、
  後続のプロンプトは `standard_dialog_detect` / `standard_dialog_execute` で処理する。
- メニューは HWND が再利用されるため、`ui_menu_detect` / `_wait` / `_wait_closed` は内容で判定する。
- 待機は sleep ではなく `*_wait` / `*_wait_closed` / `ui_window_wait_idle` を使う。

## Do Not

```text
Do not pick the first ambiguous element automatically.
Do not interact with a blocked modal owner.
Do not reuse a stale HWND after a window closes.
Do not use upstream ui_send_keys for Extended x64 keyboard automation.
Do not prefer screen coordinates over structured selectors.
Do not leave diagnostics at trace during normal long-running work.
Do not intentionally place secrets in diagnostics_mark.
```

`ui_send_keys` の項は Phase 9 の実測（x64 の Visual Studio 上で `SendInput` が `inserted=0` /
`ERROR_INVALID_PARAMETER` を返す）に基づく。upstream ツール自体は Extended では修正していない。

## Phase 11 の self-use test

Skill の文言が実際に Claude の選択を変えるかを、人間がツール順序を指示せずに確かめる。

1. Experimental Instance で検証アプリ（`tests/wpf/WindowListSample`）をデバッグ実行する。
2. `diagnostics_mark "self-use-test start"` を入れ、ログの開始点を作る。
3. サブエージェントに配布先の `%USERPROFILE%\.claude\skills\vs-ui-explore\SKILL.md` だけを読ませ、
   HTTP POST で MCP を呼ばせる。**使うツールと順序は指示しない。**
4. 課題は 8 つ: メインウィンドウのボタン / MessageBox / ファイルダイアログのキャンセル /
   WPF ContextMenu の項目 / 入れ子メニュー / キーボード入力 / モーダルにブロックされた owner を先に解消 /
   意図した Error の後の diagnostics status・mark・export。
5. 判定は JSONL の `tool.start` を時系列に並べ、優先順位に反する呼び出し
   （generic `ui_click` / `ui_send_keys`、曖昧な候補の `index=0`、閉じた HWND の再利用、ブロックされた owner への操作）を数える。
6. 誤選択が出たら、原因を「Skill の説明不足 → description → ツール名 → get_help」の順で評価し、
   直した上で再実行する（最大 2 回）。

## 関連

- `docs/extended/11-DIAGNOSTICS.md` — 診断トレース基盤の詳細
- `docs/extended/07-TEST-PLAN.md` — テスト計画（Phase 11 の節を含む）
- `src/VsMcp.Extension/Skills/vs-ui-explore/SKILL.md` — Skill 本文
