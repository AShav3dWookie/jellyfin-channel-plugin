---
name: make-channels
description: Create, change or remove Linear TV channels from the user's real library: themed film channels, series marathons, episode playlists. Use whenever the user asks for a channel ("make me a horror channel", "a Sunday war films channel", "put Band of Brothers on 205", "drop channel 203"). Takes the content and goes all the way to built channel streams on this PC (library, channel file, check, encode), then publishes to the NAS only with the user's go-ahead.
argument-hint: "[p1-p7: encoder preset, default p4]"
---

# Making Linear TV channels

Since 1.0 a channel is **built on this PC** into one continuous video stream and **copied to
the NAS**, where the plugin plays it (`docs/architecture.md`). The channel file format is
`docs/channel-format.md`: read it first. `./x` does everything:

| Step | Command | Changes anything? |
|---|---|---|
| list the library | `./x channels --nas library` | no |
| check a channel file | `./x builder --nas check <file>` | no |
| build it | `./x builder --nas build <file>` | this PC only: encodes, hours for big channels |
| put it on air | `./x publish --nas <number>…` | **the NAS** |
| take it off air | `./x unpublish --nas <number>…` | **the NAS** |
| what's on air | `./x store --nas` | no |

## Arguments

The skill takes an optional **encoder preset**, `p1` to `p7` (for example `/make-channels p7`).
Pass it to every build as `--preset <preset>`: `./x builder --nas build <file> --preset p7`.

- **`p4`** (default): measured for H.265 at CQ30, 34× real time on the GPU, files within about
  2% of p7's and 0.2–0.5 VMAF lower (invisible).
- **`p7`**: the best quality per bit, but 13× on the GPU, which then limits the whole build.
- **`p1`**: 43×, about 5% bigger files; only worth it if a build must be quick.

On real films the CPU (decoding, scaling, audio) is the other limit: about 6–8× real time per
film, 3 at a time. Don't game during a build: a game takes the GPU's encoder and builds slow to
under 1×.

If no preset is given, use `BUILD_PRESET` from `artifacts/.builder.env` if set, else `p4`. Say
which preset a build uses when you give the time estimate. Items already encoded at a different
preset (left in the work folder by an interrupted build) are re-encoded, never mixed.

## Which server

