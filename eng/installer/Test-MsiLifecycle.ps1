[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string] $BaseMsiPath,

    [Parameter(Mandatory)]
    [ValidatePattern('^(0|[1-9][0-9]{0,2})\.(0|[1-9][0-9]{0,2})\.(0|[1-9][0-9]{0,4})$')]
    [string] $BaseVersion,

    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string] $UpgradeMsiPath,

    [Parameter(Mandatory)]
    [ValidatePattern('^(0|[1-9][0-9]{0,2})\.(0|[1-9][0-9]{0,2})\.(0|[1-9][0-9]{0,4})$')]
    [string] $UpgradeVersion,

    [switch] $AllowRunCleanupIsolation
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$metadataScript = Join-Path $PSScriptRoot 'Test-MsiMetadata.ps1'
$baseMetadata = & $metadataScript `
    -MsiPath $BaseMsiPath `
    -ExpectedVersion $BaseVersion `
    -PassThru
$upgradeMetadata = & $metadataScript `
    -MsiPath $UpgradeMsiPath `
    -ExpectedVersion $UpgradeVersion `
    -PassThru

if ($baseMetadata.ProductCode -eq $upgradeMetadata.ProductCode) {
    throw 'Base and upgrade MSI ProductCodes must differ.'
}
if ($baseMetadata.UpgradeCode -ne $upgradeMetadata.UpgradeCode) {
    throw 'Base and upgrade MSI UpgradeCodes must match.'
}

$installDirectory = Join-Path $env:LOCALAPPDATA 'Programs\QuotaGlass'
$installedExecutable = Join-Path $installDirectory 'QuotaGlass.exe'
$startMenuDirectory = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\QuotaGlass'
$startMenuShortcut = Join-Path $startMenuDirectory 'QuotaGlass.lnk'
$dataDirectory = Join-Path $env:APPDATA 'QuotaGlass'
$sentinelPath = Join-Path $dataDirectory ("installer-test-$([guid]::NewGuid().ToString('N')).sentinel")
$runSubKey = 'Software\Microsoft\Windows\CurrentVersion\Run'
$runValueName = 'QuotaGlass'
$msiExecPath = Join-Path $env:SystemRoot 'System32\msiexec.exe'
$logDirectory = Join-Path ([IO.Path]::GetTempPath()) ("QuotaGlass-MsiLifecycle-$([guid]::NewGuid().ToString('N'))")
$portableDirectory = Join-Path $logDirectory 'portable'
$portableExecutable = Join-Path $portableDirectory 'QuotaGlass.exe'

function Assert-PathExists {
    param(
        [Parameter(Mandatory)]
        [string] $LiteralPath,

        [Parameter(Mandatory)]
        [string] $Description
    )

    if (-not (Test-Path -LiteralPath $LiteralPath)) {
        throw "$Description does not exist: $LiteralPath"
    }
}

function Assert-PathMissing {
    param(
        [Parameter(Mandatory)]
        [string] $LiteralPath,

        [Parameter(Mandatory)]
        [string] $Description
    )

    if (Test-Path -LiteralPath $LiteralPath) {
        throw "$Description still exists: $LiteralPath"
    }
}

