# flatten-native.ps1
# 将本工程（插件）引用的 RID 特定原生资产（runtimes\<rid>\native\*.dll）
# 从 NuGet 缓存复制到插件输出根目录，供 Assembly.LoadFrom 加载的插件在运行时解析。
#
# 用法（由各插件 csproj 的 FlattenRuntimeNative 目标调用）：
#   powershell -File flatten-native.ps1 -AssetsPath <project.assets.json>
#              -ProjectDir <plugin project dir> -Configuration <Debug|Release> -Platform <Platform>

param(
    [Parameter(Mandatory = $true)][string]$AssetsPath,
    [Parameter(Mandatory = $true)][string]$ProjectDir,
    [Parameter(Mandatory = $true)][string]$Configuration,
    [string]$Platform = '',
    [string]$SkipNames = ''
)

$ErrorActionPreference = 'Stop'

$pluginDir = Split-Path $ProjectDir -Leaf
$repoRoot = Split-Path (Split-Path $ProjectDir -Parent) -Parent
$OutputPath = Join-Path $repoRoot "Build\$Configuration\QuickLook.Plugin\$pluginDir"

$rid = switch ($Platform) {
    'ARM64' { 'win-arm64' }
    'x86'   { 'win-x86' }
    default { 'win-x64' }
}

# v1.3.0: file names (case-insensitive) that must NOT be flattened into the
# plugin root - some plugins load natives from runtimes\... explicitly and the
# root copy would only bloat the package.
$skipSet = @($SkipNames.Split(',', [System.StringSplitOptions]::RemoveEmptyEntries) |
    ForEach-Object { $_.Trim().ToLowerInvariant() })

if (-not (Test-Path $AssetsPath)) {
    Write-Host "flatten-native: assets not found, skip: $AssetsPath"
    exit 0
}

$assets = Get-Content $AssetsPath -Raw | ConvertFrom-Json
$nugetRoot = $assets.packageFolders.PSObject.Properties | Select-Object -First 1 -ExpandProperty Name
if (-not $nugetRoot) {
    Write-Host 'flatten-native: no packageFolders in assets'
    exit 0
}

$ridTargets = @($assets.targets.PSObject.Properties | Where-Object { $_.Name -like "*/$rid" })
if ($ridTargets.Count -eq 0) {
    Write-Host "flatten-native: no RID target for $rid"
    exit 0
}

$copied = 0
foreach ($target in $ridTargets) {
    $target.Value.PSObject.Properties | ForEach-Object {
        $pkg = $_
        if ($pkg.Value.native) {
            $pkg.Value.native.PSObject.Properties | ForEach-Object {
                $rel = $_.Name
                if ($rel -like "runtimes/$rid/native/*") {
                    $parts = $pkg.Name -split '/'
                    if ($parts.Count -lt 2) { return }
                    $pkgId = $parts[0].ToLowerInvariant()
                    $pkgVer = $parts[1].ToLowerInvariant()
                    $src = Join-Path $nugetRoot (Join-Path $pkgId (Join-Path $pkgVer $rel))
                    if (Test-Path -LiteralPath $src) {
                        $fileName = [IO.Path]::GetFileName($rel)
                        if ($skipSet -contains $fileName.ToLowerInvariant()) {
                            Write-Host "flatten-native: skip $fileName"
                        }
                        else {
                            $dest = Join-Path $OutputPath $fileName
                            Copy-Item -LiteralPath $src -Destination $dest -Force
                            $copied++
                        }
                    }
                    else {
                        Write-Host "flatten-native: source missing: $src"
                    }
                }
            }
        }
    }
}

Write-Host "flatten-native: copied $copied native file(s) for $rid"
