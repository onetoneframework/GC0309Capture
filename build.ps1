$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$binaryDirectory = Join-Path $PSScriptRoot 'bin'
New-Item -ItemType Directory -Path $binaryDirectory -Force | Out-Null
$sources = @('EndoscopeDevice.cs', 'RawFrameDecoder.cs', 'AviRecording.cs', 'CaptureWindow.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& $compiler /nologo /platform:x64 /r:System.Drawing.dll /r:System.Windows.Forms.dll ("/out:" + (Join-Path $binaryDirectory 'GC0309Endoscope.exe')) $sources
if ($LASTEXITCODE -ne 0) { throw 'Capture application compilation failed.' }
& $compiler /nologo /platform:x64 /r:System.Drawing.dll /r:System.Windows.Forms.dll /main:GC0309Endoscope.EndoscopeTests ("/out:" + (Join-Path $binaryDirectory 'EndoscopeTests.exe')) $sources (Join-Path $PSScriptRoot 'EndoscopeTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
& (Join-Path $binaryDirectory 'EndoscopeTests.exe')
if ($LASTEXITCODE -ne 0) { throw 'Endoscope unit tests failed.' }
