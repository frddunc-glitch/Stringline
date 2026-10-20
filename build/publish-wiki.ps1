<#
.SYNOPSIS
    Copies the user guide (docs/user-guide) into the GitHub wiki (S28, #4). The repository is the source: the wiki is
    never edited by hand, and each run replaces its pages.

.DESCRIPTION
    The guide's pages become wiki pages: README.md is Home, links to other pages lose their ".md" (the wiki's own page
    links), and the images go in the wiki's images folder. A _Sidebar lists the pages in the guide's order and a
    _Footer says where to edit them.

    -Out <folder>   writes the wiki's files into that folder and stops (no git; for a look, and for the tests).
    -Push           clones the wiki, writes the pages, and commits and pushes any change. The token comes from
                    GITHUB_TOKEN and is passed to git as a header, never in a URL or the log. If the wiki has no first
                    page yet (GitHub makes its repository only then), it warns and stops without failing.

.EXAMPLE
    ./build/publish-wiki.ps1 -Out wiki-preview
.EXAMPLE
    $env:GITHUB_TOKEN = '…'; ./build/publish-wiki.ps1 -Push
#>
[CmdletBinding(DefaultParameterSetName = 'Out')]
param(
    [Parameter(ParameterSetName = 'Out', Mandatory)]
    [string]$Out,

    [Parameter(ParameterSetName = 'Push', Mandatory)]
    [switch]$Push,

    [Parameter(ParameterSetName = 'Push')]
    [string]$Repository = $(if ($env:GITHUB_REPOSITORY) { $env:GITHUB_REPOSITORY } else { 'frddunc-glitch/Stringline' })
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$guide = Join-Path $root 'docs/user-guide'

# The wiki page a guide page becomes.
function Get-WikiName([string]$file) {
    $name = [System.IO.Path]::GetFileNameWithoutExtension($file)
    if ($name -eq 'README') { 'Home' } else { $name }
}

# A page's Markdown with its links to other pages as wiki links: "re-planning.md#grips" -> "re-planning#grips".
function Convert-Links([string]$text) {
    [regex]::Replace($text, '\]\((?<page>[^)\s#:]+)\.md(?<heading>#[^)\s]*)?\)', {
        param($m)
        '](' + (Get-WikiName $m.Groups['page'].Value) + $m.Groups['heading'].Value + ')'
    })
}

# The pages in the order the contents (README.md) links to them.
function Get-Pages {
    $readme = Get-Content -Raw -Encoding utf8 (Join-Path $guide 'README.md')
    $order = [System.Collections.Generic.List[string]]::new()
    foreach ($m in [regex]::Matches($readme, '\]\((?<page>[^)\s#:]+\.md)')) {
        $page = $m.Groups['page'].Value
        if (-not $order.Contains($page) -and (Test-Path (Join-Path $guide $page))) { $order.Add($page) }
    }
    $order
}

function Write-Wiki([string]$folder) {
    New-Item -ItemType Directory -Force $folder | Out-Null
    # Replace what the last run wrote; keep git's own folder.
    Get-ChildItem $folder -Force | Where-Object Name -ne '.git' | Remove-Item -Recurse -Force

    $utf8 = [System.Text.UTF8Encoding]::new($false)
    foreach ($page in Get-ChildItem $guide -Filter '*.md') {
        $text = Convert-Links (Get-Content -Raw -Encoding utf8 $page.FullName)
        [System.IO.File]::WriteAllText((Join-Path $folder ((Get-WikiName $page.Name) + '.md')), $text, $utf8)
    }

    $images = Join-Path $guide 'images'
    if (Test-Path $images) {
        Copy-Item -Recurse $images (Join-Path $folder 'images')
    }

    $sidebar = @('**[Stringline user guide](Home)**', '')
    foreach ($page in Get-Pages) {
        $title = (Get-Content -Encoding utf8 (Join-Path $guide $page) | Where-Object { $_ -like '# *' } | Select-Object -First 1).Substring(2).Trim()
        $sidebar += "- [$title]($(Get-WikiName $page))"
    }
    [System.IO.File]::WriteAllText((Join-Path $folder '_Sidebar.md'), ($sidebar -join "`n") + "`n", $utf8)
    [System.IO.File]::WriteAllText(
        (Join-Path $folder '_Footer.md'),
        "This wiki is made from [docs/user-guide](https://github.com/$Repository/tree/main/docs/user-guide) by ``build/publish-wiki.ps1``. Edit the guide there: changes made here are replaced.`n",
        $utf8)
}

if (-not $Push) {
    $Repository = 'frddunc-glitch/Stringline'
    Write-Wiki $Out
    Write-Host "Wrote the wiki's pages to $Out"
    return
}

if (-not $env:GITHUB_TOKEN) { throw 'Set GITHUB_TOKEN to a token that may push to the repository.' }
$auth = 'AUTHORIZATION: basic ' + [Convert]::ToBase64String([System.Text.Encoding]::ASCII.GetBytes("x-access-token:$($env:GITHUB_TOKEN)"))
$url = "https://github.com/$Repository.wiki.git"
$clone = Join-Path ([System.IO.Path]::GetTempPath()) "stringline-wiki-$PID"
if (Test-Path $clone) { Remove-Item -Recurse -Force $clone }

& git -c "http.extraheader=$auth" clone --quiet --depth 1 $url $clone 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Warning "Could not clone $url. Turn on the wiki (Settings ▸ Features ▸ Wikis) and create its first page; GitHub makes the wiki's repository only then. Not publishing."
    return
}

try {
    Write-Wiki $clone
    & git -C $clone add --all
    & git -C $clone diff --cached --quiet
    if ($LASTEXITCODE -eq 0) {
        Write-Host 'The wiki is already up to date.'
        return
    }

    $commit = if ($env:GITHUB_SHA) { $env:GITHUB_SHA.Substring(0, 7) } else { (& git -C $root rev-parse --short HEAD) }
    & git -C $clone -c user.name='github-actions[bot]' -c user.email='41898282+github-actions[bot]@users.noreply.github.com' commit --quiet -m "User guide from $commit"
    if ($LASTEXITCODE -ne 0) { throw 'git commit failed' }
    & git -C $clone -c "http.extraheader=$auth" push --quiet origin HEAD 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not push to $url" }
    Write-Host "Published the user guide to the wiki ($commit)."
}
finally {
    Remove-Item -Recurse -Force $clone -ErrorAction SilentlyContinue
}
