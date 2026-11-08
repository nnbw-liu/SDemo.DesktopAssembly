param(
    [string]$OutDir = "C:\SDemo\DesktopAssembly\spike"
)

$ErrorActionPreference = "Stop"
$target = (Invoke-RestMethod "http://127.0.0.1:9222/json")[0]
$ws = [System.Net.WebSockets.ClientWebSocket]::new()
$ws.ConnectAsync([Uri]$target.webSocketDebuggerUrl, [Threading.CancellationToken]::None).Wait()

$script:nextId = 0
function Send-Cdp {
    param([string]$Method, [hashtable]$Params = @{})

    $script:nextId++
    $id = $script:nextId
    $payload = @{ id = $id; method = $Method; params = $Params } | ConvertTo-Json -Depth 12 -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($payload)
    $ws.SendAsync([ArraySegment[byte]]::new($bytes), [System.Net.WebSockets.WebSocketMessageType]::Text, $true, [Threading.CancellationToken]::None).Wait()

    $buffer = New-Object byte[] 1048576
    while ($true) {
        $ms = [IO.MemoryStream]::new()
        do {
            $result = $ws.ReceiveAsync([ArraySegment[byte]]::new($buffer), [Threading.CancellationToken]::None).Result
            $ms.Write($buffer, 0, $result.Count)
        } while (-not $result.EndOfMessage)

        $message = ([Text.Encoding]::UTF8.GetString($ms.ToArray())) | ConvertFrom-Json
        if ($message.id -eq $id) {
            if ($message.error) { throw "CDP $Method failed: $($message.error.message)" }
            return $message.result
        }
    }
}

function Eval {
    param([string]$Expression)
    $r = Send-Cdp "Runtime.evaluate" @{ expression = $Expression; returnByValue = $true; awaitPromise = $true }
    return $r.result.value
}

Eval "document.querySelector('.app-card')?.click(); 'card-clicked'" | Write-Host
Start-Sleep -Seconds 1
Eval "Array.from(document.querySelectorAll('button')).find(b => b.textContent.trim() === '启动应用')?.click(); 'launch-clicked'" | Write-Host

for ($i = 0; $i -lt 24; $i++) {
    Start-Sleep -Milliseconds 500
    $status = Eval "document.querySelector('.card-status')?.innerText ?? ''"
    if ($status -match '已连接') { break }
}

$text = Eval "document.body.innerText"
Write-Host ($text -replace "\s+", " ")

$shot = Send-Cdp "Page.captureScreenshot" @{ format = "png"; captureBeyondViewport = $false }
[IO.File]::WriteAllBytes((Join-Path $OutDir "m3-launched.png"), [Convert]::FromBase64String($shot.data))
Write-Host "saved m3-launched.png"

$ws.Dispose()
