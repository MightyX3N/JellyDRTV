# JellyDRTV

A Jellyfin plugin that puts all of DRTV into Jellyfin as a normal library.

- **Everything streams.** Every series, film and program on DRTV shows up in Jellyfin with title, description, poster and episode pictures. Press play and it streams straight from DR, in every Jellyfin app.
- **Download what you want to keep.** Episodes, whole seasons, whole series, films and programs. Downloads replace the streaming placeholder in the same spot, with Danish subtitles, so they survive when DR removes the title.
- **Follow series.** New episodes are downloaded automatically.
- **Favourite = download.** Mark something as favourite in any Jellyfin app (phone, TV, web) and it's downloaded. A favourited series is followed.

> Only works on a Jellyfin server **in Denmark**. DR blocks most streams outside Denmark, and the plugin does not try to get around that. Titles that are DRM-protected can't be played or downloaded.

## Requirements

- Jellyfin **12.4** or newer
- Enough disk space for the library folder: about **1.5 GB** of pictures and metadata after the first sync (less with episode thumbnails turned off), plus whatever you download

yt-dlp is downloaded and kept up to date by the plugin automatically. Jellyfin's own ffmpeg is used for merging.

## Install

**From the plugin repository (recommended):**

1. In Jellyfin go to **Dashboard → Plugins → Repositories**, press **+** and add:
   - Name: `JellyDRTV`
   - URL: `https://raw.githubusercontent.com/MightyX3N/JellyDRTV/main/manifest.json`
2. Go to **Catalog**, find **JellyDRTV** and install it.
3. Restart Jellyfin.

Updates then show up in Jellyfin like any other plugin.

**Manually:** put `Jellyfin.Plugin.JellyDRTV.dll` (from the zip in `releases/`, or built yourself, see below) in a folder called `JellyDRTV` inside Jellyfin's `plugins` folder, e.g. `/config/data/plugins/JellyDRTV/` in Docker, and restart Jellyfin.

## Set up

1. **Dashboard → Plugins → JellyDRTV**: set the **Library folder** (e.g. `/media/drtv`) and save.
2. Press **Sync DRTV catalog now**. The first sync fetches about 3,500 titles and 28,000 episodes, so it takes a while (roughly 15-30 minutes). Later syncs only take a few minutes. It then runs every night by itself.
3. Add three libraries in **Dashboard → Libraries**:
   | Library type | Folder |
   |---|---|
   | Shows | `<library folder>/Serier` |
   | Movies | `<library folder>/Film` |
   | Movies | `<library folder>/Programmer` |

   The first library scan of 35,000 items takes a while too.

Optional: in those libraries' settings you can turn off the online metadata downloaders (TMDb etc.) to make scans faster. The DR titles and descriptions are kept either way.

If you changed Jellyfin's port or set a base URL, change **Jellyfin's own address** in the plugin settings to match (e.g. `http://127.0.0.1:8096/jellyfin`) and sync again.

## Using it

- **In any Jellyfin app:** DRTV is just part of your libraries: search, browse, continue watching. Favourite an episode, season, film or series to download it.
- **On the plugin page:** search DRTV, open a series to see its seasons, and use *Download*, *Download season*, *Download all* or *Follow*. The download queue with progress and retry is on the same page.

### Scheduled tasks

| Task | Default | What it does |
|---|---|---|
| Sync DRTV catalog | daily 04:00 | Adds new titles and episodes, removes what DR took down (downloaded files are never removed) |
| Check followed DRTV series | every 2 hours | Downloads new episodes of followed series |

## How it works

- The library folder contains `.strm` files. Each one points at `/JellyDRTV/Stream/<id>/master.m3u8` on your own Jellyfin server. When played, the plugin asks DR for a fresh stream and hands Jellyfin the best quality version. DR's Danish subtitles are saved next to the episode the first time it plays.
- Metadata comes from `.nfo` files and pictures the plugin writes from DR's data.
- Downloads use [yt-dlp](https://github.com/yt-dlp/yt-dlp) on DR's HLS stream (the same stream Jellyfin plays), saved as `.mkv` with `.vtt` subtitles next to it.

## Folder layout

```
<library folder>/
  Serier/
    Serie [drtv-12345]/
      tvshow.nfo, poster.jpg, fanart.jpg
      Season 01/
        Serie S01E01 - Afsnitstitel.strm   (or .mkv once downloaded)
        Serie S01E01 - Afsnitstitel.nfo
        Serie S01E01 - Afsnitstitel.da.vtt
  Film/
    Title (2019) [drtv-67890]/Title (2019).strm
  Programmer/
    Title (2021) [drtv-13579]/Title (2021).strm
```

Some DR series number their seasons by year, so you'll see folders like `Season 2008`.

## Build

```
dotnet build Jellyfin.Plugin.JellyDRTV -c Release
```

Output: `Jellyfin.Plugin.JellyDRTV/bin/Release/net10.0/Jellyfin.Plugin.JellyDRTV.dll`. Needs the .NET 10 SDK.

## Notes

- Downloads are meant for private use. Don't share them.
- The plugin uses the same public DRTV web API as dr.dk/drtv. If DR changes it, the plugin may need an update.
