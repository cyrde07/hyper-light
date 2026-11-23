@echo off
rem LED count test: radiator lights only LEDs 65-128 (white).
rem Please quit OpenRGB first.
"%~dp0HyperLight.exe" board range 65 128
pause
