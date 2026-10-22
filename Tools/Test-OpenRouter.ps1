# Real free-tier vision + text contract probe. Uses a repository sample image, never a room photo.
param([string]$ConfigPath)
$ErrorActionPreference = 'Stop'
$taskProject = Split-Path $PSScriptRoot -Parent
if (-not $ConfigPath) { $ConfigPath = Join-Path $taskProject '.local/AIEnglish.OpenRouter.json' }
$taskConfig = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
if (-not $taskConfig.model.EndsWith(':free')) { throw 'Paid model rejected.' }
$taskHeaders = @{ Authorization = 'Bearer ' + $taskConfig.apiKey; 'X-OpenRouter-Title' = 'AIEnglish Quest' }
$taskModel = (Invoke-RestMethod 'https://openrouter.ai/api/v1/models').data | Where-Object id -eq $taskConfig.model
if (-not $taskModel -or [decimal]$taskModel.pricing.prompt -ne 0 -or [decimal]$taskModel.pricing.completion -ne 0) {
    throw 'Model missing or no longer free.'
}
$taskProvider = @{ allow_fallbacks = $false; max_price = @{ prompt = 0; completion = 0; request = 0; image = 0 } }
function Invoke-FreeCompletion($TaskMessages) {
    $taskBody = @{ model = $taskConfig.model; messages = $TaskMessages; max_tokens = 2048; temperature = 0.2; stream = $false; provider = $taskProvider } | ConvertTo-Json -Depth 15
    if ($taskConfig.model -eq 'nvidia/nemotron-3.5-super-vl:free') {
        $taskBodyObject = $taskBody | ConvertFrom-Json -AsHashtable
        $taskBodyObject.reasoning = @{ enabled = $false }
        $taskBody = $taskBodyObject | ConvertTo-Json -Depth 15
    }
    $taskTimer = [Diagnostics.Stopwatch]::StartNew()
    $taskResult = Invoke-RestMethod 'https://openrouter.ai/api/v1/chat/completions' -Method Post -Headers $taskHeaders -ContentType 'application/json' -Body ([Text.Encoding]::UTF8.GetBytes($taskBody)) -TimeoutSec 90
    if ($taskResult.usage.cost -gt 0) { throw 'Nonzero cost reported by free endpoint.' }
    $taskText = $taskResult.choices[0].message.content -replace '^\s*```(?:json)?\s*|\s*```\s*$', ''
    [pscustomobject]@{ Content = ($taskText | ConvertFrom-Json); Cost = $taskResult.usage.cost; Model = $taskResult.model; Milliseconds = $taskTimer.ElapsedMilliseconds }
}
$taskImage = [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $taskProject 'Assets/SpatialLingo/Textures/Samples/InferenceEngine/cup.png')))
$taskVisionMessages = @(
    @{ role = 'system'; content = 'You are a language teacher. Return only a JSON object.' },
    @{ role = 'user'; content = @(
        @{ type = 'text'; text = 'Identify the pictured object. Return {"Wordclouds":[{"Language":"English","Word":"Cup","Adjectives":["...","...","..."],"Verbs":["...","..."]},{"Language":"Spanish","Word":"...","Adjectives":["...","...","..."],"Verbs":["...","..."]}]}. Use meaningful words, not placeholders.' },
        @{ type = 'image_url'; image_url = @{ url = 'data:image/png;base64,' + $taskImage } }
    ) }
)
$taskVision = Invoke-FreeCompletion $taskVisionMessages
if ($taskVision.Content.Wordclouds.Count -ne 2) { throw 'Vision response did not match the Wordclouds contract.' }
foreach ($taskCloud in $taskVision.Content.Wordclouds) {
    if (-not $taskCloud.Word -or $taskCloud.Adjectives.Count -ne 3 -or $taskCloud.Verbs.Count -ne 2) {
        throw 'Vision response has missing lesson fields.'
    }
}
Start-Sleep -Seconds 4
$taskJudgment = Invoke-FreeCompletion @(
    @{ role = 'system'; content = 'Evaluate an English practice sentence. Reply only with JSON {"Pass":true or false,"Reason":"short explanation"}.' },
    @{ role = 'user'; content = 'Required noun: cup. Required adjective: blue. Transcript: I drink from a blue cup. Does it include the required words and form a correct sentence?' }
)
if ($taskJudgment.Content.Pass -isnot [bool] -or -not $taskJudgment.Content.Reason) { throw 'Text response did not match the verdict contract.' }
New-Item -ItemType Directory -Path (Join-Path $taskProject 'Logs') -Force | Out-Null
$taskSummary = @{ Vision = $taskVision; Judgment = $taskJudgment; Time = (Get-Date).ToString('o') }
$taskSummary | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $taskProject 'Logs/openrouter-smoke.json') -Encoding utf8NoBOM
$taskQuota = (Invoke-RestMethod 'https://openrouter.ai/api/v1/key' -Headers $taskHeaders).data
[pscustomobject]@{ Vision = 'Passed'; Text = 'Passed'; Model = $taskVision.Model; Cost = ($taskVision.Cost + $taskJudgment.Cost); KeyLimit = $taskQuota.limit; FreeDailyRequests = $taskQuota.free_model_daily_requests } | ConvertTo-Json -Depth 5
