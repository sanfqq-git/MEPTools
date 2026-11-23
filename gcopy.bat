@echo off
chcp 65001 >nul
echo =========================================
echo 正在抓取当前的代码改动...
git add .
for /f "usebackq delims=" %%A in (`powershell -NoProfile -Command "(git diff --cached).Count"`) do set DIFFCOUNT=%%A
if "%DIFFCOUNT%"=="0" (
    echo.
    echo ⚠️ 暂存区没有代码改动，未复制任何内容。
    echo =========================================
    pause
    exit /b
)
powershell -NoProfile -Command "git diff --cached | Set-Clipboard"
echo.
echo ✅ 代码改动已成功复制到剪贴板！
echo 👉 赶快去粘贴给 AI，让它帮你写 Commit Message。
echo =========================================
pause