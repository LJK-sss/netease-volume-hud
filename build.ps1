<#
    LevelBand - 一键编译脚本
    需要 .NET Framework 4.x 自带的 csc.exe（Windows 10/11 默认就有）

    用法：
        .\build.ps1              编译到 dist\LevelBand.exe
        .\build.ps1 -Run         编译完直接运行
#>

param(
    [switch]$Run
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $root 'src'
$dist = Join-Path $root 'dist'
$out = Join-Path $dist 'LevelBand.exe'

# 1) 找 csc.exe
$csc = $null
$candidates = @(
    "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
    "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)
foreach ($c in $candidates) {
    if (Test-Path $c) { $csc = $c; break }
}
if (-not $csc) {
    throw "找不到 csc.exe。请确认已安装 .NET Framework 4.x（Windows 10/11 自带）。"
}

# 2) 结束正在运行的实例并清理旧产物（否则会报 CS0016 无法写入）
Get-Process LevelBand -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500
if (-not (Test-Path $dist)) { New-Item -ItemType Directory -Path $dist | Out-Null }
if (Test-Path $out) { Remove-Item $out -Force }

# 3) 编译
Push-Location $src
try {
    & $csc /nologo /target:winexe /platform:x64 /unsafe /optimize+ `
        /win32icon:app.ico /resource:avatar.png `
        /out:$out `
        /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Core.dll `
        *.cs
    if ($LASTEXITCODE -ne 0) { throw "编译失败（csc 退出码 $LASTEXITCODE）" }
}
finally {
    Pop-Location
}

if (-not (Test-Path $out)) { throw "编译失败：没有生成 $out" }

$size = [math]::Round((Get-Item $out).Length / 1KB, 1)
$hash = (Get-FileHash $out -Algorithm SHA256).Hash
Write-Host ""
Write-Host "编译成功" -ForegroundColor Green
Write-Host "  输出：$out"
Write-Host "  大小：$size KB"
Write-Host "  SHA256：$hash"

if ($Run) {
    Write-Host "启动中…"
    Start-Process $out
}
