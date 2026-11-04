using System.Net;
using System.Security.Cryptography;
using System.Text;
using SuperDev.Models;
using SuperDev.Services.Ai;

namespace SuperDev.Tests;

public class AISetupTests
{
    private const string ReleaseJson = """
        {"assets":[
          {"name":"Ollama-darwin.zip","browser_download_url":"https://github.com/ollama/ollama/releases/download/v0.40.14/Ollama-darwin.zip","size":219000000},
          {"name":"OllamaSetup.exe","browser_download_url":"https://github.com/ollama/ollama/releases/download/v0.40.14/OllamaSetup.exe","size":1600000000},
          {"name":"ollama-windows-arm64.zip","browser_download_url":"https://github.com/ollama/ollama/releases/download/v0.40.14/ollama-windows-arm64.zip","size":900000000},
          {"name":"sha256sum.txt","browser_download_url":"https://github.com/ollama/ollama/releases/download/v0.40.14/sha256sum.txt","size":400}
        ]}
        """;

    private const string Checksums =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa  Ollama-darwin.zip\n" +
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb *OllamaSetup.exe\n" +
        "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc  windows/ollama-windows-arm64.zip\n";

    [Fact]
    public void Release_selection_requires_an_official_checksum_and_https_asset()
    {
        var mac = OfficialOllamaRelease.Select(ReleaseJson, Checksums, OfficialOllamaRelease.MacZip);
        var windows = OfficialOllamaRelease.Select(ReleaseJson, Checksums, OfficialOllamaRelease.WindowsSetup);
        var arm = OfficialOllamaRelease.Select(ReleaseJson, Checksums, OfficialOllamaRelease.WindowsArmZip);

        Assert.NotNull(mac);
        Assert.Equal(219_000_000, mac.Size);
        Assert.StartsWith("aaa", mac.Sha256, StringComparison.Ordinal);
        Assert.Equal(1_600_000_000, windows!.Size);
        Assert.Equal(900_000_000, arm!.Size);
        Assert.Null(OfficialOllamaRelease.Select(ReleaseJson, "not a checksum", OfficialOllamaRelease.MacZip));
        Assert.Null(OfficialOllamaRelease.Select(
            ReleaseJson.Replace("https://github.com", "http://github.com", StringComparison.Ordinal),
            Checksums,
            OfficialOllamaRelease.MacZip));
        Assert.Equal(219_000_000, OfficialOllamaRelease.AssetSize(ReleaseJson, OfficialOllamaRelease.MacZip));
        Assert.False(OfficialOllamaRelease.IsAllowedDownload(new Uri("http://github.com/ollama/ollama/releases/latest")));
        Assert.False(OfficialOllamaRelease.IsAllowedDownload(new Uri("https://example.com/Ollama-darwin.zip")));
        Assert.True(OfficialOllamaRelease.IsAllowedDownload(new Uri("https://github.com/ollama/ollama/releases/download/v0.40.14/Ollama-darwin.zip")));
        Assert.True(OfficialOllamaRelease.IsAllowedDownload(new Uri("https://objects.githubusercontent.com/ollama")));
        Assert.True(OfficialOllamaRelease.IsAllowedDownload(new Uri("https://ollama.com/download/Ollama-darwin.zip")));
        if (OperatingSystem.IsMacOS())
            Assert.Equal(OfficialOllamaRelease.MacZip, OfficialOllamaRelease.AssetNameForCurrentPlatform());
        if (OperatingSystem.IsWindows() && System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.X64)
            Assert.Equal(OfficialOllamaRelease.WindowsSetup, OfficialOllamaRelease.AssetNameForCurrentPlatform());
    }

