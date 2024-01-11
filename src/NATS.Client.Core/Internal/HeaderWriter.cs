using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using NATS.Client.Core.Commands;

namespace NATS.Client.Core.Internal;

internal class HeaderWriter
{
    private const byte ByteCr = (byte)'\r';
    private const byte ByteLf = (byte)'\n';
    private const byte ByteColon = (byte)':';
    private const byte ByteSpace = (byte)' ';
    private const byte ByteDel = 127;
    private readonly PipeWriter _pipeWriter;
    private readonly Encoding _encoding;

    public HeaderWriter(PipeWriter pipeWriter, Encoding encoding)
    {
        _pipeWriter = pipeWriter;
        _encoding = encoding;
    }

    private static ReadOnlySpan<byte> CrLf => new[] { ByteCr, ByteLf };

    private static ReadOnlySpan<byte> ColonSpace => new[] { ByteColon, ByteSpace };

    internal long Write(NatsHeaders headers)
    {
        var initialCount = _pipeWriter.UnflushedBytes;
        _pipeWriter.WriteSpan(CommandConstants.NatsHeaders10NewLine);

        foreach (var kv in headers)
        {
            foreach (var value in kv.Value)
            {
                if (value != null)
                {
                    // write key
                    var keyLength = _encoding.GetByteCount(kv.Key);
                    var keySpan = _pipeWriter.GetSpan(keyLength);
                    _encoding.GetBytes(kv.Key, keySpan);
                    if (!ValidateKey(keySpan.Slice(0, keyLength)))
                    {
                        throw new NatsException(
                            $"Invalid header key '{kv.Key}': contains colon, space, or other non-printable ASCII characters");
                    }

                    _pipeWriter.Advance(keyLength);
                    _pipeWriter.Write(ColonSpace);

                    // write values
                    var valueLength = _encoding.GetByteCount(value);
                    var valueSpan = _pipeWriter.GetSpan(valueLength);
                    _encoding.GetBytes(value, valueSpan);
                    if (!ValidateValue(valueSpan.Slice(0, valueLength)))
                    {
                        throw new NatsException($"Invalid header value for key '{kv.Key}': contains CRLF");
                    }

                    _pipeWriter.Advance(valueLength);
                    _pipeWriter.Write(CrLf);
                }
            }
        }

        // Even empty header needs to terminate.
        // We will send NATS/1.0 version line
        // even if there are no headers.
        _pipeWriter.Write(CrLf);

        return _pipeWriter.UnflushedBytes - initialCount;
    }

    // cannot contain ASCII Bytes <=32, 58, or 127
    private static bool ValidateKey(ReadOnlySpan<byte> span)
    {
        foreach (var b in span)
        {
            if (b <= ByteSpace || b == ByteColon || b >= ByteDel)
            {
                return false;
            }
        }

        return true;
    }

    // cannot contain CRLF
    private static bool ValidateValue(ReadOnlySpan<byte> span)
    {
        while (true)
        {
            var pos = span.IndexOf(ByteCr);
            if (pos == -1 || pos == span.Length - 1)
            {
                return true;
            }

            pos += 1;
            if (span[pos] == ByteLf)
            {
                return false;
            }

            span = span[pos..];
        }
    }
}
