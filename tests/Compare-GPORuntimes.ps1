<#
.SYNOPSIS
    Runs Search-AllGPOSettingsDynamic over the same snapshot in Windows PowerShell and in pwsh,
    each with its own cache, and reports every difference in GpmcPath and GpmcPathStatus.

.EXAMPLE
    .\Compare-GpoRuntimes.ps1 -ModulePath ..\gpos.psm1 -JsonPath C:\temp\all_gpos.json

.NOTES
    Works from either shell. Output: a summary in the console plus runtimes-diff.csv in OutDir.
    Items are matched on GPOId, Scope, Extension, ItemPath and their order within that group.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ModulePath,
    [Parameter(Mandatory)][string]$JsonPath,
    [string]$OutDir = (Join-Path ([System.IO.Path]::GetTempPath()) 'gpo-runtime-compare'),
    [string]$WindowsPowerShell = 'powershell.exe',
    [string]$Pwsh = 'pwsh',
    [string]$SearchString,
    [switch]$NoGpmcPath
)

$ErrorActionPreference = 'Stop'
$ModulePath = (Resolve-Path -LiteralPath $ModulePath).ProviderPath
$JsonPath = (Resolve-Path -LiteralPath $JsonPath).ProviderPath
if (Test-Path -LiteralPath $OutDir) { Remove-Item -LiteralPath $OutDir -Recurse -Force }
[void](New-Item -ItemType Directory -Path $OutDir)

# Child script, run once per runtime. Each runtime gets its own copy of the snapshot so no cache is shared.
$child = Join-Path $OutDir 'child.ps1'
Set-Content -LiteralPath $child -Encoding UTF8 -Value @'
param([string]$Module, [string]$Json, [string]$Csv, [string]$Meta, [string]$Search, [int]$NoPath)
$ErrorActionPreference = 'Stop'
Import-Module $Module -Force
$splat = @{ JsonPath = $Json }
if ($Search) { $splat['SearchString'] = $Search }
if ($NoPath) { $splat['NoGpmcPath'] = $true }
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$rows = @(Search-AllGPOSettingsDynamic @splat -WarningVariable warn -WarningAction SilentlyContinue)
$sw.Stop()
$rows | Select-Object GPOId, Scope, Extension, ItemPath, GpmcPath, GpmcPathStatus | Export-Csv -LiteralPath $Csv -NoTypeInformation -Encoding UTF8
$runtime = [System.Runtime.InteropServices.RuntimeInformation]::FrameworkDescription
@(
    "PSVersion=$($PSVersionTable.PSVersion)",
    "Edition=$($PSVersionTable.PSEdition)",
    "Runtime=$runtime",
    "Seconds=$([math]::Round($sw.Elapsed.TotalSeconds, 1))",
    "Items=$($rows.Count)",
    "Warnings=$(@($warn) -join ' || ')"
) | Set-Content -LiteralPath $Meta -Encoding UTF8
'@

$runtimes = [ordered]@{ WinPS = $WindowsPowerShell; Pwsh = $Pwsh }
$results = [ordered]@{}
foreach ($name in $runtimes.Keys) {
    $dir = Join-Path $OutDir $name
    [void](New-Item -ItemType Directory -Path $dir)
    $copy = Join-Path $dir 'snapshot.json'
    Copy-Item -LiteralPath $JsonPath -Destination $copy
    $csv = Join-Path $dir 'items.csv'
    $meta = Join-Path $dir 'meta.txt'

    Write-Host "Running $name ($($runtimes[$name])) ..."
    $exe = $runtimes[$name]
    $argList = @('-NoProfile', '-File', $child, '-Module', $ModulePath, '-Json', $copy, '-Csv', $csv, '-Meta', $meta, '-Search', $SearchString, '-NoPath', [int][bool]$NoGpmcPath)
    & $exe @argList 2>&1 | ForEach-Object { Write-Host "  [$name] $_" }
    if (-not (Test-Path -LiteralPath $csv)) { throw "$name produced no output (see the messages above)" }

    $info = @{}
    foreach ($line in Get-Content -LiteralPath $meta) {
        $i = $line.IndexOf('=')
        if ($i -gt 0) { $info[$line.Substring(0, $i)] = $line.Substring($i + 1) }
    }
    $results[$name] = [PSCustomObject]@{ Rows = @(Import-Csv -LiteralPath $csv); Info = $info }
}

