param([string]$OutputName = 'MoiraSoundboard.exe')
$ErrorActionPreference = 'Stop'
if ([System.IO.Path]::GetFileName($OutputName) -ne $OutputName -or $OutputName -notmatch '\.exe$') { throw 'OutputName must be an executable filename.' }
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) { throw 'Windows .NET Framework C# compiler is unavailable.' }
$sourcePath = Join-Path $PSScriptRoot 'MoiraSoundboard.cs'
$outputPath = Join-Path $PSScriptRoot $OutputName
$mixerSource = Join-Path $PSScriptRoot 'AudioMixer.cs'
$customSource = Join-Path $PSScriptRoot 'Customization.cs'
& $compilerPath /nologo /target:winexe /platform:anycpu /optimize+ /utf8output /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Runtime.Serialization.dll "/out:$outputPath" $sourcePath $mixerSource $customSource
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Output "Built: $outputPath"
