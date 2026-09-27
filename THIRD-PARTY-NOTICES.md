# Third-party code

Tavern Tracker includes or builds on these open-source projects. Thank you to their authors.

| Project | Used for | License |
| --- | --- | --- |
| [@firestone-hs/simulate-bgs-battle](https://www.npmjs.com/package/@firestone-hs/simulate-bgs-battle) (Firestone, Zero to Heroes) | Combat odds simulator, bundled as `src/TavernTracker.App/Combat/tt-sim.js` (see `/sim`) | MIT |
| [@firestone-hs/reference-data](https://www.npmjs.com/package/@firestone-hs/reference-data) (Firestone) | Card data helpers used by the simulator | MIT |
| [UnitySpy](https://github.com/hackf5/unityspy) via [Firestone's fork](https://github.com/Zero-to-Heroes/unity-spy-.net4.5) | Read-only memory reader (`src/TavernTracker.Memory/UnitySpy`) | MIT |
| [Microsoft ClearScript](https://github.com/microsoft/ClearScript) | Runs the simulator's JavaScript (V8) | MIT |
| HearthSim ([HDT](https://github.com/HearthSim/Hearthstone-Deck-Tracker), [python-hslog](https://github.com/HearthSim/python-hslog), [python-hearthstone](https://github.com/HearthSim/python-hearthstone)) | Reference for log formats, game tags and combat input (no code copied) | MIT |
| [Firestone](https://www.firestoneapp.com/) public data (static.zerotoheroes.com, static.firestoneapp.com) | Card pool and tribe rules for the minion browser; hero, trinket and quest statistics for the pick overlays | Public data files |
| [HearthstoneJSON](https://hearthstonejson.com/) | Card art (tiles and card pictures); fallback card data | Data service |
| [HSReplay.net](https://hsreplay.net/) | Live Battlegrounds pool corrections | Public API |
| [Cinzel](https://github.com/NDISCOVER/Cinzel) font | Headings and logo (`src/TavernTracker.App/Fonts`, license in `OFL-Cinzel.txt`) | SIL Open Font License 1.1 |
