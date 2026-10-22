#!/usr/bin/env bash
# Runs INSIDE a clean Debian/Ubuntu container (packaging/deb-install-test.sh mounts the .deb): installs it with apt,
# checks the files it promises, that every shipped native binary resolves its libraries on this distro, that the
# commands run, and that the window starts on a virtual screen and stays up. Exit 0 = everything passed.
set -uo pipefail
apt-get update -qq >/dev/null
if ! apt-get install -y -q /tmp/viper-synapse.deb xvfb xauth dbus > /tmp/apt.log 2>&1; then
    tail -25 /tmp/apt.log
    exit 1
fi
. /etc/os-release
echo "installed $(dpkg-query -W -f='${Package} ${Version}' viper-synapse) on $PRETTY_NAME"

problems=0
for f in /usr/bin/viper-synapse /usr/bin/viper-synapsed /usr/bin/viper-cli \
         /usr/lib/udev/rules.d/70-viper-synapse.rules /usr/lib/modules-load.d/viper-synapse-uinput.conf \
         /usr/share/applications/io.github.codingncaffeine.ViperSynapse.desktop \
         /usr/share/icons/hicolor/256x256/apps/viper-synapse.png; do
    [ -e "$f" ] || { echo "FAIL $f missing"; problems=1; }
done
[ $problems = 1 ] || echo "ok   commands, udev rule, uinput, desktop entry and icons installed"

# Every shipped native binary must resolve its libraries on this distro. Exception: .NET's LTTng trace provider,
# which the runtime loads only when LTTng is installed.
for f in /usr/lib/viper-synapse/viper-synapse /usr/lib/viper-synapse/viper-synapsed /usr/lib/viper-synapse/viper-cli /usr/lib/viper-synapse/*.so*; do
    case $(basename "$f") in libcoreclrtraceptprovider.so) continue ;; esac
    bad=$(ldd "$f" 2>&1 | grep -E "not found|version .* not found" || true)
    if [ -n "$bad" ]; then
        echo "FAIL $(basename "$f") does not load here:"
        echo "$bad" | sed 's/^/        /'
        problems=1
    fi
done
[ $problems = 1 ] || echo "ok   every shipped library resolves"

v=$(/usr/bin/viper-synapse --version 2>&1 | head -1)
case $v in "Viper Synapse"*) echo "ok   viper-synapse --version: $v" ;; *) echo "FAIL viper-synapse --version said: $v"; problems=1 ;; esac
if /usr/bin/viper-cli list > /tmp/cli.log 2>&1; then echo "ok   viper-cli list runs (no devices in a container)"; else echo "FAIL viper-cli list: $(head -3 /tmp/cli.log)"; problems=1; fi

# The window, on a virtual screen with a private bus: it must start and still be running after 10 seconds.
export HOME=/tmp/home XDG_CONFIG_HOME=/tmp/home/.config
mkdir -p "$HOME"
xvfb-run -a dbus-run-session -- sh -c '/usr/bin/viper-synapse > /tmp/app.log 2>&1 & pid=$!; sleep 10; if kill -0 $pid 2>/dev/null; then kill $pid; exit 0; else exit 1; fi'
if [ $? = 0 ]; then echo "ok   the window started and stayed up"; else echo "FAIL the window exited:"; tail -15 /tmp/app.log; problems=1; fi
exit $problems
