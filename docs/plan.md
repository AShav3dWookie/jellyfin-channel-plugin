# Implementation Plan — Linear TV channels for Jellyfin

Follows [feasibility.md](./feasibility.md). Supersedes the concat/remux plan.

> **Status (2026-09-30): channels work end to end on the test server.** Channels are defined on
> the settings page, appear in the Live TV guide, and tune to the scheduled programme at the
> right offset. That is verified by tuning through the client API and reading the burned-in
> clock (see `docs/testing.md`, channels test). Phases 2–5 below are implemented in one pass.
> **Not yet seen on a real client**, and end-of-programme behaviour (§5.2) is still unknown.
>
> Where the implementation departs from this plan:
> - **§4.1: the schedule is computed, not stored.** Each channel loops its content from a
>   fixed epoch (2020-01-01 UTC), so no timeline is persisted. Far simpler and restart-proof.
>   The cost is that any change to a channel's content shifts its whole timeline. Revisit if
>   that bothers you in practice.
> - **§4.4: always remux, even at offset 0.** The `static=true` direct-play path for offset 0
>   isn't implemented yet. It's an optimisation, not a correctness issue.
> - **§4.7: the API key is created and managed by the plugin** (named "Linear TV"), not entered
>   in settings.
> - **Settings save triggers a guide refresh.** `BasePlugin<T>` has no configuration-changed
>   event in 12.1, so the plugin overrides `UpdateConfiguration` and raises its own.
> - **Jellyfin forces every third-party Live TV video stream to "interlaced"**, and that caused
>   full re-encodes in browsers. Found in real use, from a Playback Info screenshot. The source:
>   `LiveTvMediaSourceProvider.Normalize()` runs
>   `if (service is not DefaultLiveTvService) … stream.IsInterlaced = true;` on every video
>   stream. Browsers reject interlaced H.264, so what should have been an HLS remux became a
>   decode, deinterlace and re-encode of progressive video. There is no supported opt-out. The
>   fix: the service implements `ISupportsDirectStreamProvider` and returns its own
>   `LinearLiveStream`, whose `MediaSource` getter restores each stream's true value.
>   `MediaSourceManager` re-reads that getter after `Normalize()` and before any playback
>   decision. Verified by Jellyfin's ffmpeg command changing from a video re-encode to
>   `-codec:v:0 copy`. **It depends on internal call order, so re-check after every Jellyfin
>   upgrade** (`docs/testing.md`, channels test E). **Strong upstream PR candidate:**
>   `Normalize()` should respect a service's declared `IsInterlaced`.
> - Probing was considered as the alternative and rejected. Jellyfin only probes a live source
>   whose streams are unknown (index −1). That costs at least 3 s per tune and caches the first
>   probe per channel, which is wrong for mixed-format channels.
> - **`ScheduleRetentionDays` was removed**: nothing is stored, so there is nothing to prune.
>   The guide horizon default rose from 24 to 48 hours, because Jellyfin refreshes the guide
>   every 24. **Superseded in 0.4: the horizon setting is gone.** The guide covers the range
>   Jellyfin asks for (its own *guide data days*, 7 by default, 14 at most). 48 hours had
>   ended the guide after two days. A cap of 3,000 programmes per channel per refresh bounds
>   channels of very short programmes.
>
> - **§5.2 decided: channels are continuous, and the plugin now carries the bytes.** This
>   overturns "the plugin serves no media" above. One stream per programme ended at the
>   programme's end, and the web client stopped and returned to the menu (reported). Now
>   `LinearLiveStream.GetStream()` returns a `ChannelStream`, which Jellyfin serves at
>   `/LiveTv/LiveStreamFiles/{id}/stream.ts`. The stream joins Jellyfin's remux of each
>   programme end to end, in schedule order. Joined raw, it broke the browser route: Jellyfin
>   remuxes to HLS with `-copyts`, so timestamps restarting at each programme gave ~25,000
>   non-monotonic DTS errors, and the output froze after the first join. So `TsSplicer`
>   rewrites PTS, DTS, PCR and continuity counters so the timeline runs on. That's header
>   bookkeeping only: **the plugin still never decodes or encodes.** Measured result: 0 timestamp
>   errors, real-time pacing (`ReadAtNativeFramerate` → `-re`), and clean joins on both the
>   HLS and direct routes (`docs/testing.md`, channels test F).
> - **Channel change time, browser: ~31 s → ~2 s.** Jellyfin lists a live HLS playlist only once
>   three segments exist, and its `-re` paced from the first frame, so a browser waited three
>   segment lengths (keyframe-bound: 10 s each is common). Now `TsPacer` paces the channel
>   stream by PCR after an initial 35 s burst, and `ReadAtNativeFramerate` is off, so there's
>   no `-re`. That exposed Jellyfin's default `-analyzeduration 200M`, a 166 s wait on a paced
>   input, so `AnalyzeDurationMs` = 5000. Direct route unchanged at ~1.2 s, which is Jellyfin's
>   own remux start-up. Joins verified still continuous afterwards.
> - **Known limit: channels mixing video formats. Tested 2026-10-01; option (a) below is now
>   in, so streams stop cleanly instead of freezing**
>   (`docs/testing.md`, channels test H, using the Mixed Show clips):
>   - **Browser:** the picture freezes at the first format change. Jellyfin runs one ffmpeg per
>     viewing, and its decoder is fixed at start. It's not a clean stop, so the user's
>     "stops and needs restarting is fine for now" isn't met.
>   - **Direct (TV):** untested on a TV. The stream announces every change, but always as PMT
>     version 0, and a continuously running generic decoder ignored them. Every programme's data
>     is intact, though: a decoder restarted anywhere decodes it.
>   - **AV1:** no picture on any route. Jellyfin's TS remux turns AV1 into unrecognised
>     `bin_data`. That's 16 episodes across 6 series in the user's library.
>
>   It's the normal case here, not an edge case: 31 of the user's 46 TV series mix codecs.
>   Options, cheapest first:
>   (a) **DONE (2026-10-01): end the stream cleanly at a format change**, so the client returns
>   to the menu and re-tuning plays the new programme correctly. That's the "stops, restart it"
>   behaviour the user accepted. "Format" means video codec plus audio codec, compared with the
>   programme tuned in on. Resolution changes play through, which was tested. Unknown formats
>   never stop a channel.
>   Making the end prompt needed one more change: **channel streams are now served from the
>   plugin's own endpoint**, `/LinearTv/Stream/{id}.ts` (`Api/ChannelStreamController`,
>   `LiveTv/StreamRegistry`), not Jellyfin's `/LiveTv/LiveStreamFiles`. Jellyfin's endpoint
>   wraps the stream in a `ProgressiveFileStream`, which keeps retrying for 30 s after the end.
>   And jellyfin-web buffers only 6 s ahead in Chrome/Edge/Firefox on fast connections, so
>   browsers would have held a frozen last frame for ~24 s. The new endpoint is anonymous like
>   Jellyfin's, keyed by the stream's unguessable GUID, and exists only while the stream is open.
>   (b) **Bump the PMT version number at each join** (and recompute its CRC) in `TsSplicer`.
>   That makes each change standards-correct, so a TV player *may* switch decoders cleanly.
>   Needs a real TV to judge.
>   (c) **Exclude AV1 items** from channels, with a count on the settings page, or transcode them.
>   (d) Transcode non-matching programmes: continuous everywhere, but a full re-encode of those.
>   (e) Single-format channels only: impractical with this library.
>
> - **Channels can be imported and exported as JSON (2026-10-01).** Channels get generated
>   outside Jellyfin, by hand or by an AI given a list of the library, and loaded live. The
>   Claude API was considered for generating them inside the plugin and decided against.
>   `POST /LinearTv/Channels/Import` and `GET /LinearTv/Channels/Export` are admin-only. The
>   format names content by title and year, or by series, season and episode, because item IDs
>   derive from file paths and differ between servers. Matching ignores case, punctuation and
>   accents, and allows a year off by one. Anything ambiguous or missing is refused, with
>   suggestions. Imports are all or nothing, offer a dry run, and either merge by channel number
>   or replace the whole list. User documentation: [channel-format.md](./channel-format.md).
>   Driven by `./x channels` (`tools/channels.sh`), which backs up before every import and
>   imports only a file that passed a check against that server, and by the repo's
>   `make-channels` skill (`.claude/skills/`), which holds the workflow and conventions for
>   generating channels in Claude Code.
>
> Code map: `Scheduling/` (pure: schedule, shuffle), `Library/ContentResolver.cs` (sources →
> playable items), `LiveTv/` (the `ILiveTvService`; `LinearLiveStream`, `ChannelStream` and
> `TsSplicer` for continuous playback; URL builder, API key, guide refresh), `Import/` (pure:
> the channel file format, name matching, merging; plus the library snapshot it matches
> against), `Api/` (the stream and import/export endpoints).

