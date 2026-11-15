#!/usr/bin/env sh
set -eu
cd "$(dirname "$0")/.."
dotnet restore
dotnet build --no-restore
(cd src/JobFlow.Web && npm ci && npm run build)
mkdir -p src/JobFlow.Api/wwwroot
cp -R src/JobFlow.Web/dist/jobflow-web/browser/. src/JobFlow.Api/wwwroot/
