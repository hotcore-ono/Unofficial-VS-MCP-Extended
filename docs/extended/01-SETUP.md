# 01. セットアップ

## 前提

- Windows 11
- Visual Studio 2026
- Git
- GitHubアカウント
- Claude Code
- upstream: `https://github.com/dhq-boiler/Unofficial-VS-MCP.git`

## GitHub側

1. upstreamリポジトリをForkする。
2. Fork名を `Unofficial-VS-MCP-Extended` とする。
3. upstreamのライセンスファイルを保持する。

## ローカル初期化

PowerShell:

```powershell
.\scripts\Initialize-Repository.ps1 `
    -ForkUrl "https://github.com/<YOUR_ACCOUNT>/Unofficial-VS-MCP-Extended.git" `
    -DestinationDirectory "C:\Users\<USER>\Documents\Workspace\Unofficial-VS-MCP-Extended"
```

このスクリプトは以下を行う。

- Forkをclone
- `upstream` remote追加
- upstream取得
- `main` を upstream/main に同期
- `develop` 作成
- `feature/modal-window-capture` 作成

## overlayの配置

このZIPの `project-overlay` 以下をclone先へコピーする。

例:

```text
clone-root/
├─ CLAUDE.md
├─ docs/
│  └─ extended/
├─ scripts/
└─ .github/
```

upstream側に同名ファイルが存在する場合は上書きせず、差分を確認して統合する。
