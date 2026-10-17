# Builds a trimmed, portable 'office' folder for MailStudio from a LibreOffice administrative install (msiexec /a).
param(
  [string]$Src = 'work\third-party\lo-admin',   # msiexec /a LibreOffice_x.y.z_Win_x86-64.msi /qn TARGETDIR=<this folder>
  [string]$Dst = 'work\third-party\lo-portable'
)
$ErrorActionPreference = 'Stop'
if (Test-Path $Dst) { Remove-Item $Dst -Recurse -Force }
New-Item -ItemType Directory $Dst | Out-Null
foreach ($d in 'program','share','Fonts','presets') { robocopy "$Src\$d" "$Dst\$d" /E /NFL /NDL /NJH /NJS /NP | Out-Null }
foreach ($f in 'LICENSE.html','license.txt','NOTICE','CREDITS.fodt') { if (Test-Path "$Src\$f") { Copy-Item "$Src\$f" $Dst } }
# Visual C++ runtime next to the app (app-local deployment)
Copy-Item "$Src\System64\*.dll" "$Dst\program" -Force

$keepLang = @('en-US','tr')
# UI translations
Get-ChildItem "$Dst\program\resource" -Directory | ? { $keepLang -notcontains $_.Name } | Remove-Item -Recurse -Force
# Language pack configuration
Get-ChildItem "$Dst\share\registry\res" -File | ? { $n=$_.BaseName; -not ($keepLang | ? { $n.EndsWith("_$_") }) } | Remove-Item -Force
# Spell-check dictionaries and unneeded extensions (not used for conversion)
Get-ChildItem "$Dst\share\extensions" -Directory | ? { $_.Name -ne 'dict-tr' -and $_.Name -ne 'dict-en' } | Remove-Item -Recurse -Force
Remove-Item "$Dst\share\extensions\dict-tr","$Dst\share\extensions\dict-en" -Recurse -Force
# Icon themes: not needed headless, only the default is kept
Get-ChildItem "$Dst\share\config" -Filter 'images_*.zip' | ? { $_.Name -ne 'images_colibre.zip' } | Remove-Item -Force
# Gallery, tips, autotext translations
foreach ($d in 'gallery','tipoftheday','wizards') { if (Test-Path "$Dst\share\$d") { Remove-Item "$Dst\share\$d" -Recurse -Force } }
Get-ChildItem "$Dst\share\autotext","$Dst\share\autocorr","$Dst\share\template" -Directory -ErrorAction SilentlyContinue | ? { $_.Name -notin @('en-US','tr','common') } | Remove-Item -Recurse -Force
Get-ChildItem "$Dst\share\autocorr" -File -ErrorAction SilentlyContinue | ? { $_.Name -notmatch 'acor_(en-US|tr-TR)\.dat' } | Remove-Item -Force

$mb = (Get-ChildItem $Dst -Recurse -File | Measure-Object Length -Sum).Sum/1MB
"Boyut: {0:N0} MB" -f $mb
