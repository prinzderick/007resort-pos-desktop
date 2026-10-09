@echo off
title SERI Resort POS - uninstall
echo This removes the SERI Resort POS program from this PC.
echo The till's saved registration and queued items (in your user profile) are NOT deleted.
pause
taskkill /im R007.Pos.exe /f >nul 2>&1
del "%PUBLIC%\Desktop\SERI Resort POS.lnk" >nul 2>&1
del "%ProgramData%\Microsoft\Windows\Start Menu\Programs\SERI Resort POS.lnk" >nul 2>&1
rmdir /s /q "C:\SERI-POS"
echo Done.
pause
