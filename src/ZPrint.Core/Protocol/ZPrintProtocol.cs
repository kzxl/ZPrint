using System;
using System.IO;
using System.Text;
using ZeroNetwork.Common;
using ZPrint.Core.Models;

namespace ZPrint.Core.Protocol
{
    /// <summary>
    /// Packet types supported across ZPrint network nodes.
    /// </summary>
    public enum ZPrintPacketType : byte
    {
        DiscoverPing = 0x01,
        DiscoverPong = 0x02,
        SubmitJobRequest = 0x03,
        SubmitJobResponse = 0x04,
        JobStatusQuery = 0x05,
        JobStatusUpdate = 0x06,
        CancelJobRequest = 0x07,
        CancelJobResponse = 0x08
    }

    /// <summary>
    /// Ultra-fast binary packet framing protocol for ZPrint, incorporating <see cref="ZeroLz4"/> payload compression.
    /// Format:
    /// [4B Magic: 'ZPRT'] [1B Version: 1] [1B PacketType] [1B Flags] [2B MetaLen BE] [4B PayloadLen BE] [MetaBytes] [PayloadBytes]
    /// </summary>
    public static class ZPrintProtocol
    {
        public static readonly byte[] Magic = new byte[] { (byte)'Z', (byte)'P', (byte)'R', (byte)'T' };
        public const byte CurrentVersion = 1;
        public const byte FlagLz4Compressed = 0x01;

        /// <summary>
        /// Encodes a ZPrint packet into a raw byte buffer.
        /// </summary>
        public static byte[] EncodePacket(ZPrintPacketType packetType, string metadataJson, ReadOnlySpan<byte> rawPayload, bool compressPayload = true)
        {
            byte[] metaBytes = string.IsNullOrEmpty(metadataJson) ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(metadataJson);
            if (metaBytes.Length > 65535)
                throw new ArgumentException("Metadata JSON exceeds 65535 byte limit.", nameof(metadataJson));

            byte flags = 0;
            byte[] payloadBytes;

            if (compressPayload && rawPayload.Length > 64)
            {
                payloadBytes = ZeroLz4.CompressFramed(rawPayload);
                flags |= FlagLz4Compressed;
            }
            else
            {
                payloadBytes = rawPayload.ToArray();
            }

            int totalLen = 4 + 1 + 1 + 1 + 2 + 4 + metaBytes.Length + payloadBytes.Length;
            byte[] buffer = new byte[totalLen];
            int offset = 0;

            // Header
            Magic.CopyTo(buffer, offset); offset += 4;
            buffer[offset++] = CurrentVersion;
            buffer[offset++] = (byte)packetType;
            buffer[offset++] = flags;

            // Metadata Length (UInt16 BE)
            buffer[offset++] = (byte)(metaBytes.Length >> 8);
            buffer[offset++] = (byte)metaBytes.Length;

            // Payload Length (Int32 BE)
            buffer[offset++] = (byte)(payloadBytes.Length >> 24);
            buffer[offset++] = (byte)(payloadBytes.Length >> 16);
            buffer[offset++] = (byte)(payloadBytes.Length >> 8);
            buffer[offset++] = (byte)payloadBytes.Length;

            // Metadata payload
            if (metaBytes.Length > 0)
            {
                metaBytes.CopyTo(buffer, offset);
                offset += metaBytes.Length;
            }

            // Binary payload
            if (payloadBytes.Length > 0)
            {
                payloadBytes.CopyTo(buffer, offset);
                offset += payloadBytes.Length;
            }

            return buffer;
        }

        /// <summary>
        /// Decodes a ZPrint packet from a source byte span, throwing an exception if invalid.
        /// </summary>
        public static (ZPrintPacketType Type, string Metadata, byte[] Payload) DecodePacket(ReadOnlySpan<byte> source)
        {
            if (TryDecodePacket(source, out var type, out var meta, out var payload, out _))
            {
                return (type, meta, payload);
            }
            throw new InvalidDataException("Incomplete or truncated ZPrint packet buffer.");
        }

        /// <summary>
        /// Decodes a ZPrint packet from a source byte span.
        /// </summary>
        public static bool TryDecodePacket(
            ReadOnlySpan<byte> source,
            out ZPrintPacketType packetType,
            out string metadataJson,
            out byte[] uncompressedPayload,
            out int bytesConsumed)
        {
            packetType = default;
            metadataJson = string.Empty;
            uncompressedPayload = Array.Empty<byte>();
            bytesConsumed = 0;

            const int minHeaderLen = 4 + 1 + 1 + 1 + 2 + 4;
            if (source.Length < minHeaderLen) return false;

            // Validate magic
            if (source[0] != Magic[0] || source[1] != Magic[1] || source[2] != Magic[2] || source[3] != Magic[3])
                throw new InvalidDataException("Invalid ZPrint magic header.");

            byte version = source[4];
            if (version != CurrentVersion)
                throw new InvalidDataException($"Unsupported ZPrint protocol version {version}.");

            packetType = (ZPrintPacketType)source[5];
            byte flags = source[6];

            ushort metaLen = (ushort)((source[7] << 8) | source[8]);
            int payloadLen = (source[9] << 24) | (source[10] << 16) | (source[11] << 8) | source[12];

            if (payloadLen < 0 || payloadLen > 1024 * 1024 * 512) // 512MB safety cap
                throw new InvalidDataException("Invalid payload length.");

            int totalRequired = minHeaderLen + metaLen + payloadLen;
            if (source.Length < totalRequired) return false;

            int offset = minHeaderLen;

            // Read metadata
            if (metaLen > 0)
            {
#if NET8_0_OR_GREATER || NETSTANDARD2_1_OR_GREATER
                metadataJson = Encoding.UTF8.GetString(source.Slice(offset, metaLen));
#else
                metadataJson = Encoding.UTF8.GetString(source.Slice(offset, metaLen).ToArray());
#endif
                offset += metaLen;
            }

            // Read payload
            if (payloadLen > 0)
            {
                var payloadSlice = source.Slice(offset, payloadLen);
                if ((flags & FlagLz4Compressed) != 0)
                {
                    uncompressedPayload = ZeroLz4.DecompressFramed(payloadSlice);
                }
                else
                {
                    uncompressedPayload = payloadSlice.ToArray();
                }
            }

            bytesConsumed = totalRequired;
            return true;
        }
    }
}