**Architecture: the plugin serves no media.** It implements `ILiveTvService` to publish
channels and an EPG, and when a channel is tuned it returns a `MediaSourceInfo` whose `Path`
points back at **Jellyfin's own `/Videos/{id}/stream` endpoint** with the live offset as a
query parameter. Jellyfin does everything else.

No ffmpeg in the plugin. No concat. No segmenting. No profile analysis. No normalisation
cache. The `Streaming/` layer from the previous plan does not exist.

```
offset == 0  →  static=true                        true direct play, zero server work
offset  > 0  →  videoCodec=copy&audioCodec=copy     copy remux, bit-identical, ~nil CPU
```

**Requirements satisfied**
- Appears as live TV natively, in the guide, on every client
- Not seekable (`IsInfiniteStream` is force-set — verified verbatim)
- Tune in mid-programme, from a wall-clock schedule
- Plugin never decodes, encodes, muxes or serves bytes
- No library watch-state pollution

**Known limitation:** when a programme ends, the stream ends. Client behaviour at that moment
is the one genuine unknown, and Spike 2 settles it before any code is written.

---

## 0. Spikes — before writing any C#

Both of these validate the entire architecture with no plugin at all. Do them first.

### Spike 1 — does a copy remux honour `startTimeTicks`? (~5 minutes)

