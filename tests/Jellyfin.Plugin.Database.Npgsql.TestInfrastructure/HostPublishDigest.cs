using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.Database.Npgsql.TestInfrastructure;

/// <summary>Writes the canonical, unambiguous host-publish digest framing.</summary>
internal sealed class HostPublishDigest : IDisposable
{
    private const string Domain = "Jellyfin.Plugin.Database.Npgsql/host-publish";
    private const uint FormatVersion = 1;
    private const byte RegularFileEntry = 1;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly ulong _expectedEntries;
    private ulong _writtenEntries;
    private ulong _remainingFileBytes;
    private bool _completed;

    public HostPublishDigest(int entryCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(entryCount);
        _expectedEntries = checked((ulong)entryCount);
        AppendLengthPrefixedUtf8(Domain);
        AppendUInt32(FormatVersion);
        AppendUInt64(_expectedEntries);
    }

    public void BeginFile(string relativePath, long contentLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(contentLength);
        EnsureEntryCanStart();
        AppendEntryHeader(RegularFileEntry, relativePath, checked((ulong)contentLength));
        _remainingFileBytes = checked((ulong)contentLength);
    }

    public void AppendFileContent(ReadOnlySpan<byte> content)
    {
        EnsureNotCompleted();
        if ((ulong)content.Length > _remainingFileBytes)
        {
            throw new InvalidOperationException("Host-publish digest content exceeds its framed file length.");
        }

        _hash.AppendData(content);
        _remainingFileBytes -= (ulong)content.Length;
    }

    public string Complete()
    {
        EnsureNotCompleted();
        if (_remainingFileBytes != 0 || _writtenEntries != _expectedEntries)
        {
            throw new InvalidOperationException("Host-publish digest framing is incomplete.");
        }

        _completed = true;
        return Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();
    }

    public void Dispose() => _hash.Dispose();

    private void AppendEntryHeader(byte entryType, string relativePath, ulong contentLength)
    {
        _hash.AppendData([entryType]);
        AppendLengthPrefixedUtf8(relativePath);
        AppendUInt64(contentLength);
        _writtenEntries++;
    }

    private void AppendLengthPrefixedUtf8(string value)
    {
        var bytes = StrictUtf8.GetBytes(value);
        AppendUInt32(checked((uint)bytes.Length));
        _hash.AppendData(bytes);
    }

    private void AppendUInt32(uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        _hash.AppendData(bytes);
    }

    private void AppendUInt64(ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);
        _hash.AppendData(bytes);
    }

    private void EnsureEntryCanStart()
    {
        EnsureNotCompleted();
        if (_remainingFileBytes != 0)
        {
            throw new InvalidOperationException("Host-publish digest file content is incomplete.");
        }

        if (_writtenEntries >= _expectedEntries)
        {
            throw new InvalidOperationException("Host-publish digest contains more entries than its framed entry count.");
        }
    }

    private void EnsureNotCompleted()
    {
        if (_completed)
        {
            throw new InvalidOperationException("Host-publish digest framing is already complete.");
        }
    }
}
