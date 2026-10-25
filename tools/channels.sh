#!/usr/bin/env bash
# Lists a server's library, for writing channel files (by hand, or with an AI). Runs inside the
# sdk container via `./x channels` (curl and jq live there, not on the host). Checking and
# building a channel file is the builder's job: ./x builder check|build.
#
#   ./x channels [--nas] library
#
# Default target is the test server. --nas targets the server in artifacts/.nas.env:
#   NAS_URL=http://192.168.1.3:8096
#   NAS_KEY=<an administrator's API key>
set -euo pipefail

die()  { echo "error: $*" >&2; exit 1; }
info() { echo "==> $*"; }

usage() {
  cat <<'EOF'
usage: ./x channels [--nas] library

  library               list the server's library -> artifacts/channels/<target>/
                          library.jsonl (films, series, collections), episodes.jsonl, libraries.jsonl

  --nas     target the server in artifacts/.nas.env instead of the test server

Then write a channel file (docs/channel-format.md) and check it: ./x builder [--nas] check <file>
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
mkdir -p "$DIR"

api() { curl -sS -H "Authorization: MediaBrowser Token=\"$KEY\"" "$@"; }

# Fails early, with a reason, rather than letting the listing produce a confusing result.
preflight() {
  local code
  code=$(api -m 15 -o /dev/null -w '%{http_code}' "$URL/Library/VirtualFolders") \
    || die "cannot reach $URL"
  case "$code" in
    200) ;;
    401) die "$URL rejected the API key" ;;
    403) die "the API key on $URL doesn't belong to an administrator" ;;
    *)   die "$URL answered HTTP $code" ;;
  esac
}

case "$cmd" in
  library)
    preflight
    info "Reading the library on $URL"
    # CollapseBoxSetItems=false: with "group movies into collections" on, Jellyfin otherwise
    # hides every film that belongs to a collection inside it.
    api -f "$URL/Items?Recursive=true&IncludeItemTypes=Movie,Series,BoxSet&IsMissing=false&CollapseBoxSetItems=false&Fields=ProductionYear,Genres,OfficialRating,ProviderIds,RecursiveItemCount,ChildCount&EnableImages=false&EnableUserData=false" \
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

  ""|-h|--help|help) usage ;;
  *) usage; exit 1 ;;
esac
