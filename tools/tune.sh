#!/usr/bin/env bash
# Tunes a channel as a client would and captures what it gets. Runs in the sdk container.
#   tune.sh <channel number> <hls|direct> <seconds> <out prefix>
set -euo pipefail
number="$1" route="$2" seconds="$3" out="$4"
J=http://jellyfin:8096
KEY=$(sed -n 's/^API_KEY=//p' artifacts/.testserver.env)
A="Authorization: MediaBrowser Client=\"tune\", Device=\"tune\", DeviceId=\"tune-$RANDOM\", Version=\"1\", Token=\"$KEY\""
USER=$(curl -sf "$J/Users" -H "$A" | jq -r '.[0].Id')
CH=$(curl -sf "$J/LiveTv/Channels?UserId=$USER" -H "$A" | jq -r --arg n "$number" '.Items[] | select(.ChannelNumber==$n) | .Id')
[ -n "$CH" ] || { echo "no channel $number"; exit 1; }

if [ "$route" = hls ]; then
  PROFILE=artifacts/browser-profile.json
else
  PROFILE=/tmp/direct.json
  echo '{"DeviceProfile":{"MaxStreamingBitrate":120000000,"DirectPlayProfiles":[{"Container":"ts,mpegts","Type":"Video","VideoCodec":"h264,hevc","AudioCodec":"aac"}],"TranscodingProfiles":[]}}' > $PROFILE
fi

start=$(date +%s.%N)
INFO=$(curl -sf -X POST "$J/Items/$CH/PlaybackInfo?UserId=$USER&AutoOpenLiveStream=true&IsPlayback=true&StartTimeTicks=0" \
  -H "$A" -H 'Content-Type: application/json' -d @$PROFILE)
echo "$INFO" | jq -c '.MediaSources[0] | {SupportsDirectPlay, SupportsDirectStream, SupportsTranscoding, TranscodeReasons, LiveStreamId, Path, TranscodingUrl}' > "$out-info.json"
cat "$out-info.json"

if [ "$route" = hls ]; then
  URL=$(echo "$INFO" | jq -r '.MediaSources[0].TranscodingUrl')
  [ "$URL" != null ] || { echo "no TranscodingUrl"; exit 1; }
  MASTER=$(curl -sf "$J$URL" -H "$A")
  VARIANT=$(echo "$MASTER" | grep -v '^#' | head -n1)
  BASE="$J/videos/$(echo "$URL" | sed -n 's|.*/videos/\([^/]*\)/.*|\1|p')"
  : > "$out.ts"
  got=0; first=""
  for i in $(seq 1 120); do
    LIST=$(curl -sf "$BASE/$VARIANT" -H "$A" || true)
    for seg in $(echo "$LIST" | grep -v '^#'); do
      if ! grep -qxF "$seg" "$out.segs" 2>/dev/null; then
        curl -sf "$BASE/$seg" -H "$A" >> "$out.ts" && echo "$seg" >> "$out.segs"
        got=$((got + 1))
        [ -z "$first" ] && first=$(echo "$(date +%s.%N) - $start" | bc 2>/dev/null || echo "?")
      fi
    done
    [ "$(stat -c %s "$out.ts")" -gt 0 ] && [ "$got" -ge $((seconds / 3)) ] && break
    sleep 1
  done
  echo "segments: $got, first after ${first}s"
else
  PATHURL=$(echo "$INFO" | jq -r '.MediaSources[0].Path')
  curl -s --max-time "$seconds" -o "$out.ts" "$PATHURL" || true
  echo "direct bytes: $(stat -c %s "$out.ts")"
fi

LS=$(echo "$INFO" | jq -r '.MediaSources[0].LiveStreamId')
curl -sf -X POST "$J/LiveStreams/Close?LiveStreamId=$LS" -H "$A" || true
