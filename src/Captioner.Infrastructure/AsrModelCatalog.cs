namespace Captioner.Infrastructure;

public enum AsrModelFamily
{
    Whisper,
    Qwen3Asr
}

public sealed record AsrModelFile(
    string RelativePath,
    Uri DownloadUri,
    long SizeBytes,
    string Sha256);

public sealed record AsrModelArchive(
    Uri DownloadUri,
    long SizeBytes,
    string Sha256,
    string RootDirectory);

public sealed record AsrModelDefinition(
    string Id,
    string DisplayName,
    AsrModelFamily Family,
    string License,
    string Revision,
    string ModelStem,
    IReadOnlyList<AsrModelFile> Files,
    AsrModelArchive? Archive = null)
{
    public long DownloadSizeBytes => Files.Sum(file => file.SizeBytes) + (Archive?.SizeBytes ?? 0);
}

public static class AsrModelCatalog
{
    private const string SileroUrl = "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/silero_vad.onnx";
    private const long SileroSize = 643_854;
    private const string SileroSha = "9e2449e1087496d8d4caba907f23e0bd3f78d91fa552479bb9c23ac09cbb1fd6";

    private static readonly IReadOnlyDictionary<string, AsrModelDefinition> Models =
        CreateModels().ToDictionary(model => model.Id, StringComparer.OrdinalIgnoreCase);

