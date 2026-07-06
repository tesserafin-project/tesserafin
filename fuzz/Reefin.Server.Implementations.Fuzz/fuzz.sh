#!/bin/sh

set -e

dotnet build -c Release ../../Reefin.Server.Core/Reefin.Server.Core.csproj --output bin
sharpfuzz bin/Reefin.Server.Core.dll
cp bin/Reefin.Server.Core.dll .

dotnet build
mkdir -p Findings
AFL_SKIP_BIN_CHECK=1 afl-fuzz -i "Testcases/$1" -o "Findings/$1" -t 5000 ./bin/Debug/net10.0/Reefin.Server.Implementations.Fuzz "$1"
