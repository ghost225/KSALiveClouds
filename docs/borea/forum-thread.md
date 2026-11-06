# Draft: forums.ahwoo.com mod-release thread

Post this in the Mod Releases forum. Borea's index needs the thread's URL in `links.forums`.

---

**Title:** [StarMap] KSA Live Clouds 0.1.0 (pre-release): Earth's clouds from today's real weather

Stock Earth has one painted cloud map that slowly slides around the planet, so the Cape is often under cloud whatever the weather. **KSA Live Clouds** swaps it for today's real cloud cover from NOAA's hourly satellite image of the whole globe. If it's clear over the Cape today, it's clear over the Cape in the game.

**What it does**
- At launch it downloads NOAA's newest geostationary infrared mosaic (about 7 MB, no account or key) and rebuilds Earth's cloud maps from it in about 5 seconds while the game loads.
- Both cloud layers follow the real weather. The volumetric clouds from 2 km up follow real cloud, and the cirrus at 11 km follows the coldest cloud tops.
- Cloud types come from the weather: smooth decks lean stratus, lumpy fields and storms lean cumulus.
- The weather stays over the places it really is.
- While you play, the next hourly image fades in over 10 minutes.
- During time warp the clouds drift, then ease back over the real weather when you return to 1×.
- The poles and offline play fall back to the stock clouds.

**Requirements:** KSA 2026.10.25.5707 or later and StarMap 0.4.8 or later. Only Earth in the Sol system is changed.

**Status:** 0.1.0 is a pre-release. The cloud swap at launch has been seen working in game. The hourly fade and the time-warp drift have so far only been tested outside the game. It was developed and tested on Linux; Windows should work but hasn't been tried. Reports are welcome, ideally with the `[KSALiveClouds]` lines from the game log.

**Known limits:** infrared can't see warm low cloud (sea fog, marine stratus), so those mostly come out clear. New weather fades in as a dissolve rather than visible motion.

**Download:** https://github.com/ghost225/KSALiveClouds/releases (and on Borea once it's listed)
**Source and issues:** https://github.com/ghost225/KSALiveClouds (MIT)

Satellite data: NOAA/NESDIS GMGSI via the NOAA Open Data Dissemination program on AWS (public domain; not endorsed by NOAA).
