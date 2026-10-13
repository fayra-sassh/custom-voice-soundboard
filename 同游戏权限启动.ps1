$ErrorActionPreference = 'Stop'
$playerPath = Join-Path $PSScriptRoot 'MoiraSoundboard.exe'
if (-not (Test-Path -LiteralPath $playerPath)) { throw 'Player executable not found.' }
# Close only this workspace's generated player. Never touch the game or launcher.
$players = Get-Process -Name MoiraSoundboard -ErrorAction SilentlyContinue
foreach ($playerProcess in $players) {
    if ($playerProcess.Path -eq $playerPath) {
        if (-not $playerProcess.CloseMainWindow() -or -not $playerProcess.WaitForExit(5000)) {
            throw 'Please close the existing soundboard window first.'
        }
    }
}
try {
    # Windows shows its UAC confirmation. No UIAccess, driver, hooks, or auto-elevation.
    Start-Process -FilePath $playerPath -WorkingDirectory $PSScriptRoot -Verb RunAs -WindowStyle Normal
} catch {
    # If UAC was cancelled, restore the normal-permission player.
    Start-Process -FilePath $playerPath -WorkingDirectory $PSScriptRoot -WindowStyle Normal
    Write-Output 'Administrator launch was cancelled or unavailable; normal player restored.'
}
