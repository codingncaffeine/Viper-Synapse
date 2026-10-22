#!/usr/bin/env perl
# Builds data/devices.json from an OpenRazer checkout: the facts about each device (USB id, name,
# kind, lighting zones and their effects, matrix size, DPI range, polling rates, power features).
# The transaction id and control interface are not imported; Viper Synapse finds those at runtime
# with read-only probes, so a device missing here still works.
#
#   perl tools/import-openrazer.pl PATH/TO/openrazer > data/devices.json
use strict;
use warnings;
use JSON::PP;

my $root = shift or die "usage: $0 OPENRAZER_CHECKOUT\n";
my $dir = "$root/daemon/openrazer_daemon/hardware";
my $commit = `git -C '$root' log -1 --format=%h 2>/dev/null`; chomp $commit;

my (%class, @order);
for my $file (sort glob "$dir/*.py") {
    open my $fh, '<', $file or die "$file: $!";
    my $text = do { local $/; <$fh> };
    close $fh;
    # Split at each top-level class; the body runs to the next one.
    while ($text =~ /^class (\w+)\(([\w.]+)\):\n(.*?)(?=^class |\z)/msg) {
        my ($name, $base, $body) = ($1, $2, $3);
        my %c = (name => $name, base => $base, file => $file);
        $c{doc} = $1 if $body =~ /"""\s*(?:Class for (?:the )?)?(.*?)\s*"""/s;
        $c{pid} = hex $1 if $body =~ /^\s+USB_PID = 0x([0-9A-Fa-f]+)/m;
        $c{matrix} = [$1, $2] if $body =~ /^\s+MATRIX_DIMS = \[(\d+),\s*(\d+)\]/m;
        $c{dpi_max} = $1 + 0 if $body =~ /^\s+DPI_MAX = (\d+)/m;
        $c{poll} = [map { $_ + 0 } $1 =~ /(\d+)/g] if $body =~ /^\s+POLL_RATES = \[([^\]]*)\]/m;
        if ($body =~ /^\s+METHODS = (.*?\])\s*$/ms) {
            my $expr = $1;
            $c{methods_base} = $1 if $expr =~ /^(\w+)\.METHODS/;
            $c{methods} = [$expr =~ /'(\w+)'/g];
        }
        $class{$name} = \%c;
        push @order, $name;
    }
}

# Attributes a class does not set come from its base class; METHODS may extend another class's list.
sub inherited {
    my ($c, $key, $seen) = @_;
    $seen //= {};
    return undef if !$c || $seen->{$c->{name}}++;
    return $c->{$key} if exists $c->{$key};
    return inherited($class{$c->{base}}, $key, $seen);
}
sub methods {
    my ($c, $seen) = @_;
    $seen //= {};
    return [] if !$c || $seen->{$c->{name}}++;
    if (exists $c->{methods}) {
        my @m = @{$c->{methods}};
        unshift @m, @{methods($class{$c->{methods_base}}, $seen)} if $c->{methods_base};
        return \@m;
    }
    return methods($class{$c->{base}}, $seen);
}

my %zone_prefix = map { $_ => 1 } qw(logo scroll backlight right left charging fast_charging fully_charged);
my %feature_of = (
    get_dpi_xy => 'dpi', set_dpi_xy => 'dpi', get_dpi_xy_byte => 'dpi',
    get_dpi_stages => 'dpi_stages', set_dpi_stages => 'dpi_stages',
    get_poll_rate => 'poll_rate', set_poll_rate => 'poll_rate',
    get_battery => 'battery', is_charging => 'charging',
    get_idle_time => 'idle_time', set_idle_time => 'idle_time',
    get_low_battery_threshold => 'low_battery_threshold', set_low_battery_threshold => 'low_battery_threshold',
    get_scroll_mode => 'scroll_mode', set_scroll_mode => 'scroll_mode',
    get_scroll_acceleration => 'scroll_acceleration', set_scroll_acceleration => 'scroll_acceleration',
    get_scroll_smart_reel => 'smart_reel', set_scroll_smart_reel => 'smart_reel',
    set_custom_effect => 'custom_frame', set_key_row => 'custom_frame',
    get_keyswitch_optimization => 'keyswitch_optimization', set_keyswitch_optimization => 'keyswitch_optimization',
    get_game_mode => 'game_mode', set_game_mode => 'game_mode',
    get_macro_mode => 'macro_led', set_macro_mode => 'macro_led',
    keypad_set_profile_led_red => 'profile_leds', keypad_set_mode_modifier => 'mode_modifier',
    set_hyperpolling_wireless_dongle_indicator_led_mode => 'hyperpolling_dongle',
    get_keyboard_layout => 'keyboard_layout',
);

my @devices;
for my $name (@order) {
    my $c = $class{$name};
    next unless defined $c->{pid} && $name !~ /^_/;
    my $methods = methods($c);
    my ($type) = map { /^get_device_type_(\w+)/ ? $1 : () } @$methods;
    next unless $type;
    my (%zones, %features);
    for my $m (@$methods) {
        $features{$feature_of{$m}} = 1 if $feature_of{$m};
        next if $m =~ /^get_device_type/;
        my ($verb, $rest) = $m =~ /^(get|set)_(\w+)$/ or next;
        my $zone = 'main';
        for my $p (sort { length $b <=> length $a } keys %zone_prefix) {
            if ($rest =~ /^\Q$p\E_(.+)$/) { $zone = $p; $rest = $1; last }
        }
        $rest =~ s/_effect$//;
        if ($rest eq 'brightness') { $zones{$zone}{brightness} = JSON::PP::true; next }
        next if $verb ne 'set';
        next unless $rest =~ /^(static|static_classic|spectrum|wave|none|on|reactive|breath_\w+|starlight_\w+|ripple|ripple_random_colour|blinking|pulsate|wheel)$/;
        $zones{$zone}{effects}{$rest} = 1;
    }
    for my $z (values %zones) {
        $z->{effects} = [sort keys %{$z->{effects} // {}}];
        $z->{brightness} //= JSON::PP::false;
    }
    my $doc = $c->{doc} // $name;
    $doc =~ s/\s+/ /g;
    my %d = (
        pid => sprintf('%04x', $c->{pid}),
        name => $doc,
        type => $type,
        zones => \%zones,
        features => [sort keys %features],
        source => "openrazer $name",
    );
    my $matrix = inherited($c, 'matrix');
    $d{matrix} = [map { $_ + 0 } @$matrix] if $matrix && $features{custom_frame};
    my $dpi = inherited($c, 'dpi_max');
    $d{dpi_max} = $dpi if $dpi && $features{dpi};
    my $poll = inherited($c, 'poll');
    $d{poll_rates} = $poll if $poll && $features{poll_rate};
    push @devices, \%d;
}
@devices = sort { $a->{pid} cmp $b->{pid} } @devices;

my $json = JSON::PP->new->canonical->pretty->indent_length(2);
print $json->encode({
    source => "OpenRazer (https://github.com/openrazer/openrazer) $commit",
    note => 'Generated by tools/import-openrazer.pl; do not edit by hand. Hand-written entries go in data/devices-extra.json.',
    devices => \@devices,
});
