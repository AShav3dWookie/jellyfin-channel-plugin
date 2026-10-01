#!/usr/bin/env bash
# Channel files against a live server: list the library, check, import, back up, restore.
# Runs inside the sdk container via `./x channels` (curl and jq live there, not on the host).
#
#   ./x channels [--nas] <command>
#
# Default target is the test server. --nas targets the server in artifacts/.nas.env:
#   NAS_URL=http://192.168.1.3:8096
#   NAS_KEY=<an administrator's API key>
#
# Guarantees, so they don't depend on whoever (or whatever) is driving:
#   - import refuses a file unless that exact content passed `check` against that server in that
#     mode;
#   - import and restore back up the current channels first.
set -euo pipefail

die()  { echo "error: $*" >&2; exit 1; }
info() { echo "==> $*"; }

usage() {
  cat <<'EOF'
usage: ./x channels [--nas] <command>

  library               list the server's library -> artifacts/channels/<target>/
                          library.jsonl (films, series, collections), episodes.jsonl, libraries.jsonl
  list                  the channels on the server now
  backup                save the current channels -> artifacts/channels/<target>/backup-<time>.json
  check <file> [--replace]   dry run: how every entry resolves; nothing is saved
  import <file> [--replace]  back up, then import a file that passed check
  restore <backup>      back up, then make the channel list exactly <backup>

  --nas     target the server in artifacts/.nas.env instead of the test server
  --replace imported channels become the whole list (default: merge by channel number)

Files are paths inside the repo (the command runs in a container that sees only the repo).
Format: docs/channel-format.md
EOF
}

target=test
if [[ "${1:-}" == --nas ]]; then target=nas; shift; fi
cmd="${1:-}"; shift || true

case "$target" in
  test)
    URL=http://jellyfin:8096
    KEY=$(sed -n 's/^API_KEY=//p' artifacts/.testserver.env 2>/dev/null || true)
    [[ -n "$KEY" ]] || die "no test server API key - run ./x init"
    ;;
  nas)
    [[ -f artifacts/.nas.env ]] || die "no artifacts/.nas.env - create it with NAS_URL=http://<nas-ip>:8096 and NAS_KEY=<admin API key>"
    URL=$(sed -n 's/^NAS_URL=//p' artifacts/.nas.env | tr -d '\r')
    KEY=$(sed -n 's/^NAS_KEY=//p' artifacts/.nas.env | tr -d '\r')
    [[ -n "$URL" && -n "$KEY" ]] || die "artifacts/.nas.env needs NAS_URL and NAS_KEY"
    URL=${URL%/}
    ;;
esac

DIR=artifacts/channels/$target
mkdir -p "$DIR/checked"

api() { curl -sS -H "Authorization: MediaBrowser Token=\"$KEY\"" "$@"; }

# Fails early, with a reason, rather than letting a later command produce a confusing report.
preflight() {
  local code
  code=$(api -m 15 -o /dev/null -w '%{http_code}' "$URL/LinearTv/Channels/Export") \
    || die "cannot reach $URL"
  case "$code" in
    200) ;;
    401) die "$URL rejected the API key" ;;
    403) die "the API key on $URL doesn't belong to an administrator" ;;
    404) die "$URL has no channel import endpoint: Linear TV 0.3 or later isn't installed there" ;;
    *)   die "$URL answered HTTP $code" ;;
  esac
}

# Readable form of an import report, from the last import call.
report() {
  local code="$1" file="$DIR/last-report.json"
  if [[ "$code" == 400 ]]; then
    echo "HTTP 400 - refused: $(jq -r .error "$file")"
    return
  fi
  jq -r --arg code "$code" '
    "HTTP \($code) - \(if .ok then "OK" else "REFUSED: fix the X lines" end)"
      + (if .dryRun then " (dry run, nothing saved)" elif .saved then ", saved" else ", nothing saved" end)
      + ", mode \(.mode)",
    (.errors[] | "  ERROR \(.)"),
    (.channels[] |
      "",
      "\(.number) \(.name)\(if .shuffle then " (shuffle)" else "" end)\(if .action then "  [\(.action)]" else "" end)",
      (.content[] |
        if .matched == null then "  X  \(.requested): \(.note)"
        elif .note then "  ~  \(.requested) -> \(.matched)   (\(.note))"
        else "  ok \(.requested) -> \(.matched)" end),
      (.problems[] | select(startswith("Missing") or startswith("No content")) | "  X  \(.)"),
      (.warnings[] | "  !  \(.)")),
    (if (.removed | length) > 0 then "", "Removed: \(.removed | join(", "))" else empty end),
    "",
    "Legend: ok exact match, ~ matched with a note (check it), X refused, ! warning"
  ' "$file"
}

# Posts a channel file to the import endpoint; prints the HTTP status.
post() {
  local file="$1" query="$2"
  api -o "$DIR/last-report.json" -w '%{http_code}' -H 'Content-Type: application/json' \
    --data-binary @"$file" "$URL/LinearTv/Channels/Import?$query"
}

