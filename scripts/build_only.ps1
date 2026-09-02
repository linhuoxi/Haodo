# build_only.ps1 - 自动编译 C# 源码 + Obfuscar 混淆 + 修复资源段 + 单文件重打包（本地，不发布云端）
$ErrorActionPreference = "Stop"

try { $host.UI.RawUI.WindowTitle = "Haodo 源码编译混淆打包工具" } catch {}

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
Write-Host "     Haodo 源码编译混淆打包工具（仅本地）" -ForegroundColor Cyan
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

Write-Host "[1/8] 读取发布版本号: v$verRaw ($(Split-Path $verFilePath -Leaf))" -ForegroundColor Green

# 3. 检查并结束正在运行的 Haodo.exe 进程
Write-Host "[2/8] 检查是否存在运行中的 Haodo.exe 进程..." -ForegroundColor Yellow
$runningProcs = Get-Process -Name "Haodo" -ErrorAction SilentlyContinue
if ($runningProcs) {
    Write-Host "      检测到 Haodo.exe 正在运行，正在强制结束进程..." -ForegroundColor DarkYellow
    $runningProcs | Stop-Process -Force
    Start-Sleep -Seconds 1
    Write-Host "      Haodo.exe 进程已成功终止。" -ForegroundColor Green
} else {
    Write-Host "      Haodo.exe 当前未运行。" -ForegroundColor Gray
}

# 4. 执行 dotnet publish Release 编译（首次全量编译，生成干净中间程序集）
Write-Host "[3/8] 正在调用 dotnet publish 编译 Release 单文件程序..." -ForegroundColor Yellow
$buildTmp = Join-Path $env:TEMP "HaodoBuild"
$publishTmp = Join-Path $buildTmp "publish"
$artifactsTmp = Join-Path $buildTmp "artifacts"

if (Test-Path $buildTmp) { Remove-Item $buildTmp -Recurse -Force -ErrorAction SilentlyContinue }

# 中间程序集与混淆相关路径（--artifacts-path 结构：obj\<项目名>\<config>_<rid>）
$objDir = Join-Path $artifactsTmp "obj\CLIProxyAPI_GUI\release_win-x64"
$objDll = Join-Path $objDir "Haodo.dll"
$obfOutDir = Join-Path $buildTmp "obfuscated"
$obfDll = Join-Path $obfOutDir "Haodo.dll"
$obfFixedDll = Join-Path $obfOutDir "Haodo_fixed.dll"
$obfConfig = Join-Path $buildTmp "obfuscar.xml"
$obfTool = Join-Path $rootDir "tools\Obfuscar\GlobalTools.dll"
$fixScript = Join-Path $rootDir "scripts\fix_obfuscated_rsrc.py"

