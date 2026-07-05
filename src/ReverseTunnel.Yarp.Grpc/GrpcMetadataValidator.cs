using Grpc.Core;

namespace ReverseTunnel.Yarp.Grpc;

internal static class GrpcMetadataValidator
{
    public static Metadata CreateRequestMetadata(IEnumerable<KeyValuePair<string, string>> headers)
    {
        var metadata = new Metadata();
        foreach (var header in headers)
        {
            AddValidated(metadata, header.Key, header.Value);
        }

        return metadata;
    }

    private static void AddValidated(Metadata metadata, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException("gRPC metadata keys must not be empty.");
        }

        if (key[0] == ':')
        {
            throw new InvalidOperationException($"gRPC metadata key '{key}' is reserved for HTTP/2 pseudo-headers.");
        }

        if (!IsAscii(key))
        {
            throw new InvalidOperationException($"gRPC metadata key '{key}' must be ASCII.");
        }

        var normalizedKey = key.ToLowerInvariant();
        if (normalizedKey.StartsWith("grpc-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"gRPC metadata key '{key}' uses the reserved 'grpc-' prefix.");
        }

        foreach (var c in normalizedKey)
        {
            if (!IsValidKeyCharacter(c))
            {
                throw new InvalidOperationException($"gRPC metadata key '{key}' contains invalid character '{c}'. Use ASCII letters, digits, '.', '_' or '-'.");
            }
        }

        value ??= string.Empty;
        if (normalizedKey.EndsWith("-bin", StringComparison.Ordinal))
        {
            if (!IsAscii(value))
            {
                throw new InvalidOperationException($"Binary gRPC metadata value for key '{key}' must be base64 ASCII.");
            }

            try
            {
                metadata.Add(normalizedKey, Convert.FromBase64String(value));
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException($"Binary gRPC metadata value for key '{key}' must be valid base64.", ex);
            }

            return;
        }

        if (!IsAscii(value) || value.Any(static c => c is '\r' or '\n'))
        {
            throw new InvalidOperationException($"gRPC metadata value for key '{key}' must be ASCII and must not contain CR/LF.");
        }

        metadata.Add(normalizedKey, value);
    }

    private static bool IsAscii(string value) => value.All(static c => c <= 0x7f);

    private static bool IsValidKeyCharacter(char c) =>
        c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-';
}