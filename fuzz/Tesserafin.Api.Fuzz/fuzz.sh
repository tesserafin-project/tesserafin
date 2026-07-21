#!/bin/sh

set -e

dotnet build -c Release ../../Tesserafin.Api/Tesserafin.Api.csproj --output bin
sharpfuzz bin/Tesserafin.Api.dll
cp bin/Tesserafin.Api.dll .

dotnet build
mkdir -p Findings
AFL_SKIP_BIN_CHECK=1 afl-fuzz -i "Testcases/$1" -o "Findings/$1" -t 5000 ./bin/Debug/net10.0/Tesserafin.Api.Fuzz "$1"
