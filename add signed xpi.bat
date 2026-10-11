@echo off
rem drag the signed .xpi from addons.mozilla.org onto this file
cd /d "%~dp0"
node release.js signed "%~1"
if not errorlevel 1 node release.js publish
pause
