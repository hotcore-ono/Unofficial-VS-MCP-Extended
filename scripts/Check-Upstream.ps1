param(
    [Parameter(Mandatory = $true)]
    [string]$RepositoryDirectory
)

$ErrorActionPreference = "Stop"

$TheRepositoryPath = [System.IO.Path]::GetFullPath($RepositoryDirectory)

if (-not (Test-Path (Join-Path $TheRepositoryPath ".git")))
{
    throw "Not a git repository: $TheRepositoryPath"
}

Push-Location $TheRepositoryPath
try
{
    & git fetch upstream

    if ($LASTEXITCODE -ne 0)
    {
        throw "git fetch upstream failed."
    }

    $TheLocalCommit = (& git rev-parse main).Trim()
    $TheUpstreamCommit = (& git rev-parse upstream/main).Trim()

    Write-Host "main         : $TheLocalCommit"
    Write-Host "upstream/main: $TheUpstreamCommit"

    if ($TheLocalCommit -eq $TheUpstreamCommit)
    {
        Write-Host "Status: up to date."
        exit 0
    }

    $TheBehindCount = (& git rev-list --count "main..upstream/main").Trim()
    $TheAheadCount = (& git rev-list --count "upstream/main..main").Trim()

    Write-Host "Behind upstream: $TheBehindCount commit(s)"
    Write-Host "Ahead upstream : $TheAheadCount commit(s)"

    if ([int]$TheBehindCount -gt 0)
    {
        Write-Host "Status: upstream update available."
        exit 2
    }

    Write-Host "Status: branches differ."
    exit 3
}
finally
{
    Pop-Location
}
