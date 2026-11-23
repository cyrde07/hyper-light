@echo off
rem Motherboard onboard lights: only I/O cover BOTTOM white, top off.
rem Please quit the tray HyperLight and OpenRGB first.
"%~dp0HyperLight.exe" board onboard 000000 FFFFFF
pause
