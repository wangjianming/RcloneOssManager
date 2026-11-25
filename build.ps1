# 构建全部源码；默认输出到build目录，不覆盖正式运行程序。
# 用法：build.cmd；或build.cmd -OutputPath D:\目标目录\RcloneOssManager.exe。
# 部署：build.cmd -DeployDir D:\tools\RcloneOssManager，编译后停止旧实例、覆盖并启动，便于立即验证。
param([string]$OutputPath = '', [string]$RcloneExecutable = '', [switch]$NoEmbeddedRclone, [string]$DeployDir = '')
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$root = $PSScriptRoot
$stage = $null
try {
    if ([string]::IsNullOrWhiteSpace($OutputPath)) { $OutputPath = Join-Path $root 'build\RcloneOssManager.exe' }
    $output = [IO.Path]::GetFullPath($OutputPath)
    if ([IO.Path]::GetExtension($output) -ne '.exe') { throw '输出路径必须使用.exe扩展名。' }
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    if (!(Test-Path -LiteralPath $compiler)) { throw '未找到.NET Framework 64位C#编译器。' }
    $sources = @(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Recurse -File -Filter '*.cs' | Sort-Object FullName)
    if ($sources.Count -eq 0) { throw 'src目录内没有可编译的C#源码。' }
    # 严格检查UTF-8，避免系统代码页使中文注释和界面字符串损坏。
    $utf8 = New-Object System.Text.UTF8Encoding($false, $true)
    foreach ($source in $sources) { [void][IO.File]::ReadAllText($source.FullName, $utf8) }
    $folder = [IO.Path]::GetDirectoryName($output)
    [void][IO.Directory]::CreateDirectory($folder)
    $stage = Join-Path $folder ('.build-' + [Guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($stage)
    # 临时目录保持固定文件名，使程序集名称不随每次构建的GUID变化。
    $stagedExe = Join-Path $stage 'RcloneOssManager.exe'
    $args = @('/nologo', '/codepage:65001', '/target:winexe', '/main:RcloneOssManager.Program', ('/out:' + $stagedExe), ('/win32icon:' + (Join-Path $root 'oss-manager.ico')))
    $args += '/resource:' + (Join-Path $root 'oss-manager.ico') + ',oss-manager.ico'
    # 清单缺失时csc会生成无清单exe，界面只能靠系统位图拉伸，AutoScaleMode.Dpi也不会生效。
    $manifest = Join-Path $root 'app.manifest'
    if (!(Test-Path -LiteralPath $manifest -PathType Leaf)) { throw '未找到app.manifest，无法确定高DPI感知与请求执行级别。' }
    $args += '/win32manifest:' + $manifest
    if (!$NoEmbeddedRclone) {
        if ([string]::IsNullOrWhiteSpace($RcloneExecutable)) {
            $RcloneExecutable = Join-Path $root 'rclone.exe'
        }
        if (!(Test-Path -LiteralPath $RcloneExecutable -PathType Leaf)) { throw '单文件构建需要rclone.exe，请通过-RcloneExecutable明确指定。' }
        # 引擎压缩进入程序集，运行时仅在用户目录按哈希释放，不捆绑任何连接配置或认证。
        $packed = Join-Path $stage 'rclone.exe.gz'
        $source = [IO.File]::OpenRead($RcloneExecutable)
        $target = [IO.File]::Create($packed)
        $gzip = New-Object IO.Compression.GZipStream($target, [IO.Compression.CompressionMode]::Compress)
        try { $source.CopyTo($gzip) } finally { $gzip.Dispose(); $target.Dispose(); $source.Dispose() }
        $hashFile = Join-Path $stage 'rclone.sha256'
        [IO.File]::WriteAllText($hashFile, (Get-FileHash -LiteralPath $RcloneExecutable -Algorithm SHA256).Hash.ToLowerInvariant(), $utf8)
        $args += '/resource:' + $packed + ',rclone.exe.gz'
        $args += '/resource:' + $hashFile + ',rclone.sha256'
    }
    foreach ($reference in @('System.Windows.Forms', 'System.Drawing', 'System.Management', 'System.IO.Compression', 'System.IO.Compression.FileSystem', 'System.Web.Extensions', 'System.Xml')) { $args += '/r:' + $reference + '.dll' }
    $args += @($sources | ForEach-Object { $_.FullName })
    Push-Location -LiteralPath $root
    try {
        # 编译时保持工作目录为程序目录，兼容既有基于BaseDirectory的图标和rclone资源定位。
        & $compiler @args
        if ($LASTEXITCODE -ne 0) { throw ('编译失败，退出码：' + $LASTEXITCODE) }
    }
    finally { Pop-Location }
    Copy-Item -LiteralPath $stagedExe -Destination $output -Force
    Write-Host ('编译通过：' + $sources.Count + '个源码文件 → ' + $output + '；单文件内嵌引擎=' + (!$NoEmbeddedRclone))
    if (![string]::IsNullOrWhiteSpace($DeployDir)) {
        $installDir = [IO.Path]::GetFullPath($DeployDir)
        [void][IO.Directory]::CreateDirectory($installDir)
        $installExe = Join-Path $installDir 'RcloneOssManager.exe'
        Get-Process RcloneOssManager -ErrorAction SilentlyContinue | ForEach-Object { [void]$_.CloseMainWindow() }
        Start-Sleep -Seconds 2
        Get-Process RcloneOssManager -ErrorAction SilentlyContinue | Stop-Process -Force
        Copy-Item -LiteralPath $output -Destination $installExe -Force
        Start-Process -FilePath $installExe
        Write-Host ('已部署并启动：' + $installExe)
    }
}
catch { Write-Error $_; exit 1 }
finally { if ($stage -and (Test-Path -LiteralPath $stage)) { Remove-Item -LiteralPath $stage -Recurse -Force } }
