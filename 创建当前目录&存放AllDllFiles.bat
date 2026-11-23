@echo off
setlocal enabledelayedexpansion
:: 创建存放dll的文件夹
set "dllDir=All_Dll_Files"
md "%dllDir%" 2>nul

echo 正在搜索当前目录及子目录所有dll文件...
for /r %%i in (*.dll) do (
    echo 复制: %%~nxi
    copy "%%i" "%dllDir%\" >nul
)
echo.
echo ======================================
echo 收集完成！所有DLL保存在【%dllDir%】文件夹
pause