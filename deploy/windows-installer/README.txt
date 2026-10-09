SERI Resort POS - installing on a till
======================================

You need: a Windows 10 or 11 PC (64-bit). Nothing else - the program carries everything it needs.

1. Unzip this whole folder (right-click the zip > Extract All). Do not run it from inside the zip.
2. Double-click  Install.cmd  and say Yes to the Windows prompt.
   It asks three short questions (you can press Enter to skip any):
     - the PROPERTY server address (inside the building), for example  http://192.168.1.75
     - the ONLINE server address (already filled in - just press Enter)
     - a name for this till, for example  Reception POS 2
3. Open "SERI Resort POS" from the desktop.
4. First start only: type the REGISTRATION CODE from IT, a name for this till, and press Register.
   (IT makes the code in the admin: People > Devices > Register a device. Choose the till's Home facility.)
   Then sign in with your staff number and PIN.

Switching between the property server and the online server
-----------------------------------------------------------
Click the "SERI Resort" title at the top of the screen 7 times in a row. Pick the server. The till keeps its
registration on each server, so switching back needs no new code. It will not switch while unsent items are waiting.

Updating
--------
Run the NEW installer the same way. It replaces the program and keeps this till's settings and registration.

If something goes wrong
-----------------------
- "Registration code is invalid, expired or already used": a code works once and for 24 hours. Ask IT for a new one.
- The screen explains most problems in plain words. For anything else IT can read the till's error log:
  %LOCALAPPDATA%\R007Pos\logs\errors.log
- To remove the program: run Uninstall.cmd.
