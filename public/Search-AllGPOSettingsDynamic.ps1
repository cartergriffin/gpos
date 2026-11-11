function Search-AllGPOSettingsDynamic {
<#
.SYNOPSIS
    Searches every setting in every GPO in a snapshot, for every extension type, with no per-type code.

.DESCRIPTION
    The work is done by compiled code (Private/GpoSnapshotIndex.cs); this function loads the
    snapshot, calls it, and turns each hit into an object.

    Pass 1 (cached next to the JSON as <JsonPath>.itempaths.cache): for each extension type,
    finds which element paths repeat (a record-like element appearing more than once under
    one parent anywhere in the snapshot). The outermost such paths are that type's item paths.

    Pass 2: each Extension is split into items at those paths, so every repeated record
    (a certificate, a firewall rule, a policy, a file entry, a drive map) is its own item,
    even in a GPO that has only one. Anything outside an item path is gathered into one
    '(extension)' item. Each item is flattened to name = value pairs: element paths relative
    to the item, attributes as Path@name, repeated siblings as Name[0], Name[1].

    A search string matches an item if it appears in the extension type name, the item path,
    any pair name, or any pair value (case-insensitive, literal). Each matching item is
    returned as one object: GPOName, GPOId, ModifiedTime, Scope, Extension, ItemPath, Item,
    Matches, then every pair as a property. Matches is an array of objects with Name and
    Value, one per pair that hit. Property names contain / @ [ ] so quote them:
    $row.'Display/Name'. Every value is returned whole; nothing is joined or truncated.

.PARAMETER JsonPath
    Snapshot JSON from Get-MassiveGPOReportSnapshot.

.PARAMETER SearchString
    Literal string. Optional; without it every item is returned.

.PARAMETER Type
    Wildcard on the extension type name, for example 'PublicKey*'. Default is all types.

.PARAMETER Renderer
    Scriptblock that takes a GPO report XML string and returns GPMC HTML. Default is the Windows
    report engine (Microsoft.GroupPolicy.Reporting). Mainly for testing.

.PARAMETER NoGpmcPath
    Skip the GPMC path mapping entirely.

.PARAMETER RebuildCache
    Recompute the item paths even if the cache file is newer than the JSON.

.EXAMPLE
    Search-AllGPOSettingsDynamic -JsonPath .\all_gpos.json -SearchString 'mesquite' | fl
#>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$JsonPath,
        [string]$SearchString,
        [string]$Type = '*',
        [switch]$RebuildCache,
        [switch]$NoGpmcPath,
        [scriptblock]$Renderer
    )

    Initialize-GpoSearchNative

    $fullPath = (Resolve-Path -LiteralPath $JsonPath).ProviderPath
    $snapshot = Get-Content -LiteralPath $fullPath -Raw | ConvertFrom-Json
    $reports = [string[]]@($snapshot | ForEach-Object { $_.Report })
    $snapshot = $null

    $htmlRenderer = $null
    $engineError = $null
    if (-not $NoGpmcPath) {
        try {
            if ($null -ne $Renderer) {
                $htmlRenderer = [GpoSearch.HtmlRenderer]$Renderer
            }
            else {
                $htmlRenderer = ([GpoSearch.ReportEngine]::new()).AsRenderer()
            }
        }
        catch {
            $engineError = $_.Exception.Message
            Write-Warning "Report engine unavailable, GpmcPath will be empty: $engineError"
        }
    }

    $index = [GpoSearch.GpoSnapshotIndex]::new(
        $reports,
        "$fullPath.itempaths.cache",
        "$fullPath.gpmcpaths.cache",
        (Get-Item -LiteralPath $fullPath).LastWriteTimeUtc,
        [bool]$RebuildCache,
        (-not $NoGpmcPath),
        $htmlRenderer)

    $progress = [Action[int, int]] {
        param($done, $total)
        Write-Progress -Activity 'Search-AllGPOSettingsDynamic' -Status "GPO $done of $total" -PercentComplete (100 * $done / [math]::Max(1, $total))
    }

    try {
        foreach ($item in $index.Search($SearchString, $Type, $progress)) {
            $out = [ordered]@{
                GPOName          = $item.GpoName
                GPOId            = $item.GpoId
                ModifiedTime     = $item.ModifiedTime
                Scope            = $item.Scope
                Extension        = $item.Extension
                ExtensionName    = $item.ExtensionName
                GpmcPath         = $item.GpmcPath
                GpmcPathSegments = $item.PathSegments
                GpmcPathStatus   = $item.PathStatus
                GpmcFields       = @($item.GpmcFields | ForEach-Object { [PSCustomObject]@{ Name = $_.Name; Value = $_.Value } })
                ItemPath         = $item.ItemPath
                Item             = $item.Item
                Matches          = @($item.Matches | ForEach-Object { [PSCustomObject]@{ Name = $_.Name; Value = $_.Value } })
            }
            for ($i = 0; $i -lt $item.Props.Count; $i++) {
                $name = $item.Props.Keys[$i]
                while ($out.Contains($name)) { $name = "_$name" }
                $out[$name] = $item.Props.Values[$i]
            }
            $obj = [PSCustomObject]$out
            $obj.PSObject.TypeNames.Insert(0, "GPOSetting.$($item.Extension).$($item.ItemPath)")
            $obj
        }
    }
    finally {
        Write-Progress -Activity 'Search-AllGPOSettingsDynamic' -Completed
    }

    if ($index.MappingFailures -gt 0) {
        Write-Warning "$($index.MappingFailures) item(s) could not be mapped to a GPMC path (GpmcPathStatus starts with 'Error'). First error: $($index.FirstMappingError)"
    }
}