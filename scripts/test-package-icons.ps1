param(
    [Parameter(Mandatory)][string]$PackageRoot,
    [Parameter(Mandatory)][string]$SdkTools,
    [string]$ReportPath
)
$ErrorActionPreference = 'Stop'
$PackageRoot = (Resolve-Path -LiteralPath $PackageRoot).Path
if (-not $ReportPath) { $ReportPath = Join-Path ([IO.Path]::GetTempPath()) ('NetEaseNEXT-icons-' + [guid]::NewGuid().ToString('N') + '.json') }
$taskDump = [IO.Path]::ChangeExtension($ReportPath,'.pri.xml')
& (Join-Path $SdkTools 'makepri.exe') dump /if (Join-Path $PackageRoot 'resources.pri') /of $taskDump /dt detailed /o | Out-Null
if ($LASTEXITCODE) { throw 'Cannot inspect the package resource index.' }
[xml]$taskResources = Get-Content -LiteralPath $taskDump -Raw
[xml]$taskManifest = Get-Content -LiteralPath (Join-Path $PackageRoot 'AppxManifest.xml') -Raw
$taskResourcesByUri = @{}
foreach($taskResource in $taskResources.SelectNodes('//NamedResource[@uri]')) { $taskResourcesByUri[$taskResource.uri] = $taskResource }
$taskVisual = $taskManifest.Package.Applications.Application.VisualElements
$taskLogos = @($taskManifest.Package.Properties.Logo,$taskVisual.Square44x44Logo,$taskVisual.Square150x150Logo)
$taskResults = foreach($taskLogo in $taskLogos) {
    $taskUri = 'ms-resource://' + $taskManifest.Package.Identity.Name + '/Files/' + $taskLogo.Replace('\','/')
    $taskResource = $taskResourcesByUri[$taskUri]
    $taskCandidates = if($taskResource) { @($taskResource.Candidate) } else { @() }
    $taskMissing = @($taskCandidates | Where-Object { -not (Test-Path -LiteralPath (Join-Path $PackageRoot $_.Value)) })
    [pscustomobject]@{ Logo=$taskLogo; Uri=$taskUri; Passed=$null -ne $taskResource -and $taskCandidates.Count -gt 0 -and $taskMissing.Count -eq 0; Candidates=$taskCandidates.Count }
}
$taskMainLogo = $taskResourcesByUri[('ms-resource://' + $taskManifest.Package.Identity.Name + '/Files/' + $taskVisual.Square44x44Logo.Replace('\','/'))]
foreach($taskForm in @('unplated','lightunplated')) {
    $taskThemed = @($taskMainLogo.Candidate | Where-Object { @($_.QualifierSet.Qualifier | Where-Object { $_.name -eq 'AlternateForm' -and $_.value -eq $taskForm }).Count -gt 0 })
    $taskResults += [pscustomobject]@{Logo=$taskForm;Uri=$taskVisual.Square44x44Logo;Passed=$taskThemed.Count -gt 0;Candidates=$taskThemed.Count}
}
$taskResults | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $ReportPath -Encoding utf8
$taskResults | Format-Table Logo,Passed,Candidates | Out-String | Write-Host
if (@($taskResults | Where-Object {-not $_.Passed}).Count) { throw "Manifest icon paths do not resolve in the PRI: $ReportPath" }
