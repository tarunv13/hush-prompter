<#
  Optional end-to-end check of the AI flow through the real interface. It makes one small
  request with your configured provider (Claude Code by default), so it is kept separate.
  Opens the AI menu, runs "Split into cues", waits for the preview, accepts it, and checks the
  saved note on disk now has cue headings.
  Usage: powershell -ExecutionPolicy Bypass -File tests\e2e\run-ai-e2e.ps1 [-Exe path] [-Shot file.png]
#>
param(
  [string]$Exe = (Join-Path $PSScriptRoot '..\..\src\Hush\bin\Release\Hush.exe'),
  [string]$Shot = ''
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class PW {
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
'@
$A = [System.Windows.Automation.AutomationElement]
$Scope = [System.Windows.Automation.TreeScope]
function Cond($prop, $val) { New-Object System.Windows.Automation.PropertyCondition($prop, $val) }
function FindWindow($name, $ms = 10000) {
  $c = New-Object System.Windows.Automation.AndCondition((Cond $A::NameProperty $name), (Cond $A::ControlTypeProperty ([System.Windows.Automation.ControlType]::Window)))
  $sw = [Diagnostics.Stopwatch]::StartNew()
  while ($sw.ElapsedMilliseconds -lt $ms) { $e = $A::RootElement.FindFirst($Scope::Descendants, $c); if ($e) { return $e }; Start-Sleep -Milliseconds 200 }
  throw "window '$name' not found"
}
function FindByName($root, $name, $ms = 5000) {
  $sw = [Diagnostics.Stopwatch]::StartNew()
  while ($sw.ElapsedMilliseconds -lt $ms) { $e = $root.FindFirst($Scope::Descendants, (Cond $A::NameProperty $name)); if ($e) { return $e }; Start-Sleep -Milliseconds 150 }
  throw "element '$name' not found"
}
function Invoke($el) { $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }

$work = Join-Path ([IO.Path]::GetTempPath()) ("hush-ai-e2e-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force (Join-Path $work 'notes') | Out-Null
'{"Calibrated":true,"HideMainWindowFromCapture":false,"AiProvider":"Auto","AiModel":"Auto"}' | Set-Content (Join-Path $work 'settings.json')
$note = Join-Path $work 'notes\ainote.md'
[IO.File]::WriteAllText($note, "# Pitch`n`nHello, I run a small bakery. First the problem: rent keeps rising. Then our idea: a shared kitchen for five bakers. Finally the ask: a loan for one oven.", (New-Object Text.UTF8Encoding $false))

$p = Start-Process (Resolve-Path $Exe).Path -ArgumentList @('--data-dir', $work, '--open-note', 'ainote', '--skip-calibration') -PassThru
$fails = 0
function Check($cond, $name) { if ($cond) { Write-Host "  PASS  $name" } else { Write-Host "  FAIL  $name" -ForegroundColor Red; $script:fails++ } }
try {
  $main = FindWindow 'Hush'
  Start-Sleep -Milliseconds 800
  $buttons = $main.FindAll($Scope::Descendants, (Cond $A::ControlTypeProperty ([System.Windows.Automation.ControlType]::Button)))
  $ai = $buttons | Where-Object { $_.Current.HelpText -eq 'AI' } | Select-Object -First 1
  Invoke $ai
  $item = FindByName $A::RootElement 'Split into cues'
  Invoke $item
  $win = FindWindow 'AI'
  $replace = FindByName $win 'Replace note'
  $sw = [Diagnostics.Stopwatch]::StartNew()
  while (-not $replace.Current.IsEnabled -and $sw.Elapsed.TotalSeconds -lt 120) { Start-Sleep -Milliseconds 500 }
  Check $replace.Current.IsEnabled ("preview ready in " + [math]::Round($sw.Elapsed.TotalSeconds, 1) + " s")
  $texts = $win.FindAll($Scope::Descendants, (Cond $A::ControlTypeProperty ([System.Windows.Automation.ControlType]::Text))) | ForEach-Object { $_.Current.Name }
  $route = $texts | Where-Object { $_ -match 'Quick' } | Select-Object -First 1
  Check ($route -ne $null) "routed to the fast tier: $route"
  if ($Shot) {
    $h = [IntPtr]$win.Current.NativeWindowHandle; $r = New-Object PW+RECT; [PW]::GetWindowRect($h, [ref]$r) | Out-Null
    $bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T); $g = [System.Drawing.Graphics]::FromImage($bmp); $hdc = $g.GetHdc()
    [PW]::PrintWindow($h, $hdc, 2) | Out-Null; $g.ReleaseHdc($hdc); $g.Dispose(); $bmp.Save($Shot); $bmp.Dispose()
  }
  Invoke $replace
  Start-Sleep -Milliseconds 1500   # the editor saves 600 ms after a change
  $saved = [IO.File]::ReadAllText($note)
  Check ($saved -match '(?m)^## ') 'the accepted text is saved with cue headings'
  Check ($saved -match 'bakery' -and $saved -match 'oven') 'the original content is kept'
}
finally {
  Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
  Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
Write-Host "`n$fails failed"
exit $fails