mode_of() { [[ "${1:-}" == --replace ]] && echo replace || echo merge; }

# Identifies "this content, checked against this server, in this mode".
marker() { echo "$DIR/checked/$(cat "$1" | sha256sum | cut -c1-16)-$2"; }

backup() {
  local out
  out="$DIR/backup-$(date -u +%Y%m%dT%H%M%SZ).json"
  api -f "$URL/LinearTv/Channels/Export" > "$out" || die "export failed"
  info "Backed up $(jq '.channels | length' "$out") channels to $out"
}

need_file() { [[ -n "${1:-}" && -f "$1" ]] || die "no such file: ${1:-<missing>} (paths are relative to the repo)"; }

case "$cmd" in
  library)
    preflight
    info "Reading the library on $URL"
    api -f "$URL/Items?Recursive=true&IncludeItemTypes=Movie,Series,BoxSet&IsMissing=false&Fields=ProductionYear,Genres,OfficialRating,ProviderIds,RecursiveItemCount,ChildCount&EnableImages=false&EnableUserData=false" \
      | jq -c '.Items[] | {
          type: ({"Movie":"movie","Series":"series","BoxSet":"collection"}[.Type]),
          title: .Name,
          year: .ProductionYear,
          genres: (if (.Genres // []) == [] then null else .Genres end),
          rating: .OfficialRating,
          minutes: (if .Type == "Movie" and .RunTimeTicks then (.RunTimeTicks / 600000000 | floor) else null end),
          seasons: (if .Type == "Series" then .ChildCount else null end),
          items: (if .Type != "Movie" then .RecursiveItemCount else null end),
          unidentified: (if (.ProviderIds // {}) == {} and .Type != "BoxSet" then true else null end)
        } | with_entries(select(.value != null))' > "$DIR/library.jsonl"
    api -f "$URL/Items?Recursive=true&IncludeItemTypes=Episode&IsMissing=false&Fields=ProductionYear&EnableImages=false&EnableUserData=false&SortBy=SeriesSortName,ParentIndexNumber,IndexNumber" \
      | jq -c '.Items[] | {series: .SeriesName, season: .ParentIndexNumber, episode: .IndexNumber, title: .Name}
          | with_entries(select(.value != null))' > "$DIR/episodes.jsonl"
    api -f "$URL/Library/VirtualFolders" \
      | jq -c '.[] | {title: .Name, kind: .CollectionType}' > "$DIR/libraries.jsonl"
    info "$(jq -s 'map(select(.type=="movie")) | length' "$DIR/library.jsonl") films, $(jq -s 'map(select(.type=="series")) | length' "$DIR/library.jsonl") series, $(jq -s 'map(select(.type=="collection")) | length' "$DIR/library.jsonl") collections, $(wc -l < "$DIR/episodes.jsonl") episodes, $(wc -l < "$DIR/libraries.jsonl") libraries"
    unidentified=$(jq -s 'map(select(.unidentified)) | length' "$DIR/library.jsonl")
    [[ "$unidentified" == 0 ]] || info "$unidentified not identified by Jellyfin: their titles are file names"
    info "Written to $DIR/{library,episodes,libraries}.jsonl"
    ;;

  list)
    preflight
    api -f "$URL/LinearTv/Channels/Export" \
      | jq -r '.channels[] | "\(.number)\t\(.name)\t\(if .shuffle then "shuffle" else "in order" end)\t\(.content | length) entries"'
    ;;

  backup)
    preflight
    backup
    ;;

  check)
    need_file "${1:-}"
    file="$1"; mode=$(mode_of "${2:-}")
    preflight
    code=$(post "$file" "dryRun=true&mode=$mode")
    report "$code"
    if [[ "$code" == 200 ]]; then
      touch "$(marker "$file" "$mode")"
      echo
      info "Ready to import: ./x channels$([[ $target == nas ]] && echo " --nas") import $file$([[ $mode == replace ]] && echo " --replace")"
    else
      exit 1
    fi
    ;;

  import)
    need_file "${1:-}"
    file="$1"; mode=$(mode_of "${2:-}")
    [[ -f "$(marker "$file" "$mode")" ]] \
      || die "$file hasn't passed a check against the $target server in $mode mode (or has changed since): run ./x channels$([[ $target == nas ]] && echo " --nas") check $file$([[ $mode == replace ]] && echo " --replace")"
    preflight
    backup
    code=$(post "$file" "mode=$mode")
    report "$code"
    rm -f "$(marker "$file" "$mode")"
    # The library can change between check and import; a refusal here saved nothing.
    [[ "$code" == 200 ]] || exit 1
    ;;

  restore)
    need_file "${1:-}"
    file="$1"
    preflight
    code=$(post "$file" "dryRun=true&mode=replace")
    [[ "$code" == 200 ]] || { report "$code"; die "$file doesn't resolve cleanly on $URL; nothing changed"; }
    backup
    code=$(post "$file" "mode=replace")
    report "$code"
    [[ "$code" == 200 ]] || exit 1
    ;;

  ""|-h|--help|help) usage ;;
  *) usage; exit 1 ;;
esac
