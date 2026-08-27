[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string] $MsiPath,

    [Parameter(Mandatory)]
    [ValidatePattern('^(0|[1-9][0-9]{0,2})\.(0|[1-9][0-9]{0,2})\.(0|[1-9][0-9]{0,4})$')]
    [string] $ExpectedVersion,

    [switch] $PassThru
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$expectedUpgradeCode = '{A5EAB091-04C8-41D2-8F9E-2A0BFDC0D7E1}'
$runKey = 'Software\Microsoft\Windows\CurrentVersion\Run'

function Assert-Equal {
    param(
        [AllowNull()]
        [object] $Actual,

        [AllowNull()]
        [object] $Expected,

        [Parameter(Mandatory)]
        [string] $Description
    )

    if ($Actual -ne $Expected) {
        throw "$Description must be '$Expected', found '$Actual'."
    }
}

function Assert-True {
    param(
        [Parameter(Mandatory)]
        [bool] $Condition,

        [Parameter(Mandatory)]
        [string] $Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Get-MsiRows {
    param(
        [Parameter(Mandatory)]
        [object] $Database,

        [Parameter(Mandatory)]
        [string] $Query,

        [Parameter(Mandatory)]
        [string[]] $Columns
    )

    $view = $Database.OpenView($Query)
    $rows = [Collections.Generic.List[object]]::new()
    try {
        [void] $view.Execute()
        while ($record = $view.Fetch()) {
            try {
                $values = [ordered]@{}
                for ($index = 0; $index -lt $Columns.Count; $index++) {
                    $values[$Columns[$index]] = $record.StringData($index + 1)
                }
                $rows.Add([pscustomobject] $values)
            }
            finally {
                [void] [Runtime.InteropServices.Marshal]::FinalReleaseComObject($record)
            }
        }
    }
    finally {
        [void] [Runtime.InteropServices.Marshal]::FinalReleaseComObject($view)
    }

    return $rows.ToArray()
}

function Get-MsiProperty {
    param(
        [Parameter(Mandatory)]
        [object] $Database,

        [Parameter(Mandatory)]
        [string] $Name
    )

    $escapedName = $Name.Replace("'", "''", [StringComparison]::Ordinal)
    $rows = @(Get-MsiRows `
        -Database $Database `
        -Query "SELECT ``Value`` FROM ``Property`` WHERE ``Property`` = '$escapedName'" `
        -Columns @('Value'))

    if ($rows.Count -eq 0) {
        return $null
    }
    if ($rows.Count -ne 1) {
        throw "MSI property '$Name' occurs $($rows.Count) times."
    }
    return $rows[0].Value
}

$resolvedMsiPath = (Resolve-Path -LiteralPath $MsiPath).Path
$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $null
$summary = $null

try {
    $database = $installer.OpenDatabase($resolvedMsiPath, 0)

    Assert-Equal (Get-MsiProperty $database 'ProductName') 'QuotaGlass' 'ProductName'
    Assert-Equal (Get-MsiProperty $database 'ProductVersion') $ExpectedVersion 'ProductVersion'
    Assert-Equal (Get-MsiProperty $database 'Manufacturer') 'QuotaGlass' 'Manufacturer'
    Assert-Equal (Get-MsiProperty $database 'ProductLanguage') '1033' 'ProductLanguage'
    Assert-Equal (Get-MsiProperty $database 'UpgradeCode') $expectedUpgradeCode 'UpgradeCode'
    Assert-Equal (Get-MsiProperty $database 'ARPNOMODIFY') '1' 'ARPNOMODIFY'
    Assert-Equal `
        (Get-MsiProperty $database 'MSIRESTARTMANAGERCONTROL') `
        'Disable' `
        'MSIRESTARTMANAGERCONTROL'
    Assert-Equal `
        (Get-MsiProperty $database 'ARPURLINFOABOUT') `
        'https://github.com/medokin/QuotaGlass' `
        'ARPURLINFOABOUT'
    Assert-Equal (Get-MsiProperty $database 'ARPPRODUCTICON') 'QuotaGlassIcon' 'ARPPRODUCTICON'
    Assert-Equal (Get-MsiProperty $database 'ALLUSERS') $null 'ALLUSERS'

    $productCode = Get-MsiProperty $database 'ProductCode'
    Assert-True `
        ($productCode -match '^\{[0-9A-F]{8}(-[0-9A-F]{4}){3}-[0-9A-F]{12}\}$') `
        "ProductCode is not a braced uppercase GUID: '$productCode'."
    Assert-True `
        ($productCode -ne $expectedUpgradeCode) `
        'ProductCode must differ from the stable UpgradeCode.'

    $summary = $database.SummaryInformation(0)
    Assert-Equal $summary.Property(7) 'x64;1033' 'Summary template'

    $directories = @(Get-MsiRows $database `
        'SELECT `Directory`,`Directory_Parent`,`DefaultDir` FROM `Directory`' `
        @('Id', 'Parent', 'Name'))
    $installDirectory = @($directories | Where-Object Id -eq 'INSTALLFOLDER')
    Assert-Equal $installDirectory.Count 1 'INSTALLFOLDER row count'
    Assert-Equal $installDirectory[0].Parent 'ProgramsFolder' 'INSTALLFOLDER parent'
    Assert-True `
        ($installDirectory[0].Name -match '(^|\|)QuotaGlass$') `
        "INSTALLFOLDER name must resolve to QuotaGlass, found '$($installDirectory[0].Name)'."

    $programsDirectory = @($directories | Where-Object Id -eq 'ProgramsFolder')
    Assert-Equal $programsDirectory.Count 1 'ProgramsFolder row count'
    Assert-Equal $programsDirectory[0].Parent 'LocalAppDataFolder' 'ProgramsFolder parent'
    Assert-Equal $programsDirectory[0].Name 'Programs' 'ProgramsFolder name'

    $components = @(Get-MsiRows $database `
        'SELECT `Component`,`ComponentId`,`Directory_`,`Attributes`,`KeyPath` FROM `Component`' `
        @('Id', 'Guid', 'Directory', 'Attributes', 'KeyPath'))
    Assert-Equal $components.Count 1 'Component count'
    Assert-Equal $components[0].Id 'QuotaGlassApplication' 'Application component id'
    Assert-Equal $components[0].Directory 'INSTALLFOLDER' 'Application component directory'
    $componentAttributes = [int] $components[0].Attributes
    Assert-True `
        (($componentAttributes -band 0x100) -ne 0) `
        'Application component must carry the 64-bit component attribute.'
    Assert-True `
        (($componentAttributes -band 0x4) -ne 0) `
        'Application component must use a registry key path.'

    $files = @(Get-MsiRows $database `
        'SELECT `File`,`Component_`,`FileName` FROM `File`' `
        @('Id', 'Component', 'Name'))
    Assert-Equal $files.Count 1 'MSI payload file count'
    Assert-Equal $files[0].Id 'QuotaGlassExecutable' 'Payload file id'
    Assert-Equal $files[0].Component 'QuotaGlassApplication' 'Payload component'
    Assert-True `
        ($files[0].Name -match '(^|\|)QuotaGlass\.exe$') `
        "MSI payload must contain only QuotaGlass.exe, found '$($files[0].Name)'."

    $shortcuts = @(Get-MsiRows $database `
        'SELECT `Shortcut`,`Directory_`,`Name`,`Component_`,`Target`,`Icon_` FROM `Shortcut`' `
        @('Id', 'Directory', 'Name', 'Component', 'Target', 'Icon'))
    Assert-Equal $shortcuts.Count 1 'Shortcut count'
    Assert-Equal $shortcuts[0].Id 'QuotaGlassStartMenuShortcut' 'Shortcut id'
    Assert-Equal $shortcuts[0].Directory 'ApplicationProgramsFolder' 'Shortcut directory'
    Assert-Equal $shortcuts[0].Target '[#QuotaGlassExecutable]' 'Shortcut target'
    Assert-Equal $shortcuts[0].Icon 'QuotaGlassIcon' 'Shortcut icon'
    Assert-Equal @($shortcuts | Where-Object Directory -eq 'DesktopFolder').Count 0 'Desktop shortcut count'

    $registryRows = @(Get-MsiRows $database `
        'SELECT `Root`,`Key`,`Name`,`Value`,`Component_` FROM `Registry`' `
        @('Root', 'Key', 'Name', 'Value', 'Component'))
    Assert-Equal $registryRows.Count 1 'Registry row count'
    $installerMarker = @($registryRows | Where-Object Key -eq 'Software\QuotaGlass\Installer')
    Assert-Equal $installerMarker.Count 1 'Installer marker row count'
    Assert-Equal $installerMarker[0].Root '1' 'Installer marker root'
    Assert-Equal $installerMarker[0].Name 'InstallLocation' 'Installer marker value name'
    Assert-Equal $installerMarker[0].Value '[INSTALLFOLDER]' 'Installer marker value'

    Assert-Equal @($registryRows | Where-Object Key -eq $runKey).Count 0 'Authored Run value count'

    $upgrades = @(Get-MsiRows $database `
        'SELECT `UpgradeCode`,`VersionMin`,`VersionMax`,`Attributes`,`ActionProperty` FROM `Upgrade`' `
        @('UpgradeCode', 'VersionMin', 'VersionMax', 'Attributes', 'ActionProperty'))
    Assert-Equal $upgrades.Count 2 'Major upgrade row count'
    Assert-Equal @($upgrades | Where-Object UpgradeCode -ne $expectedUpgradeCode).Count 0 'Unexpected upgrade code count'
    Assert-Equal @($upgrades | Where-Object ActionProperty -eq 'WIX_UPGRADE_DETECTED').Count 1 'Upgrade detection row count'
    Assert-Equal @($upgrades | Where-Object ActionProperty -eq 'WIX_DOWNGRADE_DETECTED').Count 1 'Downgrade detection row count'

    $customActions = @(Get-MsiRows $database `
        'SELECT `Action`,`Type`,`Source`,`Target` FROM `CustomAction`' `
        @('Action', 'Type', 'Source', 'Target'))
    $closeAction = @($customActions | Where-Object Action -eq 'CloseRunningQuotaGlass')
    Assert-Equal $closeAction.Count 1 'Close application action count'
    Assert-Equal $closeAction[0].Source 'Wix4UtilCA_X64' 'Close application binary'
    Assert-Equal $closeAction[0].Target 'WixQuietExec' 'Close application entry point'

    $closeCommand = @($customActions | Where-Object Action -eq 'SetCloseRunningQuotaGlassCommand')
    Assert-Equal $closeCommand.Count 1 'Close application command count'
    Assert-True `
        ($closeCommand[0].Target -match 'taskkill\.exe" /F /IM "QuotaGlass\.exe"$') `
        "Close application command has an unexpected target: '$($closeCommand[0].Target)'."

    $cleanupAction = @($customActions | Where-Object Action -eq 'AddQuotaGlassAutostartCleanup')
    Assert-Equal $cleanupAction.Count 1 'Autostart cleanup action count'
    Assert-Equal $cleanupAction[0].Source 'QuotaGlassInstallerActions' 'Autostart cleanup binary'
    Assert-Equal $cleanupAction[0].Target 'AddQuotaGlassAutostartCleanup' 'Autostart cleanup entry point'
    Assert-True (([int] $cleanupAction[0].Type -band 0x400) -eq 0) 'Autostart cleanup must be immediate.'

    $sequences = @(Get-MsiRows $database `
        'SELECT `Action`,`Condition`,`Sequence` FROM `InstallExecuteSequence`' `
        @('Action', 'Condition', 'Sequence'))
    $closeSequence = @($sequences | Where-Object Action -eq 'CloseRunningQuotaGlass')
    $installValidateSequence = @($sequences | Where-Object Action -eq 'InstallValidate')
    Assert-Equal $closeSequence.Count 1 'Close application sequence count'
    Assert-Equal $closeSequence[0].Condition 'Installed OR WIX_UPGRADE_DETECTED' 'Close application condition'
    Assert-Equal $installValidateSequence.Count 1 'InstallValidate sequence count'
    Assert-True `
        ([int] $closeSequence[0].Sequence -lt [int] $installValidateSequence[0].Sequence) `
        'Close application action must run before InstallValidate checks locked files.'

    $cleanupSequence = @($sequences | Where-Object Action -eq 'AddQuotaGlassAutostartCleanup')
    $removeRegistrySequence = @($sequences | Where-Object Action -eq 'RemoveRegistryValues')
    Assert-Equal $cleanupSequence.Count 1 'Autostart cleanup sequence count'
    Assert-Equal $removeRegistrySequence.Count 1 'RemoveRegistryValues sequence count'
    Assert-Equal `
        $cleanupSequence[0].Condition `
        'REMOVE="ALL" AND NOT UPGRADINGPRODUCTCODE' `
        'Autostart cleanup condition'
    Assert-True `
        ([int] $cleanupSequence[0].Sequence -lt [int] $removeRegistrySequence[0].Sequence) `
        'Autostart cleanup row must be added before RemoveRegistryValues.'

    $removeFolders = @(Get-MsiRows $database `
        'SELECT `FileKey`,`DirProperty`,`InstallMode` FROM `RemoveFile`' `
        @('Id', 'Directory', 'InstallMode'))
    Assert-Equal $removeFolders.Count 3 'RemoveFolder row count'
    Assert-Equal @($removeFolders | Where-Object InstallMode -ne '2').Count 0 'Non-uninstall RemoveFolder row count'
    $actualRemoveDirectories = @($removeFolders.Directory | Sort-Object)
    $expectedRemoveDirectories = @('ApplicationProgramsFolder', 'INSTALLFOLDER', 'ProgramsFolder')
    Assert-Equal ($actualRemoveDirectories -join ',') ($expectedRemoveDirectories -join ',') 'RemoveFolder directory set'

    $result = [pscustomobject] @{
        MsiPath = $resolvedMsiPath
        ProductCode = $productCode
        UpgradeCode = $expectedUpgradeCode
        Version = $ExpectedVersion
    }

    if ($PassThru) {
        $result
    }
    else {
        Write-Host "MSI metadata verified: $resolvedMsiPath"
    }
}
finally {
    if ($null -ne $summary) {
        [void] [Runtime.InteropServices.Marshal]::FinalReleaseComObject($summary)
    }
    if ($null -ne $database) {
        [void] [Runtime.InteropServices.Marshal]::FinalReleaseComObject($database)
    }
    [void] [Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
}
