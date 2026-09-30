# Pseudo-Channels for Jellyfin — Feasibility Study

**Date:** 2026-09-28

**Question:** Can a Jellyfin plugin present library content as tunable pseudo-channels with a
live "where are we now" clock, *without* the plugin doing its own video encoding (the thing
that made Tunarr's output worse than Jellyfin's)?

**Verdict: Feasible, with one genuinely hard part.** The encoding can be pushed onto Jellyfin.
The *stream continuity across program boundaries* cannot be waved away — it is the actual
reason Tunarr and ErsatzTV encode, and it is the thing to design around.

---

## 1. Target platform

| Item | Value |
|---|---|
| Jellyfin stable | **12.1** (released 2026-09-15) |
| Plugin TFM | **net10.0** (required, not optional, for JF 12) |
| NuGet | `Jellyfin.Controller` / `Jellyfin.Model` **12.1.0** |
| Plugin ABI | `targetAbi: 12.0.0.0` in `build.yaml` |

Jellyfin 10.11.x still exists on `net9.0` with `Jellyfin.Controller 10.11.x`. Multi-targeting
`net9.0;net10.0` is a known pattern but doubles the CI and API-drift burden. Recommend
targeting 12.x only.

---

## 2. Which extension point

Three candidate plugin surfaces:

| Interface | What it gives you | Verdict |
|---|---|---|
| `IChannel` | A browsable folder of items in the "Channels" tab. No EPG, no tuning. | Wrong shape |
| `ITunerHost` | A tuner feeding Jellyfin's built-in Live TV (what M3U/HDHomeRun use) | Viable, lower level |
| **`ILiveTvService`** | Channels **+ EPG + stream resolution**, all served in-process | **Correct** |

`ILiveTvService` (verified against `MediaBrowser.Controller/LiveTv/ILiveTvService.cs` on master)
is the contract. The three methods that matter:

```csharp
Task<IEnumerable<ChannelInfo>> GetChannelsAsync(CancellationToken ct);
Task<IEnumerable<ProgramInfo>> GetProgramsAsync(string channelId, DateTime startUtc, DateTime endUtc, CancellationToken ct);
Task<MediaSourceInfo>          GetChannelStream(string channelId, string streamId, CancellationToken ct);
```

Everything else on the interface is DVR/timer plumbing (`CreateTimerAsync`,
`GetSeriesTimersAsync`, `ResetTuner`, ...) which can be stubbed with
`NotImplementedException` / empty collections. Registration is via
`IPluginServiceRegistrator.RegisterServices`.

Plugins may also register ordinary ASP.NET `ControllerBase` classes, so the plugin can expose
its own HTTP endpoints for the channel streams and for a config/preview UI.

So the shape is: **your channel list is your schedule, your EPG is generated from the
schedule, and `GetChannelStream` is asked "what do I play right now?"**. That maps almost
exactly onto your mental model.

---

## 3. Four hard constraints found in Jellyfin's source

These are the load-bearing findings. All were read from the current code, not assumed.

### 3.1 `MediaSourceInfo` has no start-offset field

Full property list read from `MediaBrowser.Model/Dto/MediaSourceInfo.cs`. There is
`RunTimeTicks` (duration), `Protocol`, `Path`, `TranscodingUrl`, `IsInfiniteStream`,
`BufferMs`, `RequiresLooping` ... and **nothing that says "begin at position N"**.

*Consequence:* you cannot hand Jellyfin a library file and say "start 14 minutes in". The
offset must be baked into whatever produces the bytes. This is the single most important
finding — it eliminates the simplest imaginable design.

### 3.2 `IsInfiniteStream` is force-set to `true` — you cannot opt out

`LiveTvMediaSourceProvider.Normalize()` overwrites whatever the service returns:
`IsInfiniteStream = true`, `RequiresClosing = true`, `Type = MediaSourceType.Default`,
`BufferMs ??= 1500`. It also normalises/repairs the `MediaStreams` you supply, and injects
dummy video/audio streams if you supply none.

