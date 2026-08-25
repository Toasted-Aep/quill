# zrun.ps1 - sweep a list of presets of ONE grid type at the CURRENT zoom.
#
#   .\zrun.ps1 -Type 2-Point -Idx 0,2,3 -Names "2 Point","1_4 Narrow","Side Narrow" -Tag S10
#
# The viewport is deliberately NOT the frozen 100% one - see 15.5c. Every
# capture in a run shares one zoom, and the zoom readout is saved beside each
# frame so a capture can never be silently attributed to the wrong viewport.

param(
  [Parameter(Mandatory=$true)][string]$Type,
  [Parameter(Mandatory=$true)][int[]]$Idx,
  [Parameter(Mandatory=$true)][string[]]$Names,
  [string]$Tag = "S10"
)
$ErrorActionPreference = "Stop"
$S = "C:\Users\irony\AppData\Local\Temp\claude\C--Users-irony\5d0bc6f7-2eaf-4e19-afbf-f5efd33b5de9\scratchpad"
. "$S\zlib.ps1"

if ($Idx.Count -ne $Names.Count) { throw "ABORT: $($Idx.Count) indices but $($Names.Count) names" }

ZPre 0 | Out-Null
VP-CheckHandover | Out-Null
VP-Log "=== ZRUN $Type [$($Names -join ', ')] tag=$Tag ==="
ZEditor $Type

for ($k = 0; $k -lt $Idx.Count; $k++) {
  $name = "$Type - $($Names[$k])"
  ZPreset $Idx[$k] $name $Tag | Out-Null
  ZCapture $name $Tag
  # the panel does not reliably reopen where it was closed - renavigate,
  # probed, rather than assuming
  $p = ZWorkspace
  if (-not $p.inEditor) { VP-Log "panel reopened outside the editor - renavigating"; ZEditor $Type }
}
VP-SaveHandover
VP-Log "=== ZRUN $Type done ==="
"OK"
