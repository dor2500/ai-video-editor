# Builds the app, runs the self-test and produces dist\AIVideoEditor-Setup-<version>.exe + a portable exe.
param([string]$Version = "2.6.0", [switch]$SkipTests)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$dist = Join-Path $root "dist"
$app = Join-Path $root "artifacts\app"
Remove-Item $app, $dist -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $dist | Out-Null

Write-Host "== publishing app $Version"
dotnet publish "$root\src\AIVideoEditor\AIVideoEditor.csproj" -c Release -o $app -p:Version=$Version
if ($LASTEXITCODE) { throw "app publish failed" }

if (-not $SkipTests) {
    Write-Host "== self-test"
    dotnet run --project "$root\tests\SelfTest\SelfTest.csproj" -c Release
    if ($LASTEXITCODE) { throw "self-test failed" }
}

Write-Host "== publishing installer"
# NativeAOT locates the MSVC linker through vswhere.exe
$vsInstaller = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer"
if ((Test-Path "$vsInstaller\vswhere.exe") -and ($env:PATH -notlike "*$vsInstaller*")) { $env:PATH = "$vsInstaller;$env:PATH" }
$setupOut = Join-Path $root "artifacts\setup"
dotnet publish "$root\installer\Setup\Setup.csproj" -c Release -o $setupOut -p:Version=$Version -p:PayloadPath="$app\AIVideoEditor.exe"
if ($LASTEXITCODE) { throw "setup publish failed" }

Copy-Item "$setupOut\AIVideoEditor-Setup.exe" "$dist\AIVideoEditor-Setup-$Version.exe"
Copy-Item "$app\AIVideoEditor.exe" "$dist\AIVideoEditor-$Version-portable.exe"
Get-ChildItem $dist | ForEach-Object {
    $h = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower()
    "$h  $($_.Name)"
} | Set-Content "$dist\SHA256SUMS.txt"
Get-ChildItem $dist | Format-Table Name, Length
