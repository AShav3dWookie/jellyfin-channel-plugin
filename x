#!/usr/bin/env bash
# Single entry point for development. Requires only Docker - no host SDK, no host tools. Building
# channels also runs two things natively, which ./x fetches or builds itself: a pinned ffmpeg, and
# the builder as a self-contained executable.
# Works in Git Bash on Windows and natively on Linux (including the NAS).
#
#   ./x build | test | deploy | up | down | logs | media | init | mirror | channels | ffmpeg
#       | builder | publish | unpublish | store | package | release | shell
#
set -euo pipefail
cd "$(dirname "$0")"

# Git Bash rewrites arguments that look like POSIX paths (/work/...) into Windows paths
# before they reach docker. Every path passed below is container-side, so disable that.
export MSYS_NO_PATHCONV=1

# On Linux, run containers as the invoking user so artifacts and test data are not
# root-owned. Docker Desktop ignores bind-mount ownership, so root is fine there.
if [[ "$(uname -s)" == Linux ]]; then
  HOST_UID="$(id -u)"; HOST_GID="$(id -g)"
else
  HOST_UID=0; HOST_GID=0
fi
export HOST_UID HOST_GID

PROJECT=src/Jellyfin.Plugin.LinearTv
ASSEMBLY=Jellyfin.Plugin.LinearTv
TESTS=(tests/Jellyfin.Plugin.LinearTv.Tests tests/LinearTv.Builder.Tests)
BUILDER=src/LinearTv.Builder
CONFIGURATION=Debug

# The one ffmpeg every channel is encoded with (docs/architecture.md, "Fixed format"). Changing it
# changes the output: raise EncodeProfile.Revision with it.
FFMPEG_RELEASE=autobuild-2026-10-23-13-04
FFMPEG_BUILD=ffmpeg-n8.1.3-24-g92611900bb
if [[ "$(uname -s)" == Linux ]]; then
  FFMPEG_ASSET="$FFMPEG_BUILD-linux64-gpl-8.1.tar.xz"
  FFMPEG_SHA256=d0eb612b8994407f78413e2929cb4b073bc699c8ab79e198b31624736fdc2f93
  BUILDER_RID=linux-x64; EXE=""
else
  FFMPEG_ASSET="$FFMPEG_BUILD-win64-gpl-8.1.zip"
  FFMPEG_SHA256=ec297d65145f71686498436bb7d5281f540d26fb6791072ba73b194498581b22
  BUILDER_RID=win-x64; EXE=".exe"
fi
FFMPEG_DIR="artifacts/tools/${FFMPEG_ASSET%.zip}"; FFMPEG_DIR="${FFMPEG_DIR%.tar.xz}"
BUILDER_EXE="artifacts/builder/linear-tv-builder$EXE"

# A path for programs that run natively: Windows form under Git Bash.
native_path() { if command -v cygpath >/dev/null; then cygpath -wa "$1"; else realpath -m "$1"; fi; }

die()  { echo "error: $*" >&2; exit 1; }
info() { echo "==> $*"; }

# Rebuilds the SDK image when Dockerfile.dev changes. The rebuild checks the base image online,
# so offline it fails; then the image already built is good enough, and work on the local
# network (the test server, the NAS) carries on.
sdk() {
  if ! docker compose build -q sdk >/dev/null 2>&1; then
    docker image inspect linear-tv-sdk:dev >/dev/null 2>&1 \
      || die "can't build the SDK image (offline?), and there's no earlier build to fall back on"
    echo "==> SDK image not rebuilt (offline?): using the existing one" >&2
  fi
  docker compose run --rm sdk "$@"
}
# SVT_LOG=1: the AV1 encoder ignores ffmpeg's log level and prints its config unless told.
ffmpeg() { docker compose run --rm -e SVT_LOG=1 ffmpeg "$@"; }

ensure_dirs() {
  mkdir -p artifacts/build artifacts/plugin artifacts/dist artifacts/repo \
           testdata/media testdata/store testdata/jellyfin/config testdata/jellyfin/cache
}

jellyfin_running() {
  [[ -n "$(docker compose ps --status running -q jellyfin 2>/dev/null)" ]]
}

cmd_build() {
  ensure_dirs
  info "Building $ASSEMBLY ($CONFIGURATION)"
  sdk dotnet build "$PROJECT" -c "$CONFIGURATION" -o artifacts/build
}

cmd_test() {
  local project
  for project in "${TESTS[@]}"; do
    sdk dotnet test "$project" -c "$CONFIGURATION"
  done
}

cmd_deploy() {
  cmd_build
  # Ship only what build.yaml lists (plus the PDB for stack traces). The full build output
  # contains Jellyfin.Controller.dll and friends, which must not shadow the server's own.
  info "Staging plugin into artifacts/plugin"
  cp "artifacts/build/$ASSEMBLY.dll" artifacts/plugin/
  cp "artifacts/build/$ASSEMBLY.pdb" artifacts/plugin/ 2>/dev/null || true
  # Jellyfin writes meta.json here on first load and then reports the version from it, not from
  # the DLL - so a version bump would keep showing the old version. Let it regenerate.
  rm -f artifacts/plugin/meta.json

  if jellyfin_running; then
    info "Restarting jellyfin (plugins load only at startup)"
    docker compose restart jellyfin >/dev/null
  else
    cmd_up
  fi
}

cmd_up() {
  ensure_dirs
  docker compose up -d jellyfin
  info "Test server: http://localhost:8097  (login: dev / dev, once ./x init has run)"
}

cmd_down() { docker compose down; }

cmd_logs() { docker compose logs -f --tail=200 jellyfin; }

# Clip with a large burned-in running clock, so any offset can be verified by eye.
#   make_clip <path> <seconds> <label> <colour> <tone Hz> [size] [video args] [audio args]
# Defaults: 640x360 H.264 + AAC, the format of the original test clips.
H264="-c:v libx264 -preset ultrafast -crf 30 -pix_fmt yuv420p"
HEVC="-c:v libx265 -preset ultrafast -crf 32 -pix_fmt yuv420p -x265-params log-level=error"
AV1="-c:v libsvtav1 -preset 12 -crf 45 -pix_fmt yuv420p"
XVID="-c:v mpeg4 -q:v 5 -vtag XVID"
AAC="-c:a aac -b:a 64k"
AC3="-c:a ac3 -b:a 192k"
EAC3="-c:a eac3 -b:a 192k"

