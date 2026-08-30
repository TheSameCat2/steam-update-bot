# Steam Update Bot

Patch notes, in Discord, when they actually drop.

This is a single-tenant homelab bot for one server. Point it at the games you play, and it posts official Steam community announcements into a channel you choose — patch notes, hotfixes, roadmaps, and yes, the occasional publisher trailer. It reads Steam's public news feed, so you do not need a Steam API key.

It does **not** watch silent depot or build bumps. If the game updates and nobody writes an announcement, the bot stays quiet. That is by design.

## Discord setup

1. Create an application and bot in the [Discord Developer Portal](https://discord.com/developers/applications).
2. Install it into the target server with the `bot` and `applications.commands` scopes.
3. Grant the bot **View Channel**, **Send Messages**, and **Embed Links** in the announcement channel. It does not need Administrator, Message Content, Guild Members, or Read Message History.
4. Enable Discord Developer Mode, then copy the server, announcement-channel, and manager-role IDs.
5. Copy `.env.example` to `.env` and enter the bot token and IDs.

The manager role can add and remove games. Discord Administrators have a recovery bypass. Anyone in the server can list games and check status. All command replies are ephemeral, so the announcement channel stays clean.

## Run with Docker Compose

CI publishes `ghcr.io/thesamecat2/steam-update-bot` from `main` (`latest` and `sha-<commit>`). The first package created by GHCR is private; set it public under the repo's Packages settings if you want unauthenticated pulls.

```sh
git clone https://github.com/TheSameCat2/steam-update-bot.git
cd steam-update-bot
cp .env.example .env
# edit .env
docker compose pull
docker compose up -d
docker compose logs -f steam-update-bot
```

`docker compose up --build` still builds from the local checkout if you are changing the image.

Find a game's App ID on its Steam store page (the number in `/app/570/` and similar URLs), then `/steam add` it. The bot silently baselines whatever is already on the feed so you are not flooded with last month's notes. After that, new posts show up in the channel.

## Recommended setup on Unraid

Unraid does not ship `docker compose`. Use Portainer, Compose Manager Plus, or the Compose CLI plugin. Deploy the published image; you do not need a source checkout or a local build. If your stack UI tries to build because `compose.yaml` still has a `build:` key, delete that key. Pulling `ghcr.io/thesamecat2/steam-update-bot` is enough.

### 1. Data directory

```sh
mkdir -p /mnt/user/appdata/steam-update-bot/data
chown -R 1654:1654 /mnt/user/appdata/steam-update-bot/data
```

The `chown` is the step people miss. The image runs as uid 1654, and a bind mount keeps the host directory's ownership rather than the image's, so without it SQLite cannot create the database and the container restarts in a loop. Keep this path in appdata so the Appdata Backup plugin covers the database. Named volumes live on the Docker vDisk, which that plugin does not back up and which is destroyed when the vDisk is recreated.

### 2. Configure

Copy `compose.yaml` and `.env.example` into the stack (Portainer web editor is fine), then set the Discord token, the three IDs, and the data path:

```ini
BOT_DATA_PATH=/mnt/user/appdata/steam-update-bot/data
```

`.env` holds your bot token in plain text. `chmod 600` if it lives on disk, and do not export the appdata share over SMB or NFS.

### 3. Start it

```sh
docker compose pull
docker compose up -d
docker compose logs -f steam-update-bot
```

Wait for `Discord gateway ready for guild` in the logs. The bot registers its slash commands at that point, so `/steam` will not appear in Discord until you see it.

### 4. Verify

```sh
docker compose ps
curl -s http://127.0.0.1:8080/health/ready
```

A fresh install reports `Healthy`: with no games monitored yet there is nothing to poll, so only the database and gateway are checked. Add a game with `/steam add`, and its first poll runs within one poll interval (five minutes by default). Readiness only reports stale polling if a monitored game has gone three intervals without a successful poll.

### Updating

Pull `latest` (or pin `IMAGE_TAG` to a `sha-*` tag) and recreate the stack. Do not `git pull` and rebuild unless you are running a local checkout on purpose.

### Backups

With `BOT_DATA_PATH` under appdata, the Appdata Backup plugin picks the database up automatically. The bot runs SQLite in WAL mode, so stop the container before copying by hand — otherwise copy `steam-update-bot.db`, `-wal`, and `-shm` together, or you will restore a torn database.

### Notes

Port 8080 is published on `127.0.0.1` only, so nothing is reachable from your LAN. If another container already holds host port 8080, change the left-hand side of the `ports` mapping in `compose.yaml`; the container port stays 8080.

The bot ignores `TZ`. `/steam status` always reports UTC, while announcement embeds carry a real timestamp that Discord renders in each viewer's local time.

## Where the database lives

By default the SQLite database lives in the `steam-update-bot-data` named Docker volume. Back it up by stopping the container, then copying the volume with your usual Docker-volume workflow.

The Compose project name is pinned to `steam-update-bot`, so the volume name does not change if you rename or re-clone the checkout directory. Do not remove the top-level `name:` key — without it Compose derives the project name from the folder, and a rename silently starts the bot against a new empty database.

To use a host directory instead, set `BOT_DATA_PATH` in `.env` to an absolute path and it becomes a bind mount. This is the better choice on Unraid, because named volumes live on the Docker vDisk, which the Appdata Backup plugin does not cover and which is destroyed when the vDisk is recreated. See [Recommended setup on Unraid](#recommended-setup-on-unraid) for the full walkthrough.

The image runs as the aspnet `$APP_UID` user (1654). A bind mount keeps the host directory's ownership rather than the image's, so `chown -R 1654:1654` the directory before the first start. The same applies to an existing named volume created under a different uid.

## Delivery guarantees

Announcement delivery is at-least-once. If the process dies in the narrow window between Discord accepting a message and the bot recording its message ID, that announcement is posted again on restart. Duplicates are possible; missed announcements are not.

A publish failure that looks transient (rate limits, 5xx, timeouts) retries indefinitely without counting toward the five-attempt poison budget. To keep an indefinitely stuck outbox from hiding, `/steam status` reports the oldest undelivered announcement, and `/health/ready` fails once anything has been undelivered for more than an hour.

Health endpoints are bound to localhost only (`127.0.0.1:8080`). Both return compact JSON (`status`, `description`, and check `data`):

- `http://127.0.0.1:8080/health/live` — process liveness. Docker HEALTHCHECK uses this for `unhealthy` visibility. It stays healthy through brief Discord reconnects. After 10 minutes disconnected, the process exits so Compose `restart: unless-stopped` recycles it. Compose does not restart a still-running unhealthy container on its own.
- `http://127.0.0.1:8080/health/ready` — operational readiness (database, Discord, stale Steam polls, outbox drain). A retrying delivery is degraded; an outbox that has not drained for over an hour is a hard failure. Use this for humans, `docker inspect`, or `/steam status`, not as the container restart probe.

## Commands

| Command | Access | Behavior |
| --- | --- | --- |
| `/steam add app-id:<number>` | Manager or Administrator | Validates the Steam game, stores its current announcements as a silent baseline, and starts watching for new ones. |
| `/steam remove app-id:<number>` | Manager or Administrator | Stops monitoring and drops pending deliveries for that game. |
| `/steam list page:<number>` | Server members | Lists monitored games, 20 per page. |
| `/steam status app-id:<optional number>` | Server members | Shows bot or game health, including a truncated last error. It does not expose the bot token. |

## Local development

```sh
dotnet restore SteamUpdateBot.sln
dotnet build SteamUpdateBot.sln --no-restore
dotnet test SteamUpdateBot.sln --no-build
dotnet run --project src/SteamUpdateBot.App
```

Use environment variables or .NET user secrets for local Discord credentials. Never put the token in `appsettings.json`.
