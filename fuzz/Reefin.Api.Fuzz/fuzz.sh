#!/bin/sh

set -e

dotnet build -c Release ../../Reefin.Api/Reefin.Api.csproj --output bin
sharpfuzz bin/Reefin.Api.dll
cp bin/Reefin.Api.dll .

dotnet build
mkdir -p Findings
AFL_SKIP_BIN_CHECK=1 afl-fuzz -i "Testcases/$1" -o "Findings/$1" -t 5000 ./bin/Debug/net10.0/Reefin.Api.Fuzz "$1"
