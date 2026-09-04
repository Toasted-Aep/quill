# Phase A - launch against the scratch folder, pin, capture the boot state and
# the window metrics.  Nothing is pressed.
. "$PSScriptRoot\vp10.ps1"

Add-Type -Namespace VPA -Name W -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RC r);
[DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref PT p);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RC r);
[DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
public struct RC { public int L,T,R,B; }
public struct PT { public int X,Y; }
'@

"SEAL BEFORE : $((Q-Seal) | ConvertTo-Json -Compress)"
"DESKTOP     : $(Q-Desktop)"
"cursor      : $([Q]::Cursor())   idle $([Q]::Idle())"
"screen      : $([Q]::SW) x $([Q]::SH) physical"

if (Test-Path $script:QGeom) { Remove-Item $script:QGeom -Force }
if (Test-Path $script:QProbe) { Remove-Item $script:QProbe -Force }

$pid1 = Q-Launch
"launched pid: $pid1"
Q-Safe | Out-Null
Start-Sleep -Seconds 2

$q = Get-Process -Id $pid1
$h = $q.MainWindowHandle
$wr = New-Object VPA.W+RC; [VPA.W]::GetWindowRect($h, [ref]$wr) | Out-Null
$cr = New-Object VPA.W+RC; [VPA.W]::GetClientRect($h, [ref]$cr) | Out-Null
$o  = New-Object VPA.W+PT; $o.X = 0; $o.Y = 0
[VPA.W]::ClientToScreen($h, [ref]$o) | Out-Null
"window rect : $($wr.L),$($wr.T) .. $($wr.R),$($wr.B)"
"client size : $($cr.R) x $($cr.B)"
"client origin (screen px): $($o.X),$($o.Y)"
"dpi for win : $([VPA.W]::GetDpiForWindow($h))"

Shot "a01-boot"
"cursor after: $([Q]::Cursor())   idle $([Q]::Idle())"
""
"GEOM LOG:"
if (Test-Path $script:QGeom) { Get-Content $script:QGeom } else { "<none yet>" }
""
"THEME LOG (last):"
Q-Theme
