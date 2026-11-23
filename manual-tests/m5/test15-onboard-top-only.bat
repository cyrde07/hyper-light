@echo off
rem Motherboard onboard lights: only I/O cover TOP white, bottom off.
rem Please quit the tray HyperLight and OpenRGB first.
"%~dp0HyperLight.exe" board onboard FFFFFF 000000
pause
