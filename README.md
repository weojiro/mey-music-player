<div align="center">

# mey music player

**What you're listening to, right on your Windows taskbar.**

<img src="docs/open-close.gif" alt="The cover on the taskbar opening into the player" width="452">

[**Download**](../../releases/latest) · [Türkçe](README.tr.md)

</div>

mey music player (MMP) puts the cover of whatever is playing on your taskbar: Spotify, YouTube in your
browser, or any app that uses the Windows media controls. Click the cover and a small player slides open
with the controls, the time and, for Spotify, the lyrics. Its colours come from the cover, so it always
matches the music.

## Features

### A player on the taskbar

<img src="docs/player.png" alt="The player with its controls" width="452">

Previous, play / pause, next, a seek bar, the volume of the app that is playing, shuffle and repeat.
Click the cover to open or close the player, drag it along the taskbar to put it where you like.

### Colours from the cover

<img src="docs/chameleon.png" alt="The player in the colours of four different covers" width="452">

Every song paints the player in its own colours.

### Design your player

<img src="docs/design-flame.gif" alt="A player with an animated flame along its bottom edge opening on the taskbar" width="452">

Open **Options** in the tray menu to make the player your own: pick the background and button colours
(or keep *Chameleon*, the colours from the cover) and put PNG, JPG or GIF images on it. Drag, resize and
rotate them on a big preview and drag them up or down in the image list to choose which one is in front;
they always stay behind the buttons and GIFs play only while the player is open. Save up to 5 players and
switch between them.

<img src="docs/design-options.png" alt="The Options window with a big preview of the player, colour swatches and the image list" width="640">

<img src="docs/designs.png" alt="Three saved players: a flame, purple with notes, and a rainbow" width="452">

### Lyrics, like in Spotify

<img src="docs/lyrics.gif" alt="Lyrics changing line by line in the player" width="420">

Turn on **Lyrics** in the tray menu and the open player shows the line being sung. The next line comes
up from below as the old one fades away. Move the mouse over the player to get the buttons back.
Works with Spotify.

### The song's name at a glance

<img src="docs/title-card.png" alt="The song's name above the cover" width="220">

Rest the mouse on the cover for a second.

### Quick controls

- **Mouse wheel on the cover:** next / previous song
- **Middle click on the cover:** play / pause
- **Mouse wheel on the time bar:** skip 5 seconds; **on the speaker:** volume; **click the speaker:** mute
- **Tray icon:** left click plays / pauses, the mouse wheel changes the volume

### Tray menu

<img src="docs/tray-menu.png" alt="The tray menu" width="232">

Right-click the tray icon for Options, Lyrics, Start with Windows, the side of the taskbar (left or right),
the language, Restart and Close.

## Install

1. Download **MMP.zip** from the [latest release](../../releases/latest).
2. Extract it and double-click **MMP Launcher.bat**.
3. When it says "Installation complete!", start the app with the **mey music player** shortcut on your desktop.

The launcher also installs the .NET Desktop Runtime 8 if it is missing. MMP starts with Windows from then on
(you can turn that off in the tray menu).

Works on Windows 10 (version 1809 or newer, 64-bit) and Windows 11.

**Updates** are built in: the app checks for a new version when it starts and once a day, shows what's new
and updates itself with one click.

## Questions

**Windows says "Windows protected your PC" / unknown publisher.**
The app is not signed with a paid certificate. Choose *More info* and then *Run anyway*.

**Which apps does it work with?**
Anything that shows up in the Windows media controls (the media box in the volume / quick settings flyout):
Spotify, browsers playing YouTube, X, SoundCloud and so on, and most music and video players.
Lyrics are for Spotify only.

**The volume buttons don't move the slider inside Spotify.**
MMP changes the app's volume in the Windows volume mixer, which sounds the same. In a browser, all tabs
share one volume. *Good to know:* in Firefox the volume can't be changed from MMP (Firefox doesn't tell
Windows which app it is), but everything else works.

**What does it send over the internet?**
It asks GitHub for new versions. With Lyrics on, the title, artist, album and length of the Spotify song
go to [LRCLIB](https://lrclib.net) to find its lyrics; found lyrics are kept in `%LOCALAPPDATA%\MMP\lyrics`.
Nothing else.

**It says .NET is missing.**
Run **MMP Launcher.bat** again.

**My taskbar is on the side or on another screen.**
For now MMP sits on the taskbar of the main screen, at the bottom or the top.

**Something doesn't work.**
MMP writes unexpected errors to `%LOCALAPPDATA%\MMP\error.log`. It stays on your PC; attach it when you
[open an issue](../../issues).

**How do I remove it?**
Turn off *Start with Windows* and choose *Close* in the tray menu, then delete the `%LOCALAPPDATA%\MMP`
folder (the app, its settings and your saved players) and the shortcut on your desktop.

## Languages

English, Türkçe, Español, Français, Deutsch, Português (Brasil), Русский, 简体中文, 日本語, العربية, हिन्दी.
Pick one in the tray menu under *Language*.

## For developers

```
src/        the app (C# / WPF)
launcher/   MMP Launcher.bat + launcher.ps1 (what users download)
tools/      developer helpers
docs/       the pictures in this README
.github/    the workflow that builds the releases
```

`tools/Dev Build.bat` builds the code in `src/` on your PC and starts it (installs the .NET 8 SDK if needed).
`tools/debug.bat` starts the installed app with a debug window.

## License

[MIT](LICENSE) © weojiro
