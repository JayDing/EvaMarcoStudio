@echo off
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /out:EvaMacro-UI.exe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll MacroStudio.cs Editing.cs Sequences.cs FeatureTests.cs Interaction.cs LiveUI.cs BatchEditing.cs
if errorlevel 1 pause
