param(
    [Parameter(Mandatory = $true)]
    [string]$RepositoryDirectory,

    [switch]$SkipPush
)

$ErrorActionPreference = "Stop"

function Invoke-GitCommand
{
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [Parameter(Mandatory = $true)]
        [string]$WorkingDirectory
    )

    Push-Location $WorkingDirectory
    try
    {
        & git @Arguments

        if ($LASTEXITCODE -ne 0)
        {
            throw "git command failed: git $($Arguments -join ' ')"
        }
    }
    finally
    {
        Pop-Location
    }
}

$TheRepositoryPath = [System.IO.Path]::GetFullPath($RepositoryDirectory)

if (-not (Test-Path (Join-Path $TheRepositoryPath ".git")))
{
    throw "Not a git repository: $TheRepositoryPath"
}

Invoke-GitCommand -Arguments @("status", "--short") -WorkingDirectory $TheRepositoryPath

Push-Location $TheRepositoryPath
try
{
    $TheDirtyState = & git status --porcelain
}
finally
{
    Pop-Location
}

if ($TheDirtyState)
{
    throw "Working tree is not clean. Commit or stash changes before syncing upstream."
}

Invoke-GitCommand -Arguments @("fetch", "upstream") -WorkingDirectory $TheRepositoryPath
Invoke-GitCommand -Arguments @("switch", "main") -WorkingDirectory $TheRepositoryPath
Invoke-GitCommand -Arguments @("merge", "--ff-only", "upstream/main") -WorkingDirectory $TheRepositoryPath

if (-not $SkipPush)
{
    Invoke-GitCommand -Arguments @("push", "origin", "main") -WorkingDirectory $TheRepositoryPath
}

Invoke-GitCommand -Arguments @("switch", "develop") -WorkingDirectory $TheRepositoryPath
Invoke-GitCommand -Arguments @("rebase", "main") -WorkingDirectory $TheRepositoryPath

if (-not $SkipPush)
{
    Invoke-GitCommand -Arguments @("push", "--force-with-lease", "origin", "develop") -WorkingDirectory $TheRepositoryPath
}

Write-Host ""
Write-Host "Upstream sync completed."
Write-Host "Review the develop branch, then run Build/Test before merging feature work."
