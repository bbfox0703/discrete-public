<#
.SYNOPSIS
    Convert scripts/README.md → dist/README.html with GitHub-style CSS.

.DESCRIPTION
    Build-time converter so end users who unzip dist/ get a polished
    HTML file they can double-click (no Markdown reader required).
    The source of truth stays scripts/README.md — repo readers see
    proper Markdown rendering on GitHub, end users get HTML.

    Uses PowerShell 7's ConvertFrom-Markdown (wraps Markdig). On
    Windows PowerShell 5.1 this script bails — pwsh is required.
    build.ps1 spawns pwsh to invoke this so the rest of the build can
    run under either 5.1 or 7.

.PARAMETER InputPath
    Markdown source. Default: <repo>/scripts/README.md.

.PARAMETER OutputPath
    HTML output. Default: <repo>/dist/README.html.
#>
#Requires -Version 7.0

[CmdletBinding()]
param(
    [string]$InputPath  = '',
    [string]$OutputPath = ''
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrEmpty($InputPath))  { $InputPath  = Join-Path $repoRoot 'scripts/README.md' }
if ([string]::IsNullOrEmpty($OutputPath)) { $OutputPath = Join-Path $repoRoot 'dist/README.html' }

if (-not (Test-Path $InputPath)) {
    throw "Input markdown not found: $InputPath"
}

# Ensure output directory exists.
$outDir = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrEmpty($outDir) -and -not (Test-Path $outDir)) {
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
}

$md = Get-Content -Raw -Path $InputPath
$body = (ConvertFrom-Markdown -InputObject $md).Html
$title = "Discrete — CE Lua Bridge"

# Light-theme CSS — mimics GitHub-flavored Markdown's stable look.
# Inline so the .html is fully self-contained (no external requests,
# nothing breaks if the user is offline).
$css = @'
:root {
  color-scheme: light dark;
  --fg: #1f2328;
  --fg-muted: #59636e;
  --bg: #ffffff;
  --bg-alt: #f6f8fa;
  --border: #d1d9e0;
  --link: #0969da;
  --code-bg: rgba(175,184,193,0.2);
  --pre-bg: #f6f8fa;
  --table-stripe: #f6f8fa;
}
@media (prefers-color-scheme: dark) {
  :root {
    --fg: #e6edf3;
    --fg-muted: #9198a1;
    --bg: #0d1117;
    --bg-alt: #161b22;
    --border: #3d444d;
    --link: #4493f8;
    --code-bg: rgba(101,108,118,0.4);
    --pre-bg: #161b22;
    --table-stripe: #161b22;
  }
}
* { box-sizing: border-box; }
body {
  font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", "Noto Sans",
               Helvetica, Arial, sans-serif, "Apple Color Emoji", "Segoe UI Emoji";
  font-size: 16px;
  line-height: 1.5;
  color: var(--fg);
  background: var(--bg);
  margin: 0;
  padding: 32px 16px;
}
.markdown-body {
  max-width: 980px;
  margin: 0 auto;
}
h1, h2, h3, h4, h5, h6 {
  margin-top: 24px;
  margin-bottom: 16px;
  font-weight: 600;
  line-height: 1.25;
}
h1 { font-size: 2em;    padding-bottom: 0.3em; border-bottom: 1px solid var(--border); }
h2 { font-size: 1.5em;  padding-bottom: 0.3em; border-bottom: 1px solid var(--border); }
h3 { font-size: 1.25em; }
h4 { font-size: 1em; }
p, ul, ol, table, pre, blockquote { margin-top: 0; margin-bottom: 16px; }
a { color: var(--link); text-decoration: none; }
a:hover { text-decoration: underline; }
code {
  font-family: ui-monospace, SFMono-Regular, "SF Mono", Menlo, Consolas, monospace;
  font-size: 85%;
  padding: 0.2em 0.4em;
  background: var(--code-bg);
  border-radius: 6px;
  white-space: break-spaces;
}
pre {
  font-family: ui-monospace, SFMono-Regular, "SF Mono", Menlo, Consolas, monospace;
  font-size: 85%;
  line-height: 1.45;
  padding: 16px;
  background: var(--pre-bg);
  border-radius: 6px;
  overflow: auto;
}
pre code {
  display: block;
  padding: 0;
  background: transparent;
  border-radius: 0;
  white-space: pre;
}
ul, ol { padding-left: 2em; }
li + li { margin-top: 0.25em; }
blockquote {
  padding: 0 1em;
  color: var(--fg-muted);
  border-left: 0.25em solid var(--border);
}
table {
  border-collapse: collapse;
  display: block;
  width: max-content;
  max-width: 100%;
  overflow: auto;
}
table th, table td {
  padding: 6px 13px;
  border: 1px solid var(--border);
}
table th { font-weight: 600; background: var(--bg-alt); }
table tr:nth-child(2n) td { background: var(--table-stripe); }
hr {
  height: 0.25em;
  margin: 24px 0;
  background: var(--border);
  border: 0;
}
img { max-width: 100%; }
'@

$buildStamp = Get-Date -Format 'yyyy-MM-dd HH:mm K'

$html = @"
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="generator" content="Discrete build pipeline; source: scripts/README.md">
<meta name="build-stamp" content="$buildStamp">
<title>$title</title>
<style>
$css
</style>
</head>
<body>
<article class="markdown-body">
$body
</article>
</body>
</html>
"@

Set-Content -Path $OutputPath -Value $html -Encoding UTF8
Write-Host "Wrote $OutputPath  ($($html.Length) bytes)" -ForegroundColor Green
