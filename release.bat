@echo off
rem double-click: build and prepare the next release. "release.bat publish" fills ..\_publish afterwards.
cd /d "%~dp0"
node release.js %*
pause
