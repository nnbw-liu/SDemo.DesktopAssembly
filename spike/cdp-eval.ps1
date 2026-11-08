param(
    [Parameter(Mandatory = $true)][string]$Expression,
    [string]$TargetUrl = 'http://127.0.0.1:9222/json'
)

$targets = Invoke-RestMethod $TargetUrl
if (-not $targets) { throw "No CDP targets at $TargetUrl" }
$target = $targets | Where-Object { $_.type -eq 'page' } | Select-Object -First 1
if (-not $target) { $target = $targets | Select-Object -First 1 }

Add-Type -AssemblyName System.Net.WebSockets.Client
$ws = [System.Net.WebSockets.ClientWebSocket]::new()
$ws.ConnectAsync([Uri]$target.webSocketDebuggerUrl, [Threading.CancellationToken]::None).GetAwaiter().GetResult()

try {
    $payload = @{
        id = 1
        method = 'Runtime.evaluate'
        params = @{
            expression = $Expression
            returnByValue = $true
            awaitPromise = $true
        }
    } | ConvertTo-Json -Depth 12 -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($payload)
    $ws.SendAsync([ArraySegment[byte]]::new($bytes), [System.Net.WebSockets.WebSocketMessageType]::Text, $true, [Threading.CancellationToken]::None).GetAwaiter().GetResult()

    $buffer = New-Object byte[] 65536
    $builder = [Text.StringBuilder]::new()
    while ($true) {
        $result = $ws.ReceiveAsync([ArraySegment[byte]]::new($buffer), [Threading.CancellationToken]::None).GetAwaiter().GetResult()
        [void]$builder.Append([Text.Encoding]::UTF8.GetString($buffer, 0, $result.Count))
        if ($result.EndOfMessage) {
            $response = $builder.ToString() | ConvertFrom-Json
            if ($response.id -eq 1) {
                if ($response.result.exceptionDetails) {
                    $response.result.exceptionDetails | ConvertTo-Json -Depth 8
                }
                else {
                    $response.result.result.value | ConvertTo-Json -Depth 8
                }
                break
            }
            [void]$builder.Clear()
        }
    }
}
finally {
    if ($ws.State -eq [System.Net.WebSockets.WebSocketState]::Open) {
        $ws.CloseAsync([System.Net.WebSockets.WebSocketCloseStatus]::NormalClosure, 'done', [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    }
    $ws.Dispose()
}
