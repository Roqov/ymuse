@echo off
rem Собрать и запустить Ymuse.
cd /d "%~dp0"
dotnet run --project src\Ymuse -c Release