make_clip() {
  local out="$1" seconds="$2" label="$3" colour="$4" freq="$5"
  local size="${6:-640x360}" video="${7:-$H264}" audio="${8:-$AAC}"
  [[ -f "testdata/media/$out" ]] && { echo "    exists: $out"; return 0; }
  echo "    $out"
  local font=/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf
  local faststart=""
  [[ "$out" == *.mp4 ]] && faststart="-movflags +faststart"
  # shellcheck disable=SC2086  # the codec variables are deliberately word-split
  ffmpeg -y \
    -f lavfi -i "color=c=${colour}:s=${size}:r=25:d=${seconds}" \
    -f lavfi -i "sine=frequency=${freq}:sample_rate=48000:duration=${seconds}" \
    -vf "drawtext=fontfile=${font}:text='${label}':fontsize=36:fontcolor=white:x=(w-tw)/2:y=60,drawtext=fontfile=${font}:text='%{pts\:hms}':fontsize=72:fontcolor=yellow:box=1:boxcolor=black@0.6:boxborderw=12:x=(w-tw)/2:y=(h-th)/2" \
    $video $audio $faststart \
    "/media/$out"
}

cmd_media() {
  ensure_dirs
  local colours=(0x1f3a5f 0x5f1f3a 0x1f5f3a 0x5f4a1f 0x3a1f5f 0x1f5f5f)

  info "Generating 6 x 30 min episodes (normal programmes)"
  mkdir -p "testdata/media/shows/Clock Show/Season 01"
  for i in 1 2 3 4 5 6; do
    make_clip "shows/Clock Show/Season 01/Clock Show - S01E0$i.mp4" 1800 \
      "Clock Show S01E0$i" "${colours[$((i-1))]}" $((300 + i * 60))
  done

  info "Generating 6 x 30 s episodes (fast boundary testing)"
  mkdir -p "testdata/media/shows/Short Show/Season 01"
  for i in 1 2 3 4 5 6; do
    make_clip "shows/Short Show/Season 01/Short Show - S01E0$i.mp4" 30 \
      "Short Show S01E0$i" "${colours[$((i-1))]}" $((600 + i * 60))
  done

  # Real libraries mix formats, even within one series (measured on the user's library: 31 of 46
  # series switch codec between episodes). Played in order, each join tests one transition.
  info "Generating 7 x 30 s episodes in different formats (mixed-format testing)"
  local d="shows/Mixed Show/Season 01"
  mkdir -p "testdata/media/$d"
  make_clip "$d/Mixed Show - S01E01.mkv" 30 "S01E01 H264 AAC"          0x1f3a5f 700  640x360  "$H264" "$AAC"
  make_clip "$d/Mixed Show - S01E02.mkv" 30 "S01E02 HEVC AAC"          0x5f1f3a 760  640x360  "$HEVC" "$AAC"
  make_clip "$d/Mixed Show - S01E03.mkv" 30 "S01E03 H264 AC3"          0x1f5f3a 820  640x360  "$H264" "$AC3"
  make_clip "$d/Mixed Show - S01E04.mkv" 30 "S01E04 AV1 AAC"           0x5f4a1f 880  640x360  "$AV1"  "$AAC"
  make_clip "$d/Mixed Show - S01E05.avi" 30 "S01E05 XVID AC3 AVI"      0x3a1f5f 940  640x360  "$XVID" "$AC3"
  make_clip "$d/Mixed Show - S01E06.mkv" 30 "S01E06 HEVC 720p EAC3"    0x1f5f5f 1000 1280x720 "$HEVC" "$EAC3"
  # Same format as S01E01 at a different resolution: the loop's E07 -> E01 join is a resolution
  # change with no codec change, which channels deliberately play straight through.
  make_clip "$d/Mixed Show - S01E07.mkv" 30 "S01E07 H264 720p AAC"     0x5f5f1f 1060 1280x720 "$H264" "$AAC"

  make_films

  info "Done. Run ./x init to set up the test server library."
}

# An SRT file with one cue every 5 seconds, so any frame grab shows whether it was burned in.
make_srt() {
  local out="$1" text="$2" seconds="$3" t n=1
  : > "$out"
  for ((t = 0; t < seconds; t += 5)); do
    printf '%d\n00:%02d:%02d,000 --> 00:%02d:%02d,000\n%s\n\n' "$n" $((t / 60)) $((t % 60)) $(((t + 4) / 60)) $(((t + 4) % 60)) "$text" >> "$out"
    n=$((n + 1))
  done
}

# One test film: 60 s, a label and running clock on screen, an .nfo naming its production
# country (what the builder falls back on without TMDb), plus whatever streams follow.
#   make_film <title> <year> <country> <label> <size> <extra ffmpeg args...>
# Input 0 is the picture; extra args add inputs 1+ and all the -map/-c/-metadata options.
make_film() {
  local title="$1" year="$2" country="$3" label="$4" size="$5"; shift 5
  local dir="films/$title ($year)" file="$title ($year).mkv"
  [[ -f "testdata/media/$dir/$file" ]] && { echo "    exists: $dir"; return 0; }
  echo "    $dir"
  mkdir -p "testdata/media/$dir"
  printf '<?xml version="1.0" encoding="utf-8"?>\n<movie>\n  <title>%s</title>\n  <year>%s</year>\n  <country>%s</country>\n</movie>\n' \
    "$title" "$year" "$country" > "testdata/media/$dir/$title ($year).nfo"
  local font=/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf
  ffmpeg -y \
    -f lavfi -i "testsrc2=s=${size}:r=25:d=60,drawtext=fontfile=${font}:text='${label}':fontsize=28:fontcolor=white:box=1:boxcolor=black@0.7:x=(w-tw)/2:y=40,drawtext=fontfile=${font}:text='%{pts\:hms}':fontsize=56:fontcolor=yellow:box=1:boxcolor=black@0.6:x=(w-tw)/2:y=(h-th)/2" \
    "$@" "/media/$dir/$file"
}

