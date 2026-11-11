# gpos

PowerShell module for searching every setting in every Active Directory Group Policy Object, by setting name, value, GPO name, or the exact path GPMC shows (`Policies > Windows Settings > Security Settings > Public Key Policies/...`).

It works from an offline JSON snapshot of the domain's GPO reports. Paths and display text come from Windows' own GPMC report engine (`Microsoft.GroupPolicy.Reporting.dll`), which renders each setting one at a time, so there are no hand-written mapping tables. The write-up is here: [Searching every GPO by the path GPMC shows you](https://cartergriffin.github.io/blog/2026/11/11/searching-every-gpo-by-gpmc-path/).

## Requirements

- Windows with the Group Policy Management tools (GPMC / RSAT). They provide the `GroupPolicy` module used to take snapshots, and they install `Microsoft.GroupPolicy.Reporting` in the GAC, which the path mapping needs.
- Read access to the domain's GPOs, needed only to take a snapshot. Searching is offline.
- Windows PowerShell 5.1 is the reference runtime, because it's the .NET Framework runtime the report engine was built for. PowerShell 7 works too. Use `tests/Compare-GPORuntimes.ps1` to check that both give the same results on your snapshot.

## Quick start

```powershell
git clone https://github.com/cartergriffin/gpos.git
cd gpos
Import-Module .\gpos.psd1

# 1. Snapshot every GPO's report XML into one JSON file (talks to AD)
Get-MassiveGPOReportSnapshot -outPath C:\temp\gpo

# 2. Search it (offline)
$jp = (Get-ChildItem C:\temp\gpo\all_gpos-*.json | Sort-Object LastWriteTime | Select-Object -Last 1).FullName
Search-AllGPOSettingsDynamic -JsonPath $jp -SearchString 'Public Key Policies'
Search-AllGPOSettingsDynamic -JsonPath $jp -SearchString 'Maximum lifetime for service ticket' | Format-List GPOName, GpmcPath, GpmcFields
```

The first search renders and caches every item, which is slow on a large domain. Later searches read from the cache.

## Commands

| Command | What it does |
|---------|--------------|
| `Get-MassiveGPOReportSnapshot [-outPath <dir>]` | Runs `Get-GPO -All` and `Get-GPOReport -Guid <id> -ReportType Xml` for each GPO. Writes `all_gpos-<yyyy_MM_dd__hh_mm_ss>.json` to `-outPath` (default: current directory). Each record holds the GPO metadata (name, domain, owner, id, status, description, created/modified times, user/computer versions, WMI filter) plus the report XML in `Report`. |
| `Search-AllGPOSettingsDynamic -JsonPath <file> [-SearchString <s>] [-Type <wildcard>] [-NoGpmcPath] [-RebuildCache] [-Renderer <scriptblock>]` | The main search, described below. |
| `Search-AllGPOSettings -JsonPath <file> -SearchString <s>` | Legacy search. Regex-prefilters the snapshot and hands matching reports to `Find-GPOSetting`. Kept for compatibility. |
| `Find-GPOSetting -GPOReport <xml> -SearchString <s>` | Legacy per-report matcher with hand-written branches for registry, policy, firewall and a few other shapes. It misses extension types it has no branch for. Use the dynamic search instead. |

### `Search-AllGPOSettingsDynamic`

Matching is case-insensitive and literal: no regex, and `*` is a plain character, not a wildcard. An item matches if the search string appears in any of the following:

- the GPO name
- the extension type (`PublicKeySettings`) or extension name (`Public Key`)
- the GPMC path (`GpmcPath`)
- the item path inside the extension (`Certificates/Certificate`)
- any field name or value, raw from the XML or as GPMC displays it (`GpmcFields`)

With no `-SearchString`, every item in the snapshot is returned.

| Parameter | Meaning |
|-----------|---------|
| `-JsonPath` | Snapshot from `Get-MassiveGPOReportSnapshot`. Required. |
| `-SearchString` | Literal text to match. Optional. |
| `-Type` | Wildcard on the extension type, e.g. `'PublicKey*'`. Default `*`. |
| `-NoGpmcPath` | Skip path mapping and render nothing. Faster, but `GpmcPath` and `GpmcFields` are empty and path text isn't searchable. |
| `-RebuildCache` | Recompute the item shapes even if the cache is newer than the JSON. |
| `-Renderer` | Scriptblock that takes report XML and returns GPMC HTML, used instead of the Windows engine. For testing. |

Each result is one setting ("item"), with these properties:

| Property | Meaning |
|----------|---------|
| `GPOName`, `GPOId`, `ModifiedTime`, `Scope` | Which GPO, and Computer or User. |
| `Extension`, `ExtensionName` | Extension type (namespace prefix removed) and its `ExtensionData/Name`. |
| `GpmcPath`, `GpmcPathSegments` | The heading chain GPMC renders above the item, as a string joined with ` > ` and as an array. |
| `GpmcPathStatus` | How the path was found. See below. |
| `GpmcFields` | Name/value pairs exactly as GPMC displays them, e.g. `Policy = Domain controller: LDAP server signing requirements`, `Setting = None`. |
| `ItemPath`, `Item` | Where the item sits in the XML, and its display name. |
| `Matches` | The name/value pairs that matched the search string. |
| *(everything else)* | Every raw XML field of the item, flattened. Element paths become names, attributes are `Path@name`, and repeated siblings are `Name[0]`, `Name[1]`. Quote these names in PowerShell: `$r.'EditText/Value'`. |

`GpmcPathStatus` values:

| Status | Meaning |
|--------|---------|
| `Ok` | The item was rendered alone, and the path and fields come from that render. |
| `OkExtensionLevel` | A one-item render failed (some templates need sibling data, e.g. wireless policies), so the whole extension was rendered and its shared heading chain used. The path is correct but shallower. |
| `OkDisabled` | A disabled preference item, which GPMC hides. A copy with `disabled="0"` was rendered to find where it belongs. |
| `NotRendered` | GPMC doesn't display this item: the `(extension)` leftover fields, or registry entries with no value. Expected, not an error. |
| `Error: ...` | The engine threw. The message holds the full exception chain, and the search prints a warning with the count and the first error. |
| `EngineUnavailable` | `Microsoft.GroupPolicy.Reporting` couldn't be loaded, so no paths. Install GPMC / RSAT. |

## How it works

1. **Item discovery** (cached as `<JsonPath>.itempaths.cache`): the whole snapshot is scanned once. For each extension type, the element paths that repeat under one parent become that type's item paths (certificates, rules, policies, drive maps and so on). Nothing is per-type, so extension types the code has never seen are handled the same way.
2. **Splitting and flattening:** each extension is cut into items at those paths, and each item is flattened to name/value pairs. Fields outside any item become a single `(extension)` item.
3. **Path mapping** (cached as `<JsonPath>.gpmcpaths.cache`): for each item, a copy of the GPO's XML containing only that item is written to a temp file. It's loaded with `Microsoft.GroupPolicy.Reporting.ReportFactory.GenerateFromBackup` and rendered with `ConvertToHtml`. The parser reads the `heN` / `sectionTitle` heading chain and the table rows. Renders are keyed by a hash of the isolated XML, so identical items (e.g. one root CA pushed by 50 GPOs) render once.
4. **Search:** every item is matched against the string as described above.

Both cache files sit next to the snapshot and are tied to its last-write time. Delete them, or pass `-RebuildCache` for the item cache, to force a rebuild. A new snapshot file always starts with fresh caches.

**Legacy GAC resolution:** the audit policy renderer depends on `Microsoft.ManagementConsole` 3.0, which lives in the .NET 2.0-era GAC (`%SystemRoot%\assembly`) that .NET 4+ doesn't search. `ReportEngine` installs an `AssemblyResolve` handler that looks there by name, version and public key token.

## Layout

| Path | What it is |
|------|------------|
| `gpos.psd1` / `gpos.psm1` | Module manifest and loader. The `.psm1` dot-sources every `private/*.ps1` and `public/*.ps1`, and exports each public file's function. |
| `public/` | Exported commands, one function per file. |
| `private/GpoSearchNative.ps1` | Compiles the C# below with `Add-Type` on first use (`Initialize-GpoSearchNative`). On Windows PowerShell it adds `System.Xml.dll` as a reference. |
| `private/GpoSnapshotIndex.cs` | Item discovery, splitting/flattening, isolation, the report engine wrapper (`ReportEngine`), both caches, and the search. Written in C# 5 so that Windows PowerShell 5.1's compiler accepts it. |
| `private/GpoHtmlTree.cs` | Small parser for GPMC report HTML (heading tree and table rows). Windows PowerShell has no usable built-in HTML parser. |
| `tests/Compare-GPORuntimes.ps1` | Runs `Search-AllGPOSettingsDynamic` over the same snapshot under `powershell.exe` and `pwsh`, each with its own cache copy. Reports every difference in `GpmcPath` / `GpmcPathStatus` and writes `runtimes-diff.csv`. Parameters: `-ModulePath`, `-JsonPath`, `-OutDir` (default `%TEMP%\gpo-runtime-compare`), `-WindowsPowerShell`, `-Pwsh`, `-SearchString`, `-NoGpmcPath`. |

## Known limits

- **Firewall rule names:** the raw XML stores many of these as resource references (`@FirewallAPI.dll,-28502`). A search for the readable name can only hit through `GpmcFields`, and only if the report engine resolves the reference on the machine running the search. With `-NoGpmcPath`, search by port, program path or action instead.
- **Output shape:** items of the same extension can have different raw properties, so `Format-Table` with wildcards only expands against the first object. Select columns explicitly or use `Format-List *`.
- **Point in time:** the snapshot reflects the domain when it was taken. ADMX display text comes from the central store, or from the local `PolicyDefinitions` of the machine that took the snapshot if there's no central store.

## License

MIT, see `LICENSE.md`.
