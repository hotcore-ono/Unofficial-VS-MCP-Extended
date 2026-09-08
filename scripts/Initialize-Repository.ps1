param(
    [Parameter(Mandatory = $true)]
    [string]$ForkUrl,

    [Parameter(Mandatory = $true)]
    [string]$DestinationDirectory,

    [string]$UpstreamUrl = "https://github.com/dhq-boiler/Unofficial-VS-MCP.git"
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

$TheDestinationPath = [System.IO.Path]::GetFullPath($DestinationDirectory)

if (Test-Path $TheDestinationPath)
{
    throw "Destination already exists: $TheDestinationPath"
}

$TheParentDirectory = Split-Path -Parent $TheDestinationPath
$TheRepositoryName = Split-Path -Leaf $TheDestinationPath

New-Item -ItemType Directory -Force -Path $TheParentDirectory | Out-Null

Push-Location $TheParentDirectory
try
{
    & git clone $ForkUrl $TheRepositoryName

    if ($LASTEXITCODE -ne 0)
    {
        throw "git clone failed."
    }
}
finally
{
    Pop-Location
}

Invoke-GitCommand -Arguments @("remote", "add", "upstream", $UpstreamUrl) -WorkingDirectory $TheDestinationPath
Invoke-GitCommand -Arguments @("fetch", "upstream") -WorkingDirectory $TheDestinationPath

Invoke-GitCommand -Arguments @("switch", "main") -WorkingDirectory $TheDestinationPath
Invoke-GitCommand -Arguments @("merge", "--ff-only", "upstream/main") -WorkingDirectory $TheDestinationPath
Invoke-GitCommand -Arguments @("push", "origin", "main") -WorkingDirectory $TheDestinationPath

$TheDevelopExists = $false
Push-Location $TheDestinationPath
try
{
    & git show-ref --verify --quiet refs/heads/develop
    $TheDevelopExists = ($LASTEXITCODE -eq 0)
}
finally
{
    Pop-Location
}

if (-not $TheDevelopExists)
{
    Invoke-GitCommand -Arguments @("switch", "-c", "develop", "main") -WorkingDirectory $TheDestinationPath
    Invoke-GitCommand -Arguments @("push", "-u", "origin", "develop") -WorkingDirectory $TheDestinationPath
}
else
{
    Invoke-GitCommand -Arguments @("switch", "develop") -WorkingDirectory $TheDestinationPath
}

$TheFeatureBranch = "feature/modal-window-capture"

Push-Location $TheDestinationPath
try
{
    & git show-ref --verify --quiet "refs/heads/$TheFeatureBranch"
    $TheFeatureExists = ($LASTEXITCODE -eq 0)
}
finally
{
    Pop-Location
}

if (-not $TheFeatureExists)
{
    Invoke-GitCommand -Arguments @("switch", "-c", $TheFeatureBranch, "develop") -WorkingDirectory $TheDestinationPath
    Invoke-GitCommand -Arguments @("push", "-u", "origin", $TheFeatureBranch) -WorkingDirectory $TheDestinationPath
}

Write-Host ""
Write-Host "Repository initialization completed."
Write-Host "Path: $TheDestinationPath"
Write-Host "Current branch: $TheFeatureBranch"
Write-Host ""
Write-Host "Next:"
Write-Host "1. Copy project-overlay files into the repository as needed."
Write-Host "2. Commit the Extended project documents."
Write-Host "3. Start modal window capture implementation."
