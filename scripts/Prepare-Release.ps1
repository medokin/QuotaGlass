Set-StrictMode -Version Latest

function Resolve-ReleaseVersion {
    [CmdletBinding()]
    param(
        [AllowEmptyString()]
        [Parameter(Mandatory)]
        [string] $Tag
    )

    if ($Tag -notmatch '^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') {
        throw "Release tag '$Tag' must be an exact stable SemVer tag in the form vMAJOR.MINOR.PATCH."
    }

    return $Tag.Substring(1)
}

function Get-ReleaseNotes {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $ChangelogPath,

        [Parameter(Mandatory)]
        [string] $Version,

        [Parameter(Mandatory)]
        [ValidatePattern('^\d{4}-\d{2}-\d{2}$')]
        [string] $ExpectedDate
    )

    $content = Get-Content -Raw -LiteralPath $ChangelogPath -ErrorAction Stop
    $escapedVersion = [regex]::Escape($Version)
    $headingPattern = '^## \[' + $escapedVersion + '\] - (\d{4}-\d{2}-\d{2})\r?$'
    $headings = [regex]::Matches(
        $content,
        $headingPattern,
        [Text.RegularExpressions.RegexOptions]::Multiline)

    if ($headings.Count -ne 1) {
        throw "CHANGELOG.md must contain exactly one release section for version $Version."
    }

    $heading = $headings[0]
    $actualDate = $heading.Groups[1].Value
    if ($actualDate -ne $ExpectedDate) {
        throw "The CHANGELOG.md section for version $Version must be dated $ExpectedDate, not $actualDate."
    }

    $sectionStart = $heading.Index + $heading.Length
    $remaining = $content.Substring($sectionStart)
    $boundaries = @(
        @(
            [regex]::Match(
                $remaining,
                '^## \[',
                [Text.RegularExpressions.RegexOptions]::Multiline)
            [regex]::Match(
                $remaining,
                '^\[[^\]\r\n]+\]:\s+\S',
                [Text.RegularExpressions.RegexOptions]::Multiline)
        ) | Where-Object Success | Sort-Object Index
    )

    $sectionLength = if ($boundaries.Count -gt 0) {
        $boundaries[0].Index
    }
    else {
        $remaining.Length
    }

    $notes = $remaining.Substring(0, $sectionLength).Trim()
    if ($notes -notmatch '(?m)^- \S') {
        throw "The CHANGELOG.md section for version $Version must contain at least one changelog entry."
    }

    return $notes
}

function New-ReleaseArtifacts {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $ExecutablePath,

        [Parameter(Mandatory)]
        [string] $OutputDirectory,

        [Parameter(Mandatory)]
        [string] $Tag
    )

    $null = Resolve-ReleaseVersion -Tag $Tag

    if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) {
        throw "Published executable not found at '$ExecutablePath'."
    }

    if ((Split-Path -Leaf $ExecutablePath) -ne 'QuotaGlass.exe') {
        throw "Published executable must be named QuotaGlass.exe."
    }

    $null = New-Item -ItemType Directory -Path $OutputDirectory -Force
    $archiveName = "QuotaGlass-$Tag-win-x64.zip"
    $checksumName = "QuotaGlass-$Tag-win-x64.sha256"
    $archivePath = Join-Path $OutputDirectory $archiveName
    $checksumPath = Join-Path $OutputDirectory $checksumName

    if ((Test-Path -LiteralPath $archivePath) -or (Test-Path -LiteralPath $checksumPath)) {
        throw "Release artifacts already exist for tag $Tag."
    }

    $archive = [IO.Compression.ZipFile]::Open(
        $archivePath,
        [IO.Compression.ZipArchiveMode]::Create)
    try {
        $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $archive,
            $ExecutablePath,
            'QuotaGlass.exe',
            [IO.Compression.CompressionLevel]::Optimal)
    }
    finally {
        $archive.Dispose()
    }

    $hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $manifest = "$hash  $archiveName`n"
    [IO.File]::WriteAllText(
        $checksumPath,
        $manifest,
        [Text.UTF8Encoding]::new($false))

    return [pscustomobject]@{
        ArchivePath = $archivePath
        ChecksumPath = $checksumPath
    }
}

function Confirm-ReleaseBinaryVersion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $ExecutablePath,

        [Parameter(Mandatory)]
        [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')]
        [string] $Version
    )

    if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) {
        throw "Published executable not found at '$ExecutablePath'."
    }

    $versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($ExecutablePath)
    $expectedFileVersion = "$Version.0"
    if ($versionInfo.ProductVersion -ne $Version -or
        $versionInfo.FileVersion -ne $expectedFileVersion) {
        throw (
            "Binary version mismatch. ProductVersion must be $Version but is " +
            "'$($versionInfo.ProductVersion)'; FileVersion must be $expectedFileVersion but is " +
            "'$($versionInfo.FileVersion)'.")
    }
}

function Confirm-ReleaseCommit {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string] $RepositoryPath,

        [Parameter(Mandatory)]
        [string] $Commit,

        [Parameter(Mandatory)]
        [string] $BranchRef
    )

    $PSNativeCommandUseErrorActionPreference = $false

    $null = & git -C $RepositoryPath cat-file -e "$Commit^{commit}" 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "Release commit '$Commit' does not exist in '$RepositoryPath'."
    }

    $null = & git -C $RepositoryPath cat-file -e "$BranchRef^{commit}" 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "Release branch ref '$BranchRef' does not exist in '$RepositoryPath'."
    }

    $null = & git -C $RepositoryPath merge-base --is-ancestor $Commit $BranchRef 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "Release commit '$Commit' is not contained in '$BranchRef'."
    }
}
