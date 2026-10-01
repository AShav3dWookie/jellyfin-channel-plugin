# Channel import format

Linear TV channels can be written as a JSON file and loaded into Jellyfin over its API, with no
rebuild and no restart. You can write the file by hand, script it, or have an AI write it
(Claude Code, claude.ai, anything) from a list of your library. The guide refreshes as soon as
an import saves.

- **Export:** `GET /LinearTv/Channels/Export` returns the current channels in this format.
- **Import:** `POST /LinearTv/Channels/Import` loads a file. Add `?dryRun=true` to check it
  without saving.

Both need an **administrator's** API key.

---

## Quick start

**From this repo** (Git Bash), `./x channels` does it all through the development container,
backing up before every import and refusing any file that hasn't passed a check. Put your
server and an admin API key (*Dashboard → API Keys → +*) in `artifacts/.nas.env`, which is
gitignored:

```
NAS_URL=http://192.168.1.3:8096
NAS_KEY=paste-your-api-key-here
```

```bash
./x channels --nas library                 # list your library -> artifacts/channels/nas/
./x channels --nas list                    # channels on the server now
./x channels --nas check my-channels.json  # dry run: nothing is saved
./x channels --nas import my-channels.json # back up, then import (only after a passing check)
./x channels --nas restore artifacts/channels/nas/backup-<time>.json   # undo
```

Leave out `--nas` to use the test server. Add `--replace` to `check` and `import` to make the
file the whole channel list. Files must be inside the repo, because the commands run in a
container that only sees the repo.

**From anywhere else**, use plain curl:

```bash
SERVER=http://192.168.1.3:8096      # your Jellyfin; the test server is http://localhost:8097
KEY=paste-your-api-key-here
AUTH="Authorization: MediaBrowser Token=\"$KEY\""

# 1. Back up what you have
curl -s "$SERVER/LinearTv/Channels/Export" -H "$AUTH" > channels-backup.json

# 2. Check a new file: nothing is saved
curl -s "$SERVER/LinearTv/Channels/Import?dryRun=true" -H "$AUTH" \
     -H "Content-Type: application/json" --data-binary @channels.json

# 3. Import it for real
curl -s "$SERVER/LinearTv/Channels/Import" -H "$AUTH" \
     -H "Content-Type: application/json" --data-binary @channels.json
```

To undo, import the backup with `?mode=replace`.

---

## The file

```jsonc
{
  "version": 1,
  "channels": [
    {
      "number": "201",
      "name": "Sci-Fi Nights",
      "shuffle": true,
      "content": [
        { "type": "movie", "title": "Blade Runner", "year": 1982 },
        { "type": "movie", "title": "Arrival" },
        { "type": "series", "title": "Battlestar Galactica", "year": 2004 },
        { "type": "collection", "title": "Alien Collection" }
      ]
    },
    {
      "number": "202",
      "name": "Sopranos Pilots",
      "content": [
        { "type": "episode", "series": "The Sopranos", "season": 1, "episode": 1 },
        { "type": "episode", "series": "The Sopranos", "season": 2, "episode": 1 }
      ]
    }
  ]
}
```

### Channel

| Field | Required | Meaning |
|---|---|---|
| `number` | yes | Channel number as text: `"201"`, `"7.1"`. Unique within the file. |
| `name` | yes | Shown in the guide. |
| `shuffle` | no | `false` (default): play the content in the order listed, in episode order within a series. `true`: a stable shuffle that is the same every loop. |
| `content` | yes | One or more content entries. |

### Content entry

