@echo off
rem LED count test: radiator lights only LEDs 129-192 (white).
rem Please quit OpenRGB first.
"%~dp0HyperLight.exe" board range 129 192
pause
