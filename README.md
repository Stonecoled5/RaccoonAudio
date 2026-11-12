# Raccoon Audio

A small Windows 11 app that plays your music in a Discord voice channel through raccoon man, the bot. You pick one
program on your computer (Spotify, usually), press **Go live**, and everyone in the voice channel you're in hears it.
They get `/radio` to see what's on, vote to skip, pause it, and ask you for songs.

It sits by the clock as a coloured dot: grey when it's idle, green when it's connected, **red while it's live**.

## What it sends, and where

Read this before you use it.

- **It sends the sound of one program, and only that program.** Whatever that program plays goes out to the whole
  voice channel: music, a podcast, a video. It never sends the rest of your computer's sound: no notifications, no
  calls, no other apps. It does this with Windows 11's per-program capture, and if your Windows can't do that the app
  says so and does nothing, rather than falling back to capturing everything.
- **Only while you're live.** Capturing starts when a stream starts and stops the moment it ends, for any reason.
- **It sends the title and artist of what's playing** (from Windows' own media controls), so raccoon man can show it
  and say a line about it in the voice channel's chat. Turn music tracking off in `/me` in Discord and he won't.
- **It goes to raccoon man's server and nowhere else.** No analytics, no accounts, no logging in to Spotify or
  anything. It doesn't update itself.
- **Your computer's key** (what proves this computer is yours) is stored encrypted for your Windows account, and is
  never shown or written to a log.
- **Playing a music service's audio to other people is against that service's terms.** That's between you and them.

## Getting it

1. Download the latest `RaccoonAudio-win-x64.zip` from the [Releases](../../releases/latest) page and unzip it anywhere.
2. Run `RaccoonAudio.exe`. **Windows will warn you** ("Windows protected your PC") because the app isn't signed with a
   paid certificate. Choose **More info**, then **Run anyway**. If you'd rather not trust a download, build it
   yourself (below): it's about 1,500 lines.

It needs Windows 11. Nothing else has to be installed.

## Pairing your computer (once)

1. In Discord, run `/me`, open **Audio app…** and press **Get a pairing code**. Only you can see it. It works once and
   runs out after ten minutes. Don't share it: whoever uses it can play audio as you.
2. In the app, press **Pair this computer…** and type the six characters.

You can pair up to three computers. To unpair one, use `/me` → **Audio app…** in Discord, and **Unpair this
computer** under **Advanced** in the app.

## Playing music

1. Start your music player and pick it under **Program to stream** (press **Refresh** if it isn't listed; it has to
   have made a sound).
2. Join a voice channel in Discord.
3. Press **Go live**. Raccoon man joins your channel and plays it.
4. Press **Stop** when you're done. Leaving the voice channel or closing the app ends it too.

While you're live, people in the channel can use `/radio` in that channel's chat:

- **Skip**: when a vote passes, the app either skips the song or just tells you. Choose which under **When the channel
  votes to skip**.
- **Pause / resume**: the app presses pause or play in your music player.
- **Requests**: `/radio add` pops up here with who asked and for what. Playing it is up to you.
- **Spotify Jam**: paste your Jam link and press **Share**, and he posts it so people can add songs from their own
  Spotify. Hosting a Jam needs Spotify Premium, and joining one from elsewhere probably does too.

Only one person can stream in the server at a time.

## If something's wrong

| What you see | What's going on |
|---|---|
| "Windows protected your PC" | The app is unsigned. **More info** → **Run anyway**. |
| "This needs Windows 11" | Per-program capture arrived in Windows 11. On Windows 10 the app won't run a stream. |
| "Get in a voice channel first" | He plays in the voice channel you're in. Join one, then Go live. |
| "… is already streaming" | One stream at a time. Wait for theirs to end, or ask them to stop. |
| "… isn't running" | Start the program you picked, or pick another. |
| Your program isn't in the list | It only shows programs that have sound open. Play something, then **Refresh**. |
| It says **Reconnecting…** now and then | Raccoon man restarts at least once a day. The app reconnects and carries on by itself within a few seconds. |
| No song titles | That program doesn't tell Windows what it's playing. The stream still works. |
| Pause or skip doesn't do anything | Same reason: there's no pause or skip button for the app to press in that program. |
| "The bot no longer knows this app" | It was unpaired in `/me`. Pair it again. |
| "The app is older than the bot" | Download the latest version. |

## Building it yourself

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download) (or newer) on Windows.

```
dotnet test
dotnet publish src/RaccoonAudio -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

How it's put together:

- `src/RaccoonAudio.Core`: everything that isn't Windows-specific and can be tested on its own: the protocol
  (`Protocol.cs`, read from `protocol/stream-protocol.json`), the conversation with the bot including reconnecting
  (`StreamClient.cs`), and turning sound into 20 ms Opus packets (`OpusFramer.cs`).
- `src/RaccoonAudio`: the Windows app. Capturing one program's sound (`Audio/ProcessLoopbackCapture.cs`), finding
  programs with sound (`Audio/AudioPrograms.cs`), Windows' media controls (`Media/MediaWatcher.cs`), settings and the
  encrypted key (`Settings.cs`), and the tray icon and window (`TrayApp.cs`, `MainForm.cs`).
- `tests/RaccoonAudio.Tests`: tests for the core, against a fake connection.

### The protocol copy

`protocol/stream-protocol.json` is **the bot's file**, copied here. It's the only place the app reads the protocol
from. If the bot's copy changes, copy it over this one; a test fails when the two differ and the bot's repository is
next to this one (or `RACCOONBOT_PROTOCOL` points at its copy). Don't edit it here.

### Checks you can run without Discord

These are for development. None of them touches your saved key.

- `RaccoonAudio.exe --list-programs report.txt`: the programs that have sound open.
- `RaccoonAudio.exe --capture-test <process id> <seconds> report.txt`: captures that program's sound, encodes it, and
  writes down how much it got. Sends nothing anywhere.
- `node scripts/local-server.mjs <path to RaccoonBot> [port]`: runs the bot's real stream server on this computer
  with a stand-in for voice, and prints an address and a pairing code. Then
  `RaccoonAudio.exe --connect-test <address> <code> <seconds> report.txt` pairs, signs in, goes live, streams a test
  tone and stops. To try the real app against it, put the address under **Advanced**.

## Releasing

Pushing a tag like `v0.1.0` builds the app on GitHub and attaches the zip to a release (`.github/workflows/release.yml`).