*Consequence:* the "declare the channel as a finite, seekable item and let the client resume
at the live offset" trick is dead. A Live TV channel is, by construction, an unseekable
infinite stream. (This is fine — arguably correct for a channel — but it closes a door.)

### 3.3 An HTTP `Path` is pulled **server-side** and buffered to disk

> **Correction (2026-09-30): this section describes `M3UTunerHost`, not an `ILiveTvService`
> plugin.** Plugin streams are wrapped in `ExclusiveLiveStream` — no `SharedHttpStream`, no
> temp-file buffer, no path rewrite, no sharing between viewers. See `plan.md` section 1 for
> the source and the consequences. The claims below hold for M3U tuners only.

From `SharedHttpStream.cs`: when a live source has `Protocol = Http`, Jellyfin opens the URL
itself with `HttpClient` (redirects followed transparently), copies the bytes into a temp
file, and then **rewrites `MediaSource.Path` to `/LiveTv/LiveStreamFiles/{id}/stream.ts`**.
The client never sees your URL. No ffmpeg is involved in this hop — it is a plain byte copy.

*Consequence, and this is the good news:* your plugin's endpoint can be a **byte pump**, not
an encoder. And because Jellyfin fetches it in-process over loopback, the plugin can point
that URL at *Jellyfin's own* `/Videos/{itemId}/stream?...&startTimeTicks=N` endpoint — meaning
the seek and the container work are done by **Jellyfin's ffmpeg, with Jellyfin's argument
construction**, which is precisely what you asked for.

Note the stream is *shared*: two viewers on one channel share one buffer. That is correct
broadcast behaviour and a bonus.

### 3.4 Live TV to client transcode fallback is less reliable than library playback

Jellyfin issue #15338 ("Live TV stream does not transcode in browser when playback should be
possible", ErsatzTV HEVC source) was **closed as not-planned** in April 2026. The Live TV
playback path does not share all of the library path's profile-negotiation and fallback
logic.

*Consequence:* codec discipline matters. Whatever you hand to the Live TV pipeline should be
something your clients can play, because the automatic "Jellyfin will just transcode it"
safety net is weaker here than for normal library items.

---

## 4. Why Tunarr encodes (the crux)

It is tempting to read Tunarr's transcoding as over-engineering. It isn't. A channel must
emit **one continuous stream that never ends**, while the content behind it is a sequence of
separate files with different codecs, resolutions, frame rates, pixel formats and audio
layouts.

Concatenating those at the container level produces timestamp, PID and continuity-counter
discontinuities, and parameter changes mid-stream that most decoders will not follow. Tunarr
and ErsatzTV solve this by **normalising everything through a single encoder** — one ffmpeg
process, one consistent output format, seamless boundaries. The cost is exactly the quality
loss you noticed.

**So the real design question is not "can we avoid encoding" — it is "how do we get
continuity without normalising by encoding".** That framing drives everything below.

---

## 5. Architectures

### A — Live TV service, plugin as pure byte-proxy (no plugin ffmpeg at all)

```
client --tune--> Jellyfin LiveTV --http--> plugin /Channel/{id}.ts
                                              | (computes now -> item + offset)
                                              +--loopback http--> Jellyfin
                                                   /Videos/{item}/stream
                                                   ?container=ts
                                                   &videoCodec=copy&audioCodec=copy
                                                   &startTimeTicks=N
```

The plugin spawns **zero** processes. It copies bytes, and on EOF opens the next program's
URL and keeps writing to the same response.

- Quality is bit-exact (`copy` = remux, no re-encode). Any real encode is Jellyfin's.
- Native UX: appears in the TV guide, tunable from every client.
- **Boundary risk.** Two independently-remuxed TS streams glued end-to-end will have
  independent PID/PCR/CC state. Downstream may stall, glitch, or drop at each transition.
- Codec changes between programs (H.264 480p to HEVC 1080p) are unhandled.

