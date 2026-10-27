using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

public sealed record StoredFile(string Id, string Name, string Mime, long Size, long StoredSize,
    bool Compressed, DateTimeOffset CreatedAt, string Sha256);

public sealed class Storage
{
    public string Root { get; }
    public long MaxBytes { get; }
    public long QuotaBytes { get; }
    public string? UploadKey { get; }
    private readonly SemaphoreSlim gate = new(1, 1);
    private long usedBytes;
    public Storage(IConfiguration config)
    {
        Root = Path.GetFullPath(config["Storage:Path"] ?? "data");
        MaxBytes = config.GetValue<long?>("Storage:MaxFileBytes") ?? 104857600;
        QuotaBytes = config.GetValue<long?>("Storage:QuotaBytes") ?? 10737418240;
        UploadKey = config["Storage:UploadKey"];
        if (MaxBytes <= 0 || QuotaBytes < MaxBytes) throw new InvalidOperationException("Invalid storage limits.");
        Directory.CreateDirectory(Root);
        foreach (var path in Directory.EnumerateFiles(Root, "*.tmp")) File.Delete(path);
        // A metadata file is the commit marker. Remove blobs left by an interrupted commit.
        foreach (var path in Directory.EnumerateFiles(Root, "*.blob"))
            if (!File.Exists(Path.ChangeExtension(path, ".json"))) File.Delete(path);
        usedBytes = Directory.EnumerateFiles(Root).Sum(p => new FileInfo(p).Length);
    }
    public static bool ValidId(string id) => Regex.IsMatch(id, "^[a-f0-9]{32}$", RegexOptions.CultureInvariant);
    public string BlobPath(string id) => Path.Combine(Root, id + ".blob");
    public async Task<StoredFile?> Find(string id, CancellationToken ct)
    {
        if (!ValidId(id)) return null;
        var path = Path.Combine(Root, id + ".json");
        if (!File.Exists(path)) return null;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<StoredFile>(stream, cancellationToken: ct);
    }
    public bool Authorized(string supplied)
    {
        if (string.IsNullOrEmpty(UploadKey)) return true;
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(supplied)),
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(UploadKey)));
    }
    public async Task<StoredFile> Save(Stream input, string name, CancellationToken ct)
    {
        if (!await gate.WaitAsync(0, ct)) throw new UploadException(503, "Another upload is processing. Please try again shortly.");
        string id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        string raw = Path.Combine(Root, id + ".raw.tmp"), zip = Path.Combine(Root, id + ".gzip.tmp");
        string json = Path.Combine(Root, id + ".json"), jsonTemp = json + ".tmp";
        bool committed = false;
        try
        {
            // Reserve worst-case disk space: original + compression scratch + metadata.
            if (usedBytes + 2 * MaxBytes + 65536 > QuotaBytes)
                throw new UploadException(507, "Storage is full. Contact the host administrator.");
            long size = 0;
            byte[] header = new byte[16];
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(raw, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                byte[] buffer = new byte[65536];
                int count;
                while ((count = await input.ReadAsync(buffer, ct)) != 0)
                {
                    if (size + count > MaxBytes) throw new UploadException(413, "This file exceeds the upload limit.");
                    if (size < header.Length) Array.Copy(buffer, 0, header, (int)size, (int)Math.Min(count, header.Length - size));
                    size += count;
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), ct);
                }
            }
            if (size == 0) throw new UploadException(400, "Choose a file that is not empty.");
            await using (var source = File.OpenRead(raw))
            await using (var target = File.Create(zip))
            await using (var compressor = new GZipStream(target, CompressionLevel.SmallestSize))
                await source.CopyToAsync(compressor, ct);
            long zippedSize = new FileInfo(zip).Length;
            bool compressed = zippedSize < size;
            var file = new StoredFile(id, CleanName(name), DetectMime(header), size, compressed ? zippedSize : size,
                compressed, DateTimeOffset.UtcNow, Convert.ToHexStringLower(hash.GetHashAndReset()));
            File.Move(compressed ? zip : raw, BlobPath(id));
            await File.WriteAllTextAsync(jsonTemp, JsonSerializer.Serialize(file), ct);
            File.Move(jsonTemp, json);
            committed = true;
            usedBytes += file.StoredSize + new FileInfo(json).Length;
            return file;
        }
        finally
        {
            foreach (var p in new[] { raw, zip, jsonTemp }) if (File.Exists(p)) File.Delete(p);
            if (!committed && File.Exists(BlobPath(id))) File.Delete(BlobPath(id));
            gate.Release();
        }
    }
    public Stream Open(StoredFile file)
    {
        var source = new FileStream(BlobPath(file.Id), FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        return file.Compressed ? new GZipStream(source, CompressionMode.Decompress) : source;
    }
    private static string CleanName(string value)
    {
        string name = Path.GetFileName(value.Replace('\\', '/'));
        name = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (name.Length > 180) name = name[..180];
        return string.IsNullOrWhiteSpace(name) || name is "." or ".." ? "file" : name;
    }
    private static string DetectMime(byte[] h)
    {
        if (h.AsSpan(0, 8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10})) return "image/png";
        if (h[0] == 255 && h[1] == 216 && h[2] == 255) return "image/jpeg";
        var s = System.Text.Encoding.ASCII.GetString(h);
        if (s.StartsWith("GIF87a") || s.StartsWith("GIF89a")) return "image/gif";
        if (s.StartsWith("RIFF") && s.Substring(8, 4) == "WEBP") return "image/webp";
        return "application/octet-stream";
    }
}
public sealed class UploadException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}
