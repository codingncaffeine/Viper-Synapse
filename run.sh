#!/usr/bin/env bash
# Builds the current source and starts Viper Synapse from it. Use this instead of reaching into bin/ folders:
# a copy already running from an older build is replaced by this one.
set -euo pipefail
cd "$(dirname "$0")"
if [ -f scratch/env.sh ]; then . scratch/env.sh; fi
dotnet build src/ViperSynapse.App/ViperSynapse.App.csproj -c Release -v q --nologo
exec src/ViperSynapse.App/bin/Release/net10.0/viper-synapse "$@"
