# Linear TV 1.0: pre-built channels

**Date:** 2026-10-24. Replaces the delivery design of `plan.md` (0.1–0.4), which joined
Jellyfin's per-programme remuxes at play time. That design worked for channels whose content
shared one format, and stopped at every codec change otherwise: 31 of 46 series in the real
library mix codecs, and AV1 had no picture at all (`testing.md`, channels test H).

## The idea

Each channel is **one continuous MPEG-TS**, encoded offline from the whole lineup, end to end, in
one fixed format. Programme boundaries are inside a single stream with continuous timestamps,
so there is nothing to join at play time and nothing for a player to re-initialise.

```
 this PC (Ryzen 7800X3D, RTX A5000)                         NAS (Jellyfin)
 ───────────────────────────────────                        ─────────────────────────────
 channel file (JSON, as before)                              Linear TV plugin
   │                                                          · lists channels in the store
   ▼                                                          · guide from each manifest
 LinearTv.Builder ── Jellyfin API ──► resolve titles,         · tune: (now − epoch) mod cycle
   │                                  paths, metadata,          → keyframe index → byte offset
   │                                  artwork                   → read file at real-time pace
   ▼                                                          · no ffmpeg, no library access
 ffmpeg (NVENC) per item ──► pieces (temporary, local)
   │                                                        store/
   ▼                                                          201-20261024T1530Z/
 assemble: pieces → one restamped TS ──► publish ──────────►    channel.json  manifest
   + keyframe index, manifest, artwork    (robocopy)            channel.ts    (or chunks)
                                                                index.bin
                                                                art/*.jpg
```

The heavy work happens once, on the PC. At play time the NAS reads a file.

## Fixed format

Every channel uses the same format, so every programme in a channel matches every other.

| | |
|---|---|
| Video | H.265 Main, level 4.1, 1280×720, square pixels, 8-bit 4:2:0, SDR BT.709 |
| Aspect | Never changed. Scaled by display shape (anamorphic sources corrected first), fitted inside a fixed 1280×720 frame, black bars for the rest |
| Frame rate | One per channel: the most common among its items (50/60 fps sources count as 25/30). Others are converted |
| Keyframes | Every 2 seconds exactly, closed GOPs, SPS/PPS repeated on each |
| Audio | AAC-LC stereo, 48 kHz, 192 kb/s, loudness-normalised (EBU R128, −23 LUFS) |
| HDR | Tone-mapped to SDR on the GPU (`libplacebo`, BT.2390), and scaled there when there are no image subtitles |
| Interlaced | Deinterlaced (`bwdif`) |
| Subtitles | Burned in when needed (see below); never carried as a separate stream |
| Encoder | `hevc_nvenc`, preset p4 (or `--preset`), VBR with constant quality 30, max 10 Mb/s |

**H.265 at CQ30**, chosen with the user on 2026-10-24 for size, with audio quality the priority
(AAC 192 kb/s, 48 kHz, unchanged). Measured on 5-minute segments, VMAF against the source scaled
to 720p:

| | Akira (grain) | | Parasite | |
|---|---|---|---|---|
| | GB/hour | VMAF | GB/hour | VMAF |
| H.264 CQ21 (the first choice) | 2.51 | 96.1 | 1.53 | 97.7 |
| **H.265 CQ30** | **0.61** | **93.1** | **0.38** | **95.4** |

The consequence: TV apps that play H.265 direct-play the channel. A client that can't (measured:
jellyfin-web's H.264-only browser profile) gets a full video transcode to H.264 by Jellyfin on
the server, `libx264` on the NAS's CPU, per viewer. Audio is still copied.

The encoder is one pinned ffmpeg build: FFmpeg 8.1.3 (BtbN's win64 GPL build of 2026-10-23).
Which build doesn't matter to playback; the output is a plain MPEG-TS. 8.1 rather than 9.0: 9.0's
NVENC needs NVIDIA driver 610+, which the A5000 doesn't have. For H.264 on this card the output
is the same: NVENC quality is the GPU's, not FFmpeg's.

### Audio track and subtitles

Decided with the user, 2026-10-24.

**Audio.** One track, chosen in this order:

1. The language the channel file asks for (`"audio": "eng"` on a channel or one entry), to allow a
   dub.
2. Otherwise the **original language**: the track flagged original, else a track in the title's
   original language from TMDb (`original_language`, via Jellyfin's TMDb id; the series' for an
   episode). Without a TMDb key or id: the first production country's language.
3. If the original language isn't known: the first track.

Commentary and audio-description tracks are never chosen. Untagged tracks count as the original
language. An item whose only tracks are tagged with other languages (a dub only) is left out
with a warning, unless the channel file asks for that language.

**Subtitles, burned in.** The viewer's language is the channel file's `language`, default
English.

| Chosen audio | Burned in |
|---|---|
| Not in the viewer's language | Full subtitles in the viewer's language, non-SDH preferred. None available: the item is left out, with a warning |
| In the viewer's language | Forced subtitles in the viewer's language, if any: flagged forced, or titled "forced" |

`"subtitles": "none" | "forced" | "full"` on a channel or entry overrides this. Image subtitles
(PGS, VobSub) are overlaid at the source's resolution before scaling; text subtitles (SRT, ASS,
external files too) are rendered by libass onto the final 1280×720 frame, so they stay legible
and may sit in the black bars.

