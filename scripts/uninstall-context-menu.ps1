[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$shellKey = 'Registry::HKEY_CURRENT_USER\Software\Classes\SystemFileAssociations\image\shell\ImageViewerAutoscale'
$progIdKey = 'Registry::HKEY_CURRENT_USER\Software\Classes\ImageViewerAutoscale.Image'
$appKey = 'Registry::HKEY_CURRENT_USER\Software\Classes\Applications\ImageViewerAutoscale.exe'
$capabilitiesKey = 'Registry::HKEY_CURRENT_USER\Software\ImageViewerAutoscale'
$supportedExtensions = @('.png', '.jpg', '.jpeg', '.webp', '.bmp', '.gif', '.tif', '.tiff')

if (Test-Path -LiteralPath $shellKey) {
    Remove-Item -LiteralPath $shellKey -Recurse -Force
    Write-Output 'Removed Image Viewer Autoscale context menu.'
} else {
    Write-Output 'Context menu entry was not installed.'
}

foreach ($key in @($progIdKey, $appKey, $capabilitiesKey)) {
    if (Test-Path -LiteralPath $key) {
        Remove-Item -LiteralPath $key -Recurse -Force
    }
}

$registeredApplicationsKey = 'Registry::HKEY_CURRENT_USER\Software\RegisteredApplications'
if (Test-Path -LiteralPath $registeredApplicationsKey) {
    Remove-ItemProperty -Path $registeredApplicationsKey -Name 'Image Viewer Autoscale' -ErrorAction SilentlyContinue
}

foreach ($extension in $supportedExtensions) {
    $extensionKey = "Registry::HKEY_CURRENT_USER\Software\Classes\$extension"
    $openWithProgIdsKey = Join-Path $extensionKey 'OpenWithProgids'
    if (Test-Path -LiteralPath $openWithProgIdsKey) {
        Remove-ItemProperty -Path $openWithProgIdsKey -Name 'ImageViewerAutoscale.Image' -ErrorAction SilentlyContinue
    }

    if ((Test-Path -LiteralPath $extensionKey) -and ((Get-Item -LiteralPath $extensionKey).GetValue('') -eq 'ImageViewerAutoscale.Image')) {
        Set-Item -Path $extensionKey -Value ''
    }
}

Write-Output 'Removed Image Viewer Autoscale app association registration.'
