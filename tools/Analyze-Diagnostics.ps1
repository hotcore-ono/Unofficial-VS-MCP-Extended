#Requires -Version 7.0

<#
.SYNOPSIS
    Unofficial-VS-MCP-Extended の診断 export（ZIP または events.jsonl）をローカルで解析する。

.DESCRIPTION
    diagnostics_export が出力した ZIP、または events.jsonl を読み、次の 3 ファイルを出力する。

      analysis-summary.json : 集計結果（機械可読）
      analysis-summary.md   : 同じ数値の要約（人間可読）
      suspects.jsonl        : 疑わしい correlationId の元ログ行（原文のまま）

    ネットワークアクセスは行わない（完全にローカル処理）。
    出力へ載せる本文は JSONL に既に sanitize 済みで入っている値だけで、
    伏字の復元・パスの復元・キー名からの推測は行わない（元の sanitizer 方針を維持する）。
    入力の場所は既定でファイル名だけを出力へ載せる（フルパスは -IncludePaths と実行ログに限る）。

.PARAMETER InputPath
    解析対象の export ZIP、または events.jsonl のパス。

.PARAMETER OutputDirectory
    出力先ディレクトリ。省略時は入力と同じ場所の analysis-<yyyyMMdd-HHmmss>。

.PARAMETER SlowFactor
    そのツールの通常値（成功呼び出しの p50）の何倍を「遅い」とみなすか。既定は 3.0。
    1000 ms 未満の呼び出しと、成功が 2 件に満たないツールは slow 判定の対象にしない。

.PARAMETER MaxSuspects
    suspects.jsonl へ書き出す correlationId の上限。既定は 50。

.PARAMETER IncludePaths
    analysis-summary.json / .md へ入力のフルパスを載せる。既定（未指定）はファイル名だけを載せる。
    出力はバグ報告に添付される前提なので、フォルダー構成（ユーザー名を含み得る）は既定で出さない。
    実行ログ（Write-Host）には常にフルパスを出す。

.EXAMPLE
    pwsh -NoProfile -File tools\Analyze-Diagnostics.ps1 -InputPath "$env:LOCALAPPDATA\Unofficial-VS-MCP-Extended\Exports\diagnostics-20260910-174333-e-fe4abaa3.zip"
#>

param(
    [Parameter(Mandatory = $true)]
    [string]$InputPath,

    [Parameter(Mandatory = $false)]
    [string]$OutputDirectory = "",

    [Parameter(Mandatory = $false)]
    [double]$SlowFactor = 3.0,

    [Parameter(Mandatory = $false)]
    [int]$MaxSuspects = 50,

    [Parameter(Mandatory = $false)]
    [switch]$IncludePaths
)

$ErrorActionPreference = "Stop"

# ---------------------------------------------------------------------------
# ヘルパー
# ---------------------------------------------------------------------------

<#
.SYNOPSIS
    PSCustomObject から指定したプロパティの値を取り出す（無ければ $null）。
.PARAMETER InObject
    対象のオブジェクト。$null を渡してもよい。
.PARAMETER InName
    プロパティ名。
.OUTPUTS
    プロパティの値。存在しない場合は $null。
#>
function Get-EventProperty
{
    param(
        [Parameter(Mandatory = $true)]
        [AllowNull()]
        [object]$InObject,

        [Parameter(Mandatory = $true)]
        [string]$InName
    )

    if ($null -eq $InObject)
    {
        return $null
    }

    $TheProperty = $InObject.PSObject.Properties[$InName]
    if ($null -eq $TheProperty)
    {
        return $null
    }

    return $TheProperty.Value
}

<#
.SYNOPSIS
    時刻値を ISO 8601（UTC）の文字列へ揃える。
.DESCRIPTION
    events.jsonl の timestampUtc は ISO 8601 の UTC 文字列だが、ConvertFrom-Json は DateTime へ変換する。
    そのまま文字列化するとロケール依存の表記になり、元のログと突き合わせられなくなるため必ず "o" 形式へ戻す。
.PARAMETER InValue
    時刻を表す値（DateTime / DateTimeOffset / 文字列）。
.OUTPUTS
    ISO 8601（UTC）の文字列。値が無い場合は $null。
#>
function ConvertTo-Iso8601Text
{
    param(
        [Parameter(Mandatory = $true)]
        [AllowNull()]
        [object]$InValue
    )

    if ($null -eq $InValue)
    {
        return $null
    }

    if ($InValue -is [DateTimeOffset])
    {
        return ([DateTimeOffset]$InValue).ToUniversalTime().ToString("o")
    }

    if ($InValue -is [DateTime])
    {
        $TheDateTime = [DateTime]$InValue
        if ($TheDateTime.Kind -eq [System.DateTimeKind]::Unspecified)
        {
            # JSONL の timestampUtc は UTC 表記なので、種別が付いていない値は UTC として扱う
            $TheDateTime = [DateTime]::SpecifyKind($TheDateTime, [System.DateTimeKind]::Utc)
        }
        return $TheDateTime.ToUniversalTime().ToString("o")
    }

    return [string]$InValue
}

<#
.SYNOPSIS
    JSON から読んだ値を [long] へ変換する（数値として読めなければ $null）。
.DESCRIPTION
    events.jsonl は外部入力であり、sequence / elapsedMs が文字列や小数で入っていることがある。
    [long] への直接キャストは例外で解析全体を止めてしまうため、必ず TryParse で判定する。
    呼び出し側は「元の値があるのに $null が返った」ことで壊れた項目を数える。
.PARAMETER InValue
    変換する値。$null を渡してもよい。
.OUTPUTS
    変換した [long]。値が無い、または数値として読めない場合は $null。
#>
function ConvertTo-LongOrNull
{
    param(
        [Parameter(Mandatory = $true)]
        [AllowNull()]
        [object]$InValue
    )

    if ($null -eq $InValue)
    {
        return $null
    }

    [long]$TheParsed = 0
    if ([long]::TryParse([string]$InValue, [ref]$TheParsed))
    {
        return $TheParsed
    }

    return $null
}

<#
.SYNOPSIS
    数値配列のパーセンタイルを nearest-rank 法（順位 = ceil(p × 件数)）で求める。
.PARAMETER InValues
    対象の値。空なら $null を返す。
.PARAMETER InPercentile
    0.0〜1.0 の割合（p50 なら 0.5）。
.OUTPUTS
    パーセンタイル値。対象が空の場合は $null。
#>
function Get-PercentileValue
{
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [long[]]$InValues,

        [Parameter(Mandatory = $true)]
        [double]$InPercentile
    )

    if ($InValues.Count -eq 0)
    {
        return $null
    }

    [long[]]$TheSortedValues = $InValues | Sort-Object
    [int]$TheRank = [Math]::Ceiling($InPercentile * $TheSortedValues.Count)
    if ($TheRank -lt 1)
    {
        $TheRank = 1
    }
    if ($TheRank -gt $TheSortedValues.Count)
    {
        $TheRank = $TheSortedValues.Count
    }

    return $TheSortedValues[$TheRank - 1]
}

<#
.SYNOPSIS
    sequence の昇順に並べた配列から、指定した sequence 範囲（両端を含む）の event を取り出す。
.DESCRIPTION
    マーカー区間ごとに全 event を線形に絞ると区間数 × 件数の総当たりになるため、
    並べ替え済みの sequence 配列を二分探索して開始位置だけを求め、そこから範囲外になるまで進める。
.PARAMETER InSortedRecords
    sequence 昇順に並べた event の配列。
.PARAMETER InSortedSequences
    InSortedRecords と同じ並びの sequence 値の配列（二分探索用）。
.PARAMETER InFromSequence
    範囲の下限（この値自身を含む）。
