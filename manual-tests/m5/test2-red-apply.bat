@echo off
rem M5-1: only if test1 did not change the lights. Same, plus the apply command.
rem Please quit OpenRGB first.
"%~dp0HyperLight.exe" board color FF0000 --apply
pause
