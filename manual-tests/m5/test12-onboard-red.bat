@echo off
rem Motherboard onboard lights (I/O cover top and bottom): red.
rem Please quit the tray HyperLight and OpenRGB first.
"%~dp0HyperLight.exe" board onboard FF0000
pause
