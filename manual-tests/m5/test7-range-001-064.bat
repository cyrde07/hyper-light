@echo off
rem LED count test: radiator lights only LEDs 1-64 (white). Note which part lights up.
rem Please quit OpenRGB first.
"%~dp0HyperLight.exe" board range 1 64
pause
