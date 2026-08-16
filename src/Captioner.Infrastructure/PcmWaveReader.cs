using System.Buffers.Binary;
using System.Text;

namespace Captioner.Infrastructure;

internal sealed record PcmWaveData(int SampleRate, float[] Samples)
{
    public static PcmWaveData Read(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF")
        {
            throw new InvalidDataException("Local ASR input is not a RIFF WAV file.");
        }

        _ = reader.ReadUInt32();
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE")
        {
            throw new InvalidDataException("Local ASR input is not a WAVE file.");
        }

        ushort format = 0;
        ushort channels = 0;
        var sampleRate = 0;
        ushort bitsPerSample = 0;
        byte[]? pcm = null;
        while (stream.Position + 8 <= stream.Length)
        {
            var chunkId = Encoding.ASCII.GetString(reader.ReadBytes(4));
            var chunkSize = reader.ReadUInt32();
            if (chunkSize > int.MaxValue || stream.Position + chunkSize > stream.Length)
            {
                throw new InvalidDataException("WAV chunk size is invalid.");
            }

            if (chunkId == "fmt ")
            {
                var bytes = reader.ReadBytes((int)chunkSize);
                if (bytes.Length < 16)
                {
                    throw new InvalidDataException("WAV format chunk is incomplete.");
                }

                format = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4));
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(14));
            }
            else if (chunkId == "data")
            {
                pcm = reader.ReadBytes((int)chunkSize);
            }
            else
            {
                stream.Seek(chunkSize, SeekOrigin.Current);
            }

            if ((chunkSize & 1) != 0 && stream.Position < stream.Length)
            {
                stream.Seek(1, SeekOrigin.Current);
            }
        }

        if (format != 1 || channels != 1 || bitsPerSample != 16 || sampleRate <= 0 || pcm is null)
        {
            throw new InvalidDataException("Local ASR requires mono 16-bit PCM WAV audio.");
        }

        var samples = new float[pcm.Length / 2];
        for (var index = 0; index < samples.Length; index++)
        {
            samples[index] = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(index * 2, 2)) / 32768f;
        }

        return new(sampleRate, samples);
    }
}