# Key every row: GPOId|Scope|Extension|ItemPath|n, n = order within that group
function Get-KeyedRows {
    param($Rows)
    $seen = @{}
    $out = [ordered]@{}
    foreach ($r in $Rows) {
        $base = "$($r.GPOId)|$($r.Scope)|$($r.Extension)|$($r.ItemPath)"
        $n = [int]$seen[$base]
        $seen[$base] = $n + 1
        $out["$base|$n"] = $r
    }
    $out
}

$a = Get-KeyedRows $results['WinPS'].Rows
$b = Get-KeyedRows $results['Pwsh'].Rows

$diff = [System.Collections.Generic.List[object]]::new()
foreach ($k in $a.Keys) {
    if (-not $b.Contains($k)) {
        $diff.Add([PSCustomObject]@{ Kind = 'OnlyInWinPS'; Extension = $a[$k].Extension; ItemPath = $a[$k].ItemPath; GPOId = $a[$k].GPOId; WinPSStatus = $a[$k].GpmcPathStatus; PwshStatus = ''; WinPSPath = $a[$k].GpmcPath; PwshPath = '' })
        continue
    }
    $x = $a[$k]; $y = $b[$k]
    if ($x.GpmcPathStatus -ne $y.GpmcPathStatus -or $x.GpmcPath -ne $y.GpmcPath) {
        $kind = 'PathDiffers'
        if ($x.GpmcPathStatus -ne $y.GpmcPathStatus) { $kind = 'StatusDiffers' }
        $diff.Add([PSCustomObject]@{ Kind = $kind; Extension = $x.Extension; ItemPath = $x.ItemPath; GPOId = $x.GPOId; WinPSStatus = $x.GpmcPathStatus; PwshStatus = $y.GpmcPathStatus; WinPSPath = $x.GpmcPath; PwshPath = $y.GpmcPath })
    }
}
foreach ($k in $b.Keys) {
    if (-not $a.Contains($k)) {
        $diff.Add([PSCustomObject]@{ Kind = 'OnlyInPwsh'; Extension = $b[$k].Extension; ItemPath = $b[$k].ItemPath; GPOId = $b[$k].GPOId; WinPSStatus = ''; PwshStatus = $b[$k].GpmcPathStatus; WinPSPath = ''; PwshPath = $b[$k].GpmcPath })
    }
}

Write-Host ''
Write-Host '=== Runtimes ==='
foreach ($name in $results.Keys) {
    $i = $results[$name].Info
    Write-Host ("{0,-6} PS {1} ({2}) on {3}, {4}s, {5} items" -f $name, $i['PSVersion'], $i['Edition'], $i['Runtime'], $i['Seconds'], $i['Items'])
    if ($i['Warnings']) { Write-Host "       warnings: $($i['Warnings'])" }
}

Write-Host ''
Write-Host '=== Status counts ==='
function Get-StatusCounts {
    param($Rows)
    $counts = @{}
    foreach ($r in $Rows) {
        $key = $r.GpmcPathStatus
        if ($key -like 'Error*') { $key = 'Error' }
        $counts[$key] = 1 + [int]$counts[$key]
    }
    $counts
}
$sa = Get-StatusCounts $results['WinPS'].Rows
$sb = Get-StatusCounts $results['Pwsh'].Rows
$names = @(@($sa.Keys) + @($sb.Keys) | Sort-Object -Unique)
$table = foreach ($n in $names) {
    [PSCustomObject]@{ Status = $n; WinPS = [int]$sa[$n]; Pwsh = [int]$sb[$n] }
}
Write-Host ('{0,-18} {1,8} {2,8}' -f 'Status', 'WinPS', 'Pwsh')
foreach ($row in $table) { Write-Host ('{0,-18} {1,8} {2,8}' -f $row.Status, $row.WinPS, $row.Pwsh) }

Write-Host "=== Differences: $($diff.Count) ==="
if ($diff.Count -gt 0) {
    $csvOut = Join-Path $OutDir 'runtimes-diff.csv'
    $diff | Export-Csv -LiteralPath $csvOut -NoTypeInformation -Encoding UTF8
    $groups = $diff | Group-Object Kind, Extension, WinPSStatus, PwshStatus | Sort-Object Count -Descending | Select-Object -First 25
    foreach ($g in $groups) { Write-Host ('{0,6}  {1}' -f $g.Count, $g.Name) }
    Write-Host "Full list: $csvOut"
}
else {
    Write-Host 'No differences.'
}
$diff