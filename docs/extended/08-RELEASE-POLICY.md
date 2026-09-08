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
