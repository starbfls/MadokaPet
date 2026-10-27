$ErrorActionPreference = 'Stop'
$taskRoot = $PSScriptRoot
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Windows .NET Framework 4.8 is required.' }
$references = @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Web.Extensions.dll', 'System.Xaml.dll', 'WPF\WindowsBase.dll', 'WPF\PresentationCore.dll', 'WPF\PresentationFramework.dll')
$compileArgs = @('/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/utf8output', ('/out:' + (Join-Path $taskRoot 'MadokaPet.exe')))
foreach ($reference in $references) { $compileArgs += '/reference:' + (Join-Path $framework $reference) }
$compileArgs += '/resource:' + (Join-Path $taskRoot 'assets\madoka.png') + ',madoka.png'
$compileArgs += '/resource:' + (Join-Path $taskRoot 'dialogues.json') + ',dialogues.json'
$compileArgs += Join-Path $taskRoot 'src\MadokaPet.cs'
& $compiler @compileArgs
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Output (Join-Path $taskRoot 'MadokaPet.exe')