```bash
curl "http://localhost:8096/Videos/{itemId}/stream\
?startTimeTicks=6000000000\
&container=ts&videoCodec=copy&audioCodec=copy\
&api_key={key}" -o test.ts
```

`6000000000` ticks = 10 minutes. Play `test.ts` and confirm it starts ~10 minutes in.

- **Works** → the whole plan is viable
- **Offset ignored** → fall back to programmes starting at the top (section 5.1); everything
  else in this plan is unchanged
- Measure the keyframe overshoot while you are here

> **Result (2026-09-30): PASS.** Run via `./x spike1` against the containerised test server,
> using generated clips with a burned-in clock (10s keyframe interval).
>
> | Requested | First frame | |
> |---|---|---|
> | 600s | 10:00.000 | keyframe-aligned |
> | 617s | 10:10.000 | 7s early |
> | 905s | 15:00.000 | 5s early |
> | 1234s | 20:30.000 | 4s early |
> | 1452s | 24:10.000 | 2s early |
>
> - Jellyfin's log confirms a genuine remux: `-ss <t> -codec:v:0 copy -codec:a:0 copy`.
>   Nothing decoded or encoded.
> - **Seek snaps to the keyframe *before* the target** — viewers join up to one keyframe
>   interval early, never late. No content is ever skipped. Real content typically keys every
>   2–10s. No compensation needed.
> - The remux is far faster than real time: 20 minutes of content in under a second. The cost
>   really is negligible.
> - **Found a correctness bug in the design — see 4.8.** A second request for the same item
>   returned the *first* request's output.
>
> Section 5.1 is not needed.

### Spike 2 — what do clients do when the stream ends? (~30 minutes)

Put that same URL in a plain `.m3u` file, add it as an M3U tuner in Jellyfin, and tune it from
your Android TV. Use a short clip so the end arrives quickly.

Questions: does the client sit on a black screen, return to the guide, or show an error? Does
Jellyfin re-request the stream? Does tuning again work cleanly?

The answer decides section 5.2, and it is worth knowing before committing.

