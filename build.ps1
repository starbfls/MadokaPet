param([string]$OutputName = 'MadokaPet.exe')
$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
if ([IO.Path]::GetFileName($OutputName) -ne $OutputName -or [IO.Path]::GetExtension($OutputName) -ne '.exe') { throw 'OutputName must be an .exe file name.' }
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Windows .NET Framework 4.8 is required.' }
$references = @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Web.Extensions.dll', 'System.Xaml.dll', 'WPF\WindowsBase.dll', 'WPF\PresentationCore.dll', 'WPF\PresentationFramework.dll')
$compileArgs = @('/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/utf8output', ('/out:' + (Join-Path $taskRoot $OutputName)))
foreach ($reference in $references) { $compileArgs += '/reference:' + (Join-Path $framework $reference) }
$compileArgs += '/resource:' + (Join-Path $taskRoot 'assets\madoka.png') + ',madoka.png'
$compileArgs += '/resource:' + (Join-Path $taskRoot 'assets\rinze.png') + ',rinze.png'
$compileArgs += '/resource:' + (Join-Path $taskRoot 'dialogues.json') + ',dialogues.json'
$compileArgs += '/resource:' + (Join-Path $taskRoot 'dialogues.rinze.json') + ',dialogues.rinze.json'
$compileArgs += '/resource:' + (Join-Path $taskRoot 'characters.json') + ',characters.json'
$compileArgs += Join-Path $taskRoot 'src\MadokaPet.cs'
& $compiler @compileArgs
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Output (Join-Path $taskRoot $OutputName)
