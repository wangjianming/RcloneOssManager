# 构建隔离验收程序：源码 + 测试，输出到 build/tests，不触碰正式 EXE。
param([string]$OutputPath = '')
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputPath)) { $OutputPath = Join-Path $root 'build/tests/RcloneOssManager.Tests.exe' }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw 'missing .NET Framework 64-bit C# compiler' }
$sources = @(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Recurse -File -Filter '*.cs' | Sort-Object FullName)
$sources += Get-Item -LiteralPath (Join-Path $PSScriptRoot 'PulseUiTests.cs')
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($OutputPath))
$arguments = @('/nologo', '/codepage:65001', '/target:exe', '/main:RcloneOssManagerTester.Program', ('/out:' + $OutputPath))
foreach ($reference in @('System.Windows.Forms', 'System.Drawing', 'System.Management', 'System.IO.Compression', 'System.IO.Compression.FileSystem', 'System.Web.Extensions', 'System.Xml')) { $arguments += '/r:' + $reference + '.dll' }
$arguments += @($sources | ForEach-Object { $_.FullName })
Push-Location -LiteralPath $root
try {
    & $compiler @arguments
    if ($LASTEXITCODE -ne 0) { throw ('compile failed, exit ' + $LASTEXITCODE) }
}
finally { Pop-Location }
Write-Host ('test exe built: ' + $OutputPath)
