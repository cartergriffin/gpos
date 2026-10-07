function Find-GPOSetting {
<#
.SYNOPSIS
    Searches a GPO XML report for a specific setting string.

.DESCRIPTION
    Takes a parsed GPO XML report and a search string, then traverses the
    extension data across both User and Computer scopes to find matching
    registry, policy, and other setting entries. Returns structured objects
    identifying the GPO, scope, extension type, and matched property values.

.PARAMETER GPOReport
    A parsed XML GPO report object, as returned by Get-GPOReport -ReportType Xml.

.PARAMETER SearchString
    The string to search for within the GPO report extension data.

.EXAMPLE
    $report = [xml](Get-GPOReport -Guid $gpo.Id -ReportType Xml)
    Find-GPOSetting -GPOReport $report -SearchString 'TlsVersion'
#>
    param(
        [xml]$GPOReport,
        [string]$SearchString
    )
    
    $escapedSearch = [regex]::Escape($SearchString)
    $gpoName = $GPOReport.GPO.Name
    $gpoId = $GPOReport.GPO.Identifier.Identifier.InnerText
    [datetime]$modifiedTime = $GPOReport.GPO.ModifiedTime
    
    @($GPOReport.GPO.User, $GPOReport.GPO.Computer) | ForEach-Object {
        $scope = $_.LocalName
        $_.ExtensionData.Extension |
            Where-Object { $_.InnerXml -match $escapedSearch } |
            ForEach-Object {
                #Write-Host "Matched Extension: $($_.type)" -ForegroundColor Green
                $extension = $_

                if ($extension.type -like '*:WindowsFirewallSettings') {
                    foreach ($ruleSet in @('InboundFirewallRules', 'OutboundFirewallRules')) {
                        $rules = $extension.$ruleSet
                        if (-not $rules) { continue }
                
                        $rules | Where-Object {
                            (Resolve-FirewallString $_.Name)  -match $escapedSearch -or
                            (Resolve-FirewallString $_.Desc)  -match $escapedSearch -or
                            (Resolve-FirewallString $_.EmbedCtxt) -match $escapedSearch -or
                            $_.App    -match $escapedSearch -or
                            $_.LPort  -match $escapedSearch -or
                            $_.RA4    -match $escapedSearch -or
                            $_.Action -match $escapedSearch
                        } | ForEach-Object {
                            [PSCustomObject]@{
                                GPOName      = $gpoName
                                GPOId        = $gpoId
                                ModifiedTime = $modifiedTime
                                Scope        = $scope
                                Extension    = $extension.type
                                Property     = $ruleSet
                                RuleName     = Resolve-FirewallString $_.Name
                                Direction    = $_.Dir
                                Action       = $_.Action
                                Protocol     = $_.Protocol
                                LocalPort    = $_.LPort
                                App          = $_.App
                                RemoteScope  = $_.RA4 -join ', '
                                Active       = $_.Active
                                Group        = Resolve-FirewallString $_.EmbedCtxt
                            }
                        }
                    }
                    return
                }

                $extension.PSObject.Properties |
                    Where-Object { $_.Name -notmatch '^(ParentNode|OwnerDocument|ChildNodes|FirstChild|LastChild|NextSibling|PreviousSibling|Attributes|SchemaInfo|BaseURI|Prefix|NamespaceURI|LocalName|Name|NodeType|Value|InnerXml|OuterXml|InnerText|PreviousText)$' } |
                    ForEach-Object {
                        #Write-Host "Property: $($_.Name)" -ForegroundColor Yellow
                        $propertyName = $_.Name
                        $_.Value | Where-Object {
                            $_.InnerXml -match $escapedSearch -or
                            $_.Display.Name -match $escapedSearch  -or
                            $_.SettingString -match $escapedSearch -or
                            $_.SettingNumber -match $escapedSearch -or
                            $_.EditText.Value -match $escapedSearch -or
                            $_.CheckBox.Value -match $escapedSearch -or
                            $_.Text -match $escapedSearch
                        } |
                        ForEach-Object {
                            if ($_.KeyName) {
                                [PSCustomObject]@{
                                    GPOName = $gpoName
                                    GPOId = $gpoId
                                    ModifiedTime = $modifiedTime
                                    Scope = $scope
                                    Extension = $extension.type
                                    Property = $propertyName
                                    KeyName = $_.KeyName
                                    Display = $_.Display.Name
                                    SettingString = $_.SettingString
                                    SettingNumber = $_.SettingNumber
                                }
                            } elseif ($_.Name -and $_.State) {
                                # policy path
                                $policyNode = $_
                                $widgetTypes = 'DropDownList','ListBox','ComboBox','CheckBox','EditText','Text','Numeric','MultiText','Decimal','LongDecimal'
                                foreach ($widgetType in $widgetTypes) {
                                    $policyNode.$widgetType | Where-Object {
                                        $_.Name -match $escapedSearch -or
                                        $_.State -match $escapedSearch -or
                                        $_.Value.Name -match $escapedSearch -or
                                        $_.Value.Element -match $escapedSearch -or
                                        $_.Value -match $escapedSearch
                                    } | ForEach-Object {
                                        [PSCustomObject]@{
                                            GPOName      = $gpoName
                                            GPOId        = $gpoId
                                            ModifiedTime = $modifiedTime
                                            SettingType  = $widgetType
                                            SettingName  = $_.Name
                                            SettingState = $_.State
                                            SettingValue = if ($_.Value.Element) { $_.Value.Element } elseif ($_.Value.Name) { $_.Value.Name } else { $_.Value }
                                            Scope        = $scope
                                            Extension    = $extension.type
                                            Property     = $propertyName
                                            PolicyName   = $policyNode.Name
                                            PolicyState  = $policyNode.State
                                        }
                                    }
                                }
                                if ($policyNode.Name -match $escapedSearch) {
                                    [PSCustomObject]@{
                                        GPOName      = $gpoName
                                        GPOId        = $gpoId
                                        ModifiedTime = $modifiedTime
                                        Scope        = $scope
                                        Extension    = $extension.type
                                        Property     = $propertyName
                                        PolicyName   = $policyNode.Name
                                        PolicyState  = $policyNode.State
                                        SettingType  = $null
                                        SettingName  = $null
                                        SettingState = $null
                                        SettingValue = $null
                                    }
                                }
                            } elseif (
                                $extension.type -like '*:RegistrySettings' -and
                                $_.Registry
                            ) {
                                foreach ($reg in $_.Registry) {
                                    foreach ($prop in $reg.Properties) {
                                        if (
                                            $prop.key   -match $escapedSearch -or
                                            $prop.name  -match $escapedSearch -or
                                            $prop.value -match $escapedSearch
                                        ) {
                                            [PSCustomObject]@{
                                                GPOName      = $gpoName
                                                GPOId        = $gpoId
                                                ModifiedTime = $modifiedTime
                                                Scope        = $scope
                                                Extension    = $extension.type
                                                Property     = $propertyName
                                                PolicyName   = $reg.name
                                                Hive         = $prop.hive
                                                KeyPath      = $prop.key
                                                ValueName    = $prop.name
                                                ValueType    = $prop.type
                                                ValueData    = $prop.value
                                                Action       = $prop.action
                                                Order        = $reg.GPOSettingOrder
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        # todo - map this spreadsheet from MS to resolve all reg keys to either custom gpos or admin templates
                        # https://www.microsoft.com/en-us/download/details.aspx?id=25250
                    }
            }
    }
}