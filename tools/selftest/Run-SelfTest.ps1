# MailStudio full test run - builds, runs every test suite and the real-app self-test, writes ONE summary.
#   powershell -ExecutionPolicy Bypass -File tools\selftest\Run-SelfTest.ps1 [-Repo D:\MailStudio] [-Out D:\MailStudio\work\selftest]
# Only synthetic / demo data is used (isolated data folders); nothing is sent, your mailbox is never opened.
param(
    [string]$Repo = (Resolve-Path "$PSScriptRoot\..\..").Path,
    [string]$Out = "",
    [string]$Work = "",
    [switch]$SkipUiTests,
    [string[]]$Languages = @('tr', 'en')
)
$ErrorActionPreference = 'Continue'
if (-not $Work) { $Work = if (Test-Path (Join-Path $Repo 'work\tech-fixtures')) { Join-Path $Repo 'work' } else { 'D:\MailStudio\work' } }
if (-not $Out) { $Out = Join-Path $Work 'selftest' }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$run = Join-Path $Out $stamp
New-Item -ItemType Directory -Force $run | Out-Null
$summary = Join-Path $run 'summary.txt'
function S($text) { Add-Content -Path $summary -Value $text -Encoding UTF8; Write-Host $text }
S "MailStudio full test run $stamp"
S "repo: $Repo · fixtures: $Work"

# ---------------------------------------------------------------- build
Push-Location $Repo
dotnet build MailStudio.sln -c Debug -v q -nologo *> (Join-Path $run 'build.log')
$buildOk = $LASTEXITCODE -eq 0
S ("[build] " + ($(if ($buildOk) { 'OK' } else { 'FAILED' })))
if (-not $buildOk) {
    Select-String -Path (Join-Path $run 'build.log') -Pattern ' error ' | Select-Object -First 20 | ForEach-Object { S ("  " + $_.Line.Trim()) }
    S "DONE"; Pop-Location; exit 1
}

# ---------------------------------------------------------------- core regression (synthetic, temp folder)
$reg = Join-Path $run 'regression.log'
dotnet run --project tests\MailStudio.Regression -c Debug --no-build *> $reg
if (-not (Select-String -Path $reg -Pattern '^RESULT' -Quiet)) {
    # timing-based fake servers / antivirus can make one check flaky: one retry, both logs kept
    Copy-Item $reg (Join-Path $run 'regression-first-try.log') -Force
    dotnet run --project tests\MailStudio.Regression -c Debug --no-build *> $reg
}
$r = Select-String -Path $reg -Pattern '^RESULT' | Select-Object -First 1
S ("[regression] " + $(if ($r) { $r.Line } else { 'FAILED' }))
if (-not $r) { Select-String -Path $reg -Pattern 'Exception:' | Select-Object -First 5 | ForEach-Object { S ("  " + $_.Line.Trim()) } }

# ---------------------------------------------------------------- WPF UI regression (synthetic fixtures from $Work)
if (-not $SkipUiTests) {
    foreach ($lang in $Languages) {
        $ui = Join-Path $run "ui-$lang"
        New-Item -ItemType Directory -Force $ui | Out-Null
        $env:MAILSTUDIO_LANG = $lang
        $env:MAILSTUDIO_DATA_DIR = Join-Path $ui 'data'
        $env:MAILSTUDIO_TECHNICAL_EXE = Join-Path $Work 'technical-dist\MailStudio.Technical\MailStudio.Technical.exe'
        $env:MAILSTUDIO_TECHNICAL_FIXTURES = Join-Path $Work 'tech-fixtures'
        $env:MAILSTUDIO_PDF_STUDIO_SAMPLE = Join-Path $Work 'pdftest\sample.pdf'
        $env:MAILSTUDIO_PPTX_SAMPLE = Join-Path $Work 'viewtest\ornek.pptx'
        $env:MAILSTUDIO_CAD_SAMPLE = Join-Path $Work 'cadtest\flans.dxf'
        $env:MAILSTUDIO_DWG_SAMPLE = Join-Path $Work 'cadtest\example_2018.dwg'
        $env:MAILSTUDIO_DWG2DXF = Join-Path $Work 'third-party\libredwg\dwg2dxf.exe'
        $env:MAILSTUDIO_LIBREOFFICE = Join-Path $Work 'third-party\lo-portable'
        $env:MAILSTUDIO_FORCE_LIBREOFFICE = '1'
        $env:MAILSTUDIO_LEGACY_SAMPLES = Join-Path $Work 'viewtest\legacy'
        $log = Join-Path $run "ui-$lang.log"
        dotnet run --project tests\MailStudio.UiRegression -c Debug 2>&1 | Out-File $log -Encoding utf8
        $res = Select-String -Path $log -Pattern '^RESULT' | Select-Object -First 1
        S ("[ui-$lang] " + $(if ($res) { $res.Line } else { 'FAILED' }))
        Select-String -Path $log -Pattern '^FAIL|UNTRANSLATED|Unhandled' | Select-Object -First 10 | ForEach-Object { S ("  " + $_.Line.Trim()) }
        foreach ($v in 'MAILSTUDIO_LANG','MAILSTUDIO_DATA_DIR','MAILSTUDIO_TECHNICAL_FIXTURES','MAILSTUDIO_PDF_STUDIO_SAMPLE','MAILSTUDIO_PPTX_SAMPLE','MAILSTUDIO_CAD_SAMPLE','MAILSTUDIO_DWG_SAMPLE','MAILSTUDIO_DWG2DXF','MAILSTUDIO_LIBREOFFICE','MAILSTUDIO_FORCE_LIBREOFFICE','MAILSTUDIO_LEGACY_SAMPLES') { Remove-Item "Env:$v" -ErrorAction SilentlyContinue }
    }
}

