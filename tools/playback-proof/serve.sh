#!/usr/bin/env bash
# usage: serve.sh <bindir> <statedir> <port>; starts a disposable real server in the foreground.
set -eu
BIN=$1; ST=$2; PORT=$3
FF=${PCR_FFMPEG:?set PCR_FFMPEG to ffwrap}
mkdir -p "$ST"/{data,config,cache,log}
cat > "$ST/config/network.xml" <<X
<?xml version="1.0" encoding="utf-8"?>
<NetworkConfiguration><InternalHttpPort>$PORT</InternalHttpPort><EnableIPv6>false</EnableIPv6></NetworkConfiguration>
X
exec dotnet "$BIN/tesserafin.dll" --datadir "$ST/data" --configdir "$ST/config" --cachedir "$ST/cache" --logdir "$ST/log" --nowebclient --ffmpeg "$FF"