### A-prime — Live TV service, plugin runs ffmpeg in **stream-copy only** mode (recommended)

Same as A, but instead of hand-gluing bytes the plugin drives a single long-lived ffmpeg
process in `-c copy` mode (concat), using **Jellyfin's own bundled ffmpeg** via `IMediaEncoder`.

- Still **no decoding and no encoding** — `-c copy` is a remux. Bit-exact output.
- ffmpeg handles PTS/PCR rewriting and continuity properly across the concat — this is the
  thing it is actually good at, and it is why this solves section 5-A's boundary problem.
- Native UX, same as A.
- Copy-concat still requires codec/parameter compatibility across consecutive programs.
  Mitigations: constrain a channel to one codec family at scan time; or accept a brief stream
  restart at incompatible boundaries; or fall back to letting Jellyfin transcode *that one
  item* to the channel's canonical format.
- Reintroduces an ffmpeg dependency in the plugin — but **only as a muxer**. This is the
  key distinction from Tunarr: Tunarr's problem was its *encoder settings*, not the mere
  presence of ffmpeg.

### B — No Live TV at all: session driver

Plugin computes the schedule and, to "tune", issues
`POST /Sessions/{id}/Playing?playCommand=PlayNow&itemIds={item}&startPositionTicks={offset}`
(in-process via `ISessionManager`). Verified: this endpoint takes `startPositionTicks` and
starts the client at that position. On program end, the plugin fires the next Play command.

- **Perfect** playback: ordinary library playback, full device-profile negotiation, direct
  play when possible, subtitles, audio tracks, HDR, all of it. Zero stream plumbing. Zero
  ffmpeg from the plugin. This is the highest-fidelity option by a wide margin.
- Genuinely simple back end — closest to your "just a timer" description.
- **Not a channel.** No TV guide entry, no "tune in" affordance on the client itself. You
  need a plugin web page to press "Tune", and the target client must be remote-controllable.
  On a TV client you'd be reaching for your phone to start the channel.
- Boundary is a visible stop/start, not a seamless cut.

### C — Degraded mode worth keeping in the back pocket

Within A / A-prime: if continuity proves intractable, serve exactly one program from its live
offset and let the stream end at the boundary. Channels become "tune in, catch the rest of
what's on". Much simpler, and honestly usable.

---

## 6. Risk register

| # | Risk | Severity | Evidence | Mitigation |
|---|---|---|---|---|
| 1 | TS concat discontinuity at program boundaries | **High** | sec. 4, 3.3 | A-prime (ffmpeg copy-concat); prototype first |
| 2 | Codec/param mismatch between consecutive programs | **High** | sec. 4 | Constrain channel codec at scan; per-item normalise fallback |
| 3 | Live TV transcode fallback weaker than library path | Medium | JF #15338 (not-planned) | Keep output in widely-playable TS profile |
| 4 | Stream-copy seek lands on keyframes only | Low | ffmpeg semantics | Accept a few seconds drift; snap schedule to GOP |
| 5 | Loopback auth to `/Videos/...` needs an API key | Low | — | Plugin-generated key, or use in-process APIs |
| 6 | Live TV API churn between JF 10.11 / 12.x | Medium | LiveTv refactor history | Target 12.x only; pin `Jellyfin.Controller 12.1.0` |
| 7 | Subtitle handling (PGS/ASS) through TS | Medium | container limits | Document as unsupported initially |

---

## 7. Recommendation

**Build A-prime,** with the schedule engine written so it is completely independent of the
delivery mechanism — so that B remains available as a second front end over the same core, and
C as a fallback.

The project decomposes into three genuinely separable layers:

1. **Scanner + channel definition** — pick library content, build a channel's playlist.
   No Jellyfin streaming API surface at all. Easy, testable, and where most of the *product*
   lives.
2. **Schedule/clock engine** — given a channel and `DateTime.UtcNow`, return
   `(itemId, offsetTicks, programEndsAt)`. Pure function, trivially unit-testable, no I/O.
   This is your "timer mechanism", and it is genuinely as simple as you expect.
