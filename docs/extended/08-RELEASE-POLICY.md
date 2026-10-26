# 08. リリース方針

## バージョン

upstreamのバージョンと混同しないため、
Extended側のタグ命名を明示的に分けることを推奨する。

例:

```text
extended-v0.1.0
extended-v0.2.0
```

または:

```text
v0.13.3-ext.1
v0.13.3-ext.2
```

後者は「どのupstreamをベースにしているか」が分かりやすい。

## VSIX の識別子（2026-10-26）

upstream は VS Marketplace に `dhq-boiler.Unofficial-VS-MCP`（表示名 VS MCP Server）を公開している。
Extended が upstream と同じ Identity Id を使っていると、VS は Marketplace の upstream を「更新」として提示し、
「更新」を押した時点で Extended が upstream に置き換わる。拡張機能マネージャー上でも両者を区別できない。
このため Extended の `source.extension.vsixmanifest` は次の値を持つ（upstream との差分はこの 4 箇所に限定する）。

| 属性 | Extended の値 |
|---|---|
| `Identity/@Id` | `VsMcp.Extension.Extended.bb11461c-f882-4133-92eb-3bc75123ba6b` |
| `Identity/@Publisher` | `hotcore` |
| `DisplayName` | `VS MCP Server (Extended)` |
| `MoreInfo` | `https://github.com/hotcore-ono/Unofficial-VS-MCP-Extended` |

- `Identity/@Version` は `<upstream 版>.<Extended 連番>`（例: upstream 0.13.4 に対して 0.13.4.2）。csproj の `<Version>` は upstream のまま。
- Package GUID（`VsMcpPackage.PackageGuidString`）・名前付きパイプ・`%LOCALAPPDATA%\VsMcp`・MCP サーバー名 `vs-mcp` は upstream と共有している。
  **upstream と Extended を同時に導入してはならない**（入れ替えて使う）。導入手順: upstream の「VS MCP Server」をアンインストール → VS を終了 → Extended の VSIX を導入。
- Marketplace からの更新は Extended には来ない。upstream の取り込みは git（`03-UPSTREAM-SYNC.md`）で行い、Extended の VSIX を作り直す。
- upstream の rebase で vsixmanifest がコンフリクトしたら、上表の 4 箇所を Extended 値で再適用する。
- `obj/<構成>/net48/extension.vsixmanifest` は VS / dotnet のビルドで作り直されないことがある。版や識別子を変えたら obj 側を削除してからビルドし、`unzip -p <vsix> extension.vsixmanifest` で中身を確認する。

## リリースノート

必ず以下を書く。

- Base upstream commit/tag
- Extended changes
- Breaking changes
- Known limitations
- Tested Visual Studio version
- Tested Claude Code/MCP client
- Known upstream divergence

## upstreamに同等機能が入った場合

- 自前実装を比較
- upstream実装を優先
- Extended独自の付加価値だけ残す
- 不要になったpatchは削除
- README / release noteへ移行情報を記載
