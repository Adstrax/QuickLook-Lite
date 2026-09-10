# QuickLookNext 冒烟测试：每次提交前必须运行并通过。
# 覆盖：全量构建 -> 启动 -> 插件加载无失败 -> PNG/文本/SQLite 预览 -> 窗口断言 -> 日志零新增错误。
#
# 用法: .\test.ps1

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $root 'Build\Release\QuickLook-Next.exe'
$log = Join-Path $env:APPDATA 'pooi.moe\QuickLookNext\QuickLookNext.Exception.log'
# v1.2.36: keep the smoke-test files inside the repository
# (E:\Codex\QK-Lite\<version>\ql-smoke) instead of the C: temp folder; the
# app's diagnostics (timing/startup/tray-menu) follow via QL_SMOKE_DIR.
$smoke = Join-Path $root 'ql-smoke'
$env:QL_SMOKE_DIR = $smoke
$failed = $false

function Assert([bool]$cond, [string]$msg) {
    if ($cond) { Write-Host "PASS: $msg" -ForegroundColor Green }
    else { Write-Host "FAIL: $msg" -ForegroundColor Red; $script:failed = $true }
}

function Get-LogLength {
    if (Test-Path $log) { (Get-Item $log).Length } else { 0 }
}

function Get-QuickLookNextWindows([int]$targetPid) {
    if (-not ('WinEnum3' -as [type])) {
        Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Collections.Generic;
public class WinEnum3 {
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
  [DllImport("user32.dll")] static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
  delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
  public static List<string> Titles(uint targetPid) {
    var list = new List<string>();
    EnumWindows((h, l) => { uint pid; GetWindowThreadProcessId(h, out pid); if (pid == targetPid) { var sb = new StringBuilder(512); GetWindowText(h, sb, 512); if (sb.Length > 0) list.Add(sb.ToString()); } return true; }, IntPtr.Zero);
    return list;
  }
}
"@
    }
    [WinEnum3]::Titles($targetPid)
}

function Get-QuickLookNextWindowRect([int]$targetPid, [string]$titleMatch) {
    if (-not ('WinEnumRect' -as [type])) {
        Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class WinEnumRect {
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
  [DllImport("user32.dll")] static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
  static string result = "";
  public static string Rect(uint targetPid, string titleMatch) {
    result = "";
    EnumWindows((h, l) => { uint pid; GetWindowThreadProcessId(h, out pid); if (pid == targetPid && IsWindowVisible(h)) { var sb = new StringBuilder(512); GetWindowText(h, sb, 512); if (sb.Length > 0 && sb.ToString().Contains(titleMatch)) { RECT r; GetWindowRect(h, out r); result = r.Left + "|" + r.Top + "|" + (r.Right - r.Left) + "|" + (r.Bottom - r.Top); return false; } } return true; }, IntPtr.Zero);
    return result;
  }
}
"@
    }
    [WinEnumRect]::Rect($targetPid, $titleMatch)
}

# ---------- 1. 清理旧实例 ----------
Write-Host "== 1/6 清理旧实例 ==" -ForegroundColor Cyan
# v3.31.0: 进程名是 "QuickLook-Next"（程序集名带连字符）。旧写法永远匹配不到，
# 于是托盘里残留的实例会让下面启动的实例作为「第二实例」转发后立即退出，
# 后续所有断言都在检查一个已经死掉的进程 —— 表现为一整片莫名其妙的失败。
Get-Process -Name 'QuickLook-Next' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 3
if ($null -ne (Get-Process -Name 'QuickLook-Next' -ErrorAction SilentlyContinue)) {
    Write-Host '=== 旧实例无法清理，测试终止（新实例会作为第二实例直接退出）===' -ForegroundColor Red
    exit 1
}

# ---------- 2. 构建 ----------
Write-Host "== 2/6 全量构建 ==" -ForegroundColor Cyan
Get-ChildItem (Join-Path $root 'Build\Release') -Force -ErrorAction SilentlyContinue |
    Remove-Item -Recurse -Force
