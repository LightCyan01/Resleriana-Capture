param(
    [Parameter(Mandatory = $true)][int]$ProcessId,
    [ValidateSet('status', 'abort')][string]$Command = 'status',
    [string]$RunId,
    [string]$Reason
)
$ErrorActionPreference = 'Stop'
if ($ProcessId -le 0) { throw 'A positive game process ID is required.' }
if ($Command -eq 'abort' -and ([string]::IsNullOrWhiteSpace($RunId) -or [string]::IsNullOrWhiteSpace($Reason))) {
    throw 'Abort requires the current run ID from status and an explicit reason.'
}
$captureRequest = @{ schema = 1; command = $Command }
if ($RunId) { $captureRequest.run_id = $RunId }
if ($Reason) { $captureRequest.reason = $Reason }
$captureBytes = [Text.Encoding]::UTF8.GetBytes(($captureRequest | ConvertTo-Json -Compress))
if ($captureBytes.Length -gt 4096) { throw 'Control request exceeds the frame limit.' }
$captureOptions = [IO.Pipes.PipeOptions]::Asynchronous
if ([enum]::GetNames([IO.Pipes.PipeOptions]) -contains 'CurrentUserOnly') {
    $captureOptions = $captureOptions -bor [IO.Pipes.PipeOptions]::CurrentUserOnly
}
$capturePipe = New-Object IO.Pipes.NamedPipeClientStream('.', "atelier-capture-$ProcessId", [IO.Pipes.PipeDirection]::InOut, $captureOptions)
try {
    $capturePipe.Connect(5000)
    $capturePipe.Write([BitConverter]::GetBytes($captureBytes.Length), 0, 4)
    $capturePipe.Write($captureBytes, 0, $captureBytes.Length)
    $capturePipe.Flush()
    function Read-CaptureFrameBytes([int]$Count) {
        $captureBuffer = New-Object byte[] $Count
        $captureOffset = 0
        while ($captureOffset -lt $Count) {
            $captureRead = $capturePipe.ReadAsync($captureBuffer, $captureOffset, ($Count - $captureOffset))
            if (-not $captureRead.Wait(5000)) { throw 'Control response timed out.' }
            $captureReceived = $captureRead.Result
            if ($captureReceived -eq 0) { throw 'Control response closed before completion.' }
            $captureOffset += $captureReceived
        }
        return ,$captureBuffer
    }
    $captureLength = [BitConverter]::ToInt32((Read-CaptureFrameBytes 4), 0)
    if ($captureLength -le 0 -or $captureLength -gt 1048576) { throw 'Control response exceeds the frame limit.' }
    [Text.Encoding]::UTF8.GetString((Read-CaptureFrameBytes $captureLength))
}
finally { $capturePipe.Dispose() }
