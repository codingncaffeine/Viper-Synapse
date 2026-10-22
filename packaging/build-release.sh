#!/usr/bin/env bash
# Builds the Linux release artifacts into packaging/out:
#   ViperSynapse-<ver>-linux-x64.tar.gz   self-contained, extract anywhere, run ./viper-synapse
#   viper-synapse_<ver>_amd64.deb          system install, with the udev rule and uinput at boot
# The version is VersionPrefix in Directory.Build.props.
set -euo pipefail
cd "$(dirname "$0")/.."

VER=$(grep -oPm1 '(?<=<VersionPrefix>)[^<]+' Directory.Build.props)
OUT=${VIPER_OUT:-packaging/out}
PUB=$OUT/publish
# Every removal names its variable with :? so an empty one stops the script instead of reaching /.
rm -rf "${OUT:?}" && mkdir -p "$PUB"

# Stale obj/ output can publish XAML that was never compiled: every release starts from clean trees.
for p in src/ViperSynapse.App src/ViperSynapse.Service src/ViperSynapse.Cli src/ViperSynapse.Hardware src/ViperSynapse.Core; do
    rm -rf "${p:?}/bin" "${p:?}/obj"
done

echo "── publish v$VER (self-contained linux-x64)"
for project in ViperSynapse.App ViperSynapse.Service ViperSynapse.Cli; do
    dotnet publish "src/$project/$project.csproj" -c Release -r linux-x64 --self-contained true \
        -p:VersionPrefix="$VER" -o "$PUB" -v q --nologo
done

# The views must be compiled into the app; a publish that fell back to loading XAML at run time is not shipped.
if ! grep -qa CompiledAvaloniaXaml "$PUB/viper-synapse.dll"; then
    echo "!! viper-synapse.dll carries no compiled XAML: refusing to package." >&2
    exit 3
fi
for f in viper-synapse viper-synapsed viper-cli; do
    [[ -x "$PUB/$f" ]] || { echo "!! $f is missing from the publish." >&2; exit 3; }
done

cp LICENSE "$PUB/LICENSE"
cp packaging/README.txt "$PUB/README.txt"
cp packaging/udev/70-viper-synapse.rules "$PUB/"
cp assets/icon-512.png "$PUB/viper-synapse.png"

echo "── tarball"
tar -C "$PUB" -czf "$OUT/ViperSynapse-$VER-linux-x64.tar.gz" .

echo "── deb"
DEB=$OUT/debroot
rm -rf "${DEB:?}"
mkdir -p "$DEB/DEBIAN" "$DEB/usr/lib/viper-synapse" "$DEB/usr/bin" "$DEB/usr/share/applications" \
         "$DEB/usr/share/doc/viper-synapse" "$DEB/usr/lib/udev/rules.d" "$DEB/usr/lib/modules-load.d"
cp -a "$PUB/." "$DEB/usr/lib/viper-synapse/"
for f in README.txt LICENSE viper-synapse.png 70-viper-synapse.rules; do
    rm -f "${DEB:?}/usr/lib/viper-synapse/$f"
done
for f in viper-synapse viper-synapsed viper-cli; do
    printf '#!/bin/sh\nexec /usr/lib/viper-synapse/%s "$@"\n' "$f" > "$DEB/usr/bin/$f"
    chmod 755 "$DEB/usr/bin/$f"
done
for s in 16 32 48 64 128 256 512; do
    install -Dm644 "assets/icon-$s.png" "$DEB/usr/share/icons/hicolor/${s}x${s}/apps/viper-synapse.png"
done
cp packaging/io.github.codingncaffeine.ViperSynapse.desktop "$DEB/usr/share/applications/"
cp packaging/udev/70-viper-synapse.rules "$DEB/usr/lib/udev/rules.d/"
cp packaging/viper-synapse-uinput.conf "$DEB/usr/lib/modules-load.d/"
cp packaging/README.txt "$DEB/usr/share/doc/viper-synapse/README.txt"
cp LICENSE "$DEB/usr/share/doc/viper-synapse/copyright"
cat > "$DEB/DEBIAN/postinst" <<'POST'
#!/bin/sh
set -e
if [ "$1" = "configure" ]; then
    modprobe uinput 2>/dev/null || true
    if command -v udevadm >/dev/null 2>&1; then
        udevadm control --reload || true
        udevadm trigger --subsystem-match=hidraw --subsystem-match=misc --action=change || true
    fi
fi
POST
chmod 755 "$DEB/DEBIAN/postinst"
INSTALLED_KB=$(du -sk "$DEB/usr" | cut -f1)
cat > "$DEB/DEBIAN/control" <<CTRL
Package: viper-synapse
Version: $VER
Section: utils
Priority: optional
Architecture: amd64
Installed-Size: $INSTALLED_KB
Depends: libc6, libgcc-s1, libstdc++6, libicu76 | libicu74 | libicu72 | libicu70, libfontconfig1, libfreetype6, libx11-6, libx11-xcb1, libxcb1, libxi6, libxcursor1, libxext6, libxrandr2, libxrender1, libice6, libsm6, libegl1, libgl1, libwayland-client0, libxkbcommon0, zlib1g, udev
Maintainer: Viper Synapse <codingncaffeine@users.noreply.github.com>
Homepage: https://github.com/codingncaffeine/Viper-Synapse
Description: Razer device settings: key bindings, macros, lighting and sensitivity
 Viper Synapse controls Razer keyboards, keypads and mice in a window modelled on
 Razer Synapse: rebind any key (keyboard and mouse functions, turbo, Hypershift,
 text, programs), record and edit macros, set lighting effects, sensitivity stages
 and polling rate, keep profiles and switch them with the program in front.
 .
 It talks to devices directly over hidraw, with no kernel module, and types through
 uinput; the package installs the udev rule that gives the logged-in user access.
CTRL
dpkg-deb --build --root-owner-group "$DEB" "$OUT/viper-synapse_${VER}_amd64.deb" > /dev/null
rm -rf "${DEB:?}"

echo "── artifacts:"
ls -sh1 "$OUT" | grep -v publish
