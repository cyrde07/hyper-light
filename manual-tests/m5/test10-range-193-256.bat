@echo off
rem LED count test: radiator lights only LEDs 193-256 (white).
rem Please quit OpenRGB first.
"%~dp0HyperLight.exe" board range 193 256
pause