### TMDb

The original language comes from TMDb's API (a free key, given to the builder). Lookups are
cached in the work folder; one per film or series.

## The builder (`src/LinearTv.Builder`, runs on the PC)

A .NET console app, built in the SDK container as a self-contained Windows exe: no SDK on the
host. It reads a Jellyfin server's library over its REST API (the test server or the NAS),
read-only, and never talks to the plugin. The store format and `TsSplicer` are shared with the
plugin as linked source files.

1. **Resolve** the channel file. Same format and same matching rules as 0.3's import
   (`channel-format.md`). The matching code moved here from the plugin.
2. **Expand** each entry to playable items (series → episodes in order, collection → its films,
   library → everything), each with its path, streams and guide metadata.
3. **Order.** In order, or a stable shuffle. `passes` (default 1) repeats the lineup that many
   times in the file, each pass shuffled differently, never the same item twice in a row,
   including across the loop back to the start.
4. **Encode** each item: one ffmpeg run, source → piece TS in the fixed format. Sources are
   read where Jellyfin says they are, through a path map (`/media` → `\\TRUENAS\media`).
   Several encodes run in parallel. A failed encode leaves a `.log` beside its piece with the
   exact command and ffmpeg's output.
5. **Assemble** the pieces into the channel file: the bytes of each piece in turn, through the
   same `TsSplicer` the plugin used, so timestamps run continuously from start to end. While
   writing, it records every video keyframe (time, byte offset) in the index, and where each
   programme starts.
6. **Chunk.** A channel of 100 hours or more is cut into hour-long files, each starting at a
   keyframe. Playback is identical; copying is resumable per file.
7. **Write** `channel.json`, `index.bin`, and artwork into a new build folder named
   `<number>-<UTC time>`.

Pieces are temporary: each is deleted once it has been joined for the last time (a piece plays
once per pass). A build that stops part-way leaves its finished pieces in the work folder, and
running it again reuses them. Disk use peaks at about the channel's size plus one pass of pieces.
Changing a channel re-encodes it. The published channel has no link to the pieces or the
source files.

Superseded local builds of a channel are deleted once a new one has finished: each is a full copy
of the channel.

## The store (on the NAS)

One folder per build. The plugin uses the newest complete build of each channel number, and
ignores folders without a `channel.json`, so a half-copied build is never on air. Publishing
copies the manifest last, then removes the channel's older builds.

`channel.json` (format 1):

```jsonc
{
  "format": 1,
  "number": "201", "name": "Sci-Fi Nights",
  "built": "2026-10-24T15:30:00Z",
  "cycleTicks": 1234567890000,          // length of one loop, 100 ns ticks
  "video": { "codec": "h264", "profile": "High", "level": 41, "width": 1280, "height": 720,
             "frameRate": 25, "maxBitRate": 10000000 },
  "audio": { "codec": "aac", "channels": 2, "sampleRate": 48000, "bitRate": 192000 },
  "psi": "<base64 PAT + PMT packets>",  // sent first on every tune-in
  "files": [ { "name": "channel.ts", "bytes": 123456789 } ],
  "index": "index.bin",                 // little-endian (int64 ticks, int64 byte offset) per keyframe
  "programmes": [
    { "start": 0, "duration": 72000000000, "title": "Blade Runner", "year": 1982,
      "movie": true, "overview": "…", "genres": ["Science Fiction"], "rating": "15",
      "image": "art/3f2a….jpg" },
    { "start": 72000400000, "duration": 26000000000, "title": "Battlestar Galactica",
      "episodeTitle": "33", "season": 1, "episode": 1, "overview": "…", "image": "art/…" }
  ]
}
```

Times are offsets into the file's own timeline, in 100 ns ticks.

