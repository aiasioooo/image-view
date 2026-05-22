[CmdletBinding()]
param(
    [string]$ExePath,
    [switch]$NoPublish,
    [switch]$MakeDefault
)

$ErrorActionPreference = 'Stop'

$projectRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$defaultPublishExe = Join-Path $projectRoot 'bin\Release\net8.0-windows\win-x64\publish\ImageViewerAutoscale.exe'

if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $ExePath = $defaultPublishExe
}

if (-not $NoPublish) {
    dotnet publish $projectRoot -c Release -r win-x64 --self-contained false
}

if (-not (Test-Path -LiteralPath $ExePath)) {
    throw "Executable not found: $ExePath. Run dotnet publish or pass -ExePath."
}

$resolvedExe = (Resolve-Path -LiteralPath $ExePath).Path
$progId = 'ImageViewerAutoscale.Image'
$supportedExtensions = @('.png', '.jpg', '.jpeg', '.webp', '.bmp', '.gif', '.tif', '.tiff')
$blockedUserChoiceExtensions = New-Object System.Collections.Generic.List[string]

$progIdKey = "Registry::HKEY_CURRENT_USER\Software\Classes\$progId"
$progIdIconKey = Join-Path $progIdKey 'DefaultIcon'
$progIdCommandKey = Join-Path $progIdKey 'shell\open\command'
$appKey = 'Registry::HKEY_CURRENT_USER\Software\Classes\Applications\ImageViewerAutoscale.exe'
$appCommandKey = Join-Path $appKey 'shell\open\command'
$capabilitiesKey = 'Registry::HKEY_CURRENT_USER\Software\ImageViewerAutoscale\Capabilities'
$fileAssociationsKey = Join-Path $capabilitiesKey 'FileAssociations'
$registeredApplicationsKey = 'Registry::HKEY_CURRENT_USER\Software\RegisteredApplications'

New-Item -Path $progIdKey -Force | Out-Null
New-Item -Path $progIdIconKey -Force | Out-Null
New-Item -Path $progIdCommandKey -Force | Out-Null
Set-Item -Path $progIdKey -Value 'Image Viewer Autoscale Image'
Set-ItemProperty -Path $progIdKey -Name 'FriendlyTypeName' -Value 'Image Viewer Autoscale Image'
Set-Item -Path $progIdIconKey -Value $resolvedExe
Set-Item -Path $progIdCommandKey -Value "`"$resolvedExe`" `"%1`""

New-Item -Path $appKey -Force | Out-Null
New-Item -Path $appCommandKey -Force | Out-Null
Set-ItemProperty -Path $appKey -Name 'FriendlyAppName' -Value 'Image Viewer Autoscale'
Set-Item -Path $appCommandKey -Value "`"$resolvedExe`" `"%1`""

New-Item -Path $capabilitiesKey -Force | Out-Null
New-Item -Path $fileAssociationsKey -Force | Out-Null
Set-ItemProperty -Path $capabilitiesKey -Name 'ApplicationName' -Value 'Image Viewer Autoscale'
Set-ItemProperty -Path $capabilitiesKey -Name 'ApplicationDescription' -Value 'Minimal image viewer with zoom and AI upscaling modes.'
foreach ($extension in $supportedExtensions) {
    Set-ItemProperty -Path $fileAssociationsKey -Name $extension -Value $progId
}
New-Item -Path $registeredApplicationsKey -Force | Out-Null
Set-ItemProperty -Path $registeredApplicationsKey -Name 'Image Viewer Autoscale' -Value 'Software\ImageViewerAutoscale\Capabilities'

foreach ($extension in $supportedExtensions) {
    $extensionKey = "Registry::HKEY_CURRENT_USER\Software\Classes\$extension"
    $openWithProgIdsKey = Join-Path $extensionKey 'OpenWithProgids'
    New-Item -Path $extensionKey -Force | Out-Null
    New-Item -Path $openWithProgIdsKey -Force | Out-Null
    New-ItemProperty -Path $openWithProgIdsKey -Name $progId -Value ([byte[]]@()) -PropertyType Binary -Force | Out-Null

    if ($MakeDefault) {
        Set-Item -Path $extensionKey -Value $progId

        $userChoiceKey = "Registry::HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\$extension\UserChoice"
        if (Test-Path -LiteralPath $userChoiceKey) {
            $regPath = "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\$extension\UserChoice"
            $previousErrorActionPreference = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            try {
                $regOutput = & reg.exe delete $regPath /f 2>&1
                $deleteExitCode = $LASTEXITCODE
            } catch {
                $deleteExitCode = 1
            } finally {
                $ErrorActionPreference = $previousErrorActionPreference
            }

            if ($deleteExitCode -ne 0) {
                $blockedUserChoiceExtensions.Add($extension)
            }
        }
    }
}

$shellKey = 'Registry::HKEY_CURRENT_USER\Software\Classes\SystemFileAssociations\image\shell\ImageViewerAutoscale'
$commandKey = Join-Path $shellKey 'command'

New-Item -Path $shellKey -Force | Out-Null
New-Item -Path $commandKey -Force | Out-Null

Set-Item -Path $shellKey -Value 'Open with Image Viewer Autoscale'
Set-ItemProperty -Path $shellKey -Name 'Icon' -Value $resolvedExe
Set-ItemProperty -Path $shellKey -Name 'MultiSelectModel' -Value 'Player'
Set-Item -Path $commandKey -Value "`"$resolvedExe`" `"%1`""

Write-Output "Installed image context menu for: $resolvedExe"
Write-Output 'Registered Image Viewer Autoscale as an image-file app.'
if ($MakeDefault) {
    Write-Output 'Set per-user defaults for common image extensions and reset protected UserChoice overrides.'
    if ($blockedUserChoiceExtensions.Count -gt 0) {
        Write-Output "Windows blocked protected UserChoice reset for: $($blockedUserChoiceExtensions -join ', ')"
        Write-Output 'Pick Image Viewer Autoscale once in Open with or Default Apps for those extensions.'
    }
}
Write-Output "If Explorer has cached the old menu, restart Explorer or sign out/in."