- **The user's NAS:** `--nas`. It needs `artifacts/.nas.env` with `NAS_URL`, `NAS_KEY` (an
  administrator's API key), `NAS_MAP` (how this PC reaches the media: server path=local path)
  and `NAS_STORE` (the plugin's store folder, as this PC reaches it). If anything is missing,
  ask the user to add it. Don't ask for keys in chat.
- **The test server:** no flag. For trying things out.

If the user doesn't say which, they mean the NAS. **`publish` and `unpublish` against the NAS
need the user's go-ahead for those specific channels.** An earlier yes doesn't carry over.
Building only uses this PC and needs no go-ahead: once the check passes, build.

## Steps

1. **Read the library.** `./x channels --nas library` writes three files to
   `artifacts/channels/nas/`:
   - `library.jsonl`: films, series and collections with year, genres, rating, minutes (films),
     seasons and items (series, collections)
   - `episodes.jsonl`: every episode as series, season, episode, title
   - `libraries.jsonl`: the Jellyfin libraries

   Run it once per session, and again if the user has added media since. The files are large,
   so `grep` them rather than reading them whole.
2. **Write the channel file** to `artifacts/channels/nas/<short-name>.json`, under the
   conventions below.
3. **Check it:** `./x builder --nas check <file>`. Nothing is encoded or written. The report
   shows how each entry matched, then per channel: items, length, frame rate, the audio and
   subtitles each foreign-language item gets, and items **left out** with why.
4. **Fix and re-check until it passes**, without asking the user about routine fixes:
   - `X … not found; did you mean …`: use the suggested title if it's clearly the same work.
     Otherwise drop the entry.
   - `X … ambiguous`: add the year.
   - `X no movie of that name, but … is a series`: fix the type.
5. **Tell the user what's being built**, briefly, and start the build straight away:
   - each channel's number, name, order, length, and what's on it (summarise long lists)
   - every `~` line (a match made with a note) and every `-  left out` line. Left-out items
     are usually a dub with no original-language audio, or foreign audio with no English
     subtitles: say which, and offer `"audio": "eng"` for a dub if the user wants it (that
     means building again)
   - how long it will take: roughly **1 hour of encoding per 10–15 hours** of content (see
     Arguments). A channel is about **0.5 GB per hour** (H.265).
6. **Build:** `./x builder --nas build <file> [--only 201,202] [--preset pN]`. Run it in the
   background and redirect its output to a log in the build work folder (it takes hours for
   big channels); you're notified when it ends. Don't poll. A build that stops can be run
   again; finished items are reused. A `FAILED` item stops its channel from being built: read
   its `.log`, fix the cause or drop the item, and build again.
7. **When the build has finished, ask before publishing:** say what was built (the `built …`
   lines: length, size) and ask whether to put it on air.
8. **Publish on a yes:** `./x publish --nas <number>…`. It copies the newest build (about 1 GB
   per 10 s on gigabit), removes the channel's older build from the NAS, and the guide
   refreshes.

## Conventions

- **Titles come from `library.jsonl` and `episodes.jsonl`, never from memory.** Copy the
  title and year exactly. Your own knowledge decides *what fits a theme*; the library decides
  *what exists*. If something the user asked for isn't in the library, say so.
- **Always give a year** for films and series.
- **Numbering:** run `./x store --nas` first. New channels go from **200** upward, in the
  first free numbers. Reuse a number only when the user wants that channel replaced: the
  newest build of a number is the one on air.
- **Order:** film channels shuffle (`"shuffle": true`), series channels play in order, unless
  the user says otherwise. `"passes": 2` or more puts the lineup in the file that many times,
  each shuffled differently: use it for small shuffled channels that would otherwise repeat
  the same order daily. It multiplies the channel's size, not the encoding time.
- **Whole series** go in as `series`; single episodes as `episode` entries.
- **Size:** a channel needs enough content not to repeat within a day or two. Under roughly
  12 hours, mention that it will loop often.
- **Audio and subtitles** follow the rules in `docs/architecture.md`: original-language audio,
  English subtitles burned in for foreign audio, forced subtitles otherwise. Only set
  `"audio"` or `"subtitles"` when the user asks for something different.

## Things the user should know (say when relevant)

- **A channel doesn't follow the library.** It's what was built; changing it means building
  and publishing again, and a rebuilt channel comes back at a different point in its loop.
- **Pictures are 720p H.265**, re-encoded, with black bars for anything not 16:9. TV apps
  play it directly. A browser that can't decode H.265 makes Jellyfin transcode it on the NAS.

## Removing a channel

`./x unpublish --nas <number>` deletes the channel's build folder from the NAS store and
refreshes the guide. Needs a go-ahead. The local build (in the build output folder) is kept, so
`./x publish --nas <number>` puts it back.

## If something fails

- **`no TMDb key`** (a note, not an error): original languages come from production countries.
  The user can add `TMDB_API_KEY` to `artifacts/.builder.env`.
- **`can't open …`** in left-out lines: `NAS_MAP` doesn't map that path.
- **`can't read … permission denied`:** this PC's SMB login (`truenas_aaron`) can't read the
  file. Media files are owned by `apps` (UID 568, Radarr/Sonarr), and `truenas_aaron` reads them
  through membership of the `apps` group (added 2026-10-24). If this reappears, check the file's
  group and mode on the NAS (`ssh truenas_admin@192.168.1.3`, `sudo stat <path>`).
- **`FAILED … (details: <file>.log)`** during a build: the log has the exact ffmpeg command and
  its error.
- **`rejected the API key`:** the user needs a new key in `artifacts/.nas.env`.
