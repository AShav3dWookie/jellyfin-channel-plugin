# Testing guide

How to test the Linear TV plugin and its tooling, from a clean machine to the NAS.

**Start with [Channels — the main test](#channels--the-main-test).** That is the feature.
Sections 2–9 test the tooling and the building blocks underneath it.

Every command here was run on the development machine (Windows 11, Docker Desktop 27.4, Git
Bash) on 2026-09-30, unless it is marked **not yet run**.

---

## Quick reference

| What | Value |
|---|---|
| Test server | **http://localhost:8097** (from another device: `http://<pc-ip>:8097`) |
| Username | **`dev`** |
| Password | **`dev`** |
| Account type | Administrator. It is shown on the login screen as a user tile. |
| API key | In `artifacts/.testserver.env`, written by `./x init` |
| **Live TV (channels and guide)** | **http://localhost:8097/web/#/livetv** |
| Plugin settings (create channels here) | http://localhost:8097/web/#/configurationpage?name=Linear%20TV |
| Plugin list | http://localhost:8097/web/#/dashboard/plugins |
| Channel import/export | `./x channels` (help with no arguments), or the endpoints `POST /LinearTv/Channels/Import` and `GET /LinearTv/Channels/Export`; see [channel-format.md](./channel-format.md) and tests I–K |
| Server logs | `./x logs` (Ctrl+C to stop following) |
| Help | `./x` with no arguments |

The credentials are set by `./x init` (see [`x`](../x)). The test server is disposable and runs
only on your machine. It shares nothing with your NAS Jellyfin.

### Test library item IDs

IDs are derived from file paths, so they are the same every time the library is rebuilt from
the same media.

| Episode | Length | Item ID |
|---|---|---|
| Clock Show S01E01 | 30 min | `b95ed1c4c8ba04c3e7fd27a473c5ff63` |
| Clock Show S01E02 | 30 min | `8a44e5a9ff4bff462928794413c2cf70` |
| Clock Show S01E03 | 30 min | `e9e6bbc7a59c9051ff8734f618a90a3b` |
| Clock Show S01E04 | 30 min | `cc90316937a20abc29eeef3c2f6fd270` |
| Clock Show S01E05 | 30 min | `fa8d3bdc0b2c4e15e41943e59de9fa35` |
| Clock Show S01E06 | 30 min | `5b8791d7490e6364d739b2f7cab59402` |
| Short Show S01E01 | 30 s | `78d9a83cb85aead2d3f3b3a012eedf61` |
| Short Show S01E02 | 30 s | `7b49daac0adc20775406f852d52a28ae` |
| Short Show S01E03 | 30 s | `68f74b45744ed6fb718be8950eb09990` |
| Short Show S01E04 | 30 s | `b088343523cf97ccbdc2a3744fe8f4fb` |
| Short Show S01E05 | 30 s | `4b7bed30562981c3e4d55ea930121786` |
| Short Show S01E06 | 30 s | `bd541ffcd3b466c856d95954dde798a0` |
| Mixed Show S01E01 (H.264 · AAC · MKV) | 30 s | `b406b88fe29310989db6278a6769e50b` |
| Mixed Show S01E02 (HEVC · AAC · MKV) | 30 s | `d0ab143e0a99f4ee757e7e7672be255a` |
| Mixed Show S01E03 (H.264 · AC3 · MKV) | 30 s | `f86af165024fbefc1a6dd78edac3862d` |
| Mixed Show S01E04 (AV1 · AAC · MKV) | 30 s | `496840668a6a5babb66737e598f88d3e` |
| Mixed Show S01E05 (XviD · AC3 · AVI) | 30 s | `241d3d6fca92af5b547a8154e6c5e7d9` |
| Mixed Show S01E06 (HEVC 720p · E-AC3 · MKV) | 30 s | `c42ca1b0e67d648c46c33912803b03c5` |
| Mixed Show S01E07 (H.264 720p · AAC · MKV) | 30 s | `65818c61f38d9165488e95de6f534b61` |

`./x init` prints this list too. If your IDs ever differ, trust its output.

---

## 0. Before you start

**You need:** Docker Desktop, running, and Git Bash. Nothing else — no .NET SDK, no ffmpeg, no
curl setup. Everything runs in containers.

**Check Docker is up:**

```bash
docker info --format '{{.ServerVersion}} {{.OSType}}'
```

Expect a version and `linux`, for example `27.4.0 linux`. An error means Docker Desktop isn't
running: start it and wait for the whale icon to settle.

**Open Git Bash in the repo:** in Explorer, right-click the `jellyfin-channels` folder and
choose *Open Git Bash here*. Or, in any Git Bash window:

```bash
cd ~/git/jellyfin-channels
```

Run every command in this guide from that folder, and in **Git Bash**, not PowerShell or
cmd. `./x` is a bash script.

### What lives where

```
artifacts/            build output, release packages, spike results   (safe to delete)
  plugin/             the DLL the test server loads
  .testserver.env     the test server's API key
testdata/             test server state and media                     (safe to delete)
  media/              the 12 generated clips, ~400 MB
  jellyfin/           the test server's config and database, ~230 MB
```

Both folders are gitignored. Deleting them loses nothing that `./x` can't rebuild.

---

## 1. First-time setup

Skip to section 2 if the test server is already running (`docker compose ps` shows
`jellyfin` as `running`).

```bash
./x media      # ~5–7 min: generates the 12 test clips
./x deploy     # ~1 min first time: builds the plugin, starts the test server
./x init       # ~1 min: wizard, library, login visibility, API key
```

The first `./x deploy` also downloads the SDK image (~1 GB) and the Jellyfin image (~1.7 GB),
which can add several minutes on a slow connection. That's a one-off.

**`./x init` should end like this:**

```
==> Completing first-run wizard (user dev / password dev)
==> Showing the dev user on the login screen
==> Creating Shows library (remote metadata disabled)
==> Waiting for scan and probe
==> Test library
    b95ed1c4c8ba04c3e7fd27a473c5ff63  Clock Show / Clock Show - S01E01  1800s
    ...
    bd541ffcd3b466c856d95954dde798a0  Short Show / Short Show - S01E06  30s
==> API key saved to artifacts/.testserver.env - log in at http://localhost:8097 as dev / dev
```

Check that all 12 episodes are listed, under **Clock Show** and **Short Show**. If you see
real TV titles instead ("The 11 O'Clock Show", "Pilot"), the library wasn't created by `init`;
see Troubleshooting.

`./x init` is safe to re-run. It skips anything already done.

---

## Channels — the main test

**Purpose:** channels you can see in the guide, tune into part-way through a programme, and
flick between.

### Demo channels already on the test server

Three channels were created on the test server while building this, so there is something
to look at straight away:

| No. | Name | Content | Order |
|---|---|---|---|
| 101 | Clock TV | Clock Show (6 × 30 min) | In order |
| 102 | Quick Cuts | Short Show (6 × 30 s) | Shuffle |
| 103 | Everything | the whole Shows library | Shuffle |

If you've reset the test server since, they're gone. Create them again in part D.

### A. See them in the guide

1. Log in at http://localhost:8097 as **dev** / **dev**.
2. Open **Live TV**: http://localhost:8097/web/#/livetv, or pick *Live TV* from the home screen
   or menu.
3. Open the **Guide** tab.

**Pass:**
- Channels **101**, **102** and **103** are listed.
- Each has a filled guide. Clock TV shows *Clock Show* episodes on the hour and half-hour,
  in order: S01E01, S01E02, … S01E06, then back to S01E01.
- Times are shown in *your* timezone.
- **The guide runs a week ahead**, or as many days as Jellyfin's *Dashboard → Live TV →
  guide data days* says (7 by default, 14 at most). Before 0.4 it stopped after 2 days.

Verified from the server side: all three channels are listed, the guide has programmes, and
Clock TV's slots run S01E03 → S01E04 → S01E05 → S01E06 on the half-hour. **Not yet looked
at in a browser.**

**Guide length, verified 2026-10-02 (0.4):** Jellyfin asked for 7 days, and Clock TV and
Everything ran to 9 October, a week out. Before, the plugin cut every request at 48 hours,
which on the NAS ended the Spielberg guide on the Sunday. The 30-second-clip channels stop at
3,000 programmes (about a day), which the log reports as *Guide for … stops at 3000
programmes*. On this test server the first refresh after the change took over 20 minutes,
because Jellyfin deleted thousands of old clip programmes at about 2 per second from a
database on a Windows folder shared into Docker. Real channels of normal programmes need about
80–500 programmes a week.

### B. Tune in part-way through

1. In the guide, pick **103 Everything** and play it.
2. Note what the guide says is on, and when it started. For example, *Clock Show S01E05,
   started 17:14*.
3. Read the yellow clock in the picture.

**Pass:** the clock shows how long ago that programme started. Tune in at 17:28:22 to a
programme that started at 17:14:00 and the clock reads about **00:14:20**: the time since the
start, rounded down to a keyframe (every 10 s in the test clips). The episode title at the top
matches the guide.

**Also expected:**
- **No seek bar, no scrubbing.** It's live TV.
- **Sometimes the next programme plays instead of the one the guide shows.** If less than the
  *Join threshold* (default 5 minutes) is left, you start the next programme from the top
  rather than catching the credits. Clock TV at 17:28 shows its 17:30 episode from 00:00.
  Quick Cuts' clips are only 30 s long, so it *always* does this. Set the threshold to 0 on
  the settings page to turn it off.

Verified by tuning through the API, the same calls a client makes, and reading the first
frame. All three channels showed the scheduled episode at the right point:

| Tuned at (UTC) | Channel | Guide said | Picture showed |
|---|---|---|---|
| 16:28:18 | 101 Clock TV | S01E03, 1m42s left | S01E04 at 00:00 (join threshold) |
| 16:28:21 | 102 Quick Cuts | a clip with 9s left | the next clip at 00:00 (join threshold) |
| 16:28:22 | 103 Everything | S01E05 from 16:14:00 | **S01E05 at 00:14:20** |

### C. Flick between channels

Change channel the way your client does it. On Android TV, that's the guide, or channel
up/down while watching; the exact controls vary by app version. In the web client, go back to
the guide and pick another channel.

**Pass:** each channel starts within a few seconds, showing *that* channel's programme at the
right point.

**Not yet run on a real client.** Worth noting how long each change takes.

### D. Create your own channel

This is the channel editor's first run in a real browser. Its saving has only been exercised
through the same API calls it makes.

1. Open the settings page:
   http://localhost:8097/web/#/configurationpage?name=Linear%20TV
2. Click **Add channel**. Enter number `104` and name `My Channel`. Leave *Order* on *In order*.
3. In *Content*, tick **Short Show**, then **Clock Show**. The line under the list should read
   *Plays in this order: Short Show → Clock Show*. The order you tick is the play order.
4. Click **Save channel**.

**Pass:**
- The editor closes and **104 My Channel** appears in the channel list with its content
  summary.
- Within a few seconds **104** appears in the Live TV guide, without restarting anything:
  saving triggers a guide refresh.
- In the guide, 104's programmes follow your order: the six Short Show clips, then the six
  Clock Show episodes, then round again. Which part is on *now* depends on where the channel
  is in its loop. Every channel loops continuously from a fixed start date, so it's already
  "been running", and a brand-new channel won't necessarily start at the top.

Then try the rest of the editor:
- **Edit** — change the name, then save. The guide updates.
- **Delete** — asks for confirmation, then the channel leaves the guide after the refresh.
- **Refusals** — a duplicate number, a blank name, or no content ticked are each refused
  with a message.

To recreate the demo channels: **101 Clock TV** (Clock Show, in order), **102 Quick Cuts**
(Short Show, shuffle), **103 Everything** (tick *Shows* under *Whole libraries*, shuffle).

### E. Confirm the video isn't being re-encoded

**Purpose:** the whole point of the project. A channel should never make Jellyfin decode and
re-encode video that the client could take as it is.

**In the web client:** while a channel plays, open the player's ⚙ menu → *Playback Info*.
- **Pass:** under *Transcoding Info*, the only reason is **"The container is not supported"**.
  That is unavoidable in a browser, because browsers can't play MPEG-TS. Jellyfin repackages
  the stream as HLS without touching the video.
- **Fail:** a reason of **"Interlaced video is not supported"**. See below.

**On the server,** the conclusive check: while a channel plays in a browser, run

```bash
docker compose logs jellyfin | grep "TranscodeManager: /usr/lib/jellyfin-ffmpeg/ffmpeg" | tail -1 | grep -oE -- '-codec:v:0 [a-z0-9_]+'
```

- **Pass:** `-codec:v:0 copy`. The video is copied untouched.
- **Fail:** `-codec:v:0 libx264` or `h264_…`. The video is being re-encoded.

**Why this can fail.** Jellyfin marks every third-party Live TV stream as interlaced
(`LiveTvMediaSourceProvider.Normalize()`), and browsers refuse interlaced H.264, which forces a
full transcode. The plugin undoes that in `LinearLiveStream`, relying on the order in which
Jellyfin reads the stream details. **After any Jellyfin upgrade, repeat this check.** If the
interlaced reason comes back, that call order has changed.

Verified on 12.1 (2026-09-30):

| | Transcode reasons | ffmpeg video |
|---|---|---|
| Before the fix | `ContainerNotSupported, InterlacedVideoNotSupported` | re-encoded (as in the reported screenshot) |
| After the fix | `ContainerNotSupported` | **`-codec:v:0 copy`** |

### F. Programmes run on into the next

**Purpose:** a channel is one continuous stream. When a programme ends, the next one starts
straight away, without stopping or returning to the menu.

1. Tune **102 Quick Cuts**. Its clips are 30 s long, so a programme change comes within half
   a minute.
2. Watch the clock reach `00:00:29.9x`.

**Pass:** the next clip starts from `00:00:00` with a different episode title and background
colour, and playback carries on. The change should take a moment at most: the join has a
deliberate 40 ms gap.

**Fail:** playback stops, errors, or returns to the menu. That was the behaviour before
continuity was built, first reported from the web client.

Please try this on **every client you use**. Browsers and the Android TV app read the stream
by different routes (below), and only the browser-like route has been exercised.

**How it works:** the channel's stream joins each programme's remux end to end, and
`TsSplicer` shifts each programme's timestamps to follow on from the last. Without that,
the browser route failed outright. It remuxes with `-copyts`, which can't cope with
timestamps restarting at every programme.

Verified 2026-09-30 on Quick Cuts:

| Route | Used by | Result |
|---|---|---|
| HLS remux by Jellyfin (browser profile, 75 s) | web browsers | 0 non-monotonic timestamps (before: ~25,000), speed 0.99×, all 1,500 frames decode; S01E02 at 0:20 → **S01E06 at 0:00** → S01E06 at 0:20 |
| Direct MPEG-TS (10 s capture) | clients that play TS as-is | all 6,000 frames decode; 0 timestamp discontinuities, 0 corrupt packets |

**Not yet verified in a real browser or on a TV**, only through the API calls a client makes.

### G. Channel changes are quick

**Purpose:** flicking through channels shouldn't mean staring at a spinner.

Pick a channel and count from the click to the first picture. Then change channel a few times.

**Pass:** about **2 seconds** in a browser, and a similar time on a TV. The picture starts at the
right point in the programme (check the clock against the guide, as in B), not further in.

Measured 2026-09-30, through the client API, from pressing play to holding the first playable
video:

| Route | Before | After |
|---|---|---|
| Browser (HLS) | ~31 s | **1.4–2.5 s** |
| Direct (MPEG-TS) | ~1.2 s | ~1.2 s, almost all of it Jellyfin starting its remux of the episode |

**Why it was slow, and what changed.** Jellyfin only lists a live HLS playlist once three
segments exist. Segments can only be cut at keyframes, and 10 s between keyframes is common,
so three segments are 30 s of video. Jellyfin was pacing the stream to real time from its first
frame, so a browser waited those 30 s. Now the plugin paces its own stream (`TsPacer`): the
first **35 s** go out immediately, then it runs at real time. Two knock-on fixes were needed:

- Without Jellyfin's pacing, its 200-second input analysis turned into a 166 s wait. The
  plugin now asks for 5 seconds.
- The pacing also stops a TV pulling minutes of future programmes into its buffer.

**Expected side effect:** the player holds about 35 s of buffer. Playback still starts at the
tune-in point. Jellyfin's web player starts HLS from the beginning of the playlist, not the
live edge, according to its code. **Worth confirming in a real browser:** if the picture
starts noticeably *later* in the programme than the guide says, that assumption is wrong.

### H. Mixed formats — the stream stops cleanly at a format change

**Purpose:** real libraries mix formats, even within one series. Measured on the user's library,
31 of 46 TV series switch codec between episodes. This test shows what a channel does at each
change.

`./x media` generates **Mixed Show**: seven 30-second episodes in different formats. Put it on
a channel **in order** (channel **104 Mixed Formats** on the test server), so every join is a
specific transition:

| Episode | Video | Audio | Container | Join into it tests |
|---|---|---|---|---|
| S01E01 | H.264 360p | AAC | MKV | **resolution only**: H.264 720p → 360p, same format (wraps from E07) |
| S01E02 | HEVC | AAC | MKV | H.264 → HEVC, the commonest switch in the user's TV library |
| S01E03 | H.264 | AC3 | MKV | HEVC → H.264, with an audio codec change |
| S01E04 | AV1 | AAC | MKV | → AV1 |
| S01E05 | MPEG-4 (XviD) | AC3 | AVI | → XviD in AVI |
| S01E06 | HEVC 1280×720 | E-AC3 | MKV | → HEVC with a resolution and audio change |
| S01E07 | H.264 1280×720 | AAC | MKV | HEVC → H.264 |

Each clip names its format on screen, so a frame grab shows what played.

**Try it:** tune channel 104 and watch.
- **Pass:** at a change of video or audio codec, the player stops within a second or two of the
  programme ending and returns to the menu. Tune in again and the new programme plays. The
  E07 → E01 join (resolution only) plays straight through without stopping.
- **Fail:** a frozen picture, a garbled picture, or a long hold on the last frame before stopping.

**Results after the fix, 2026-10-01:** the channel stream ends at the first programme whose
video or audio codec differs from the one tuned in on (`ChannelStream`). It's served from the
plugin's own endpoint, so the end reaches the player straight away.

| | Before | After |
|---|---|---|
| Browser, at a codec change | picture frozen; 2,966 decoder errors | **clean end**: playlist marked ended 1.7 s after tuning, with the whole programme in it; 0 errors |
| Direct, at a codec change | only matching programmes decoded | **clean end** 0.5 s after the last data |
| Resolution-only change (H.264 720p → 360p) | — | **plays straight through**: three 1280×720 segments, then three 640×360; 0 errors |
| Same-format channel (Quick Cuts) | plays through | still plays through: 4 programmes, 0 errors, still paced to real time |

The 1.7 s and 0.5 s depend on the burst: the whole 30-second programme had already been
delivered, so the end arrived at once. A long programme ends at its scheduled end.

**Not verified in a real browser or on a TV:** whether the player redraws cleanly at the
resolution-only join (server-side it's error-free), and what the player does on reaching the
end. Earlier, the web client stopped and returned to the menu at a stream's end.

**Still unfixed:** AV1 programmes have no picture (below). `docs/plan.md` option (c) covers
excluding them.

**History: before the fix (v0.1), this test failed.**

| Route | What happens at a format change | Evidence |
|---|---|---|
| **Browser** | **The picture freezes.** It doesn't stop cleanly. | Jellyfin's ffmpeg sets up one video decoder when the stream starts and feeds every later programme into it: 2,966 decoder errors, output stuck at 25 s after 150 s of playback. The browser only ever saw the one programme matching that decoder. |
| **Direct** (TV that plays MPEG-TS) | Probably garbled or frozen; **not yet tried on a TV** | The stream announces each change, but always as table-of-contents version 0, which players may ignore. A generic decoder decoded only the HEVC programmes: 1,498 of ~4,600 frames. The data for every programme is intact: a decoder started fresh at any point decodes that programme correctly. |
| **AV1, any route** | **No picture at all** | Jellyfin's copy into MPEG-TS turns AV1 video into unrecognised data (`bin_data`); only the audio survives. H.264, HEVC and XviD all survive the same copy. |

**Tuning in again always works**: a fresh tune sets everything up for the programme on air.
The exception is AV1, which has no picture however you tune in.

### I. Import channels from a file

**Purpose:** channels written outside Jellyfin, by hand or by an AI, load straight into the
guide. The format and endpoints are in [channel-format.md](./channel-format.md).

Set up once per shell:

```bash
. artifacts/.testserver.env
AUTH="Authorization: MediaBrowser Token=\"$API_KEY\""
jq() { docker run --rm -i --entrypoint jq linear-tv-sdk:dev "$@"; }
curl -s localhost:8097/LinearTv/Channels/Export -H "$AUTH" > artifacts/channels-backup.json
```

Write `artifacts/try.json`, referring to everything by name:

```jsonc
{
  "channels": [
    { "number": "105", "name": "Pick and Mix", "content": [
      { "type": "show", "title": "clock show" },
      { "type": "episode", "series": "Short Show", "season": 1, "episode": 3 },
      { "type": "episode", "series": "Mixed Show", "season": 1, "episode": 1 },
    ]},
  ]
}
```

1. **Dry run:**
   `curl -s "localhost:8097/LinearTv/Channels/Import?dryRun=true" -H "$AUTH" -H "Content-Type: application/json" --data-binary @artifacts/try.json`
   **Pass:** HTTP 200, `"ok": true`, `"saved": false`, action `create`, and each entry
   `matched` to the right item. The export still lists only the original channels.
2. **Import:** the same command without `?dryRun=true`. **Pass:** `"saved": true`. Within a
   few seconds **105 Pick and Mix** is in the Live TV guide, showing Clock Show S01E01–06, then
   Short Show S01E03, then Mixed Show S01E01.
3. **Refusals:** change a title to `Clok Show`, and add a second channel numbered `105`.
   **Pass:** HTTP 422, `"saved": false`, a `not found` problem for *Clok Show*, and an error
   naming the duplicate number. The export is unchanged. Change the title to `Clock` and the
   problem should suggest *Clock Show*.
4. **Replace:** import `artifacts/channels-backup.json` with `?mode=replace`. **Pass:**
   `removed` lists `105 Pick and Mix`, the four original channels report `unchanged`, and 105
   leaves the guide.
5. **Admins only:** the export without the `-H "$AUTH"` gets **401**. With a non-admin user's
   token it gets **403**.

Verified 2026-10-01, all five steps:

| Step | Result |
|---|---|
| 1. Dry run | 200, every entry matched, nothing saved. A fourth entry, Short Show under a made-up item ID, matched by name with the note *id not on this server* |
| 2. Import | saved; 105 in the guide in the right order (Clock Show E01–06, Short Show E03, Mixed Show E01, then the Short Show series) |
| 3. Refusals | 422 with `Channel number 105 appears more than once`, `Clok Show: not found`, `Clock: not found; did you mean Clock Show?`, and `movie 'Clock Show'` explaining it's a series. Nothing saved. Broken JSON, `mode=wipe` and `version: 2` each got 400 with a message |
| 4. Replace | dry run listed `105 Pick and Mix` as removed and 101–104 as unchanged; after the real import, 105 left the guide within ~15 s |
| 5. Admins only | no token 401, bad token 401, non-admin user 403 (export and import), admin 200 |

### J. Import against your real library

Test I uses five made-up shows. This checks the matching against real titles: remakes,
subtitles, punctuation, collections, and how your files happen to be named.

```bash
./x mirror //TRUENAS/media/Movies //TRUENAS/media/TV
```

This copies the **folder structure only** into `testdata/media/mirror/`, as empty placeholder
files. The shares are listed but no file on them is opened, so read-only access is enough.
Folders you can't list are skipped and logged in `artifacts/mirror-*-skipped.txt`. It then
adds *Mirror Movies* and *Mirror TV* libraries to the test server, with TMDb metadata on, so
the titles, years and collections come out as they do on the NAS. The first scan takes
a while, around 10–30 minutes for ~1,750 files, mostly fetching cast lists. The
placeholders don't play.

Then follow [channel-format.md](./channel-format.md), *Generating channels with an AI*,
against the test server. Ask for channels from the list, dry-run, and read the report.

**Run 2026-10-02** against the user's library: 600 films and 1,154 episodes mirrored, five
unlistable folders skipped. TMDb had identified 146 films and 14 series when it was run.
Four channels and 31 references were written **from memory, not copied from the list**, the
way an AI would, in `artifacts/real.json`.

| | First run | After the fixes |
|---|---|---|
| Matched | 25 of 31 | **30 of 31** |
| `Alien 3` → *Alien³*, `The Accountant 2` → *The Accountant²* | not found; plain `The Accountant` reported as ambiguous | matched. **Bug:** superscripts were dropped, not read as digits |
| `Die Hard 2: Die Harder` → *Die Hard 2*, `Dracula` (1992) → *Bram Stoker's Dracula* | not found, though the right title was suggested | matched as partial titles confirmed by year, with a note |
| `Barry` as a movie | refused: *no movie of that name, but Barry (2018) is a series* | same; a planted mistake |

Matched first time: punctuation (`Alien: Resurrection`, `2001 A Space Odyssey`,
`ET the Extra Terrestrial`, `Daredevil - Born Again`), `Chico and Rita` → *Chico & Rita*,
`Cache` → *Caché*, `Invincible` → *INVINCIBLE*, `Fargo` 1997 → 1996 with a note, two single
episodes, and a whole library.

The matching rules (case, punctuation, accents, `&`, years ±1, ambiguity, suggestions) and the
merge and replace logic are covered by unit tests in `ChannelImporterTests`.

### K. `./x channels` keeps its guarantees

**Purpose:** the script is what makes repeated use safe, whether it's you or an AI running it.
The test server is the default target, so none of this touches the NAS.

| Try | Pass |
|---|---|
| `./x channels library` | Writes `artifacts/channels/test/{library,episodes,libraries}.jsonl` and prints the counts |
| `import` a file that was never checked | Refused, telling you the `check` command to run |
| `check` it, then `import` | Backs up first (and says where), then imports. `list` shows the new channel |
| `import` the same file again | Refused: each check is good for one import |
| `check`, edit the file, then `import` | Refused: the content changed since the check |
| `check` in merge mode, then `import --replace` | Refused: the check was for the other mode |
| `check` a file with a bad title and a missing number | 422 and exit 1, with `X` lines for both |
| `restore` the backup | Dry-runs first, backs up again, then replaces the list; *Removed* names the new channel |
| `--nas list` without `artifacts/.nas.env` | Says what to put in the file |

All verified 2026-10-02.

**One caveat from the run:** about 780 mirrored episodes had no series or episode number. They
had never been refreshed (`DateLastRefreshed: null`), because a redeploy restarted the server
mid-scan. Re-run the scan (*Dashboard → Libraries → Scan*) and they fill in. A server that has
finished scanning, like the NAS, doesn't have this.

---

## 2. Test: the toolchain builds and tests with no host SDK

**Purpose:** proves the whole build runs in containers.

```bash
./x build
./x test
```

**Pass:**

```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

```
Passed!  - Failed:     0, Passed:     3, Skipped:     0, Total:     3
```

Warnings are treated as errors, so `0 Warning(s)` is a real zero.

**What the three tests check:**

| Test | Guards against |
|---|---|
| `ConfigPage_IsEmbeddedUnderTheExactNameThePluginRequests` | A settings page that is missing on Linux because of a filename case mismatch |
| `PluginId_MatchesBuildYamlAndConfigPage` | The plugin GUID drifting between `Plugin.cs`, `build.yaml` and `configPage.html`. Drift gives a settings page that loads but never saves, or an update that installs *alongside* the old version. |
| `Configuration_DefaultsToUtcAndAFiveMinuteJoinThreshold` | Default settings changing unnoticed |

**Optional: prove a failing test fails the command.** Edit
`tests/Jellyfin.Plugin.LinearTv.Tests/PluginIdentityTests.cs`, change `Assert.Equal(5, …)` to
`Assert.Equal(6, …)`, then run:

```bash
./x test; echo "exit code: $?"
```

Expect `Failed! … Failed: 1` and `exit code: 1`. Change it back afterwards.

---

## 3. Test: the plugin loads and its settings page works

**Purpose:** proves the server loads the plugin and that its settings page loads and saves in a
real browser. The save is JavaScript on the page, and it has **only been checked through the
API, never in a browser**. This is the one check here that has never been done.

1. Open **http://localhost:8097**.
2. **Log in:** click the **dev** tile and enter password **`dev`**. If there's no tile and you
   see empty *Username* / *Password* fields instead, type `dev` and `dev`. The tile only
   appears once `./x init` has run.
3. **Plugin list:** open http://localhost:8097/web/#/dashboard/plugins, or go through the menu:
   top-left ☰ → *Dashboard* → *Plugins*. Menu labels can vary slightly between Jellyfin
   versions; the direct link always works.
   - **Pass:** *Linear TV* is listed, version **0.2.0.0**, not marked as failed or disabled.
4. **Settings page:** click *Linear TV*, or open
   http://localhost:8097/web/#/configurationpage?name=Linear%20TV
   - **Pass:** two sections. **Channels** lists the channels, with an *Add channel* button
     (the channels test covers these). **Settings** has two fields, *Join threshold
     (minutes)* and *Schedule timezone*, and a note that the guide length is Jellyfin's own
     setting. Until 0.3 there was also *Guide horizon (hours)*.
5. **Save and persist:**
   1. Set *Join threshold* to `9` and *Schedule timezone* to `Europe/London`.
   2. Click **Save settings**. Jellyfin normally shows a brief confirmation; the reload in the
      next step is the real test.
   3. **Reload the page** (F5).
   - **Pass:** both values are still there after the reload.
6. **Written to disk:** the settings are stored in
   `testdata/jellyfin/config/plugins/configurations/Jellyfin.Plugin.LinearTv.xml`.
   - **Pass:** that file exists and contains `<JoinThresholdMinutes>9</JoinThresholdMinutes>`.
   - The file is **only created on the first save**. Before that, the plugin runs on its
     built-in defaults, and the folder holds only other plugins' files. Its absence before a
     save is normal.
   - Channel definitions live in the same file, under `<Channels>`.
7. **Survives a restart:** run `./x deploy`, wait about 20 seconds, reload the settings page.
   - **Pass:** both values are still there, and so are your channels. Verified for channels:
     they are still listed in Live TV after a restart.

**Server-side cross-check:** `./x logs`, then Ctrl+C. Near start-up you should see:

```
Loaded plugin: Linear TV 0.2.0.0
```

---

## 4. Test: the test media plays and the clock is legible

**Purpose:** proves the harness itself works. Every offset test reads that clock.

1. Log in at http://localhost:8097, open the **Shows** library → **Clock Show** → **S01E01**,
   and press Play.
2. **Pass:**
   - A coloured background with **"Clock Show S01E01"** at the top.
   - A large **yellow clock** in the centre counting up from `00:00:00.000`.
   - A steady tone, whose pitch differs per episode.
   - Seeking moves the clock to match the seek bar.
3. Try **Short Show**: the same layout, 30 seconds long.

If the series are named after real TV shows, see Troubleshooting.

---

## 5. Test: Spike 1 — joining a programme mid-way

**Purpose:** the plugin's whole design depends on Jellyfin starting a copy remux at a requested
offset. This reproduces that result on your machine.

```bash
./x spike1 b95ed1c4c8ba04c3e7fd27a473c5ff63 617
```

That's Clock Show S01E01, starting 617 seconds (10:17) in. Output:

```
==> Requesting 617s (6170000000 ticks), capturing ~15s
==> Extracting first frame
Open artifacts/spike1.png and read the yellow clock:
  ~00:10:17  -> PASS: offset honoured
  ...
```

Open **`artifacts/spike1.png`** in any image viewer.

**Pass:** the clock reads **`00:10:10.000`**, a few seconds *before* what you asked for.

**Why it's early, and why that's correct:** a copy remux can only start on a keyframe, and it
takes the keyframe *before* the target. The test clips have a keyframe every 10 seconds, so a
request always lands on the 10-second mark at or before it. Viewers therefore join slightly
early and never miss anything.

**Try a few more.** Each run overwrites `artifacts/spike1.png`.

| Command | Clock should read |
|---|---|
| `./x spike1 b95ed1c4c8ba04c3e7fd27a473c5ff63 600` | `00:10:00.000` (exactly on a keyframe) |
| `./x spike1 b95ed1c4c8ba04c3e7fd27a473c5ff63 905` | `00:15:00.000` |
| `./x spike1 b95ed1c4c8ba04c3e7fd27a473c5ff63 1234` | `00:20:30.000` |
| `./x spike1 cc90316937a20abc29eeef3c2f6fd270 61` | `00:01:00.000`, and the title reads **S01E04** |

**Fail** would be a clock reading `00:00:00`, meaning the offset was ignored. That result was
never seen.

**Server-side proof that it's a remux, not a transcode:**

```bash
docker compose logs jellyfin | grep "codec:v:0 copy" | tail -1
```

Look for `-codec:v:0 copy` and `-codec:a:0 copy` in that line. Nothing is decoded or
re-encoded.

---

## 6. Optional: see the bug Spike 1 found

**Purpose:** makes `docs/plan.md` §4.8 concrete. Jellyfin names its remux output file from the
media path, user agent, device and **play session ID**, but not from the start time. Reuse a
session ID and a second request is served the first request's output.

This block reuses one session ID for two different offsets. It runs in a subshell, `( … )`, so
the `MSYS_NO_PATHCONV` setting it needs doesn't leak into your terminal.

```bash
(
  export MSYS_NO_PATHCONV=1
  source artifacts/.testserver.env
  ITEM=5b8791d7490e6364d739b2f7cab59402          # Clock Show S01E06
  SESSION=same-session-$RANDOM                   # deliberately reused for both requests
  for OFFSET in 300 1500; do
    docker compose run --rm -T sdk sh -c "curl -s --max-time 10 -o artifacts/demo-$OFFSET.ts \
      'http://jellyfin:8096/Videos/$ITEM/stream?startTimeTicks=${OFFSET}0000000&container=ts&videoCodec=copy&audioCodec=copy&PlaySessionId=$SESSION&ApiKey=$API_KEY'; true"
    docker compose run --rm ffmpeg -y -i /artifacts/demo-$OFFSET.ts -frames:v 1 /artifacts/demo-$OFFSET.png
  done
)
```

Open `artifacts/demo-300.png` and `artifacts/demo-1500.png`.

**Expected (this is the bug):** **both** read `00:05:00.000`. The request for 25:00 received the
5:00 output. `./x spike1` doesn't show this because it generates a fresh session ID on every
run, and the plugin must do the same.

---

## 7. Test: your Android TV (or any client) against the test server

**Purpose:** confirms real clients can reach and play from the test server. Spike 2 needs this.
**Not yet run** from any real client.

**1. Find your PC's LAN address.** In a Windows terminal (PowerShell or cmd):

```
ipconfig
```

Under the adapter you're connected with (*Wi-Fi* or *Ethernet*), note the **IPv4 Address**, for
example `192.168.1.50`. Ignore any `vEthernet (WSL…)` or Docker adapters.

**2. Check reachability from a phone first.** It's easier to diagnose than a TV. On a phone on
the same network, browse to `http://<pc-ip>:8097`.
- **Pass:** the Jellyfin login page with the **dev** tile.
- **Fail:** see "Other devices can't connect" in Troubleshooting.

**3. Add the server to the TV app.** In the Jellyfin Android TV app, add a server. The option
is on the server-selection screen, and its exact label varies by app version. Enter
`http://<pc-ip>:8097`, then log in as **dev** / **dev**.

The TV app will then hold two servers: your NAS and the test server. Switch between them on
the server-selection screen. Adding one doesn't affect the other.

**4. Play Clock Show S01E01.**
- **Pass:** it plays, with the clock visible.

**5. Useful to note for Spike 2:** open http://localhost:8097/web/#/dashboard (Dashboard home) on
the PC while the TV is playing. The active session shows whether the TV is **direct playing**
or **transcoding**. Note which.

---

## 8. Test: release package and installation on the NAS

> **Releases now come from GitHub (since v0.3).** `./x release` publishes the version in
> `build.yaml` as a GitHub release, built from its tag. Add this repository in Jellyfin once:
> `https://github.com/AShav3dWookie/jellyfin-channel-plugin/releases/latest/download/manifest.json`.
> Then install *Linear TV* from the catalogue and restart; later versions arrive as normal
> plugin updates. To release: bump `build.yaml` and the `.csproj`, commit, tag `vX.Y`, push
> the tag, then run `./x release`. Verified for v0.3: the manifest and zip download without
> logging in, and the checksum matches. The steps below serve a release from this PC instead,
> which is still useful for trying a build on the NAS before tagging it.

**Purpose:** proves the release path from start to finish: build a versioned zip and manifest,
serve them, and install through Jellyfin's own plugin catalogue. **Not yet run** against the NAS.

> **This installs onto your real NAS server.** The plugin does nothing yet (no channels, no
> background work), so it's harmless, and the uninstall steps are at the end. It's still your
> production server, so pick a quiet moment: installation requires a server restart.

**1. Package for your PC's address.** The repository base URL is written **into** the manifest,
and it must be the address the **NAS** uses to reach your PC, not `localhost`:

```bash
rm -rf artifacts/repo artifacts/dist
./x package 0.2.0.0 http://<pc-ip>:8098
```

**Pass:** ends with
`Serve artifacts/repo/ and add http://<pc-ip>:8098/manifest.json as a repository in Jellyfin`.
The `rm` matters: without it you're adding 0.2.0.0 to a manifest that already has an entry
for it, pointing at the old URL.

**2. Serve the repository** from the SDK container. Leave it running:

```bash
docker compose run --rm -p 8098:8098 sdk python3 -m http.server 8098 -d artifacts/repo
```

From another Git Bash window, check it before involving the NAS:

```bash
curl -s http://localhost:8098/manifest.json | head -5
```

Expect JSON beginning `[ { "guid": "437f1b36-…"`.

**3. Add the repository on the NAS.** In the NAS Jellyfin web UI, as an administrator:
1. Open `http://<nas>:8096/web/#/dashboard/plugins/repositories`, or ☰ → *Dashboard* →
   *Plugins* → *Repositories*.
2. Add a repository. Name: `Linear TV (dev)`. URL: `http://<pc-ip>:8098/manifest.json`.
   Save.

**4. Install.**
1. Open the catalogue: `http://<nas>:8096/web/#/dashboard/plugins/catalog`.
2. Find **Linear TV**. Its category is set to Live TV in `build.yaml`, so look there, or use
   the page's search/filter. Open it and choose **Install** for version 0.2.0.0.
3. The Python server in step 2 should log a `GET /linear-tv/linear-tv_0.2.0.0.zip … 200`
   line. That's the NAS downloading the package.
4. **Restart the NAS Jellyfin.** It's needed; plugins load only at start-up.

**Pass:** after the restart, the NAS's *Dashboard → Plugins* lists **Linear TV 0.2.0.0** as
active, and its settings page opens.

You can stop the Python server (Ctrl+C) once it's installed. It's only needed while
installing or updating.

**5. Uninstall afterwards (recommended until the plugin actually does something):**
1. *Dashboard → Plugins* → **Linear TV** → **Uninstall**.
2. Restart the NAS Jellyfin.
3. *Plugins → Repositories*: remove `Linear TV (dev)`. Otherwise the NAS will keep trying to
   reach your PC for updates.

---

## 9. Test: building on the NAS itself

**Purpose:** proves the toolchain is really cross-platform, with nothing Windows-specific. It is
also the first real test of the Linux file-ownership handling. **Not yet run.**

**Needs on the NAS:** SSH access, Docker Engine with the Compose v2 plugin, and bash. Check with:

```bash
docker compose version   # expect "Docker Compose version v2.x"
```

**1. Copy the repo to the NAS** with `git clone`, or over a network share. When copying from
Windows:
- A share copy can lose the executable bit on `x`. Fix with `chmod +x x`, or run it as
  `bash x build`.
- Git checkouts are protected against CRLF line endings by `.gitattributes`. A file copied
  after being edited in a Windows editor might not be. The symptom is
  `/usr/bin/env: 'bash\r': No such file or directory`.

**2. Build and test:**

```bash
./x build && ./x test
```

**Pass:** the same `Build succeeded, 0 Warning(s)` and `Passed: 3` as on Windows.

**3. Check file ownership.** This is the part only Linux can test:

```bash
ls -ln artifacts/build | head -3
```

**Pass:** the owner column shows **your** numeric user ID (`id -u` prints it), not `0`. Files
owned by `0` mean the containers ran as root, and you'd need `sudo` to delete your own build
output.

Don't run `./x up` on the NAS unless you mean to: it would start a second, test Jellyfin next
to your real one. It uses port 8097, so it won't collide with the default 8096, but it would
still use NAS resources.

---

## Resetting and cleaning up

| Goal | Commands |
|---|---|
| Stop the test server | `./x down` |
| Rebuild the test server from nothing, keeping the media | `./x down && rm -rf testdata/jellyfin artifacts/.testserver.env && ./x deploy && ./x init` |
| Regenerate the media | `rm -rf testdata/media && ./x media`, then reset the server as above |
| Clear build and spike output | `rm -rf artifacts` |
| Remove everything, including ~3 GB of images and the NuGet cache | `./x down && rm -rf testdata artifacts && docker image rm linear-tv-sdk:dev jellyfin/jellyfin:12.1.20260915-010956 && docker volume rm jellyfin-channels_nuget` |

The last row is **not yet run**, since it would wipe this environment. It removes only this
project's images and volume.

Disk used: SDK image 978 MB, Jellyfin image 1.71 GB, media 400 MB, test server state ~230 MB.

---

## Troubleshooting

Each of these was actually hit while building this, except where noted.

| Symptom | Cause | Fix |
|---|---|---|
| Login page shows empty fields, no **dev** tile | Jellyfin 12 hides users from the login screen by default; `init` un-hides `dev` | Type `dev` / `dev`, or re-run `./x init` |
| Login rejected | `./x init` never completed, so the user doesn't exist | `./x init`. If it errors, see the next rows. |
| `init` fails with `server never became ready` | The server didn't start | `./x logs`. Look for errors near the end. |
| Series named after real TV shows ("The 11 O'Clock Show", episodes called "Pilot") | The library was created by hand in the UI, so internet metadata lookups are on. Jellyfin matched the test series to real shows. | Delete the *Shows* library in the UI, then `./x init`, which recreates it with lookups off. A `tvshow.nfo` with `<lockdata>` does **not** fix this; that was tried. |
| *Linear TV* not in the plugin list | Plugin not deployed, or the server wasn't restarted after deploying | `./x deploy`, then check `./x logs` for `Loaded plugin: Linear TV` |
| `./x spike1` says `no API key - run ./x init` | `artifacts/.testserver.env` is missing (wiped with `artifacts/`) | `./x init` |
| `./x spike1` says `no data returned` | Wrong item ID | Take an ID from `./x init`'s output |
| A Docker command typed by hand fails on a path, or silently writes nothing | Git Bash rewrites arguments starting with `/` (like `/artifacts/x.ts`) into Windows paths | Prefix the command with `MSYS_NO_PATHCONV=1`, or use relative paths. `./x` does this for you. |
| A hand-typed `curl -o /tmp/...` reports `200` but the file is empty or missing | Same cause, with `MSYS_NO_PATHCONV=1` set: Windows `curl.exe` can't write `/tmp/...` | Write to a relative path, for example `-o artifacts/out.bin` |
| Other devices can't connect to `:8097` | Windows Firewall blocking inbound traffic *(not yet hit, but the most likely cause)* | Windows Security → Firewall → *Allow an app through firewall* → allow the **Docker Desktop** entries on **Private** networks. Also check your PC's network is set to *Private*, not *Public* (Settings → Network & internet → your adapter → *Network profile type*), and that the device isn't on a guest Wi-Fi. A **VPN client** on the PC can also block inbound LAN traffic: look for a setting like NordVPN's *Invisibility on LAN*, or disconnect the VPN to test. |
| `bash\r: No such file or directory` | `x` has Windows CRLF line endings *(not yet hit)* | `sed -i 's/\r$//' x` |
| `Bind for 0.0.0.0:8097 failed: port is already allocated` | Something else uses port 8097 *(not yet hit)* | Change `8097:8096` in `compose.yaml` and use the new port throughout |

---

## Known gaps

- **A channel stops at each change of video or audio codec**, and you tune in again; it's no
  longer a frozen picture. Resolution changes within a codec play through. With the user's
  library, where 31 of 46 series mix codecs, these stops will be frequent. Continuous play
  across codecs would need a TV that switches decoders cleanly (`docs/plan.md` option b) or
  transcoding (option d). See channels test H.
- **AV1 programmes have no picture**, on any client. Jellyfin can't copy AV1 into MPEG-TS.
- **The guide doesn't update itself on library changes.** Add episodes to a channel's series
  and they join the schedule at the next guide refresh, which is daily, or when you next save
  the plugin settings. Tuning always uses the live library, so for a while the picture can
  differ from the guide.
- **Changing a channel's content shifts its whole schedule.** Channels loop from a fixed start
  date, so adding or removing an episode moves everything after it, including what's on
  right now. A deliberate simplification for the first version; `docs/plan.md` §4.1 describes
  the stored-timeline alternative.
- **Channels of very short programmes have a shorter guide.** Each channel publishes at most
  3,000 programmes per guide refresh: about a day of 30-second clips, or four weeks of films.
  (0.3 and earlier saw Jellyfin store 3,001 of the 5,760 a 48-hour horizon implied for Quick
  Cuts. That was never explained, and the cap now keeps under it.)

---

## Reporting results

Copy this and fill it in:

```
Environment:   Windows / NAS (model: ___)   Docker ___

CHANNELS
A In the guide         pass / fail   all three listed? ___  times look right? ___
B Tune mid-programme   pass / fail   guide said ___ started ___, clock read ___
C Flick                pass / fail   client: ___   seconds per change: ___
D Create channel       pass / fail   saved? ___  in guide without restart? ___  edit/delete ok? ___
E No re-encode         pass / fail   reasons shown: ___   ffmpeg video: ___
F Runs on             client: ___   next programme started? ___   visible glitch at the join? ___
G Quick change         client: ___   seconds to picture: ___   started at the right point? ___
H Mixed formats        client: ___   codec change: stops cleanly / freezes / garbled   720p→360p join: smooth? ___

TOOLING
2 Toolchain            pass / fail   notes:
3 Plugin + settings    pass / fail   save survived reload? ___  survived restart? ___
4 Media + clock        pass / fail
5 Spike 1              pass / fail   617s read as: ___
6 Cache bug demo       reproduced? yes / no
7 Client               device: ___   reached server? ___   direct play or transcode? ___
8 NAS install          pass / fail / skipped
9 NAS build            pass / fail / skipped   owner uid shown: ___
```
