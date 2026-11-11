# Reads the FDC3 App Directory (fdc3-appd.json) for the native side apps that ship in Bermuda's MSIX.
# Used by src/Bermuda/Bermuda.csproj (the CreateMsix and PublishNativeApps targets).
#
#   -Mode List   prints one line per native app: appId|exe|project   (project is an absolute path, or empty)
#   -Mode Patch  replaces the <!-- @NATIVE_APPS@ --> marker in -Manifest with one <Application> per native app
param(
    [Parameter(Mandatory)] [string] $Directory,
    [Parameter(Mandatory)] [ValidateSet('List', 'Patch')] [string] $Mode,
    [string] $Manifest
)
$ErrorActionPreference = 'Stop'

$dir = Split-Path -Parent (Resolve-Path $Directory)
$apps = (Get-Content -Raw -Path $Directory | ConvertFrom-Json).applications | Where-Object { $_.type -eq 'native' }

foreach ($app in $apps) {
    if (-not $app.details.path) { throw "Native app '$($app.appId)' in fdc3-appd.json needs details.path (its exe in the package)." }
}

if ($Mode -eq 'List') {
    foreach ($app in $apps) {
        $project = if ($app.details.project) { [System.IO.Path]::GetFullPath((Join-Path $dir $app.details.project)) } else { '' }
        "$($app.appId)|$($app.details.path)|$project"
    }
    return
}

if (-not $Manifest) { throw '-Manifest is required for -Mode Patch.' }
$esc = { param($v) [System.Security.SecurityElement]::Escape([string]$v) }
$xml = foreach ($app in $apps) {
    # Manifest Application ids are letters and digits only.
    $key = 'Native' + ($app.appId -replace '[^A-Za-z0-9]', '')
    $title = if ($app.title) { $app.title } else { $app.appId }
    $desc = if ($app.description) { $app.description } else { $title }
    @"

    <!-- Native FDC3 side app $(& $esc $app.appId) (from fdc3-appd.json): shipped in this package so it installs and
         updates with Bermuda. A background app with no Start menu entry; Bermuda starts and stops it. -->
    <Application Id="$key" Executable="$(& $esc $app.details.path)" EntryPoint="Windows.FullTrustApplication">
      <uap:VisualElements DisplayName="$(& $esc $title)" Description="$(& $esc $desc)" BackgroundColor="transparent"
        Square150x150Logo="Assets\Square150x150Logo.png" Square44x44Logo="Assets\Square44x44Logo.png" AppListEntry="none" />
    </Application>
"@
}
$marker = '<!-- @NATIVE_APPS@ -->'
$text = [System.IO.File]::ReadAllText($Manifest)
if (-not $text.Contains($marker)) { throw "The manifest has no $marker marker." }
[System.IO.File]::WriteAllText($Manifest, $text.Replace($marker, ($xml -join "`n")))