# Films for the audio, subtitle and picture rules (docs/architecture.md). Each label says what
# the built channel should show; the tones tell the audio tracks apart (Japanese 1 kHz, English
# 300 Hz, others 600 Hz).
make_films() {
  info "Generating 8 x 60 s films for the language and format rules"
  local src=testdata/media/_src
  mkdir -p "$src"
  make_srt "$src/full.srt" "FULL ENGLISH SUBTITLES" 60
  make_srt "$src/forced.srt" "FORCED SUBTITLES" 60
  make_srt "$src/wrong.srt" "WRONG: FULL SUBS ON ENGLISH AUDIO" 60
  local tone="sine=sample_rate=48000:duration=60:frequency"
  local x264=(-c:v libx264 -preset ultrafast -crf 28 -pix_fmt yuv420p)

  make_film "Lang Test" 2001 Japan "EXPECT JAPANESE AUDIO (1 kHz) + FULL ENGLISH SUBS" 1280x720 \
    -f lavfi -i "$tone=300" -f lavfi -i "$tone=1000" -i /media/_src/full.srt -i /media/_src/forced.srt \
    -map 0:v -map 1:a -map 2:a -map 3 -map 4 "${x264[@]}" -c:a aac -b:a 96k -c:s srt \
    -metadata:s:a:0 language=eng -metadata:s:a:0 title=English -disposition:a:0 default \
    -metadata:s:a:1 language=jpn -metadata:s:a:1 title=Japanese -disposition:a:1 0 \
    -metadata:s:s:0 language=eng -metadata:s:s:0 title=English \
    -metadata:s:s:1 language=eng -metadata:s:s:1 title=Signs -disposition:s:1 forced

  make_film "Forced Test" 2002 "United Kingdom" "EXPECT ENGLISH AUDIO + FORCED SUBS ONLY" 1280x720 \
    -f lavfi -i "$tone=300" -i /media/_src/wrong.srt -i /media/_src/forced.srt \
    -map 0:v -map 1:a -map 2 -map 3 "${x264[@]}" -c:a aac -b:a 96k -c:s srt \
    -metadata:s:a:0 language=eng -metadata:s:s:0 language=eng -metadata:s:s:0 title=English \
    -metadata:s:s:1 language=eng -metadata:s:s:1 "title=English (Forced)"

  # Square box drawn at 1024x576, then stored anamorphic (720x576, 16:9) and interlaced, as a DVD.
  make_film "Anamorphic Test" 2003 "United Kingdom" "ANAMORPHIC + INTERLACED - BOX MUST BE SQUARE" 1024x576 \
    -f lavfi -i "$tone=300" \
    -vf "drawbox=x=362:y=188:w=300:h=300:color=white:t=8,scale=720:576,setsar=64/45,fps=50,tinterlace=interleave_top,fieldorder=tff" \
    -map 0:v -map 1:a -c:v mpeg2video -b:v 6M -flags +ildct+ilme -top 1 -c:a ac3 -b:a 192k -metadata:s:a:0 language=eng

  make_film "Scope Test" 2004 France "EXPECT LETTERBOX + FRENCH AUDIO + FULL ENGLISH SUBS" 1920x800 \
    -f lavfi -i "$tone=600" -i /media/_src/full.srt \
    -map 0:v -map 1:a -map 2 "${x264[@]}" -c:a aac -b:a 96k -c:s srt \
    -metadata:s:a:0 language=fre -metadata:s:s:0 language=eng

  make_film "HDR Test" 2005 "United States of America" "HDR10 - EXPECT NATURAL SDR COLOURS" 1920x1080 \
    -f lavfi -i "$tone=300" -vf setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc \
    -map 0:v -map 1:a -c:v libx265 -preset ultrafast -crf 30 -pix_fmt yuv420p10le \
    -x265-params "log-level=error:colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc:hdr10=1:max-cll=1000,400" \
    -color_primaries bt2020 -color_trc smpte2084 -colorspace bt2020nc -c:a aac -b:a 96k -metadata:s:a:0 language=eng

  make_film "Dub Only" 2006 Japan "ENGLISH DUB ONLY - LEFT OUT UNLESS AUDIO ENG ASKED" 1280x720 \
    -f lavfi -i "$tone=300" -map 0:v -map 1:a "${x264[@]}" -c:a aac -b:a 96k -metadata:s:a:0 language=eng

  make_film "External Subs" 2007 Germany "EXPECT GERMAN AUDIO + EXTERNAL ENGLISH SUBS" 1280x720 \
    -f lavfi -i "$tone=600" -map 0:v -map 1:a "${x264[@]}" -c:a aac -b:a 96k -metadata:s:a:0 language=ger
  [[ -f "testdata/media/films/External Subs (2007)/External Subs (2007).en.srt" ]] \
    || make_srt "testdata/media/films/External Subs (2007)/External Subs (2007).en.srt" "EXTERNAL ENGLISH SUBTITLES" 60

  # An apostrophe in the path: the subtitle filter's escaping.
  make_film "Schindler's Test" 2008 Poland "APOSTROPHE PATH - EXPECT FULL ENGLISH SUBS" 1280x720 \
    -f lavfi -i "$tone=600" -i /media/_src/full.srt \
    -map 0:v -map 1:a -map 2 "${x264[@]}" -c:a aac -b:a 96k -c:s srt \
    -metadata:s:a:0 language=pol -metadata:s:s:0 language=eng
}

