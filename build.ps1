[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',
    [string]$DotNet = 'dotnet',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$publishDirectory = Join-Path $projectRoot "artifacts\$Runtime"
$zipPath = Join-Path $projectRoot "artifacts\BackupDesk-$Runtime.zip"
$appProject = Join-Path $projectRoot 'src\ManualBackup.App\ManualBackup.App.csproj'
$testProject = Join-Path $projectRoot 'tests\ManualBackup.Tests\ManualBackup.Tests.csproj'
$settingsTestProject = Join-Path $projectRoot 'tests\ManualBackup.SettingsTests\ManualBackup.SettingsTests.csproj'

# These variables apply only to this process and prevent first-run workload
# discovery from requiring access to the Windows Service Control Manager.
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
$env:MSBuildEnableWorkloadResolver = 'false'

Push-Location $projectRoot
try {
    if (-not $SkipTests -and (Test-Path -LiteralPath $testProject)) {
        & $DotNet run --project $testProject --configuration Release '-p:WarningsNotAsErrors=NU1900' --ignore-failed-sources
        if ($LASTEXITCODE -ne 0) { throw 'Backup integration tests failed.' }
    }
    if (-not $SkipTests -and (Test-Path -LiteralPath $settingsTestProject)) {
        & $DotNet run --project $settingsTestProject --configuration Release '-p:WarningsNotAsErrors=NU1900' --ignore-failed-sources
        if ($LASTEXITCODE -ne 0) { throw 'Settings migration tests failed.' }
    }

    # Clear only this script's resolved per-runtime output directory, so an old
    # publish cannot accidentally add stale binaries to the portable package.
    $artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts'))
    $resolvedPublishDirectory = [System.IO.Path]::GetFullPath($publishDirectory)
    $expectedPublishDirectory = [System.IO.Path]::GetFullPath((Join-Path $artifactsRoot $Runtime))
    if ($resolvedPublishDirectory -ne $expectedPublishDirectory -or
        -not $resolvedPublishDirectory.StartsWith($artifactsRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'Publish directory is outside the expected artifacts folder.'
    }
    if (Test-Path -LiteralPath $resolvedPublishDirectory) {
        Remove-Item -LiteralPath $resolvedPublishDirectory -Recurse -Force
    }

    # Keep known vulnerability warnings as errors, but permit an offline audit
    # service (NU1900) when all required runtime packs are already cached.
    & $DotNet publish $appProject --configuration Release --runtime $Runtime --self-contained true --output $publishDirectory --ignore-failed-sources '-p:WarningsNotAsErrors=NU1900' '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:EnableCompressionInSingleFile=true' '-p:DebugType=None' '-p:DebugSymbols=false'
    if ($LASTEXITCODE -ne 0) { throw 'Application publish failed.' }
    if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory 'BackupDesk.exe'))) {
        throw 'Publish did not produce the expected BackupDesk.exe.'
    }

    $readmePath = Join-Path $projectRoot 'README.md'
    if (Test-Path -LiteralPath $readmePath) {
        Copy-Item -LiteralPath $readmePath -Destination (Join-Path $publishDirectory 'README.md') -Force
    }
    $licensePath = Join-Path $projectRoot 'THIRD-PARTY-NOTICES.txt'
    if (Test-Path -LiteralPath $licensePath) {
        Copy-Item -LiteralPath $licensePath -Destination (Join-Path $publishDirectory 'THIRD-PARTY-NOTICES.txt') -Force
    }

    Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $zipPath -CompressionLevel Optimal -Force
    Get-Item -LiteralPath $zipPath | Select-Object FullName, Length
}
finally {
    Pop-Location
}
