Add-Type -TypeDefinition @"
using System;using System.Drawing;using System.Runtime.InteropServices;
public class WCap {
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint f);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
 public static void Cap(IntPtr h, string path) {
   RECT r; GetWindowRect(h, out r);
   int w = r.R-r.L, ht = r.B-r.T;
   using (var bmp = new Bitmap(w, ht))
   using (var g = Graphics.FromImage(bmp)) {
     IntPtr hdc = g.GetHdc();
     PrintWindow(h, hdc, 2);
     g.ReleaseHdc(hdc);
     bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
   }
 }
}
"@ -ReferencedAssemblies System.Drawing,System.Windows.Forms
$p = Get-Process blender -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowTitle -ne "" } | Select-Object -First 1
if ($p) { [WCap]::Cap($p.MainWindowHandle, "gui_printwindow.png"); Write-Output "captured $($p.Id)" } else { Write-Output "no gui blender" }
