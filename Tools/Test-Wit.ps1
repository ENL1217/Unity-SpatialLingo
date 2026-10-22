# Synthesized sample sentences only; no microphone recording or personal audio is uploaded.
$ErrorActionPreference = 'Stop'
$taskProject = Split-Path $PSScriptRoot -Parent
$taskConfig = Get-Content (Join-Path $taskProject '.local/AIEnglish.OpenRouter.json') -Raw | ConvertFrom-Json
$taskResults = @()
foreach ($taskCase in @(
    @{ Language = 'English'; Token = $taskConfig.witEnglishToken; Text = 'I see a blue cup.'; Expected = 'blue cup' },
    @{ Language = 'Spanish'; Token = $taskConfig.witSpanishToken; Text = 'Hola. Esta es una taza.'; Expected = 'una taza' }
)) {
    if (-not $taskCase.Token) { throw "Missing $($taskCase.Language) client token." }
    $taskTts = [Net.Http.HttpClient]::new()
    $taskStt = [Net.Http.HttpClient]::new()
    try {
        $taskTts.Timeout = $taskStt.Timeout = [TimeSpan]::FromSeconds(40)
        $taskTts.DefaultRequestHeaders.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $taskConfig.witEnglishToken)
        $taskTts.DefaultRequestHeaders.Accept.ParseAdd('audio/wav')
        $taskBody = @{ q = $taskCase.Text; voice = 'wit$Charlie'; style = 'default'; speed = 100; pitch = 100 } | ConvertTo-Json -Compress
        $taskContent = [Net.Http.StringContent]::new($taskBody, [Text.Encoding]::UTF8, 'application/json')
        $taskContent.Headers.ContentType.CharSet = $null
        $taskAudioResponse = $taskTts.PostAsync('https://api.wit.ai/synthesize?v=20240304', $taskContent).GetAwaiter().GetResult()
        [void]$taskAudioResponse.EnsureSuccessStatusCode()
        $taskAudio = $taskAudioResponse.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
        $taskStt.DefaultRequestHeaders.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $taskCase.Token)
        $taskAudioContent = [Net.Http.ByteArrayContent]::new($taskAudio)
        $taskAudioContent.Headers.ContentType = [Net.Http.Headers.MediaTypeHeaderValue]::new('audio/wav')
        $taskSpeechResponse = $taskStt.PostAsync('https://api.wit.ai/speech?v=20240304', $taskAudioContent).GetAwaiter().GetResult()
        [void]$taskSpeechResponse.EnsureSuccessStatusCode()
        $taskStream = $taskSpeechResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        $taskObjects = ('[' + ($taskStream -replace '}\r?\n{', '},{') + ']') | ConvertFrom-Json
        $taskFinal = $taskObjects | Where-Object type -eq 'FINAL_TRANSCRIPTION' | Select-Object -Last 1
        if (-not $taskFinal -or -not $taskFinal.text.ToLowerInvariant().Contains($taskCase.Expected)) {
            throw "Unexpected $($taskCase.Language) transcript."
        }
        $taskResults += [pscustomobject]@{
            Language = $taskCase.Language; TTS = 'Passed'; STT = 'Passed'; Transcript = $taskFinal.text; AudioBytes = $taskAudio.Length
        }
    }
    finally { $taskTts.Dispose(); $taskStt.Dispose() }
}
$taskResults | ConvertTo-Json | Set-Content (Join-Path $taskProject 'Logs/wit-smoke.json') -Encoding utf8NoBOM
$taskResults | ConvertTo-Json
