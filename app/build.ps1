$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Windows .NET Framework 4.x compiler was not found.' }
$source = Join-Path $PSScriptRoot 'DesktopPet.cs'
$artwork = Join-Path $PSScriptRoot 'InputArtwork.cs'
$target = Join-Path $PSScriptRoot 'YierBubu-1.0.1.exe'
& $compiler /nologo /target:winexe /optimize+ /platform:x64 /reference:System.Drawing.dll /reference:System.Windows.Forms.dll "/out:$target" $source $artwork
if ($LASTEXITCODE -ne 0) { throw "Compilation failed: $LASTEXITCODE" }
Write-Output $target
