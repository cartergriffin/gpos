function Search-AllGPOSettings {
<#
.SYNOPSIS
    Searches a JSON GPO report snapshot for a specific setting string.

.DESCRIPTION
    Loads a JSON file containing serialized GPO report XML (as produced by
    Get-MassiveGPOReportSnapshot), filters to reports containing the search
    string, and passes each matching report to Find-GPOSetting for structured output.

.PARAMETER JsonPath
    Path to the JSON file containing the GPO report snapshot.

.PARAMETER SearchString
    The string to search for within the GPO reports.

.EXAMPLE
    Search-AllGPOSettings -JsonPath 'C:\temp\all_gpos.json' -SearchString 'TlsVersion'
#>
    param(
        [string]$JsonPath,
        [string]$SearchString
    )
    
    $escapedSearch = [regex]::Escape($SearchString)
    $reports = Get-Content $JsonPath |
     ConvertFrom-Json |
     Where-Object { $_.Report -match $escapedSearch }
    
    $reports  | ForEach-Object {
        Find-GPOSetting -GPOReport ([xml]$_.Report) -SearchString $SearchString
    }
}