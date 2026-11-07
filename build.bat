@echo off
chcp 65001 >nul
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe
"%CSC%" /nologo /codepage:65001 /target:winexe /optimize+ /out:CSSSTool.exe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll CSSSTool.cs LogoData.cs
if errorlevel 1 (echo.& echo DERLEME HATASI - yukaridaki mesajlara bakin) else (echo.& echo Tamam: CSSSTool.exe olusturuldu)
pause
