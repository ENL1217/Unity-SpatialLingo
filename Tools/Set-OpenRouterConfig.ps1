param(
    [string]$Model = 'nvidia/nemotron-3.5-super-vl:free',
    [string]$Adb = "$env:LOCALAPPDATA/Android/Sdk/platform-tools/adb.exe",
    [string]$Serial,
    [switch]$WithWit,
    [switch]$Push
)
$ErrorActionPreference = 'Stop'
if ($Model -notmatch '^[^\s/?#]+/[^\s?#]+:free$') { throw 'Only explicit :free model IDs are allowed.' }
$taskProject = Split-Path $PSScriptRoot -Parent
$taskSecret = Read-Host 'OpenRouter API key (hidden; never paste it in chat)' -AsSecureString
$taskPlain = [System.Net.NetworkCredential]::new('', $taskSecret).Password
if (-not $taskPlain.StartsWith('sk-or-')) { throw 'Not an OpenRouter API key.' }
$taskConfig = @{ apiKey = $taskPlain; model = $Model }
if ($WithWit) {
    $taskWitEnglish = Read-Host 'Wit.ai English client access token (hidden)' -AsSecureString
    $taskWitSpanish = Read-Host 'Wit.ai Spanish client access token (hidden)' -AsSecureString
    $taskConfig.witEnglishToken = [System.Net.NetworkCredential]::new('', $taskWitEnglish).Password
    $taskConfig.witSpanishToken = [System.Net.NetworkCredential]::new('', $taskWitSpanish).Password
}
$taskDirectory = Join-Path $taskProject '.local'
New-Item -ItemType Directory -Path $taskDirectory -Force | Out-Null
$taskPath = Join-Path $taskDirectory 'AIEnglish.OpenRouter.json'
$taskExistingPath = Join-Path $taskDirectory 'AIEnglish.OpenRouter.json'
if ((Test-Path -LiteralPath $taskExistingPath) -and -not $WithWit) {
    $taskExisting = Get-Content -LiteralPath $taskExistingPath -Raw | ConvertFrom-Json
    foreach ($taskField in @('witEnglishToken', 'witSpanishToken')) {
        if ($taskExisting.$taskField) { $taskConfig[$taskField] = $taskExisting.$taskField }
    }
}
$taskConfig | ConvertTo-Json | Set-Content -LiteralPath $taskPath -Encoding utf8NoBOM
$taskPlain = $null
Write-Output 'Saved key in ignored .local/AIEnglish.OpenRouter.json.'
if ($Push) {
    $taskDeviceArgs = @()
    if ($Serial) { $taskDeviceArgs = @('-s', $Serial) }
    $taskRemote = '/sdcard/Android/data/com.enl1217.aienglish/files'
    & $Adb @taskDeviceArgs shell mkdir -p $taskRemote
    if ($LASTEXITCODE -ne 0) { throw 'Could not create device configuration directory. Install/start APK first.' }
    & $Adb @taskDeviceArgs push $taskPath "$taskRemote/AIEnglish.OpenRouter.json"
    if ($LASTEXITCODE -ne 0) { throw 'Could not push device configuration.' }
    Write-Output 'Restart AIEnglish on Quest to load the configuration.'
}
