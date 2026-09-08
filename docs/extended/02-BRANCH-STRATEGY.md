# 02. ブランチ戦略

## main

目的:
- upstream追従専用
- 独自機能を入れない

同期:

```powershell
git switch main
git fetch upstream
git merge --ff-only upstream/main
git push origin main
```

## develop

目的:
- Extended機能の統合
- リリース候補の母体

upstream更新後:

```powershell
git switch develop
git rebase main
git push --force-with-lease origin develop
```

## featureブランチ

例:

```text
feature/modal-window-capture
feature/standard-dialog-automation
feature/window-wait
```

小さな論理コミットに分割する。

例:

```text
Add top-level window enumeration
Generalize capture pipeline for arbitrary HWND
Add modal dialog capture tool
Add active window capture
Add regression tests
```

## 原則

- mainに独自コミットを入れない
- rebase後のforce pushは `--force-with-lease` のみ
- upstream変更を取り込んだらBuild/Test
- コンフリクトは変更意図単位で解決する
