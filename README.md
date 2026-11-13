# Raccoon Audio

A small Windows 11 app that plays your music in a Discord voice channel through Raccoon Man, the bot. You pick one
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
- **It turns that program down on your computer while you're live**, unless you untick the box. Otherwise you'd hear
  the music twice: once from the program and, a moment later, from Raccoon Man. It does this by dragging the
  program's slider in Windows' volume mixer almost to nothing (and turning up what it sends by the same amount, so
  the channel hears it at full volume). It puts the slider back when the stream ends, when the app closes, and, if
  the app was killed mid-stream, the next time it starts. It touches no other program's volume.
- **It sends the title and artist of what's playing** (from Windows' own media controls), so Raccoon Man can show it
  and say a line about it in the voice channel's chat. Turn music tracking off in `/me` in Discord and he won't.
- **Your sound and song titles go to Raccoon Man's server and nowhere else.** No analytics, no accounts, no logging
  in to Spotify or anything.
- **It asks GitHub whether there's a newer version**, when it starts and about once a day while it's running. That
  is one request, to `https://api.github.com/repos/Stonecoled5/RaccoonAudio/releases/latest`, and all it carries is
  the app's name (`User-Agent: RaccoonAudio`) and the kind of answer it wants. It doesn't say which version you
  have, what your computer is called, or anything else about you or your computer. GitHub does see the internet
  address the request comes from, as any website you visit does. If the request fails the app says nothing and
  tries again in an hour.
- **It only updates itself the way you've chosen.** By default a newer version found when the app starts is installed
  then and there; turn off **Update automatically when the app starts** under **Advanced** and nothing is downloaded
  or changed until you press **Update**. Either way it downloads that version's zip from this repository's Releases page on
  GitHub and from nowhere else. See [Updating](#updating) for exactly what happens and what it does and doesn't
  protect you from.
- **Your computer's key** (what proves this computer is yours) is stored encrypted for your Windows account, and is
  never shown or written to a log.
- **Playing a music service's audio to other people is against that service's terms.** That's between you and them.

## Getting it

1. Download the latest `RaccoonAudio-win-x64.zip` from the [Releases](../../releases/latest) page and unzip it anywhere.
2. Run `RaccoonAudio.exe`. **Windows will warn you** ("Windows protected your PC") because the app isn't signed with a
   paid certificate. Choose **More info**, then **Run anyway**. If you'd rather not trust a download, build it
   yourself (below): it's a couple of thousand lines.

It needs Windows 11. Nothing else has to be installed.

## Updating

The app looks for a newer version as soon as it starts, and about once a day after that.

- **When it starts**, a newer version is installed then and there, without asking: that's **Update automatically when
  the app starts** under **Advanced**, on unless you turn it off. If you're live (or it can't update itself), it asks
  instead, with a window in front of everything else.
- **With that setting off**, and for what it finds while it's running, it asks with that window, once per version:
  **Update now**, or **Later**.
- Either way, a newer version also waits in a line at the top of the window with an **Update** button, and in an
  **Update to version …** entry when you right-click the icon by the clock.

You can also look whenever you like: **Advanced** shows the version you have and a **Check for updates** button.

When you press **Update**, the app:

1. Downloads that version's `RaccoonAudio-win-x64.zip` from this repository's Releases page. It only takes a release
   whose download is this app's zip, under that version, in this repository on `github.com`, and it only follows
   the download to GitHub's own file servers (`*.githubusercontent.com`). Anything else is refused.
2. Checks the download against the SHA-256 checksum GitHub publishes for that file. If they differ, the download is
   thrown away.
3. Takes `RaccoonAudio.exe` out of the zip and starts it once, beside the one you have, to see that it runs on your
   computer. If it doesn't, it stops there.
4. Moves the running one aside as `RaccoonAudio.exe.old` and puts the new one in its place.
5. Starts the new version and closes. The new version deletes `RaccoonAudio.exe.old`.

It takes about a minute, most of it the download (about 70 MB). It won't update while you're live: stop the stream
first. Your settings, your pairing and **Start with Windows** aren't touched: they're kept in
`%AppData%\RaccoonAudio` and in Windows, not in the app's file.

