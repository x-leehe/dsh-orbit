# 从 CHANGELOG.md 取某个版本的正文；取不到就返回非 0（发版门禁）。
#
#   pwsh tools/changelog.ps1 -Validate                 # 校验格式 + csproj 版本必须有条目
#   pwsh tools/changelog.ps1 -Version 0.2.0            # 打印该版本正文（发版用）
#   pwsh tools/changelog.ps1 -Version 0.2.0 -Out x.md  # 写文件
#
# 规则：标题必须是 `## [x.y.z] - YYYY-MM-DD`；同一版本不能重复；正文至少 80 字符。

param(
    [string]$Version,
    [string]$Out,
    [string]$Path = "CHANGELOG.md",
    [switch]$Validate
)

$ErrorActionPreference = 'Stop'

function Fail([string]$message) {
    Write-Host "::error::$message"
    Write-Error $message
    exit 1
}

if (-not (Test-Path $Path)) {
    Fail "缺少 $Path —— 没有 changelog 禁止发版"
}

$lines = Get-Content $Path
$headings = @()
$inFence = $false
for ($i = 0; $i -lt $lines.Count; $i++) {
    # 代码块里的示例标题不算
    if ($lines[$i] -match '^\s*```') {
        $inFence = -not $inFence
        continue
    }

    if (-not $inFence -and $lines[$i] -match '^##\s+(.+)$') {
        $headings += [pscustomobject]@{ Index = $i; Text = $matches[1].Trim() }
    }
}

if ($headings.Count -eq 0) {
    Fail "$Path 里没有任何版本条目"
}

$versions = @{}
foreach ($heading in $headings) {
    if ($heading.Text -notmatch '^\[?(\d+\.\d+\.\d+)\]?\s*-\s*(\d{4}-\d{2}-\d{2})$') {
        Fail "标题格式不合法：'## $($heading.Text)'，应为 '## [x.y.z] - YYYY-MM-DD'"
    }

    $parsed = $matches[1]
    if ($versions.ContainsKey($parsed)) {
        Fail "版本 $parsed 重复出现"
    }

    $versions[$parsed] = $heading.Index
}

Write-Host "CHANGELOG 校验通过：$($headings.Count) 个版本（$($versions.Keys -join ', ')）"

if ($Validate -and -not $Version) {
    # 主分支校验：csproj 里的 <Version> 必须有对应条目
    [xml]$project = Get-Content "DshOrbit.csproj"
    $declared = $project.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if (-not $declared) {
        Fail "DshOrbit.csproj 里没有 <Version>"
    }

    if (-not $versions.ContainsKey($declared)) {
        Fail "DshOrbit.csproj 的 Version=$declared 在 CHANGELOG.md 里没有条目（先补 changelog 再发版）"
    }

    Write-Host "csproj Version=$declared 已有 changelog 条目"
    exit 0
}

if (-not $Version) {
    exit 0
}

$normalized = $Version.TrimStart('v')
if (-not $versions.ContainsKey($normalized)) {
    Fail "CHANGELOG.md 里没有 $normalized 的条目 —— 先补 changelog 再发版"
}

$start = $versions[$normalized]
$end = $lines.Count
for ($i = $start + 1; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '^##\s') {
        $end = $i
        break
    }
}

$body = (($lines[$start..($end - 1)]) -join "`n").Trim()
if ($body.Length -lt 80) {
    Fail "$normalized 的 changelog 正文太短（$($body.Length) 字符），疑似占位"
}

if ($Out) {
    Set-Content -Path $Out -Value $body -Encoding utf8
    Write-Host "已写入 $Out（$($body.Length) 字符）"
}
else {
    Write-Output $body
}
