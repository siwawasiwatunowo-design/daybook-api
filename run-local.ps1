$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "src\Daybook.Backend\Daybook.Backend.csproj"

Write-Host "Checking .NET SDK..." -ForegroundColor Cyan
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw ".NET SDK is missing. Install .NET 10 SDK, reopen PowerShell, then run this script again." }
$version = (& dotnet --version).Trim()
if ([int]($version.Split('.')[0]) -lt 10) { throw "Found .NET SDK $version. Install .NET 10 SDK." }

Write-Host "Checking FFmpeg..." -ForegroundColor Cyan
if (-not (Get-Command ffmpeg -ErrorAction SilentlyContinue)) { throw "FFmpeg not found in PATH. Install FFmpeg and reopen PowerShell." }
if (-not (Get-Command ffprobe -ErrorAction SilentlyContinue)) { throw "FFprobe not found in PATH. It is normally included with FFmpeg." }

# User Secrets are queried only to validate key names; values are never printed.
$secretOutput = (& dotnet user-secrets list --project $project 2>$null | Out-String)
$required = @("ConnectionStrings:Postgres", "Storage:Endpoint", "Storage:Bucket", "Storage:AccessKey", "Storage:SecretKey", "Stt:ApiKey", "Gemini:ApiKey")
$missing = @($required | Where-Object { $secretOutput -notmatch [regex]::Escape($_ + " =") -and [string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable(($_ -replace ':','__'))) })
if ($missing.Count -gt 0) {
    Write-Host "Missing required settings (set via dotnet user-secrets or environment variables):" -ForegroundColor Yellow
    $missing | ForEach-Object { Write-Host " - $_" }
    Write-Host "See docs/FREE-SETUP-WINDOWS.md." -ForegroundColor Yellow
    exit 2
}

$credentialPath = [Environment]::GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS")
if ([string]::IsNullOrWhiteSpace($credentialPath) -or -not (Test-Path $credentialPath)) {
    throw "Set GOOGLE_APPLICATION_CREDENTIALS to the Firebase service-account JSON path (outside the repository)."
}

$env:ASPNETCORE_ENVIRONMENT = "Development"
Write-Host "Starting Daybook API + Background Worker at http://0.0.0.0:8080" -ForegroundColor Green
Write-Host "Health check: http://localhost:8080/health" -ForegroundColor Green
dotnet run --project $project --urls "http://0.0.0.0:8080"
