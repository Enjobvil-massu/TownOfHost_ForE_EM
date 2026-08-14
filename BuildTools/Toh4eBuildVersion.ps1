[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("Prepare", "Increment", "Set")]
    [string]$Action,

    [string]$GeneratedFile,
    [string]$StampFile,
    [string]$CounterFile,
    [string]$Version
)

$ErrorActionPreference = "Stop"
$Utf8NoBom = [System.Text.UTF8Encoding]::new($false)

function Get-CounterPath {
    if (-not [string]::IsNullOrWhiteSpace($CounterFile)) {
        return $CounterFile
    }

    if ([string]::IsNullOrWhiteSpace($env:LOCALAPPDATA)) {
        throw "LOCALAPPDATA is not available."
    }

    $StateDirectory = Join-Path $env:LOCALAPPDATA "TownOfHost_ForE"
    Join-Path $StateDirectory "build-version.txt"
}

function Write-AtomicText {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Text
    )

    $Directory = Split-Path -Parent $Path

    if (-not [string]::IsNullOrWhiteSpace($Directory)) {
        New-Item -ItemType Directory -Path $Directory -Force | Out-Null
    }

    $TemporaryPath = $Path + "." + [Guid]::NewGuid().ToString("N") + ".tmp"

    try {
        [System.IO.File]::WriteAllText($TemporaryPath, $Text, $Utf8NoBom)
        Move-Item -LiteralPath $TemporaryPath -Destination $Path -Force
    }
    finally {
        if (Test-Path -LiteralPath $TemporaryPath) {
            Remove-Item -LiteralPath $TemporaryPath -Force
        }
    }
}

function Read-BuildVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Build-version file was not found: $Path"
    }

    $Value = [System.IO.File]::ReadAllText($Path).Trim()

    if ($Value -notmatch '^([0-9]+)\.([0-9]+)$') {
        throw "Invalid build version: $Value"
    }

    $Value
}

function Get-NextBuildVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Current
    )

    if ($Current -notmatch '^([0-9]+)\.([0-9]+)$') {
        throw "Invalid build version: $Current"
    }

    $Major = [int]$Matches[1]
    $Revision = [int]$Matches[2]
    $Major.ToString() + "." + ($Revision + 1).ToString()
}

$ResolvedCounterFile = Get-CounterPath

switch ($Action) {
    "Prepare" {
        if ([string]::IsNullOrWhiteSpace($GeneratedFile)) {
            throw "GeneratedFile is required for Prepare."
        }

        if ([string]::IsNullOrWhiteSpace($StampFile)) {
            throw "StampFile is required for Prepare."
        }

        if (-not (Test-Path -LiteralPath $ResolvedCounterFile)) {
            Write-AtomicText -Path $ResolvedCounterFile -Text "600.0`r`n"
        }

        $Current = Read-BuildVersion -Path $ResolvedCounterFile

        $GeneratedSource =
            "namespace TownOfHostForE`r`n" +
            "{`r`n" +
            "    internal static class BuildVersionInfo`r`n" +
            "    {`r`n" +
            "        internal const string DisplayVersion = `"$Current`";`r`n" +
            "    }`r`n" +
            "}`r`n"

        Write-AtomicText -Path $GeneratedFile -Text $GeneratedSource
        Write-AtomicText -Path $StampFile -Text ($Current + "`r`n")

        Write-Host ("TOH4E display version for this build: v" + $Current)
        break
    }

    "Increment" {
        if ([string]::IsNullOrWhiteSpace($StampFile)) {
            throw "StampFile is required for Increment."
        }

        $UsedVersion = Read-BuildVersion -Path $StampFile
        $CurrentVersion = Read-BuildVersion -Path $ResolvedCounterFile
        $NextVersion = Get-NextBuildVersion -Current $UsedVersion

        if ($CurrentVersion -eq $UsedVersion) {
            Write-AtomicText -Path $ResolvedCounterFile -Text ($NextVersion + "`r`n")
            Write-Host ("TOH4E next display version: v" + $NextVersion)
        }
        elseif ($CurrentVersion -eq $NextVersion) {
            Write-Host ("TOH4E display version was already incremented: v" + $CurrentVersion)
        }
        else {
            throw (
                "Build-version counter mismatch. Used=" +
                $UsedVersion +
                " Current=" +
                $CurrentVersion
            )
        }

        break
    }

    "Set" {
        if ([string]::IsNullOrWhiteSpace($Version)) {
            throw "Version is required for Set."
        }

        if ($Version -notmatch '^[0-9]+\.[0-9]+$') {
            throw "Invalid build version: $Version"
        }

        Write-AtomicText -Path $ResolvedCounterFile -Text ($Version + "`r`n")
        Write-Host ("TOH4E display version was set to v" + $Version)
        break
    }
}