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

```sh
cp .env.example .env
# edit .env
docker compose up --build -d
docker compose logs -f steam-update-bot
```

Find a game's App ID on its Steam store page (the number in `/app/570/` and similar URLs), then `/steam add` it. The bot silently baselines whatever is already on the feed so you are not flooded with last month's notes. After that, new posts show up in the channel.

The SQLite database lives in the `steam-update-bot-data` named Docker volume. Back it up by stopping the container, then copying the volume with your usual Docker-volume workflow.

The image runs as the aspnet `$APP_UID` user (1654). If an existing volume was created as a different uid and the container cannot write `/data`, chown the volume contents to 1654 before starting again.

Health endpoints are bound to localhost only (`127.0.0.1:8080`). Both return compact JSON (`status`, `description`, and check `data`):

- `http://127.0.0.1:8080/health/live` — process liveness. Docker HEALTHCHECK uses this for `unhealthy` visibility. It stays healthy through brief Discord reconnects. After 10 minutes disconnected, the process exits so Compose `restart: unless-stopped` recycles it. Compose does not restart a still-running unhealthy container on its own.
- `http://127.0.0.1:8080/health/ready` — operational readiness (database, Discord, stale Steam polls). A retrying delivery is degraded, not a hard failure. Use this for humans, `docker inspect`, or `/steam status`, not as the container restart probe.

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