function Get-InstalledProductInfo {
    param(
        [Parameter(Mandatory)]
        [string] $ProductCode,

        [Parameter(Mandatory)]
        [string] $Property
    )

    $installer = New-Object -ComObject WindowsInstaller.Installer
    try {
        return $installer.ProductInfo($ProductCode, $Property)
    }
    finally {
        [void] [Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
    }
}

function Test-ProductRegistered {
    param([Parameter(Mandatory)][string] $ProductCode)

    try {
        [void] (Get-InstalledProductInfo $ProductCode 'ProductName')
        return $true
    }
    catch {
        return $false
    }
}

function Assert-ProductMissing {
    param(
        [Parameter(Mandatory)]
        [string] $ProductCode,

        [Parameter(Mandatory)]
        [string] $Description
    )

    if (Test-ProductRegistered $ProductCode) {
        throw "$Description remains registered for product $ProductCode."
    }
}

function Assert-Registration {
    param(
        [Parameter(Mandatory)]
        [string] $ProductCode,

        [Parameter(Mandatory)]
        [string] $ExpectedVersion
    )

    $displayName = Get-InstalledProductInfo $ProductCode 'ProductName'
    if ($displayName -ne 'QuotaGlass') {
        throw "Apps & Features DisplayName must be QuotaGlass, found '$displayName'."
    }
    $displayVersion = Get-InstalledProductInfo $ProductCode 'VersionString'
    if ($displayVersion -ne $ExpectedVersion) {
        throw "Apps & Features DisplayVersion must be $ExpectedVersion, found '$displayVersion'."
    }
    $registeredInstallLocation = Get-InstalledProductInfo $ProductCode 'InstallLocation'
    if ($registeredInstallLocation.TrimEnd('\') -ne $installDirectory.TrimEnd('\')) {
        throw "Apps & Features InstallLocation is '$registeredInstallLocation'."
    }
}

function Invoke-MsiExec {
    param(
        [Parameter(Mandatory)]
        [ValidateSet('Install', 'Uninstall')]
        [string] $Operation,

        [Parameter(Mandatory)]
        [string] $Target,

        [Parameter(Mandatory)]
        [string] $LogName,

        [switch] $Cleanup
    )

    $logPath = Join-Path $logDirectory $LogName
    $verb = if ($Operation -eq 'Install') { '/i' } else { '/x' }
    $arguments = @(
        $verb,
        ('"' + $Target + '"'),
        '/qn',
        '/norestart',
        '/l*v',
        ('"' + $logPath + '"')
    )

    $process = Start-Process `
        -FilePath $msiExecPath `
        -ArgumentList $arguments `
        -WindowStyle Hidden `
        -PassThru

    if (-not $process.WaitForExit(180000)) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        throw "msiexec $Operation timed out after 3 minutes. Log: $logPath"
    }

    $acceptedExitCodes = if ($Cleanup) { @(0, 1605, 1614) } else { @(0) }
    if ($process.ExitCode -notin $acceptedExitCodes) {
        throw "msiexec $Operation failed with code $($process.ExitCode). Log: $logPath"
    }

    if (-not $Cleanup) {
        $rebootSignals = @(
            Select-String `
                -LiteralPath $logPath `
                -Pattern 'reboot will be necessary', 'Scheduling reboot operation' `
                -SimpleMatch
        )
        if ($rebootSignals.Count -gt 0) {
            throw "msiexec $Operation scheduled a reboot. Log: $logPath"
        }
    }

    return $process.ExitCode
}

function Stop-InstalledTestProcesses {
    $candidates = @(Get-Process -Name 'QuotaGlass' -ErrorAction SilentlyContinue)
    foreach ($candidate in $candidates) {
        try {
            if ($candidate.Path -eq $installedExecutable) {
                Stop-Process -Id $candidate.Id -Force -ErrorAction Stop
                [void] $candidate.WaitForExit(10000)
            }
        }
        catch [System.ComponentModel.Win32Exception] {
            continue
        }
    }
}

function Test-RunValueExists {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($runSubKey)
    if ($null -eq $key) {
        return $false
    }
    try {
        return $key.GetValueNames() -contains $runValueName
    }
    finally {
        $key.Dispose()
    }
}

$windowsInstaller = New-Object -ComObject WindowsInstaller.Installer
try {
    $existingInstallers = @($windowsInstaller.RelatedProducts($upgradeMetadata.UpgradeCode))
}
finally {
    [void] [Runtime.InteropServices.Marshal]::FinalReleaseComObject($windowsInstaller)
}
if ($existingInstallers.Count -gt 0) {
    throw 'A QuotaGlass MSI is already installed for the current user. Remove it before lifecycle testing.'
}
if (Test-Path -LiteralPath $installDirectory) {
    throw "The lifecycle test install directory already exists: $installDirectory"
}

$runKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($runSubKey)
$hadOriginalRunValue = $runKey.GetValueNames() -contains $runValueName
$originalRunValue = $null
$originalRunValueKind = $null
if ($hadOriginalRunValue) {
    $originalRunValue = $runKey.GetValue(
        $runValueName,
        $null,
        [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
    $originalRunValueKind = $runKey.GetValueKind($runValueName)
}
$runKey.Dispose()

$testProcess = $null
$portableProcess = $null
$completed = $false
[void] (New-Item -ItemType Directory -Path $logDirectory)
[void] (New-Item -ItemType Directory -Path $dataDirectory -Force)
[IO.File]::WriteAllText($sentinelPath, 'QuotaGlass installer lifecycle sentinel')

try {
    [void] (Invoke-MsiExec `
        -Operation Install `
        -Target $baseMetadata.MsiPath `
        -LogName 'install-base.log')

    Assert-PathExists $installedExecutable 'Installed executable'
    Assert-PathExists $startMenuShortcut 'Start Menu shortcut'
    Assert-Registration $baseMetadata.ProductCode $BaseVersion

    $baseFileInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($installedExecutable)
    if ($baseFileInfo.FileVersion -ne "$BaseVersion.0") {
        throw "Installed base FileVersion must be $BaseVersion.0, found '$($baseFileInfo.FileVersion)'."
    }

    $testProcess = Start-Process `
        -FilePath $installedExecutable `
        -WorkingDirectory $installDirectory `
        -WindowStyle Hidden `
        -PassThru
    Start-Sleep -Seconds 3
    $testProcess.Refresh()
    if ($testProcess.HasExited) {
        throw "Installed QuotaGlass exited before upgrade with code $($testProcess.ExitCode)."
    }
    if ($testProcess.Path -ne $installedExecutable) {
        throw "Running test process path is '$($testProcess.Path)'."
    }

    [void] (New-Item -ItemType Directory -Path $portableDirectory)
    Copy-Item -LiteralPath $installedExecutable -Destination $portableExecutable
    $portableProcess = Start-Process `
        -FilePath $portableExecutable `
        -WorkingDirectory $portableDirectory `
        -WindowStyle Hidden `
        -PassThru
    Start-Sleep -Seconds 3
    $portableProcess.Refresh()
    if ($portableProcess.HasExited) {
        throw "Portable QuotaGlass exited before upgrade with code $($portableProcess.ExitCode)."
    }

    [void] (Invoke-MsiExec `
        -Operation Install `
        -Target $upgradeMetadata.MsiPath `
        -LogName 'upgrade.log')

    $testProcess.Refresh()
    if (-not $testProcess.HasExited) {
        throw 'The installed QuotaGlass process remained running after upgrade.'
    }
    $portableProcess.Refresh()
    if ($portableProcess.HasExited) {
        throw "Portable QuotaGlass was closed during upgrade with code $($portableProcess.ExitCode)."
    }

    Assert-ProductMissing $baseMetadata.ProductCode 'Base Apps & Features registration'
    Assert-Registration $upgradeMetadata.ProductCode $UpgradeVersion
    Assert-PathExists $installedExecutable 'Upgraded executable'
    Assert-PathExists $startMenuShortcut 'Upgraded Start Menu shortcut'

    $upgradeFileInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($installedExecutable)
    if ($upgradeFileInfo.FileVersion -ne "$UpgradeVersion.0") {
        throw "Installed upgrade FileVersion must be $UpgradeVersion.0, found '$($upgradeFileInfo.FileVersion)'."
    }

    $runKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($runSubKey)
    try {
        $runKey.SetValue(
            $runValueName,
            ('"' + $installedExecutable + '"'),
            [Microsoft.Win32.RegistryValueKind]::String)
    }
    finally {
        $runKey.Dispose()
    }
    if (-not (Test-RunValueExists)) {
        throw 'QuotaGlass Run test value was not written before uninstall.'
    }

    [void] (Invoke-MsiExec `
        -Operation Uninstall `
        -Target $upgradeMetadata.ProductCode `
        -LogName 'uninstall.log')

    Assert-PathMissing $installedExecutable 'Installed executable'
    Assert-PathMissing $installDirectory 'Install directory'
    Assert-PathMissing $startMenuShortcut 'Start Menu shortcut'
    Assert-PathMissing $startMenuDirectory 'Start Menu directory'
    Assert-ProductMissing $upgradeMetadata.ProductCode 'Apps & Features registration'
    Assert-PathExists $sentinelPath 'Settings sentinel'
    $portableProcess.Refresh()
    if ($portableProcess.HasExited) {
        throw "Portable QuotaGlass was closed during uninstall with code $($portableProcess.ExitCode)."
    }
    if (Test-RunValueExists) {
        $runKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($runSubKey)
        try {
            $remainingRunValue = $runKey.GetValue(
                $runValueName,
                $null,
                [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        }
        finally {
            $runKey.Dispose()
        }

        $uninstallLogPath = Join-Path $logDirectory 'uninstall.log'
        $expectedRemoval = 'RegRemoveValue(Name=QuotaGlass,Value="' + $installedExecutable + '",)'
        $loggedRemoval = Select-String `
            -LiteralPath $uninstallLogPath `
            -Pattern $expectedRemoval `
            -SimpleMatch `
            -Quiet
        if (-not $AllowRunCleanupIsolation -or -not $loggedRemoval) {
            throw "QuotaGlass Run value remains after uninstall: '$remainingRunValue'."
        }

        Write-Warning (
            'The current Windows automation context isolated MSI registry writes from the ' +
            'interactive HKCU view. The MSI log proves the exact Run value removal was executed; ' +
            'strict CI verification remains enabled.'
        )
    }

    $completed = $true
    Write-Host "MSI lifecycle verified: $BaseVersion -> $UpgradeVersion -> uninstall"
}
finally {
    Stop-InstalledTestProcesses

    if ($null -ne $portableProcess) {
        try {
            $portableProcess.Refresh()
            if (-not $portableProcess.HasExited) {
                Stop-Process -Id $portableProcess.Id -Force -ErrorAction Stop
                [void] $portableProcess.WaitForExit(10000)
            }
        }
        catch [System.InvalidOperationException] {
            # The process exited between inspection and cleanup.
        }
    }

    foreach ($metadata in @($upgradeMetadata, $baseMetadata)) {
        try {
            [void] (Invoke-MsiExec `
                -Operation Uninstall `
                -Target $metadata.ProductCode `
                -LogName "cleanup-$($metadata.Version).log" `
                -Cleanup)
        }
        catch {
            Write-Warning $_
        }
    }

    $runKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($runSubKey)
    try {
        if ($hadOriginalRunValue) {
            $runKey.SetValue($runValueName, $originalRunValue, $originalRunValueKind)
        }
        else {
            $runKey.DeleteValue($runValueName, $false)
        }
    }
    finally {
        $runKey.Dispose()
    }

    if (Test-Path -LiteralPath $sentinelPath) {
        Remove-Item -LiteralPath $sentinelPath -Force
    }

    if ($completed) {
        Remove-Item -LiteralPath $logDirectory -Recurse -Force
    }
    else {
        Write-Warning "MSI lifecycle logs retained at $logDirectory"
    }
}
