<#
  End-to-end checks of the real app, with no microphone or person needed.
  1. Synthesises a spoken script with the built-in Windows voice: speech, 3 s silence, speech.
  2. Starts Hush with that WAV as its microphone, in an isolated data folder.
  3. Reads the status file Hush writes every 200 ms and checks the behaviour.
  4. Captures the screen to prove the prompter is hidden from capture, then shows it when allowed.
  5. Sends the global hotkeys and checks their effect.
  Usage: powershell -ExecutionPolicy Bypass -File tests\e2e\run-e2e.ps1 [-Exe path] [-Shots dir]
#>
param(
  [string]$Exe = (Join-Path $PSScriptRoot '..\..\src\Hush\bin\Release\Hush.exe'),
  [string]$Shots = ''
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Speech, System.Drawing, System.Windows.Forms
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class Keys {
  [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  public static void Chord(params byte[] vks) {
    foreach (var k in vks) keybd_event(k, 0, IsExt(k) ? 1u : 0u, UIntPtr.Zero);
    System.Threading.Thread.Sleep(40);
    for (int i = vks.Length - 1; i >= 0; i--) keybd_event(vks[i], 0, (IsExt(vks[i]) ? 1u : 0u) | 2u, UIntPtr.Zero);
  }
  static bool IsExt(byte k) { return k == 0x25 || k == 0x26 || k == 0x27 || k == 0x28 || k == 0x21 || k == 0x22; }
}
'@

$Exe = (Resolve-Path $Exe).Path
$work = Join-Path ([IO.Path]::GetTempPath()) ("hush-e2e-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $work | Out-Null
$fails = New-Object System.Collections.Generic.List[string]
function Check($cond, $name) { if ($cond) { Write-Host "  PASS  $name" } else { Write-Host "  FAIL  $name" -ForegroundColor Red; $script:fails.Add($name) } }

# ---------- 1. spoken test audio ----------
$wav = Join-Path $work 'speech.wav'
$synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
$synth.Rate = 0
$synth.SetOutputToWaveFile($wav, (New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo 16000, ([System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen), ([System.Speech.AudioFormat.AudioChannel]::Mono)))
$ssml = '<speak version="1.0" xmlns="http://www.w3.org/2001/10/synthesis" xml:lang="en-GB"><break time="3500ms"/>' +
        'Good morning everyone. Today I want to walk you through our plan for the next quarter, and why it matters for every team in this room.' +
        '<break time="3000ms"/>' +
        'First, we will focus on the customers who already use the product every single day, and listen carefully to what they need from us next.' +
        '<break time="2500ms"/></speak>'
$synth.SpeakSsml($ssml); $synth.Dispose()
Write-Host "Test audio: $([math]::Round((Get-Item $wav).Length / 32000, 1)) s"

# ---------- 2. isolated profile ----------
$data = Join-Path $work 'data'
New-Item -ItemType Directory -Force -Path (Join-Path $data 'notes') | Out-Null
$script = "# Plan`n`nGood morning everyone. Today I want to walk you through our plan for the next quarter, and why it matters for every team in this room.`n`n## Customers`nFirst, we will focus on the customers who already use the product every single day, and listen carefully to what they need from us next.`n`n## Close`n" + (("We will share the numbers every week, and we will ask for your feedback at each step of the way. " * 6) + "`n`n") * 3 + "Thank you."
[IO.File]::WriteAllText((Join-Path $data 'notes\e2etestnote.md'), $script, (New-Object Text.UTF8Encoding $false))
function Write-Settings($hide) {
  $json = '{"Calibrated":true,"HideFromCapture":' + $hide + ',"HideMainWindowFromCapture":' + $hide + ',"CountdownSeconds":3,"SpeedStep":6,"FontSize":22,"Mode":"Voice","AdaptiveTempo":true}'
  [IO.File]::WriteAllText((Join-Path $data 'settings.json'), $json)
}
$status = Join-Path $work 'status.json'

function Read-Status {
  # The app replaces the file atomically; a read can land mid-swap, so retry briefly.
  for ($i = 0; $i -lt 10; $i++) { try { return (Get-Content $status -Raw -ErrorAction Stop | ConvertFrom-Json) } catch { Start-Sleep -Milliseconds 30 } }
  return $null
}
function Wait-Until([scriptblock]$cond, [int]$ms = 3000) {
  $sw = [Diagnostics.Stopwatch]::StartNew()
  while ($sw.ElapsedMilliseconds -lt $ms) { $s = Read-Status; if ($s -and (& $cond $s)) { return $true }; Start-Sleep -Milliseconds 100 }
  return $false
}
function Start-Hush($extra) {
  $args = @('--data-dir', $data, '--status-file', $status, '--open-note', 'e2etestnote', '--skip-calibration') + $extra
  $p = Start-Process -FilePath $Exe -ArgumentList $args -PassThru
  for ($i = 0; $i -lt 60; $i++) { Start-Sleep -Milliseconds 250; $s = Read-Status; if ($s -and $s.running) { break } }
  return $p
}
function Capture($x, $y, $w, $h, $file) {
  $bmp = New-Object System.Drawing.Bitmap $w, $h
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($x, $y, 0, 0, $bmp.Size)
  $g.Dispose()
  if ($file) { $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png) }
  return $bmp
}
function DarkShare($bmp) {
  $dark = 0; $n = 0
  for ($x = 0; $x -lt $bmp.Width; $x += 4) { for ($y = 0; $y -lt $bmp.Height; $y += 4) { $c = $bmp.GetPixel($x, $y); $n++; if (($c.R + $c.G + $c.B) -lt 30) { $dark++ } } }
  return $dark / [math]::Max(1, $n)
}

# ---------- 3. voice scrolling run (hidden from capture) ----------
Write-Settings 'true'
Remove-Item $status -ErrorAction SilentlyContinue
$p = Start-Hush @('--audio-file', $wav, '--start', 'notch')
$log = New-Object System.Collections.Generic.List[object]
$sw = [Diagnostics.Stopwatch]::StartNew()
$shotHidden = $null
while ($sw.Elapsed.TotalSeconds -lt 26) {
  Start-Sleep -Milliseconds 200
  $s = Read-Status
  if ($s) { $log.Add([pscustomobject]@{ t = $sw.Elapsed.TotalSeconds; off = [double]$s.offset; sp = [bool]$s.speaking; cd = [double]$s.countdown; aff = [int]$s.affinity; main = [int]$s.mainAffinity; left = [double]$s.windowLeft; w = [double]$s.windowWidth; tempo = [double]$s.tempo }) }
  if (-not $shotHidden -and $sw.Elapsed.TotalSeconds -gt 9) {
    $scr = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $shotHidden = Capture ([int]($scr.Width / 2 - 200)) 0 400 100 $(if ($Shots) { Join-Path $Shots 'capture-hidden.png' })
  }
}
$last = Read-Status
if ($Shots) { $log | Export-Csv (Join-Path $Shots 'voice-log.csv') -NoTypeInformation }

Write-Host "`nVoice run: $($log.Count) samples"
$cd = $log | Where-Object { $_.cd -gt 0.2 }
Check ($cd.Count -gt 0 -and ($cd | Measure-Object off -Maximum).Maximum -lt 0.5) 'countdown holds the text still'
$speech = $log | Where-Object { $_.sp }
Check ($speech.Count -gt 15) "speech detected in the synthesised audio ($($speech.Count) samples)"
# Find the 3 s silence after the first sentence: the longest run of not-speaking after speech started.
$firstSpeech = ($log | Where-Object { $_.sp } | Select-Object -First 1).t
$gapStart = $null; $gapEnd = $null; $best = 0; $runStart = $null
foreach ($e in $log) {
  if ($e.t -le $firstSpeech) { continue }
  if (-not $e.sp) { if ($null -eq $runStart) { $runStart = $e } }
  else { if ($runStart) { $len = $e.t - $runStart.t; if ($len -gt $best) { $best = $len; $gapStart = $runStart; $gapEnd = $e } }; $runStart = $null }
}
Check ($best -gt 1.5) ("pause between sentences recognised (" + [math]::Round($best, 1) + " s)")
if ($gapStart) {
  $during = $log | Where-Object { $_.t -ge ($gapStart.t + 1.0) -and $_.t -lt ($gapEnd.t - 0.25) }
  $drift = ($during | Measure-Object off -Maximum).Maximum - ($during | Measure-Object off -Minimum).Minimum
  Check ($drift -lt 2) ("text stops during the pause (drift " + [math]::Round($drift, 1) + " px)")
  $before = $log | Where-Object { $_.t -ge $firstSpeech -and $_.t -le $gapStart.t }
  $moved = ($before | Measure-Object off -Maximum).Maximum - ($before | Measure-Object off -Minimum).Minimum
  Check ($moved -gt 40) ("text moves while speaking (" + [math]::Round($moved) + " px in the first sentence)")
  $after = $log | Where-Object { $_.t -gt $gapEnd.t }
  $moved2 = ($after | Measure-Object off -Maximum).Maximum - $gapEnd.off
  Check ($moved2 -gt 40) ("text resumes when speech resumes (" + [math]::Round($moved2) + " px)")
}
Check (($log | Select-Object -Last 1).aff -eq 17) 'prompter window carries WDA_EXCLUDEFROMCAPTURE (0x11)'
Check (($log | Select-Object -Last 1).main -eq 17) 'main window carries WDA_EXCLUDEFROMCAPTURE (0x11)'
$scr = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$l = $log | Select-Object -Last 1
Check ([math]::Abs(($l.left + $l.w / 2) - $scr.Width / 2) -lt 3 -and $l.left -ge 0) 'prompter is centred at the top of the screen'
Check ($shotHidden -and (DarkShare $shotHidden) -lt 0.5) ('screen capture does not show the black notch (dark share ' + [math]::Round((DarkShare $shotHidden), 2) + ')')

# ---------- 4. hotkeys ----------
$step0 = [int]$last.speedStep
[Keys]::Chord(0x11, 0x12, 0x27)   # Ctrl+Alt+Right
Start-Sleep -Milliseconds 600
$s = Read-Status
Check ([int]$s.speedStep -eq $step0 + 1) "Ctrl+Alt+Right raises the speed ($step0 -> $($s.speedStep))"
[Keys]::Chord(0x11, 0x12, 0x50)   # Ctrl+Alt+P
Check (Wait-Until { param($x) $x.paused -eq $true }) 'Ctrl+Alt+P pauses'
[Keys]::Chord(0x11, 0x12, 0x50)
Start-Sleep -Milliseconds 400
$c0 = [int](Read-Status).cue
[Keys]::Chord(0x11, 0x12, 0x22)   # Ctrl+Alt+PageDown
Start-Sleep -Milliseconds 600
$s = Read-Status
Check ([int]$s.cue -eq [math]::Min($c0 + 1, [int]$s.cues - 1)) "Ctrl+Alt+Page Down jumps to the next cue ($c0 -> $($s.cue) of $($s.cues))"
[Keys]::Chord(0x11, 0x12, 0x22); Start-Sleep -Milliseconds 300; [Keys]::Chord(0x11, 0x12, 0x22)
Start-Sleep -Milliseconds 700
$s = Read-Status
Check ([int]$s.cue -eq [int]$s.cues - 1) "the last cue can be reached and stays current ($($s.cue + 1) of $($s.cues))"
[Keys]::Chord(0x11, 0x12, 0x21)   # Ctrl+Alt+PageUp
Start-Sleep -Milliseconds 600
Check ([int](Read-Status).cue -eq [int]$s.cues - 2) 'Ctrl+Alt+Page Up goes back one cue'
[Keys]::Chord(0x11, 0x12, 0x51)   # Ctrl+Alt+Q
Check (Wait-Until { param($x) $x.running -eq $false }) 'Ctrl+Alt+Q stops the prompter'
Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800

# ---------- 5. visible run (capture allowed): screenshots for design review ----------
Write-Settings 'false'
Remove-Item $status -ErrorAction SilentlyContinue
$p = Start-Hush @('--audio-file', $wav, '--start', 'notch')
Start-Sleep -Seconds 9
$shotShown = Capture ([int]($scr.Width / 2 - 200)) 0 400 100 $(if ($Shots) { Join-Path $Shots 'capture-shown.png' })
Check ((DarkShare $shotShown) -gt 0.5) ('with hiding off, the notch is visible in a capture (dark share ' + [math]::Round((DarkShare $shotShown), 2) + ')')
if ($Shots) { Capture ([int]($scr.Width / 2 - 340)) 0 680 330 (Join-Path $Shots 'notch.png') | Out-Null }
Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800

Remove-Item $status -ErrorAction SilentlyContinue
$p = Start-Hush @('--audio-file', $wav, '--start', 'float')
Start-Sleep -Seconds 10
$s = Read-Status
Check ($s.floating -eq $true) 'floating mode starts'
if ($Shots) { Capture ([int]$s.windowLeft - 20) ([int]$s.windowTop - 20) ([int]$s.windowWidth + 40) 420 (Join-Path $Shots 'floating.png') | Out-Null }
Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue

Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
Write-Host "`n$($fails.Count) failed"
if ($fails.Count -gt 0) { exit 1 } else { exit 0 }