If anything goes wrong at any step (no connection, a download that's cut off or doesn't match its checksum, a new
version that won't start), the version you had is put back if it had been moved, keeps running, and tells you what
happened. You can always update by hand instead: download the zip, close the app, and replace `RaccoonAudio.exe`.

**Version 0.1.0 has no Update button.** If that's what you have, replace it by hand this once; every version after
it can update itself.

**What the checksum is worth, and what it isn't.** It catches a download that was damaged, cut short or swapped for
something else on its way to you. It can't tell you who published the release. The app isn't signed, and the
checksum comes from the same GitHub account as the download, so an update is exactly as trustworthy as that account:
whoever can publish a release in this repository decides what **Update** installs. That's the same trust you gave it
when you first downloaded it and chose **Run anyway**. If you'd rather not extend it, don't press **Update**: read
what changed and build it yourself (below).

## Pairing your computer (once)

1. In Discord, run `/me`, open **Audio app…** and press **Get a pairing code**. Only you can see it. It works once and
   runs out after ten minutes. Don't share it: whoever uses it can play audio as you.
2. In the app, press **Pair with Raccoon Man** and type the six characters.
   (The bot can be renamed: the app uses whatever name he gives when it connects.)

You can pair up to three computers. To unpair one, press **Unpair** under **Go live** in the app (it shows once a computer is paired): it
comes off your list in `/me` too. (If the bot can't be reached right then, the app says so; finish in `/me` →
**Audio app…** in Discord.) You can also unpair any of them from `/me`.

## Playing music

1. Start your music player and pick it under **Program to stream** (press **Refresh** if it isn't listed; it has to
   have made a sound). The line under the list says whether that program is making sound right now.
2. Join a voice channel in Discord.
3. Press **Go live**. Raccoon Man joins your channel and plays it.
4. Press **Stop** when you're done. Leaving the voice channel or closing the app ends it too, straight away, so
   someone else can stream.

With **Don't play it on this computer while I'm live** ticked (it is to begin with), you hear the music through
Raccoon Man like everyone else, about a second behind your player. Untick it to hear your player directly instead;
then mute Raccoon Man for yourself in Discord (right-click him → **Mute**), or you'll hear it twice.

While you're live, people in the channel can use `/radio` in that channel's chat:

- **Skip**: when a vote passes, the app either skips the song or just tells you. Choose which under **When the channel
  votes to skip**.
- **Pause / resume**: the app presses pause or play in your music player.
- **Requests**: each `/radio add` pops up by the clock and waits in the **Requests** list, which fills the right side
  of the window (make the window bigger for a longer list; it scrolls past that). The arrow in the strip between the
  two sides folds the list away, narrowing the window, and brings it back; folded, it shows how many are waiting, and
  the app remembers which you picked. The same list is under
  **Requests** when you right-click the icon by the clock, and the icon gets a yellow mark while there are requests
  you haven't looked at.
  - **Open in Spotify** opens Spotify on a search for what they typed, or on the song itself if they sent a Spotify
    link. Nothing is looked up anywhere: adding it to your queue is yours to do in Spotify.
  - **Done** takes a request off the list, played or not. Each person can have 20 requests waiting with you, and
    ticking one of theirs off is what gives them room for another. **Clear all** empties the list.
  - The list is kept until you tick things off or close the app. A new stream starts everyone's count from nothing.
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
| You hear the music twice | Tick **Don't play it on this computer while I'm live**, or mute Raccoon Man for yourself in Discord. |
| Your player is silent on your computer after a stream | It should come back the moment the stream ends. If the app was killed mid-stream, start it again and it puts the volume back. Failing that, open Windows' volume mixer and drag that program's slider back up. |
| It says **Reconnecting…** now and then | Raccoon Man restarts at least once a day. The app reconnects and carries on by itself within a few seconds. |
| No song titles | That program doesn't tell Windows what it's playing. The stream still works. |
| Pause or skip doesn't do anything | Same reason: there's no pause or skip button for the app to press in that program. |
| "The bot no longer knows this app" | It was unpaired in `/me`. Pair it again. |
| "The app is older than the bot" | Press **Update** in the window, or right-click the icon by the clock. If there's no Update button (version 0.1.0 has none, or GitHub can't be reached), download the latest version from the Releases page and replace `RaccoonAudio.exe`. |
| "Stop the stream first, then press Update" | Updating closes the app and opens the new version, which would end your stream. Press **Stop**, then **Update**. |
| "Couldn't update. …" | The rest of the line says what went wrong: GitHub couldn't be reached, the download was cut off, it didn't match its checksum, the app's folder can't be written to, or the new version wouldn't start. Nothing was changed and you're still on the version you had. Try again later. If it says it can't write to its folder, move the app somewhere you can save files (Documents, say). Failing all that, update by hand: download the zip from the Releases page, close the app, and replace `RaccoonAudio.exe`. |
| "GitHub is turning requests away for now" | GitHub limits how often one internet address can ask it things, and something else on your network used that up. Try again in an hour. |
| A `RaccoonAudio.exe.old` next to the app | The version you had before an update. The new one deletes it when it starts. If it's still there after that, delete it yourself. |

