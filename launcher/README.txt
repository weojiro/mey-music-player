mey music player
================

1. Double-click "MMP Launcher.bat".
2. Wait until it says "Installation complete!". It installs the app and puts a
   "mey music player" shortcut on your desktop. Nothing else needs to be installed.
3. Start the app with that shortcut.

The launcher also turns off Windows' multiplane overlay (MPO), so the player shows over borderless full-screen
games and in recordings without black frames. Windows asks for permission once; it takes effect after a restart.
To undo: in a terminal opened as administrator run
reg delete "HKLM\SOFTWARE\Microsoft\Windows\Dwm" /v OverlayTestMode /f
and restart.

Updates are offered inside the app. If the app ever stops starting, run "MMP Launcher.bat" again.

The first time, Windows may warn about an unknown publisher. Choose "More info" and then "Run anyway".
