#!/usr/bin/env bash
# Build this self-hosted PhoneBox (agent + APK), optionally installing it.
# Signing comes from ~/.config/phonebox/selfhost/signing.env (see docs/SELFHOST.md).
set -euo pipefail
root=$(cd "$(dirname "$0")/.." && pwd)
cd "$root"

install_agent=false
install_app=false
for arg in "$@"; do
    case "$arg" in
        --install-agent) install_agent=true ;;
        --install-app) install_app=true ;;
        --install) install_agent=true; install_app=true ;;
        -h|--help) sed -n '2,3p' "$0"; echo "usage: $0 [--install-agent|--install-app|--install]"; exit 0 ;;
        *) echo "usage: $0 [--install-agent|--install-app|--install]" >&2; exit 2 ;;
    esac
done

signing=${PHONEBOX_SIGNING_ENV:-$HOME/.config/phonebox/selfhost/signing.env}
if [ ! -f "$signing" ]; then
    echo "missing $signing: put PHONEBOX_STORE_FILE / PHONEBOX_STORE_PASSWORD / PHONEBOX_KEY_ALIAS / PHONEBOX_KEY_PASSWORD there" >&2
    exit 1
fi
# shellcheck disable=SC1090
. "$signing"

: "${ANDROID_HOME:=${HOME}/Android/Sdk}"
: "${ANDROID_SDK_ROOT:=$ANDROID_HOME}"
: "${JAVA_HOME:=/usr/lib/jvm/java-17-openjdk}"
export ANDROID_HOME ANDROID_SDK_ROOT JAVA_HOME
export PATH="$JAVA_HOME/bin:$PATH"

version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' agent/PhoneBox.Agent/PhoneBox.Agent.csproj | head -1)
out="$root/dist-selfhost"
rm -rf "$out"
mkdir -p "$out"

echo "==> APK $version"
(cd android && ./gradlew assembleRelease --console=plain -q)
cp android/app/build/outputs/apk/release/app-release.apk "$out/PhoneBox-$version-selfhost.apk"

echo "==> agent linux-x64 $version"
dotnet publish agent/PhoneBox.Agent/PhoneBox.Agent.csproj -c Release -f net10.0 -r linux-x64 \
    --self-contained true -p:PublishSingleFile=true -o "$out/agent" \
    --nologo -v q --disable-build-servers

echo "built: $out/PhoneBox-$version-selfhost.apk"
echo "built: $out/agent/PhoneBoxAgent"

if $install_agent; then
    app="$HOME/.local/share/PhoneBox/app"
    systemctl --user stop phonebox.service 2>/dev/null || true
    if [ -d "$app" ]; then
        backup="$app.bak-$(date +%Y%m%d-%H%M%S)"
        mv "$app" "$backup"
        echo "previous agent kept at $backup"
    fi
    cp -a "$out/agent" "$app"
    systemctl --user start phonebox.service
    echo "agent installed and started"
fi

if $install_app; then
    adb install -r "$out/PhoneBox-$version-selfhost.apk"
    adb shell am start -n net.oxog.phonebox.deskpanel/.MainActivity >/dev/null
    echo "APK installed and opened (adb install -r needs the same signing key; uninstall once if it changed)"
fi
