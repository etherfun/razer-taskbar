Add-Type -AssemblyName System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public class Cap {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindowW(string cls, string title);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
  public struct R { public int L, T, Rt, B; }
}
'@
[Cap]::SetProcessDPIAware() | Out-Null
$h = [Cap]::FindWindowW('RazerTaskbarWidget', 'RazerTaskbarWidget')
if ($h -eq [IntPtr]::Zero) { Write-Output 'widget window not found'; exit 1 }
$r = New-Object Cap+R
[void][Cap]::GetWindowRect($h, [ref]$r)
Write-Output "widget rect: $($r.L),$($r.T) - $($r.Rt),$($r.B)"
$w = $r.Rt - $r.L; $hh = $r.B - $r.T
$bmp = New-Object System.Drawing.Bitmap($w, $hh)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.L, $r.T, 0, 0, (New-Object System.Drawing.Size($w, $hh)))
$g.Dispose()
$bmp.Save('F:\dev\GitHub\razer-taskbar\tests\ct-widget-1x.png')
$bmp.Dispose()
Write-Output 'saved'