    [Fact]
    public void Checksum_mismatch_and_zip_slip_are_rejected()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "ollama");
            var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
            Assert.True(OfficialOllamaRelease.HashEquals(file, actual));
            Assert.False(OfficialOllamaRelease.HashEquals(file, new string('d', 64)));
        }
        finally
        {
            File.Delete(file);
        }

        var root = Path.Combine(Path.GetTempPath(), "superdev-zip-" + Guid.NewGuid().ToString("N"));
        Assert.Throws<InvalidOperationException>(() => OfficialOllamaRelease.SafeEntryPath(root, "../outside.txt"));
        var safe = OfficialOllamaRelease.SafeEntryPath(root, "Ollama/ollama.exe");
        Assert.StartsWith(Path.GetFullPath(root), safe, StringComparison.Ordinal);
    }

    [Fact]
    public void Pull_progress_adds_each_layer_and_keeps_the_status()
    {
        var layers = new Dictionary<string, (long Completed, long Total)>();
        Assert.True(OllamaPullProgress.TryApply(
            """{"status":"downloading","digest":"a","total":100,"completed":40}""", layers, out var first));
        Assert.True(OllamaPullProgress.TryApply(
            """{"status":"downloading","digest":"b","total":50,"completed":25}""", layers, out var second));
        Assert.True(OllamaPullProgress.TryApply("""{"status":"success"}""", layers, out var done));

        Assert.Equal(40, first.Completed);
        Assert.Equal(100, first.Total);
        Assert.Equal(65, second.Completed);
        Assert.Equal(150, second.Total);
        Assert.True(done.Succeeded);
        Assert.False(done.Failed);
        Assert.Equal("success", done.Status);
    }

    [Theory]
    [InlineData(false, false, false, false, false, AiSetupNeed.InstallRuntime)]
    [InlineData(true, false, false, false, false, AiSetupNeed.StartRuntime)]
    [InlineData(true, true, false, false, false, AiSetupNeed.DownloadModel)]
    [InlineData(true, true, true, true, false, AiSetupNeed.None)]
    [InlineData(true, true, true, false, false, AiSetupNeed.Repair)]
    [InlineData(false, false, false, false, true, AiSetupNeed.Repair)]
    public void Readiness_decides_each_missing_piece_on_its_own(
        bool runtime, bool api, bool model, bool loads, bool hadRuntime, AiSetupNeed need)
    {
        Assert.Equal(need, AISetupManager.Decide(runtime, api, model, loads, hadRuntime));
    }

    [Fact]
    public async Task Check_does_not_download_and_a_removed_install_is_a_repair()
    {
        var ai = new FakeLocalAi { Reachable = false };
        var installer = new FakeInstaller();
        var models = new FakeModels();
        var catalog = new FakeCatalog();
        var cache = new MemoryAISetupCache();
        cache.Write(new AISetupRecord { HadRuntime = true, ExecutablePath = "/missing/ollama", Model = "qwen3.5:0.8b" });
        var manager = Manager(ai, new FakeDetector(installed: false), installer, new FakeProcesses(), models, cache, catalog);

        var readiness = await manager.CheckAsync();
        var estimate = await manager.DescribeDownloadAsync();

        Assert.False(readiness.IsReady);
        Assert.False(readiness.RuntimeInstalled);
        Assert.Equal(AiSetupNeed.Repair, readiness.Need);
        Assert.Equal(0, installer.Calls);
        Assert.Equal(0, models.Pulls);
        Assert.Equal(0, catalog.Finds);
        Assert.Equal(1, catalog.Estimates);
        Assert.Contains("MB", estimate, StringComparison.Ordinal);
        Assert.Contains("model size", estimate, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Nothing_installed_downloads_runtime_starts_service_and_pulls_the_model()
    {
        var ai = new FakeLocalAi();
        var installer = new FakeInstaller();
        var processes = new FakeProcesses(() => ai.Reachable = true);
        var models = new FakeModels();
        var manager = Manager(ai, new FakeDetector(installed: false), installer, processes, models, new MemoryAISetupCache(), new FakeCatalog());

        var result = await manager.SetupAsync(progress: null);

        Assert.True(result.Succeeded);
        Assert.Equal(1, installer.Calls);
        Assert.Equal(1, processes.Starts);
        Assert.Equal(1, models.Pulls);
        Assert.Equal("qwen3.5:0.8b", models.PulledModel);
        Assert.True(ai.Reachable);
    }

    [Fact]
    public async Task Installed_but_stopped_starts_the_service_without_a_new_download()
    {
        var ai = new FakeLocalAi { ModelInstalled = true };
        var installer = new FakeInstaller();
        var processes = new FakeProcesses(() => ai.Reachable = true);
        var models = new FakeModels { Describe = true, Load = true };
        var manager = Manager(
            ai,
            new FakeDetector(installed: true, running: false),
            installer,
            processes,
            models,
            new MemoryAISetupCache(),
            new FakeCatalog());

        var result = await manager.SetupAsync(progress: null);

        Assert.True(result.Succeeded);
        Assert.Equal(0, installer.Calls);
        Assert.Equal(1, processes.Starts);
        Assert.Equal(0, models.Pulls);
    }

    [Fact]
    public async Task Running_service_with_a_missing_model_pulls_once()
    {
        var ai = new FakeLocalAi { Reachable = true };
        var installer = new FakeInstaller();
        var processes = new FakeProcesses();
        var models = new FakeModels();
        var manager = Manager(
            ai,
            new FakeDetector(installed: true, running: true),
            installer,
            processes,
            models,
            new MemoryAISetupCache(),
            new FakeCatalog());

        var first = await manager.SetupAsync(progress: null);
        ai.ModelInstalled = true;
        var second = await manager.SetupAsync(progress: null);

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.Equal(0, installer.Calls);
        Assert.Equal(0, processes.Starts);
        Assert.Equal(1, models.Pulls);
    }

    [Fact]
    public async Task Ready_installation_skips_setup_work()
    {
        var ai = new FakeLocalAi { Reachable = true, ModelInstalled = true };
        var installer = new FakeInstaller();
        var models = new FakeModels { Describe = true, Load = true };
        var manager = Manager(
            ai,
            new FakeDetector(installed: true, running: true),
            installer,
            new FakeProcesses(),
            models,
            new MemoryAISetupCache(),
            new FakeCatalog());

        var readiness = await manager.CheckAsync();

        Assert.True(readiness.IsReady);
        Assert.Equal(AiSetupNeed.None, readiness.Need);
        Assert.Equal(0, installer.Calls);
        Assert.Equal(0, models.Pulls);
    }

    [Fact]
    public async Task Cancellation_network_installer_and_disk_failures_can_be_retried()
    {
        var ai = new FakeLocalAi();
        var models = new FakeModels();
        var catalog = new FakeCatalog();
        var cache = new MemoryAISetupCache();
        var detector = new FakeDetector(installed: false);

        var cancelled = new FakeInstaller { Error = new OperationCanceledException() };
        var cancelResult = await Manager(ai, detector, cancelled, new FakeProcesses(), models, cache, catalog).SetupAsync(null);
        Assert.True(cancelResult.Cancelled);
        Assert.Equal(0, models.Pulls);

        var network = new FakeInstaller
        {
            Error = new AISetupException(AiSetupFailureKind.Network, "The download was interrupted.", "Check the internet connection and try again.")
        };
        var networkResult = await Manager(ai, detector, network, new FakeProcesses(), models, cache, catalog).SetupAsync(null);
        Assert.Equal(AiSetupFailureKind.Network, networkResult.Failure);
        Assert.Contains("internet", networkResult.Recovery, StringComparison.OrdinalIgnoreCase);

        var failed = new FakeInstaller
        {
            Error = new AISetupException(AiSetupFailureKind.InstallerFailed, "The installer did not finish.", "Try the setup again.")
        };
        var failedResult = await Manager(ai, detector, failed, new FakeProcesses(), models, cache, catalog).SetupAsync(null);
        Assert.Equal(AiSetupFailureKind.InstallerFailed, failedResult.Failure);

        var disk = new FakeInstaller
        {
            Error = new AISetupException(AiSetupFailureKind.InsufficientStorage, "This computer needs about 2.2 GB free for local AI.", "Free up disk space and try again.")
        };
        var diskResult = await Manager(ai, detector, disk, new FakeProcesses(), models, cache, catalog).SetupAsync(null);
        Assert.Equal(AiSetupFailureKind.InsufficientStorage, diskResult.Failure);
        Assert.Contains("disk", diskResult.Recovery, StringComparison.OrdinalIgnoreCase);

        var recovered = await Manager(ai, detector, new FakeInstaller(), new FakeProcesses(() => ai.Reachable = true), models, cache, catalog).SetupAsync(null);
        Assert.True(recovered.Succeeded);
    }

    [Fact]
    public async Task A_running_process_with_no_api_is_not_started_again()
    {
        var ai = new FakeLocalAi { Reachable = false };
        var processes = new FakeProcesses();
        var manager = Manager(
            ai,
            new FakeDetector(installed: true, running: true),
            new FakeInstaller(),
            processes,
            new FakeModels(),
            new MemoryAISetupCache(),
            new FakeCatalog(),
            apiWait: TimeSpan.FromMilliseconds(40),
            apiPoll: TimeSpan.FromMilliseconds(10));

        var result = await manager.SetupAsync(progress: null);

        Assert.Equal(AiSetupFailureKind.ServiceFailed, result.Failure);
        Assert.Contains("try again", result.Recovery, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, processes.Starts);
    }

    [Fact]
    public async Task Interrupted_model_download_can_be_retried_without_reinstalling()
    {
        var ai = new FakeLocalAi { Reachable = true };
        var installer = new FakeInstaller();
        var models = new FakeModels { FailPulls = 1 };
        var manager = Manager(
            ai,
            new FakeDetector(installed: true, running: true),
            installer,
            new FakeProcesses(),
            models,
            new MemoryAISetupCache(),
            new FakeCatalog());

        var failed = await manager.SetupAsync(progress: null);
        var retried = await manager.SetupAsync(progress: null);

        Assert.Equal(AiSetupFailureKind.ModelDownloadFailed, failed.Failure);
        Assert.Contains("resume", failed.Recovery, StringComparison.OrdinalIgnoreCase);
        Assert.True(retried.Succeeded);
        Assert.Equal(0, installer.Calls);
        Assert.Equal(2, models.Pulls);
    }

    [Fact]
    public async Task Setup_then_generation_uses_the_same_local_service()
    {
        var pulled = false;
        var pulls = 0;
        var updates = new List<ModelDownloadProgress>();
        using var service = LocalService(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("pull", StringComparison.Ordinal))
            {
                pulls++;
                var body = await request.Content!.ReadAsStringAsync();
                Assert.Contains("\"stream\":true", body, StringComparison.Ordinal);
                Assert.Contains("qwen3.5:0.8b", body, StringComparison.Ordinal);
                pulled = true;
                return Text("""
                    {"status":"downloading","digest":"a","total":100,"completed":40}
                    {"status":"downloading","digest":"b","total":50,"completed":25}
                    {"status":"success"}
                    """);
            }

            if (path.Contains("tags", StringComparison.Ordinal))
                return Text(pulled
                    ? """{"models":[{"name":"qwen3.5:0.8b"}]}"""
                    : """{"models":[]}""");
            if (path.Contains("show", StringComparison.Ordinal) || path.Contains("generate", StringComparison.Ordinal))
                return Text("""{"response":"ok"}""");
            return Text("""
                {"message":{"content":"{\"title\":\"Login fix\",\"summary\":\"Fix email validation.\",\"sections\":[{\"name\":\"Implementation\",\"items\":[{\"title\":\"Fix email validation\",\"description\":\"Reject invalid addresses.\",\"type\":\"Implementation\",\"priority\":\"Required\",\"acceptanceCriteria\":\"Invalid email is rejected.\"}]}]}"}}
                """);
        });
        var installer = new FakeInstaller();
        var manager = new AISetupManager(
            service,
            new FakeDetector(installed: true, running: true),
            installer,
            new FakeProcesses(),
            new OllamaModelManager(service),
            new MemoryAISetupCache(),
            new FakeCatalog());
        var progress = new Progress<SetupProgress>(update =>
        {
            if (update.CompletedBytes is long completed)
                updates.Add(new ModelDownloadProgress(completed, update.TotalBytes, update.Message, false, false));
        });

        var setup = await manager.SetupAsync(progress);
        var generated = await service.GenerateChecklistAsync(new AiChecklistRequest
        {
            Platform = DevPlatform.Android,
            Framework = DevFramework.Maui,
            WorkKind = AiWorkKind.BugFix,
            Description = "Email validation rejects a valid address"
        });

        Assert.True(setup.Succeeded);
        Assert.Equal(0, installer.Calls);
        Assert.Equal(1, pulls);
        Assert.Contains(updates, update => update.Completed == 65 && update.Total == 150);
        Assert.True(generated.Succeeded);
        Assert.Equal("Login fix", generated.Checklist!.Title);
    }

    [Fact]
    public void A_missing_cache_file_is_ignored()
    {
        var path = Path.Combine(Path.GetTempPath(), "superdev-cache-" + Guid.NewGuid().ToString("N") + ".json");
        var cache = new FileAISetupCache(path);
        Assert.Null(cache.Read());
        cache.Write(new AISetupRecord { HadRuntime = true, ExecutablePath = "/tmp/ollama", Model = "qwen3.5:0.8b" });
        Assert.True(cache.Read()!.HadRuntime);
        File.WriteAllText(path, "{not json");
        Assert.Null(cache.Read());
        File.Delete(path);
    }

    [Fact]
    public void A_cached_path_that_does_not_exist_is_not_reported_as_the_executable()
    {
        var missing = Path.Combine(Path.GetTempPath(), "missing-ollama-" + Guid.NewGuid().ToString("N"));
        var snapshot = new OllamaInstallationDetector().Inspect(missing);

        Assert.NotEqual(missing, snapshot.ExecutablePath);
        if (snapshot.ExecutablePath is not null)
            Assert.True(File.Exists(snapshot.ExecutablePath));
        Assert.Equal(snapshot.ExecutablePath is not null, snapshot.ExecutableFound);
    }

    [Fact]
    public async Task Live_local_runtime_is_reused_when_it_is_already_ready()
    {
        using var service = OllamaService.CreateLocal();
        var manager = AISetupManager.Create(service);
        AIReadiness readiness;
        try
        {
            readiness = await manager.CheckAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OllamaRequestException)
        {
            return;
        }

        if (!readiness.ApiResponding)
            return;

        Assert.True(readiness.ModelInstalled);
        Assert.True(readiness.ModelLoads);
        Assert.True(readiness.IsReady);
    }

    private static AISetupManager Manager(
        FakeLocalAi ai,
        FakeDetector detector,
        FakeInstaller installer,
        FakeProcesses processes,
        FakeModels models,
        IAISetupCache cache,
        FakeCatalog catalog,
        TimeSpan? apiWait = null,
        TimeSpan? apiPoll = null) =>
        new(ai, detector, installer, processes, models, cache, catalog, apiWait: apiWait, apiPoll: apiPoll);

    private static OllamaService LocalService(Func<HttpRequestMessage, Task<HttpResponseMessage>> send)
    {
        var client = new HttpClient(new StubHandler((request, _) => send(request)))
        {
            BaseAddress = OllamaService.LocalEndpoint,
            Timeout = Timeout.InfiniteTimeSpan
        };
        return new OllamaService(
            client,
            new PromptBuilder(),
            new ChecklistValidator(),
            OllamaService.DefaultModel,
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5));
    }

    private static HttpResponseMessage Text(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class FakeLocalAi : ILocalAIService
    {
        public bool Reachable { get; set; }

        public bool ModelInstalled { get; set; }

        public Task<OllamaAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new OllamaAvailability(Reachable
                ? OllamaReachability.Reachable
                : OllamaReachability.ConnectionRefused));

        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(ModelInstalled ? ["qwen3.5:0.8b"] : []);

        public Task<bool> IsModelAvailableAsync(string modelName, CancellationToken cancellationToken = default) =>
            Task.FromResult(ModelInstalled);

        public Task<AiGenerationResult> GenerateChecklistAsync(AiChecklistRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AiGenerationResult> GenerateSectionAsync(
            AiChecklistRequest request,
            string sectionName,
            IReadOnlyList<string> existingTitles,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AiGenerationResult> GenerateCheckAsync(
            CheckGenerationRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeDetector(bool installed, bool running = false) : IOllamaInstallationDetector
    {
        public OllamaInstallSnapshot Inspect(string? cachedPath) => new()
        {
            RuntimeInstalled = installed,
            ExecutableFound = installed,
            ProcessRunning = running,
            ExecutablePath = installed ? "/Applications/Ollama.app/Contents/Resources/ollama" : null,
            AppPath = installed ? "/Applications/Ollama.app" : null
        };
    }

    private sealed class FakeInstaller : IOllamaInstaller
    {
        public int Calls { get; private set; }

        public Exception? Error { get; init; }

        public bool IsSupported => true;

        public Task<OllamaInstallSnapshot> InstallAsync(IProgress<SetupProgress>? progress, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Error is not null)
                throw Error;
            progress?.Report(new SetupProgress
            {
                Stage = AiSetupStage.DownloadingRuntime,
                Message = "Downloading Ollama.",
                Fraction = 1,
                CompletedBytes = 10,
                TotalBytes = 10
            });
            return Task.FromResult(new OllamaInstallSnapshot
            {
                RuntimeInstalled = true,
                ExecutableFound = true,
                ExecutablePath = "/Applications/Ollama.app/Contents/Resources/ollama",
                AppPath = "/Applications/Ollama.app"
            });
        }
    }

    private sealed class FakeProcesses(Action? onStart = null) : IOllamaProcessManager
    {
        public int Starts { get; private set; }

        public Task StartAsync(OllamaInstallSnapshot installation, CancellationToken cancellationToken = default)
        {
            Starts++;
            onStart?.Invoke();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeModels : IOllamaModelManager
    {
        public int Pulls { get; private set; }

        public int FailPulls { get; init; }

        public string? PulledModel { get; private set; }

        public bool Describe { get; init; } = true;

        public bool Load { get; init; } = true;

        public Task PullAsync(string model, IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken = default)
        {
            Pulls++;
            PulledModel = model;
            if (Pulls <= FailPulls)
                throw new OllamaRequestException(AiFailureKind.Unexpected, "model-download");
            progress?.Report(new ModelDownloadProgress(4, 8, "downloading", false, false));
            progress?.Report(new ModelDownloadProgress(8, 8, "success", true, false));
            return Task.CompletedTask;
        }

        public Task<bool> DescribeAsync(string model, CancellationToken cancellationToken = default) =>
            Task.FromResult(Describe);

        public Task<bool> LoadAsync(string model, CancellationToken cancellationToken = default) =>
            Task.FromResult(Load);
    }

    private sealed class FakeCatalog : IOllamaReleaseCatalog
    {
        public int Estimates { get; private set; }

        public int Finds { get; private set; }

        public Task<long?> EstimateBytesAsync(CancellationToken cancellationToken = default)
        {
            Estimates++;
            return Task.FromResult<long?>(219_000_000);
        }

        public Task<OllamaReleaseAsset?> FindAsync(CancellationToken cancellationToken = default)
        {
            Finds++;
            return Task.FromResult<OllamaReleaseAsset?>(null);
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
