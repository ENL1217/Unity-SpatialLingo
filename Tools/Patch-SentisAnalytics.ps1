# Sentis 2.6.1 analytics reads rank even when the imported model has a dynamic rank.
# Keep Unity/Sentis current; change only editor import telemetry, never the inference graph.
$ErrorActionPreference = 'Stop'
$taskProject = Split-Path $PSScriptRoot -Parent
$taskPackages = Get-ChildItem (Join-Path $taskProject 'Library/PackageCache') -Directory -Filter 'com.unity.ai.inference@*'
$taskChanged = 0
foreach ($taskPackage in $taskPackages) {
    $taskManifest = Get-Content (Join-Path $taskPackage.FullName 'package.json') -Raw | ConvertFrom-Json
    if ($taskManifest.version -ne '2.6.1') { continue }
    $taskSource = Join-Path $taskPackage.FullName 'Editor/Analytics/SentisModelImportAnalyticsHelper.cs'
    $taskText = [IO.File]::ReadAllText($taskSource)
    $taskBefore = 'if (dataType.HasValue && shape.HasValue)'
    $taskAfter = 'if (dataType.HasValue && shape.HasValue && !shape.Value.isRankDynamic)'
    if ($taskText.Contains($taskAfter)) { Write-Output 'Sentis analytics workaround already applied.'; continue }
    if (-not $taskText.Contains($taskBefore)) { throw 'Sentis source changed; review the analytics workaround.' }
    (Get-Item -LiteralPath $taskSource).IsReadOnly = $false
    [IO.File]::WriteAllText($taskSource, $taskText.Replace($taskBefore, $taskAfter))
    $taskChanged++
}
Write-Output "Sentis 2.6.1 analytics workaround: $taskChanged file(s) patched."