| `type` | Fields | Plays |
|---|---|---|
| `movie` | `title`, optional `year` | that film |
| `series` | `title`, optional `year` | every episode, in order |
| `collection` | `title` | every film in the Jellyfin collection |
| `library` | `title` (the library's name, e.g. `"Movies"`) | everything in the library |
| `episode` | `series`, `season`, `episode`, optional `year` (of the series) | that one episode |

Any entry may also carry an `id`: a Jellyfin item ID. It's used when it exists on the server,
and the name is used when it doesn't. IDs are derived from file paths, so they differ between
servers. Exports include both, which makes them exact on the same server and portable to
another one.

Accepted spellings: `film`/`movies` for `movie`, `show`/`tv` for `series`, and `boxset` for
`collection`.

The file may contain `//` comments and trailing commas, and field names may use any casing.

### How names are matched

- **Case, punctuation and accents are ignored**, and `&` matches `and`. So `alien earth`
  finds *Alien: Earth*, `Amelie` finds *Amélie*, and `Law and Order` finds *Law & Order*.
  The original title works too.
  Superscripts count as digits: `Alien 3` finds *Alien³*.
- **Years may be off by one.** Databases disagree about release years. The report notes when
  this happens.
- **Subtitles may differ, given the year.** `Die Hard 2: Die Harder` (1990) finds TMDb's
  *Die Hard 2*, and `Dracula` (1992) finds *Bram Stoker's Dracula*. One title must contain
  the other, the year must match exactly, and it must be the only such title. Without a year,
  it's only suggested. The report notes each partial match: check them.
- **Nothing is guessed.** A title that matches more than one item is refused, and the report
  lists the matches so you can add a year. A title with no match is refused, with up to three
  similar titles suggested where there are any.
- **The type must match.** A series is never found by `"type": "movie"`, but the report points
  it out: *no movie of that name, but The Sopranos (1999) is a series*.

---

## Importing

```
POST /LinearTv/Channels/Import?dryRun=false&mode=merge
```

| Parameter | Values | Default |
|---|---|---|
| `dryRun` | `true`: report only. `false`: save if everything resolves. | `false` |
| `mode` | `merge`: imported channels replace channels with the same number, and every other channel is kept. `replace`: the imported channels become the whole list. | `merge` |

**All or nothing.** If any entry fails to resolve, nothing is saved, so the channel list is
never left half-imported.

A channel replaced by number **keeps its identity**, so the guide treats it as the same channel.

### Responses

| Status | Meaning |
|---|---|
| **200** | Everything resolved. Saved, unless it was a dry run. |
| **422** | Something didn't resolve. Nothing was saved, and the report says what failed. |
| **400** | Not valid JSON, an unknown `mode`, or an unsupported `version`. |
| **401** | No API key, or an invalid one. |
| **403** | The key belongs to a user who isn't an administrator. |

The report, for both 200 and 422:

```jsonc
{
  "dryRun": true,
  "saved": false,
  "mode": "merge",
  "ok": false,                      // true: can be (or was) saved
  "errors": [],                     // problems with the file as a whole
  "channels": [
    {
      "number": "201",
      "name": "Sci-Fi Nights",
      "shuffle": true,
      "action": "create",           // create | replace | unchanged (set when ok)
      "content": [
        { "requested": "movie 'Blade Runner' (1982)", "matched": "Blade Runner (1982)",
          "type": "movie", "id": "…" },
        { "requested": "movie 'Arrival'", "note": "ambiguous: Arrival (2016), Arrival (1996); add a year to choose" }
      ],
      "problems": ["movie 'Arrival': ambiguous: Arrival (2016), Arrival (1996); add a year to choose"],
      "warnings": []
    }
  ],
  "removed": []                     // replace mode: channels that would go
}
```

Fix what `problems` lists, then dry-run again until `ok` is `true`.

---

## Generating channels with an AI

The AI needs two things: **what's in your library**, and **this format**.

### In Claude Code (this repo)

Ask: *"make me a Sunday-afternoon war films channel"*. The repo's `make-channels` skill
(`.claude/skills/make-channels/`) has it read your library, write and check the file, show
you the result, and import only once you say yes. It uses `./x channels --nas`, so set up
`artifacts/.nas.env` first (see Quick start).

### In claude.ai, or any other AI

1. **List your library:** `./x channels --nas library` writes three files to
   `artifacts/channels/nas/`:
   - `library.jsonl`: films, series and collections, with year, genres, rating and length
   - `episodes.jsonl`: every episode, as series, season, episode and title
   - `libraries.jsonl`: your Jellyfin libraries

   No repo to hand? A plain file listing works too, with the forgiving matching doing more of
   the work: `dir /s /b \TRUENAS\media\Movies > movies.txt`, and the same for TV.
2. **Ask.** Attach the files and this document, then, for example:

   > Using only titles from library.jsonl, write a Linear TV channel file (format in
   > channel-format.md) with six themed channels numbered from 201: one each for sci-fi,
   > horror, 80s films, comedy series, documentaries, and Christmas. Shuffle the film
   > channels; play series in order. Copy titles and years exactly from the list.
3. **Load:** save its answer in the repo and run `./x channels --nas check <file>`, then
   `import`. Or use the curl commands above.

**Tips**
- Ask for **years on everything**. They are what tells remakes apart.
- Tell it to use **only titles from the list**. Otherwise an AI fills a sci-fi channel with
  films you don't own, and the check refuses them all.
- **Codec mix:** channels stop when the video or audio codec changes between programmes, and
  you re-tune (see `docs/plan.md`). Not something the AI can see from the list.
- **Paste the check's report back** to the AI if anything fails. The suggestions in it are
  usually enough for it to fix its own mistakes.
- **Number AI channels in a range of their own,** such as 200–299. Merge mode then replaces only
  those channels, and importing the AI's next attempt replaces its last one.

---

## Version

`version` is `1`, and may be left out. A future version that changes the meaning of a field
will bump it, and this version of the plugin will refuse that file rather than misread it.
