param([ValidateSet('Features','Startup','Full','Sidebar','Workflow','Indicators')][string]$Mode = 'Features', [ValidatePattern('^[A-Za-z0-9.-]*$')][string]$ReportPrefix = '')
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -ne 'Desktop') { throw 'Use Windows PowerShell 5.1.' }
$taskRoot = Split-Path $PSScriptRoot -Parent
$taskPackage = Get-AppxPackage -Name NetEaseNEXT
if (-not $taskPackage -or $taskPackage.Version -lt [version]'0.3.0.0') { throw 'Install the 0.3.0 package first.' }
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
[ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
class NativePackageActivator {}
[ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IApplicationActivationManager {
    [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appId,
        [MarshalAs(UnmanagedType.LPWStr)] string arguments, uint options, out uint processId);
}
public static class PackageCheckLauncher {
    public static uint Start(string id, string args) {
        var manager = (IApplicationActivationManager)new NativePackageActivator();
        try { uint pid; Marshal.ThrowExceptionForHR(manager.ActivateApplication(id, args, 2, out pid)); return pid; }
        finally { Marshal.FinalReleaseComObject(manager); }
    }
}
'@
$taskArgs = switch ($Mode) {
    'Features' { '--smoke-test --native-feature-check' }
    'Startup' { '--smoke-test --startup-menu-check' }
    'Full' { '--smoke-test --social-read --measure-background' }
    'Sidebar' { '--smoke-test --sidebar-theme-check' }
    'Workflow' { '--smoke-test --library-workflow-check' }
    'Indicators' { '--smoke-test --download-indicator-check' }
}
$taskReport = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'NetEaseNEXT\smoke-test.json'
$taskStarted = Get-Date
$taskPid = [PackageCheckLauncher]::Start(($taskPackage.PackageFamilyName + '!App'), $taskArgs)
$taskNodeChildren = @()
$taskClock = [Diagnostics.Stopwatch]::StartNew()
do {
    $taskProcess = Get-Process -Id $taskPid -ErrorAction SilentlyContinue
    if (-not $taskProcess) { break }
    $taskNodeChildren += @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $taskPid" | Where-Object Name -EQ 'node.exe' | Select-Object ProcessId,ParentProcessId,Name)
    [void]$taskProcess.WaitForExit(1000)
} while ($taskClock.Elapsed.TotalSeconds -lt 180)
if (Get-Process -Id $taskPid -ErrorAction SilentlyContinue) { throw "Check still running: $taskPid" }
if (-not (Test-Path -LiteralPath $taskReport) -or (Get-Item -LiteralPath $taskReport).LastWriteTime -lt $taskStarted) { throw 'No fresh report was generated.' }
$taskDate = (Get-Date).ToString('yyyy-MM-dd')
$taskDestination = Join-Path $PSScriptRoot ($ReportPrefix + 'installed-' + $Mode.ToLowerInvariant() + '-' + $taskDate + '.json')
Copy-Item -LiteralPath $taskReport -Destination $taskDestination -Force
$taskResults = Get-Content -LiteralPath $taskReport -Raw | ConvertFrom-Json
$taskFailed = @($taskResults | Where-Object { $_.PSObject.Properties.Name -contains 'passed' -and -not $_.passed })
[ordered]@{ Mode=$Mode; Package=$taskPackage.PackageFullName; ProcessId=$taskPid; Checks=$taskResults.Count; Failures=$taskFailed.Count; NodeChildren=@($taskNodeChildren | Sort-Object ProcessId -Unique); ElapsedSeconds=[math]::Round($taskClock.Elapsed.TotalSeconds,2) } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $PSScriptRoot ($ReportPrefix + 'installed-' + $Mode.ToLowerInvariant() + '-process-' + $taskDate + '.json')) -Encoding UTF8
if ($taskFailed.Count -or $taskNodeChildren.Count) { $taskFailed | ConvertTo-Json -Depth 6; throw 'Installed verification failed.' }
Write-Host "$Mode passed: $($taskResults.Count) entries, zero Node children."