# ---------------------------------------------------------------- real-app self-test on the demo mailbox
$exe = Join-Path $Repo 'src\MailStudio\bin\Debug\net8.0-windows\MailStudio.exe'
foreach ($lang in $Languages) {
    $dir = Join-Path $run "selftest-$lang"
    $data = Join-Path $run "data-$lang"
    New-Item -ItemType Directory -Force $dir, $data | Out-Null
    $env:MAILSTUDIO_DATA_DIR = $data
    $env:MAILSTUDIO_LANG = $lang
    if (Test-Path (Join-Path $Work 'technical-dist\MailStudio.Technical\MailStudio.Technical.exe')) { $env:MAILSTUDIO_TECHNICAL_EXE = Join-Path $Work 'technical-dist\MailStudio.Technical\MailStudio.Technical.exe' }
    $p = Start-Process $exe -ArgumentList '--demo', '--selftest', "`"$dir`"" -PassThru
    if (-not $p.WaitForExit(20 * 60 * 1000)) {
        S "[selftest-$lang] TIMEOUT after 20 min (process $($p.Id) stopped)"
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    }
    $st = Join-Path $dir 'summary.txt'
    if (Test-Path $st) { Get-Content $st -Encoding UTF8 | Select-Object -Skip 1 | ForEach-Object { S ("[selftest-$lang] " + $_) } }
    else { S "[selftest-$lang] no summary (crashed?) - see $dir\selftest.log" }
    Remove-Item Env:MAILSTUDIO_DATA_DIR, Env:MAILSTUDIO_LANG -ErrorAction SilentlyContinue
}

# ---------------------------------------------------------------- MCP agent server smoke test (demo data of the self-test)
$mcp = Join-Path $Repo 'src\MailStudio.Mcp\bin\Debug\net8.0\MailStudio.Mcp.exe'
if (Test-Path $mcp) {
    $env:MAILSTUDIO_DATA_DIR = Join-Path $run "data-$($Languages[0])"
    $mlog = Join-Path $run 'mcp.log'
    try {
        $psi = New-Object System.Diagnostics.ProcessStartInfo $mcp; $psi.Arguments = '--permission draft'
        $psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true; $psi.UseShellExecute = $false
        $psi.StandardOutputEncoding = [Text.Encoding]::UTF8
        $mp = [Diagnostics.Process]::Start($psi)
        function Send($o) { $mp.StandardInput.WriteLine(($o | ConvertTo-Json -Compress -Depth 10)); $mp.StandardInput.Flush() }
        Send @{ jsonrpc = '2.0'; id = 1; method = 'initialize'; params = @{ protocolVersion = '2025-06-18'; capabilities = @{}; clientInfo = @{ name = 'selftest'; version = '1' } } }
        $null = $mp.StandardOutput.ReadLine()
        Send @{ jsonrpc = '2.0'; method = 'notifications/initialized' }
        Send @{ jsonrpc = '2.0'; id = 2; method = 'tools/list' }
        $tools = ($mp.StandardOutput.ReadLine() | ConvertFrom-Json).result.tools | ForEach-Object name
        Add-Content $mlog ("tools: " + ($tools -join ', '))
        $bad = 0
        $calls = @(
            @{ n = 'list_accounts'; a = @{} }, @{ n = 'list_messages'; a = @{ limit = 5 } }, @{ n = 'search_messages'; a = @{ query = 'flange' } },
            @{ n = 'read_message'; a = @{ account = 'demo'; folder = 'INBOX'; uid = 1 } }, @{ n = 'list_skills'; a = @{} },
            @{ n = 'list_events'; a = @{ days = 7 } }, @{ n = 'find_free_time'; a = @{ minutes = 30 } }, @{ n = 'list_tasks'; a = @{} },
            @{ n = 'list_rules'; a = @{} }, @{ n = 'list_followups'; a = @{ filter = 'all' } }, @{ n = 'list_quotes'; a = @{ filter = 'all' } },
            @{ n = 'add_task'; a = @{ title = 'MCP self-test task' } },
            @{ n = 'add_rule'; a = @{ name = 'Smoke Rule'; conditionField = 'from'; conditionValue = 'smoke@example.com'; actionType = 'flag' } }
        )
        $id = 10
        foreach ($c in $calls) {
            Send @{ jsonrpc = '2.0'; id = $id; method = 'tools/call'; params = @{ name = $c.n; arguments = $c.a } }; $id++
            $resp = $mp.StandardOutput.ReadLine() | ConvertFrom-Json
            $err = $resp.result.isError -or $resp.error
            if ($err) { $bad++ }
            $t = [string]$resp.result.content[0].text
            Add-Content $mlog ("{0} -> {1} {2}" -f $c.n, $(if ($err) { 'ERROR' } else { 'ok' }), $t.Substring(0, [Math]::Min(300, $t.Length)))
        }
        $hasSend = $tools | Where-Object { $_ -match 'send' }
        $mp.StandardInput.Close(); $mp.Kill()
        S ("[mcp] {0} tools, {1} calls, {2} errors{3}" -f $tools.Count, $calls.Count, $bad, $(if ($hasSend) { ' - FAIL: a send tool exists!' } else { '' }))
    }
    catch { S "[mcp] FAILED: $($_.Exception.Message)" }
    Remove-Item Env:MAILSTUDIO_DATA_DIR -ErrorAction SilentlyContinue
}
Pop-Location
S "screenshots and logs: $run"
S "DONE"
Copy-Item $summary (Join-Path $Out 'latest-summary.txt') -Force
