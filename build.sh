#!/usr/bin/env sh
set -eu
if [ "$#" -gt 0 ]; then
  dotnet build BuildAdvisor.csproj -c Release -p:Sts2ManagedDir="$1"
else
  dotnet build BuildAdvisor.csproj -c Release
fi

