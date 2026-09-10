# bench.ps1 - 预览延迟基准
#
# 依赖 QuickLookNext 内置的隐藏 /test-timing 钩子：常驻实例在每次预览
# "内容就绪"（spinner 消失）时向 %TEMP%\ql-smoke\timing.txt 追加一条
# 带文件路径的就绪时间戳。本脚本为每个测试文件发起一次预览请求，
# 用"请求时刻 -> 就绪时刻"计算延迟。
#
# 用法: .\bench.ps1 [-Rounds 2]
# 前置: 先运行 test.ps1（准备测试文件并完成构建），或手动构建 Release。

param([int]$Rounds = 2)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $root 'Build\Release\QuickLook-Next.exe'
# v1.2.36: keep bench files inside the repository instead of the C: temp
# folder; the app's diagnostics follow via QL_SMOKE_DIR.
$smoke = Join-Path $root 'ql-smoke'
$env:QL_SMOKE_DIR = $smoke
$timing = Join-Path $smoke 'timing.txt'
$startup = Join-Path $smoke 'startup.txt'

if (-not (Test-Path $exe)) {
    throw "未找到 $exe，请先运行 test.ps1 完成构建"
}

$fileNames = @('test.png', 'test.txt', 'test.md', 'test.json', 'test.zip', 'test.ttf', 'test.pdf')
$files = $fileNames | ForEach-Object { Join-Path $smoke $_ } |
    Where-Object { Test-Path -LiteralPath $_ }
if ($files.Count -eq 0) {
    throw "测试文件不存在（$smoke），请先运行 test.ps1"
}

if (Test-Path -LiteralPath $timing) {
    Remove-Item -LiteralPath $timing -Force
}
if (Test-Path -LiteralPath $startup) {
    Remove-Item -LiteralPath $startup -Force
}

$p = Start-Process -FilePath $exe -ArgumentList '/autorun', '/test-timing', '/test-startup' -PassThru
$requests = [System.Collections.Generic.List[object]]::new()
try {
    Start-Sleep -Seconds 14   # 等插件加载完成
    foreach ($round in 1..$Rounds) {
        foreach ($f in $files) {
            $t0 = Get-Date
            & $exe $f | Out-Null
            $requests.Add([pscustomobject]@{ Path = $f; RequestTime = $t0 })
            Start-Sleep -Seconds 15   # 给预览完成留足时间
        }
    }
}
finally {
    Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
}

if (-not (Test-Path -LiteralPath $timing)) {
    Write-Host "没有生成 timing 条目（timing.txt 缺失）" -ForegroundColor Red
    exit 1
}

# 解析就绪时间戳（按文件路径分组，同一文件可能有多轮条目）
$ready = @{}
foreach ($line in Get-Content -LiteralPath $timing) {
    $parts = $line.Split('|')
    if ($parts.Count -lt 2) { continue }
    $t = [DateTime]::Parse($parts[0]).ToLocalTime()
    $path = $parts[1]
    if (-not $ready.ContainsKey($path)) {
        $ready[$path] = [System.Collections.Generic.List[DateTime]]::new()
    }
    $ready[$path].Add($t)
}

# 为每个请求匹配"晚于请求时刻"的最早就绪条目
$results = [System.Collections.Generic.List[object]]::new()
foreach ($r in $requests) {
    $candidates = @($ready[$r.Path] | Where-Object { $_ -ge $r.RequestTime.AddSeconds(-2) })
    if ($candidates.Count -gt 0) {
        $rt = ($candidates | Sort-Object)[0]
        $latency = [math]::Round(($rt - $r.RequestTime).TotalMilliseconds, 0)
        $results.Add([pscustomobject]@{ File = [IO.Path]::GetFileName($r.Path); LatencyMs = $latency })
        $ready[$r.Path].Remove($rt) | Out-Null
    }
    else {
        $results.Add([pscustomobject]@{ File = [IO.Path]::GetFileName($r.Path); LatencyMs = $null })
    }
}

Write-Host "逐次结果：" -ForegroundColor Cyan
$results | Format-Table -AutoSize

Write-Host "汇总（ms）：" -ForegroundColor Cyan
$results | Group-Object File | ForEach-Object {
    $lats = @($_.Group | Where-Object { $null -ne $_.LatencyMs } | Select-Object -ExpandProperty LatencyMs)
    [pscustomobject]@{
        File = $_.Name
        次数 = $lats.Count
        平均 = if ($lats.Count) { [math]::Round(($lats | Measure-Object -Average).Average, 0) } else { 'N/A' }
        最快 = if ($lats.Count) { ($lats | Measure-Object -Minimum).Minimum } else { 'N/A' }
        最慢 = if ($lats.Count) { ($lats | Measure-Object -Maximum).Maximum } else { 'N/A' }
    }
} | Format-Table -AutoSize

if (Test-Path -LiteralPath $startup) {
    $startupLines = Get-Content -LiteralPath $startup
    $end = $startupLines | Where-Object { $_ -match '\|onstartup-end$' } |
        ForEach-Object { $_.Split('|')[0] } | Select-Object -First 1
    $plugins = $startupLines | Where-Object { $_ -match '\|plugins-inited$' } |
        ForEach-Object { $_.Split('|')[0] } | Select-Object -First 1
    Write-Host "启动耗时（ms）：" -ForegroundColor Cyan
    [pscustomobject]@{
        UI就绪 = if ($end) { "$end ms" } else { 'N/A' }
        插件就绪 = if ($plugins) { "$plugins ms" } else { 'N/A' }
    } | Format-Table -AutoSize
}
