@echo off
rem Builds bin\Loupe.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
rem No SDK or Visual Studio required.
setlocal
cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo error: csc.exe from .NET Framework 4 was not found.
    exit /b 1
)

if not exist bin mkdir bin

"%CSC%" /nologo /optimize+ /unsafe /target:winexe /out:bin\Loupe.exe ^
    /win32icon:assets\loupe.ico /resource:assets\loupe.ico,Loupe.loupe.ico ^
    /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
    src\*.cs
if errorlevel 1 exit /b 1

echo Built bin\Loupe.exe
