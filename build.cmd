@echo off
rem Builds DesktopOrder.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ /win32icon:app.ico /out:"%USERPROFILE%\Desktop\DesktopOrder.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll DesktopOrder.cs
