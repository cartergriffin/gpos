Small collection of functions to interact with Active Directory Group Policy
objects (GPOs). Usage:

1. clone repo // download zip
2. `import-module gpos.psd1`

3. `Get-MassiveGPOReportSnapshot`

4. `Search-AllGPOSettings -searchString "SomeSearchString" -jsonPath {path of output from moment ago}`

Most types of settings configurable via GPO will return when passed as search text. You can do -SearchString {SomeSettingTextVisibleInGPMC} and it will very likely return every gpo with that setting configured. You can also search for the value that setting is set to, separately, to grep through all GPOs to search for pointers to an old server name, for instance. Very handy.

Firewalls rules are more complex and deserve their own scripts, I will add those at some point, they do exist.
