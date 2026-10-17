# Builds the portable Windows x64 release folder (no installation needed on the target computer).
#
#   powershell -ExecutionPolicy Bypass -File tools\packaging\build-release.ps1 -Out work\release
#
# Inputs (see docs/BUILDING.md):
#   -Technical  frozen technical worker folder (PyInstaller output of src/MailStudio.Technical)
#   -LibreDwg   folder with dwg2dxf.exe + DLLs (LibreDWG win64 release, GPLv3, shipped as a separate program)
#   -Office     trimmed portable LibreOffice folder made by make-lo-portable.ps1 (MPL-2.0)
#   -Notices    ThirdPartyNotices folder (license texts of the bundled components)
param(
  [string]$Out = 'work\release',
  [string]$Technical = 'work\technical-dist\MailStudio.Technical',
  [string]$LibreDwg = 'work\third-party\libredwg',
  [string]$Office = 'work\third-party\lo-portable',
  [string]$Notices = 'work\ThirdPartyNotices'
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
Set-Location $root
if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }

dotnet publish src\MailStudio\MailStudio.csproj -c Release -r win-x64 --self-contained true -o $Out -nologo -v q
if ($LASTEXITCODE) { throw 'publish MailStudio failed' }
dotnet publish src\MailStudio.Mcp\MailStudio.Mcp.csproj -c Release -r win-x64 --self-contained true -o "$Out\mcp" -nologo -v q
if ($LASTEXITCODE) { throw 'publish MCP failed' }

function Copy-Dir($from, $to) { if (Test-Path $from) { robocopy $from $to /E /NFL /NDL /NJH /NJS /NP | Out-Null } else { Write-Warning "missing: $from" } }
Copy-Dir $Technical "$Out\technical"
Copy-Dir $LibreDwg "$Out\technical\libredwg"
Copy-Dir $Office "$Out\office"
Copy-Dir $Notices "$Out\ThirdPartyNotices"
Copy-Dir docs "$Out\docs"
Remove-Item "$Out\docs\media" -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item README.md, README.tr.md, LICENSE, CHANGELOG.md $Out -ErrorAction SilentlyContinue
Copy-Item src\MailStudio\Resources\MailStudio.ico $Out
if (Test-Path "$Out\office") {
  New-Item -ItemType Directory "$Out\ThirdPartyNotices\LibreOffice" -Force | Out-Null
  foreach ($f in 'LICENSE.html', 'license.txt', 'NOTICE', 'CREDITS.fodt') { if (Test-Path "$Out\office\$f") { Copy-Item "$Out\office\$f" "$Out\ThirdPartyNotices\LibreOffice" } }
}
'{0:N0} MB  {1}' -f ((Get-ChildItem $Out -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), (Resolve-Path $Out)
