# Reliable two-step obfuscated release build for DANBOT.
#
# Use this when the in-build obfuscate.targets hook (AfterTargets=Compile) does
# not line up on your machine. It avoids MSBuild ordering entirely:
#
#   1. build   (Release, win-x64, self-contained) -> bin has HappyBot.dll + all deps
#   2. obfuscar  in that bin dir (deps resolve from the same folder), then copy
#      the obfuscated HappyBot.dll back over the bin copy
#   3. publish  with the SingleExe profile and --no-build, so the bundler reuses
#      the already-obfuscated assembly instead of recompiling it
#
# After this finishes, run the signing/upload step LAST:
#     node tools/release.mjs --upload
# (release.mjs hashes/signs the final publish\DANBOT.exe, so it must run after
#  obfuscation has changed the bytes.)
#
# Run from the repo root:  pwsh tools/obfuscate-release.ps1

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

$tfm = 'net10.0-windows'
$rid = 'win-x64'
$config = 'Release'
$binDir = Join-Path $repo "bin\$config\$tfm\$rid"
$asm = Join-Path $binDir 'HappyBot.dll'
$obfOut = Join-Path $binDir 'obfuscated'

Write-Host '==> dotnet tool restore' -ForegroundColor Cyan
dotnet tool restore

Write-Host '==> Step 1/3: build (Release, self-contained)' -ForegroundColor Cyan
dotnet build HappyBot.csproj -c $config -r $rid --self-contained true -p:Obfuscate=false
if (-not (Test-Path $asm)) { throw "Build did not produce $asm" }

Write-Host '==> Step 2/3: obfuscate HappyBot.dll in place' -ForegroundColor Cyan
Push-Location $binDir
try {
    # obfuscar.xml uses InPath='.' / OutPath='.\obfuscated' and
    # AssemblySearchPath='.', so run it from the bin dir where every dependency
    # assembly already sits next to HappyBot.dll.
    dotnet obfuscar.console (Join-Path $repo 'obfuscar.xml')
}
finally { Pop-Location }

$obfAsm = Join-Path $obfOut 'HappyBot.dll'
if (-not (Test-Path $obfAsm)) { throw "Obfuscar did not produce $obfAsm" }
Copy-Item $obfAsm $asm -Force
Write-Host "    obfuscated HappyBot.dll copied over $asm" -ForegroundColor Green

Write-Host '==> Step 3/3: publish single-file (--no-build, reuses obfuscated assembly)' -ForegroundColor Cyan
dotnet publish HappyBot.csproj -p:PublishProfile=SingleExe --no-build -p:Obfuscate=false

$exe = Join-Path $repo 'publish\DANBOT.exe'
if (-not (Test-Path $exe)) { throw "Publish did not produce $exe" }
Write-Host "==> Done: $exe" -ForegroundColor Green
Write-Host '    Now verify at runtime (sign in / redeem / start bot), then: node tools/release.mjs --upload' -ForegroundColor Yellow
