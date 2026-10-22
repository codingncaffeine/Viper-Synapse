Viper Synapse
=============

Key bindings, macros, lighting and sensitivity for Razer keyboards, keypads and mice on Linux.

Run ./viper-synapse to open the window. It starts its background service (viper-synapsed) by itself;
viper-cli lists and probes devices from a terminal.

One-time setup (the .deb and AUR packages do this for you)
---------------------------------------------------------
Viper Synapse talks to Razer devices directly and types through a virtual keyboard. Both need the udev
rule that hands Razer devices and /dev/uinput to the logged-in user:

    sudo install -m644 70-viper-synapse.rules /etc/udev/rules.d/
    sudo modprobe uinput
    sudo udevadm control --reload && sudo udevadm trigger

To load uinput at every boot:

    echo uinput | sudo tee /etc/modules-load.d/viper-synapse-uinput.conf

Linked programs (a profile that switches on while a program is in front) work on KDE Plasma, Hyprland
and X11 desktops. GNOME on Wayland does not tell other programs which window is in front.

Viper Synapse is free software under the GPL-3.0-or-later. It is not affiliated with or endorsed by
Razer Inc. Device photos are fetched from Razer's own servers and kept in ~/.cache/viper-synapse.
