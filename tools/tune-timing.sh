#!/usr/bin/env bash
# Times a channel change as a client makes it, stage by stage. Runs in the sdk container.
#   tune-timing.sh <channel number> <direct|hls>
# Prints: PlaybackInfo (Jellyfin opens the live stream through the plugin), first byte of the
# stream, first 1 MB, and for hls the playlist and first segment.
set -euo pipefail
number="$1" route="$2"
J=http://jellyfin:8096
KEY=$(sed -n 's/^API_KEY=//p' artifacts/.testserver.env)
A="Authorization: MediaBrowser Client=\"tune\", Device=\"tune\", DeviceId=\"tune-$RANDOM\", Version=\"1\", Token=\"$KEY\""
USER=$(curl -sf "$J/Users" -H "$A" | jq -r '.[0].Id')
CH=$(curl -sf "$J/LiveTv/Channels?UserId=$USER" -H "$A" | jq -r --arg n "$number" '.Items[] | select(.ChannelNumber==$n) | .Id')
ms() { date +%s%3N; }

if [ "$route" = hls ]; then
  PROFILE='{"DeviceProfile":{"MaxStreamingBitrate":120000000,"DirectPlayProfiles":[{"Container":"mp4","Type":"Video","VideoCodec":"h264,hevc","AudioCodec":"aac"}],"TranscodingProfiles":[{"Container":"ts","Type":"Video","VideoCodec":"hevc,h264","AudioCodec":"aac","Protocol":"hls","Context":"Streaming","MaxAudioChannels":"2"}]}}'
else
  PROFILE='{"DeviceProfile":{"MaxStreamingBitrate":120000000,"DirectPlayProfiles":[{"Container":"ts,mpegts","Type":"Video","VideoCodec":"h264,hevc","AudioCodec":"aac"}],"TranscodingProfiles":[]}}'
fi

t0=$(ms)
INFO=$(curl -sf -X POST "$J/Items/$CH/PlaybackInfo?UserId=$USER&AutoOpenLiveStream=true&IsPlayback=true" -H "$A" -H 'Content-Type: application/json' -d "$PROFILE")
t1=$(ms)
LS=$(echo "$INFO" | jq -r '.MediaSources[0].LiveStreamId')

if [ "$route" = hls ]; then
  URL=$(echo "$INFO" | jq -r '.MediaSources[0].TranscodingUrl')
  BASE="$J/videos/$(echo "$URL" | sed -n 's|.*/videos/\([^/]*\)/.*|\1|p')"
  MASTER=$(curl -sf "$J$URL" -H "$A"); VARIANT=$(echo "$MASTER" | grep -v '^#' | head -n1)
  t2=$(ms)
  SEG=""
  for i in $(seq 1 600); do
    SEG=$(curl -sf "$BASE/$VARIANT" -H "$A" | grep -v '^#' | head -n1 || true)
    [ -n "$SEG" ] && break
    sleep 0.05
  done
  t3=$(ms)
  curl -sf "$BASE/$SEG" -H "$A" -o /dev/null
  t4=$(ms)
  echo "hls    ch $number: playbackinfo $((t1 - t0)) ms, master playlist +$((t2 - t1)), first segment listed +$((t3 - t2)), downloaded +$((t4 - t3)) = $((t4 - t0)) ms to the first segment"
else
  PATHURL=$(echo "$INFO" | jq -r '.MediaSources[0].Path')
  T=$(curl -s --max-time 3 -o /dev/null -r 0-1048575 -w '%{time_starttransfer} %{time_total} %{size_download}' "$PATHURL" || true)
  set -- $T
  echo "direct ch $number: playbackinfo $((t1 - t0)) ms, first byte +$(awk -v s="$1" 'BEGIN{printf "%d", s*1000}') ms, 1 MB in $(awk -v s="$2" 'BEGIN{printf "%d", s*1000}') ms ($3 bytes)"
fi
curl -sf -X POST "$J/LiveStreams/Close?LiveStreamId=$LS" -H "$A" >/dev/null || true
