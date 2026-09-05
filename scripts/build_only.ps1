# build_only.ps1 - 自动编译 C# 源码为轻量单文件程序（本地，不发布云端，开源无混淆）
$ErrorActionPreference = "Stop"

try { $host.UI.RawUI.WindowTitle = "Haodo 源码编译打包工具" } catch {}

# 1. 动态获取根目录
$scriptDir = $PSScriptRoot
if (Test-Path (Join-Path $scriptDir "src\CLIProxyAPI_GUI.csproj")) {
    $rootDir = $scriptDir
} else {
    $rootDir = Split-Path $scriptDir -Parent
}

$csprojPath = Join-Path $rootDir "src\CLIProxyAPI_GUI.csproj"
$verFilePath = Join-Path $rootDir "版本号.txt"
if (-not (Test-Path $verFilePath)) {
    $verFilePath = Join-Path $rootDir "VERSION.txt"
}

Write-Host "==================================================" -ForegroundColor Cyan
Write-Host "     Haodo 源码编译打包工具（单文件无混淆）" -ForegroundColor Cyan
Write-Host "==================================================" -ForegroundColor Cyan

# 2. 检查工程文件与版本描述文件
if (-not (Test-Path $csprojPath)) {
    Write-Host "[错误] 无法找到 C# 工程文件: $csprojPath" -ForegroundColor Red
    Read-Host "按回车键退出..."
    exit 1
}

if (-not (Test-Path $verFilePath)) {
    Write-Host "[错误] 无法在根目录找到 版本号.txt 或 VERSION.txt！" -ForegroundColor Red
    Write-Host "提示: 请先在根目录下创建 版本号.txt 并写入版本号。" -ForegroundColor Yellow
    Read-Host "按回车键退出..."
    exit 1
}

$rawVer = (Get-Content $verFilePath -Raw -Encoding UTF8).Trim()
$verRaw = $rawVer -replace '^[vV]', ''

Write-Host "[1/4] 读取发布版本号: v$verRaw ($(Split-Path $verFilePath -Leaf))" -ForegroundColor Green

# 3. 检查并结束正在运行的 Haodo.exe 进程
Write-Host "[2/4] 检查是否存在运行中的 Haodo.exe 进程..." -ForegroundColor Yellow
$runningProcs = Get-Process -Name "Haodo" -ErrorAction SilentlyContinue
if ($runningProcs) {
    Write-Host "      检测到 Haodo.exe 正在运行，正在强制结束进程..." -ForegroundColor DarkYellow
    $runningProcs | Stop-Process -Force
    Start-Sleep -Seconds 1
    Write-Host "      Haodo.exe 进程已成功终止。" -ForegroundColor Green
} else {
    Write-Host "      Haodo.exe 当前未运行。" -ForegroundColor Gray
}

# 4. 执行 dotnet publish Release 编译（生成轻量单文件）
Write-Host "[3/4] 正在调用 dotnet publish 编译 Release 单文件程序..." -ForegroundColor Yellow
$buildTmp = Join-Path $env:TEMP "HaodoBuild"
$publishTmp = Join-Path $buildTmp "publish"

if (Test-Path $buildTmp) { Remove-Item $buildTmp -Recurse -Force -ErrorAction SilentlyContinue }

# 单文件发布参数：框架依赖（免自包含，体积 ~900KB，目标机器需装 .NET 10 Desktop Runtime）+ 不生成调试符号
$dotnetArgs = @(
    "publish",
    "$csprojPath",
    "-c", "Release",
    "-r", "win-x64",
    "--no-self-contained",
    "-p:PublishSingleFile=true",
    "-p:DebugType=None",
    "-p:DebugSymbols=false",
    "-o", "$publishTmp"
)

& dotnet.exe @dotnetArgs
if ($LASTEXITCODE -ne 0) {
    Write-Host "[错误] dotnet publish 编译失败！请检查上方 C# 代码报错信息。" -ForegroundColor Red
    Read-Host "按回车键退出..."
    exit 1
}

# 5. 复制编译产物到根目录并清理临时输出
Write-Host "[4/4] 正在复制 Haodo.exe 到根目录..." -ForegroundColor Yellow
$outExe = Join-Path $publishTmp "Haodo.exe"
$targetExe = Join-Path $rootDir "Haodo.exe"
$targetPdb = Join-Path $rootDir "Haodo.pdb"

if (-not (Test-Path $outExe)) {
    Write-Host "[错误] 未找到编译输出文件: $outExe" -ForegroundColor Red
    Read-Host "按回车键退出..."
    exit 1
}

$copySuccess = $false
for ($i = 1; $i -le 5; $i++) {
    try {
        Copy-Item $outExe $targetExe -Force
        $copySuccess = $true
        break
    } catch {
        Start-Sleep -Milliseconds 800
    }
}
if (-not $copySuccess) {
    Write-Host "[错误] 无法覆盖根目录 Haodo.exe，文件可能仍被占用！" -ForegroundColor Red
    Read-Host "按回车键退出..."
    exit 1
}
$exeSizeKb = [math]::Round((Get-Item $targetExe).Length / 1KB, 2)
Write-Host "      Haodo.exe 覆盖更新完成（体积: $exeSizeKb KB）。" -ForegroundColor Green

if (Test-Path $targetPdb) { Remove-Item $targetPdb -Force -ErrorAction SilentlyContinue }
if (Test-Path $buildTmp) { Remove-Item $buildTmp -Recurse -Force -ErrorAction SilentlyContinue }

Write-Host ""
Write-Host "==================================================" -ForegroundColor Green
Write-Host " 🎉 本地编译打包成功！Haodo v$verRaw 已更新至根目录！" -ForegroundColor Green
Write-Host "    （体积仅约 $exeSizeKb KB，开源无混淆）" -ForegroundColor Yellow
Write-Host "==================================================" -ForegroundColor Green
Write-Host ""
Write-Host "编译流程完毕，窗口将在 3 秒后自动关闭..." -ForegroundColor Gray
Start-Sleep -Seconds 3
