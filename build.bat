@echo off
rem builds optima-save.exe (the helper + installer: WebView2, the trim and setup pages, the logo, the
rem font, the sound and the chrome extension all inside), optima-save.xpi (zen / firefox) and chrome\
rem (chrome, brave, edge). "build.bat test <out.exe>" makes the test build (debug port, settings of its own).
cd /d "%~dp0"

if exist chrome rmdir /s /q chrome
mkdir chrome
copy /y extension\background.js chrome\ >nul
copy /y icons\icon16.png chrome\ >nul
copy /y icons\icon32.png chrome\ >nul
copy /y icons\icon48.png chrome\ >nul
copy /y icons\icon128.png chrome\ >nul
copy /y chrome-manifest.json chrome\manifest.json >nul

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set REFS=/r:System.Web.Extensions.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /r:vendor\Microsoft.Web.WebView2.Core.dll /r:vendor\Microsoft.Web.WebView2.WinForms.dll
set RES=/resource:vendor\Microsoft.Web.WebView2.Core.dll,Microsoft.Web.WebView2.Core.dll /resource:vendor\Microsoft.Web.WebView2.WinForms.dll,Microsoft.Web.WebView2.WinForms.dll /resource:vendor\WebView2Loader.dll,WebView2Loader.dll
set RES=%RES% /resource:trim.html,trim.html /resource:setup.html,setup.html /resource:vendor\inter.woff2,inter.woff2 /resource:vendor\done.wav,done.wav
set RES=%RES% /resource:vendor\logo-small.png,logo-small.png /resource:vendor\logo-320.png,logo-320.png
set RES=%RES% /resource:chrome\manifest.json,chrome/manifest.json /resource:chrome\background.js,chrome/background.js /resource:chrome\icon16.png,chrome/icon16.png /resource:chrome\icon32.png,chrome/icon32.png /resource:chrome\icon48.png,chrome/icon48.png /resource:chrome\icon128.png,chrome/icon128.png
set FLAGS=/nologo /target:winexe /platform:x64 /optimize+ /win32icon:icons\icon.ico

if /i "%~1"=="test" (
  "%CSC%" %FLAGS% /define:TESTMODE "/out:%~2" %REFS% %RES% optima-save.cs || exit /b 1
  echo built test helper %~2
  exit /b 0
)

"%CSC%" %FLAGS% /out:optima-save.exe %REFS% %RES% optima-save.cs || exit /b 1
del optima-save.xpi 2>nul
powershell -NoProfile -Command "Add-Type -A System.IO.Compression.FileSystem; [IO.Compression.ZipFile]::CreateFromDirectory('extension', 'optima-save.xpi')" || exit /b 1
echo built optima-save.exe, optima-save.xpi and chrome\