3. **Delivery** — `ILiveTvService` + stream endpoint. This is where all the difficulty is
   concentrated.

Layers 1 and 2 are low-risk and immediately useful. Layer 3 is the whole feasibility question.

**Before committing to layer 3, do a throwaway spike:** hand-build a two-program copy-concat
TS from two real files in your library, feed it to Jellyfin as a static M3U tuner entry, and
watch what your actual clients do at the boundary. That single experiment settles risks 1, 2
and 3 in an afternoon and decides between A-prime and C. Everything else in the plan is
ordinary plugin work.

---

## 8. Open questions for the spike

- Does `/Videos/{id}/stream?container=ts&videoCodec=copy&audioCodec=copy&startTimeTicks=N`
  honour the offset in copy mode, and how far does it overshoot to the next keyframe?
- Does a copy-concat boundary survive Jellyfin's `SharedHttpStream` buffering *and* the
  downstream client?
- Which of your clients (web / Android TV / whatever you actually use) tolerate a mid-stream
  parameter change?
- Does the Live TV path transcode your library's worst-case codec for your worst-case client,
  or does it fail silently as in #15338?

---

## 9. Prior art

| Project | Approach | Relevance |
|---|---|---|
| Tunarr / dizqueTV | External server, HDHomeRun+M3U emulation, full encode | What you're replacing |
| ErsatzTV | Same shape as Tunarr, full encode | Same tradeoff, same reason |
| `JPKribs/jellyfin-plugin-livechannels` | **In-process Jellyfin plugin**, `ILiveTvService`, but still runs its own ffmpeg with HDR tone-mapping, pacing, encoder failover. Author states it is test-only and points users back to ErsatzTV/Tunarr. | Proves the plugin-side integration works; confirms nobody has solved the no-encode variant |
| `DrewThomasson/JellyfinTV` | External schedule simulator | Schedule-engine ideas only |

The gap in the ecosystem is exactly the thing you described: every existing option
re-encodes. Nobody has built the copy-only variant.

---

## 10. Addendum (2026-09-28, after UX review)

Architecture **B was tried and rejected on requirements**, not on technical grounds. The
requirement is now explicit:

> It must appear as live TV on the surface, and it must not be seekable.

B cannot satisfy that at any price — the client is playing an ordinary library item and knows
nothing about channels. Architecture **A-prime** is selected. Three further findings changed
the design.

### 10.1 The non-seekable requirement is now free

Finding 3.2 (`IsInfiniteStream` force-set to `true`, `RequiresClosing` forced, no way to opt
out) was logged as a constraint that closed a door. Under the new requirement it is the
feature being asked for. Live TV channels cannot be sought, paused into the future, or
scrubbed. Nothing needs building to get this.

### 10.2 Direct play is ours to declare — corrected

An earlier draft of this section claimed HLS sources can never be direct-played and treated
that as desirable, on the strength of `M3UTunerHost.IsManifest()` forcing
`SupportsDirectPlay = false` for `.m3u8`. **That was a bad generalisation.** `IsManifest()` is
that host's local policy for arbitrary *remote* manifests it cannot reason about. It is not a
platform rule and it does not apply to us.

Read directly from `LiveTvMediaSourceProvider`:

- `Normalize()` modifies `IsInfiniteStream`, the `MediaStreams` collection and their
  individual properties, and `SupportsTranscoding` (forced `true` for any non-default
  service).
- `GetMediaSourcesInternal()` additionally sets `Type`, `BufferMs`, `RequiresOpening`,
  `OpenToken`, and `Path` *only when it is empty*.
- **Neither method ever touches `SupportsDirectPlay` or `SupportsDirectStream`.**

So a plugin implementing `ILiveTvService` declares its own direct-play support, and the
platform honours it. Direct play is available, and refusing it would waste server resources
for no reason.

