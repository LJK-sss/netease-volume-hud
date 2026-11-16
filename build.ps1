<#
    LevelBand 构建脚本
    需要 .NET Framework 4.x 自带的 csc.exe（Windows 10/11 默认就有）

    用法：
        .\build.ps1           完整版（带卡片顶边的大肥鱼）-> dist\LevelBand.exe
        .\build.ps1 -Lite     轻量版（不含大肥鱼，窗口 196x76）-> dist\LevelBand-lite.exe
        .\build.ps1 -All      两个都编
        .\build.ps1 -Run      编译完直接运行
#>

param(
    [switch]$Lite,
    [switch]$All,
    [switch]$Run
)

$ErrorActionPreference = 'Stop'

$root   = $PSScriptRoot
$srcDir = Join-Path $root 'src'
$dist   = Join-Path $root 'dist'

# 1) 找 csc.exe
$csc = $null
foreach ($c in @(
    "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
    "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)) {
    if (Test-Path $c) { $csc = $c; break }
}
if (-not $csc) { throw "找不到 csc.exe。请确认已安装 .NET Framework 4.x（Windows 10/11 自带）。" }
if (-not (Test-Path $srcDir)) { throw "找不到源码目录：$srcDir" }

# 2) 准备输出目录
#    这里不结束正在运行的 LevelBand：输出在 dist\ 下，和已经部署的那份不冲突；
#    万一目标文件被占用，csc 会报 CS0016，把那个进程关掉再编一次即可。
if (-not (Test-Path $dist)) { New-Item -ItemType Directory -Path $dist | Out-Null }

function Invoke-Build {
    param([bool]$IsLite)

    $outExe = Join-Path $dist ($(if ($IsLite) { 'LevelBand-lite.exe' } else { 'LevelBand.exe' }))
    if (Test-Path $outExe) { Remove-Item $outExe -Force }

    # 轻量版让整个 WhaleSprite.cs 不参与编译：精灵图是体积的唯一来源，
    # 只靠 /define 屏蔽绘制代码是减不掉那 150 KB 资源的。
    $csFiles = @(Get-ChildItem $srcDir -Filter *.cs)
    if ($IsLite) { $csFiles = @($csFiles | Where-Object { $_.Name -ne 'WhaleSprite.cs' }) }

    $cscArgs = @(
        '/nologo', '/target:winexe', '/platform:x64', '/unsafe', '/optimize+', '/nowarn:1591',
        "/win32icon:$srcDir\app.ico"
    )
    if ($IsLite) {
        $cscArgs += '/define:NOWHALE'
    } else {
        $whale = Join-Path $srcDir 'whale.png'
        if (-not (Test-Path $whale)) { throw "找不到 src\whale.png（完整版必需）" }
        $cscArgs += "/resource:$whale,NvHud.whale.png"   # 名字要与 WhaleSprite.ResName 一致
    }
    $cscArgs += "/out:$outExe"
    $cscArgs += '/r:System.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll', '/r:System.Core.dll'
    $cscArgs += ($csFiles | ForEach-Object { $_.FullName })

    $sw = [Diagnostics.Stopwatch]::StartNew()
    # 必须把编译器输出接住：放任它进管道，会被当成函数的返回值，
    # 后面 Get-FileHash 拿到的是 "src\Overlay.cs(44,23): ..." 这种报文而不是路径。
    $log = & $csc $cscArgs 2>&1
    $code = $LASTEXITCODE
    $sw.Stop()

    if ($code -ne 0) {
        $log | Where-Object { $_ -is [string] -and $_.Trim() } | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
        throw "编译失败（csc 退出码 $code）"
    }
    if (-not (Test-Path $outExe)) { throw "编译失败：没有生成 $outExe" }

    $kb = [math]::Round((Get-Item $outExe).Length / 1KB, 1)
    Write-Host ("  {0}  {1} KB  ({2:N1}s)" -f (Split-Path $outExe -Leaf), $kb, $sw.Elapsed.TotalSeconds) -ForegroundColor Green
    return $outExe
}

$built = @()
$target = if ($All) { '两个版本' } elseif ($Lite) { '轻量版' } else { '完整版' }
Write-Host "构建$target ..."

if ($All) {
    $built += Invoke-Build -IsLite $false
    $built += Invoke-Build -IsLite $true
} else {
    $built += Invoke-Build -IsLite $Lite
}

Write-Host ""
foreach ($f in $built) {
    $hash = (Get-FileHash $f -Algorithm SHA256).Hash
    Write-Host ("{0}  SHA256 {1}" -f (Split-Path $f -Leaf), $hash.Substring(0, 16)) -ForegroundColor DarkGray
}

if ($Run) {
    $toRun = $built[0]
    Write-Host "启动 $toRun ..."
    Start-Process $toRun
}