> **Method superseded (2026-09-30): an M3U tuner tests the wrong code path.** M3U channels go
> through `SharedHttpStream`; plugin channels go through `ExclusiveLiveStream` (see section 1).
> Run as written, this spike failed on *tuning*, not on ending: `SharedHttpStream` deleted its
> buffer 15 ms after opening, because our remux reaches end-of-file almost instantly. That
> failure cannot happen on the plugin path, so it says nothing about us — and neither would any
> end-of-stream behaviour it showed.
>
> **Corrected method:** a throwaway, single-channel `ILiveTvService` inside the plugin —
> hard-coded channel, empty guide, `GetChannelStream` returning the remux URL built exactly as
> section 2 describes, with a fresh `PlaySessionId` and `ApiKey=`. Tune it through the API to
> check the server side, then from a real client for the questions above, plus one new one:
> does the client stream through Jellyfin, or try to fetch `Path` (`localhost:8096`) itself?
>
> This is the first plugin code, so it is a deliberate scope step, not a script.

---

## 1. Verified API surface

```csharp
// MediaBrowser.Controller/LiveTv/ILiveTvService.cs — the three that matter
Task<IEnumerable<ChannelInfo>> GetChannelsAsync(CancellationToken ct);
Task<IEnumerable<ProgramInfo>> GetProgramsAsync(string channelId, DateTime startUtc,
                                                DateTime endUtc, CancellationToken ct);
Task<MediaSourceInfo>          GetChannelStream(string channelId, string streamId,
                                                CancellationToken ct);
// remainder is DVR/timer plumbing — stub it
```

| Field | Who wins |
|---|---|
| `IsInfiniteStream` | **Jellyfin** — `mediaSource.IsInfiniteStream = true;` unconditional, first line of `Normalize()`. This is the non-seekable requirement, free. |
| `RequiresClosing`, `Type`, `BufferMs`, `OpenToken` | Jellyfin |
| `MediaStreams` | Jellyfin normalises; **injects dummies if we supply none** |
| `SupportsTranscoding` | Jellyfin — forced `true` for non-default services |
| `SupportsDirectPlay`, `SupportsDirectStream` | Us — never touched by the platform |
| `Protocol`, `Path` | Us (`Path` auto-filled only when empty) |
| `RunTimeTicks` | Us — never referenced by `Normalize()` |

**Corrected 2026-09-30.** An earlier version said our `Path` is pulled through
`SharedHttpStream` into a temp file. That was read from `M3UTunerHost` and **does not apply to
an `ILiveTvService` plugin.** From `LiveTvMediaSourceProvider.GetChannelStream`, verbatim:

```csharp
if (service is ISupportsDirectStreamProvider supportsManagedStream)
    ...
info = await service.GetChannelStream(channel.ExternalId, mediaSourceId, cancellationToken)...;
liveStream = new ExclusiveLiveStream(info, closeFn);
```

We do not implement `ISupportsDirectStreamProvider`, so we get **`ExclusiveLiveStream`**: no
temp-file buffer, no path rewrite, no sharing between viewers. `Path` stays our URL, and
Jellyfin reads it when a client streams the channel. Consequences:

- **No sharing** — each viewer is their own stream. Harmless here: 4.8 already gives every tune
  its own remux, and a remux costs almost nothing.
- **Not exposed to `SharedHttpStream`'s buffer deletion.** Observed on an M3U tuner: when the
  upstream source hits end-of-file, `SharedHttpStream` deletes its buffer — and because our
  remux delivers 20 minutes in under a second, it was deleted 15 ms after opening, before any
  client read it, so the tune 404'd. That would have been fatal, and it is the M3U path only.
- **Still to verify:** that every client streams through Jellyfin rather than trying to fetch
  `Path` itself. A client doing the latter would request `localhost:8096` from the TV. That is
  a Spike 2 question.

The lesson, recorded because it has now happened twice (see also `IsManifest()` in
feasibility 10.2): **anything learned from `M3UTunerHost` must be re-checked against the
`ILiveTvService` path before it is relied on.**

| Item | Value |
|---|---|
| Assembly | `Jellyfin.Plugin.LinearTv` |
| TFM | `net10.0` |
| Packages | `Jellyfin.Controller` 12.1.0, `Jellyfin.Model` 12.1.0 |
| `targetAbi` | `12.0.0.0` |

---

## 2. How it works

