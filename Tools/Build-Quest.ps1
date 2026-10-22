param(
    [string]$Unity = 'C:/Users/jaja/UnityEditors/6000.3.27f1/Editor/Unity.exe',
    [string]$Sdk = "$env:LOCALAPPDATA/Android/Sdk",
    [string]$Ndk,
    [string]$Jdk,
    [switch]$SkipRestore,
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$taskProject = Split-Path $PSScriptRoot -Parent
if (-not $Ndk) { $Ndk = Join-Path $Sdk 'ndk/27.2.12479018' }
if (-not $Jdk) {
    $Jdk = (Get-ChildItem (Join-Path $taskProject '.local/toolchain') -Directory -Filter 'jdk-17*' |
        Select-Object -First 1).FullName
}
foreach ($taskRequired in @($Unity, "$Sdk/platform-tools/adb.exe", "$Ndk/source.properties", "$Jdk/bin/java.exe")) {
    if (-not (Test-Path -LiteralPath $taskRequired)) { throw "Missing build dependency: $taskRequired" }
}
New-Item -ItemType Directory (Join-Path $taskProject 'Logs') -Force | Out-Null
$env:AIENGLISH_ANDROID_SDK = $Sdk
$env:AIENGLISH_ANDROID_NDK = $Ndk
$env:AIENGLISH_JAVA_HOME = $Jdk
function Invoke-TaskUnity([string[]]$TaskArguments) {
    $taskArgumentsAll = @('-batchmode', '-nographics', '-projectPath', $taskProject, '-buildTarget', 'Android') + $TaskArguments
    $taskProcess = Start-Process -FilePath $Unity -ArgumentList ($taskArgumentsAll | ForEach-Object { '"' + $_ + '"' }) -WindowStyle Hidden -PassThru
    # Wait for this editor, not shared licensing/helper child processes.
    $taskProcess.WaitForExit()
    if ($taskProcess.ExitCode -ne 0) { throw "Unity exited with code $($taskProcess.ExitCode). See Logs." }
}
Push-Location $taskProject
try {
    if (-not $SkipRestore) { Invoke-TaskUnity @('-quit', '-accept-apiupdate', '-logFile', "$taskProject/Logs/unity-restore.log") }
    & "$PSScriptRoot/Patch-SentisAnalytics.ps1"
    if (-not $SkipTests) {
        $taskResults = "$taskProject/Logs/openrouter-tests.xml"
        if (Test-Path -LiteralPath $taskResults) { Remove-Item -LiteralPath $taskResults }
        Invoke-TaskUnity @('-runTests', '-testPlatform', 'EditMode', '-testFilter', 'OpenRouterRequestTests;WitRuntimeConfigurationTests;CameraStartupTests;RoomStartupTests',
            '-testResults', $taskResults, '-logFile', "$taskProject/Logs/unity-tests.log")
        [xml]$taskTestXml = Get-Content -LiteralPath $taskResults
        if ($taskTestXml.'test-run'.result -ne 'Passed') { throw 'OpenRouter request tests did not pass.' }
    }
    Invoke-TaskUnity @('-quit', '-executeMethod', 'AIEnglishBuild.Android', '-logFile', "$taskProject/Logs/unity-build.log")
    Get-Item (Join-Path $taskProject 'Builds/AIEnglish-OpenRouter.apk')
}
finally { Pop-Location }
