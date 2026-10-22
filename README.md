<p align="center"><img src="assets/banner.png" alt="Viper Synapse"></p>

# Viper Synapse

A Razer Synapse–style control centre for Razer devices on Linux: key and button remapping,
Hypershift, macros, lighting, DPI, polling rate and power settings, in a window that feels like the
one you know.

Viper Synapse talks to devices directly over hidraw using Razer's control protocol, so it needs no
kernel module and no other daemon. Remaps and macros run in user space through evdev and uinput, and
they work the same on Wayland and X11.

> **Status:** early development. Nothing to install yet.

## Devices

| Device | Status |
| --- | --- |
| Tartarus V2 Pro | in progress |
| Basilisk V4 Pro | planned |

## Building

Requires the .NET 10 SDK.

```sh
dotnet build ViperSynapse.slnx -c Release
```

Device access needs the udev rule, which hands Razer devices and uinput to the logged-in user:

```sh
sudo install -m644 packaging/udev/70-viper-synapse.rules /etc/udev/rules.d/
sudo udevadm control --reload && sudo udevadm trigger
```

## Acknowledgements

The [OpenRazer](https://openrazer.github.io/) project documented the Razer control protocol that
this work builds on.

Viper Synapse is an independent project. It is not affiliated with or endorsed by Razer Inc.
"Razer" and "Synapse" are trademarks of Razer Inc.

## Licence

GPL-3.0-or-later. See [LICENSE](LICENSE).
