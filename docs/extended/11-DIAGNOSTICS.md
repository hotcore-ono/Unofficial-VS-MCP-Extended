# 11. 診断トレース基盤

Extended のツールが「何を見て、何を選び、なぜ失敗したか」を後から追えるようにするための常時記録の仕組み。
UI 自動化の失敗はその場のスクリーンショットだけでは再現できないことが多いため、判断点を JSONL で残す。

## 保存場所

ルートは `%LOCALAPPDATA%\Unofficial-VS-MCP-Extended\`。

| パス | 内容 |
|---|---|
| `Config\diagnostics.json` | 設定ファイル。無ければ既定値で自動生成 |
| `Config\diagnostics.json.invalid-<yyyyMMdd-HHmmss>` | JSON が壊れていた設定の退避先 |
| `Logs\<yyyy-MM-dd>\session-<yyyyMMdd-HHmmss>-<sessionId>.jsonl` | セッションごとのイベントログ |
| `Logs\<yyyy-MM-dd>\...-part-NNN.jsonl` | ローテーション後の続きファイル（2 個目以降） |
| `Diagnostics\dumps\` | `errorDetailDump` 用に起動時へ作るフォルダー（`DiagnosticHub.cs:318`）。ダンプ本文は JSONL の `diagnostics.errorDump` に載るため、現状このフォルダーへはファイルを書かない |
| `Diagnostics\screenshots\` | `captureScreenshotOnError=true` のときのエラー時スクリーンショット |
| `Exports\diagnostics-<yyyyMMdd-HHmmss>-<exportId>.zip` | `diagnostics_export` の出力 |

セッション ID は `s-<yyyyMMddHHmmss>-<GUID 先頭 8 桁>`、相関 ID は `c-<GUID 先頭 8 桁>`。
JSONL は 1 行 1 イベント（UTF-8 BOM なし）で、`FileShare.ReadWrite` で開いているため
Visual Studio の実行中でも外部から読める。

## JSONL のスキーマ

必須項目は `schemaVersion`(1) / `timestampUtc` / `sessionId` / `correlationId` / `sequence` /
`level` / `category` / `event` / `tool`。
任意項目は `elapsedMs` / `result` / `message` / `data` / `exception` / `dropped`。
`sequence` はセッション内で単調増加し、`correlationId` は 1 回のツール呼び出しを束ねる。

## 設定と既定値

```json
{
  "enabled": true, "level": "info", "writeJsonl": true, "writeOutputPane": true,
  "maxFileSizeMb": 16, "retentionDays": 30, "maxSessionFiles": 100,
  "errorDetailDump": true, "captureScreenshotOnError": false,
  "includeUiText": false, "includeFilePaths": false,
  "maxStringLength": 200, "maxDumpBytes": 65536,
  "maxWindows": 20, "maxUiCandidates": 10, "queueCapacity": 4096
}
```

未知のキーは無視し、欠けたキーは既定値を使う。範囲外の値は読み込み時に丸める。

## ログ水準

| level | 用途 |
|---|---|
| `off` | 完全に無効。ツールのラッパーも素通しするので負荷はほぼ 0 |
| `error` / `warning` | 異常のみ |
| `info`（既定） | ツールの開始・終了、ウィンドウ解決、UIA 検索の開始・終了、メニュー・ダイアログの操作 |
| `verbose` | 問題再現時。geometry・capture・要素解決（`uia.resolve`）などの判断材料が増える |
| `trace` | 短時間の深掘り専用。poll 1 回・訪問要素 1 個ごとに記録する |

`trace` を常用しない。作業後は `diagnostics_set_level` で `info` へ戻す。

## ツール

| ツール | 用途 |
|---|---|
| `diagnostics_get_status` | `enabled` / `level` / `sessionId` / `currentLogFile` / `queueLength` / `droppedEventCount` / `writerHealthy` と有効な上限値 |
| `diagnostics_set_level` | 水準の変更。`persist=true` で `diagnostics.json` にも保存 |
| `diagnostics_mark` | 「ここで問題が起きた」の目印。再現の前後に打ち、export した `events.jsonl` の中で位置を探すのに使う（mark 自身の `correlationId` で export しても mark 1 件しか出ない） |
| `diagnostics_export` | ZIP 出力。指定を選ぶ順（推奨）は `sessionId`（セッション全体） → `minutes`（既定 30 分） → `correlationId`（既知の 1 呼び出しだけ）。実装の適用順は `sessionId` > `correlationId` > `minutes` で、id を渡すと `minutes` は無視される（`DiagnosticExport.IsMatch`） |
| `diagnostics_flush` | キューの書き出しを待つ。検証スクリプトから外部でログを読むとき用 |

## 記録するイベント

| category | event | 主なデータ |
|---|---|---|
| session | `session.start` / `session.end` | 環境要約（ユーザー名・PC 名・パスは含まない） |
| tool | `tool.start` / `tool.end` | 引数要約、`result`（success / error / normalFalse / timeout）、`elapsedMs` |
| window | `window.resolve.start` / `.success` / `.failure` | requestedHandle / normalizedHandle / pid / className / isModalCandidate / resolutionReason |
| modal | `modal.evaluate` / `modal.block`(warning) | targetHandle / isBlocked / blockingHandle / reason |
| geometry | `geometry.resolve`(verbose) | handle / dpi / monitorName / bounds |
| uia | `uia.search.start` / `.end` / `.truncated`(warning) / `.timeout`(warning) | view / maxVisited / visitedCount / resultCount / selectorSummary |
| uia | `uia.resolve`(verbose) | windowHandle / visitedCount / elapsedMs / matchCount / failure / view / automationId / className / controlType / index / hasName / nameLength |
| interaction | `interaction.prepare`（成功は verbose・失敗は warning） | rootWindowHandle / automationId / className / elementControlType / hasName / index / bounds / point / role / isPhysical / failure |
| interaction | `interaction.execute.end` | actualMethod（戻り値が実際の操作方式を返す Tool のみ） |
| input | `input.keyboard` / `input.mouse` | requestedEventCount / insertedEventCount / win32Error / foreground の変化 |
| wait | `wait.start` / `wait.end` | timeoutMs / pollIntervalMs / elapsedMs / reason / result |
| menu | `menu.detect` / `.classify` / `.select` / `.close` / `.submenu` / `.fallback`(warning) | menuType / itemsSource / method / submenuDetection |
| dialog / fileDialog | `dialog.detect` / `.classify` / `.execute` / `fileDialog.select` / `.setFilename` / `.confirm` / `.cancel` | dialogType / mode / buttonId / action / method |
| capture | `capture.window`(verbose) | handle / width / height / mimeType / bytes |
| exception | `exception` | 型 / メッセージ（sanitize 済み）/ スタック |
| performance | `performance.tool`(verbose) | data は無し。`tool` / `correlationId` / `elapsedMs` / `result` を必須項目として持ち、`tool.end` と対で 1 件出る |
| diagnostics | `diagnostics.mark` / `.level.changed` / `.export` / `.dropped`(warning) / `.retention.failure`(warning) / `.config.invalid` / `.errorDump` | それぞれの要点 |
| diagnostics | `diagnostics.pane.failure`(warning) | reason / consecutiveFailureCount |
| diagnostics | `diagnostics.serialize.failure`(error) | data は無し（JSON 化に失敗した event の必須項目だけを書く最小行） |

`uia.resolve` は Phase 11 で追加した。`ui_window_click` などが 1 要素を決める処理
（`UiWindowElementResolver.TryResolveSingle`）の結果を、成功・失敗のどちらでも 1 件記録する。
`failure` は `None`（成功）/ `NoCriteria` / `RootUnavailable` / `NotFound` / `IndexOutOfRange` /
`Ambiguous` / `OtherWindow` / `Disappeared` / `SearchAborted`。
セレクターは `uia.search.start` の `selectorSummary` と同じ方針で、`automationId` / `className` / `controlType` /
`index` はそのまま、Name は `hasName` / `nameLength` だけを出し、本文は `includeUiText=true` のときに限り `name` に載せる。
`verbose` が無効なときは判定 1 回だけで戻り、data の組み立ても行わない。

writer 障害（`diagnostics.writer.failure`）は JSONL に残らない。writer 自身が書けなくなった状態なので、
記録は Output pane の 1 行 `diagnostics.writer.failure …` と `diagnostics_get_status` の `writerHealthy=false` だけになる。
この 2 つで検知する。

## 機密情報の扱い

- `ui_window_send_keys` の `keys` / `text` は**本文を記録しない**。`keysProvided` / `keysTokenCount` /
  `textProvided` / `textLength` だけを残す。`includeUiText=true` にしても `text` の本文は出ない。
- ファイル名・パスは `includeFilePaths=false`（既定）のとき本文を出さない。`tool.start` の引数要約は
  `hasFileName` / `fileNameLength` / `fileNamePathKind` / `fileNameIsAbsolute`（`DiagnosticSanitizer` が
  キー名 + `PathKind` / `IsAbsolute` で組み立てる）。`fileDialog.setFilename` / `.select` の data は
  `pathKind` / `isAbsolute` を使う。`<path>` への置換は、例外メッセージなどの自由文
  （`DiagnosticSanitizer.SanitizeText`）に対して行う。
- UIA の `Name` / `Value` は `includeUiText=true` のときだけ本文を記録する。
  `AutomationId` / `ControlType` / `ClassName` / HWND / bounds は常に記録する。
- 機密語（`password` / `passphrase` / `secret` / `token` / `apiKey` / `authorization` /
  `cookie` / `credential` / `privateKey`）を含むキーの値は `***` に伏せる。
  `AutomationId` / `Name` にこの 9 種のいずれかを含む要素は、`includeUiText=true` でも本文を出さない
  （`DiagnosticSanitizer.CanIncludeElementText`）。`ControlType` や `IsPassword` による判定はしていないため、
  機密語を含まない名前のパスワード欄は対象外である。
- 文字列は `maxStringLength`（既定 200）で切り、末尾に `…` を付ける。

## ローテーション・保持・キュー

- 1 ファイルが `maxFileSizeMb`（既定 16 MB）を超えると `-part-002.jsonl` 以降へ切り替える（日付はセッション開始日で固定）。
- 起動時に `retentionDays`（既定 30 日）より古い日付フォルダーと、`maxSessionFiles`（既定 100）を超える古い
  session ファイルを削除する。失敗しても動作は続き、`diagnostics.retention.failure`（warning）を 1 件残す。
- 書き込みは容量 `queueCapacity`（既定 4096）の非同期キュー経由で、背景スレッドが 64 行ごとまたは 100 ms ごとに flush する。
  ツール呼び出しから同期 flush はしない。
- キューが満杯のときは `trace` / `verbose` から捨てる。捨てた件数は `droppedEventCount` に積まれ、
  一定件数ごとに `diagnostics.dropped`（warning）を記録する。**error / warning は極力残す**
  （満杯時は先頭 8 件から `trace` / `verbose` を 1 件だけ捨てて空きを作る。先頭 8 件に捨てられる event が
  無い場合に限り、新しい event 側を諦める）。
- 書き込み例外が 5 回続くとファイル出力を停止し（`writerHealthy=false`）、Output pane に 1 度だけ警告を出す。
  以後も**ツール自体は成功し続ける**（診断はツールを止めない）。

## Output pane

`writeOutputPane=true`（既定）のとき、Visual Studio の出力ウィンドウに
「Unofficial VS MCP Extended」ペインを作り、`[HH:mm:ss.fff][correlationId][tool][LEVEL] message` の 1 行形式で書く。
出すのは `info` 以下の水準だけで、`verbose` / `trace` はファイルのみ。
ペインへの書き込みも別キューで 50 行ごとまたは 250 ms ごとにまとめ、UI スレッドを長く占有しない。

## export ZIP の構成

| エントリ | 内容 |
|---|---|
| `manifest.json` | exportId / 期間 / sessionIds / correlationIds / eventCount / errorCount / warningCount / skippedLines / 版 |
| `events.jsonl` | 対象イベント（JSON として読めない行は飛ばして `skippedLines` に数える） |
| `environment.json` | 拡張の版 / VS の版とエディション / プロセスアーキテクチャ / OS / DPI / モニター構成 / ツール数 |
| `README.txt` | 内容の説明とプライバシー方針 |
| `screenshots\` | `includeScreenshots=true` を指定した場合のみ |

ユーザー名・PC 名・リポジトリのパスは含めない。バグ報告に添付する前提の形式である。

## トラブルシュート

| 症状 | 見るところ |
|---|---|
| export が空 | `diagnostics_get_status` の `writerHealthy`。`false` なら JSONL 自体が残っていない可能性がある（「何も起きていない」証拠にはならない） |
| イベントが飛んでいる | `droppedEventCount` と `diagnostics.dropped`。`level` を下げるか `queueCapacity` を上げる |
| 設定を変えたのに効かない | `Config\diagnostics.json.invalid-*` の有無と `diagnostics.config.invalid`（`data.reason=parse`）。壊れた設定は退避され既定値で再生成される |
| ログが増えすぎる | `level` が `trace` のままになっていないか。`diagnostics_set_level` で `info` に戻す |
| 外部スクリプトから読むと途中まで | `diagnostics_flush` を呼んでから読む |

## 関連

- `docs/extended/10-CLAUDE-GUIDANCE-AND-SKILL.md` — Skill・description・get_help の役割分担
- `src/VsMcp.Extension/Skills/vs-ui-explore/SKILL.md` — Claude 向けの診断運用手順
