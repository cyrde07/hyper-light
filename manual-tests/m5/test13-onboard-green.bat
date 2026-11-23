@echo off
rem Motherboard onboard lights: green.
rem Please quit the tray HyperLight and OpenRGB first.
"%~dp0HyperLight.exe" board onboard 00FF00
pause
