@echo off
chcp 65001 >nul
setlocal enabledelayedexpansion

set "COMMIT_FILE=%~dp0commit_msg.txt"

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
echo.

rem 清空/初始化临时文件，避免残留上次内容
> "%COMMIT_FILE%" echo.

echo 即将打开记事本，请把 AI 生成的 Commit Message 粘贴进去并保存关闭。
echo 提示：第一行是标题，空一行后可写正文（多行均可）。
echo -----------------------------------------
pause
notepad "%COMMIT_FILE%"

rem 去掉空行后检查是否为空
set "HAS_CONTENT="
for /f "usebackq delims=" %%L in ("%COMMIT_FILE%") do (
    if not "%%L"=="" set "HAS_CONTENT=1"
)
if not defined HAS_CONTENT (
    echo.
    echo ⚠️ commit_msg.txt 为空或只有空行，已取消提交。
    del "%COMMIT_FILE%" >nul 2>&1
    echo =========================================
    pause
    exit /b
)

echo.
echo 正在提交...
git commit -F "%COMMIT_FILE%"
if errorlevel 1 (
    echo.
    echo ❌ git commit 失败，请检查上面的报错。
    echo （commit_msg.txt 已保留，方便你修改后重试）
    echo =========================================
    pause
    exit /b
)

del "%COMMIT_FILE%" >nul 2>&1

echo.
echo 正在推送到 origin 和 GitHub...
git push origin && git push GitHub
if errorlevel 1 (
    echo.
    echo ⚠️ 推送失败（origin 或 GitHub 其中一处出错）。commit 已创建，可手动重推。
    pause
    exit /b
)

echo.
echo ✅ 全部完成：已提交并推送到 origin 和 GitHub。
echo =========================================
pause
endlocal