## The plugin (on the NAS)

The plugin no longer reads the library or makes any HTTP calls. Settings: the store path, and the
join threshold (as before).

- **Channels:** one per channel number in the store. The id is derived from the number, so a
  rebuilt channel keeps its place in the guide.
- **Guide:** the manifest's programmes, repeated every `cycleTicks` from a fixed epoch
  (2020-01-01 UTC), for whatever range Jellyfin asks for.
- **Tune-in:** position = (now − epoch) mod cycle. If less than the join threshold is left in the
  programme, start at the next one. Find the last keyframe at or before the position in the
  index, send the PAT/PMT, then read the file from that byte offset.
- **Pacing:** `TsPacer` as before: a burst first, then real time.
- **Loop:** at the end of the last file, carry on from byte 0. `TsSplicer` restamps it to follow
  on, the one join in the whole cycle.
- **Media source:** declared from the manifest. The interlacing fix in `LinearLiveStream` stays,
  since Jellyfin still marks every third-party stream interlaced.

Removed: the loopback to Jellyfin's remux, the API key, stopping at format changes, the
channel editor and import endpoints (channel files are now built, not imported).

The store endpoint (`GET /LinearTv/Store`, `POST /LinearTv/Store/Refresh`, administrators
only) lists what the plugin found and rescans after publishing. A rescan also happens at every
guide refresh.

## Workflow

```bash
./x builder check my-channels.json          # resolve against the server: nothing encoded
./x builder build my-channels.json          # encode, assemble, into the build output folder
./x publish 201 202                         # copy the newest builds to the store, refresh the guide
./x store                                   # what the plugin sees
./x unpublish 203                           # take a channel off air
```

`--nas` targets the NAS. The first `./x builder` fetches the pinned ffmpeg (`./x ffmpeg`) and
publishes the builder executable. Settings: `channel-format.md`, "Quick start".

## Measured on the test server (2026-10-24)

Test channels from `./x media`: Mixed Show (H.264, HEVC, AV1, XviD/AVI, AC3, E-AC3, two
resolutions), and eight films for the audio, subtitle and picture rules.

| Check | Result |
|---|---|
| Mixed formats in one channel | One stream, all seven formats including AV1; 0 decode errors |
| Track rules | Japanese audio over an English default; full English subtitles for French, German (external `.srt`) and Polish audio; forced-only for English audio (track found by title, unflagged); an English-only dub of a Japanese film left out, and included with `"audio": "eng"` |
| Picture | Anamorphic 720×576 source square-pixelled and deinterlaced; 2.4:1 letterboxed with subtitles in the bar; HDR10 tone-mapped (test pattern only: needs checking on a real HDR film) |
| Loudness | Programmes within 1 dB of each other (−25 dB mean on test tones) |
| Direct play (TS client) | Direct play, no transcode; starts on a keyframe; 0 decode errors |
| Browser (HLS) | Remux only: `-codec:v:0 copy`, `-codec:a:0 copy` |
| Join part-way | Tuned 29 s into a programme, played from its keyframe at 28 s |
| Programme change and loop | 0.11 s held frame and 0.08 s of silence at each cut; nothing within a programme |
| Encoding speed, test clips | About 20× real time in total at preset p7 (the GPU's encoder is the limit); p4 measured 36× |
| Encoding speed, real films | 5.7–7.9× per film, 3 at a time (CPU-bound: decode, scale, audio). HDR was 2.3× with tone-mapping on the CPU; now on the GPU (libplacebo), measured 12.7× vs 6.8× on its own |
| Real channel (8 films/episodes, 13.7 h, H.264 CQ21) | 27.7 GB (about 2 GB per hour; H.265 CQ30 since); 0 decode errors; gaps only at the 7 programme changes (0.14–0.20 s video, 0.08 s audio); PGS, DVD and SRT subtitles burned in; 4K scaled; artwork and episode titles in the guide |
| Joining | About 300 MB/s (a 6-hour, 3.1 GB channel in 10 s) |

Not yet tried: a real client (browser, Android TV) watching through a programme change; image
subtitles (PGS/VobSub) from a real disc rip, which the test media can't contain; a real HDR film;
chunked channels (over 100 hours) end to end, though the chunk logic is unit-tested.

## What is no longer true from 0.x

- Channels no longer follow library changes. A channel is what was built; rebuild to change it.
- Picture quality is capped at 720p, and is a re-encode rather than the original stream.
- Storage: about 0.4–0.6 GB per hour of channel at H.265 CQ30 (real films, measured).
- The settings page no longer edits channels.
