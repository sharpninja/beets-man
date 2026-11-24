# Build entry point for FR-DISPATCH-001#ac-routing and TEST-DISPATCH-001.
# 2026-11-24: Run the required isolated presentation gates through Nuke.
$ErrorActionPreference = 'Stop'

Push-Location -LiteralPath $PSScriptRoot
try {
    & dotnet run --project 'build/BeetsMan.Build.csproj' -- @args
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
