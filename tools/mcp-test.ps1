# Smoke test of the MCP server over stdio (development build).
$exe = Join-Path $PSScriptRoot '..\src\MailStudio.Mcp\bin\Debug\net8.0\MailStudio.Mcp.exe'
$psi = New-Object System.Diagnostics.ProcessStartInfo $exe
$psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
$psi.UseShellExecute = $false
$psi.StandardOutputEncoding = [Text.Encoding]::UTF8
$p = [Diagnostics.Process]::Start($psi)
function Send($o) { $p.StandardInput.WriteLine(($o | ConvertTo-Json -Compress -Depth 10)); $p.StandardInput.Flush() }
Send @{ jsonrpc = "2.0"; id = 1; method = "initialize"; params = @{ protocolVersion = "2025-06-18"; capabilities = @{}; clientInfo = @{ name = "test"; version = "1" } } }
$p.StandardOutput.ReadLine() | Out-Null
Send @{ jsonrpc = "2.0"; method = "notifications/initialized" }
Send @{ jsonrpc = "2.0"; id = 2; method = "tools/list" }
$list = $p.StandardOutput.ReadLine() | ConvertFrom-Json
"ARAÇLAR: " + (($list.result.tools | % name) -join ", ")
Send @{ jsonrpc = "2.0"; id = 3; method = "tools/call"; params = @{ name = "list_accounts"; arguments = @{} } }
$r = $p.StandardOutput.ReadLine() | ConvertFrom-Json
"list_accounts -> " + $r.result.content[0].text
Send @{ jsonrpc = "2.0"; id = 4; method = "tools/call"; params = @{ name = "list_folders"; arguments = @{} } }
$r = $p.StandardOutput.ReadLine() | ConvertFrom-Json
"list_folders -> isError=" + $r.result.isError + " " + $r.result.content[0].text
$p.StandardInput.Close(); $p.Kill()
