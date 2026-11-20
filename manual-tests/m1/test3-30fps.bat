@echo off
rem M1 test 3: green at about 30 frames per second (33 ms interval), 15 seconds.
"%~dp0HyperLight.exe" color 00FF00 --interval 33 --seconds 15
pause
