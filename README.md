# mey music player

A small media player that lives on the Windows taskbar. It shows the cover of what is playing
(Spotify, YouTube in the browser, any app that uses the Windows media controls), opens into a player with
previous / play / next, a seek bar, volume and mix / loop, and takes its colors from the cover.

Works on Windows 10 (version 1809 or newer, 64-bit) and Windows 11.

## Install

1. Open the [Releases](../../releases) page and download **MMP.zip** from the newest release.
2. Extract it and double-click **MMP Launcher.bat**.
3. When it says "Installation complete!", start the app with the **mey music player** shortcut on your desktop.

The launcher installs the .NET Desktop Runtime 8 (only if it is missing) and the latest `MMP.exe`.
Windows may show an "unknown publisher" warning the first time: choose *More info* and then *Run anyway*.

## Updates

The app checks for a newer release when it starts and then once a day. If there is one, a window shows the
version and what's new, with **Update** and **Later**. If the app says .NET is missing, run
`MMP Launcher.bat` again.

## Using it

- Click the cover to open or close the player. Drag the cover along the taskbar to move it.
- Mouse wheel on the cover: forward plays the next song, back the previous one. Middle click on the cover plays / pauses.
- Mouse wheel on the time bar seeks, on the speaker changes the volume, a click on the speaker mutes.
- Tray icon: left click plays / pauses, mouse wheel changes the volume. Right click: Options, Start with Windows,
  Position (Right / Left), Language, Close.
- 11 languages: English, Türkçe, Español, Français, Deutsch, Português (Brasil), Русский, 简体中文, 日本語,
  العربية, हिन्दी.

## Publishing a new version

1. Change the code in `src/` and upload it to this repo.
2. **Releases → Draft a new release.** Create a tag like `v0.2.0` (always `v` + three numbers).
3. Write the release notes with one section per language, for example:

   ```
   ## en
   - Added something
   - Fixed something

   ## tr
   - Bir şey eklendi
   - Bir şey düzeltildi
   ```

   The update window shows the section of the user's language, or `en` if there is none.
4. Click **Publish release**. After a few minutes GitHub builds the app and attaches `MMP.exe`,
   `MMP.exe.sha256` and `MMP.zip` to the release; only then do users see the update.

## Building it yourself

`tools/Dev Build.bat` builds the code in `src/` on your PC and starts it (installs the .NET 8 SDK if needed).
`tools/debug.bat` starts the installed app with a debug window.

```
src/        the app (C# / WPF)
launcher/   MMP Launcher.bat + launcher.ps1 (what users download)
tools/      developer helpers
.github/    the workflow that builds the releases
```
