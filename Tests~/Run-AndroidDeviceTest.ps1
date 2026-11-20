param(
    [Parameter(Mandatory=$true)][string]$Device,
    [Parameter(Mandatory=$true)][string]$Apk,
    [Parameter(Mandatory=$true)][string]$LogDirectory,
    [string]$Adb = 'C:\Program Files\Unity\Hub\Editor\6000.3.13f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe'
)
$ErrorActionPreference = 'Stop'
$packageName = 'com.yun.aegisguard.probe'
$activity = $packageName + '/com.unity3d.player.UnityPlayerGameActivity'
$state = (& $Adb -s $Device get-state 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $state -notmatch '(?m)^device\r?$') { throw 'The selected device must be connected and USB-debugging authorized.' }
$logRoot = [IO.Path]::GetFullPath($LogDirectory)
New-Item -ItemType Directory -Force -Path $logRoot | Out-Null
$installOutput = & $Adb -s $Device install --user 0 -r ([IO.Path]::GetFullPath($Apk)) 2>&1
$installOutput | Set-Content -LiteralPath (Join-Path $logRoot 'install.txt')
if ($LASTEXITCODE -ne 0) { throw 'Test APK installation failed. See install.txt.' }
$identity = & $Adb -s $Device shell pm list packages -U --user 0 $packageName
$uidMatch = [regex]::Match(($identity | Out-String), '(?m)^package:' + [regex]::Escape($packageName) + ' uid:(\d+)\r?$')
if (!$uidMatch.Success) { throw 'Could not resolve exact test app UID.' }
$appUid = $uidMatch.Groups[1].Value
$log = Join-Path $logRoot 'phone.log'
$capture = Start-Process -FilePath $Adb -ArgumentList @('-s',$Device,'logcat',('--uid='+$appUid),'-v','threadtime','-T','1') -WindowStyle Hidden -PassThru -RedirectStandardOutput $log -RedirectStandardError (Join-Path $logRoot 'logcat-error.txt')
try {
    & $Adb -s $Device shell am force-stop --user 0 $packageName
    & $Adb -s $Device shell am start -W --user 0 -n $activity | Set-Content -LiteralPath (Join-Path $logRoot 'launch.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Test activity launch failed.' }
    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    $passed = $false
    while ([DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 500
        $text = if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log -Raw } else { '' }
        if ($text -match 'AEGIS_PLAYER_FAIL|AEGIS_NATIVE_METADATA_REJECTED') { throw 'Device test failed. See phone.log.' }
        # Two independent markers prevent a single old logcat tail record from passing the test.
        if ($text -match 'AEGIS_PLAYER_PASS' -and $text -match 'AEGIS_PROTECTED_VISUALS_PASS') { $passed = $true; break }
    }
    if (!$passed) { throw 'Device test timed out. See phone.log.' }
    $deviceModel = (& $Adb -s $Device shell getprop ro.product.model | Out-String).Trim()
    $androidVersion = (& $Adb -s $Device shell getprop ro.build.version.release | Out-String).Trim()
    @{ passed=$true; model=$deviceModel; android=$androidVersion; apk_sha256=(Get-FileHash -LiteralPath $Apk -Algorithm SHA256).Hash; app_uid=$appUid; test_package=$packageName } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $logRoot 'result.json')
    Write-Output "AEGIS_ANDROID_DEVICE_PASS model=$deviceModel Android=$androidVersion"
}
finally {
    $capture.Refresh()
    if (!$capture.HasExited) { Stop-Process -Id $capture.Id }
}