# Idempotent test-server setup: first-run wizard, a Shows library, an API key.
#
# The library is created with every remote metadata fetcher disabled. Otherwise TMDb matches
# the generated series to real shows ("Clock Show" -> "The 11 O'Clock Show", episodes renamed
# "Pilot"), so the test library would depend on what an internet service returns that day.
# An NFO with <lockdata> does not prevent this; only the library options do.
#
# Credentials are dev/dev. This is a disposable local test server - testdata/ is gitignored.
cmd_init() {
  jellyfin_running || die "test server is not running - ./x up"
  [[ -d testdata/media/shows ]] || die "no test media - ./x media"

  # Wait for as many episodes and films as there are video files, however many ./x media made.
  local expected films
  expected=$(find testdata/media/shows -type f \( -name '*.mp4' -o -name '*.mkv' -o -name '*.avi' \) | wc -l)
  films=$(find testdata/media/films -type f -name '*.mkv' 2>/dev/null | wc -l)

  EXPECTED="$expected" EXPECTED_FILMS="$films" docker compose run --rm -T -e EXPECTED -e EXPECTED_FILMS sdk bash -s <<'EOF'
set -euo pipefail
J=http://jellyfin:8096; H='Content-Type: application/json'
AUTH='MediaBrowser Client="LinearTvDev", Device="dev", DeviceId="lineartv-dev", Version="1.0"'

# A single 200 is not readiness. Observed on a fresh start: 503 -> 200 -> connection refused
# -> 503 -> 200. Jellyfin answers briefly, then restarts its web host partway through startup,
# so calls made on the first 200 land in the gap. Require success to hold.
ok=0
for i in $(seq 1 120); do
  if curl -sf "$J/System/Info/Public" >/dev/null; then ok=$((ok + 1)); else ok=0; fi
  [ "$ok" -ge 3 ] && break
  sleep 1
done
[ "$ok" -ge 3 ] || { echo "error: server never became ready" >&2; exit 1; }

if [ "$(curl -sf "$J/System/Info/Public" | jq -r .StartupWizardCompleted)" != true ]; then
  echo "==> Completing first-run wizard (user dev / password dev)"
  curl -sf -X POST "$J/Startup/Configuration" -H "$H" \
    -d '{"UICulture":"en-GB","MetadataCountryCode":"GB","PreferredMetadataLanguage":"en"}'
  curl -sf "$J/Startup/User" >/dev/null
  curl -sf -X POST "$J/Startup/User" -H "$H" -d '{"Name":"dev","Password":"dev"}'
  curl -sf -X POST "$J/Startup/RemoteAccess" -H "$H" -d '{"EnableRemoteAccess":true}'
  curl -sf -X POST "$J/Startup/Complete"
fi

TOKEN=$(curl -sf -X POST "$J/Users/AuthenticateByName" -H "$H" -H "Authorization: $AUTH" \
  -d '{"Username":"dev","Pw":"dev"}' | jq -r .AccessToken)
A="Authorization: $AUTH, Token=\"$TOKEN\""

# Jellyfin 12 hides users from the login screen by default, leaving empty fields and nothing to
# say what to type. On a disposable dev server there is no reason to hide the account.
ME=$(curl -sf "$J/Users/Me" -H "$A")
if [ "$(echo "$ME" | jq -r .Policy.IsHidden)" = true ]; then
  echo "==> Showing the dev user on the login screen"
  echo "$ME" | jq '.Policy | .IsHidden = false' \
    | curl -sf -X POST "$J/Users/$(echo "$ME" | jq -r .Id)/Policy" -H "$A" -H "$H" -d @-
fi

if ! curl -sf "$J/Library/VirtualFolders" -H "$A" | jq -e '.[] | select(.Name=="Shows")' >/dev/null; then
  echo "==> Creating Shows library (remote metadata disabled)"
  curl -sf -X POST "$J/Library/VirtualFolders?name=Shows&collectionType=tvshows&paths=/media/shows&refreshLibrary=true" \
    -H "$A" -H "$H" -d '{
      "LibraryOptions": {
        "EnableRealtimeMonitor": false,
        "MetadataSavers": [],
        "TypeOptions": [
          {"Type":"Series",  "MetadataFetchers":[], "ImageFetchers":[]},
          {"Type":"Season",  "MetadataFetchers":[], "ImageFetchers":[]},
          {"Type":"Episode", "MetadataFetchers":[], "ImageFetchers":[]}
        ]
      }}'
elif [ "$(curl -sf "$J/Items?Recursive=true&IncludeItemTypes=Episode" -H "$A" | jq .TotalRecordCount)" -lt "$EXPECTED" ] \
  || [ "$(curl -sf "$J/Items?Recursive=true&IncludeItemTypes=Movie&Fields=Path" -H "$A" \
          | jq '[.Items[] | select(.Path // "" | startswith("/media/films"))] | length')" -lt "$EXPECTED_FILMS" ]; then
  # Real-time monitoring is off, so clips added by ./x media need an explicit scan.
  echo "==> Scanning for new test media"
  curl -sf -X POST "$J/Library/Refresh" -H "$A"
fi

# Films for the language and format rules. Remote metadata off as for Shows; each film's .nfo
# gives its production country, which is what the builder falls back on without TMDb.
if [ "$EXPECTED_FILMS" -gt 0 ] && ! curl -sf "$J/Library/VirtualFolders" -H "$A" | jq -e '.[] | select(.Name=="Films")' >/dev/null; then
  echo "==> Creating Films library (remote metadata disabled)"
  curl -sf -X POST "$J/Library/VirtualFolders?name=Films&collectionType=movies&paths=/media/films&refreshLibrary=true" \
    -H "$A" -H "$H" -d '{
      "LibraryOptions": {
        "EnableRealtimeMonitor": false,
        "MetadataSavers": [],
        "TypeOptions": [ {"Type":"Movie", "MetadataFetchers":[], "ImageFetchers":[]} ]
      }}'
fi

