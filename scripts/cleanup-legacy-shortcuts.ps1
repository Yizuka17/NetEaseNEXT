param([switch]$WhatIf)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -ne 'Desktop') { throw 'Run this script with Windows PowerShell 5.1 (powershell.exe).' }
$taskPackage = Get-AppxPackage -Name NetEaseNEXT
if (-not $taskPackage -or $taskPackage.Publisher -cne 'CN=NetEaseNEXT Development') { throw 'A verified NetEaseNEXT MSIX installation is required; shortcuts were retained.' }
$taskShell = New-Object -ComObject WScript.Shell
$taskExplorer = New-Object -ComObject Shell.Application
$taskPrograms = [Environment]::GetFolderPath('Programs')
$taskDesktop = [Environment]::GetFolderPath('DesktopDirectory')
$taskPinned = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar'
$taskCandidates = @((Join-Path $taskPrograms 'NetEaseNEXT.lnk'), (Join-Path $taskPrograms 'NetEaseNEXT\NetEaseNEXT.lnk'), (Join-Path $taskDesktop 'NetEaseNEXT.lnk'))
if (Test-Path -LiteralPath $taskPinned) { $taskCandidates += @(Get-ChildItem -LiteralPath $taskPinned -Filter '*.lnk' -File | Select-Object -ExpandProperty FullName) }
$taskBackupRoot = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'NetEaseNEXT\MigrationBackups'))
$taskBackup = Join-Path $taskBackupRoot ([DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N'))
$taskResults = @()
foreach ($taskCandidate in ($taskCandidates | Select-Object -Unique)) {
    if (-not (Test-Path -LiteralPath $taskCandidate)) { continue }
    $taskPath = [IO.Path]::GetFullPath($taskCandidate)
    $taskLink = $taskShell.CreateShortcut($taskPath)
    $taskTarget = $taskLink.TargetPath
    if ([IO.Path]::GetFileName($taskTarget) -ine 'NetEaseNEXT.exe') { continue }
    if ($taskTarget.StartsWith($taskPackage.InstallLocation, [StringComparison]::OrdinalIgnoreCase)) { continue }
    $taskNamespace = $taskExplorer.NameSpace([IO.Path]::GetDirectoryName($taskPath))
    $taskItem = $taskNamespace.ParseName([IO.Path]::GetFileName($taskPath))
    $taskAppId = $taskItem.ExtendedProperty('System.AppUserModel.ID')
    $taskExists = Test-Path -LiteralPath $taskTarget
    $taskProduct = if ($taskExists) { [Diagnostics.FileVersionInfo]::GetVersionInfo($taskTarget).ProductName } else { '' }
    if ($taskAppId -cne 'NetEaseNEXT.Desktop' -and $taskProduct -cne 'NetEaseNEXT') { continue }
    $taskDestination = Join-Path $taskBackup (([string]$taskResults.Count) + '-' + [IO.Path]::GetFileName($taskPath))
    if (-not [IO.Path]::GetFullPath($taskDestination).StartsWith($taskBackupRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected shortcut backup path.' }
    $taskResults += [pscustomobject]@{ Shortcut = $taskPath; Target = $taskTarget; TargetExists = $taskExists; AppId = $taskAppId; Backup = $taskDestination; Moved = -not $WhatIf }
    if (-not $WhatIf) {
        New-Item -ItemType Directory -Path $taskBackup -Force | Out-Null
        # Quarantine individual installer-owned links. Never delete an app folder,
        # touch a packaged shortcut, unpin unrelated apps or remove user data.
        Move-Item -LiteralPath $taskPath -Destination $taskDestination
        $taskResults | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $taskBackup 'shortcuts.json') -Encoding UTF8
    }
}
$taskIdentityPath = 'HKCU:\Software\Classes\AppUserModelId\NetEaseNEXT.Desktop'
if (Test-Path -LiteralPath $taskIdentityPath) {
    $taskIdentity = Get-Item -LiteralPath $taskIdentityPath
    $taskValues = Get-ItemProperty -LiteralPath $taskIdentityPath
    $taskUnexpected = @($taskIdentity.GetValueNames() | Where-Object { $_ -notin @('DisplayName','IconUri','IconBackgroundColor') })
    if ($taskValues.DisplayName -ceq 'NetEaseNEXT' -and $taskIdentity.SubKeyCount -eq 0 -and $taskUnexpected.Count -eq 0) {
        $taskRegistryBackup = 'HKCU:\Software\NetEaseNEXT\MigrationBackups\' + [guid]::NewGuid().ToString('N')
        $taskResults += [pscustomobject]@{ Identity = $taskIdentityPath; Backup = $taskRegistryBackup; Moved = -not $WhatIf }
        if (-not $WhatIf) {
            New-Item -ItemType Directory -Path $taskBackup -Force | Out-Null
            & reg.exe export 'HKCU\Software\Classes\AppUserModelId\NetEaseNEXT.Desktop' (Join-Path $taskBackup 'legacy-identity.reg') /y | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Identity backup failed; the active registration was retained.' }
            New-Item -Path $taskRegistryBackup -Force | Out-Null
            Move-Item -LiteralPath $taskIdentityPath -Destination (Join-Path $taskRegistryBackup 'Identity')
            $taskResults | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $taskBackup 'shortcuts.json') -Encoding UTF8
        }
    }
}
$taskResults | ConvertTo-Json -Depth 4
