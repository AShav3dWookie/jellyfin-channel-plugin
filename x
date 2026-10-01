#!/usr/bin/env bash
# Single entry point for development. Requires only Docker - no host SDK, no host tools.
# Works in Git Bash on Windows and natively on Linux (including the NAS).
#
#   ./x build | test | deploy | up | down | logs | media | init | mirror | channels | spike1 | package | shell
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
TESTS=tests/Jellyfin.Plugin.LinearTv.Tests
CONFIGURATION=Debug

die()  { echo "error: $*" >&2; exit 1; }
info() { echo "==> $*"; }

sdk()    { docker compose run --rm --build sdk "$@"; }
# SVT_LOG=1: the AV1 encoder ignores ffmpeg's log level and prints its config unless told.
ffmpeg() { docker compose run --rm -e SVT_LOG=1 ffmpeg "$@"; }

ensure_dirs() {
  mkdir -p artifacts/build artifacts/plugin artifacts/dist artifacts/repo \
           testdata/media testdata/jellyfin/config testdata/jellyfin/cache
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
  if [[ ! -d "$TESTS" ]]; then
    info "No test project yet ($TESTS) - nothing to run"
    return 0
  fi
  sdk dotnet test "$TESTS" -c "$CONFIGURATION"
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

  info "Done. Run ./x init to set up the test server library."
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

  # Wait for as many episodes as there are video files, however many ./x media has produced.
  local expected
  expected=$(find testdata/media/shows -type f \( -name '*.mp4' -o -name '*.mkv' -o -name '*.avi' \) | wc -l)

  EXPECTED="$expected" docker compose run --rm -T -e EXPECTED sdk bash -s <<'EOF'
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
elif [ "$(curl -sf "$J/Items?Recursive=true&IncludeItemTypes=Episode" -H "$A" | jq .TotalRecordCount)" -lt "$EXPECTED" ]; then
  # Real-time monitoring is off, so clips added by ./x media need an explicit scan.
  echo "==> Scanning for new test media"
  curl -sf -X POST "$J/Library/Refresh" -H "$A"
fi

# Items are indexed before they are probed, so RunTimeTicks is briefly null. Wait for both.
echo "==> Waiting for scan and probe ($EXPECTED episodes)"
for i in $(seq 1 90); do
  st=$(curl -sf "$J/Items?Recursive=true&IncludeItemTypes=Episode" -H "$A" \
       | jq -r '"\(.TotalRecordCount) \([.Items[] | select(.RunTimeTicks == null)] | length)"')
  set -- $st
  [ "$1" -ge "$EXPECTED" ] && [ "$2" -eq 0 ] && break
  sleep 2
done
[ "$1" -ge "$EXPECTED" ] && [ "$2" -eq 0 ] || { echo "error: scan incomplete ($1 of $EXPECTED items, $2 unprobed)" >&2; exit 1; }

if ! curl -sf "$J/Auth/Keys" -H "$A" | jq -e '.Items[] | select(.AppName=="LinearTvDev")' >/dev/null; then
  curl -sf -X POST "$J/Auth/Keys?app=LinearTvDev" -H "$A"
fi
KEY=$(curl -sf "$J/Auth/Keys" -H "$A" | jq -r '[.Items[] | select(.AppName=="LinearTvDev")][0].AccessToken')
printf 'API_KEY=%s\n' "$KEY" > artifacts/.testserver.env

echo "==> Test library"
curl -sf "$J/Items?Recursive=true&IncludeItemTypes=Episode&SortBy=SeriesSortName,ParentIndexNumber,IndexNumber" -H "$A" \
  | jq -r '.Items[] | "    \(.Id)  \(.SeriesName) / \(.Name)  \(.RunTimeTicks/10000000|floor)s"'
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

# Spike 1: does Jellyfin's own endpoint honour startTimeTicks under a copy remux?
# Runs inside the compose network against http://jellyfin:8096 - the same internal URL
# shape the plugin will use via GetApiUrlForLocalAccess().
cmd_spike1() {
  local item="${1:-}" offset="${2:-600}"
  [[ -n "$item" ]] || die "usage: ./x spike1 <itemId> [offsetSeconds=600]"
  jellyfin_running || die "test server is not running - ./x up"
  [[ -f artifacts/.testserver.env ]] || die "no API key - run ./x init"
  local key; key="$(sed -n 's/^API_KEY=//p' artifacts/.testserver.env)"
  ensure_dirs
  rm -f artifacts/spike1.ts artifacts/spike1.png

  local ticks=$(( offset * 10000000 ))
  # Jellyfin names its transcode output MD5(MediaPath, UserAgent, DeviceId, PlaySessionId).
  # StartTimeTicks is not in that key, so without a fresh session id a second request for
  # the same item is served the FIRST request's output - at the first request's offset.
  # The plugin must do the same on every tune.
  local session="spike$(date +%s)${RANDOM}${RANDOM}"
  local url="http://jellyfin:8096/Videos/${item}/stream?startTimeTicks=${ticks}&container=ts&videoCodec=copy&audioCodec=copy&PlaySessionId=${session}&ApiKey=${key}"

  info "Requesting ${offset}s (${ticks} ticks), capturing ~15s"
  # The stream may run on; curl exit 28 (timeout) is the expected way this ends.
  sdk sh -c "curl -sS --max-time 15 -o artifacts/spike1.ts '$url'; rc=\$?; [ \$rc -eq 0 ] || [ \$rc -eq 28 ] || exit \$rc"

  [[ -s artifacts/spike1.ts ]] || die "no data returned - check item id and API key"

  info "Extracting first frame"
  ffmpeg -y -i /artifacts/spike1.ts -frames:v 1 /artifacts/spike1.png

  echo
  echo "Open artifacts/spike1.png and read the yellow clock:"
  echo "  ~$(printf '%02d:%02d:%02d' $((offset/3600)) $((offset%3600/60)) $((offset%60)))  -> PASS: offset honoured"
  echo "  ~00:00:00  -> FAIL: use docs/plan.md section 5.1"
  echo "Stream copy snaps to the keyframe BEFORE the target, so expect it up to one"
  echo "keyframe interval early (the test clips use 10s), never late."
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

cmd_shell() { sdk bash; }

usage() {
  cat <<'EOF'
usage: ./x <command>

  build                        compile the plugin
  test                         run unit tests
  deploy                       build, stage the DLL, restart the test server
  up | down | logs             test server lifecycle (http://localhost:8097)
  media                        generate test clips with a burned-in clock
  init                         wizard + Shows library + API key (idempotent)
  mirror <moviesDir> <tvDir>   real library as empty placeholders, for import tests
  channels [--nas] <cmd>       library, list, backup, check, import, restore (help: ./x channels)
  spike1 <itemId> [secs]       does a copy remux honour startTimeTicks?
  package <version> <repoUrl>  release zip + manifest.json for the NAS
  shell                        shell in the SDK container

First run:  ./x media && ./x deploy && ./x init
Test server: http://localhost:8097   username: dev   password: dev
Full guide: docs/testing.md
EOF
}

command="${1:-}"; shift || true
case "$command" in
  build|test|deploy|up|down|logs|media|init|mirror|channels|spike1|package|shell) "cmd_$command" "$@" ;;
  ""|-h|--help|help) usage ;;
  *) usage; exit 1 ;;
esac
