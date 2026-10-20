#Requires -Version 7
<#
.SYNOPSIS
    Verifier for the ai-smells PowerShell helpers: git output encoding, literal include
    pathspecs and the single overflow notice. Throws on the first failed check.
.DESCRIPTION
    Builds throwaway git repositories under the temp directory and removes only the unique
    directory it created, after printing it. Run it through safe-test-run (-Mode Verify).
#>
$ErrorActionPreference = 'Stop'
$aiSmells = Join-Path $PSScriptRoot '..\ai-smells'
. (Join-Path $aiSmells 'detect.ps1')
. (Join-Path $aiSmells 'pr-scope.ps1')
$failures = [System.Collections.Generic.List[string]]::new()
function Check([string] $Name, [bool] $Ok, [string] $Detail) {
    if (-not $Ok) { Write-Host "FAIL $Name :: $Detail"; $failures.Add("$(($Name -split ' ')[0]):$($Detail.Substring(0, [Math]::Min(22, $Detail.Length)))") }
}

$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$run = Join-Path $tempRoot ('ai-smells-verify-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run | Out-Null

function New-Repo([string] $Name) {
    $r = Join-Path $run $Name
    New-Item -ItemType Directory -Path $r | Out-Null
    git -C $r init -q -b main
    git -C $r config user.email t@example.com
    git -C $r config user.name t
    git -C $r config commit.gpgsign false
    git -C $r config core.autocrlf false
    return $r
}

try {
    # F7: a non-ASCII path under a legacy console code page.
    $r7 = New-Repo 'f7'
    Set-Content -LiteralPath (Join-Path $r7 'a.cs') -Value 'class A {}'
    git -C $r7 add -A
    git -C $r7 commit -q -m base
    git -C $r7 checkout -q -b feature
    New-Item -ItemType Directory -Path (Join-Path $r7 'src') | Out-Null
    $want = "src/caf$([char]0xE9).cs"
    [IO.File]::WriteAllText((Join-Path $r7 $want), "class B {}`n")
    git -C $r7 add -A
    git -C $r7 commit -q -m feat
    $saved = [Console]::OutputEncoding
    try {
        [Console]::OutputEncoding = [Text.Encoding]::GetEncoding(437)
        $info = Get-DiffLineInfo -RepoPath $r7 -Base main -Head feature
        $files = @(Get-ScanFiles -TreePath $r7 -Pathspec @($want))
    }
    finally { [Console]::OutputEncoding = $saved }
    Check 'F7 diff key survives a legacy console code page' ($info.Added.ContainsKey($want)) "keys: $(@($info.Added.Keys) -join ', ')"
    Check 'F7 ls-files lists the non-ASCII path' ($files -contains $want) "files: $($files -join ', ')"
    Check 'F7 console encoding is restored' ([Console]::OutputEncoding.CodePage -eq $saved.CodePage) "now $([Console]::OutputEncoding.CodePage)"

    # F5: an include that git would read as pathspec magic must match literally.
    $r5 = New-Repo 'f5'
    $blob = ('class C {}' | git -C $r5 hash-object -w --stdin)
    # Windows cannot hold the file on disk, so the name goes straight into the index and the
    # raw `git ls-files` output is captured before Get-ScanFiles' on-disk filters run.
    git -C $r5 -c core.protectNTFS=false update-index --add --cacheinfo "100644,$blob,:magic.cs"
    $global:lsFilesRaw = [System.Collections.Generic.List[string]]::new()
    function global:git {
        $out = & git.exe @args
        if ($args -contains 'ls-files') { foreach ($o in @($out)) { $global:lsFilesRaw.Add([string]$o) } }
        $out
    }
    try { $null = Get-ScanFiles -TreePath $r5 -Pathspec @(':magic.cs') }
    finally { Remove-Item Function:\git }
    Check 'F5 a leading-colon file name is listed, not parsed as pathspec magic' ((($global:lsFilesRaw -join '') -split "`0") -contains ':magic.cs') "raw: $($global:lsFilesRaw -join '|')"

    # C2: the overflow notice is posted once, then edited.
    $hits = 1..3 | ForEach-Object { [ordered]@{ smell = 'S99'; name = 'n'; file = "f$_.cs"; line = $_; lineText = "x$_"; context = ''; text = "x$_" } }
    $hitsPath = Join-Path $run 'hits.json'
    [ordered]@{ hits = $hits; coverage = @() } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $hitsPath
    $global:ghCalls = [System.Collections.Generic.List[string]]::new()
    $global:existingOverflow = $false
    function global:gh {
        $global:ghCalls.Add(($args -join ' '))
        $global:LASTEXITCODE = 0
        $joined = $args -join ' '
        if ($joined -match 'graphql') { return @() }
        if ($joined -match 'issues/\d+/comments' -and $joined -notmatch 'PATCH|POST') {
            if ($global:existingOverflow) { return '12345' } else { return @() }
        }
    }
    $post = Join-Path $aiSmells 'post-smell-threads.ps1'
    & $post -HitsPath $hitsPath -Repo o/r -Pr 7 -HeadSha abc -Cap 1
    $first = @($global:ghCalls | Where-Object { $_ -match 'pr comment|PATCH' })
    Check 'C2 first overflow is posted once' ($first.Count -eq 1 -and $first[0] -match 'pr comment') ($first -join ' | ')
    $global:ghCalls.Clear(); $global:existingOverflow = $true
    & $post -HitsPath $hitsPath -Repo o/r -Pr 7 -HeadSha abc -Cap 1
    $second = @($global:ghCalls | Where-Object { $_ -match 'pr comment|PATCH' })
    Check 'C2 second push edits the notice instead of posting another' ($second.Count -eq 1 -and $second[0] -match 'PATCH' -and $second[0] -match '12345') ($second -join ' | ')
    Remove-Item Function:\gh
}
finally {
    # Delete only the unique directory this run created, under the temp root.
    if ($run.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and (Split-Path -Leaf $run) -like 'ai-smells-verify-*') {
        Remove-Item -LiteralPath $run -Recurse -Force -ErrorAction SilentlyContinue
    }
}
if ($failures.Count) { Write-Host "$($failures.Count) check(s) failed"; exit 1 }
Write-Host 'all checks passed'
