# Builds Tavern Tracker into a single TavernTracker.exe in the "dist" folder.
# Run it with build.bat (double-click), or: powershell -ExecutionPolicy Bypass -File build.ps1

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

function Fail($msg) {
    Write-Host ""
    Write-Host "ERROR: $msg" -ForegroundColor Red
    exit 1
}

Write-Host "== Tavern Tracker build ==" -ForegroundColor Yellow

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Fail ("The .NET 8 SDK isn't installed. Install it (free) from https://dotnet.microsoft.com/download/dotnet/8.0 " +
          "or run:  winget install Microsoft.DotNet.SDK.8   then run this again.")
}
$sdks = & dotnet --list-sdks
if (-not ($sdks | Where-Object { $_ -match '^(8|9|10)\.' })) {
    Fail "Tavern Tracker needs the .NET 8 SDK or newer. Found: $($sdks -join ', ')"
}

# Let anything built here run on a newer .NET if the exact version isn't installed.
$env:DOTNET_ROLL_FORWARD = 'LatestMajor'

Write-Host "Running tests..."
& dotnet run --project (Join-Path $here 'tests\TavernTracker.Tests') -c Release --nologo
if ($LASTEXITCODE -ne 0) { Fail "Tests failed (see above). Nothing was built." }

Write-Host ""
Write-Host "Building TavernTracker.exe (first time takes a minute)..."
$dist = Join-Path $here 'dist'
& dotnet publish (Join-Path $here 'src\TavernTracker.App\TavernTracker.App.csproj') -c Release -o $dist --nologo -v minimal
if ($LASTEXITCODE -ne 0) { Fail "Build failed (see the messages above)." }

$exe = Join-Path $dist 'TavernTracker.exe'
if (-not (Test-Path $exe)) { Fail "Build said OK but $exe is missing." }

Write-Host ""
Write-Host "Done: $exe" -ForegroundColor Green
Write-Host "It's a single file: copy it wherever you like. Your data lives in %AppData%\TavernTracker."
Start-Process explorer.exe "/select,`"$exe`""