```
Android TV — Live TV → Guide → "90s Sitcoms" → tune
        │
        ▼
LinearTvService.GetChannelStream("90s-sitcoms")
        │   ScheduleClock: now → (Frasier S03E12, offset 14m02s)
        │
        │   MediaSourceInfo {
        │     Protocol = Http
        │     Path     = "<local>/Videos/{ep}/stream?startTimeTicks=8420000000
        │                 &container=ts&videoCodec=copy&audioCodec=copy&api_key=…"
        │   }
        ▼
Jellyfin fetches its own endpoint over loopback, applies -ss, copy-remuxes
        ▼
client plays
```

The plugin's entire job is arithmetic and a URL.

---

## 3. Layout

```
Jellyfin.Plugin.LinearTv/
  Plugin.cs                     BasePlugin<PluginConfiguration>, IHasWebPages
  PluginServiceRegistrator.cs   DI

  Configuration/
    PluginConfiguration.cs      channel definitions + settings
    configPage.html             channel builder

  Model/
    ChannelDefinition.cs        id, number, name, logo, sources, ordering, seed
    ScheduledProgram.cs         itemId, startUtc, endUtc, runtimeTicks

  Scheduling/                   ---- pure; no Jellyfin streaming types ----
    ContentResolver.cs          ChannelDefinition -> ordered IReadOnlyList<BaseItem>
    ScheduleBuilder.cs          materialises a timeline block
    ScheduleStore.cs            JSON persistence, plugin data dir
    ScheduleClock.cs            PURE: (timeline, nowUtc) -> (programme, offset, endsAt)

  LiveTv/
    LinearTvService.cs          ILiveTvService
    ChannelMapper.cs            -> ChannelInfo
    ProgramMapper.cs            -> ProgramInfo (guide metadata)
    StreamUrlBuilder.cs         offset -> the /Videos URL (static vs copy)

  Api/
    DebugController.cs          status + clock offset (dev only)
```

No streaming layer, no controller serving media.

---

## 4. Design decisions

### 4.1 Materialised timeline, not modulo arithmetic
`(now - epoch) % total` is tiny but loses the guide, and any edit rewrites history. Materialise
in 24h blocks, extend lazily, prune after ~7 days. The EPG falls straight out of it.

### 4.2 Persistence: JSON per channel in the plugin data dir
Not config XML (rewritten wholesale on every settings change). Not the Jellyfin DB (EF
migrations in a plugin, for no payoff).

### 4.3 One injected clock
Nothing in `Scheduling/` calls `DateTime.UtcNow`; take `TimeProvider` by DI. Section 7 depends
on this, and it only works if everything reads the same source.

### 4.4 Direct play whenever the offset is zero
`StreamUrlBuilder` emits `static=true` at offset 0 and the copy-remux form otherwise. Pay for
the remux only when actually joining mid-programme.

### 4.5 Join-in-progress threshold
Under N minutes left (default 5, 0 disables) skips to the next programme — which also lands on
offset 0, so it makes 4.4 fire more often.

### 4.6 Declare real `MediaStreams`
`Normalize()` injects dummy streams when none are supplied, making Jellyfin's client profile
matching meaningless. Populate them from the scheduled item so direct-play-vs-transcode is
decided correctly. Cheap, and it is the mitigation for JF #15338.

### 4.7 API key — use `ApiKey=`, never `api_key=`
Hold a plugin-owned key in configuration and use
`IServerApplicationHost.GetApiUrlForLocalAccess()` for the host part. Never log the key.

**Measured against 12.1 (2026-09-30), and it overturns the original wording here:**

| Method | Admin endpoint | `/Videos/{id}/stream` |
|---|---|---|
| no auth at all | 401 | **200** |
| `?api_key=` | **401** | 200 |
| `?api_key=` garbage | — | **200** |
| `X-Emby-Token` header | **401** | — |
| `?ApiKey=` | 200 | 200 |
| `Authorization: MediaBrowser Token="…"` | 200 | 200 |

Two facts follow:

1. **12.1 ships `EnableLegacyAuthorization: false`.** `api_key=` and `X-Emby-Token` are
   rejected. Use `ApiKey=` — it works whether or not legacy auth is on, so it is also correct
   on a server upgraded from an older release with legacy mode still enabled.
2. **The stream endpoint does not check authentication at all.** That is why Spike 1 appeared
   to work with `api_key=`: the key was never looked at. A plugin using `api_key=` would pass
   every test today and start failing with 401 the day Jellyfin secures that endpoint, with
   nothing in the plugin looking wrong.

