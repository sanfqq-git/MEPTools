@echo off
chcp 65001 >nul
echo =========================================
echo 正在抓取当前的代码改动...
git add .
powershell -NoProfile -Command "git diff --cached | Set-Clipboard"
echo.
echo ✅ 代码改动已成功复制到剪贴板！
echo 👉 赶快去粘贴给 AI，让它帮你写 Commit Message。
echo =========================================
pause