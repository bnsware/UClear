# Publishes the single-file Windows build to dist/win-x64/UClear.exe and refreshes dist/win-x64/SHA256.txt.
# A running copy of that exact EXE is closed first (normally, then forcibly after 5 s); other UClear copies are left alone.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices; using System.Text;
public static class UClearProcessPath {
  [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
  [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)] static extern bool QueryFullProcessImageNameW(IntPtr h, int flags, StringBuilder name, ref int size);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
  public static string Get(int pid) { IntPtr h = OpenProcess(0x1000, false, pid); if (h == IntPtr.Zero) return ""; try { var sb = new StringBuilder(1024); int n = sb.Capacity; return QueryFullProcessImageNameW(h, 0, sb, ref n) ? sb.ToString() : ""; } finally { CloseHandle(h); } }
}
'@ -ErrorAction SilentlyContinue
$target = [IO.Path]::GetFullPath('dist\win-x64\UClear.exe')
# An elevated UClear hides Process.Path from a normal shell, so the image path is read with a limited query handle.
$running = Get-Process -Name UClear -ErrorAction SilentlyContinue | Where-Object { [UClearProcessPath]::Get($_.Id) -ieq $target }
foreach ($p in $running) { $null = $p.CloseMainWindow(); if (-not $p.WaitForExit(5000)) { Stop-Process -Id $p.Id -Force } }
"Kapatılan süreç: $(@($running).Count)"
dotnet publish src/UClear/UClear.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -o dist/win-x64 | Select-Object -Last 1
if ($LASTEXITCODE -ne 0) { exit 1 }
$hash = (Get-FileHash dist\win-x64\UClear.exe -Algorithm SHA256).Hash
Set-Content dist\win-x64\SHA256.txt "$hash  UClear.exe"
Get-Content dist\win-x64\SHA256.txt