    public static string DefaultModelDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Captioner",
        "models");

    public static IReadOnlyList<AsrModelDefinition> All { get; } = Models.Values
        .OrderBy(model => model.Family)
        .ThenBy(model => model.Id, StringComparer.Ordinal)
        .ToArray();

    public static AsrModelDefinition Get(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return Models.TryGetValue(id, out var model)
            ? model
            : throw new KeyNotFoundException(
                $"Unknown ASR model '{id}'. Run 'captioner models list' to see available models.");
    }

    private static IEnumerable<AsrModelDefinition> CreateModels()
    {
        yield return Whisper(
            "whisper-tiny", "Whisper tiny (multilingual)", "tiny", "65176e2deb88badc814a94058666cadccc29b61c",
            ("tiny-decoder.int8.onnx", 89_855_401, "d2fece8dd42771f1df975c6c0445770d0c292bf7547c2cae04a6c0cc57540925"),
            ("tiny-encoder.int8.onnx", 12_937_772, "d24fb083ae3b1041fc24e97971d60e280c9342201fbb67b0ab428a8b4a51a434"),
            ("tiny-tokens.txt", 816_730, "b34b360dbb493e781e479794586d661700670d65564001f23024971d1f2fa126"));
        yield return Whisper(
            "whisper-tiny-en", "Whisper tiny.en", "tiny.en", "d026532c022fa99fd789d6b32446a1df7b6bfc43",
            ("tiny.en-decoder.int8.onnx", 89_853_865, "06c0e6ff6348d427e51839219d1c886c18cfdf411e629e33f5e1679bff9c1527"),
            ("tiny.en-encoder.int8.onnx", 12_937_772, "0ce578b827c94a961aacb8fa14b02f096504b337e5c94be37c36238cbe3e8bc6"),
            ("tiny.en-tokens.txt", 835_554, "306cd27f03c1a714eca7108e03d66b7dc042abe8c258b44c199a7ed9838dd930"));
        yield return Whisper(
            "whisper-base", "Whisper base (multilingual)", "base", "bb53ee204431c90d314c1cc08d28d23e5b7927cc",
            ("base-decoder.int8.onnx", 130_672_026, "9759d217388a01b3a4c7c15533201067b48ae819c4daafc8624e64b9409dc02d"),
            ("base-encoder.int8.onnx", 29_120_534, "0b8fb1304b6109976038efff5ace81720e00386f3ff6b54ee8c75291ca0a1e11"),
            ("base-tokens.txt", 816_730, "b34b360dbb493e781e479794586d661700670d65564001f23024971d1f2fa126"));
        yield return Whisper(
            "whisper-base-en", "Whisper base.en", "base.en", "59eea950fc76df2453efb57e6c0fd334548e8ffe",
            ("base.en-decoder.int8.onnx", 130_669_978, "f7162ad6db2dbef16cfaeaa7f945b9d7dd9c1b8d472f6aca82f2273d185e4d41"),
            ("base.en-encoder.int8.onnx", 29_120_534, "ef6b936f4c9b1d90a3b68634b60c4ed8576b26172b33c2535ec0e933c9edb823"),
            ("base.en-tokens.txt", 835_554, "306cd27f03c1a714eca7108e03d66b7dc042abe8c258b44c199a7ed9838dd930"));
        yield return Whisper(
            "whisper-small", "Whisper small (multilingual)", "small", "8f3c18b358db4d1f2fc1eae49d75cd20989e4309",
            ("small-decoder.int8.onnx", 262_226_114, "acad50b5c782696e91b55914cc5ab4f756f1532f76e22aa6fc615f39fb69a8ee"),
            ("small-encoder.int8.onnx", 112_442_483, "4cbe7b22fa9026b843b60a68640c747de05bafb1a11b57edc0e66c232d9f33a9"),
            ("small-tokens.txt", 816_730, "b34b360dbb493e781e479794586d661700670d65564001f23024971d1f2fa126"));
        yield return Whisper(
            "whisper-small-en", "Whisper small.en", "small.en", "d9533f69affd85061aee349af7fea5cb2996dbbe",
            ("small.en-decoder.int8.onnx", 262_223_042, "710ccf890e10f3faa15f51ec346081a2723c9f3adb6e4da81c6573a5a6f877fb"),
            ("small.en-encoder.int8.onnx", 112_442_483, "8bdac288f369aa94ee2194059238c465ed82ea9d47ee8fa4a8c0a891873e462f"),
            ("small.en-tokens.txt", 835_554, "306cd27f03c1a714eca7108e03d66b7dc042abe8c258b44c199a7ed9838dd930"));
        yield return Whisper(
            "whisper-medium", "Whisper medium (multilingual)", "medium", "8c31d28503847560985df21f90e14f0c736e075e",
            ("medium-decoder.int8.onnx", 571_059_257, "595d00a338a365a7bfa0ca7f296cabc639583bef770ab6130df90f49a6412747"),
            ("medium-encoder.int8.onnx", 374_196_283, "1c54582b4d829de0089f6cb63bbbdb3bf7555398bacaf855fbecf1a84dfd193e"),
            ("medium-tokens.txt", 816_730, "b34b360dbb493e781e479794586d661700670d65564001f23024971d1f2fa126"));
        yield return Whisper(
            "whisper-medium-en", "Whisper medium.en", "medium.en", "251ab4521f354490e8f2c206fbd0b7f3f6b0a7ec",
            ("medium.en-decoder.int8.onnx", 571_055_161, "7303be339ed4e51f4ffb7ae84f3803b10cf8e67e1dcf8a98cb4d843f0dea0141"),
            ("medium.en-encoder.int8.onnx", 374_196_283, "5a8e3a36619e0b67db9320eef3152db59d4b440f5ce0212d2c162a61b750bf80"),
            ("medium.en-tokens.txt", 835_554, "306cd27f03c1a714eca7108e03d66b7dc042abe8c258b44c199a7ed9838dd930"));
        yield return Whisper(
            "whisper-large-v3", "Whisper large-v3 (multilingual)", "large-v3", "2a6507094dd6020d939d78e3f1834a1d06267fca",
            ("large-v3-decoder.int8.onnx", 1_008_265_203, "ebc6bfd88e162a46cb3edee8a7e727e1dcbc65cabecb19e2573695e4d495e1af"),
            ("large-v3-encoder.int8.onnx", 766_671_985, "d531cf17248acc43e8c09b472a0877055e770877857a5332fc1304b36534ec85"),
            ("large-v3-tokens.txt", 816_730, "b34b360dbb493e781e479794586d661700670d65564001f23024971d1f2fa126"));

        yield return new(
            "qwen3-asr-0.6b-int8",
            "Qwen3-ASR 0.6B int8",
            AsrModelFamily.Qwen3Asr,
            "Apache-2.0",
            "2026-03-25",
            "qwen3-asr-0.6b-int8",
            [Silero()],
            new(
                new Uri("https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-qwen3-asr-0.6B-int8-2026-03-25.tar.bz2"),
                878_702_423,
                "393f8a14e2f5fb96746aaab342997a40641001fbd5bf9592a080a8329178ee96",
                "sherpa-onnx-qwen3-asr-0.6B-int8-2026-03-25"));
    }

    private static AsrModelDefinition Whisper(
        string id,
        string displayName,
        string stem,
        string revision,
        (string Name, long Size, string Sha) decoder,
        (string Name, long Size, string Sha) encoder,
        (string Name, long Size, string Sha) tokens)
    {
        var repository = $"csukuangfj/sherpa-onnx-whisper-{stem}";
        AsrModelFile File((string Name, long Size, string Sha) value) => new(
            value.Name,
            new Uri($"https://huggingface.co/{repository}/resolve/{revision}/{value.Name}"),
            value.Size,
            value.Sha);
        return new(
            id,
            displayName,
            AsrModelFamily.Whisper,
            "MIT",
            revision,
            stem,
            [File(decoder), File(encoder), File(tokens), Silero()]);
    }

    private static AsrModelFile Silero() => new(
        "silero_vad.onnx",
        new Uri(SileroUrl),
        SileroSize,
        SileroSha);
}
