# Stops the running engine cleanly (WM_CLOSE to its window removes the tray icon), killing it only as a fallback.
param([int]$TimeoutSeconds = 5)

Add-Type -Namespace CtrlAltDaze -Name Win32 -MemberDefinition @'
[DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowW(string cls, string title);
[DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
'@

$procs = @(Get-Process CtrlAltDaze -ErrorAction SilentlyContinue)
if ($procs.Count -eq 0) { return }
while (($hwnd = [CtrlAltDaze.Win32]::FindWindowW('CtrlAltDaze.Tray', $null)) -ne [IntPtr]::Zero) {
    [void][CtrlAltDaze.Win32]::PostMessageW($hwnd, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) # WM_CLOSE
    Start-Sleep -Milliseconds 200
}
foreach ($p in $procs) {
    if (-not $p.WaitForExit($TimeoutSeconds * 1000)) {
        Write-Host "stop: pid $($p.Id) didn't exit, killing it" -ForegroundColor Yellow
        Stop-Process -Id $p.Id -Force
    }
}
Write-Host "stop: engine stopped"