# v1.2.34: build the whole solution in one parallel invocation instead of
# compiling the 16 projects one by one - roughly halves the test time.
& dotnet build (Join-Path $root 'QuickLookNext.slnx') -c Release -v minimal --nologo *> $null
Assert ($LASTEXITCODE -eq 0) '构建 QuickLookNext.slnx'
if ($LASTEXITCODE -ne 0) { exit 1 }

# ---------- 3. 准备测试文件 ----------
Write-Host "== 3/6 准备测试文件 ==" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $smoke | Out-Null
Add-Type -AssemblyName System.Drawing
$bmp = New-Object System.Drawing.Bitmap 256, 256
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.Clear([System.Drawing.Color]::SteelBlue)
$font = New-Object System.Drawing.Font 'Arial', 24
$g.DrawString('QL Test', $font, [System.Drawing.Brushes]::White, 40, 100)
$g.Dispose()
$bmp.Save((Join-Path $smoke 'test.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Set-Content -Path (Join-Path $smoke 'test.txt') -Value "QuickLookNext smoke test`r`nLine 2" -Encoding UTF8
Set-Content -Path (Join-Path $smoke 'test.md') -Value "# Markdown`n`n这是 **测试**。" -Encoding UTF8
Set-Content -Path (Join-Path $smoke 'test.json') -Value '{"name":"ql-smoke","version":"1.0.0","scripts":{}}' -Encoding UTF8
Compress-Archive -Path (Join-Path $smoke 'test.txt') -DestinationPath (Join-Path $smoke 'test.zip') -Force
Copy-Item -LiteralPath "$env:WINDIR\Fonts\arial.ttf" -Destination (Join-Path $smoke 'test.ttf') -Force
if (-not (Test-Path (Join-Path $smoke 'test.mp4'))) {
    curl.exe -sL -o (Join-Path $smoke 'test.mp4') "https://interactive-examples.mdn.mozilla.net/media/cc0-videos/flower.mp4"
}
if (-not (Test-Path (Join-Path $smoke 'test.pdf'))) {
    curl.exe -sL -o (Join-Path $smoke 'test.pdf') "https://raw.githubusercontent.com/mozilla/pdf.js/master/web/compressed.tracemonkey-pldi-09.pdf"
}
# v3.7.0: rare-format coverage (on-demand plugin loading) + mermaid/math
# markdown paths. These used to be untested, which let a lazy-loading
# regression slip through.
[System.IO.File]::WriteAllBytes(
    (Join-Path $smoke 'test.bin'),
    [byte[]](0..9))
Copy-Item -LiteralPath (Join-Path $root 'Build\Release\QuickLook-Next.exe') `
    -Destination (Join-Path $smoke 'test-pe.exe') -Force
Set-Content -Path (Join-Path $smoke 'test-mermaid.md') `
    -Value "## 图`n`n``````mermaid`ngraph TD;`n  A-->B;`n``````" -Encoding UTF8
Set-Content -Path (Join-Path $smoke 'test-math.md') `
    -Value "## 公式`n`n质能方程 $E=mc^2$ 或 $$\int_0^1 x dx$$" -Encoding UTF8
# v3.12.0: a minimal but valid xlsx (OOXML zip) for the self-rendered
# spreadsheet preview.
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$xlsxPath = Join-Path $smoke 'test.xlsx'
$xlsxFs = [System.IO.File]::Open($xlsxPath, [System.IO.FileMode]::Create)
$xlsxZip = New-Object System.IO.Compression.ZipArchive($xlsxFs,
    [System.IO.Compression.ZipArchiveMode]::Create)
function Add-XlsxEntry($zip, $name, $content) {
    $entry = $zip.CreateEntry($name)
    $writer = New-Object System.IO.StreamWriter($entry.Open(),
        (New-Object System.Text.UTF8Encoding($false)))
    $writer.Write($content)
    $writer.Dispose()
}
Add-XlsxEntry $xlsxZip '[Content_Types].xml' '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/></Types>'
Add-XlsxEntry $xlsxZip '_rels/.rels' '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>'
Add-XlsxEntry $xlsxZip 'xl/workbook.xml' '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Sheet1" sheetId="1" r:id="rId1"/></sheets></workbook>'
Add-XlsxEntry $xlsxZip 'xl/_rels/workbook.xml.rels' '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>'
$xlsxRows = '<row r="1"><c r="A1" t="inlineStr"><is><t>姓名</t></is></c><c r="B1" t="inlineStr"><is><t>部门</t></is></c><c r="C1" t="inlineStr"><is><t>月薪</t></is></c><c r="D1" t="inlineStr"><is><t>入职日期</t></is></c></row>'
$xlsxData = @(@('张三','研发','18000','2024-03-15'),@('李四','产品','16000','2023-11-02'),@('王五','设计','15000','2025-01-20'))
for ($i = 0; $i -lt $xlsxData.Count; $i++) {
    $r = $i + 2
    $cells = @()
    for ($c = 0; $c -lt 4; $c++) {
        $col = [char](65 + $c)
        $val = $xlsxData[$i][$c]
        if ($c -eq 2) { $cells += "<c r=`"$col$r`"><v>$val</v></c>" }
        else { $cells += "<c r=`"$col$r`" t=`"inlineStr`"><is><t>$val</t></is></c>" }
    }
    $xlsxRows += "<row r=`"$r`">$($cells -join '')</row>"
}
Add-XlsxEntry $xlsxZip 'xl/worksheets/sheet1.xml' "<?xml version=`"1.0`" encoding=`"UTF-8`" standalone=`"yes`"?><worksheet xmlns=`"http://schemas.openxmlformats.org/spreadsheetml/2006/main`"><sheetData>$xlsxRows</sheetData></worksheet>"
$xlsxZip.Dispose()
$xlsxFs.Dispose()
# v3.14.0: a minimal but valid docx (heading / formatting / list / table) for
# the self-rendered Word preview.
$docxPath = Join-Path $smoke 'test.docx'
$docxFs = [System.IO.File]::Open($docxPath, [System.IO.FileMode]::Create)
$docxZip = New-Object System.IO.Compression.ZipArchive($docxFs,
    [System.IO.Compression.ZipArchiveMode]::Create)
$wNs = 'http://schemas.openxmlformats.org/wordprocessingml/2006/main'
Add-XlsxEntry $docxZip '[Content_Types].xml' "<?xml version=`"1.0`" encoding=`"UTF-8`" standalone=`"yes`"?><Types xmlns=`"http://schemas.openxmlformats.org/package/2006/content-types`"><Default Extension=`"rels`" ContentType=`"application/vnd.openxmlformats-package.relationships+xml`"/><Default Extension=`"xml`" ContentType=`"application/xml`"/><Override PartName=`"/word/document.xml`" ContentType=`"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml`"/><Override PartName=`"/word/styles.xml`" ContentType=`"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml`"/><Override PartName=`"/word/numbering.xml`" ContentType=`"application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml`"/></Types>"
Add-XlsxEntry $docxZip '_rels/.rels' '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>'
Add-XlsxEntry $docxZip 'word/_rels/document.xml.rels' '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/><Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/numbering" Target="numbering.xml"/></Relationships>'
Add-XlsxEntry $docxZip 'word/styles.xml' '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:styles xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:style w:type="paragraph" w:styleId="Heading1"><w:name w:val="heading 1"/></w:style></w:styles>'
Add-XlsxEntry $docxZip 'word/numbering.xml' '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:numbering xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:abstractNum w:abstractNumId="0"><w:lvl w:ilvl="0"><w:numFmt w:val="bullet"/><w:lvlText w:val="•"/></w:lvl></w:abstractNum><w:num w:numId="1"><w:abstractNumId w:val="0"/></w:num></w:numbering>'
$docxBody = "<w:document xmlns:w=`"$wNs`"><w:body>"
$docxBody += '<w:p><w:pPr><w:pStyle w:val="Heading1"/></w:pPr><w:r><w:t>QuickLook-Next 测试文档</w:t></w:r></w:p>'
$docxBody += '<w:p><w:r><w:t>普通文本，</w:t></w:r><w:r><w:rPr><w:b/></w:rPr><w:t>加粗</w:t></w:r><w:r><w:rPr><w:i/></w:rPr><w:t>斜体</w:t></w:r><w:r><w:rPr><w:color w:val="C00000"/><w:sz w:val="28"/></w:rPr><w:t>红色大字</w:t></w:r></w:p>'
$docxBody += '<w:p><w:pPr><w:numPr><w:ilvl w:val="0"/><w:numId w:val="1"/></w:numPr></w:pPr><w:r><w:t>项目一</w:t></w:r></w:p>'
$docxBody += '<w:p><w:pPr><w:numPr><w:ilvl w:val="0"/><w:numId w:val="1"/></w:numPr></w:pPr><w:r><w:t>项目二</w:t></w:r></w:p>'
$docxBody += '<w:tbl><w:tr><w:tc><w:tcPr><w:gridSpan w:val="2"/></w:tcPr><w:p><w:r><w:t>合并表头</w:t></w:r></w:p></w:tc></w:tr><w:tr><w:tc><w:p><w:r><w:t>单元格 A</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>单元格 B</w:t></w:r></w:p></w:tc></w:tr></w:tbl>'
$docxBody += '</w:body></w:document>'
Add-XlsxEntry $docxZip 'word/document.xml' $docxBody
$docxZip.Dispose()
$docxFs.Dispose()
# v3.15.0: a minimal but valid pptx (two positioned text-box slides) for the
# self-rendered PowerPoint preview.
$pptxPath = Join-Path $smoke 'test.pptx'
$pptxFs = [System.IO.File]::Open($pptxPath, [System.IO.FileMode]::Create)
$pptxZip = New-Object System.IO.Compression.ZipArchive($pptxFs,
    [System.IO.Compression.ZipArchiveMode]::Create)
$pNs = 'http://schemas.openxmlformats.org/presentationml/2006/main'
$aNs = 'http://schemas.openxmlformats.org/drawingml/2006/main'
$rNs = 'http://schemas.openxmlformats.org/officeDocument/2006/relationships'
Add-XlsxEntry $pptxZip '[Content_Types].xml' "<?xml version=`"1.0`" encoding=`"UTF-8`" standalone=`"yes`"?><Types xmlns=`"http://schemas.openxmlformats.org/package/2006/content-types`"><Default Extension=`"rels`" ContentType=`"application/vnd.openxmlformats-package.relationships+xml`"/><Default Extension=`"xml`" ContentType=`"application/xml`"/><Override PartName=`"/ppt/presentation.xml`" ContentType=`"application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml`"/><Override PartName=`"/ppt/slides/slide1.xml`" ContentType=`"application/vnd.openxmlformats-officedocument.presentationml.slide+xml`"/><Override PartName=`"/ppt/slides/slide2.xml`" ContentType=`"application/vnd.openxmlformats-officedocument.presentationml.slide+xml`"/></Types>"
Add-XlsxEntry $pptxZip '_rels/.rels' "<?xml version=`"1.0`" encoding=`"UTF-8`" standalone=`"yes`"?><Relationships xmlns=`"http://schemas.openxmlformats.org/package/2006/relationships`"><Relationship Id=`"rId1`" Type=`"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument`" Target=`"ppt/presentation.xml`"/></Relationships>"
Add-XlsxEntry $pptxZip 'ppt/_rels/presentation.xml.rels' "<?xml version=`"1.0`" encoding=`"UTF-8`" standalone=`"yes`"?><Relationships xmlns=`"http://schemas.openxmlformats.org/package/2006/relationships`"><Relationship Id=`"rId1`" Type=`"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide`" Target=`"slides/slide1.xml`"/><Relationship Id=`"rId2`" Type=`"http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide`" Target=`"slides/slide2.xml`"/></Relationships>"
Add-XlsxEntry $pptxZip 'ppt/presentation.xml' "<?xml version=`"1.0`" encoding=`"UTF-8`" standalone=`"yes`"?><p:presentation xmlns:p=`"$pNs`" xmlns:r=`"$rNs`"><p:sldSz cx=`"12192000`" cy=`"6858000`"/><p:sldIdLst><p:sldId id=`"256`" r:id=`"rId1`"/><p:sldId id=`"257`" r:id=`"rId2`"/></p:sldIdLst></p:presentation>"
$slideXml1 = "<?xml version=`"1.0`" encoding=`"UTF-8`" standalone=`"yes`"?><p:sld xmlns:p=`"$pNs`" xmlns:a=`"$aNs`" xmlns:r=`"$rNs`"><p:cSld><p:spTree>"
$slideXml1 += '<p:sp><p:nvSpPr><p:cNvPr id="2" name="Title"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:spPr><a:xfrm><a:off x="457200" y="2286000"/><a:ext cx="11277600" cy="1524000"/></a:xfrm></p:spPr><p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:pPr algn="ctr"/><a:r><a:rPr lang="zh-CN" sz="5400" b="1"/><a:t>QuickLook-Next 演示文稿</a:t></a:r></a:p></p:txBody></p:sp>'
$slideXml1 += '<p:sp><p:nvSpPr><p:cNvPr id="3" name="Subtitle"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:spPr><a:xfrm><a:off x="2286000" y="4114800"/><a:ext cx="7620000" cy="914400"/></a:xfrm></p:spPr><p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:pPr algn="ctr"/><a:r><a:rPr lang="zh-CN" sz="2400"/><a:t>自研渲染测试</a:t></a:r></a:p></p:txBody></p:sp>'
$slideXml1 += '</p:spTree></p:cSld></p:sld>'
Add-XlsxEntry $pptxZip 'ppt/slides/slide1.xml' $slideXml1
$slideXml2 = "<?xml version=`"1.0`" encoding=`"UTF-8`" standalone=`"yes`"?><p:sld xmlns:p=`"$pNs`" xmlns:a=`"$aNs`" xmlns:r=`"$rNs`"><p:cSld><p:spTree>"
$slideXml2 += '<p:sp><p:nvSpPr><p:cNvPr id="2" name="Body"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:spPr><a:xfrm><a:off x="914400" y="914400"/><a:ext cx="10363200" cy="5029200"/></a:xfrm></p:spPr><p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:rPr lang="zh-CN" sz="2800" b="1"/><a:t>要点列表</a:t></a:r></a:p><a:p><a:r><a:rPr lang="zh-CN" sz="2000"/><a:t>第一点：标题、文本、定位都支持</a:t></a:r></a:p></p:txBody></p:sp>'
$slideXml2 += '</p:spTree></p:cSld></p:sld>'
Add-XlsxEntry $pptxZip 'ppt/slides/slide2.xml' $slideXml2
Add-XlsxEntry $pptxZip 'ppt/slides/_rels/slide1.xml.rels' '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"/>'
Add-XlsxEntry $pptxZip 'ppt/slides/_rels/slide2.xml.rels' '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"/>'
$pptxZip.Dispose()
$pptxFs.Dispose()

# ---------- 4. 启动 + 插件加载 ----------
Write-Host "== 4/6 启动并验证插件加载 ==" -ForegroundColor Cyan
$before = Get-LogLength
$p = Start-Process -FilePath $exe -ArgumentList '/autorun /test-tray-menu' -PassThru
$trayMenuSeen = $false
for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Milliseconds 500
    $alive = Get-Process -Id $p.Id -ErrorAction SilentlyContinue
    if ($null -eq $alive) { break }
    $titles = Get-QuickLookNextWindows $p.Id
    if (($titles -join ' ') -match 'QuickLook-Next Tray Menu') { $trayMenuSeen = $true; break }
}
# Wait for the menu to auto-close and the plugins to finish loading.
Start-Sleep -Seconds 15
$alive = Get-Process -Id $p.Id -ErrorAction SilentlyContinue
Assert ($null -ne $alive) '启动后进程存活'
Assert $trayMenuSeen '托盘菜单窗口出现并自动关闭'
$diagFile = Join-Path $smoke 'tray-menu-dwm.txt'
$dwmDiag = if (Test-Path $diagFile) { Get-Content $diagFile -Raw } else { '' }
if ($env:QL_SMOKE_CI -eq '1') {
    # CI runners may not have DWM compositing; skip the Acrylic assertion.
    Write-Host 'SKIP: 托盘菜单 Acrylic（CI 环境）' -ForegroundColor Yellow
}
else {
    Assert ($dwmDiag -match 'accent-applied=True') '托盘菜单 Acrylic 已应用（WCA 调用成功）'
}
Assert ($dwmDiag -match 'more-menu-opened=true') 'More 菜单复用同一 Acrylic 菜单路径'
Assert ((Get-LogLength) -eq $before) '插件加载无失败（日志零新增）'

# ---------- 5. 预览测试 ----------
Write-Host "== 5/6 预览测试 ==" -ForegroundColor Cyan
$previews = @(
    @{ File = 'test.png'; Title = 'test.png' },
    @{ File = 'test.txt'; Title = 'test.txt' },
    @{ File = 'test.md'; Title = 'test.md' },
    @{ File = 'test.json'; Title = 'test.json' },
    @{ File = 'test.zip'; Title = 'test.zip' },
    @{ File = 'test.ttf'; Title = 'test.ttf' }
)
if (Test-Path (Join-Path $smoke 'test.mp4')) {
    $previews += @{ File = 'test.mp4'; Title = 'test.mp4' }
}
if (Test-Path (Join-Path $smoke 'test.pdf')) {
    $previews += @{ File = 'test.pdf'; Title = 'test.pdf' }
}
# v3.7.0: rare-format previews exercise the on-demand lazy plugin loading.
# PEViewer deliberately shows no window title (full-bleed PE info panel), so
# only the process-alive and no-error assertions apply to it.
$previews += @{ File = 'test-pe.exe'; Title = 'test-pe.exe'; CheckTitle = $false }
$previews += @{ File = 'test.bin'; Title = 'test.bin' }
$previews += @{ File = 'test-mermaid.md'; Title = 'test-mermaid.md' }
$previews += @{ File = 'test-math.md'; Title = 'test-math.md' }
# v3.12.0: self-rendered spreadsheet preview.
$previews += @{ File = 'test.xlsx'; Title = 'test.xlsx' }
# v3.14.0: self-rendered Word document preview.
$previews += @{ File = 'test.docx'; Title = 'test.docx' }
# v3.15.0: self-rendered PowerPoint preview.
$previews += @{ File = 'test.pptx'; Title = 'test.pptx' }
# v3.8.0: folder preview (InfoPanel) coverage - InfoPanel shows no title.
$previews += @{ Folder = $smoke; Title = 'ql-smoke'; CheckTitle = $false }
foreach ($pv in $previews) {
    $before = Get-LogLength
    $target = if ($pv.Folder) { $pv.Folder } else { Join-Path $smoke $pv.File }
    $label = if ($pv.Folder) { $pv.Title } else { $pv.File }
    & $exe $target
    Start-Sleep -Seconds 12
    $alive = Get-Process -Id $p.Id -ErrorAction SilentlyContinue
    Assert ($null -ne $alive) "预览 $label 后进程存活"
    if ($pv.CheckTitle -ne $false) {
        $titles = Get-QuickLookNextWindows $p.Id
        Assert (($titles -join ' ') -match [regex]::Escape($pv.Title)) "预览窗口出现: $($pv.Title)"
    }
    Assert ((Get-LogLength) -eq $before) "预览 $label 无错误（日志零新增）"

    # v1.2.36: regression guard - the preview window must be centered on the
    # screen that contains it (the off-screen warm-up once broke this).
    if ($pv.File -eq 'test.png') {
        $rectStr = Get-QuickLookNextWindowRect $p.Id $pv.Title
        if ($rectStr) {
            $rp = $rectStr.Split('|')
            $winCenter = [System.Drawing.Point]::new(
                [int]$rp[0] + [int]$rp[2] / 2,
                [int]$rp[1] + [int]$rp[3] / 2)
            $screen = [System.Windows.Forms.Screen]::AllScreens |
                Where-Object { $_.WorkingArea.Contains($winCenter) } |
                Select-Object -First 1
            if ($null -eq $screen) { $screen = [System.Windows.Forms.Screen]::PrimaryScreen }
            $wa = $screen.WorkingArea
            $cx = $wa.Left + $wa.Width / 2
            $cy = $wa.Top + $wa.Height / 2
            Assert ([math]::Abs($winCenter.X - $cx) -lt $wa.Width * 0.15) "预览窗口水平居中: $($pv.Title)"
            Assert ([math]::Abs($winCenter.Y - $cy) -lt $wa.Height * 0.15) "预览窗口垂直居中: $($pv.Title)"
        }
        else {
            Assert $false "预览窗口矩形可读取: $($pv.Title)"
        }
    }
}

# ---------- 6. 清理 ----------
# ---------- 6. Shell 集成验证（空格键链路：Explorer 选区读取） ----------
Write-Host "== 6/7 Shell 集成验证 ==" -ForegroundColor Cyan
$shellProbe = @"
using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
public class ShellProbe2 {
    private static readonly Guid IID_IDataObject = new Guid("0000010E-0000-0000-C000-000000000046");
    private static readonly Guid IID_IShellBrowser = new Guid("000214E2-0000-0000-C000-000000000046");
    private static readonly Guid CLSID_ShellWindows = new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IServiceProvider { [PreserveSig] int QueryService(ref Guid g, ref Guid r, out IntPtr p); }
    [ComImport, Guid("000214E2-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellBrowser {
        [PreserveSig] int GetWindow(out IntPtr phwnd);
        [PreserveSig] int ContextSensitiveHelp(int f);
        [PreserveSig] int InsertMenusSB(IntPtr a, IntPtr b);
        [PreserveSig] int SetMenuSB(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int RemoveMenusSB(IntPtr a);
        [PreserveSig] int SetStatusTextSB(IntPtr a);
        [PreserveSig] int EnableModelessSB(int f);
        [PreserveSig] int TranslateAcceleratorSB(IntPtr a, ushort b);
        [PreserveSig] int BrowseObject(IntPtr a, uint b);
        [PreserveSig] int GetViewStateStream(uint a, out IntPtr b);
        [PreserveSig] int GetControlWindow(uint a, out IntPtr b);
        [PreserveSig] int SendControlMsg(uint a, uint b, IntPtr c, IntPtr d, out IntPtr e);
        [PreserveSig] int QueryActiveShellView(out IntPtr ppshv);
        [PreserveSig] int OnViewWindowActive(IntPtr a);
        [PreserveSig] int SetToolbarItems(IntPtr a, uint b, uint c);
    }
    [ComImport, Guid("000214E3-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellView {
        [PreserveSig] int GetWindow(out IntPtr phwnd);
        [PreserveSig] int ContextSensitiveHelp(int f);
        [PreserveSig] int TranslateAccelerator(IntPtr a);
        [PreserveSig] int EnableModeless(int f);
        [PreserveSig] int UIActivate(uint s);
        [PreserveSig] int Refresh();
        [PreserveSig] int CreateViewWindow(IntPtr a, IntPtr b, uint c, IntPtr d, out IntPtr e);
        [PreserveSig] int DestroyViewWindow();
        [PreserveSig] int GetCurrentInfo(out IntPtr a);
        [PreserveSig] int AddPropertySheetPages(uint a, IntPtr b, IntPtr c);
        [PreserveSig] int SaveViewState();
        [PreserveSig] int SelectItem(IntPtr a, uint b);
        [PreserveSig] int GetItemObject(uint u, ref Guid r, out IntPtr p);
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int DragQueryFile(IntPtr h, uint i, StringBuilder b, int c);
    [DllImport("ole32.dll")]
    static extern void ReleaseStgMedium(ref STGMEDIUM m);
    public static string Probe() {
        Type swType = Type.GetTypeFromCLSID(CLSID_ShellWindows);
        object sw = Activator.CreateInstance(swType);
        int count = (int)swType.InvokeMember("Count", BindingFlags.GetProperty, null, sw, null);
        for (int i = 0; i < count; i++) {
            object disp = swType.InvokeMember("Item", BindingFlags.InvokeMethod, null, sw, new object[] { i });
            var sp = (IServiceProvider)disp;
            Guid iid = IID_IShellBrowser;
            IntPtr sbPtr;
            if (sp.QueryService(ref iid, ref iid, out sbPtr) != 0) continue;
            var browser = (IShellBrowser)Marshal.GetObjectForIUnknown(sbPtr);
            IntPtr psvPtr;
            if (browser.QueryActiveShellView(out psvPtr) != 0 || psvPtr == IntPtr.Zero) continue;
            var view = (IShellView)Marshal.GetObjectForIUnknown(psvPtr);
            Guid diid = IID_IDataObject;
            IntPtr daoPtr;
            if (view.GetItemObject(0x1, ref diid, out daoPtr) != 0 || daoPtr == IntPtr.Zero) continue;
            var dao = (IDataObject)Marshal.GetObjectForIUnknown(daoPtr);
            var fe = new FORMATETC { cfFormat = 15, ptd = IntPtr.Zero, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL };
            STGMEDIUM sm;
            try { dao.GetData(ref fe, out sm); }
            catch { return string.Empty; }
            try {
                var sb = new StringBuilder(32767);
                return DragQueryFile(sm.unionmember, 0, sb, sb.Capacity) > 0 ? sb.ToString() : string.Empty;
            }
            finally { ReleaseStgMedium(ref sm); }
        }
        return string.Empty;
 }
}
"@
Add-Type -TypeDefinition $shellProbe

# The selection probe depends on Explorer actually exposing a live file
# selection, which can be unavailable in non-interactive / minimized sessions
# (explorer.exe may refuse to open a /select window at all). Retry a few times;
# if no Explorer window can provide a selection, SKIP instead of failing - the
# app's own selection chain is exercised by the same COM probe whenever
# Explorer cooperates.
$probeResult = $null
for ($attempt = 1; $attempt -le 3; $attempt++) {
    $selWin = Get-Process explorer -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowTitle -like '*ql-smoke*' } | Select-Object -First 1
    if ($selWin) { $selWin.CloseMainWindow() | Out-Null; Start-Sleep -Seconds 2 }

    Start-Process explorer.exe "/select,`"$smoke\test.png`""
    Start-Sleep -Seconds 8

    $probeResult = [ShellProbe2]::Probe()
    if (-not [string]::IsNullOrWhiteSpace($probeResult)) { break }
}

if (-not [string]::IsNullOrWhiteSpace($probeResult)) {
    Assert $true "Explorer 选区读取链路（COM 探针）返回: $probeResult"
}
else {
    Write-Host "SKIP: 当前环境无 Explorer 文件窗口可供选区探针读取（不影响应用功能）" -ForegroundColor Yellow
}

# ---------- 7. 清理 ----------
Write-Host "== 7/7 清理 ==" -ForegroundColor Cyan
Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
# v3.31.0: also clean up anything a preview request may have spawned while the
# main instance was gone, so the next run starts from a known state.
Get-Process -Name 'QuickLook-Next' -ErrorAction SilentlyContinue | Stop-Process -Force

if ($failed) {
    Write-Host "`n=== 测试失败 ===" -ForegroundColor Red
    exit 1
}
Write-Host "`n=== 全部测试通过 ===" -ForegroundColor Green
exit 0
