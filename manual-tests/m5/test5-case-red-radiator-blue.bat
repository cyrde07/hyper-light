@echo off
rem M5-1: case red, radiator blue (checks the two zones are not swapped).
rem Please quit OpenRGB first.
"%~dp0HyperLight.exe" board color FF0000 0000FF
pause