This makes **raw MPEG-TS the correct transport**: with `Protocol = Http`, a `.ts` path and
`video/MP2T`, the source qualifies for `SharedHttpStream`, which gives *both* direct play
*and* stream sharing — one server-side pull feeding every viewer of a channel.

Because `Normalize()` also forces `SupportsTranscoding = true`, Jellyfin retains its transcode
path for clients that genuinely cannot play the channel profile. Direct play for capable
clients, transcode for the rest, decided per client by Jellyfin's normal profile matching —
provided we declare accurate `MediaStreams` (see 10.6).

### 10.3 HLS has a purpose-built mechanism for exactly this problem

`#EXT-X-DISCONTINUITY` signals that the next segment may change encoding parameters, timeline,
codec, sample rate or channel layout. Players flush the decoder, re-init, and resync. It is
how broadcast ad insertion works, paired with `#EXT-X-DISCONTINUITY-SEQUENCE`.

Program boundaries in a pseudo-channel are the textbook case. *Naively* glued raw TS has no
equivalent signal — you are concatenating bytes and hoping.

Given 10.2 and 10.6 this is no longer the default design, because a single continuous ffmpeg
produces one coherent timeline rather than a glued one, and needs no discontinuity signal at
all. HLS is retained as a **per-channel fallback mode** for genuinely heterogeneous channels,
where discontinuity-tolerant playback is worth more than direct play.

### 10.4 A no-transcode mode is proven, shipping, and its limits are documented

ErsatzTV ships **HLS Direct**, which "does not transcode content and can perform better on low
power systems", with the caveat that "some clients will have issues at program boundaries".

Two things follow. First, the no-encode approach is not exotic — it exists in production.
Second, and more useful: ErsatzTV's **MPEG-TS mode, which *does* transcode, carries the same
program-boundary caveat**. Full re-encoding does not actually buy immunity here. That
meaningfully lowers the estimated cost of *not* encoding, since the thing being given up is
less valuable than it appeared in section 4.

Note also that ErsatzTV's MPEG-TS mode is described as "a light wrapper over the HLS Segmenter
streaming mode" — HLS is the substrate in the mature implementation of this idea too.

### 10.6 One ffmpeg, one continuous timeline — the mechanism that makes direct play work

The concat demuxer solves the boundary problem outright, in copy mode:

> The concat demuxer reads a list of files and demuxes them one after the other, as if all
> their packets had been muxed together, **with timestamps adjusted so that the first file
> starts at 0 and each next file starts where the previous one finishes.**

That is a single coherent output timeline from one long-lived process — not two streams glued
together — so PTS/DTS continuity and MPEG-TS continuity counters are correct by construction,
and the client's demuxer sees one unbroken stream. No discontinuity signalling is required
because there is no discontinuity.

The join offset is handled by the **`inpoint` directive**, per entry in the concat list:

```
file '/media/Frasier.S03E12.mkv'
inpoint 840.0
file '/media/Frasier.S03E13.mkv'
file '/media/Frasier.S03E14.mkv'
```

`inpoint` makes the demuxer seek to that timestamp when it opens the file. Documented caveat,
which matches risk 4 already logged: it "works best with intra frame codecs, because for
non-intra frame ones you will usually get extra packets before the actual in point" — i.e.
keyframe granularity, a few seconds of overshoot.

**The price, and it is a real one:** `-c copy` through the concat demuxer requires that
"all inputs share the same codec, resolution, and frame rate."

This promotes codec homogeneity from a mitigation to a **first-class design constraint**. A
channel is not an arbitrary bag of files; it is a set of content sharing one canonical media
profile. That has to be enforced when the channel is built, not discovered at playback.

One practical consequence: a concat list is finite, so it is generated from the materialised
timeline for a window (hours), and ffmpeg is restarted when the window is exhausted. Schedule
that restart on a programme boundary, and prefer to do it while the channel is idle.

### 10.7 Revised risk position

Risk 1 (boundary discontinuity) drops from **High** to **Low**: a single concat process
produces one continuous timeline, so the failure mode largely disappears.