.PARAMETER InToSequence
    範囲の上限（この値自身を含む）。
.OUTPUTS
    範囲内の event のリスト。1 件も無ければ空。
#>
function Get-RecordsInSequenceRange
{
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [object[]]$InSortedRecords,

        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [long[]]$InSortedSequences,

        [Parameter(Mandatory = $true)]
        [long]$InFromSequence,

        [Parameter(Mandatory = $true)]
        [long]$InToSequence
    )

    $TheResults = [System.Collections.Generic.List[object]]::new()
    if ($InSortedRecords.Count -eq 0)
    {
        return $TheResults
    }

    [int]$TheStartIndex = [Array]::BinarySearch($InSortedSequences, $InFromSequence)
    if ($TheStartIndex -lt 0)
    {
        # 見つからない場合は「その値を入れるべき位置」の補数が返る
        $TheStartIndex = -$TheStartIndex - 1
    }
    else
    {
        # 同じ sequence が複数ある場合に取りこぼさないよう、最初の 1 件まで戻す
        while ($TheStartIndex -gt 0 -and $InSortedSequences[$TheStartIndex - 1] -eq $InFromSequence)
        {
            $TheStartIndex--
        }
    }

    for ([int]$TheIndex = $TheStartIndex; $TheIndex -lt $InSortedRecords.Count; $TheIndex++)
    {
        if ($InSortedSequences[$TheIndex] -gt $InToSequence)
        {
            break
        }
        $TheResults.Add($InSortedRecords[$TheIndex])
    }

    return $TheResults
}

<#
.SYNOPSIS
    suspect の理由を correlationId ごとに追記する（同じ理由は 1 度だけ）。
.PARAMETER InSuspectMap
    correlationId をキー、理由のリストを値とする辞書（参照渡しで更新される）。
.PARAMETER InCorrelationId
    対象の correlationId。
.PARAMETER InReason
    追記する理由（error / timeout / errorDump / warning / fallback / slow）。
#>
function Add-SuspectReason
{
    param(
        [Parameter(Mandatory = $true)]
        [System.Collections.IDictionary]$InSuspectMap,

        [Parameter(Mandatory = $true)]
        [string]$InCorrelationId,

        [Parameter(Mandatory = $true)]
        [string]$InReason
    )

    if (-not $InSuspectMap.Contains($InCorrelationId))
    {
        $InSuspectMap[$InCorrelationId] = [System.Collections.Generic.List[string]]::new()
    }

    if (-not $InSuspectMap[$InCorrelationId].Contains($InReason))
    {
        $InSuspectMap[$InCorrelationId].Add($InReason)
    }
}

<#
.SYNOPSIS
    suspects.jsonl へ含める event 名かどうかを判定する。
.DESCRIPTION
    tool.start / tool.end / diagnostics.errorDump / exception と、
    window. / modal. / menu. / uia. / input. / wait. / capture. / interaction. /
    geometry. / dialog. / fileDialog. で始まる event だけを含める。
    診断基盤自身の内部 event（diagnostics.mark / .export など）と performance.tool は含めない。
.PARAMETER InEventName
    判定する event 名。
.OUTPUTS
    含めるなら $true。
#>
function Test-IsSuspectEventName
{
    param(
        [Parameter(Mandatory = $true)]
        [AllowNull()]
        [AllowEmptyString()]
        [string]$InEventName
    )

    if ([string]::IsNullOrEmpty($InEventName))
    {
        return $false
    }

    if ($InEventName -eq "tool.start" -or $InEventName -eq "tool.end" -or $InEventName -eq "diagnostics.errorDump" -or $InEventName -eq "exception")
    {
        return $true
    }

    # 大文字小文字はログの表記そのまま（fileDialog. の D は大文字）で、序数比較する
    foreach ($ThePrefix in @("window.", "modal.", "menu.", "uia.", "input.", "wait.", "capture.", "interaction.", "geometry.", "dialog.", "fileDialog.", "exception."))
    {
        if ($InEventName.StartsWith($ThePrefix, [System.StringComparison]::Ordinal))
        {
            return $true
        }
    }

    return $false
}

<#
.SYNOPSIS
    Markdown の表セルへ値を埋め込める形へ整える（改行と縦棒を潰し、長ければ切る）。
.PARAMETER InValue
    セルへ入れる値。$null は "-" になる。
.PARAMETER InMaxLength
    最大文字数。超える分は切って末尾に … を付ける。
.OUTPUTS
    表セル用の文字列。
#>
function Format-MarkdownCell
{
    param(
        [Parameter(Mandatory = $true)]
        [AllowNull()]
        [object]$InValue,

        [Parameter(Mandatory = $false)]
        [int]$InMaxLength = 100
    )

    if ($null -eq $InValue)
    {
        return "-"
    }

    [string]$TheText = [string]$InValue
    if ($TheText.Length -eq 0)
    {
        return "-"
    }

    $TheText = $TheText.Replace("`r", " ").Replace("`n", " ").Replace("|", "\|")
    if ($TheText.Length -gt $InMaxLength)
    {
        $TheText = $TheText.Substring(0, $InMaxLength) + "…"
    }

    return $TheText
}

<#
.SYNOPSIS
    manifest の値と、解析結果との照合結果を Markdown の 1 セルへ整える。
.PARAMETER InValue
    manifest 側の値。manifest がない、または項目が無ければ $null。
.PARAMETER InIsMatch
    照合結果。照合できなかった場合は $null。
.OUTPUTS
    "64（一致）" のような文字列。値が無ければ "-"。
#>
function Format-ManifestCell
{
    param(
        [Parameter(Mandatory = $true)]
        [AllowNull()]
        [object]$InValue,

        [Parameter(Mandatory = $true)]
        [AllowNull()]
        [object]$InIsMatch
    )

    if ($null -eq $InValue)
    {
        return "-"
    }

    if ($null -eq $InIsMatch)
    {
        return (Format-MarkdownCell -InValue $InValue)
    }

    $TheMatchText = if ([bool]$InIsMatch) { "一致" } else { "不一致" }
    return "$(Format-MarkdownCell -InValue $InValue)（$TheMatchText）"
}

# ---------------------------------------------------------------------------
# 引数の検証と入出力の解決
# ---------------------------------------------------------------------------

if ($SlowFactor -le 0)
{
    throw "SlowFactor must be greater than 0: $SlowFactor"
}

if ($MaxSuspects -lt 1)
{
    throw "MaxSuspects must be 1 or greater: $MaxSuspects"
}

$TheInputItem = Get-Item -LiteralPath $InputPath -ErrorAction SilentlyContinue
if ($null -eq $TheInputItem -or $TheInputItem.PSIsContainer)
{
    throw "Input file not found (expected a diagnostics export .zip or events.jsonl): $InputPath"
}

$TheInputFullPath = $TheInputItem.FullName
$TheInputDirectory = $TheInputItem.DirectoryName

# 対応していない入力で出力先だけ作らないよう、種別は先に確かめる
$TheExtension = [System.IO.Path]::GetExtension($TheInputFullPath).ToLowerInvariant()
if ($TheExtension -ne ".zip" -and $TheExtension -ne ".jsonl")
{
    throw "Unsupported input type (expected .zip or .jsonl): $TheInputFullPath"
}