## Building it yourself

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download) (or newer) on Windows.

```
dotnet test
dotnet publish src/RaccoonAudio -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

How it's put together:

- `src/RaccoonAudio.Core`: everything that isn't Windows-specific and can be tested on its own: the protocol
  (`Protocol.cs`, read from `protocol/stream-protocol.json`), the conversation with the bot including reconnecting
  (`StreamClient.cs`), turning sound into 20 ms Opus packets (`OpusFramer.cs`), turning sound back up when the
  program has been turned down (`Pcm.cs`), and updating: reading what GitHub says the newest release is, deciding
  whether to trust it, downloading and checking it, and swapping it in (`Updates.cs`).
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
  writes down how much it got. Sends nothing anywhere. Add `--quiet <program name>` to turn that program down
  meanwhile, the way a stream does, and check what's captured is still at full volume.
- `RaccoonAudio.exe --profile <folder>`: the real app, keeping its settings and key in that folder instead of the
  usual place, and able to run beside your ordinary copy. For trying things without touching your own pairing.
- `node scripts/local-server.mjs <path to RaccoonBot> [port] [requests]`: runs the bot's real stream server on this
  computer with a stand-in for voice, and prints an address and a pairing code. With a number of requests, it sends
  that many made-up song requests once the app goes live, and says when they're ticked off. Then
  `RaccoonAudio.exe --connect-test <address> <code> <seconds> report.txt` pairs, signs in, goes live, streams a test
  tone and stops. To try the real app against it, start it with `--profile <folder> --server <address>`.
- `node scripts/fake-release.mjs <zip> <version> [port] [how it goes]`: a stand-in for GitHub's releases on this
  computer, for trying an update without publishing one. Build an older and a newer copy (the `dotnet publish` line
  above with `-p:Version=0.1.0` and `-p:Version=0.1.1`, to two folders), zip the newer `RaccoonAudio.exe` as
  `RaccoonAudio-win-x64.zip`, give the stand-in that zip and `0.1.1`, and start the older copy with
  `RaccoonAudio.exe --profile <folder> --update-from http://127.0.0.1:8123/latest`. It offers the update like a real
  one. The last argument makes it go wrong on purpose: `wrong-hash`, `cut-off`, `missing`, `elsewhere`,
  `no-checksum`, or `checksum-file` for a release with only its own `.sha256` file. The stand-in prints every request
  with its headers, which is how to see exactly what the app sends.
  - `--update-from` only takes an address on this computer, and it's only ever a command-line flag. There is no
    setting for where updates come from, on purpose: one that could be changed would be a way to put someone else's
    program on a person's computer.
  - Add `--update-test <report file>` and the app takes whatever the stand-in offers without being clicked, writes
    down what it told the person, and closes. It does nothing without `--update-from`.

## Releasing

Pushing a tag like `v0.1.1` builds the app on GitHub and attaches the zip, and a `.sha256` file holding its checksum,
to a release (`.github/workflows/release.yml`).

Every copy of the app that's out there offers that release to its owner within a day, so:

- **The tag has to be `v` and three numbers** (`v0.1.1`), higher than the last. The app ignores anything else, and
  it ignores drafts and pre-releases.
- **The zip has to be called `RaccoonAudio-win-x64.zip` and have `RaccoonAudio.exe` at its top**, which is what the
  workflow makes. Don't rename or re-upload it by hand.
- **Try the update before you tag**, from the last released version to the new one, with `scripts/fake-release.mjs`
  (above).
- **Keep two-factor sign-in on the GitHub account that publishes releases, and don't hand out write access to this
  repository.** The app isn't signed, so that account is the only thing standing between the people who use this
  and whatever a release contains.