# Items are indexed before they are probed, so RunTimeTicks is briefly null. Wait for both.
echo "==> Waiting for scan and probe ($EXPECTED episodes, $EXPECTED_FILMS films)"
for i in $(seq 1 90); do
  st=$(curl -sf "$J/Items?Recursive=true&IncludeItemTypes=Episode,Movie&Fields=Path" -H "$A" \
       | jq -r '"\([.Items[] | select(.Type=="Episode")] | length) \([.Items[] | select(.Type=="Movie" and (.Path // "" | startswith("/media/films")))] | length) \([.Items[] | select(.RunTimeTicks == null and ((.Path // "") | startswith("/media/mirror") | not))] | length)"')
  set -- $st
  [ "$1" -ge "$EXPECTED" ] && [ "$2" -ge "$EXPECTED_FILMS" ] && [ "$3" -eq 0 ] && break
  sleep 2
done
[ "$1" -ge "$EXPECTED" ] && [ "$2" -ge "$EXPECTED_FILMS" ] && [ "$3" -eq 0 ] \
  || { echo "error: scan incomplete ($1 of $EXPECTED episodes, $2 of $EXPECTED_FILMS films, $3 unprobed)" >&2; exit 1; }

# The plugin's store is /store in the container (testdata/store), where ./x publish copies builds.
PLUGIN=437f1b36-c02d-4c02-9083-19440d7f243b
if CONF=$(curl -sf "$J/Plugins/$PLUGIN/Configuration" -H "$A"); then
  if [ "$(echo "$CONF" | jq -r .StorePath)" != /store ]; then
    echo "==> Setting Linear TV's store folder to /store"
    echo "$CONF" | jq '.StorePath = "/store"' | curl -sf -X POST "$J/Plugins/$PLUGIN/Configuration" -H "$A" -H "$H" -d @-
  fi
else
  echo "==> Linear TV isn't loaded (./x deploy first?): set its store folder to /store, or run ./x init again"
fi

if ! curl -sf "$J/Auth/Keys" -H "$A" | jq -e '.Items[] | select(.AppName=="LinearTvDev")' >/dev/null; then
  curl -sf -X POST "$J/Auth/Keys?app=LinearTvDev" -H "$A"
fi
KEY=$(curl -sf "$J/Auth/Keys" -H "$A" | jq -r '[.Items[] | select(.AppName=="LinearTvDev")][0].AccessToken')
printf 'API_KEY=%s\n' "$KEY" > artifacts/.testserver.env

echo "==> Test library"
curl -sf "$J/Items?Recursive=true&IncludeItemTypes=Episode&SortBy=SeriesSortName,ParentIndexNumber,IndexNumber" -H "$A" \
  | jq -r '.Items[] | select(.RunTimeTicks != null) | "    \(.Id)  \(.SeriesName) / \(.Name)  \(.RunTimeTicks/10000000|floor)s"'
echo "==> API key saved to artifacts/.testserver.env - log in at http://localhost:8097 as dev / dev"
EOF
}

# A stand-in for a real library, for testing channel import against real titles.
#
# Copies the folder structure of real Movies and TV folders (a NAS share is fine) as EMPTY
# placeholder files under testdata/media/mirror/. The source is only listed: no file is opened,
# so it works on a read-only share, or on one whose files this user can't read. Jellyfin then
# identifies the placeholders by name through TMDb, as a production server would, giving real
# titles, years, episode numbers and collections. Item ids differ from the production server's
# (different paths), which is exactly the cross-server case the import format is built for.
# The placeholders don't play.
cmd_mirror() {
  local movies="${1:-}" tv="${2:-}"
  [[ -n "$movies" && -n "$tv" ]] || die "usage: ./x mirror <moviesDir> <tvDir>   e.g. ./x mirror //TRUENAS/media/Movies //TRUENAS/media/TV"
  jellyfin_running || die "test server is not running - ./x up"
  [[ -f artifacts/.testserver.env ]] || die "no API key - run ./x init"

  local name src dest n skipped
  for name in movies tv; do
    [[ $name == movies ]] && src="$movies" || src="$tv"
    [[ -d "$src" ]] || die "cannot list $src"
    dest="testdata/media/mirror/$name"
    skipped="$PWD/artifacts/mirror-$name-skipped.txt"
    rm -rf "$dest" && mkdir -p "$dest"
    info "Mirroring $src -> $dest (names only)"
    ( cd "$src" && find . -type f \( -iname '*.mkv' -o -iname '*.mp4' -o -iname '*.m4v' -o -iname '*.avi' \
        -o -iname '*.ts' -o -iname '*.m2ts' -o -iname '*.wmv' -o -iname '*.mov' -o -iname '*.mpg' -o -iname '*.webm' \) \
        -print0 2>"$skipped" || true ) \
      | while IFS= read -r -d '' f; do mkdir -p "$dest/$(dirname "$f")" && : > "$dest/$f"; done
    n=$(find "$dest" -type f | wc -l)
    info "  $n placeholder files"
    # Folders this user can't list are skipped, not forced: their permissions are the NAS's call.
    if [[ -s "$skipped" ]]; then
      info "  skipped $(wc -l < "$skipped") unreadable folder(s); see artifacts/mirror-$name-skipped.txt"
    fi
  done

  docker compose run --rm -T sdk bash -s <<'EOF'
set -euo pipefail
J=http://jellyfin:8096; H='Content-Type: application/json'
KEY=$(sed -n 's/^API_KEY=//p' artifacts/.testserver.env)
A="Authorization: MediaBrowser Token=\"$KEY\""

# Remote metadata ON (that's the point), but nothing saved beside the media and no trickplay or
# chapter images: the placeholders have no frames, and /media is mounted read-only anyway.
opts='{"LibraryOptions":{"EnableRealtimeMonitor":false,"MetadataSavers":[],"SaveLocalMetadata":false,
  "EnableTrickplayImageExtraction":false,"EnableChapterImageExtraction":false,
  "AutomaticallyAddToCollection":true,"PreferredMetadataLanguage":"en","MetadataCountryCode":"GB"}}'

for lib in "Mirror Movies|movies|/media/mirror/movies" "Mirror TV|tvshows|/media/mirror/tv"; do
  IFS='|' read -r name type path <<<"$lib"
  if curl -sf "$J/Library/VirtualFolders" -H "$A" | jq -e --arg n "$name" '.[] | select(.Name==$n)' >/dev/null; then
    echo "==> Rescanning $name"
  else
    echo "==> Creating $name library"
    curl -sf -X POST "$J/Library/VirtualFolders?name=$(jq -rn --arg n "$name" '$n|@uri')&collectionType=$type&paths=$path&refreshLibrary=false" \
      -H "$A" -H "$H" -d "$opts"
  fi
