# 03. upstream追従手順

## 通常同期

```powershell
.\scripts\Sync-Upstream.ps1 -RepositoryDirectory "C:\Path\To\Unofficial-VS-MCP-Extended"
```

## 手動手順

```powershell
git fetch upstream

git switch main
git merge --ff-only upstream/main
git push origin main

git switch develop
git rebase main
git push --force-with-lease origin develop
```

## コンフリクト発生時

Claude Codeへ以下の観点で解析させる。

1. upstreamで変更された責務は何か
2. Extended側の変更目的は何か
3. upstreamの新しい実装を利用して同じ目的を達成できるか
4. 重複実装になっていないか
5. 公開MCP Toolの互換性が壊れていないか

解決後:

```powershell
git status
git diff main..develop
```

その後:
- Build
- Test
- Tool一覧確認
- モーダルダイアログ回帰確認
- 標準ダイアログ回帰確認
