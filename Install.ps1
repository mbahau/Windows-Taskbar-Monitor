# Installs SysMonitor: builds the exe (if needed) and enables auto-start at login.
# Run this from the folder containing SysMonitor.cs / SysMonitor.exe.
$ErrorActionPreference = "Stop"
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $dir "SysMonitor.exe"

if (-not (Test-Path $exe)) {
    Write-Host "Building SysMonitor.exe ..."
    $csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
    if (-not (Test-Path $csc)) { $csc = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe" }
    & $csc /nologo /target:winexe /platform:anycpu /optimize+ `
        /out:$exe /win32manifest:"$dir\app.manifest" `
        /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll `
        "$dir\SysMonitor.cs"
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }
}

# Register auto-start (per-user, no admin required)
$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
Set-ItemProperty -Path $runKey -Name "SysMonitor" -Value "`"$exe`""

Write-Host "Installed. Starting now..."
Start-Process -FilePath $exe
Write-Host "Done. Look for the CPU-% icon in your taskbar tray (may be under the ^ overflow arrow)."
Write-Host "It will now start automatically every time you log in."
