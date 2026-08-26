BeforeAll {
    $scriptPath = Join-Path $PSScriptRoot '..\..\scripts\Prepare-Release.ps1'
    . $scriptPath
}

Describe 'Resolve-ReleaseVersion' {
    $validTags = @(
        @{ Tag = 'v0.1.0'; Expected = '0.1.0' }
        @{ Tag = 'v1.2.3'; Expected = '1.2.3' }
        @{ Tag = 'v10.20.30'; Expected = '10.20.30' }
    )

    It 'returns <Expected> for stable tag <Tag>' -ForEach $validTags {
        Resolve-ReleaseVersion -Tag $Tag | Should -BeExactly $Expected
    }

    $invalidTags = @(
        '1.2.3',
        'v1.2',
        'v01.2.3',
        'v1.02.3',
        'v1.2.03',
        'v1.2.3-alpha.1',
        'v1.2.3+build.7',
        'v1.2.3.4',
        'vx.y.z',
        ''
    )

    It 'rejects invalid release tag <_>' -ForEach $invalidTags {
        { Resolve-ReleaseVersion -Tag $_ } |
            Should -Throw -ExpectedMessage '*exact stable SemVer tag*'
    }
}

Describe 'Get-ReleaseNotes' {
    It 'extracts only the matching version section' {
        $path = Join-Path $TestDrive 'CHANGELOG.md'
        @'
# Changelog

## [Unreleased]

### Added

- Work in progress.

## [1.2.3] - 2026-08-26

### Added

- Versioned releases.

### Fixed

- Reliable checksums.

## [1.2.2] - 2026-08-20

### Fixed

- Older fix.

[Unreleased]: https://example.test/compare/v1.2.3...HEAD
[1.2.3]: https://example.test/compare/v1.2.2...v1.2.3
'@ | Set-Content -LiteralPath $path

        $notes = Get-ReleaseNotes `
            -ChangelogPath $path `
            -Version '1.2.3' `
            -ExpectedDate '2026-08-26'

        $notes | Should -BeExactly @'
### Added

- Versioned releases.

### Fixed

- Reliable checksums.
'@
        $notes | Should -Not -Match 'Older fix'
        $notes | Should -Not -Match '\[1\.2\.3\]:'
    }

    It 'rejects a missing version section' {
        $path = Join-Path $TestDrive 'missing.md'
        "# Changelog`n`n## [Unreleased]`n" | Set-Content -LiteralPath $path

        { Get-ReleaseNotes -ChangelogPath $path -Version '1.2.3' -ExpectedDate '2026-08-26' } |
            Should -Throw -ExpectedMessage '*exactly one*1.2.3*'
    }

    It 'rejects duplicate version sections' {
        $path = Join-Path $TestDrive 'duplicate.md'
        @'
# Changelog

## [1.2.3] - 2026-08-26

### Added

- First copy.

## [1.2.3] - 2026-08-25

### Fixed

- Second copy.
'@ | Set-Content -LiteralPath $path

        { Get-ReleaseNotes -ChangelogPath $path -Version '1.2.3' -ExpectedDate '2026-08-26' } |
            Should -Throw -ExpectedMessage '*exactly one*1.2.3*'
    }

    It 'rejects a section with a different release date' {
        $path = Join-Path $TestDrive 'wrong-date.md'
        @'
# Changelog

## [1.2.3] - 2026-08-25

### Added

- Versioned releases.
'@ | Set-Content -LiteralPath $path

        { Get-ReleaseNotes -ChangelogPath $path -Version '1.2.3' -ExpectedDate '2026-08-26' } |
            Should -Throw -ExpectedMessage '*1.2.3*must be dated 2026-08-26*'
    }

    It 'rejects a section without a changelog entry' {
        $path = Join-Path $TestDrive 'empty.md'
        @'
# Changelog

## [1.2.3] - 2026-08-26

### Added

## [1.2.2] - 2026-08-20

### Fixed

- Older fix.
'@ | Set-Content -LiteralPath $path

        { Get-ReleaseNotes -ChangelogPath $path -Version '1.2.3' -ExpectedDate '2026-08-26' } |
            Should -Throw -ExpectedMessage '*at least one changelog entry*'
    }
}

