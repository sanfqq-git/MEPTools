@echo off
setlocal enabledelayedexpansion
set "dllDir=All_DLL_Files"
md "%dllDir%" 2>nul

echo 正在搜索当前目录及子目录所有dll文件...
for /r %%i in (*.dll) do (
    set "src=%%i"
    call :CopyWithUniqueName "%%i"
)
echo.
echo ======================================
echo 收集完成！所有DLL保存在【%dllDir%】文件夹
pause
exit /b

:CopyWithUniqueName
set "src=%~1"
set "name=%~n1"
set "ext=%~x1"
set "dst=%dllDir%\%name%%ext%"
set /a n=0
:findName
if exist "%dst%" (
    set /a n+=1
    set "dst=%dllDir%\%name%_!n!%ext%"
    goto findName
)
echo 复制: %~nx1  -^>  %dst%
copy "%src%" "%dst%" >nul
exit /b