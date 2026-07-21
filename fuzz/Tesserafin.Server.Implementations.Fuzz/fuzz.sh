#!/bin/sh

set -e

dotnet build -c Release ../../Tesserafin.Server.Core/Tesserafin.Server.Core.csproj --output bin
sharpfuzz bin/Tesserafin.Server.Core.dll
cp bin/Tesserafin.Server.Core.dll .

dotnet build
mkdir -p Findings
AFL_SKIP_BIN_CHECK=1 afl-fuzz -i "Testcases/$1" -o "Findings/$1" -t 5000 ./bin/Debug/net10.0/Tesserafin.Server.Implementations.Fuzz "$1"
