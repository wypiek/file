# FileHarbor

A small .NET 10 file host. Drag in one file, upload it, and copy the link. Regular responsive website layout, with vendored 7.css 0.21.1 controls and no window/desktop UI. No database, JavaScript packages, or external storage services.

## Start with Docker Compose

Requires Docker Engine with Compose v2 and BuildKit. From this directory:

```sh
cp .env.example .env
docker compose up -d --build
```

Open **http://localhost:8080**. The first build compiles Caddy from pinned Go module source; it can take several minutes. **Compose never pulls a Caddy image**: there is no `image: caddy` or `FROM caddy`. It does download the .NET, Go, and Alpine base images and Go modules needed to build.

### Public server with automatic HTTPS

Point a domain's A/AAAA records at your server. In `.env`, set:

```dotenv
PUBLIC_URL=https://files.example.com
SITE_ADDRESS=files.example.com
HTTP_PORT=80
HTTPS_PORT=443
```

Allow inbound TCP 80/443 (and optionally UDP 443 for HTTP/3), then run `docker compose up -d --build`. Caddy obtains and renews TLS certificates. Optionally set a certificate contact email in `deploy/Caddyfile`. Keep both Caddy volumes so certificates survive restarts. `PUBLIC_URL` must match the URL visitors use, including scheme and port; it controls generated links, Discord metadata, and browser upload-origin checks. Use an origin without a path. Never expose the app container directly on an internet-facing port.

To restrict uploads, set a long random `UPLOAD_KEY` in `.env` and recreate the services. The page prompts for the key; it is sent in a header, never a URL or stored in browser storage. Downloads and previews remain public to anyone who knows the link. For an internet-facing instance, an upload key is recommended unless you intend to accept anonymous uploads. There is no moderation service or malware scanner.

## Storage and compression

- Default per-file limit: **100 MiB**. Storage budget: **10 GiB**. Adjust `MAX_FILE_BYTES` and `STORAGE_QUOTA_BYTES`, then recreate services.
- Streams uploads to disk with bounded buffers; processes one upload at a time. Concurrent uploads receive a retryable 503. A global limit permits 30 upload requests per minute. No in-memory upload buffering or background job service.
- Gzip at `SmallestSize` is lossless. The smaller of the original and compressed data is retained. ZIPs, photos, and videos often cannot shrink further. No lossy media conversion or archive extraction.
- Downloads stream the exact original bytes under the original filename, with a SHA-256 checksum shown on the share page. Compression is internal: visitors do not receive a `.gz` archive.
- Opaque 128-bit random IDs, metadata JSON, and blobs live in the `files` volume. Metadata is committed last; links are returned only after processing and saving. Startup removes interrupted temporary files and orphan blobs.
- The budget includes metadata and reserves two maximum-size files plus 64 KiB for temporary compression work. Set quota above that reserve; leave actual host disk headroom too. The quota is a logical application limit, not a filesystem quota.
- Files remain until the administrator removes them. No automatic expiration, user accounts, listing, delete API, or deduplication. Keep one app instance per volume. Download range/resume is not supported because stored files may be compressed.

## Sharing and Discord

The result includes a direct download URL (`/d/id/filename`) and a share page (`/f/id`). **Paste the share page into Discord** for the embed. Share pages render Open Graph title, filename, original size, image, canonical URL, and theme color on the server, without requiring JavaScript or authentication.

PNG, JPEG, GIF, and WebP files recognized by their signatures get inline image previews up to 20 MiB. Other files use a bundled FileHarbor image. Arbitrary uploads, including HTML and SVG, are only served as attachments, never executable site content. Discord must be able to reach the host publicly over HTTPS. Discord may cache, suppress, or reject previews, including malformed images; rendering is ultimately controlled by Discord. Direct download URLs are for downloading; they are not embed pages. There are no video players or document renderers.

## Maintain

```sh
docker compose logs -f --tail=100
docker compose ps
docker compose up -d --build    # rebuild after source/base-image updates
docker compose down            # preserves files and TLS volumes
```

Do not use `down -v` unless you intend to erase every uploaded file and certificate. Back up the whole files volume, including JSON metadata and blobs. To remove a file, stop the app, delete its `<id>.json` and `<id>.blob` together from that volume, and restart. Stopping the app also ensures a consistent backup and recalculates quota usage after manual changes. Treat file links as bearer secrets, not private authenticated storage. Back up important originals separately.

Check app liveness at `/healthz`. It is a process check, not an exhaustive disk health test. Monitor host disk usage and logs. To update Caddy, change the `CADDY_VERSION` build argument default in `deploy/Caddy.Dockerfile`, rebuild, and test. Vendored 7.css is MIT licensed; its original license is in `wwwroot/vendor/7.css.LICENSE`.

## Develop and verify without Docker

Install the .NET 10 SDK:

```sh
dotnet run --urls http://localhost:8080
```

Local data goes in `./data`. Configuration uses normal .NET environment variables: `PublicUrl`, `Storage__Path`, `Storage__MaxFileBytes`, `Storage__QuotaBytes`, and `Storage__UploadKey`. The `.env` file is read by Compose, not by `dotnet run`.

```sh
dotnet build -c Release
python3 tests/integration.py
```

The integration check starts isolated app processes and checks compressed/uncompressed byte-for-byte downloads, restart persistence, upload limits, origin rejection, access keys, safe filenames, previews, and Open Graph output. Set `DOTNET=/path/to/dotnet` if it is not on PATH.
