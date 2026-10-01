---
name: make-channels
description: Create, change or remove Linear TV channels on a Jellyfin server from the user's real library: themed film channels, series marathons, episode playlists. Use whenever the user asks for a channel ("make me a horror channel", "a Sunday war films channel", "put Band of Brothers on 205", "drop channel 203"), or to undo or restore channels. Covers reading the library, writing the channel file, checking it, and importing it only with the user's go-ahead.
---

# Making Linear TV channels

Channels are written as a JSON file (format: `docs/channel-format.md`, read it first) and loaded
into a running Jellyfin through the plugin's import endpoint. **`./x channels` does all the
talking to the server.** Use it rather than hand-written curl: it backs up before every import,
and refuses to import a file that hasn't passed a check.

## Which server

- **The user's NAS:** `./x channels --nas …`. It needs `artifacts/.nas.env`:
  ```
  NAS_URL=http://192.168.1.3:8096
  NAS_KEY=<an administrator's API key>
  ```
  If the file is missing, ask the user to create it, with a key from *Dashboard → API Keys*.
  Don't ask for the key in chat. Containers can't resolve the name `truenas`, so use the IP.
- **The test server:** `./x channels …` with no flag. For trying things out.

If the user doesn't say which, they mean the NAS. **Anything that changes the NAS (`import`,
`restore`) needs the user's go-ahead for that specific file.** An earlier yes doesn't carry
over to a new file.

## Steps

1. **Read the library.** `./x channels --nas library` writes three files to
   `artifacts/channels/nas/`:
   - `library.jsonl`: films, series and collections with year, genres, rating, minutes (films),
     seasons and items (series, collections)
   - `episodes.jsonl`: every episode as series, season, episode, title
   - `libraries.jsonl`: the Jellyfin libraries

   Run it once per session, and again if the user has added media since. The files are large,
   so `grep` them rather than reading them whole (for example `grep -i '"Horror"' library.jsonl`).
2. **Write the channel file** to `artifacts/channels/nas/<short-name>.json`, under the
   conventions below.
3. **Check it:** `./x channels --nas check <file>`. Nothing is saved.
4. **Fix and re-check until it passes**, without asking the user about routine fixes:
   - `X … not found; did you mean …`: use the suggested title if it's clearly the same work.
     Otherwise drop the entry.
   - `X … ambiguous`: add the year.
   - `X no movie of that name, but … is a series`: fix the type.
5. **Show the user the plan** before importing:
   - each channel's number, name, order, and what's on it (summarise long lists)
   - every `~` line, which is a match the server made with a note (partial title, year off by
     one). Ask about any that look wrong.
   - anything you dropped, and why
   - the action: `create`, or `replace` with the old content named

   Then ask whether to import.
6. **Import on a yes:** `./x channels --nas import <file>`. It backs up first, then imports,
   and the guide updates within seconds. Pass on the backup's file name.

## Conventions

- **Titles come from `library.jsonl` and `episodes.jsonl`, never from memory.** Copy the
  title and year exactly. The server matches forgivingly, but exact copies need no forgiveness.
  Your own knowledge decides *what fits a theme*; the library decides *what exists*. If
  something the user asked for isn't in the library, say so rather than leaving it out silently.
- **Always give a year** for films and series.
- **Numbering:** the user's existing channels stay as they are. Run
  `./x channels --nas list` first. New channels go from **200** upward, in the first free
  numbers. Reuse a number only when the user wants that channel replaced; merge mode replaces
  by number.
- **Order:** film channels shuffle (`"shuffle": true`), and series channels play in order
  (`false`), unless the user says otherwise. A themed mix of series usually shuffles; a
  marathon doesn't.
- **Whole series** go in as `series`. Single episodes go in as `episode` entries, with series,
  season and episode from `episodes.jsonl`.
- **Size:** a channel needs enough content not to repeat within a day or two. Under roughly
  12 hours of material, mention that it will loop often. Very large is fine. Prefer a
  collection or library entry over listing hundreds of items.
- **Entries marked `"unidentified": true`** are titled by file name. Copy those titles
  exactly too.
- **Collections** (`"type": "collection"`) exist only if the user's server has them.

## Things the user should know (say when relevant)

- **Channels stop at a codec change** (for example H.264 to HEVC) between programmes, and the
  user re-tunes. They've accepted this. The library listing has no codec information, so
  don't promise single-format channels.
- **AV1 files have no picture** when played through a channel.
- **Changing a channel's content shifts its whole schedule.** It loops from a fixed date, so
  what's "on now" changes.

## Removing and undoing

- **Remove channels:** export with `./x channels --nas backup`, copy that backup to a new
  file without the channels to remove, check it with `--replace`, show the user what the
  report lists under *Removed*, and import with `--replace` on a yes.
- **Undo the last import:** `./x channels --nas restore artifacts/channels/nas/backup-<time>.json`,
  using the backup the import made. It needs a go-ahead, like an import.

## If something fails

- **`no channel import endpoint`:** Linear TV 0.3 or later isn't installed on that server.
- **`rejected the API key`** or **`doesn't belong to an administrator`:** the user needs a new
  key in `artifacts/.nas.env`.
- **`cannot reach`:** check `NAS_URL`, and that Jellyfin is up.
