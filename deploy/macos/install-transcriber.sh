#!/usr/bin/env bash
# Installs (or updates) the Kick Gateway live transcriber as a launchd *user agent* on an Apple
# Silicon Mac, so it starts at login, restarts on failure and runs Whisper on the GPU via Metal.
#
#   1. brew install ffmpeg ; install the .NET 10 SDK (brew install --cask dotnet-sdk)
#   2. cp deploy/macos/transcriber.env.example ~/.config/kickgateway/transcriber.env  (fill in RabbitMq__*)
#   3. deploy/macos/install-transcriber.sh
#
# Re-run after `git pull` to publish the new build and restart the agent.
# Uninstall: launchctl bootout gui/$(id -u)/com.tailoredapps.kickgateway.transcriber && rm ~/Library/LaunchAgents/com.tailoredapps.kickgateway.transcriber.plist
#
# A second agent next to the default one, e.g. the Twitch transcriber:
#   TRANSCRIBER_INSTANCE=twitch deploy/macos/install-transcriber.sh
# suffixes the label (com.tailoredapps.kickgateway.transcriber-twitch), the app dir, the env file
# (~/.config/kickgateway/transcriber-twitch.env) and the log, and defaults Transcriber__Source to the
# instance name (the env file may override it). Both agents share the Whisper model cache and the GPU.
#
# Several agents per platform (more Whisper throughput - modest, one GPU is shared): name them with a
# '-' suffix (kick-a / kick-b, twitch-a / twitch-b; the platform is the prefix, the queue
# transcriber-<instance>) and give each env file a DISJOINT Transcriber__Channels__N list. An agent
# without a channel list binds every channel of its platform, so next to another agent it stores every
# sentence twice - that includes the unnamed default agent.
#
# A LaunchAgent needs a logged-in user session: on a headless Mac mini enable automatic login
# (System Settings > Users & Groups) and keep it awake (`sudo pmset -a sleep 0 disksleep 0`).
set -euo pipefail

INSTANCE="${TRANSCRIBER_INSTANCE:-}"
case "$INSTANCE" in *[!a-z0-9-]*) echo "TRANSCRIBER_INSTANCE may only contain a-z, 0-9 and '-'"; exit 1;; esac
SUFFIX="${INSTANCE:+-$INSTANCE}"
LABEL="com.tailoredapps.kickgateway.transcriber$SUFFIX"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PROJECT="$REPO_ROOT/src/TailoredApps.KickGateway.Subscribers.Transcriber"
APP_DIR="$HOME/Library/Application Support/kickgateway/transcriber$SUFFIX"
ENV_FILE="${TRANSCRIBER_ENV_FILE:-$HOME/.config/kickgateway/transcriber$SUFFIX.env}"
PLIST="$HOME/Library/LaunchAgents/$LABEL.plist"
LOG_DIR="$HOME/Library/Logs/kickgateway"
LOG_FILE="$LOG_DIR/transcriber$SUFFIX.log"

if [ "$(uname -s)" != "Darwin" ]; then echo "This installer is for macOS."; exit 1; fi

DOTNET="$(command -v dotnet || true)"
[ -n "$DOTNET" ] || { echo "dotnet not found - install the .NET 10 SDK (brew install --cask dotnet-sdk)"; exit 1; }
FFMPEG="$(command -v ffmpeg || true)"
[ -n "$FFMPEG" ] || { echo "ffmpeg not found - brew install ffmpeg"; exit 1; }

if [ ! -f "$ENV_FILE" ]; then
  mkdir -p "$(dirname "$ENV_FILE")"
  cp "$REPO_ROOT/deploy/macos/transcriber.env.example" "$ENV_FILE"
  chmod 600 "$ENV_FILE"
  echo "Created $ENV_FILE - fill in the RabbitMq__* values, then run this script again."
  exit 1
fi

echo "Publishing the transcriber to: $APP_DIR"
"$DOTNET" publish "$PROJECT" -c Release -o "$APP_DIR" --nologo -v quiet
mkdir -p "$LOG_DIR" "$(dirname "$PLIST")"