So send a key that is genuinely valid, even though it is not currently checked, and pin the
parameter name with a unit test on `StreamUrlBuilder`. Note that no integration test through
the stream endpoint can catch a wrong key, precisely because of fact 2.

### 4.8 A fresh `PlaySessionId` on every tune — mandatory
Found by Spike 1. Jellyfin names transcode output from, verbatim
(`Jellyfin.Api/Helpers/StreamingHelpers.cs`, `GetOutputFilePath`):

```csharp
var data = $"{state.MediaPath}-{state.UserAgent}-{deviceId!}-{playSessionId!}";
var filename = data.GetMD5().ToString("N", CultureInfo.InvariantCulture);
```

**`StartTimeTicks` is not in that key.** Two tunes of the same programme with no session id
hash to the same file, and the second is served the first's output — observed: a request for
617s returned the 600s remux, and Jellyfin never ran a second ffmpeg. On a live channel that is
a viewer re-tuning twenty minutes later and landing twenty minutes in the past.

`StreamUrlBuilder` must therefore emit a new `PlaySessionId` (a GUID) on every call, and a unit
test must assert two consecutive URLs for the same item and offset differ.

---

## 5. Open behaviours

### 5.1 If Spike 1 fails
**Not needed — Spike 1 passed.** Retained for reference: had `startTimeTicks` been ignored under
`videoCodec=copy`, the fallback was `static=true` always, with programmes starting at the top.

### 5.2 End of programme
Decided by Spike 2. Options in increasing order of effort: let the stream end and the viewer
re-tunes; have `GetChannelStream` always return the *current* programme so a re-tune is
seamless; or revisit the concat design (previous plan, in git history) if seamless boundaries
turn out to matter more than simplicity.

Do not build for this until Spike 2 says what is actually needed.

---

## 6. Phases

| Phase | Content | Done when |
|---|---|---|
| **0** | Spikes 1 and 2 | The two questions above are answered |
| 1 | Skeleton: builds on 12.1, loads, CI zip | Appears in the dashboard |
| 2 | `ContentResolver` + channel definition UI | You can define a channel and preview its items |
| 3 | Schedule engine + unit tests | `dotnet test` proves the clock for any channel/time |
| 4 | **Guide only** — `ILiveTvService`, `GetChannelStream` throws | **Channels appear in the Live TV guide on your TV with correct programmes and artwork** |
| 5 | `StreamUrlBuilder` + real `GetChannelStream` | Tuning plays the right programme at the right offset, no seek bar |
| 6 | End-of-programme handling per 5.2 | A channel is usable for an evening |
| 7 | Polish: numbers, logos, filler, audio/subtitle prefs | — |

Phase 4 is the checkpoint worth respecting: two mappers and no streaming code, and it answers
"does this actually look like live TV" before anything else is built on it.

*Watch in Phase 2:* items with null `RunTimeTicks` cannot be scheduled — filter at resolve
time and show the count, or you get silent gaps.

**Observed, and it changes the handling:** null is usually *transient*. Jellyfin indexes items
before probing them, so immediately after a scan, freshly added episodes all read null for a
few seconds. Treat null as "not probed yet" — skip it for *this* build and pick it up on the
next — rather than permanently excluding the item or reporting it as broken. Only an item that
stays null across rebuilds is genuinely unschedulable.

---

## 7. Testing

Unit tests cover `ScheduleClock`, `ScheduleBuilder` and `StreamUrlBuilder` — all pure, all
cheap. Assert the generated URL exactly, including the `static=true` case.

**The core problem:** this is wall-clock driven, so the interesting moments are rare and cannot
be forced. Two affordances, both in from Phase 3:

1. **Debug clock offset.** `POST /LinearTv/Debug/Clock?offsetSeconds=1280` shifts the injected
   `TimeProvider` so you can jump to just before a boundary, repeatedly. Offset, not scaling —
   video still plays at 1x.
2. **Short-clip test channel.** 30-second clips give a boundary every 30 seconds with real
   media and real clients.

**Server.** A second Jellyfin in Docker, media mounted read-only, own config volume, port 8097.
Jellyfin clients support multiple servers, so your real Android TV can point at it — and client
behaviour is the part CI cannot cover.