# 单文件发布参数：框架依赖（免自包含，体积 ~900KB，目标机器需装 .NET 10 Desktop Runtime）+ 不生成调试符号
$dotnetArgs = @(
    "publish",
    "$csprojPath",
    "-c", "Release",
    "-r", "win-x64",
    "--no-self-contained",
    "--artifacts-path", "$artifactsTmp",
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

# 4.1 使用 Obfuscar（免费开源混淆器）对中间程序集进行混淆
Write-Host "[4/8] 正在使用 Obfuscar 混淆程序集..." -ForegroundColor Yellow
if (-not (Test-Path $obfTool)) {
    Write-Host "[错误] 未找到 Obfuscar 工具: $obfTool" -ForegroundColor Red
    Write-Host "提示: 请确认 tools\Obfuscar 目录完整（Obfuscar 3.0 GlobalTool 解包产物）。" -ForegroundColor Yellow
    Read-Host "按回车键退出..."
    exit 1
}
if (-not (Test-Path $objDll)) {
    Write-Host "[错误] 未找到中间程序集: $objDll" -ForegroundColor Red
    Read-Host "按回车键退出..."
    exit 1
}

# 动态生成混淆配置（Obfuscar 3.0 要求绝对路径，不支持变量展开）
# 注意: SkipType 同时保留 LocalProxyServer 与 DpiBootstrap
#   - LocalProxyServer: 由 UserControl 在 XAML 解析时经反射创建，混淆后反射找不到类型
#   - DpiBootstrap:     入口程序集静态构造引用，混淆后进程启动即崩溃
$obfXml = @"
<?xml version='1.0'?>
<Obfuscator>
  <Var name="InPath" value="$objDir" />
  <Var name="OutPath" value="$obfOutDir" />
  <Var name="LogFile" value="$buildTmp\obfuscar.log" />
  <Var name="HidePrivateApi" value="true" />
  <Var name="KeepPublicApi" value="true" />
  <Var name="HideStrings" value="true" />
  <Var name="AnalyzeXaml" value="true" />
  <Var name="UseUnicodeNames" value="true" />
  <Var name="SkipGenerated" value="true" />
  <Var name="SkipSpecialName" value="true" />
  <Var name="RegenerateDebugInfo" value="true" />
  <Var name="SuppressIldasm" value="true" />
  <Module file="$objDll">
    <SkipType name="CLIProxyAPI_GUI.LocalProxyServer" />
    <SkipType type="CLIProxyAPI_GUI.DpiBootstrap" />
  </Module>
</Obfuscator>
"@
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($obfConfig, $obfXml, $utf8NoBom)

& dotnet.exe $obfTool $obfConfig
if ($LASTEXITCODE -ne 0) {
    Write-Host "[错误] Obfuscar 混淆失败！请检查上方日志。" -ForegroundColor Red
    Read-Host "按回车键退出..."
    exit 1
}
if (-not (Test-Path $obfDll)) {
    Write-Host "[错误] 混淆产物缺失: $obfDll" -ForegroundColor Red
    Read-Host "按回车键退出..."
    exit 1
}
Write-Host "      Obfuscar 混淆完成。" -ForegroundColor Green

# 4.2 修复混淆后 Win32 资源段（.rsrc）RVA 偏移缺陷
# 背景: Obfuscar 3.0 重写 PE 时把 PE32+ 改写为 PE32，.rsrc 段整体后移（本工程 DELTA=+0xE000），
#       但资源树叶子节点 OffsetToData（绝对 RVA）未同步更新，导致 SDK CreateAppHost 任务
#       单文件打包时越界崩溃（ArgumentOutOfRangeException）。
# 方案: 用脚本对比原版与混淆版节表，自动重定位全部叶子节点 RVA 并自校验。
Write-Host "[5/8] 正在修复混淆产物的 Win32 资源段 RVA（Obfuscar 3.0 缺陷）..." -ForegroundColor Yellow
if (-not (Test-Path $fixScript)) {
    Write-Host "[错误] 未找到资源修复脚本: $fixScript" -ForegroundColor Red
    Read-Host "按回车键退出..."
    exit 1
}
& python.exe $fixScript $objDll $obfDll $obfFixedDll
if ($LASTEXITCODE -ne 0) {
    Write-Host "[错误] 资源段修复失败！请检查上方日志。" -ForegroundColor Red
    Read-Host "按回车键退出..."
    exit 1
}
if (-not (Test-Path $obfFixedDll)) {
    Write-Host "[错误] 资源修复产物缺失: $obfFixedDll" -ForegroundColor Red
    Read-Host "按回车键退出..."
    exit 1
}

# 4.3 用修复后的混淆程序集覆盖 obj 中间程序集
Copy-Item $obfFixedDll $objDll -Force
Write-Host "      已用修复后的混淆程序集替换中间产物。" -ForegroundColor Green

# 5. 使用混淆后的中间程序集重新打包单文件（--no-build 不重新编译，防止混淆被冲掉）
Write-Host "[6/8] 正在将混淆产物重新打包为单文件..." -ForegroundColor Yellow
& dotnet.exe @dotnetArgs "--no-build"
if ($LASTEXITCODE -ne 0) {
    Write-Host "[错误] 单文件重打包失败！请检查上方日志。" -ForegroundColor Red
    Read-Host "按回车键退出..."
    exit 1
}

# 6. 复制编译产物到根目录并清理 PDB 调试符号
Write-Host "[7/8] 正在复制混淆后的 Haodo.exe 到根目录..." -ForegroundColor Yellow
$outExe = Join-Path $publishTmp "Haodo.exe"
$targetExe = Join-Path $rootDir "Haodo.exe"
$targetPdb = Join-Path $rootDir "Haodo.pdb"

if (-not (Test-Path $outExe)) {
    Write-Host "[错误] 未找到编译输出文件: $outExe" -ForegroundColor Red
    Read-Host "按回车键退出..."
    exit 1
}

Copy-Item $outExe $targetExe -Force
Write-Host "      Haodo.exe 覆盖更新完成（混淆版）。" -ForegroundColor Green

# 7. 清理
Write-Host "[8/8] 正在清理 PDB 调试符号及临时编译缓存..." -ForegroundColor Yellow
if (Test-Path $targetPdb) { Remove-Item $targetPdb -Force -ErrorAction SilentlyContinue }
if (Test-Path $buildTmp) { Remove-Item $buildTmp -Recurse -Force -ErrorAction SilentlyContinue }

Write-Host ""
Write-Host "==================================================" -ForegroundColor Green
Write-Host " 🎉 本地编译混淆成功！Haodo v$verRaw 已更新至根目录！" -ForegroundColor Green
Write-Host "    （本次仅执行本地编译，未上传服务器发布）" -ForegroundColor Yellow
Write-Host "==================================================" -ForegroundColor Green
Write-Host ""
Write-Host "编译流程完毕，窗口将在 3 秒后自动关闭..." -ForegroundColor Gray
Start-Sleep -Seconds 3
