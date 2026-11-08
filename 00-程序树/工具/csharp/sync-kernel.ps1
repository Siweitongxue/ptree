#Requires -Version 5.1
<#
.SYNOPSIS
    比较公开仓内核源码与公司仓内核源码，输出「一致 / 落后」结论。默认干跑（只读）。

.DESCRIPTION
    按相对路径集合 + SHA256 文件哈希，比对两个 src 目录下的所有 *.cs 文件，忽略 bin/obj 目录。

    输出三种情况：
      1) 两仓一致         -> 打印「一致」+ 文件数，exit 0
      2) 目标落后         -> 打印「落后 N 个文件」+ 逐行文件名（缺失 / 内容不同），exit 2
      3) 目标比源多出文件 -> 单独列一组「目标多余」，不阻塞，不改变退出码

.PARAMETER Check
    干跑模式（默认行为）。只读，不写任何文件。不加任何参数即等同于 -Check。

.PARAMETER Apply
    先打印「将被覆盖/新增的文件清单」，再把 源 -> 目标 逐个复制，
    复制完成后重新计算哈希并打印「同步后：一致」。
    只有全部一致才 exit 0，否则 exit 1。
    与 -Check 同时给出时以 -Apply 为准。

.PARAMETER Source
    源目录（公开仓内核源码）。
    默认：D:/程序树/00-程序树/工具/csharp/src

.PARAMETER Target
    目标目录（公司仓内核源码）。
    默认：D:/AI Company/00-程序树/工具/csharp/src

.EXAMPLE
    powershell -NoProfile -File sync-kernel.ps1
    干跑：落后则逐行列出文件并 exit 2。

.EXAMPLE
    powershell -NoProfile -File sync-kernel.ps1 -Apply
    同步 公开 -> 公司，同步后校验一致则 exit 0。

.OUTPUTS
    中文文本（UTF-8）。兼容 Windows PowerShell 5.1 与 PowerShell 7+。

.NOTES
    退出码语义：
      0 = 一致（-Check）或 -Apply 复制后校验一致
      1 = 运行错误（目录不存在等），或 -Apply 复制后仍不一致
      2 = 目标落后（存在缺失或内容不同的文件），需要同步
#>
[CmdletBinding()]
param(
    [switch]$Check,
    [switch]$Apply,
    [string]$Source = 'D:/程序树/00-程序树/工具/csharp/src',
    [string]$Target = 'D:/AI Company/00-程序树/工具/csharp/src'
)

$ErrorActionPreference = 'Stop'

# 统一按 UTF-8 输出中文（重定向时也生效）
try {
    $utf8 = New-Object System.Text.UTF8Encoding $false
    [Console]::OutputEncoding = $utf8
    $OutputEncoding = $utf8
} catch { }