done
curl -sf -X POST "$J/Library/Refresh" -H "$A"
echo "==> Scan started. TMDb lookups for a large library take a while; watch progress with:"
echo "    curl -s localhost:8097/Items/Counts -H 'Authorization: MediaBrowser Token=\"<API_KEY>\"'"
EOF
}

# ---- building channels (docs/architecture.md) ----
#
# Settings for building, in artifacts/.builder.env (gitignored), all optional:
#   TMDB_API_KEY=<TMDb API key or read access token>   original languages; without it, production countries
#   BUILD_WORK=D:\channel-transcoding\work   pieces while a channel builds (default artifacts/builder-work)
#   BUILD_OUT=D:\channel-transcoding\built   finished builds, in test/ and nas/ (default artifacts/built)
#   BUILD_JOBS=3                   encodes at a time
#   BUILD_PRESET=p4                NVENC preset p1-p7 (default p4); --preset on the command line wins
# For --nas, artifacts/.nas.env also needs:
#   NAS_MAP=/mnt/tank/media=\\TRUENAS\media   server path prefix=local path; several separated by ;
#   NAS_STORE=\\TRUENAS\media\linear-tv       the plugin's store folder, as this machine reaches it

# A setting from an env file; empty if the file or setting isn't there.
env_value() { [[ -f "$1" ]] || return 0; sed -n "s/^$2=//p" "$1" | tr -d '\r' | tail -n1; }

# Sets URL KEY MAPS STORE for the target, test or nas, and the build settings.
builder_target() {
  local target="$1"
  case "$target" in
    test)
      jellyfin_running || die "test server is not running - ./x up"
      URL=http://localhost:8097
      SDK_URL=http://jellyfin:8096 # the same server, from inside the SDK container
      KEY="$(env_value artifacts/.testserver.env API_KEY)"
      [[ -n "$KEY" ]] || die "no test server API key - run ./x init"
      MAPS=("/media=$(native_path testdata/media)")
      STORE=testdata/store
      ;;
    nas)
      [[ -f artifacts/.nas.env ]] || die "no artifacts/.nas.env (see docs/channel-format.md)"
      URL="$(env_value artifacts/.nas.env NAS_URL)"; URL="${URL%/}"; SDK_URL="$URL"
      KEY="$(env_value artifacts/.nas.env NAS_KEY)"
      STORE="$(env_value artifacts/.nas.env NAS_STORE)"
      [[ -n "$URL" && -n "$KEY" ]] || die "artifacts/.nas.env needs NAS_URL and NAS_KEY"
      IFS=';' read -r -a MAPS <<<"$(env_value artifacts/.nas.env NAS_MAP)"
      ;;
  esac
  WORK="$(env_value artifacts/.builder.env BUILD_WORK)"; WORK="${WORK:-artifacts/builder-work}"
  # One output folder per target, so `./x publish all` never sends a test build to the NAS.
  OUT="$(env_value artifacts/.builder.env BUILD_OUT)"; OUT="${OUT:-artifacts/built}/$target"
  JOBS="$(env_value artifacts/.builder.env BUILD_JOBS)"; JOBS="${JOBS:-3}"
  PRESET="$(env_value artifacts/.builder.env BUILD_PRESET)"
  TMDB_API_KEY="${TMDB_API_KEY:-$(env_value artifacts/.builder.env TMDB_API_KEY)}"
  export TMDB_API_KEY
}

# The pinned ffmpeg, downloaded once and checked against its published checksum.
cmd_ffmpeg() {
  if [[ -x "$FFMPEG_DIR/bin/ffmpeg$EXE" ]]; then
    info "ffmpeg is in $FFMPEG_DIR"
    return 0
  fi
  local url="https://github.com/BtbN/FFmpeg-Builds/releases/download/$FFMPEG_RELEASE/$FFMPEG_ASSET"
  local archive="artifacts/tools/download/$FFMPEG_ASSET"
  mkdir -p artifacts/tools/download
  info "Downloading $FFMPEG_ASSET"
  curl -fL --retry 3 -o "$archive" "$url"
  echo "$FFMPEG_SHA256  $archive" | sha256sum -c --quiet - || { rm -f "$archive"; die "checksum mismatch for $FFMPEG_ASSET"; }
  case "$archive" in
    *.zip) unzip -q -o "$archive" -d artifacts/tools ;;
    *) tar -xf "$archive" -C artifacts/tools ;;
  esac
  rm -f "$archive"
  "$FFMPEG_DIR/bin/ffmpeg$EXE" -hide_banner -version | head -n1
}

# Publishes the builder as a self-contained executable for this machine, when its source changed.
ensure_builder() {
  local sources=("$BUILDER" "$PROJECT/Store" "$PROJECT/LiveTv/TsSplicer.cs")
  if [[ -x "$BUILDER_EXE" ]] && [[ -z "$(find "${sources[@]}" -newer "$BUILDER_EXE" -name '*.cs' -print -quit)" ]]; then
    return 0
  fi
  info "Building the channel builder ($BUILDER_RID)"
  sdk dotnet publish "$BUILDER" -c Release -r "$BUILDER_RID" --self-contained \
    -p:PublishSingleFile=true -p:DebugType=none -o artifacts/builder >/dev/null
}