Describe 'New-ReleaseArtifacts' {
    It 'creates a versioned single-file archive and matching SHA-256 manifest' {
        $publishDirectory = Join-Path $TestDrive 'publish'
        $outputDirectory = Join-Path $TestDrive 'release'
        $null = New-Item -ItemType Directory -Path $publishDirectory
        $executablePath = Join-Path $publishDirectory 'QuotaGlass.exe'
        [IO.File]::WriteAllText($executablePath, 'published executable fixture')

        $artifacts = New-ReleaseArtifacts `
            -ExecutablePath $executablePath `
            -OutputDirectory $outputDirectory `
            -Tag 'v1.2.3'

        Split-Path -Leaf $artifacts.ArchivePath |
            Should -BeExactly 'QuotaGlass-v1.2.3-win-x64.zip'
        Split-Path -Leaf $artifacts.ChecksumPath |
            Should -BeExactly 'QuotaGlass-v1.2.3-win-x64.sha256'

        $archive = [IO.Compression.ZipFile]::OpenRead($artifacts.ArchivePath)
        try {
            $archive.Entries.Count | Should -Be 1
            $archive.Entries[0].FullName | Should -BeExactly 'QuotaGlass.exe'

            $reader = [IO.StreamReader]::new($archive.Entries[0].Open())
            try {
                $reader.ReadToEnd() | Should -BeExactly 'published executable fixture'
            }
            finally {
                $reader.Dispose()
            }
        }
        finally {
            $archive.Dispose()
        }

        $archiveBytes = [IO.File]::ReadAllBytes($artifacts.ArchivePath)
        $expectedHash = [Convert]::ToHexString(
            [Security.Cryptography.SHA256]::HashData($archiveBytes)).ToLowerInvariant()
        $expectedManifest = "$expectedHash  QuotaGlass-v1.2.3-win-x64.zip"

        (Get-Content -Raw -LiteralPath $artifacts.ChecksumPath).Trim() |
            Should -BeExactly $expectedManifest
    }
}

Describe 'Confirm-ReleaseBinaryVersion' {
    It 'rejects an executable whose product and file versions do not match the release' {
        $executablePath = (Get-Process -Id $PID).Path

        { Confirm-ReleaseBinaryVersion -ExecutablePath $executablePath -Version '99.98.97' } |
            Should -Throw -ExpectedMessage '*ProductVersion*99.98.97*FileVersion*99.98.97.0*'
    }
}

Describe 'release commit provenance' {
    BeforeEach {
        $repositoryPath = Join-Path $TestDrive ([guid]::NewGuid().ToString('N'))
        $null = New-Item -ItemType Directory -Path $repositoryPath
        git -C $repositoryPath init --initial-branch=master | Out-Null
        git -C $repositoryPath config user.email 'release-tests@example.invalid'
        git -C $repositoryPath config user.name 'Release Tests'

        [IO.File]::WriteAllText((Join-Path $repositoryPath 'tracked.txt'), 'master')
        git -C $repositoryPath add tracked.txt

        git -C $repositoryPath commit -m 'test: add master commit' | Out-Null

        $masterCommit = git -C $repositoryPath rev-parse HEAD
        git -C $repositoryPath switch -c feature 2>$null | Out-Null
        [IO.File]::WriteAllText((Join-Path $repositoryPath 'tracked.txt'), 'feature')
        git -C $repositoryPath add tracked.txt
        git -C $repositoryPath commit -m 'test: add feature commit' | Out-Null
        $featureCommit = git -C $repositoryPath rev-parse HEAD
    }

    It 'accepts a commit contained in the release branch' {
        { Confirm-ReleaseCommit `
                -RepositoryPath $repositoryPath `
                -Commit $masterCommit `
                -BranchRef master } | Should -Not -Throw
    }

    It 'rejects a commit outside the release branch' {
        { Confirm-ReleaseCommit `
                -RepositoryPath $repositoryPath `
                -Commit $featureCommit `
                -BranchRef master } |
            Should -Throw -ExpectedMessage '*not contained in*master*'
    }
}
