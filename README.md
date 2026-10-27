# m5rcel’s awesome file hoster

A small .NET 10 file host. Drag in one file, upload it, and copy the link. Regular responsive website layout, with vendored 7.css 0.21.1 controls and no window/desktop UI. No database, JavaScript packages, or external storage services.

## Start with Docker Compose

Requires Docker Engine with Compose v2 and BuildKit. From this directory:

```sh
cp .env.example .env
docker compose up -d --build --remove-orphans
```

Open **http://localhost:6968**. The application listens directly on HTTP port **6968**, published as `6968:6968`. Compose runs **only the .NET app**; it does not pull, build, or run Caddy. The build downloads the .NET base images.

### VPS over HTTP

In `.env`, set the address visitors will use, including port 6968:

```dotenv
PUBLIC_URL=http://YOUR_VPS_IP:6968
```

You can also use `http://files.example.com:6968` after pointing its DNS records at the VPS. Allow inbound TCP **6968** in the VPS firewall. Run `docker compose up -d --build --remove-orphans`, then visit that exact URL. `PUBLIC_URL` controls generated download links, Discord metadata, and browser upload-origin checks. Update it if you previously used port 8080 or HTTPS. No domain or TLS certificate is required for this HTTP setup.

`--remove-orphans` also removes the previous Compose-managed Caddy container when upgrading. Uploaded files remain in the existing `files` volume. Old Caddy certificate volumes are not deleted automatically.

### Optional: separately managed Caddy

If you later want HTTPS, install or manage Caddy separately and adapt `deploy/Caddyfile` for your domain. It proxies to `127.0.0.1:6968`. Set `PUBLIC_URL=https://your-domain` and allow TCP 80/443 for that separate Caddy instance. Caddy is never included in this project's Compose configuration.

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

PNG, JPEG, GIF, and WebP files recognized by their signatures get inline image previews up to 20 MiB. Other files use a bundled m5rcel’s awesome file hoster image. Arbitrary uploads, including HTML and SVG, are only served as attachments, never executable site content. Discord must be able to reach the public share page and its preview image; localhost and private VPS addresses will not work. Discord may cache, suppress, or reject previews, including malformed images; rendering is ultimately controlled by Discord. Direct download URLs are for downloading; they are not embed pages. There are no video players or document renderers.

## Maintain

```sh
docker compose logs -f --tail=100
docker compose ps
docker compose up -d --build    # rebuild after source/base-image updates
docker compose down            # preserves uploaded files
```

Do not use `down -v` unless you intend to erase every uploaded file. Back up the whole files volume, including JSON metadata and blobs. To remove a file, stop the app, delete its `<id>.json` and `<id>.blob` together from that volume, and restart. Stopping the app also ensures a consistent backup and recalculates quota usage after manual changes. Treat file links as bearer secrets, not private authenticated storage. Back up important originals separately.

Check app liveness at `/healthz`. It is a process check, not an exhaustive disk health test. Monitor host disk usage and logs. Vendored 7.css is MIT licensed; its original license is in `wwwroot/vendor/7.css.LICENSE`.

## Develop and verify without Docker

Install the .NET 10 SDK:

```sh
dotnet run --urls http://localhost:6968
```

Local data goes in `./data`. Configuration uses normal .NET environment variables: `PublicUrl`, `Storage__Path`, `Storage__MaxFileBytes`, `Storage__QuotaBytes`, and `Storage__UploadKey`. The `.env` file is read by Compose, not by `dotnet run`.

```sh
dotnet build -c Release
python3 tests/integration.py
```

The integration check starts isolated app processes and checks compressed/uncompressed byte-for-byte downloads, restart persistence, upload limits, origin rejection, access keys, safe filenames, previews, and Open Graph output. Set `DOTNET=/path/to/dotnet` if it is not on PATH.
