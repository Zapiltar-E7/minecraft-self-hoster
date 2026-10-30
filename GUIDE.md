# User guide

Host a Minecraft **Java Edition** server on your PC so friends can join over the internet, with or without port forwarding.

## How it works

**If you can't (or don't want to) set up your router**, the app uses [playit.gg](https://playit.gg), a free tunnel service. A small agent on your PC opens an *outgoing* connection to playit.gg. Friends connect to a public address (like `name.gl.joinmc.link`), and playit relays their traffic through that connection to your server.

```
Friend's game  ->  playit.gg  ->  (outbound tunnel)  ->  playit agent on your PC  ->  your server port
```

**If you can forward a port on your router**, pick **Port forwarding** on the Connections tab instead. Friends then connect straight to your public IP, and neither playit.gg nor Cloudflare is needed.

## The app

Double-click **`Minecraft Host.exe`**. It's a single self-contained program, so you don't need to install .NET or anything else.

Keep the `.exe` in its folder. It keeps its own files next to itself: `tools` (Java, playit, cloudflared), `backups` and `host-config.json`. The server itself can be in any folder (see **Server folder** below).

**Several servers:** the ▾ next to the server name lists every server the app manages.
- **Switch:** click one to manage it. Every tab then shows that server. One server runs at a time, so stop the current one first.
- **Add a server...:** choose a folder that already has a server, make a new one, or set one up **from a modpack**. A new one gets its own folder under `servers\` next to the app; then pick a type and version under Server and click Install.
- **Duplicate this server...:** copies the current server (stopped) into `servers\<new name>` with the same plugins, mods, settings, type and version, RAM and launch arguments, then shows the copy. You can leave out the worlds (the copy starts a new world but keeps the datapacks) and BlueMap's map data (off by default; the copy's map draws itself again). Logs and caches aren't copied, and the panel checks there's room on the drive first. The original isn't changed.
- **Remove this server from the list...:** takes it off the list. Its files are never deleted, and you can add it back with Choose its folder.
- **Per-server settings:** each server keeps its own name, folder, type and version, RAM, launch arguments, playit address, sharing switches and connection addresses. Sounds, startup options and the CurseForge key are shared by all of them.
- **Importing** a `.mchost` file adds the server to the list instead of replacing the current one.

The sidebar holds the server's name, its status and the start and stop buttons, then the pages: **Server · Logs · Connections · Plugins · Mods · Configs**. **Tools**, **Settings**, **Help** and **Info** are at the bottom. A small dot on **Plugins** or **Mods** means an update is available there; on **Connections** it means a plugin or mod added a connection you haven't looked at yet.

**Help** (bottom of the sidebar) starts a short tour of the page you're on. It highlights each field in turn and explains it. Use Next and Back (or the arrow keys), or End tour (Esc).

**Drag and drop:** drop files anywhere on the window to install them.
- **Plugins and mods (`.jar`):** the app reads each jar to tell which it is, then installs it into `plugins` or `mods`.
- **Datapacks:** a `.zip` (or folder) with a `pack.mcmeta` goes into the world's datapacks folder.
- **Already installed:** if an older copy of the same plugin or mod is there, the app asks before replacing it, and the old file goes to backups.
- **Server file (`.mchost`):** dropped on its own, it starts an import, the same as Tools → Import server.
- **Modpack (`.mrpack`, or a CurseForge `.zip` or `manifest.json`):** dropped on its own, it starts **Install a modpack** (below).

### Server controls (in the sidebar)

| Button | What it does |
|---|---|
| **Start / Stop** | Run the server. The status pill shows Stopped, Starting, Running or Stopping. While the server runs, **Start** becomes **Restart**: it stops the server safely (saving the worlds) and starts it again. |
| **Safe mode** | Start without plugins and custom datapacks (see below). |
| **Force stop** | Hard-kills the server, the playit tunnel and the map tunnel when they're stuck, including ones the app lost track of. The server doesn't save first, so use **Stop** whenever it still responds. |

### Server tab

Four sections along the top:
- **General:** server software and version, memory, launch arguments and the server folder. Pick the **type** (Paper, Fabric, Quilt, Forge, NeoForge or Vanilla) and **Minecraft version**, then install or switch. It warns before a switch that stops plugins or mods loading, or before going to an older version, and offers to back up the worlds first.
- **Players:** who's online and who was on recently, this week's numbers, the whitelist and operators.
- **World:** how the server looks in the multiplayer list (message of the day with colours, and the icon), and gameplay settings.
- **Backups & restarts:** automatic backups, scheduled restarts with chat warnings, and restarting after a crash.

### Tools

At the bottom of the sidebar.

| Tool | What it does |
|---|---|
| **Set up this PC** | Checks for and installs Java, the playit agent, your playit account link and cloudflared (see below). |
| **Link playit.gg account** | Links this PC to a playit.gg account (or re-links it). |
| **Set public address** | Saves the playit address shown under "Friends join with". |
| **Back up worlds** | Zips your worlds into `backups\<server name>\`. |
| **Install a modpack** | Sets a server up from a modpack file (see **Modpacks** below). Also under Server → Software, and by dropping the file on the window. |
| **Export server / Import server** | Move the server to another PC (see below). |

Closing the window while the server runs asks first, then stops the server safely and saves the worlds.

### Connections tab

How players and browsers reach your server. It updates itself from the plugins and mods you have switched on.

- **Minecraft:** choose **playit.gg** or **Port forwarding**. With port forwarding, the card shows which port to forward to which local address, and friends join with your public IP (or a domain you enter). With playit.gg, **Let friends join** opens the tunnel when the server starts. **Friends join with** is the address to give friends, with Copy and a pencil to change it. **Open playit.gg tunnels** opens your tunnel list.
- **Web pages:** maps (BlueMap, Dynmap, squaremap, Pl3xMap) and Plan each get a card.
  - **Share with a link (Cloudflare)** gives friends a free browser link while the server runs (see below).
  - An optional **playit address (paid plan)** gives a link that stays the same, if you have playit Premium.
- **Connections with their own port:**
  - **The plugins:** Simple Voice Chat (UDP), Geyser for Bedrock players (UDP) and Votifier (TCP).
  - **What the card shows:** the port on this PC, and how to add a matching tunnel at playit.gg. Paste the address playit gives you into **Public address**. Until you do, the card says **Needs setup**.
  - **Voice chat:** saving the address also sets `voice_host` in Simple Voice Chat's settings (the old file is backed up), so players' voice chat goes through the tunnel. Restart the server to use it.

### Logs tab

The live server log, with a box for server commands (for example `op YourName`, no slash needed). Errors from installs, backups and downloads also show up here.

### Settings tab

At the bottom of the sidebar.

- **Open when Windows starts:** the app opens minimized when you sign in. If another copy of the app (in another folder) already does this, a note says so, and switching this on makes it this copy instead.
- **Play sounds** (on by default):
  - **Jingle:** a light jingle when the server comes online, or an export or import finishes.
  - **Error tone:** a soft tone when one of those fails, or the server shuts down unexpectedly (a crash or a failed start, but not Stop, Force stop or an in-game `/stop`).
  - Switching it on plays the jingle once, so you can hear it.
- **Start the server when the app opens:** same as clicking Start, including the playit tunnel and map sharing if they're on. It's skipped if something is already using the server's port.
- **Update automatically** (on by default): checks this repository's releases at startup and every 6 hours. A newer build is downloaded, checked against its SHA-256, and used from the next start (or straight away with **Restart**).

### Configs tab

Sub-tabs at the top sort the files, each showing how many it has:

- **Server:** `server.properties`, `bukkit.yml`, ops and whitelist, and Paper's own configs.
- **Plugins:** each plugin's folder.
- **Datapacks:** the files inside each datapack (`pack.mcmeta`, JSON and `.mcfunction`), including packs that are still zipped. Saving a file inside a zip backs up the whole zip first. Changes apply after `/reload` or a restart.
- **Mods:** the `config` folder that Fabric and Forge mods use, and Forge's per-world `serverconfig`.

Search within a sub-tab, then click a file to edit it.

- **Settings view (default):** every setting is a row. The name and its description, taken from the file's own comments, are on the left; the value is on the right. True/false settings are switches and everything else is a text box. Rows are grouped under section headings that follow the file's nesting (for example `world › spawning`). **Filter settings...** narrows the rows, and changed rows are outlined in amber.
- **Text view:** the raw file. Use it for lists and anything else the grid can't show. You can switch between the two at any time, and edits carry over.
- **Saving keeps your file intact.** Only the values you changed are rewritten. Comments, ordering and spacing stay exactly as they were. JSON files have no comments, so they are rewritten neatly formatted.
- **Supported formats:** YAML (`.yml`), `.properties`, JSON, HOCON (`.conf`, e.g. BlueMap), and TOML/INI. Plain `.txt` files open as text.
- **Save** (or Ctrl+S) checks the file first. It flags broken JSON and YAML indented with tabs. In the Text view, Tab types two spaces.
- **Every save backs up the previous version** to `backups\configs\<date-time>\`. **Revert** throws away unsaved changes.
- Data, logs, secrets (for example LuckPerms' keystore) and mcMMO's reference copies in `defaults\` are left out of the list on purpose.
- Most changes take effect after a server restart. Some plugins also have their own reload command.

- Each plugin's and datapack's files have a coloured dot: light blue if the plugin works, light red if it's broken or out of date, grey if unknown (see the next tab).

### Plugins tab (plugins and datapacks)

Lists every plugin and datapack in the server, including switched-off ones, each with a coloured dot:

- **Light blue:** works on this server version. Either the exact file is listed for this version on Modrinth or CurseForge, it loaded cleanly the last time the server started, or (for datapacks) its pack format matches.
- **Light red:** broken or out of date. It failed or switched itself off at the last start, it has no build for this version, a newer version is available, or (for datapacks) it was made for a different pack format.
- **Grey:** nothing to go on yet (not found online and hasn't run on this version).

The tab works like this:

- **Switches** turn things on or off by moving the file between the server and server\disabled\ (plugins and datapacks). Files in the older disabled-until-... folders are listed too. The server must be stopped. It won't switch on a second copy of a plugin that is already on.
- **Delete** (the red trash button, next to the folder and web buttons) removes a plugin, datapack or mod after asking. The file isn't destroyed: it moves to `backups\deleted\<date-time>\` next to the app, so you can put it back. A plugin's settings folder is kept. The server must be stopped.
- **Checking for updates:** opening the tab looks every file up by its hash on Modrinth (and CurseForge, if a key is set), at most once every 5 minutes. **Check for updates** checks again right away. Modrinth needs no account.
- **CurseForge key:** CurseForge doesn't let apps sign you in. To include CurseForge, get a free API key at console.curseforge.com and paste it here. It's saved in host-config.json.
- **Update / Update all** download the newer file (checked against its hash) and move the old one to `backups\plugins\<date-time>\`. The server must be stopped. Some CurseForge authors only allow downloads from their website, and for those the app opens the download page instead.
### Mods tab

Works exactly like the Plugins tab, for Fabric, Quilt, Forge and NeoForge mods in the server's `mods` folder:

- **Status dots:** blue if the mod is listed for this Minecraft version on Modrinth/CurseForge, or if the server last started cleanly with it. Red if it's out of date, or made for another loader (for example a Forge mod on a Fabric server).
- **Updates:** checked for the mod's own loader.
- **Switching off:** moves the jar to `disabled\mods`.
- **Old jars after an update:** go to `backups\mods\<date-time>\`.

If the server type can't load mods (Paper, Vanilla) or plugins (Fabric, Quilt, Forge, NeoForge, Vanilla), a note on that tab says so. On the Configs tab, mod settings from the `config` folder show under **Mod configs**, or under the mod's own folder name.

## Modpacks

**Install a modpack** (Tools, or Server → Software, or drop the file on the window) takes:
- a **Modrinth** `.mrpack` file (the "Download" on a modpack's Modrinth page), or
- a **CurseForge** modpack `.zip` (the one with a `manifest.json` inside), or just its `manifest.json`. Only the zip includes the pack's own settings files.

It shows the pack's Minecraft version, mod loader and number of files, then asks where to put it:
- **A new server** named after the pack, in `servers\<pack name>`, with at least 6 GB of RAM. This is the default.
- **Replace this server:** the current server switches to the pack's loader and version. Its old `mods` folder moves to `backups\<server name>\mods\`, its `config` folder is copied to `backups\<server name>\configs\`, and the worlds can be backed up first.

Then it installs the **exact** Minecraft version and mod loader build the pack names (Fabric, Quilt, Forge or NeoForge), downloads every mod (checked against its hash), and copies the pack's settings files in.

- **Mods only players need** (shaders, menus, minimaps...) don't run on a server and often crash it. The app leaves out what the pack marks as client-only, then switches off mods whose own files or whose Modrinth page say they're client-only. They go to `disabled\mods`, so they show switched off on the Mods tab. Resource packs and shaders are left out.
- **If a mod still crashes the server while it starts**, the app reads the crash report, names the mod, and offers to switch it off and start again.
- **Java:** older Minecraft versions come with an older Java (1.20.1 uses Java 17, 1.21.1 uses Java 21), and many mods only start on that exact version. The app uses it when this PC has it, and otherwise offers to download it into `tools\java`.
- **CurseForge without a key:** mods download through curseforge.com. A few authors don't allow downloads from other apps; those are listed at the end (with their pages) to download yourself and drop on the window.
- **Friends** need the same modpack installed in their launcher (CurseForge app, Modrinth app, Prism...) to join.

## Moving the server to another PC

**On the old PC:** stop the server, then use **Tools → Export server**. A panel shows where the file will be saved (the last export folder, or the app's folder the first time), with **Change...** to pick another place, how much space that drive has, and what's included. Click **Export** to start. If the file won't fit on that drive, Export stays greyed out until you pick another. It saves one `.mchost` file containing:

- **Worlds:** every dimension, including player inventories. They're packed about 40% smaller than a zip without losing anything: each region's chunks are unpacked and compressed together, and every chunk is checked when importing.
- **Plugins and datapacks,** including switched-off ones. Plugins Modrinth recognises are downloaded again on the new PC (checked against the original file's hash). Others are carried in the file.
- **All config files and plugin data,** including CoreProtect's history, LuckPerms permissions and Essentials homes.
- **This app's settings:** server version, RAM, public address, name and CurseForge key.

It leaves out BlueMap's map data (it re-renders), logs, caches and Paper's downloaded libraries.

**The file holds every password and token in your configs** (for example the Discord bot token), so keep it private.

**On the new PC:**

1. Put `Minecraft Host.exe` in a folder and start it.
2. Use **Tools → Import server** and pick the file. The server goes into the `server` folder next to the app, or an empty folder you choose.
3. **Set up this PC** installs Java, playit and Cloudflare.
4. **Link playit.gg account** with the same playit account. Then check at playit.gg that your tunnel uses the new PC's agent, so friends keep the same address.
## Setting up a new PC

Copy `Minecraft Host.exe` to the new PC and run it. If anything is missing, a yellow **This PC needs a few things first** banner appears. Click **Set up this PC** to see a checklist, then **Install missing**:

- **Java:** a portable Eclipse Temurin build goes into `tools\java`. It needs no admin rights and doesn't change the rest of the PC.
- **playit.gg agent and cloudflared:** the official downloads go into `tools\`.
- **playit.gg account link:** opens your browser to sign in or create a free account and approve the PC.

Every download comes from the official source and is checked against its published checksum. The checklist's **Choose folder** and **Install** buttons then set up the Minecraft server itself.

## First-time setup

1. On the **Tools** tab, click **Set up this PC**, then **Install missing**.
2. Choose an existing server folder, or pick a type and version under **Server software** on the Server tab and click **Install**.
3. Click **Start**. If the playit account isn't linked yet, a claim link opens in your browser. Sign in, or create a free account, and approve the agent.
4. Go to https://playit.gg/account/tunnels and add a tunnel of type **Minecraft Java** pointing at `127.0.0.1` and your server port. The port is shown when you click **Tools → Set public address**.
5. Copy the address playit gives you, click **Tools → Set public address**, and paste it in.
6. Send that address to your friends. They add it under *Multiplayer > Add Server*.

## Looking after the server

- **Backups** (Server tab): switch on **Back up the worlds automatically** and pick how often and how many to keep. Backups run while the server is running without kicking anyone (saving pauses for a moment), and optionally at each scheduled restart. Only the oldest *automatic* backups are removed; ones you make yourself are kept. **Back up now** works any time.
- **Crashes:** with **Start it again if it crashes** on (Restarts card), a crashed server comes back after 10 seconds, at most 3 times in 10 minutes.
- **Performance** (top of the Logs tab): TPS, tick time, memory and CPU with small graphs of the last few minutes. If TPS drops below 18 the tile turns amber and the log says when the lag started and ended. To read TPS quietly the app turns on the server's RCON with a random password (in server.properties).
- **Players** (Server tab): who's online and for how long, who was on recently and how much they've played. Click a name to message, teleport, change game mode, make operator, kick or ban.
- **Console** (Logs tab): warnings in amber and errors in red, filters for chat, problems or the app's own messages, a search box, and copying. In the command box, Up and Down go through earlier commands and Tab completes commands and player names.
- **World settings** (Server tab): the message of the day with colours and a preview, difficulty, game mode, max players, view and simulation distance, PVP and flight, plus **Server icon**: choose any picture and it's cropped and resized for you.
- **Stopping with people online:** Stop and Restart ask whether to warn players and wait a minute (chat countdown; press Stop again to cancel) or do it now.
- **Asked to join:** when the whitelist turns someone away, their name appears on the Whitelist card with an Add button.
- **This week** (Server → Players): players, hours played, most online, average TPS and crashes over the last 7 days, compared with the week before.
- **Notifications:** while the app is in the tray, a note by the clock when players join or leave (Settings → Notifications).
- **Left running?** If the app closed while the server was still running, the next time it opens it offers to stop that server safely.
- **What's using TPS?** (Logs tab, TPS tile): records what the server spends its time on for 60 seconds with spark (built into Paper) and opens the report in your browser. The report is uploaded to spark.lucko.me so it can be viewed.
- **Low disk space:** when the server's drive has less than 10 GB free, the Server tab shows a warning, and automatic backups are skipped rather than filling the drive.
- **Disk space** (Tools): what's using room, largest first: worlds, the map, plugin data, logs, backups.
- **System tray** (Settings): minimizing hides the app to an icon by the clock. Click it to come back, or right-click to start or stop the server.

## Appearance

**Settings → Appearance:**
- **Highlight colour:** the colour of the open page, buttons and switches. **Auto** uses the menu colour's own, or pick one of 24.
- **Menu colour:** the background, sidebar and text colours. 75 to choose from, each shown as a small preview: Default, Dark, OLED (pure black) and Light, Minecraft ones (Diamond, Redstone, Sculk, The End, Cherry Grove...), colour moods (Ocean, Synthwave, Sakura, Matrix...), classic editor schemes (Dracula, Nord, Catppuccin, Gruvbox, Tokyo Night...) and light ones (Paper, Sky, Solarized Light...).
- **Animations:** fades and slides for pages, the sidebar, panels and colour changes. Switch it off for instant changes.

The icon next to the server's name is that server's `server-icon.png`, the picture players see in their server list. If the server doesn't have one, it's the app's blue creeper.

## Sharing a web map (or other web page)

When **Share with a link** is on for a web page on the Connections tab, starting the server also opens a free **Cloudflare quick tunnel** for it (no account needed). It gives you a link like `https://some-random-words.trycloudflare.com`, shown on that page's card. BlueMap's link is also saved to `map-link.txt`.

**The link changes every time the server restarts**, so send your friends the new one each time. Anyone with the link can see the map, including live player positions if BlueMap shows them.

**Discord:** if EssentialsX Discord is set up, the BlueMap card has a **Link in Discord** switch. Each new link then replaces a `🗺️ Live map: …` line in the description of EssentialsX Discord's primary channel (the rest of the description is kept). The bot needs the **Manage Channels** permission on that channel. Discord allows only 2 description changes per 10 minutes, so very quick restarts may wait or skip one (the log says so).

**A link that stays the same (paid playit plan):** playit's TCP tunnels need playit Premium. With it, add a **TCP** tunnel at playit.gg pointing to `127.0.0.1` and the port shown on the card (BlueMap here uses 25450), and paste its address into **playit address (paid plan)**. The card then shows the `http://` link friends open, and you can switch the Cloudflare link off.

## Safe mode

Use safe mode when the server crashes or won't start after you add a plugin or datapack:

- The world loads with the **vanilla datapack only** (Minecraft's built-in `--safeMode`).
- On Paper, **no plugins are loaded**. Your `plugins\` folder stays as it is; the server just reads from an empty folder.
- You're asked whether to **back up your worlds** first, because anything that came from a datapack can be lost if the world is saved in safe mode.
- You're asked whether to open the tunnel, so you can keep friends out while you fix things.

## Notes

- Players must use the **same Minecraft version** as the server.
- Paper servers take Bukkit/Paper plugins. Drop the `.jar` files on the window, or put them in the server's `plugins` folder.
- To play on your home network only, switch off **Let friends join**.
- Java is detected automatically. If a new enough version isn't installed, the app offers to install Eclipse Temurin with `winget`.
- Your PC must stay on, with the app running, for others to play.
- **Building from source:** see the README.

## Credits

- **NVorbis** (MIT License, Copyright (c) 2020 Andrew Ward) decodes Minecraft's `.ogg` sounds for the creeper easter egg.
- **Minecraft's sounds and particle textures** are never included in this app. The easter egg reads them from a Minecraft installation already on the PC (official launcher, Modrinth App, Prism or CurseForge). Without one, it uses its own generated sound and smoke.
