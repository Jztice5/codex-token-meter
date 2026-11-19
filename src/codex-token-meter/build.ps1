param([string]$Output = (Join-Path $PSScriptRoot 'CodexTokenMeter.exe'))

$ErrorActionPreference = 'Stop'
$framework = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$wpf = Join-Path $framework 'WPF'
$compiler = Join-Path $framework 'csc.exe'
$references = @('System.Xaml.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Web.Extensions.dll' | ForEach-Object { '/reference:' + (Join-Path $framework $_) })
$references += @('WindowsBase.dll', 'PresentationCore.dll', 'PresentationFramework.dll', 'UIAutomationClient.dll', 'UIAutomationTypes.dll' | ForEach-Object { '/reference:' + (Join-Path $wpf $_) })
$sources = @('Usage.cs', 'Today.cs', 'Follow.cs', 'Desktop.cs', 'MeterWindow.cs', 'Program.cs' | ForEach-Object { Join-Path $PSScriptRoot $_ })

& $compiler /nologo /target:winexe /platform:anycpu /optimize+ /warnaserror+ /define:NETFRAMEWORK /codepage:65001 ('/win32manifest:' + (Join-Path $PSScriptRoot 'app.manifest')) ('/out:' + $Output) @references @sources
if ($LASTEXITCODE -ne 0) { throw "编译失败，退出代码 $LASTEXITCODE。" }
Get-Item -LiteralPath $Output
