# Self-hosted build

This checkout is the owner's own distribution of PhoneBox, not a follower of upstream releases. It is built and signed
locally, and it never pulls code from `ersinkoc/phonebox`.

## What is different from upstream

- **Updates are off.** `updates.auto` defaults to `false`, and `updates.repo` defaults to empty. With an empty channel
  the agent makes no GitHub call at all (`UpdateService.CheckCoreAsync` returns early), so no upstream agent or APK can
  arrive on its own.
- **The APK is signed with the owner's key**, not the debug key and not the upstream release key. The keystore lives
  outside the repository.
- **The panel document is served by the agent** (`/panel/` through the app's `/agent/` proxy), and the agent's USB
  allowlist includes `/panel` and `/api/tmp`. This is what lets the phone recover from an agent restart and shows the
  local Storage / Temp / Compute widgets without a new APK.
- **A failed panel load shows a retrying page** instead of the raw `{"error":"offline"}` JSON: the document reloads
  itself every 3 seconds until the agent answers, so a restart or an unplugged cable no longer leaves a dead screen.

## One-time setup (already done on this machine)

Signing credentials live in `~/.config/phonebox/selfhost/`:

```
release.jks     # the APK signing key; back it up, losing it means uninstalling the app on every phone
signing.env     # PHONEBOX_STORE_FILE / PHONEBOX_STORE_PASSWORD / PHONEBOX_KEY_ALIAS / PHONEBOX_KEY_PASSWORD
```

Keep both files private (`chmod 600`), back them up somewhere safe, and never commit them. The first APK signed with a
new key needs one `adb uninstall net.oxog.phonebox.deskpanel` on each phone; later builds use `adb install -r`.

## Build and install

```sh
./scripts/selfhost.sh                 # build only: dist-selfhost/PhoneBox-<version>-selfhost.apk + dist-selfhost/agent/
./scripts/selfhost.sh --install-agent # build, replace ~/.local/share/PhoneBox/app, restart the user service
./scripts/selfhost.sh --install-app   # build, adb install -r, open the panel
./scripts/selfhost.sh --install       # both
```

The previous agent directory is kept as `~/.local/share/PhoneBox/app.bak-<timestamp>`; move it back and restart the
service to roll back. Configuration in `~/.config/PhoneBox/config.json` is never touched by the script.

## Merging an upstream security update by hand

Upstream still fixes security bugs; take those deliberately. The branch is based on an upstream tag (`v0.9.24`), with
local commits on top.

```sh
git fetch https://github.com/ersinkoc/phonebox.git main --tags
git log --oneline v0.9.24..v0.9.25            # read what changed, especially "Security"
git rebase --onto v0.9.25 v0.9.24 main         # replay this fork's commits onto the new tag
# resolve conflicts, then:
dotnet test agent/PhoneBox.Agent.Tests/PhoneBox.Agent.Tests.csproj -c Release
./scripts/selfhost.sh --install
```

Do not merge `main` of upstream (unreleased work); merge a tag. Keep `updates.auto` off. If you later want an update
channel of your own, publish releases signed with the same keystore and set `updates.repo` to that repository; the
checksum file in a release is integrity, not authentication, so keep the signing key offline.

## Known boundaries

- The package id is still `net.oxog.phonebox.deskpanel` because the agent, the USB key provider, the home alias and the
  update paths all name it. Renaming it is a separate change across `android/` and `agent/`.
- The agent still holds broad user-level powers (adb, synthetic input, `/api/run`, Control Deck, audio). Review those
  endpoints before exposing the agent beyond loopback.
- `LICENSE` is MIT: redistribution, including a private build, must keep the copyright notice and
  `THIRD-PARTY-NOTICES.txt`.