**Dev loop (Windows host, Linux container).** Build on the host (or in an SDK container), drop
the output into a bind-mounted plugin directory, restart the container:

```
host:  ./artifacts/plugin/   →   container: /config/plugins/LinearTv/   (no version in the name)
docker restart jellyfin
```

Restart is required — there is no plugin hot reload. Nothing locks the DLL on Linux, so the
copy is safe while the container runs; only the restart picks it up. Script as `deploy.ps1`.

Step-debugging into a container needs `vsdbg` installed inside it and a `docker exec` attach —
real friction for occasional use. The status endpoint below plus `Debug`-level logging covers
most of what a debugger would, and this codebase is small and mostly pure functions. Reach for
`vsdbg` only if something genuinely opaque comes up.

**Per-plugin logging**, in the Serilog config in your config dir (confirm the filename on your
install — `logging.json` vs `logger.json` has varied):

```json
{ "Serilog": { "MinimumLevel": {
    "Default": "Information",
    "Override": { "Jellyfin.Plugin.LinearTv": "Debug" }
} } }
```

**Status endpoint.** `GET /LinearTv/Debug/Status` — per channel: current programme, offset,
next, seconds to transition, and the exact URL that would be issued. Most debugging is reading
this, not stepping through code.

**Safety.** Phases 1–4 are read-only. Phase 5 issues loopback HTTP requests to your own server;
nothing writes to the library or to user data at any point.

---

## 7a. Running inside a Linux container

Jellyfin runs in a Linux container and the plugin runs inside it. Four things follow.

### Timezone — the one that will actually bite
Containers commonly run **UTC** unless `TZ` is set, so `DateTime.Now` inside the container is
not your wall clock. For a *scheduling* plugin that matters.

- All internal scheduling stays in **UTC** (4.3 already requires this) — unaffected.
- Guide times are rendered by the **client**, in the viewer's own zone — also unaffected.
- The exposure is any user-facing schedule concept: "this channel's block starts at 20:00".
  That must resolve against an **explicitly configured IANA zone** stored in plugin config
  (e.g. `Europe/London`), never the container's ambient zone and never `DateTime.Now`.
- Set `TZ` on the container anyway so log timestamps are legible, but do not depend on it.

Worth a unit test across a DST transition, since that is exactly where a "starts at 20:00"
rule goes wrong and nobody notices until October.

### Paths are a non-issue, by design
The plugin never opens a media file — it builds a URL from an item ID. So container-vs-host
path mapping, the usual source of pseudo-TV pain, simply does not arise. Worth stating because
it is a real benefit of this architecture that the concat design would have forfeited.

### Filesystem is case-sensitive
The embedded resource path for `configPage.html` and the plugin folder name must match case
exactly. This silently works on Windows and fails on Linux, so it is a classic first-deploy
bug.

### Writable data directory and the loopback URL
- Schedule JSON goes under `IApplicationPaths` (never a hardcoded path), and the container's
  UID must own it. In linuxserver-style images that is `PUID`/`PGID`. Fail loudly on a
  permissions error rather than silently losing schedules.
- The `/Videos/...` URL is fetched **by Jellyfin, from inside the container**, so it must use
  `IServerApplicationHost.GetApiUrlForLocalAccess()` — internal port `8096`, not whatever port
  is published to the host. Hardcoding the published port works from your desk and fails in
  the container.

---

## 8. Out of scope

- DVR, timers, recording
- Any media handling in the plugin — it builds URLs, nothing else
- Seamless programme boundaries, unless Spike 2 says otherwise (5.2)
- Subtitle passthrough

---

## 9. Upstream opportunity

The single line forcing all of this is `mediaSource.IsInfiniteStream = true;` in
`LiveTvMediaSourceProvider.Normalize()`, commented "Not all of the plugins are setting this."

A start-offset field on `MediaSourceInfo`, or making that assignment conditional, would let a
Live TV source start mid-item under **pure direct play** with no remux. That is a small,
well-motivated upstream PR that would benefit every pseudo-TV project, and it is the principled
fix if the remux continues to grate.

---

## 10. First step

Spike 1. It is one `curl` and it decides whether this plan or section 5.1 is the plan.
