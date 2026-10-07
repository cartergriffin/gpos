function Get-MassiveGPOReportSnapshot {
    [CmdletBinding()]
    param(
        [string]$outPath = '.\'
    )
    $gpos = get-gpo -all
    $reports = [System.Collections.Generic.List[object]]::new()
    $count = 0

    foreach ($gpo in $gpos) {
        $count++
        Write-Progress -Activity "Processing GPOs" -Status "$count / $($gpos.Count)" -PercentComplete (($count / $gpos.Count) * 100)
        
        $report = Get-GPOReport -Guid $gpo.Id -ReportType Xml
        
        $obj = [PSCustomObject]@{
            DisplayName      = $gpo.DisplayName
            DomainName       = $gpo.DomainName
            Owner            = $gpo.Owner
            Id               = $gpo.Id
            GpoStatus        = $gpo.GpoStatus
            Description      = $gpo.Description
            CreationTime     = $gpo.CreationTime
            ModificationTime = $gpo.ModificationTime
            UserVersion      = $gpo.UserVersion
            ComputerVersion  = $gpo.ComputerVersion
            WmiFilter        = $gpo.WmiFilter
            Report           = $report
        }
        $reports.Add($obj)
    }
    $d = get-date -format "yyyy_MM_dd__hh_mm_ss"
    $reports | ConvertTo-Json -Depth 10 | Out-File "$outPath\all_gpos-$d.json"
}