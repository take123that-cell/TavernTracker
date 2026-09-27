#!/bin/sh
# Rebuilds tt-sim.js from Firestone's simulator package (MIT). Needs Node.js.
#   cd sim && npm install && sh build.sh
set -e
npx esbuild entry.js --bundle --platform=neutral --format=iife --main-fields=main,module --target=es2019 \
  --inject:polyfill.js --define:process.env.BGS_COMBAT_PROFILE='"0"' --define:process.env.NODE_ENV='"production"' \
  --minify --legal-comments=eof --outfile=tt-sim.js
cp tt-sim.js ../src/TavernTracker.App/Combat/tt-sim.js
echo "Built tt-sim.js"