# 出力先。相対パスは現在のディレクトリ基準で解決する
if ([string]::IsNullOrWhiteSpace($OutputDirectory))
{
    $TheOutputDirectory = Join-Path -Path $TheInputDirectory -ChildPath ("analysis-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
}
elseif ([System.IO.Path]::IsPathRooted($OutputDirectory))
{
    $TheOutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
}
else
{
    $TheOutputDirectory = [System.IO.Path]::GetFullPath((Join-Path -Path (Get-Location).ProviderPath -ChildPath $OutputDirectory))
}

New-Item -ItemType Directory -Path $TheOutputDirectory -Force | Out-Null

# ZIP なら出力先の extracted\ へ展開し、そこから events.jsonl / manifest.json を読む
if ($TheExtension -eq ".zip")
{
    $TheExtractDirectory = Join-Path -Path $TheOutputDirectory -ChildPath "extracted"
    New-Item -ItemType Directory -Path $TheExtractDirectory -Force | Out-Null
    Expand-Archive -LiteralPath $TheInputFullPath -DestinationPath $TheExtractDirectory -Force

    $TheEventsPath = Join-Path -Path $TheExtractDirectory -ChildPath "events.jsonl"
    $TheManifestPath = Join-Path -Path $TheExtractDirectory -ChildPath "manifest.json"

    if (-not (Test-Path -LiteralPath $TheEventsPath -PathType Leaf))
    {
        throw "events.jsonl not found in archive: $TheInputFullPath"
    }
}
else
{
    # events.jsonl 直接指定。manifest.json が同じ場所にあれば補助情報として読む
    $TheEventsPath = $TheInputFullPath
    $TheManifestPath = Join-Path -Path $TheInputDirectory -ChildPath "manifest.json"
}

# manifest は補助情報なので、壊れていても解析は続ける
$TheManifest = $null
if (Test-Path -LiteralPath $TheManifestPath -PathType Leaf)
{
    try
    {
        $TheManifest = ConvertFrom-Json -InputObject ([System.IO.File]::ReadAllText($TheManifestPath))
    }
    catch
    {
        Write-Warning "manifest.json could not be parsed: $($_.Exception.Message)"
        $TheManifest = $null
    }
}

# 出力ファイルへ載せるパス表記。既定はファイル名だけにする（出力はバグ報告へ添付される前提で、
# 置き場所のフォルダー構成にはユーザー名が入り得るため）。フルパスは -IncludePaths と実行ログにだけ出す
$TheInputDisplayPath = if ($IncludePaths) { $TheInputFullPath } else { [System.IO.Path]::GetFileName($TheInputFullPath) }
$TheEventsDisplayPath = if ($IncludePaths) { $TheEventsPath } else { [System.IO.Path]::GetFileName($TheEventsPath) }

# ---------------------------------------------------------------------------
# JSONL の読み込み（1 行 1 event。読めない行は数えて飛ばす）
# ---------------------------------------------------------------------------

[string[]]$TheLines = [System.IO.File]::ReadAllLines($TheEventsPath)
$TheRecords = [System.Collections.Generic.List[object]]::new()
[int]$TheSkippedLineCount = 0
[int]$TheMalformedFieldCount = 0
[int]$TheLineNumber = 0

foreach ($TheLine in $TheLines)
{
    $TheLineNumber++
    if ([string]::IsNullOrWhiteSpace($TheLine))
    {
        continue
    }

    $TheJson = $null
    try
    {
        $TheJson = ConvertFrom-Json -InputObject $TheLine
    }
    catch
    {
        $TheSkippedLineCount++
        continue
    }

    # 1 行あたり十数回の関数呼び出しは 20,000 行規模では読み込み時間の大半を占めるため、
    # この読み込みループだけは Get-EventProperty を通さず、値を 1 度ハッシュテーブルへ写して引く
    # （PowerShell のハッシュテーブルは無いキーに $null を返すので、欠けた項目の扱いは同じになる）
    $TheValues = @{}
    foreach ($TheProperty in $TheJson.PSObject.Properties)
    {
        $TheValues[$TheProperty.Name] = $TheProperty.Value
    }

    # sequence が無い / 数値として読めない行でも解析を止めない（行番号で代用し、壊れた項目として数える）
    $TheSequenceValue = $TheValues["sequence"]
    $TheParsedSequence = $null
    if ($null -ne $TheSequenceValue)
    {
        $TheParsedSequence = ConvertTo-LongOrNull -InValue $TheSequenceValue
        if ($null -eq $TheParsedSequence)
        {
            $TheMalformedFieldCount++
        }
    }
    [long]$TheSequence = if ($null -eq $TheParsedSequence) { [long]$TheLineNumber } else { $TheParsedSequence }

    $TheElapsedValue = $TheValues["elapsedMs"]
    $TheElapsedMs = $null
    if ($null -ne $TheElapsedValue)
    {
        $TheElapsedMs = ConvertTo-LongOrNull -InValue $TheElapsedValue
        if ($null -eq $TheElapsedMs)
        {
            $TheMalformedFieldCount++
        }
    }

    $TheRecords.Add([pscustomobject]@{
        Sequence      = $TheSequence
        TimestampUtc  = [string](ConvertTo-Iso8601Text -InValue $TheValues["timestampUtc"])
        SessionId     = [string]$TheValues["sessionId"]
        CorrelationId = [string]$TheValues["correlationId"]
        Level         = [string]$TheValues["level"]
        Category      = [string]$TheValues["category"]
        EventName     = [string]$TheValues["event"]
        Tool          = [string]$TheValues["tool"]
        Result        = [string]$TheValues["result"]
        Message       = [string]$TheValues["message"]
        ElapsedMs     = $TheElapsedMs
        Data          = $TheValues["data"]
        RawLine       = $TheLine
    })
}

if ($TheSkippedLineCount -gt 0)
{
    Write-Warning "$TheSkippedLineCount line(s) could not be parsed as JSON and were skipped."
}

if ($TheRecords.Count -eq 0)
{
    Write-Warning "No diagnostic events were parsed from: $TheEventsPath"
}

# ---------------------------------------------------------------------------
# 索引（読み込み直後に 1 パスで作る）
#   correlationId → その correlationId の event（sequence 昇順）
#   tool          → その tool の tool.end
#   sequence 昇順 → マーカー区間の二分探索用
# suspect 抽出・Tool 別統計・区間集計はここを参照する（件数 × 対象数の総当たりを避けるため）
# ---------------------------------------------------------------------------

# Sort-Object は安定なので、同じ sequence の event は元の行順のまま残る
$TheRecordsBySequence = @($TheRecords | Sort-Object -Property Sequence)
[long[]]$TheSortedSequences = @($TheRecordsBySequence | ForEach-Object { $_.Sequence })

$TheRecordsByCorrelation = [System.Collections.Generic.Dictionary[string, System.Collections.Generic.List[object]]]::new([System.StringComparer]::Ordinal)
$TheToolEndsByTool = [System.Collections.Generic.Dictionary[string, System.Collections.Generic.List[object]]]::new([System.StringComparer]::Ordinal)
$TheToolEndRecords = [System.Collections.Generic.List[object]]::new()

# sequence 昇順の並びから索引を作るので、correlationId ごとのリストも sequence 昇順になる
foreach ($TheRecord in $TheRecordsBySequence)
{
    if (-not [string]::IsNullOrEmpty($TheRecord.CorrelationId))
    {
        if (-not $TheRecordsByCorrelation.ContainsKey($TheRecord.CorrelationId))
        {
            $TheRecordsByCorrelation[$TheRecord.CorrelationId] = [System.Collections.Generic.List[object]]::new()
        }
        $TheRecordsByCorrelation[$TheRecord.CorrelationId].Add($TheRecord)
    }

    if ($TheRecord.EventName -eq "tool.end")
    {
        $TheToolEndRecords.Add($TheRecord)

        $TheToolKey = if ([string]::IsNullOrEmpty($TheRecord.Tool)) { "(unknown)" } else { $TheRecord.Tool }
        if (-not $TheToolEndsByTool.ContainsKey($TheToolKey))
        {
            $TheToolEndsByTool[$TheToolKey] = [System.Collections.Generic.List[object]]::new()
        }
        $TheToolEndsByTool[$TheToolKey].Add($TheRecord)
    }
}

# ---------------------------------------------------------------------------
# セッションと全体の集計
# ---------------------------------------------------------------------------

$TheSessionMap = [ordered]@{}
[int]$TheErrorCount = 0
[int]$TheWarningCount = 0

foreach ($TheRecord in $TheRecords)
{
    if ($TheRecord.Level -eq "error")
    {
        $TheErrorCount++
    }
    elseif ($TheRecord.Level -eq "warning")
    {
        $TheWarningCount++
    }

    $TheSessionId = if ([string]::IsNullOrEmpty($TheRecord.SessionId)) { "(unknown)" } else { $TheRecord.SessionId }
    if (-not $TheSessionMap.Contains($TheSessionId))
    {
        $TheSessionMap[$TheSessionId] = [ordered]@{
            sessionId  = $TheSessionId
            startUtc   = $null
            endUtc     = $null
            eventCount = 0
        }
    }

    $TheSessionEntry = $TheSessionMap[$TheSessionId]
    $TheSessionEntry["eventCount"] = $TheSessionEntry["eventCount"] + 1

    # timestampUtc は ISO 8601 の UTC 固定長なので、文字列比較で時刻順に並ぶ
    if (-not [string]::IsNullOrEmpty($TheRecord.TimestampUtc))
    {
        if ($null -eq $TheSessionEntry["startUtc"] -or $TheRecord.TimestampUtc -lt $TheSessionEntry["startUtc"])
        {
            $TheSessionEntry["startUtc"] = $TheRecord.TimestampUtc
        }
        if ($null -eq $TheSessionEntry["endUtc"] -or $TheRecord.TimestampUtc -gt $TheSessionEntry["endUtc"])
        {
            $TheSessionEntry["endUtc"] = $TheRecord.TimestampUtc
        }
    }
}

# 捨てた event の件数は session.end にしか載らない（export に含まれていなければ不明のまま null）
$TheDroppedEventCount = $null
foreach ($TheRecord in $TheRecords)
{
    if ($TheRecord.EventName -eq "session.end")
    {
        $TheDroppedValue = Get-EventProperty -InObject $TheRecord.Data -InName "droppedEventCount"
        if ($null -ne $TheDroppedValue)
        {
            $TheParsedDropped = ConvertTo-LongOrNull -InValue $TheDroppedValue
            if ($null -eq $TheParsedDropped)
            {
                $TheMalformedFieldCount++
            }
            else
            {
                $TheDroppedEventCount = $TheParsedDropped
            }
        }
    }
}

# 数値として読めなかった項目（sequence / elapsedMs / droppedEventCount）はここまでで出そろう
if ($TheMalformedFieldCount -gt 0)
{
    Write-Warning "$TheMalformedFieldCount numeric field(s) could not be read as a number and were treated as missing."
}

# ---------------------------------------------------------------------------
# Tool 別統計（tool.end の result と elapsedMs から）
# ---------------------------------------------------------------------------

$TheToolNames = @($TheToolEndsByTool.Keys | Sort-Object)

$TheToolStatistics = [ordered]@{}
$TheToolP50SuccessMap = @{}
$TheToolSuccessCountMap = @{}

foreach ($TheToolName in $TheToolNames)
{
    $TheToolRecords = $TheToolEndsByTool[$TheToolName]

    $TheElapsedList = [System.Collections.Generic.List[long]]::new()
    # 失敗して即返った呼び出しに通常値を引き下げられないよう、slow 判定用の p50 は成功呼び出しだけから取る
    $TheSuccessElapsedList = [System.Collections.Generic.List[long]]::new()
    [int]$TheSuccessCount = 0
    [int]$TheErrorCountForTool = 0
    [int]$TheTimeoutCount = 0
    [int]$TheNormalFalseCount = 0
    [int]$TheCancelledCount = 0

    # result 内訳と elapsedMs は同じ 1 パスで数える（result ごとに全件を絞り直さない）
    foreach ($TheToolRecord in $TheToolRecords)
    {
        if ($null -ne $TheToolRecord.ElapsedMs)
        {
            $TheElapsedList.Add([long]$TheToolRecord.ElapsedMs)
        }

        switch ($TheToolRecord.Result)
        {
            "success"
            {
                $TheSuccessCount++
                if ($null -ne $TheToolRecord.ElapsedMs)
                {
                    $TheSuccessElapsedList.Add([long]$TheToolRecord.ElapsedMs)
                }
            }
            "error" { $TheErrorCountForTool++ }
            "timeout" { $TheTimeoutCount++ }
            "normalFalse" { $TheNormalFalseCount++ }
            "cancelled" { $TheCancelledCount++ }
        }
    }

    [long[]]$TheElapsedValues = $TheElapsedList.ToArray()
    [long[]]$TheSuccessElapsedValues = $TheSuccessElapsedList.ToArray()

    $TheP50 = Get-PercentileValue -InValues $TheElapsedValues -InPercentile 0.5
    $TheP95 = Get-PercentileValue -InValues $TheElapsedValues -InPercentile 0.95
    $TheMax = if ($TheElapsedValues.Count -eq 0) { $null } else { [long]($TheElapsedValues | Measure-Object -Maximum).Maximum }
    $TheP50Success = Get-PercentileValue -InValues $TheSuccessElapsedValues -InPercentile 0.5

    $TheToolStatistics[$TheToolName] = [ordered]@{
        calls       = $TheToolRecords.Count
        success     = $TheSuccessCount
        error       = $TheErrorCountForTool
        timeout     = $TheTimeoutCount
        normalFalse = $TheNormalFalseCount
        cancelled   = $TheCancelledCount
        # 既知の 5 種以外（result が無い / 未知の値）の件数。合計が calls と合わない原因をここで見せる
        other       = $TheToolRecords.Count - ($TheSuccessCount + $TheErrorCountForTool + $TheTimeoutCount + $TheNormalFalseCount + $TheCancelledCount)
        p50         = $TheP50
        p50Success  = $TheP50Success
        p95         = $TheP95
        max         = $TheMax
    }

    $TheToolP50SuccessMap[$TheToolName] = $TheP50Success
    $TheToolSuccessCountMap[$TheToolName] = $TheSuccessElapsedValues.Count
}

# ---------------------------------------------------------------------------
# slow operations（elapsedMs > 成功呼び出しの p50 × SlowFactor かつ 1000 ms 以上）
# ---------------------------------------------------------------------------

# 1 秒未満の呼び出しは、比が大きくても遅いとは扱わない
$TheSlowMinimumElapsedMs = 1000

# 成功呼び出しがこの件数に満たないツールは、通常値が定まらないので slow 判定をしない
$TheSlowMinimumSuccessCount = 2

$TheSlowOperations = [System.Collections.Generic.List[object]]::new()
foreach ($TheRecord in $TheToolEndRecords)
{
    if ($null -eq $TheRecord.ElapsedMs -or $TheRecord.ElapsedMs -lt $TheSlowMinimumElapsedMs)
    {
        continue
    }

    $TheToolName = if ([string]::IsNullOrEmpty($TheRecord.Tool)) { "(unknown)" } else { $TheRecord.Tool }
    $TheSuccessCount = $TheToolSuccessCountMap[$TheToolName]
    if ($null -eq $TheSuccessCount -or $TheSuccessCount -lt $TheSlowMinimumSuccessCount)
    {
        continue
    }

    $TheP50Success = $TheToolP50SuccessMap[$TheToolName]
    if ($null -eq $TheP50Success)
    {
        continue
    }

    if ($TheRecord.ElapsedMs -le ([double]$TheP50Success * $SlowFactor))
    {
        continue
    }

    # p50 が 0 ms のツールは倍率を出せないので null にする
    $TheFactor = if ($TheP50Success -gt 0) { [Math]::Round([double]$TheRecord.ElapsedMs / [double]$TheP50Success, 1) } else { $null }

    $TheSlowOperations.Add([ordered]@{
        tool          = $TheToolName
        correlationId = $TheRecord.CorrelationId
        elapsedMs     = $TheRecord.ElapsedMs
        p50           = $TheP50Success
        factor        = $TheFactor
    })
}

$TheSortedSlowOperations = @($TheSlowOperations | Sort-Object -Property @{ Expression = { $_["elapsedMs"] }; Descending = $true })

# ---------------------------------------------------------------------------
# fallback と warning の集計
# ---------------------------------------------------------------------------

$TheMenuFallbackCounts = [ordered]@{ ownerSubtree = 0; outsideClick = 0; win32Rect = 0 }
# interaction.fallback は menu.fallback と kind の体系が違うので、同じ数え方で別の表に持つ
$TheInteractionFallbackCounts = [ordered]@{}
$TheWarningEventCounts = @{}

foreach ($TheRecord in $TheRecords)
{
    if ($TheRecord.EventName -eq "menu.fallback")
    {
        $TheKindValue = Get-EventProperty -InObject $TheRecord.Data -InName "kind"
        $TheKind = if ([string]::IsNullOrEmpty([string]$TheKindValue)) { "(unknown)" } else { [string]$TheKindValue }
        if (-not $TheMenuFallbackCounts.Contains($TheKind))
        {
            $TheMenuFallbackCounts[$TheKind] = 0
        }
        $TheMenuFallbackCounts[$TheKind] = $TheMenuFallbackCounts[$TheKind] + 1
    }

    if ($TheRecord.EventName -eq "interaction.fallback")
    {
        $TheKindValue = Get-EventProperty -InObject $TheRecord.Data -InName "kind"
        $TheKind = if ([string]::IsNullOrEmpty([string]$TheKindValue)) { "(unknown)" } else { [string]$TheKindValue }
        if (-not $TheInteractionFallbackCounts.Contains($TheKind))
        {
            $TheInteractionFallbackCounts[$TheKind] = 0
        }
        $TheInteractionFallbackCounts[$TheKind] = $TheInteractionFallbackCounts[$TheKind] + 1
    }

    if ($TheRecord.Level -eq "warning")
    {
        $TheWarningEventName = if ([string]::IsNullOrEmpty($TheRecord.EventName)) { "(unknown)" } else { $TheRecord.EventName }
        if (-not $TheWarningEventCounts.ContainsKey($TheWarningEventName))
        {
            $TheWarningEventCounts[$TheWarningEventName] = 0
        }
        $TheWarningEventCounts[$TheWarningEventName] = $TheWarningEventCounts[$TheWarningEventName] + 1
    }
}

# 件数の多い順、同数なら event 名順で並べる
$TheWarningEventTable = [ordered]@{}
foreach ($TheWarningEventName in @($TheWarningEventCounts.Keys | Sort-Object -Property @{ Expression = { $TheWarningEventCounts[$_] }; Descending = $true }, @{ Expression = { $_ } }))
{
    $TheWarningEventTable[$TheWarningEventName] = $TheWarningEventCounts[$TheWarningEventName]
}

# ---------------------------------------------------------------------------
# マーカーと区間
# ---------------------------------------------------------------------------

$TheMarkers = [System.Collections.Generic.List[object]]::new()
foreach ($TheRecord in @($TheRecords | Where-Object { $_.EventName -eq "diagnostics.mark" } | Sort-Object -Property Sequence))
{
    # message は event 本体に載る。古い形式のために data.message も見る
    $TheMarkerMessage = $TheRecord.Message
    if ([string]::IsNullOrEmpty($TheMarkerMessage))
    {
        $TheMarkerMessage = [string](Get-EventProperty -InObject $TheRecord.Data -InName "message")
    }

    $TheMarkers.Add([ordered]@{
        message       = $TheMarkerMessage
        sequence      = $TheRecord.Sequence
        timestampUtc  = $TheRecord.TimestampUtc
        correlationId = $TheRecord.CorrelationId
        markerId      = [string](Get-EventProperty -InObject $TheRecord.Data -InName "markerId")
    })
}

# 連続する 2 マーカーの間（両端のマーカー自身を含む sequence 範囲）を 1 区間として数える
$TheMarkerRanges = [System.Collections.Generic.List[object]]::new()
for ($TheMarkerIndex = 0; $TheMarkerIndex -lt ($TheMarkers.Count - 1); $TheMarkerIndex++)
{
    [long]$TheFromSequence = $TheMarkers[$TheMarkerIndex]["sequence"]
    [long]$TheToSequence = $TheMarkers[$TheMarkerIndex + 1]["sequence"]
    # sequence 昇順の索引から二分探索で取り出す（区間ごとに全 event を絞り直さない）。
    # 取り出した時点で sequence 昇順なので、失敗した tool.end も並べ替えずにそのまま使える
    $TheRangeRecords = @(Get-RecordsInSequenceRange -InSortedRecords $TheRecordsBySequence -InSortedSequences $TheSortedSequences -InFromSequence $TheFromSequence -InToSequence $TheToSequence)

    $TheFailingToolEnds = [System.Collections.Generic.List[object]]::new()
    [int]$TheRangeErrorCount = 0
    [int]$TheRangeWarningCount = 0
    foreach ($TheRangeRecord in $TheRangeRecords)
    {
        if ($TheRangeRecord.Level -eq "error")
        {
            $TheRangeErrorCount++
        }
        elseif ($TheRangeRecord.Level -eq "warning")
        {
            $TheRangeWarningCount++
        }

        if ($TheRangeRecord.EventName -eq "tool.end" -and ($TheRangeRecord.Result -eq "error" -or $TheRangeRecord.Result -eq "timeout"))
        {
            $TheFailingToolEnds.Add([ordered]@{
                tool          = $TheRangeRecord.Tool
                correlationId = $TheRangeRecord.CorrelationId
                message       = $TheRangeRecord.Message
            })
        }
    }

    $TheMarkerRanges.Add([ordered]@{
        fromSequence    = $TheFromSequence
        toSequence      = $TheToSequence
        errorCount      = $TheRangeErrorCount
        warningCount    = $TheRangeWarningCount
        failingToolEnds = @($TheFailingToolEnds)
    })
}

# ---------------------------------------------------------------------------
# suspect correlation（同じ correlationId は 1 件にまとめ、理由を配列で持つ）
# ---------------------------------------------------------------------------

$TheSuspectReasonMap = [ordered]@{}

foreach ($TheRecord in $TheRecords)
{
    if ([string]::IsNullOrEmpty($TheRecord.CorrelationId))
    {
        continue
    }

    if ($TheRecord.EventName -eq "tool.end" -and $TheRecord.Result -eq "error")
    {
        Add-SuspectReason -InSuspectMap $TheSuspectReasonMap -InCorrelationId $TheRecord.CorrelationId -InReason "error"
    }
    if ($TheRecord.EventName -eq "tool.end" -and $TheRecord.Result -eq "timeout")
    {
        Add-SuspectReason -InSuspectMap $TheSuspectReasonMap -InCorrelationId $TheRecord.CorrelationId -InReason "timeout"
    }
    # tool.end 以外の error 水準（exception / diagnostics.serialize.failure など）も見落とさない
    if ($TheRecord.Level -eq "error")
    {
        Add-SuspectReason -InSuspectMap $TheSuspectReasonMap -InCorrelationId $TheRecord.CorrelationId -InReason "error"
    }
    if ($TheRecord.EventName -eq "diagnostics.errorDump")
    {
        Add-SuspectReason -InSuspectMap $TheSuspectReasonMap -InCorrelationId $TheRecord.CorrelationId -InReason "errorDump"
    }
    if ($TheRecord.EventName -eq "menu.fallback" -or $TheRecord.EventName -eq "interaction.fallback")
    {
        Add-SuspectReason -InSuspectMap $TheSuspectReasonMap -InCorrelationId $TheRecord.CorrelationId -InReason "fallback"
    }
    if ($TheRecord.Level -eq "warning")
    {
        Add-SuspectReason -InSuspectMap $TheSuspectReasonMap -InCorrelationId $TheRecord.CorrelationId -InReason "warning"
    }
}

foreach ($TheSlowOperation in $TheSlowOperations)
{
    if (-not [string]::IsNullOrEmpty($TheSlowOperation["correlationId"]))
    {
        Add-SuspectReason -InSuspectMap $TheSuspectReasonMap -InCorrelationId $TheSlowOperation["correlationId"] -InReason "slow"
    }
}

# 理由の重大度（大きいほど先に出す）。MaxSuspects で切られても重い方が残るようにする
$TheReasonSeverityMap = @{ error = 6; timeout = 5; errorDump = 4; fallback = 3; slow = 2; warning = 1 }

$TheSuspects = [System.Collections.Generic.List[object]]::new()
foreach ($TheCorrelationId in $TheSuspectReasonMap.Keys)
{
    if (-not $TheRecordsByCorrelation.ContainsKey($TheCorrelationId))
    {
        continue
    }

    # 索引は sequence 昇順で作ってあるので、ここで並べ替え直さない
    $TheCorrelationRecords = @($TheRecordsByCorrelation[$TheCorrelationId])
    if ($TheCorrelationRecords.Count -eq 0)
    {
        continue
    }

    $TheToolRecord = $TheCorrelationRecords | Where-Object { -not [string]::IsNullOrEmpty($_.Tool) } | Select-Object -First 1
    $TheToolName = if ($null -eq $TheToolRecord) { $null } else { $TheToolRecord.Tool }

    # 理由は重い順に並べる（event の出現順に左右されないようにするため）
    $TheReasons = @($TheSuspectReasonMap[$TheCorrelationId] | Sort-Object -Property @{ Expression = { $TheReasonSeverityMap[$_] }; Descending = $true })
    [int]$TheSeverity = 0
    foreach ($TheReason in $TheReasons)
    {
        if ($TheReasonSeverityMap[$TheReason] -gt $TheSeverity)
        {
            $TheSeverity = $TheReasonSeverityMap[$TheReason]
        }
    }

    $TheSuspects.Add([ordered]@{
        correlationId = $TheCorrelationId
        tool          = $TheToolName
        reasons       = $TheReasons
        sequenceRange = [ordered]@{
            from = $TheCorrelationRecords[0].Sequence
            to   = $TheCorrelationRecords[$TheCorrelationRecords.Count - 1].Sequence
        }
        eventCount    = $TheCorrelationRecords.Count
        severity      = $TheSeverity
    })
}

$TheSortedSuspects = @($TheSuspects | Sort-Object -Property @{ Expression = { $_["severity"] }; Descending = $true }, @{ Expression = { $_["sequenceRange"]["from"] } })

# severity は並べ替え用の内部値なので出力からは落とす
$TheSuspectOutputs = [System.Collections.Generic.List[object]]::new()
foreach ($TheSuspect in $TheSortedSuspects)
{
    $TheSuspectOutputs.Add([ordered]@{
        correlationId = $TheSuspect["correlationId"]
        tool          = $TheSuspect["tool"]
        reasons       = $TheSuspect["reasons"]
        sequenceRange = $TheSuspect["sequenceRange"]
        eventCount    = $TheSuspect["eventCount"]
    })
}

# ---------------------------------------------------------------------------
# suspects.jsonl（元の行をそのまま、sequence 順で書く）
# ---------------------------------------------------------------------------

$TheSuspectLines = [System.Collections.Generic.List[string]]::new()
[int]$TheWrittenSuspectCount = 0

foreach ($TheSuspect in $TheSortedSuspects)
{
    if ($TheWrittenSuspectCount -ge $MaxSuspects)
    {
        break
    }

    $TheCorrelationId = $TheSuspect["correlationId"]
    $TheHeader = [ordered]@{
        _suspect      = $true
        correlationId = $TheCorrelationId
        reasons       = $TheSuspect["reasons"]
    }
    $TheSuspectLines.Add((ConvertTo-Json -InputObject $TheHeader -Depth 5 -Compress))

    if ($TheRecordsByCorrelation.ContainsKey($TheCorrelationId))
    {
        foreach ($TheRecord in $TheRecordsByCorrelation[$TheCorrelationId])
        {
            if (Test-IsSuspectEventName -InEventName $TheRecord.EventName)
            {
                $TheSuspectLines.Add($TheRecord.RawLine)
            }
        }
    }

    $TheWrittenSuspectCount++
}

# MaxSuspects で書き出さなかった correlationId の件数（summary は全件、suspects.jsonl は上限まで）
[int]$TheTruncatedSuspectCount = $TheSuspectOutputs.Count - $TheWrittenSuspectCount

# ---------------------------------------------------------------------------
# analysis-summary.json
# ---------------------------------------------------------------------------

$TheManifestEventCount = if ($null -eq $TheManifest) { $null } else { Get-EventProperty -InObject $TheManifest -InName "eventCount" }
$TheManifestErrorCount = if ($null -eq $TheManifest) { $null } else { Get-EventProperty -InObject $TheManifest -InName "errorCount" }
$TheManifestWarningCount = if ($null -eq $TheManifest) { $null } else { Get-EventProperty -InObject $TheManifest -InName "warningCount" }
$TheManifestSkippedLines = if ($null -eq $TheManifest) { $null } else { Get-EventProperty -InObject $TheManifest -InName "skippedLines" }

# manifest の件数と解析結果の照合。manifest に項目が無ければ $null（照合できない）にする。
# skippedLines は export 時に飛ばした行数、skippedLineCount はこの解析器が飛ばした行数で、
# 正常な export では両方 0 になる。食い違いは「export 後に events.jsonl が壊れた」目印になる
$TheManifestMatches = [ordered]@{
    eventCount   = if ($null -eq $TheManifestEventCount) { $null } else { ([long]$TheManifestEventCount -eq [long]$TheRecords.Count) }
    errorCount   = if ($null -eq $TheManifestErrorCount) { $null } else { ([long]$TheManifestErrorCount -eq [long]$TheErrorCount) }
    warningCount = if ($null -eq $TheManifestWarningCount) { $null } else { ([long]$TheManifestWarningCount -eq [long]$TheWarningCount) }
    skippedLines = if ($null -eq $TheManifestSkippedLines) { $null } else { ([long]$TheManifestSkippedLines -eq [long]$TheSkippedLineCount) }
}

$TheSummary = [ordered]@{
    generatedUtc        = [DateTime]::UtcNow.ToString("o")
    input               = [ordered]@{
        # 既定はファイル名だけ（-IncludePaths を付けたときだけフルパス）
        inputPath            = $TheInputDisplayPath
        eventsFile           = $TheEventsDisplayPath
        includePaths         = [bool]$IncludePaths
        manifestEventCount   = $TheManifestEventCount
        manifestErrorCount   = $TheManifestErrorCount
        manifestWarningCount = $TheManifestWarningCount
        manifestSkippedLines = $TheManifestSkippedLines
        manifestMatches      = $TheManifestMatches
        skippedLineCount     = $TheSkippedLineCount
        malformedFieldCount  = $TheMalformedFieldCount
        slowFactor           = $SlowFactor
        maxSuspects          = $MaxSuspects
    }
    sessions            = @($TheSessionMap.Values)
    eventCount          = $TheRecords.Count
    errorCount          = $TheErrorCount
    warningCount        = $TheWarningCount
    droppedEventCount   = $TheDroppedEventCount
    tools               = $TheToolStatistics
    slowOperations      = @($TheSortedSlowOperations)
    fallbacks           = [ordered]@{
        menuFallback        = $TheMenuFallbackCounts
        interactionFallback = $TheInteractionFallbackCounts
        warningEvents       = $TheWarningEventTable
    }
    markers             = @($TheMarkers)
    markerRanges        = @($TheMarkerRanges)
    suspectsWritten     = $TheWrittenSuspectCount
    suspectsTruncated   = $TheTruncatedSuspectCount
    suspectCorrelations = @($TheSuspectOutputs)
}

# ---------------------------------------------------------------------------
# analysis-summary.md
# ---------------------------------------------------------------------------

$TheSummaryJsonPath = Join-Path -Path $TheOutputDirectory -ChildPath "analysis-summary.json"
$TheSummaryMarkdownPath = Join-Path -Path $TheOutputDirectory -ChildPath "analysis-summary.md"
$TheSuspectsPath = Join-Path -Path $TheOutputDirectory -ChildPath "suspects.jsonl"

$TheMarkdownLines = [System.Collections.Generic.List[string]]::new()
$TheMarkdownLines.Add("# 診断ログ解析結果")
$TheMarkdownLines.Add("")
$TheMarkdownLines.Add("生成: $($TheSummary["generatedUtc"])（ローカル処理のみ・ネットワークアクセスなし）")
$TheMarkdownLines.Add("")

$TheMarkdownLines.Add("## 入力")
$TheMarkdownLines.Add("")
$TheMarkdownLines.Add("| 項目 | 値 |")
$TheMarkdownLines.Add("|---|---|")
$TheMarkdownLines.Add("| 入力 | $(Format-MarkdownCell -InValue $TheInputDisplayPath -InMaxLength 200) |")
$TheMarkdownLines.Add("| events.jsonl | $(Format-MarkdownCell -InValue $TheEventsDisplayPath -InMaxLength 200) |")
$TheMarkdownLines.Add("| manifest の eventCount | $(Format-ManifestCell -InValue $TheManifestEventCount -InIsMatch $TheManifestMatches["eventCount"]) |")
$TheMarkdownLines.Add("| manifest の errorCount | $(Format-ManifestCell -InValue $TheManifestErrorCount -InIsMatch $TheManifestMatches["errorCount"]) |")
$TheMarkdownLines.Add("| manifest の warningCount | $(Format-ManifestCell -InValue $TheManifestWarningCount -InIsMatch $TheManifestMatches["warningCount"]) |")
$TheMarkdownLines.Add("| manifest の skippedLines | $(Format-ManifestCell -InValue $TheManifestSkippedLines -InIsMatch $TheManifestMatches["skippedLines"]) |")
$TheMarkdownLines.Add("| 読み飛ばした行 | $TheSkippedLineCount |")
$TheMarkdownLines.Add("| 数値として読めなかった項目 | $TheMalformedFieldCount |")
$TheMarkdownLines.Add("| SlowFactor | $SlowFactor |")
$TheMarkdownLines.Add("| MaxSuspects | $MaxSuspects |")
$TheMarkdownLines.Add("")
if (-not $IncludePaths)
{
    $TheMarkdownLines.Add("入力はファイル名だけを載せている（フルパスは ``-IncludePaths`` を付けたときと実行ログにだけ出る）。")
    $TheMarkdownLines.Add("")
}

$TheMarkdownLines.Add("## セッション")
$TheMarkdownLines.Add("")
$TheMarkdownLines.Add("| sessionId | startUtc | endUtc | eventCount |")
$TheMarkdownLines.Add("|---|---|---|---|")
foreach ($TheSessionEntry in $TheSessionMap.Values)
{
    $TheMarkdownLines.Add("| $(Format-MarkdownCell -InValue $TheSessionEntry["sessionId"]) | $(Format-MarkdownCell -InValue $TheSessionEntry["startUtc"]) | $(Format-MarkdownCell -InValue $TheSessionEntry["endUtc"]) | $($TheSessionEntry["eventCount"]) |")
}
$TheMarkdownLines.Add("")

$TheMarkdownLines.Add("## 集計")
$TheMarkdownLines.Add("")
$TheMarkdownLines.Add("| 項目 | 件数 |")
$TheMarkdownLines.Add("|---|---|")
$TheMarkdownLines.Add("| event | $($TheRecords.Count) |")
$TheMarkdownLines.Add("| error | $TheErrorCount |")
$TheMarkdownLines.Add("| warning | $TheWarningCount |")
$TheDroppedText = if ($null -eq $TheDroppedEventCount) { "不明（session.end が無い）" } else { [string]$TheDroppedEventCount }
$TheMarkdownLines.Add("| dropped | $TheDroppedText |")
$TheMarkdownLines.Add("")

$TheMarkdownLines.Add("## Tool 別統計")
$TheMarkdownLines.Add("")
if ($TheToolStatistics.Count -eq 0)
{
    $TheMarkdownLines.Add("tool.end が 1 件も無い。")
}
else
{
    $TheMarkdownLines.Add("other は success / error / timeout / normalFalse / cancelled のどれでもない tool.end（result が無い行を含む）。")
    $TheMarkdownLines.Add("")
    $TheMarkdownLines.Add("| tool | calls | success | error | timeout | normalFalse | cancelled | other | p50 | p50(success) | p95 | max |")
    $TheMarkdownLines.Add("|---|---|---|---|---|---|---|---|---|---|---|---|")
    foreach ($TheToolName in $TheToolStatistics.Keys)
    {
        $TheStatistic = $TheToolStatistics[$TheToolName]
        $TheMarkdownLines.Add("| $(Format-MarkdownCell -InValue $TheToolName) | $($TheStatistic["calls"]) | $($TheStatistic["success"]) | $($TheStatistic["error"]) | $($TheStatistic["timeout"]) | $($TheStatistic["normalFalse"]) | $($TheStatistic["cancelled"]) | $($TheStatistic["other"]) | $(Format-MarkdownCell -InValue $TheStatistic["p50"]) | $(Format-MarkdownCell -InValue $TheStatistic["p50Success"]) | $(Format-MarkdownCell -InValue $TheStatistic["p95"]) | $(Format-MarkdownCell -InValue $TheStatistic["max"]) |")
    }
}
$TheMarkdownLines.Add("")

$TheMarkdownLines.Add("## slow operations")
$TheMarkdownLines.Add("")
$TheMarkdownLines.Add("elapsedMs が成功呼び出しの p50 × $SlowFactor を超え、かつ $TheSlowMinimumElapsedMs ms 以上の tool.end（成功が $TheSlowMinimumSuccessCount 件以上あるツールだけを対象にする）。")
$TheMarkdownLines.Add("")
if ($TheSortedSlowOperations.Count -eq 0)
{
    $TheMarkdownLines.Add("該当なし。")
}
else
{
    $TheMarkdownLines.Add("| tool | correlationId | elapsedMs | p50(success) | factor |")
    $TheMarkdownLines.Add("|---|---|---|---|---|")
    foreach ($TheSlowOperation in $TheSortedSlowOperations)
    {
        $TheMarkdownLines.Add("| $(Format-MarkdownCell -InValue $TheSlowOperation["tool"]) | $(Format-MarkdownCell -InValue $TheSlowOperation["correlationId"]) | $($TheSlowOperation["elapsedMs"]) | $(Format-MarkdownCell -InValue $TheSlowOperation["p50"]) | $(Format-MarkdownCell -InValue $TheSlowOperation["factor"]) |")
    }
}
$TheMarkdownLines.Add("")

$TheMarkdownLines.Add("## fallback・warning 集計")
$TheMarkdownLines.Add("")
$TheMenuFallbackText = @($TheMenuFallbackCounts.Keys | ForEach-Object { "$_=$($TheMenuFallbackCounts[$_])" }) -join " / "
$TheMarkdownLines.Add("menu.fallback: $TheMenuFallbackText")
$TheMarkdownLines.Add("")
$TheInteractionFallbackText = if ($TheInteractionFallbackCounts.Count -eq 0) { "なし" } else { @($TheInteractionFallbackCounts.Keys | ForEach-Object { "$_=$($TheInteractionFallbackCounts[$_])" }) -join " / " }
$TheMarkdownLines.Add("interaction.fallback: $TheInteractionFallbackText")
$TheMarkdownLines.Add("")
if ($TheWarningEventTable.Count -eq 0)
{
    $TheMarkdownLines.Add("warning event は無い。")
}
else
{
    $TheMarkdownLines.Add("| warning event | 件数 |")
    $TheMarkdownLines.Add("|---|---|")
    foreach ($TheWarningEventName in $TheWarningEventTable.Keys)
    {
        $TheMarkdownLines.Add("| $(Format-MarkdownCell -InValue $TheWarningEventName) | $($TheWarningEventTable[$TheWarningEventName]) |")
    }
}
$TheMarkdownLines.Add("")

$TheMarkdownLines.Add("## マーカーと区間")
$TheMarkdownLines.Add("")
if ($TheMarkers.Count -eq 0)
{
    $TheMarkdownLines.Add("diagnostics.mark は無い。")
}
else
{
    $TheMarkdownLines.Add("| sequence | timestampUtc | correlationId | markerId | message |")
    $TheMarkdownLines.Add("|---|---|---|---|---|")
    foreach ($TheMarker in $TheMarkers)
    {
        $TheMarkdownLines.Add("| $($TheMarker["sequence"]) | $(Format-MarkdownCell -InValue $TheMarker["timestampUtc"]) | $(Format-MarkdownCell -InValue $TheMarker["correlationId"]) | $(Format-MarkdownCell -InValue $TheMarker["markerId"]) | $(Format-MarkdownCell -InValue $TheMarker["message"]) |")
    }
    $TheMarkdownLines.Add("")

    if ($TheMarkerRanges.Count -eq 0)
    {
        $TheMarkdownLines.Add("区間はマーカーが 2 個以上必要。")
    }
    else
    {
        $TheMarkdownLines.Add("| 区間（sequence） | error | warning | failing tool.end |")
        $TheMarkdownLines.Add("|---|---|---|---|")
        foreach ($TheMarkerRange in $TheMarkerRanges)
        {
            $TheFailingText = "-"
            if ($TheMarkerRange["failingToolEnds"].Count -gt 0)
            {
                $TheFailingText = @($TheMarkerRange["failingToolEnds"] | ForEach-Object { "$($_["tool"]) ($($_["correlationId"])): $($_["message"])" }) -join " / "
            }
            $TheMarkdownLines.Add("| $($TheMarkerRange["fromSequence"])-$($TheMarkerRange["toSequence"]) | $($TheMarkerRange["errorCount"]) | $($TheMarkerRange["warningCount"]) | $(Format-MarkdownCell -InValue $TheFailingText -InMaxLength 160) |")
        }
    }
}
$TheMarkdownLines.Add("")

$TheMarkdownLines.Add("## suspect correlation")
$TheMarkdownLines.Add("")
if ($TheSuspectOutputs.Count -eq 0)
{
    $TheMarkdownLines.Add("該当なし。")
}
else
{
    $TheMarkdownLines.Add("| correlationId | tool | reasons | sequence 範囲 | events |")
    $TheMarkdownLines.Add("|---|---|---|---|---|")
    foreach ($TheSuspectOutput in $TheSuspectOutputs)
    {
        $TheReasonText = @($TheSuspectOutput["reasons"]) -join ", "
        $TheRangeText = "$($TheSuspectOutput["sequenceRange"]["from"])-$($TheSuspectOutput["sequenceRange"]["to"])"
        $TheMarkdownLines.Add("| $(Format-MarkdownCell -InValue $TheSuspectOutput["correlationId"]) | $(Format-MarkdownCell -InValue $TheSuspectOutput["tool"]) | $(Format-MarkdownCell -InValue $TheReasonText) | $TheRangeText | $($TheSuspectOutput["eventCount"]) |")
    }

    $TheMarkdownLines.Add("")
    if ($TheTruncatedSuspectCount -gt 0)
    {
        $TheMarkdownLines.Add("この表は全 $($TheSuspectOutputs.Count) 件。suspects.jsonl には重い順に $TheWrittenSuspectCount 件だけを書き、$TheTruncatedSuspectCount 件を省略した（``-MaxSuspects`` で増やせる）。")
    }
    else
    {
        $TheMarkdownLines.Add("suspects.jsonl には全 $TheWrittenSuspectCount 件の元ログ行を書いた（省略なし）。")
    }
}
$TheMarkdownLines.Add("")

$TheMarkdownLines.Add("## 出力ファイル")
$TheMarkdownLines.Add("")
$TheMarkdownLines.Add("- ``analysis-summary.json``")
$TheMarkdownLines.Add("- ``analysis-summary.md``")
$TheMarkdownLines.Add("- ``suspects.jsonl``（correlationId $TheWrittenSuspectCount 件 / $($TheSuspectLines.Count) 行・省略 $TheTruncatedSuspectCount 件）")
$TheMarkdownLines.Add("")

# ---------------------------------------------------------------------------
# 書き出し（UTF-8 BOM なし。json / md は CRLF、jsonl は元の events.jsonl と同じ LF）
# ---------------------------------------------------------------------------

$TheUtf8NoBom = [System.Text.UTF8Encoding]::new($false)

$TheSummaryJsonText = (ConvertTo-Json -InputObject $TheSummary -Depth 12).Replace("`r`n", "`n").Replace("`n", "`r`n")
[System.IO.File]::WriteAllText($TheSummaryJsonPath, $TheSummaryJsonText + "`r`n", $TheUtf8NoBom)

[System.IO.File]::WriteAllText($TheSummaryMarkdownPath, ($TheMarkdownLines -join "`r`n") + "`r`n", $TheUtf8NoBom)

$TheSuspectsText = ""
if ($TheSuspectLines.Count -gt 0)
{
    $TheSuspectsText = ($TheSuspectLines -join "`n") + "`n"
}
[System.IO.File]::WriteAllText($TheSuspectsPath, $TheSuspectsText, $TheUtf8NoBom)

# ---------------------------------------------------------------------------
# 実行結果の要約
# ---------------------------------------------------------------------------

Write-Host "events         : $($TheRecords.Count) event(s), skipped $TheSkippedLineCount line(s), malformed $TheMalformedFieldCount field(s)"
Write-Host "error / warning: $TheErrorCount / $TheWarningCount"
Write-Host "markers        : $($TheMarkers.Count) ($($TheMarkerRanges.Count) range(s))"
Write-Host "suspects       : $($TheSuspectOutputs.Count) correlationId(s), written $TheWrittenSuspectCount, truncated $TheTruncatedSuspectCount"
Write-Host "output         : $TheOutputDirectory"
Write-Host "  $TheSummaryJsonPath"
Write-Host "  $TheSummaryMarkdownPath"
Write-Host "  $TheSuspectsPath"
