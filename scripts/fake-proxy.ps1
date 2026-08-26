<#
.SYNOPSIS
    A throwaway HTTP proxy for exercising Voica's §9.5 behaviour without a corporate network.

.DESCRIPTION
    Behind a proxy is where this app is hardest to test and easiest to get wrong: at home there is
    no proxy, and editing the system network settings just to try one is not acceptable. macOS has
    `scripts/fake-proxy.py` for this; this is the Windows path, in PowerShell so it needs nothing
    installed.

    Point Voica at it with the process-only override (spec §9.5), which beats the system settings
    without touching them:

        $env:VOICA_PROXY = "127.0.0.1:18899"
        & .\src\Voica\bin\Debug\net8.0-windows10.0.17763.0\Voica.exe

    Two modes:

      -Mode Deny   (default) every request is answered `407 Proxy Authentication Required`.
                   This is the failure a closed corporate network produces, and the one worth
                   checking: all three destinations — recognition (§2), the model download (§2.5)
                   and the update check (§10) — must come back with a sentence that NAMES the
                   proxy, not with a raw error code.

      -Mode Allow  the proxy actually works: CONNECT is tunnelled to the real destination, so
                   traffic flows and Voica behaves normally — the proof that requests really are
                   going through the proxy rather than around it.

    ⚠️ Why there is no "accepts a password" mode, unlike macOS. On Windows the credentials are the
    ones you signed in with (`DefaultNetworkCredentials`, §9.5) and travel over NTLM/Negotiate — a
    challenge-response handshake against a domain, not a password in a header. A stub cannot stand
    in for a domain controller, and faking Basic instead would test a code path the app never
    takes: .NET does not send default credentials as Basic. So the two modes above are the honest
    pair — the closed door and the open one.

.EXAMPLE
    .\scripts\fake-proxy.ps1
    .\scripts\fake-proxy.ps1 -Mode Allow -Port 18899
#>
[CmdletBinding()]
param(
    [ValidateSet('Deny', 'Allow')] [string]$Mode = 'Deny',
    [int]$Port = 18899
)

$ErrorActionPreference = 'Stop'

# Each connection is handled in its own runspace: the app opens several at once (a dictation while
# the model downloads), and a proxy that answers them one at a time would look like a hang.
$handler = {
    param($client, $mode, $log)

    function Say($text) { $log.Enqueue($text) }

    try {
        $stream = $client.GetStream()
        $reader = New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::ASCII)
        $writer = New-Object System.IO.StreamWriter($stream, [System.Text.Encoding]::ASCII)
        $writer.NewLine = "`r`n"
        $writer.AutoFlush = $true

        $requestLine = $reader.ReadLine()
        if (-not $requestLine) { return }
        while ($true) { $header = $reader.ReadLine(); if (-not $header) { break } }

        $parts = $requestLine -split ' '
        $method = $parts[0]
        $target = $parts[1]

        if ($mode -eq 'Deny') {
            Say "$requestLine  ->  407"
            $writer.WriteLine('HTTP/1.1 407 Proxy Authentication Required')
            $writer.WriteLine('Proxy-Authenticate: Basic realm="voica-fake-proxy"')
            $writer.WriteLine('Content-Length: 0')
            $writer.WriteLine('Connection: close')
            $writer.WriteLine()
            return
        }

        if ($method -ne 'CONNECT') {
            # Voica speaks HTTPS to all three destinations, so plain forwarding would be dead code.
            Say "$requestLine  ->  501 (only CONNECT is forwarded)"
            $writer.WriteLine('HTTP/1.1 501 Not Implemented')
            $writer.WriteLine('Content-Length: 0')
            $writer.WriteLine('Connection: close')
            $writer.WriteLine()
            return
        }

        $hostName, $hostPort = $target -split ':'
        $upstream = New-Object System.Net.Sockets.TcpClient
        $upstream.Connect($hostName, [int]$hostPort)
        Say "$requestLine  ->  200 tunnelled"
        $writer.WriteLine('HTTP/1.1 200 Connection Established')
        $writer.WriteLine()

        # Pump both ways until either end goes quiet. CopyToAsync in both directions, then wait for
        # whichever finishes first: a tunnel is closed by either side.
        $up = $upstream.GetStream()
        $a = $stream.CopyToAsync($up)
        $b = $up.CopyToAsync($stream)
        [System.Threading.Tasks.Task]::WaitAny(@($a, $b)) | Out-Null
        $upstream.Close()
    }
    catch {
        $log.Enqueue("connection failed: $($_.Exception.Message)")
    }
    finally {
        $client.Close()
    }
}

$log = [System.Collections.Concurrent.ConcurrentQueue[string]]::new()
$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Port)
$listener.Start()

Write-Host "fake proxy listening on 127.0.0.1:$Port  (mode: $Mode)" -ForegroundColor Cyan
Write-Host "  `$env:VOICA_PROXY = `"127.0.0.1:$Port`"   then start Voica in that shell" -ForegroundColor DarkGray
Write-Host "  Ctrl+C to stop" -ForegroundColor DarkGray

$running = @()
try {
    while ($true) {
        # Polling instead of a blocking Accept: it keeps Ctrl+C responsive and gives the log
        # somewhere to be printed from, since a runspace cannot write to this host.
        if ($listener.Pending()) {
            $client = $listener.AcceptTcpClient()
            $ps = [powershell]::Create()
            $null = $ps.AddScript($handler).AddArgument($client).AddArgument($Mode).AddArgument($log)
            $running += [pscustomobject]@{ Shell = $ps; Handle = $ps.BeginInvoke() }
        }
        else {
            Start-Sleep -Milliseconds 40
        }

        $line = $null
        while ($log.TryDequeue([ref]$line)) { Write-Host "  $line" }

        $done = $running | Where-Object { $_.Handle.IsCompleted }
        foreach ($d in $done) { $d.Shell.Dispose() }
        $running = @($running | Where-Object { -not $_.Handle.IsCompleted })
    }
}
finally {
    $listener.Stop()
    foreach ($r in $running) { $r.Shell.Dispose() }
    Write-Host 'fake proxy stopped'
}
