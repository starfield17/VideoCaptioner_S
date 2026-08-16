using Captioner.Core;
using Captioner.Infrastructure;

namespace Captioner.Tests;

public sealed class SrtSubtitlePublisherTests
{
    [Fact]
    public async Task Publish_writes_valid_bilingual_srt_and_never_silently_overwrites()
    {
        var directory = Path.Combine(Path.GetTempPath(), "captioner-srt-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "nested", "sample.srt");
            var document = new SubtitleDocument("en", "zh-CN", 3_000,
            [
                new SubtitleCue("c000001", 0, 1_250, "Hello", "你好", TimingOrigin.ProviderWord),
                new SubtitleCue("c000002", 1_250, 3_000, "World", "世界", TimingOrigin.ProviderWord)
            ]);
            var publisher = new SrtSubtitlePublisher();

            var artifact = await publisher.PublishAsync(
                document,
                path,
                SubtitleLayout.Bilingual,
                overwrite: false,
                CancellationToken.None);

            var text = await File.ReadAllTextAsync(path, CancellationToken.None);
            Assert.Contains("00:00:00,000 --> 00:00:01,250", text, StringComparison.Ordinal);
            Assert.Contains("Hello\n你好", text, StringComparison.Ordinal);
            Assert.True(await publisher.VerifyAsync(artifact, CancellationToken.None));
            await Assert.ThrowsAsync<Captioner.Engine.PipelineBlockedException>(() => publisher.PublishAsync(
                document,
                path,
                SubtitleLayout.Bilingual,
                overwrite: false,
                CancellationToken.None));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
