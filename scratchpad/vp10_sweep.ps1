# Run 7's method, which is the only one that catches a mirrored draw against an
# unmirrored pick:
#   read the pixel DRAWN at the target BEFORE the press,
#   press it,
#   read the dial's dot AFTER.
# The comparison needs no model of _rot at all - it asks whether the renderer
# and the hit test name the SAME swatch.
param([int]$Skip = 0, [int]$Take = 1000, [string]$Tag = 's',
      [int]$DotX = 285, [int]$DotY = 366,
      [int]$SentX = 978, [int]$SentY = 336,
      [string]$Csv = 'probes.csv')
. "$PSScriptRoot\vp10.ps1"

# DotX/DotY  : the dial's colour dot (DIAL-DOT-ELEMENT x2, client origin is 0,0)
# SentX/SentY: a tile that is drawn whenever the wheel is open - page colour
#              there means the wheel is shut and has to be reopened.
$Page = '#FF00FF'

function Wheel-Open {
  for ($k = 0; $k -lt 4; $k++) {
    if ((Px $SentX $SentY) -ne $Page) { return $true }
    Q-Tap $DotX $DotY | Out-Null
    Start-Sleep -Milliseconds 1400
  }
  return ((Px $SentX $SentY) -ne $Page)
}

$rows = Import-Csv "$script:QShots\$Csv"
$sel = $rows | Select-Object -Skip $Skip -First $Take
"DESKTOP : $(Q-Desktop)"
"probes  : $($sel.Count) of $($rows.Count)  (skip $Skip)"
Q-Safe | Out-Null

$log = "$script:QShots\sweep-$Tag.csv"
"idx,kind,col,ring,code,expectHex,x,y,drawn,dot,drawnOk,pickOk" | Out-File -FilePath $log -Encoding ascii
$i = $Skip
$fail = 0; $ok = 0; $skipped = 0
foreach ($r in $sel) {
  $i++
  if (-not (Wheel-Open)) { "[$i] WHEEL WOULD NOT OPEN - stopping"; break }
  # read the DRAWN pixel with the cursor still parked elsewhere
  $drawn = Px ([int]$r.x) ([int]$r.y)
  Q-Tap ([int]$r.x) ([int]$r.y) | Out-Null
  Start-Sleep -Milliseconds 1200
  $dot = Px $DotX $DotY
  $exp = $r.hex
  $dOk = if ($drawn -eq $exp) { 1 } else { 0 }
  $pOk = if ($dot -eq $drawn) { 1 } else { 0 }
  if ($drawn -eq $Page) { $skipped++; $pOk = -1 }
  elseif ($pOk -eq 1) { $ok++ } else { $fail++ }
  "$i,$($r.kind),$($r.col),$($r.ring),$($r.code),$exp,$($r.x),$($r.y),$drawn,$dot,$dOk,$pOk" |
    Out-File -FilePath $log -Encoding ascii -Append
  if ($pOk -ne 1 -or $dOk -ne 1) {
    "[$i] $($r.kind) col=$($r.col) ring=$($r.ring) $($r.code) expect=$exp drawn=$drawn dot=$dot"
  }
}
""
"drawn==expected AND dot==drawn : $ok"
"MISMATCH                       : $fail"
"landed on page (not counted)   : $skipped"
"log: $log"
"cursor: $([Q]::Cursor())"
