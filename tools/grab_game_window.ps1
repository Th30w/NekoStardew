param(
    [Parameter(Mandatory = $false)]
    # 默认落在工程根下的 docs 目录，跟着脚本位置走，不写死本机路径
    [string]$OutPath = (Join-Path (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)) 'docs\game-printwindow.png')
)

Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public class GW {
    [DllImport("user32.dll")] public static extern bool EnumWindows(Ep f, IntPtr l);
    [DllImport("user32.dll")] public static extern IntPtr GetWindowThreadProcessId(IntPtr h, out int p);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    public delegate bool Ep(IntPtr h, IntPtr l);
    public static IntPtr FindGame(int pid) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h,l) => {
            int p; GetWindowThreadProcessId(h, out p);
            if (p == pid) {
                var c = new StringBuilder(256);
                GetClassName(h, c, 256);
                if (c.ToString() == "SDL_app") { found = h; return false; }
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
'@

$proc = Get-Process -Name 'StardewModdingAPI' -ErrorAction SilentlyContinue
if (-not $proc) { Write-Output 'NO_GAME_PROCESS'; exit 1 }

$hw = [GW]::FindGame($proc.Id)
if ($hw -eq [IntPtr]::Zero) { Write-Output 'NO_GAME_WINDOW'; exit 1 }
Write-Output ("hwnd=" + $hw)

# 用客户区而不是整窗，避免把标题栏和边框一起抓进来
$cr = New-Object GW+RECT
[void][GW]::GetClientRect($hw, [ref]$cr)
$w = $cr.R - $cr.L
$h = $cr.B - $cr.T
Write-Output ("client=" + $w + "x" + $h)
if ($w -le 8 -or $h -le 8) { Write-Output 'BAD_CLIENT_SIZE'; exit 1 }

$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()

# 0x2 = PW_RENDERFULLCONTENT，抓 DirectX/合成内容的关键
$ok = [GW]::PrintWindow($hw, $hdc, 2)
Write-Output ("printwindow=" + $ok)

$g.ReleaseHdc($hdc)
$g.Dispose()

$bmp.Save($OutPath, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output ("saved=" + $OutPath + " bytes=" + (Get-Item -LiteralPath $OutPath).Length)
