# Couch Party sync server

A tiny server that lets several PCs share Couch Party profiles, stats and leaderboards.
It only stores the data and checks keys; the mod does all the merging, and every PC keeps
its own copy so the mod still works offline.

- One Python file, standard library only, runs in Docker.
- One key per PC. Remove a key to cut that PC off.
- Uploads based on an old version are refused, so two PCs syncing at once can't overwrite each other.
- The last 100 versions are kept in `data/history/`.

## Setup (Docker + Apache)

The examples use `https://your-domain.example/couchparty`. Replace it with your own address.

1. Copy this `server` folder to the server and start the container:

   ```bash
   mkdir -p data && sudo chown 1000:1000 data
   docker compose up -d --build
   ```

2. Create a key for each PC (the key is printed once):

   ```bash
   docker compose exec couchparty python /app/server.py addkey my-pc
   docker compose exec couchparty python /app/server.py addkey friends-pc
   ```

3. Forward HTTPS traffic from Apache. In the `<VirtualHost *:443>` block of your site, add:

   ```apache
   ProxyPass        /couchparty/ http://127.0.0.1:8787/
   ProxyPassReverse /couchparty/ http://127.0.0.1:8787/
   ```

   Then enable the proxy modules (once) and reload Apache:

   ```bash
   sudo a2enmod proxy proxy_http
   sudo systemctl reload apache2
   ```

4. Check that it answers: open `https://your-domain.example/couchparty/api/health`. It should say `{"ok":true}`.

5. In the game, press **F8 → Sync**, enter the address (`https://your-domain.example/couchparty`) and the PC's key,
   then press **Save & sync**.

Always use HTTPS. Over plain HTTP, anyone on the same network could read the key.

## Managing keys

```bash
docker compose exec couchparty python /app/server.py listkeys
docker compose exec couchparty python /app/server.py removekey friends-pc
```

Changes take effect immediately; no restart needed.

## Files

Everything lives in `data/` (not in git):

| File | What it is |
|---|---|
| `couchparty.json` | The current synced data |
| `revision.txt` | Version number of the current data |
| `history/rev-*.json` | The last 100 versions. Copy one over `couchparty.json` (and bump `revision.txt`) to roll back. |
| `keys.json` | PC names and their keys |

## Settings

Environment variables (set them in `docker-compose.yml` under `environment:`):

| Variable | Default | Meaning |
|---|---|---|
| `HISTORY` | 100 | How many old versions to keep |
| `MAX_BODY` | 20 MB | Largest accepted upload |
| `PORT` | 8787 | Port inside the container |