xml_escape() { printf '%s' "$1" | sed -e 's/&/\&amp;/g' -e 's/</\&lt;/g' -e 's/>/\&gt;/g' -e 's/"/\&quot;/g'; }

# Environment for the agent: defaults first, then the user's file (which may override them).
declare -a ENV_KEYS=() ENV_VALS=()
put_env() {
  local i
  for i in "${!ENV_KEYS[@]}"; do
    if [ "${ENV_KEYS[$i]}" = "$1" ]; then ENV_VALS[$i]="$2"; return; fi
  done
  ENV_KEYS+=("$1"); ENV_VALS+=("$2")
}
put_env PATH "/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin"
put_env HOME "$HOME"
put_env DOTNET_ENVIRONMENT "Production"
put_env DOTNET_CLI_TELEMETRY_OPTOUT "1"
put_env Transcriber__FfmpegPath "$FFMPEG"
# Instance defaults (the env file may override them): the platform from the name's prefix, and one
# queue per instance so two agents never compete for the same segments.
case "$INSTANCE" in twitch|twitch-*) put_env Transcriber__Source "twitch" ;; esac
if [ -n "$INSTANCE" ]; then put_env Transcriber__QueueName "transcriber-$INSTANCE"; fi
while IFS= read -r line || [ -n "$line" ]; do
  line="${line%%#*}"
  line="${line#"${line%%[![:space:]]*}"}"
  [ -z "$line" ] && continue
  case "$line" in *=*) ;; *) echo "Ignoring malformed line in $ENV_FILE: $line"; continue;; esac
  put_env "${line%%=*}" "${line#*=}"
done < "$ENV_FILE"

case "$INSTANCE" in *-*)
  has_channels=0
  for k in "${ENV_KEYS[@]}"; do case "$k" in Transcriber__Channels__*) has_channels=1 ;; esac; done
  if [ "$has_channels" = 0 ]; then
    echo "WARNING: '$INSTANCE' has no Transcriber__Channels__* in $ENV_FILE - it binds EVERY channel of its platform,"
    echo "         so next to another agent on that platform every sentence is stored twice."
  fi ;;
esac

ENV_XML=""
for i in "${!ENV_KEYS[@]}"; do
  ENV_XML+="      <key>$(xml_escape "${ENV_KEYS[$i]}")</key>"$'\n'
  ENV_XML+="      <string>$(xml_escape "${ENV_VALS[$i]}")</string>"$'\n'
done

cat > "$PLIST" <<PLIST_EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>Label</key>
  <string>$LABEL</string>
  <key>ProgramArguments</key>
  <array>
    <string>$(xml_escape "$DOTNET")</string>
    <string>$(xml_escape "$APP_DIR/TailoredApps.KickGateway.Subscribers.Transcriber.dll")</string>
  </array>
  <key>WorkingDirectory</key>
  <string>$(xml_escape "$APP_DIR")</string>
  <key>EnvironmentVariables</key>
  <dict>
$ENV_XML  </dict>
  <key>RunAtLoad</key>
  <true/>
  <key>KeepAlive</key>
  <true/>
  <key>ThrottleInterval</key>
  <integer>15</integer>
  <key>StandardOutPath</key>
  <string>$(xml_escape "$LOG_FILE")</string>
  <key>StandardErrorPath</key>
  <string>$(xml_escape "$LOG_FILE")</string>
</dict>
</plist>
PLIST_EOF
chmod 600 "$PLIST"
plutil -lint "$PLIST" >/dev/null

UID_NUM="$(id -u)"
launchctl bootout "gui/$UID_NUM/$LABEL" 2>/dev/null || true
launchctl bootstrap "gui/$UID_NUM" "$PLIST"
launchctl kickstart -k "gui/$UID_NUM/$LABEL"

echo
echo "Installed and started '$LABEL'."
echo "  log:     $LOG_FILE   (tail -f it; look for 'Transcriber online' and the per-chunk timings)"
echo "  env:     $ENV_FILE"
echo "  status:  launchctl print gui/$UID_NUM/$LABEL | head -20"
echo "  stop:    launchctl bootout gui/$UID_NUM/$LABEL"
