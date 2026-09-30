@echo off
REM AyuTranslate 构建入口（ASCII 包装，避免中文代码页问题）
REM 实际逻辑在 build.ps1
chcp 65001 >nul
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
if errorlevel 1 exit /b 1
pause
