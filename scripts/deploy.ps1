<#
.SYNOPSIS
    Packs the backlog tool and installs it, with its skill, for the current user.

.DESCRIPTION
    One artifact carries both halves: the nupkg holds the binary and the Claude Code skill it
    embeds. This packs it, replaces the globally installed tool with it, and unpacks the skill
    into ~/.claude/skills. The nupkg it leaves in artifacts/ is the thing to hand to someone else.

    Every run stamps a unique version. Without one NuGet would serve the cached 1.0.0 from
    ~/.nuget/packages and quietly install the *previous* build: the failure this script exists to
    make impossible, since the whole point is to run what you just changed.

    Kept to ASCII on purpose. Windows PowerShell 5.1 reads a BOM-less .ps1 as the ANSI codepage,
    and a UTF-8 em dash decodes there into a typographic quote, which PowerShell then treats as a
    string delimiter. The script does not parse at all, and the error names the wrong line.

.PARAMETER Version
    Ship a real version instead of a dev stamp. Use this for the nupkg other people install.

.PARAMETER SkipTests
    Skip the test run. For iterating on packaging itself, not for anything you hand to someone.

.EXAMPLE
    ./scripts/deploy.ps1
    Test, pack a dev build, and make it the tool and skill on this machine.

.EXAMPLE
    ./scripts/deploy.ps1 -Version 1.2.0
    The same, but leaves artifacts/Noogen.Backlog.Cli.1.2.0.nupkg to distribute.
#>
[CmdletBinding()]
param(
    [string] $Version,
    [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'

$repository = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repository 'src/Noogen.Backlog.slnx'
$project = Join-Path $repository 'src/Noogen.Backlog.Cli/Noogen.Backlog.Cli.csproj'
$artifacts = Join-Path $repository 'artifacts'
$package = 'Noogen.Backlog.Cli'

function Invoke-Step {
    param([string] $Description, [scriptblock] $Action)

    Write-Host "==> $Description" -ForegroundColor Cyan
    & $Action

    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

if (-not $Version) {
    $Version = '1.0.0-dev.' + (Get-Date -Format 'yyyyMMddHHmmss')
}

Write-Host "Deploying $package $Version" -ForegroundColor Green

# The OAuth client and the skill are both baked in at build time, so say which this build got.
# A tool packed without the client still works, but its users have to configure one themselves.
$oauth = Join-Path $repository 'oauth.json'
if (Test-Path $oauth) {
    Write-Host "    OAuth client: embedding $oauth"
} else {
    Write-Host "    OAuth client: none at $oauth. Whoever installs this must supply their own." -ForegroundColor Yellow
}

if (-not $SkipTests) {
    Invoke-Step 'dotnet test' { dotnet test $solution --configuration Release -p:Version=$Version --verbosity quiet }
} else {
    Write-Host '==> skipping tests' -ForegroundColor Yellow
    Invoke-Step 'dotnet build' { dotnet build $solution --configuration Release -p:Version=$Version --verbosity quiet }
}

Invoke-Step 'dotnet pack' { dotnet pack $project --configuration Release --no-build -p:Version=$Version --verbosity quiet }

$nupkg = Join-Path $artifacts "$package.$Version.nupkg"
if (-not (Test-Path $nupkg)) {
    throw "Expected $nupkg but dotnet pack did not produce it."
}

# Uninstall rather than update: `dotnet tool update` compares versions, and a dev stamp is only
# ever newer by luck.
#
# Ask first rather than uninstalling and ignoring the failure. On the first-install path there is
# nothing to remove, and Windows PowerShell turns a native command's stderr into a terminating
# error under ErrorActionPreference = Stop, so "not installed" would abort the deploy.
Write-Host "==> replacing the global tool" -ForegroundColor Cyan
$installed = dotnet tool list --global | Select-String -SimpleMatch $package
if ($installed) {
    Invoke-Step 'dotnet tool uninstall' { dotnet tool uninstall --global $package }
}

Invoke-Step 'dotnet tool install' {
    dotnet tool install --global $package --version $Version --add-source $artifacts --ignore-failed-sources
}

# Prefer the tool just installed over whatever PATH resolves. A shell opened before the very
# first `dotnet tool install` has no ~/.dotnet/tools on its PATH, so resolving by name alone would
# fail on exactly the run that matters most.
$tools = Join-Path $HOME '.dotnet/tools'
$backlog = Join-Path $tools 'backlog.exe'

if (-not (Test-Path $backlog)) {
    $backlog = Join-Path $tools 'backlog'
}

if (-not (Test-Path $backlog)) {
    $backlog = 'backlog'
}

Invoke-Step 'backlog install-skill' { & $backlog install-skill --force }

# Dev stamps accumulate; a version someone asked for is one to keep.
Get-ChildItem $artifacts -Filter "$package.*-dev.*.nupkg" |
    Where-Object { $_.FullName -ne $nupkg } |
    Remove-Item -Force

Write-Host ''
Write-Host "Deployed. $(Split-Path -Leaf $nupkg) is in artifacts/ for anyone else:" -ForegroundColor Green
Write-Host "    dotnet tool install --global $package --version $Version --add-source <folder>"
Write-Host "    backlog install-skill"
