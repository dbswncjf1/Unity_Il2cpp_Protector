param(
    [string]$Unity = 'C:\Program Files\Unity\Hub\Editor\6000.3.13f1\Editor\Unity.exe',
    [Parameter(Mandatory=$true)][string]$Project,
    [ValidateSet('Win64','Android')][string]$Target = 'Win64'
)
$ErrorActionPreference = 'Stop'
$packageRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$validationRoot = [IO.Path]::GetFullPath($Project)
if (Test-Path -LiteralPath $validationRoot) { throw 'Choose a NEW validation directory; this script never overwrites an existing project.' }
foreach ($folder in @('Assets/Editor','Packages','ProjectSettings','GuardSource')) {
    New-Item -ItemType Directory -Force -Path (Join-Path $validationRoot $folder) | Out-Null
}
$manifest = @{ dependencies = @{
    'com.yun.aegisguard' = ('file:' + $packageRoot.Replace('\','/'))
    'com.unity.modules.assetbundle' = '1.0.0'
    'com.unity.modules.unitywebrequest' = '1.0.0'
    'com.unity.modules.unitywebrequestassetbundle' = '1.0.0'
    'com.unity.modules.jsonserialize' = '1.0.0'
} }
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $validationRoot 'Packages/manifest.json')
'm_EditorVersion: 6000.3.13f1' | Set-Content -LiteralPath (Join-Path $validationRoot 'ProjectSettings/ProjectVersion.txt')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'UnityFixture/PlayerProbe.cs') -Destination (Join-Path $validationRoot 'Assets/PlayerProbe.cs')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'UnityFixture/Editor/Validation.cs') -Destination (Join-Path $validationRoot 'Assets/Editor/Validation.cs')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'UnityFixture/Editor/WeaverChecks.cs') -Destination (Join-Path $validationRoot 'Assets/Editor/WeaverChecks.cs')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'UnityFixture/Editor/ResourceFixtureInjector.cs') -Destination (Join-Path $validationRoot 'Assets/Editor/ResourceFixtureInjector.cs')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'UnityFixture/Editor/AssetFixtureChecks.cs') -Destination (Join-Path $validationRoot 'Assets/Editor/AssetFixtureChecks.cs')
$log = Join-Path $validationRoot 'validation.log'
$process = Start-Process -FilePath $Unity -ArgumentList @('-batchmode','-nographics','-quit','-projectPath',('"'+$validationRoot+'"'),'-buildTarget',$Target,'-executeMethod','AegisValidation.Run','-logFile',('"'+$log+'"')) -WindowStyle Hidden -PassThru -Wait
if ($process.ExitCode -ne 0 -or !(Select-String -LiteralPath $log -SimpleMatch 'AEGIS_BUILD_PASS' -Quiet)) { throw "Build validation failed. See $log" }
if ($Target -eq 'Win64') {
    $playerLog = Join-Path $validationRoot 'player.log'
    $player = Start-Process -FilePath (Join-Path $validationRoot 'Builds/AegisWindows/Game.exe') -ArgumentList @('-batchmode','-nographics','-logFile',('"'+$playerLog+'"')) -WindowStyle Hidden -PassThru
    if (!$player.WaitForExit(120000)) { Stop-Process -Id $player.Id; throw "Player timed out. See $playerLog" }
    if ($player.ExitCode -ne 0 -or !(Select-String -LiteralPath $playerLog -SimpleMatch 'AEGIS_PLAYER_PASS' -Quiet)) { throw "Player failed. See $playerLog" }
}
Write-Output "Validation passed: $Target. Android device execution is a separate step. Logs: $validationRoot"
