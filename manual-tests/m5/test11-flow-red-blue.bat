@echo off
rem Flow test: case and radiator flow red/blue for 10 seconds (same flow algorithm as the tray app).
rem Please quit the tray HyperLight and OpenRGB first.
"%~dp0HyperLight.exe" board flow FF0000 0000FF
pause
