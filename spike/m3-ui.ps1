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
    $ws.SendAsync(
        [ArraySegment[byte]]::new($bytes),
        [System.Net.WebSockets.WebSocketMessageType]::Text,
        $true,
        [Threading.CancellationToken]::None).Wait()

    $buffer = New-Object byte[] 1048576
    while ($true) {
        $ms = [IO.MemoryStream]::new()
        do {
            $result = $ws.ReceiveAsync(
                [ArraySegment[byte]]::new($buffer),
                [Threading.CancellationToken]::None).Result
            $ms.Write($buffer, 0, $result.Count)
        } while (-not $result.EndOfMessage)

        $text = [Text.Encoding]::UTF8.GetString($ms.ToArray())
        $message = $text | ConvertFrom-Json
        if ($message.id -eq $id) {
            if ($message.error) {
                throw "CDP $Method failed: $($message.error.message)"
            }
            return $message.result
        }
    }
}

function Click-Nav {
    param([string]$Text)

    $expression = "Array.from(document.querySelectorAll('.nav-item')).find(b => b.textContent.trim() === '$Text')?.click(); document.body.innerText.slice(0, 160)"
    $result = Send-Cdp "Runtime.evaluate" @{ expression = $expression; returnByValue = $true }
    Start-Sleep -Milliseconds 900
    return $result.result.value
}

function Save-Screenshot {
    param([string]$Name)

    $result = Send-Cdp "Page.captureScreenshot" @{ format = "png"; captureBeyondViewport = $false }
    $path = Join-Path $OutDir $Name
    [IO.File]::WriteAllBytes($path, [Convert]::FromBase64String($result.data))
    return $path
}

foreach ($page in @("设置", "诊断", "目录", "概览")) {
    $text = Click-Nav $page
    $file = Save-Screenshot ("m3-" + ($page -replace "概览", "overview") + ".png")
    Write-Host "$page -> $file"
    Write-Host ($text -replace "\s+", " ")
}

$ws.Dispose()
