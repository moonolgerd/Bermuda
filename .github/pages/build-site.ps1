# Assembles the GitHub Pages site for a release: the landing page, the .appinstaller and the
# public signing certificate. The MSIX itself stays on the GitHub Release (it is ~85 MB and the
# .appinstaller points at it), so Pages only ever holds the small, always-latest files.
#
#   pwsh .github/pages/build-site.ps1 -OutDir site -SourceDir artifacts/msix -Version 1.2.3.4 `
#        -AppInstallerUrl https://owner.github.io/repo/Bermuda.appinstaller `
#        -MsixUrl https://github.com/owner/repo/releases/download/v1.2.3.4/Bermuda_1.2.3.4_x64.msix `
#        -ReleaseUrl https://github.com/owner/repo/releases/tag/v1.2.3.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $OutDir,
    [Parameter(Mandatory)] [string] $SourceDir,
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $AppInstallerUrl,
    [Parameter(Mandatory)] [string] $MsixUrl,
    [Parameter(Mandatory)] [string] $ReleaseUrl
)

$ErrorActionPreference = 'Stop'

$appInstaller = Join-Path $SourceDir 'Bermuda.appinstaller'
if (-not (Test-Path $appInstaller)) { throw "Bermuda.appinstaller not found in $SourceDir" }

if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
New-Item -ItemType Directory $OutDir | Out-Null
Copy-Item $appInstaller $OutDir

# The cert only exists when the release was signed. Without it there is nothing to trust,
# so the page simply drops that step instead of linking to a missing file.
$certStep = ''
$cer = Join-Path $SourceDir 'Bermuda.cer'
if (Test-Path $cer) {
    Copy-Item $cer $OutDir
    $certUrl = $AppInstallerUrl -replace 'Bermuda\.appinstaller$', 'Bermuda.cer'
    $certStep = @"
    <li>
      <strong>Trust the signing certificate (once per PC).</strong><br>
      <a class="button" href="$certUrl">Download Bermuda.cer</a>
      <p>Then run this in an <em>administrator</em> PowerShell, from the folder you downloaded it to:</p>
      <pre><code>Import-Certificate -FilePath .\Bermuda.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople</code></pre>
      <small>Bermuda is signed with its own certificate rather than one from a public authority, so Windows has to be told to trust it.</small>
    </li>
"@
}

$html = Get-Content (Join-Path $PSScriptRoot 'index.html.template') -Raw
$html = $html.Replace('@CERT_STEP@', $certStep).
    Replace('@VERSION@', [System.Net.WebUtility]::HtmlEncode($Version)).
    Replace('@APPINSTALLER_URL@', [System.Net.WebUtility]::HtmlEncode($AppInstallerUrl)).
    Replace('@MSIX_URL@', [System.Net.WebUtility]::HtmlEncode($MsixUrl)).
    Replace('@RELEASE_URL@', [System.Net.WebUtility]::HtmlEncode($ReleaseUrl))
Set-Content (Join-Path $OutDir 'index.html') $html -Encoding utf8 -NoNewline

Get-ChildItem $OutDir | ForEach-Object { "{0}  {1} bytes" -f $_.Name, $_.Length }
