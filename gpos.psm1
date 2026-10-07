Get-ChildItem "$PSScriptRoot/public/*.ps1" | ForEach-Object {
    . $_.FullName
    Export-ModuleMember -Function $_.BaseName
}

if (test-path "$PSScriptRoot/private") {
    Get-ChildItem "$PSScriptRoot/private/*.ps1" | ForEach-Object {
        . $_.FullName
    }
}