# Resolve a channel file and build its channels, on this machine.
cmd_builder() {
  local target=test
  if [[ "${1:-}" == --nas ]]; then target=nas; shift; fi
  local action="${1:-}" file="${2:-}"
  [[ "$action" == check || "$action" == build ]] && [[ -n "$file" ]] \
    || die "usage: ./x builder [--nas] check|build <channel-file> [--only 201,202] [builder options]"
  shift 2
  [[ -f "$file" ]] || die "no such file: $file"
  builder_target "$target"
  cmd_ffmpeg >/dev/null
  ensure_builder

  local args=(--server "$URL" --work "$(native_path "$WORK")" --ffmpeg "$(native_path "$FFMPEG_DIR/bin/ffmpeg$EXE")" --jobs "$JOBS")
  [[ "$action" == build ]] && args+=(--out "$(native_path "$OUT")")
  [[ -n "$PRESET" ]] && args+=(--preset "$PRESET")
  local map
  for map in "${MAPS[@]}"; do [[ -n "$map" ]] && args+=(--map "$map"); done
  LINEARTV_API_KEY="$KEY" "$BUILDER_EXE" "$action" "$(native_path "$file")" "${args[@]}" "$@"
}

# Copy the newest local build of each channel to the plugin's store, then put it on air.
cmd_publish() {
  local target=test
  if [[ "${1:-}" == --nas ]]; then target=nas; shift; fi
  [[ $# -gt 0 ]] || die "usage: ./x publish [--nas] <channel number>... | all"
  builder_target "$target"
  [[ -n "$STORE" ]] || die "artifacts/.nas.env needs NAS_STORE: the store folder as this machine reaches it"
  [[ -d "$OUT" ]] || die "no builds in $OUT - ./x builder build first"

  local numbers=("$@") number build name dest old
  if [[ "${numbers[*]}" == all ]]; then
    mapfile -t numbers < <(find "$OUT" -mindepth 1 -maxdepth 1 -type d ! -name '*.partial' -printf '%f\n' | sed 's/-[0-9]\{8\}T[0-9]\{6\}Z$//' | sort -u)
  fi

  for number in "${numbers[@]}"; do
    build="$(find "$OUT" -mindepth 1 -maxdepth 1 -type d -name "$number-*" ! -name '*.partial' -printf '%f\n' | sort | tail -n1)"
    [[ -n "$build" && -f "$OUT/$build/channel.json" ]] || die "no finished build of channel $number in $OUT"
    name="$build"; dest="$STORE/$name"
    info "Publishing $name to $STORE"
    copy_build "$OUT/$name" "$dest"
    # The newest complete build is the one on air; older ones are only taking space.
    for old in "$STORE/$number"-*; do
      [[ -d "$old" && "$(basename "$old")" != "$name" ]] || continue
      info "  removing older build $(basename "$old")"
      rm -rf -- "$old"
    done
  done

  info "Rescanning the store on $URL"
  store_api -X POST /LinearTv/Store/Refresh \
    '.channels[] | "    \(.number) \(.name): \(.hours) h, \(.programmes) programmes (\(.folder))"' \
    || echo "    (couldn't reach the plugin's store endpoint on $URL; it picks the channels up at the next guide refresh)"
}

# Take channels off air: delete every build of them from the store, then refresh the guide. The
# local builds are kept, so ./x publish puts a channel back.
cmd_unpublish() {
  local target=test
  if [[ "${1:-}" == --nas ]]; then target=nas; shift; fi
  [[ $# -gt 0 ]] || die "usage: ./x unpublish [--nas] <channel number>..."
  builder_target "$target"
  [[ -n "$STORE" && -d "$STORE" ]] || die "can't reach the store folder '$STORE'"
  local number build found
  for number in "$@"; do
    found=0
    for build in "$STORE/$number"-*; do
      [[ -d "$build" && -f "$build/channel.json" ]] || continue
      info "Removing $(basename "$build") from $STORE"
      rm -rf -- "$build"
      found=1
    done
    [[ $found == 1 ]] || info "No channel $number in $STORE"
  done
  store_api -X POST /LinearTv/Store/Refresh '.channels[] | "    \(.number) \(.name)"' \
    || echo "    (couldn't reach the plugin's store endpoint on $URL; the channel goes at the next guide refresh)"
}

# Calls the plugin's store endpoint and formats the answer with jq, both in the SDK container
# (curl and jq live there, not on the host).
#   store_api [-X POST] <path> <jq filter>
store_api() {
  local method=GET
  if [[ "$1" == -X ]]; then method="$2"; shift 2; fi
  sdk bash -c 'curl -fsS -X "$1" -H "Authorization: MediaBrowser Token=\"$2\"" "$3" | jq -r "$4"' _ \
    "$method" "$KEY" "$SDK_URL$1" "$2"
}

# Copies a build folder with its manifest last: until the manifest is there, the plugin ignores it.
# robocopy on Windows: restartable per file over SMB. rsync elsewhere.
copy_build() {
  local src="$1" dest="$2" rc=0
  if [[ -x /c/Windows/System32/robocopy.exe ]]; then
    local robocopy=(/c/Windows/System32/robocopy.exe "$(native_path "$src")" "$(native_path "$dest")")
    "${robocopy[@]}" /E /Z /R:5 /W:10 /NP /NDL /NJH /NJS /XF channel.json || rc=$?
    [[ $rc -lt 8 ]] || die "copy to $dest failed (robocopy exit $rc)"
    rc=0
    "${robocopy[@]}" channel.json /Z /R:5 /W:10 /NP /NDL /NJH /NJS || rc=$?
    [[ $rc -lt 8 ]] || die "copy of $dest/channel.json failed (robocopy exit $rc)"
  else
    rsync -a --partial --exclude channel.json "$src/" "$dest/"
    rsync -a "$src/channel.json" "$dest/channel.json"
  fi
}

# The channels the plugin sees in its store.
cmd_store() {
  local target=test
  if [[ "${1:-}" == --nas ]]; then target=nas; shift; fi
  builder_target "$target"
  store_api "/LinearTv/Store?rescan=true" \
    '"store: \(.storePath)\(if .storeExists then "" else " (missing!)" end)", (.channels[] | "  \(.number) \(.name): \(.hours) h, \(.programmes) programmes, \(.bytes / 1e9 * 10 | floor / 10) GB, built \(.built) (\(.folder))")'
}

cmd_package() {
  local version="${1:-}" repo_url="${2:-${REPO_URL:-}}"
  [[ -n "$version" && -n "$repo_url" ]] \
    || die "usage: ./x package <version> <repoUrl>   (or set REPO_URL)"
  ensure_dirs
  rm -f artifacts/dist/*.zip

  info "Building release $version"
  # jprm resolves build.yaml and the .csproj non-recursively in the path given, so it is
  # pointed at the project directory rather than the repo root.
  sdk jprm --verbosity=info plugin build "$PROJECT" --output=artifacts/dist --version="$version"

  local zip
  zip="$(ls artifacts/dist/*.zip 2>/dev/null | head -n1)"
  [[ -n "$zip" ]] || die "jprm produced no zip"

  [[ -f artifacts/repo/manifest.json ]] || sdk jprm repo init artifacts/repo
  info "Adding $(basename "$zip") to repository (base URL $repo_url)"
  sdk jprm --verbosity=info repo add --url="$repo_url" artifacts/repo "$zip"

  info "Serve artifacts/repo/ and add $repo_url/manifest.json as a repository in Jellyfin"
}

# Channel files against a live server (test, or the NAS with --nas). See tools/channels.sh.
cmd_channels() { sdk bash tools/channels.sh "$@"; }

# Publishes the plugin as a GitHub release that Jellyfin installs and updates from. Add this as
# a repository in Jellyfin once; it never changes:
#   https://github.com/AShav3dWookie/jellyfin-channel-plugin/releases/latest/download/manifest.json
# Each release carries the zip and a manifest listing every version so far: the previous
# release's manifest, extended. The version comes from build.yaml, and the zip is built only if
# the plugin source matches its tag (0.3.0.0 -> v0.3), so a release is exactly what was tagged.
# Needs the GitHub CLI, logged in.
GH_REPO=AShav3dWookie/jellyfin-channel-plugin
cmd_release() {
  local gh version tag zip notes
  gh="$(command -v gh || true)"
  [[ -n "$gh" || ! -x "/c/Program Files/GitHub CLI/gh.exe" ]] || gh="/c/Program Files/GitHub CLI/gh.exe"
  [[ -n "$gh" ]] || die "the GitHub CLI (gh) is needed for releases"

  version="$(sed -n 's/^version: "\(.*\)"/\1/p' "$PROJECT/build.yaml")"
  tag="v${version%.0.0}"
  git rev-parse -q --verify "refs/tags/$tag" >/dev/null || die "no tag $tag: commit and tag the release first"
  git ls-remote --exit-code --tags origin "$tag" >/dev/null || die "tag $tag isn't pushed: git push origin $tag"
  git diff --quiet "$tag" -- "$PROJECT" || die "$PROJECT differs from $tag: release only what was tagged"
  ! "$gh" release view "$tag" -R "$GH_REPO" >/dev/null 2>&1 || die "release $tag already exists"

  rm -rf artifacts/release && mkdir -p artifacts/release/repo
  info "Building $version"
  sdk jprm --verbosity=info plugin build "$PROJECT" --output=artifacts/release --version="$version"
  zip="$(ls artifacts/release/*.zip 2>/dev/null | head -n1)"
  [[ -n "$zip" ]] || die "jprm produced no zip"

  if "$gh" release download -R "$GH_REPO" --pattern manifest.json --dir artifacts/release/repo 2>/dev/null; then
    info "Extending the previous release's manifest"
  else
    info "First release: new manifest"
    sdk jprm repo init artifacts/release/repo
  fi
  # --plugin-url: the zip is served from this release, not from a copy beside the manifest.
  sdk jprm --verbosity=info repo add \
    --plugin-url="https://github.com/$GH_REPO/releases/download/$tag/$(basename "$zip")" \
    artifacts/release/repo "$zip"

  notes="$(sed -n '/^changelog: >/,$p' "$PROJECT/build.yaml" | tail -n +2 | sed 's/^  //' | tr '\n' ' ')

**Install:** in Jellyfin, *Dashboard → Plugins → Repositories → +*, add
\`https://github.com/$GH_REPO/releases/latest/download/manifest.json\`, then install *Linear TV* from the catalogue and restart Jellyfin. Requires Jellyfin 12.1 or later."
  info "Publishing release $tag"
  "$gh" release create "$tag" -R "$GH_REPO" --verify-tag --title "Linear TV $tag" --notes "$notes" \
    "$zip" artifacts/release/repo/manifest.json
}

cmd_shell() { sdk bash; }

usage() {
  cat <<'EOF'
usage: ./x <command>

  build                        compile the plugin
  test                         run unit tests (plugin and builder)
  deploy                       build, stage the DLL, restart the test server
  up | down | logs             test server lifecycle (http://localhost:8097)
  media                        generate test clips with a burned-in clock
  init                         wizard + Shows and Films libraries + API key + store (idempotent)
  mirror <moviesDir> <tvDir>   real library as empty placeholders, for import tests
  channels [--nas] library     list the library -> artifacts/channels/<target>/, for writing channel files

  ffmpeg                       fetch the pinned ffmpeg the builder encodes with
  builder [--nas] check <file> resolve a channel file: what each channel would be; nothing encoded
  builder [--nas] build <file> encode and build the file's channels (--only 201,202, --preset p4)
  publish [--nas] <n>... | all copy the newest build of channels to the store, put them on air
  unpublish [--nas] <n>...     take channels off air (delete them from the store)
  store [--nas]                the channels the plugin sees in its store

  package <version> <repoUrl>  release zip + manifest.json for the NAS
  release                      publish build.yaml's version as a GitHub release (needs gh)
  shell                        shell in the SDK container

First run:  ./x media && ./x deploy && ./x init
Channels:   ./x builder build my-channels.json && ./x publish all
Test server: http://localhost:8097   username: dev   password: dev
Design: docs/architecture.md   Full guide: docs/testing.md
EOF
}

command="${1:-}"; shift || true
case "$command" in
  build|test|deploy|up|down|logs|media|init|mirror|channels|ffmpeg|builder|publish|unpublish|store|package|release|shell) "cmd_$command" "$@" ;;
  ""|-h|--help|help) usage ;;
  *) usage; exit 1 ;;
esac
