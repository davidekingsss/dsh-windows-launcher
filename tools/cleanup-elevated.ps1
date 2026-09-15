# Elevated cleanup for the DSH Desktop removal.
#
# Two leftovers need administrator rights or many individual reparse-point
# deletions:
#   1. C:\Program Files\DSH Desktop — an empty shell; the app itself is already
#      uninstalled, but Program Files needs elevation to delete.
#   2. 71 dangling junctions under ~\.dsh\profiles\node_modules that point into
#      that now-deleted app directory. They are dead pointers left behind by the
#      old desktop app's plugin store; the real packages live inside each
#      profile's own node_modules, so removing the links deletes no data.
#
# Progress is written to a sentinel file so the caller can read the result even
# when it cannot observe this process's console.

$sentinel = Join-Path $env:TEMP 'dsh-cleanup-result.txt'
$lines = New-Object System.Collections.Generic.List[string]

function Note([string]$text) {
    $lines.Add($text)
    Write-Host $text
}

Note "elevated cleanup started $(Get-Date -Format s)"
Note "isAdmin: $((New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))"

# ---- 1) the empty Program Files shell --------------------------------------
$appDir = 'C:\Program Files\DSH Desktop'
if (Test-Path $appDir) {
    $kids = @(Get-ChildItem $appDir -Force -Recurse -ErrorAction SilentlyContinue)
    Note "app dir children before: $($kids.Count)"
    try {
        Remove-Item $appDir -Recurse -Force -ErrorAction Stop
        Note "app dir: DELETED"
    } catch {
        Note "app dir: FAILED $($_.Exception.Message)"
    }
} else {
    Note "app dir: already gone"
}

# ---- 2) dangling junctions in the shared profile module store --------------
$store = Join-Path $env:USERPROFILE '.dsh\profiles\node_modules'
$removed = 0
$failed = 0
$kept = 0

function Sweep([string]$dir) {
    Get-ChildItem $dir -Force -ErrorAction SilentlyContinue | ForEach-Object {
        $item = $_
        if ($item.LinkType) {
            $target = $item.Target
            $targetOk = $false
            try { $targetOk = (Test-Path -LiteralPath $target) } catch { $targetOk = $false }
            if (-not $targetOk) {
                try {
                    # Remove the link itself, never the target's contents.
                    [System.IO.Directory]::Delete($item.FullName, $false)
                    $script:removed++
                } catch {
                    $script:failed++
                    Note "  could not remove $($item.FullName): $($_.Exception.Message)"
                }
            } else {
                $script:kept++
            }
        }
    }
}

if (Test-Path $store) {
    Sweep $store
    Get-ChildItem $store -Force -Directory -ErrorAction SilentlyContinue |
        Where-Object { -not $_.LinkType } |
        ForEach-Object { Sweep $_.FullName }
    Note "junctions: removed=$removed kept(live)=$kept failed=$failed"
} else {
    Note "module store: not present"
}

# ---- 3) verify -------------------------------------------------------------
$stillBroken = 0
if (Test-Path $store) {
    Get-ChildItem $store -Force -Recurse -Depth 1 -ErrorAction SilentlyContinue | ForEach-Object {
        if ($_.LinkType) {
            $ok = $false
            try { $ok = (Test-Path -LiteralPath $_.Target) } catch { $ok = $false }
            if (-not $ok) { $stillBroken++ }
        }
    }
}
Note "dangling junctions remaining: $stillBroken"
Note "app dir still present: $(Test-Path $appDir)"
Note "DONE"

[System.IO.File]::WriteAllLines($sentinel, $lines, [System.Text.UTF8Encoding]::new($false))
