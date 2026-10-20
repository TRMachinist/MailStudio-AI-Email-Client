# Runs a command on a separate, invisible Windows desktop of the current session.
# The self-test, UI tests and video recordings open many windows; on their own desktop they
# never appear on the user's screen, never take the focus and the user can keep working.
#
#   powershell -ExecutionPolicy Bypass -File tools\selftest\Start-OnHiddenDesktop.ps1 -Command "powershell -ExecutionPolicy Bypass -File ...\Run-SelfTest.ps1 ..." -Wait
param(
    [Parameter(Mandatory = $true)][string]$Command,
    [string]$Desktop = 'MailStudioTest',
    [switch]$Wait
)
# Without -Wait this script restarts itself detached and returns at once (the detached copy holds the desktop open).
if (-not $Wait) {
    $self = $MyInvocation.MyCommand.Path
    $p = Start-Process powershell -WindowStyle Hidden -PassThru -ArgumentList @('-ExecutionPolicy', 'Bypass', '-File', "`"$self`"", '-Command', "`"$($Command -replace '"', '\"')`"", '-Desktop', $Desktop, '-Wait')
    "started on desktop '$Desktop' (launcher pid $($p.Id))"
    return
}
$ErrorActionPreference = 'Stop'
if (-not ('MailStudioHiddenDesktop' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class MailStudioHiddenDesktop
{
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr devmode, int flags, uint access, IntPtr sa);
    [DllImport("user32.dll", SetLastError = true)] static extern bool CloseDesktop(IntPtr h);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO { public int cb; public string lpReserved; public string lpDesktop; public string lpTitle; public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags; public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError; }
    [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcess(string app, string cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr h, out uint code);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    public static int Run(string desktop, string command, string dir, bool wait)
    {
        const uint GENERIC_ALL = 0x10000000;
        var hDesk = CreateDesktop(desktop, IntPtr.Zero, IntPtr.Zero, 0, GENERIC_ALL, IntPtr.Zero);
        if (hDesk == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "CreateDesktop");
        try
        {
            var si = new STARTUPINFO { cb = Marshal.SizeOf(typeof(STARTUPINFO)), lpDesktop = desktop };
            PROCESS_INFORMATION pi;
            const uint CREATE_NO_WINDOW = 0x08000000;
            if (!CreateProcess(null, command, IntPtr.Zero, IntPtr.Zero, false, CREATE_NO_WINDOW, IntPtr.Zero, dir, ref si, out pi))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "CreateProcess");
            CloseHandle(pi.hThread);
            // Keep the desktop handle until the process ends: a desktop with no handle left is destroyed
            // and a process that has not attached yet fails to start.
            WaitForSingleObject(pi.hProcess, 0xFFFFFFFF);
            uint code; GetExitCodeProcess(pi.hProcess, out code);
            CloseHandle(pi.hProcess);
            return (int)code;
        }
        finally { CloseDesktop(hDesk); }   // the desktop lives on while processes use it
    }
}
'@
}
$r = [MailStudioHiddenDesktop]::Run($Desktop, $Command, (Get-Location).Path, $true)
"exit $r"; exit $r