Risk 2 (codec mismatch between programmes) stays **High** and is now unambiguously the primary
residual risk — it is the hard precondition of the whole design (10.6), not an edge case.
Mitigations, in order: enforce one canonical profile per channel when building it; surface
mismatches to the user at channel-build time rather than failing silently; optionally
pre-normalise non-conforming items *once* by having Jellyfin transcode them to the channel
profile and caching the result, preserving both the copy-only invariant and steady-state
direct play.

Risk 3 (weak Live TV transcode fallback, JF #15338) returns to **Medium** and needs active
management: with direct play enabled, a client that cannot handle the channel profile depends
on exactly the fallback that issue describes as unreliable. Mitigation is to declare accurate
`MediaStreams` on the `MediaSourceInfo` so Jellyfin's profile matching makes a correct
decision up front. Note `Normalize()` injects *dummy* video/audio streams when a service
supplies none — which would make profile matching meaningless. Supplying real codec metadata
is therefore mandatory, not cosmetic.

The per-viewer transcode cost introduced by the superseded HLS design is **withdrawn**.
Steady state is now: one copy-mode remux per *active channel*, shared across all viewers by
`SharedHttpStream`, and zero transcoding for compatible clients.

---

## 11. Decision record

Architectures were evaluated in this order. Recording the rejections because each was rejected
for a *different* reason, and the reasons are the useful part.

| # | Architecture | Outcome |
|---|---|---|
| B | Session driver, no Live TV | **Rejected on requirements.** Perfect playback, but the TV shows an ordinary episode: no guide entry, a working seek bar, and polluted Continue Watching. "Must appear as live TV and not be sought out" cannot be met this way at any price. |
| A-prime (HLS) | Plugin segments, Jellyfin normalises | **Rejected on a bad premise.** Built on `M3UTunerHost.IsManifest()` forcing `SupportsDirectPlay = false`, wrongly generalised to a platform rule. It is that host's local policy for untrusted remote manifests. See 10.2. |
| A-prime (concat) | One long-lived `-c copy` ffmpeg | **Rejected on complexity.** Technically sound and gives seamless boundaries with direct play, but requires every item in a channel to share codec, resolution and frame rate — dragging in profile analysis and a normalisation cache. Too much machinery for the benefit. Retained in git history if seamless boundaries later prove essential. |
| **Jellyfin remux URL** | `MediaSourceInfo.Path` → Jellyfin's own `/Videos/{id}/stream?startTimeTicks=…` | **Selected.** The plugin handles no media at all. |

### 11.1 The constraint that drove every one of these

`MediaSourceInfo` has no start-offset field (3.1), and `Normalize()` sets
`IsInfiniteStream = true` unconditionally as its first statement (3.2, verified verbatim).

Together those mean a client will never seek a Live TV source, so joining a programme mid-way
requires the *server* to emit a stream that already starts there. That is the entire reason any
remuxing exists in this design. It is not incidental complexity — it is the price of the
"live" in live TV.

The corollary is worth stating plainly: **non-seekability and mid-programme joining are the
same bit.** `IsInfiniteStream = true` grants the first and forbids the second. The user's
suggestion of driving the offset through Jellyfin's resume mechanism fails for exactly this
reason.

### 11.2 The remux is smaller than it sounds

`videoCodec=copy&audioCodec=copy` is a container rewrite, not a transcode. Nothing is decoded
or re-encoded; output is bit-identical; cost is low single-digit CPU and largely I/O bound.
Jellyfin's dashboard labels it "transcoding", which overstates it considerably.

It is also avoidable at offset 0, where `static=true` gives true direct play — and the
join-in-progress threshold pushes a meaningful share of tune-ins to exactly that case.

### 11.3 Cheapest possible validation

The selected design is testable with `curl` and a one-line `.m3u` file, before any C# exists.
See plan section 0. If a design cannot be validated that cheaply, it is probably the wrong
design.
