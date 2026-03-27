param(
    [string]$Configuration = "Release",
    [string]$Framework = "net10.0",
    [string]$RuntimeIdentifier = "win-x64",
    [string]$OutputDir = "",
    [switch]$EnableUpx,
    [string]$UpxPath = "upx",
    [bool]$EnableSentryProfiling = $true,
    [switch]$EnableAot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$isDefaultOutputDir = [string]::IsNullOrWhiteSpace($OutputDir)
if ($isDefaultOutputDir)
{
    $OutputDir = Join-Path $PSScriptRoot ("artifacts/publish/" + $RuntimeIdentifier)
}

$projectPath = Join-Path $PSScriptRoot "YModemWin.App/YModemWin.App.csproj"
if (-not (Test-Path $projectPath))
{
    throw "Project not found: $projectPath"
}

$sentryProfilingValue = if ($EnableSentryProfiling) { "true" } else { "false" }
$publishArgs = @("publish", $projectPath, "-c", $Configuration, "-f", $Framework, "-r", $RuntimeIdentifier, "-p:SelfContained=true", "-p:EnableSentryProfiling=$sentryProfilingValue", "-o", $OutputDir)

function Test-FileLocked
{
    param([string]$Path)

    if (-not (Test-Path $Path))
    {
        return $false
    }

    try
    {
        $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
        $stream.Dispose()
        return $false
    }
    catch [System.IO.IOException]
    {
        return $true
    }
    catch [System.UnauthorizedAccessException]
    {
        return $true
    }
}

function Set-PublishOutputDir
{
    param([string]$NewOutputDir)

    for ($index = 0; $index -lt $publishArgs.Count - 1; $index++)
    {
        if ($publishArgs[$index] -eq "-o")
        {
            $publishArgs[$index + 1] = $NewOutputDir
            break
        }
    }
}

$targetExePath = Join-Path $OutputDir "YModem.exe"
if (Test-FileLocked $targetExePath)
{
    if ($isDefaultOutputDir)
    {
        $fallbackOutputDir = Join-Path $PSScriptRoot ("artifacts/publish/" + $RuntimeIdentifier + "-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
        Write-Host "Detected locked output executable: $targetExePath"
        Write-Host "Falling back to a new output directory: $fallbackOutputDir"
        $OutputDir = $fallbackOutputDir
        Set-PublishOutputDir -NewOutputDir $OutputDir
    }
    else
    {
        throw "Output executable is locked: $targetExePath. Close the running app or use a different -OutputDir."
    }
}

if ($EnableAot.IsPresent)
{
    # NativeAOT always uses trimming in the toolchain.
    $publishArgs += @(
        "-p:PublishAot=true",
        "-p:OptimizationPreference=Size",
        "-p:DebuggerSupport=false",
        "-p:MetadataUpdaterSupport=false"
    )
    Write-Host "Publishing NativeAOT build to $OutputDir"
}
else
{
    # Match the previous IDE publish profile that was known to run:
    # SelfContained + ReadyToRun + SingleFile + no Trim.
    $publishArgs += @(
        "-p:PublishAot=false",
        "-p:PublishReadyToRun=true",
        "-p:PublishTrimmed=false",
        "-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:IncludeAllContentForSelfExtract=true"
    )
    Write-Host "Publishing ReadyToRun (non-AOT, non-Trim) build to $OutputDir"
}

& dotnet @publishArgs
if ($LASTEXITCODE -ne 0)
{
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$exePath = Join-Path $OutputDir "YModem.exe"
if (-not (Test-Path $exePath))
{
    throw "Published executable not found: $exePath"
}

if ($EnableUpx.IsPresent)
{
    $upxCommand = Get-Command $UpxPath -ErrorAction SilentlyContinue
    if ($null -eq $upxCommand)
    {
        throw "UPX not found. Install it or pass -UpxPath <full-path-to-upx.exe>."
    }

    $before = (Get-Item $exePath).Length
    Write-Host "Compressing executable with UPX: $exePath"
    & $upxCommand.Source "--best" "--lzma" "--force" $exePath
    if ($LASTEXITCODE -ne 0)
    {
        throw "UPX compression failed with exit code $LASTEXITCODE"
    }

    $after = (Get-Item $exePath).Length
    $ratio = [Math]::Round(($after / [Math]::Max($before, 1)) * 100, 2)
    Write-Host "UPX compression complete: $before -> $after bytes ($ratio%)"
}

Write-Host "Publish complete: $OutputDir"
