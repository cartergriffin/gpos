# $PSScriptRoot is the Private folder here; capture it now, it is not reliable inside a function later.
$script:GpoSearchSourceFiles = @(
    (Join-Path $PSScriptRoot 'GpoHtmlTree.cs'),
    (Join-Path $PSScriptRoot 'GpoSnapshotIndex.cs')
)

function Initialize-GpoSearchNative {
    if ('GpoSearch.GpoSnapshotIndex' -as [type]) { return }
    foreach ($f in $script:GpoSearchSourceFiles) {
        if (-not (Test-Path -LiteralPath $f)) { throw "Source file not found: $f" }
    }
    if ($PSVersionTable.PSEdition -eq 'Desktop') {
        # Windows PowerShell: System.Xml is not in the default reference set
        Add-Type -Path $script:GpoSearchSourceFiles -ReferencedAssemblies 'System.Xml.dll'
    }
    else {
        Add-Type -Path $script:GpoSearchSourceFiles
    }
}