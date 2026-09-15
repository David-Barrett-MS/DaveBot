[CmdletBinding()]
param(
    [switch]$NoBrowser
)

$ErrorActionPreference = 'Stop'

$url = 'http://localhost:5217'
$projectPath = Join-Path $PSScriptRoot '..\src\DaveBrain\DaveBrain.csproj'
if (-not (Test-Path -LiteralPath $projectPath)) {
    throw "DaveBrain project was not found at '$projectPath'."
}

$originalUrls = $env:ASPNETCORE_URLS
$originalEnvironment = $env:ASPNETCORE_ENVIRONMENT
try {
    $env:ASPNETCORE_URLS = $url
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $arguments = "run --project `"$projectPath`" --configuration Debug --no-launch-profile"
    $process = Start-Process -FilePath 'dotnet' -PassThru -ArgumentList $arguments
}
finally {
    $env:ASPNETCORE_URLS = $originalUrls
    $env:ASPNETCORE_ENVIRONMENT = $originalEnvironment
}

$deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
while ([DateTimeOffset]::UtcNow -lt $deadline) {
    if ($process.HasExited) {
        throw "DaveBrain stopped during startup (exit code $($process.ExitCode))."
    }

    try {
        $response = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 1
        if ($response.StatusCode -ge 200 -and $response.StatusCode -lt 500) {
            if (-not $NoBrowser) {
                Start-Process $url
            }
            Write-Host "DaveBrain is running at $url (PID $($process.Id))."
            exit 0
        }
    }
    catch {
        Start-Sleep -Milliseconds 500
    }
}

throw "DaveBrain did not respond at $url within 30 seconds."
