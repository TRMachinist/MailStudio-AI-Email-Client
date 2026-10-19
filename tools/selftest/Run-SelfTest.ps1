# MailStudio full test run - builds, runs every test suite and the real-app self-test, writes ONE summary.
#   powershell -ExecutionPolicy Bypass -File tools\selftest\Run-SelfTest.ps1 [-Repo D:\MailStudio] [-Out D:\MailStudio\work\selftest]
# Only synthetic / demo data is used (isolated data folders); nothing is sent, your mailbox is never opened.
param(
    [string]$Repo = (Resolve-Path "$PSScriptRoot\..\..").Path,
    [string]$Out = "",
    [switch]$SkipUiTests,
    [string[]]$Languages = @('tr', 'en')
)
$ErrorActionPreference = 'Continue'
if (-not $Out) { $Out = Join-Path $Repo 'work\selftest' }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$run = Join-Path $Out $stamp
New-Item -ItemType Directory -Force $run | Out-Null
$summary = Join-Path $run 'summary.txt'
function S($text) { Add-Content -Path $summary -Value $text -Encoding UTF8; Write-Host $text }
S "MailStudio full test run $stamp"
S "repo: $Repo"

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

# ---------------------------------------------------------------- WPF UI regression (needs the machine's fixture setup)
$uiScript = Join-Path $Repo 'work\run-ui-tests.ps1'
if (-not $SkipUiTests -and (Test-Path $uiScript)) {
    foreach ($lang in $Languages) {
        powershell -NoProfile -ExecutionPolicy Bypass -File $uiScript -Lang $lang *> (Join-Path $run "ui-$lang-console.log")
        $full = Join-Path $Repo 'work\ui-test-out.txt'
        if (Test-Path $full) { Copy-Item $full (Join-Path $run "ui-$lang.log") -Force }
        $log = Join-Path $run "ui-$lang.log"
        $res = if (Test-Path $log) { Select-String -Path $log -Pattern '^RESULT' | Select-Object -First 1 } else { $null }
        S ("[ui-$lang] " + $(if ($res) { $res.Line } else { 'FAILED' }))
        if (Test-Path $log) { Select-String -Path $log -Pattern '^FAIL|UNTRANSLATED' | Select-Object -First 10 | ForEach-Object { S ("  " + $_.Line.Trim()) } }
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
    if (Test-Path (Join-Path $Repo 'work\technical-dist\MailStudio.Technical\MailStudio.Technical.exe')) { $env:MAILSTUDIO_TECHNICAL_EXE = Join-Path $Repo 'work\technical-dist\MailStudio.Technical\MailStudio.Technical.exe' }
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
            @{ n = 'add_task'; a = @{ title = 'MCP self-test task' } }
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