function Get-RelativePathSafe {
    param([string]$Base, [string]$Full)
    $m = [System.IO.Path].GetMethod('GetRelativePath', [type[]]@([string], [string]))
    if ($null -ne $m) {
        return [System.IO.Path]::GetRelativePath($Base, $Full)
    }
    # Windows PowerShell 5.1 无 Path.GetRelativePath，用 Uri 兜底
    $baseFull = [System.IO.Path]::GetFullPath($Base).TrimEnd('\') + '\'
    $baseUri = New-Object System.Uri($baseFull)
    $fullUri = New-Object System.Uri([System.IO.Path]::GetFullPath($Full))
    return [System.Uri]::UnescapeDataString($baseUri.MakeRelativeUri($fullUri).ToString()).Replace('/', '\')
}

function Get-KernelFileMap {
    param([string]$Base)
    if (-not (Test-Path -LiteralPath $Base -PathType Container)) {
        throw "目录不存在：$Base"
    }
    $map = @{}
    $files = Get-ChildItem -LiteralPath $Base -Recurse -File -Filter *.cs |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
    foreach ($f in $files) {
        $rel = Get-RelativePathSafe -Base $Base -Full $f.FullName
        $map[$rel] = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash
    }
    return $map
}

function Compare-KernelMap {
    param($SourceMap, $TargetMap)
    $missing = @()
    $different = @()
    $extra = @()
    foreach ($k in @($SourceMap.Keys | Sort-Object)) {
        if (-not $TargetMap.ContainsKey($k)) { $missing += $k }
        elseif ($TargetMap[$k] -ne $SourceMap[$k]) { $different += $k }
    }
    foreach ($k in @($TargetMap.Keys | Sort-Object)) {
        if (-not $SourceMap.ContainsKey($k)) { $extra += $k }
    }
    return [pscustomobject]@{ Missing = $missing; Different = $different; Extra = $extra }
}

try {
    $srcMap = Get-KernelFileMap -Base $Source
    $tgtMap = Get-KernelFileMap -Base $Target
} catch {
    Write-Output ("错误：{0}" -f $_.Exception.Message)
    exit 1
}

$cmp = Compare-KernelMap -SourceMap $srcMap -TargetMap $tgtMap
$pending = @($cmp.Missing) + @($cmp.Different)
$pendingCount = $pending.Count

Write-Output ("源  ：{0}" -f $Source)
Write-Output ("目标：{0}" -f $Target)

if ($Apply) {
    if ($pendingCount -eq 0) {
        Write-Output ("一致（{0} 个文件），无需同步。" -f $srcMap.Count)
    } else {
        Write-Output ("将被覆盖/新增的文件清单：{0} 个" -f $pendingCount)
        foreach ($k in @($cmp.Missing)) { Write-Output ("  [新增] {0}" -f $k) }
        foreach ($k in @($cmp.Different)) { Write-Output ("  [覆盖] {0}" -f $k) }
        foreach ($k in $pending) {
            $from = Join-Path $Source $k
            $to = Join-Path $Target $k
            $dir = Split-Path -Parent $to
            if (-not (Test-Path -LiteralPath $dir -PathType Container)) {
                New-Item -ItemType Directory -Path $dir -Force | Out-Null
            }
            Copy-Item -LiteralPath $from -Destination $to -Force
        }
        Write-Output "复制完成，重新计算哈希……"
    }

    $srcAfter = Get-KernelFileMap -Base $Source
    $tgtAfter = Get-KernelFileMap -Base $Target
    $cmpAfter = Compare-KernelMap -SourceMap $srcAfter -TargetMap $tgtAfter

    if (@($cmpAfter.Extra).Count -gt 0) {
        Write-Output ("目标多余（不阻塞）：{0} 个" -f @($cmpAfter.Extra).Count)
        foreach ($k in @($cmpAfter.Extra)) { Write-Output ("  多余     {0}" -f $k) }
    }

    if ((@($cmpAfter.Missing).Count + @($cmpAfter.Different).Count) -eq 0) {
        Write-Output ("同步后：一致（{0} 个文件）" -f $srcAfter.Count)
        exit 0
    }

    Write-Output ("同步后：仍不一致（{0} 个文件）" -f (@($cmpAfter.Missing).Count + @($cmpAfter.Different).Count))
    foreach ($k in @($cmpAfter.Missing)) { Write-Output ("  缺失     {0}" -f $k) }
    foreach ($k in @($cmpAfter.Different)) { Write-Output ("  内容不同 {0}" -f $k) }
    exit 1
}

# -Check / 默认：干跑，只读
if ($pendingCount -eq 0) {
    Write-Output ("一致（{0} 个文件）" -f $srcMap.Count)
} else {
    Write-Output ("落后 {0} 个文件" -f $pendingCount)
    foreach ($k in @($cmp.Missing)) { Write-Output ("  缺失     {0}" -f $k) }
    foreach ($k in @($cmp.Different)) { Write-Output ("  内容不同 {0}" -f $k) }
}

if (@($cmp.Extra).Count -gt 0) {
    Write-Output ("目标多余（不阻塞）：{0} 个" -f @($cmp.Extra).Count)
    foreach ($k in @($cmp.Extra)) { Write-Output ("  多余     {0}" -f $k) }
}

if ($pendingCount -eq 0) { exit 0 } else { exit 2 }
