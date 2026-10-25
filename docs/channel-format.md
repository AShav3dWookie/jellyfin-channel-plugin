# Channel files

A channel file describes channels by what's in the library: films, series, collections,
episodes. You can write it by hand, script it, or have an AI write it (Claude Code, claude.ai,
anything) from a list of your library. The builder resolves it against Jellyfin and builds each
channel into one continuous stream (`docs/architecture.md`), which you then publish to the
plugin's store.

---

## Quick start

From this repo, in Git Bash. Put your server, an admin API key (*Dashboard → API Keys → +*),
and how this PC reaches the media and the store in `artifacts/.nas.env`, which is gitignored:

```
NAS_URL=http://192.168.1.3:8096
NAS_KEY=paste-your-api-key-here
NAS_MAP=/mnt/tank/media=\\TRUENAS\media
NAS_STORE=\\TRUENAS\media\linear-tv
```

`NAS_MAP` turns paths as Jellyfin sees them into paths this PC can open: server prefix, `=`,
local prefix. Separate several with `;`. `NAS_STORE` is the plugin's store folder (its
settings page) as this PC reaches it.

Optional build settings go in `artifacts/.builder.env`:

```
TMDB_API_KEY=your-tmdb-key           # original languages from TMDb; without it, production countries
BUILD_WORK=D:\channel-transcoding\work   # pieces while a channel builds, TMDb answers (default artifacts/builder-work)
BUILD_OUT=D:\channel-transcoding\built   # finished builds, in test\ and nas\ (default artifacts/built)
BUILD_JOBS=3                         # encodes at a time
BUILD_PRESET=p4                      # NVENC preset p1-p7: p4 default; p7 ~1% smaller, 2.6x slower
```

```bash
./x channels --nas library                  # list your library -> artifacts/channels/nas/
./x builder --nas check my-channels.json    # how everything resolves; nothing encoded
./x builder --nas build my-channels.json    # encode and build (--only 201,202 for some; --preset p4 for speed)
./x publish --nas 201 202                   # copy the builds to the NAS and put them on air
./x store --nas                             # what's on air
./x unpublish --nas 203                     # take a channel off air
```

Leave out `--nas` to use the test server.

---

## The file

```jsonc
{
  "version": 1,
  "language": "eng",   // optional: subtitles are burned in this language (default English)
  "channels": [
    {
      "number": "201",
      "name": "Sci-Fi Nights",
      "shuffle": true,
      "passes": 3,
      "content": [
        { "type": "movie", "title": "Blade Runner", "year": 1982 },
        { "type": "movie", "title": "Arrival", "year": 2016 },
        { "type": "series", "title": "Battlestar Galactica", "year": 2004 },
        { "type": "collection", "title": "Alien Collection" },
        { "type": "movie", "title": "Akira", "year": 1988, "audio": "eng" }
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
| `shuffle` | no | `false` (default): play the content in the order listed, in episode order within a series. `true`: a stable shuffle. |
| `passes` | no | How many times the lineup is in the channel's file, 1 to 20 (default 1). Each pass is shuffled differently, so a small shuffled channel doesn't repeat the same order every loop. Multiplies the channel's size; items are encoded once either way. |
| `audio` | no | Audio language for every entry that doesn't set its own (below). |
| `subtitles` | no | Subtitle rule for every entry that doesn't set its own (below). |
| `content` | yes | One or more content entries. |

### Content entry

| `type` | Fields | Plays |
|---|---|---|
| `movie` | `title`, optional `year` | that film |
| `series` | `title`, optional `year` | every episode, in order |
| `collection` | `title` | every film in the Jellyfin collection |
| `library` | `title` (the library's name, e.g. `"Movies"`) | everything in the library |
| `episode` | `series`, `season`, `episode`, optional `year` (of the series) | that one episode |

Any entry may also carry:

- an `id`: a Jellyfin item ID. It's used when it exists on the server, and the name is used
  when it doesn't. IDs are derived from file paths, so they differ between servers.
- `audio`: an audio language (`"eng"`, `"en"` or `"English"`) to use instead of the original
  language. This is how to allow a dub.
- `subtitles`: `auto` (the default), `none`, `forced` or `full`.

Accepted spellings: `film`/`movies` for `movie`, `show`/`tv` for `series`, and `boxset` for
`collection`.

The file may contain `//` comments and trailing commas, and field names may use any casing.

### Audio and subtitles

Each item gets one audio track and, where needed, subtitles burned into the picture:

- **Audio:** the original language (from TMDb, else the production country), unless `audio`
  asks for another. An item whose only audio is a dub is **left out**, with a note saying how
  to allow it.
- **Subtitles (`auto`):** full subtitles in the file's `language` when the audio is in another
  language; otherwise forced subtitles only, if there are any. A foreign-language item with no
  subtitles in your language is **left out**.

`docs/architecture.md` has the details. The check lists every item that gets subtitles, and
every item left out, with why.

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

## Checking

`./x builder check <file>` resolves everything and prints, for each entry, `ok` (exact match),
`~` (matched with a note: check it) or `X` (refused, with why). Fix the `X` lines and check
again; nothing is built until everything resolves.

Then, per channel: how many items and hours, how often it loops, its frame rate, how many items
are HDR or interlaced, where original languages came from, which items get which audio and
subtitles, which items are left out, and how much needs encoding.

---

## Generating channels with an AI

The AI needs two things: **what's in your library**, and **this format**.

### In Claude Code (this repo)

Ask: *"make me a Sunday-afternoon war films channel"*. The repo's `make-channels` skill
(`.claude/skills/make-channels/`) has it read your library, write and check the file, and build
and publish only once you say yes.

### In claude.ai, or any other AI

1. **List your library:** `./x channels --nas library` writes three files to
   `artifacts/channels/nas/`:
   - `library.jsonl`: films, series and collections, with year, genres, rating and length
   - `episodes.jsonl`: every episode, as series, season, episode and title
   - `libraries.jsonl`: your Jellyfin libraries
2. **Ask.** Attach the files and this document, then, for example:

   > Using only titles from library.jsonl, write a Linear TV channel file (format in
   > channel-format.md) with six themed channels numbered from 201: one each for sci-fi,
   > horror, 80s films, comedy series, documentaries, and Christmas. Shuffle the film
   > channels; play series in order. Copy titles and years exactly from the list.
3. **Check and build:** save its answer in the repo, run `./x builder --nas check <file>`, then
   `build` and `publish`.

**Tips**
- Ask for **years on everything**. They are what tells remakes apart.
- Tell it to use **only titles from the list**. Otherwise an AI fills a sci-fi channel with
  films you don't own, and the check refuses them all.
- **Paste the check's report back** to the AI if anything fails. The suggestions in it are
  usually enough for it to fix its own mistakes.

---

## Version

`version` is `1`, and may be left out. A future version that changes the meaning of a field
will bump it, and this builder will refuse that file rather than misread it.
