@echo off
REM ============================================================================
REM  СБОРКА ПРИЛОЖЕНИЯ «Сбор форензик-артефактов v1.0» В ОДИН .EXE
REM  Компилятор csc.exe (.NET Framework) есть в Windows из коробки —
REM  ничего устанавливать не нужно. Требуется Windows 10/11 x64.
REM
REM  Использование: двойной клик по build.cmd (или запуск из cmd).
REM  Результат: dist\ForensicCollector.exe
REM ============================================================================
setlocal
cd /d "%~dp0"

REM --- ищем компилятор C# из .NET Framework (путь стандартный для Win10/11) ---
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo [ОШИБКА] Не найден компилятор csc.exe .NET Framework 4.x.
    echo Убедитесь, что на системе установлен .NET Framework 4.7.2+ ^(входит в Windows 10/11^).
    pause
    exit /b 1
)

if not exist dist mkdir dist

echo Сборка ForensicCollector.exe ...
"%CSC%" /nologo /target:winexe ^
    /out:dist\ForensicCollector.exe ^
    /win32icon:src\ForensicCollector\app.ico ^
    /win32manifest:src\ForensicCollector\app.manifest ^
    /r:System.dll /r:System.Core.dll /r:System.Drawing.dll ^
    /r:System.Windows.Forms.dll /r:System.IO.Compression.dll ^
    /r:System.IO.Compression.FileSystem.dll /r:Microsoft.CSharp.dll ^
    src\ForensicCollector\Program.cs ^
    src\ForensicCollector\Native.cs ^
    src\ForensicCollector\Theme.cs ^
    src\ForensicCollector\ElevationPromptForm.cs ^
    src\ForensicCollector\ForensicEngine.cs ^
    src\ForensicCollector\MainForm.cs

if errorlevel 1 (
    echo.
    echo [ОШИБКА] Компиляция завершилась с ошибками — см. текст выше.
    pause
    exit /b 1
)

echo.
echo [УСПЕХ] Готово: %cd%\dist\ForensicCollector.exe
echo Запуск: двойной клик по ForensicCollector.exe
pause
endlocal
