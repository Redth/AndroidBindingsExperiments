@echo off
call "%~dp0..\..\..\build-tools\gradle\gradlew.bat" -p "%~dp0" %*
exit /b %errorlevel%
