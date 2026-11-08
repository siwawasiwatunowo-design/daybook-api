using Amazon.S3;
using Amazon.S3.Model;
using Daybook.Backend.Application;
using Daybook.Backend.Options;
using Microsoft.Extensions.Options;

namespace Daybook.Backend.Infrastructure;

// Neon Object Storage is S3-compatible. Credentials are branch-scoped Neon storage credentials.
public sealed class NeonS3RecordingObjectStore : IRecordingObjectStore, IRecordingUploadUrlProvider
{
    private readonly IAmazonS3 _s3;
    private readonly StorageOptions _options;

    public NeonS3RecordingObjectStore(IAmazonS3 s3, IOptions<StorageOptions> options)
    {
        _s3 = s3;
        _options = options.Value;
        if (string.IsNullOrWhiteSpace(_options.Endpoint) || string.IsNullOrWhiteSpace(_options.Bucket) ||
            string.IsNullOrWhiteSpace(_options.AccessKey) || string.IsNullOrWhiteSpace(_options.SecretKey))
            throw new InvalidOperationException("Storage endpoint, bucket, access key and secret key must be configured.");
    }

    public async Task DownloadToFileAsync(string objectName, string destinationPath, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        using var response = await _s3.GetObjectAsync(_options.Bucket, objectName, ct);
        await using var output = File.Create(destinationPath);
        await response.ResponseStream.CopyToAsync(output, ct);
    }

    public Task<RecordingUploadUrl> CreateUploadUrlAsync(string userId, string clientRecordingId,
        string contentType, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Guid.TryParse(clientRecordingId, out var recordingId))
            throw new ArgumentException("clientRecordingId must be a GUID.", nameof(clientRecordingId));
        var allowedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "audio/wav", "audio/x-wav", "audio/mpeg", "audio/mp4", "audio/aac", "audio/flac", "audio/ogg" };
        if (!allowedTypes.Contains(contentType))
            throw new ArgumentException("Unsupported audio content type.", nameof(contentType));

        var key = $"users/{userId}/recordings/{recordingId:D}/original";
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(15);
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket,
            Key = key,
            Verb = HttpVerb.PUT,
            Expires = expiresAt.UtcDateTime,
            ContentType = contentType
        };
        var url = _s3.GetPreSignedURL(request);
        return Task.FromResult(new RecordingUploadUrl(url, key, expiresAt, contentType));
    }
}
