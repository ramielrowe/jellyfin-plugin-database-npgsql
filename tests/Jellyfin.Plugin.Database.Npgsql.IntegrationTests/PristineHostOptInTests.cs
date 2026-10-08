using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.Database.Npgsql.TestInfrastructure;
using Xunit;

namespace Jellyfin.Plugin.Database.Npgsql.IntegrationTests;

public sealed class PristineHostOptInTests
{
    private const string ArtifactRootEnvironmentVariable = "JELLYFIN_NPGSQL_TEST_ARTIFACT_ROOT";

    [Fact]
    public async Task UntouchedPublish_StartsRepeatedlyWithoutParentCheckout()
    {
        var publishDirectory = Environment.GetEnvironmentVariable(PristineJellyfinPublishOptions.PublishDirectoryEnvironmentVariable);
        var archive = Environment.GetEnvironmentVariable(PristineJellyfinPublishOptions.ArchiveEnvironmentVariable);
        var configured = !string.IsNullOrWhiteSpace(publishDirectory) || !string.IsNullOrWhiteSpace(archive);
        var required = string.Equals(
            Environment.GetEnvironmentVariable(PostgreSqlTestOptions.RequiredEnvironmentVariable),
            "true",
            StringComparison.OrdinalIgnoreCase);
        if (required && !configured)
        {
            throw new InvalidOperationException(
                $"{PostgreSqlTestOptions.RequiredEnvironmentVariable}=true requires an exact host through "
                + $"{PristineJellyfinPublishOptions.PublishDirectoryEnvironmentVariable} or "
                + $"{PristineJellyfinPublishOptions.ArchiveEnvironmentVariable}.");
        }

        Assert.SkipUnless(
            configured,
            $"Set {PristineJellyfinPublishOptions.PublishDirectoryEnvironmentVariable} or "
            + $"{PristineJellyfinPublishOptions.ArchiveEnvironmentVariable}, plus "
            + $"{PristineJellyfinPublishOptions.Sha256EnvironmentVariable}, to run pristine-host tests.");

        var repositoryRoot = PluginRepository.FindRoot(AppContext.BaseDirectory);
        var configuredArtifactRoot = Environment.GetEnvironmentVariable(ArtifactRootEnvironmentVariable);
        var artifactRoot = configuredArtifactRoot
            ?? Path.Combine(Path.GetTempPath(), "jellyfin-npgsql-test-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifactRoot);
        var caseRoot = Path.Combine(artifactRoot, "case-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(caseRoot);
        var succeeded = false;
        try
        {
            var publish = await PristineJellyfinPublish.CreateAsync(
                PristineJellyfinPublishOptions.FromEnvironment(),
                Path.Combine(caseRoot, "pristine-host"),
                TestContext.Current.CancellationToken);
            var ffmpeg = Path.Combine(repositoryRoot, "tests", "fixtures", "fake-ffmpeg.sh");
            for (var launch = 0; launch < 2; launch++)
            {
                var httpPort = LoopbackPortReservation.GetAvailablePort();
                var httpsPort = LoopbackPortReservation.GetAvailablePort();
                var layout = JellyfinHostLayout.Create(Path.Combine(caseRoot, "runs"), publish, "launch-" + launch);
                layout.WriteNetworkConfiguration(httpPort, httpsPort);
                var password = "test-only-" + Guid.NewGuid().ToString("N");
                var redactor = new SensitiveDataRedactor(password);
                await using var host = JellyfinHostProcess.Start(
                    layout,
                    httpPort,
                    redactor,
                    ffmpeg);
                await host.WaitUntilReadyAsync(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
                Assert.Equal("Healthy", (await host.GetStringAsync("health", TestContext.Current.CancellationToken)).Trim());
                _ = await host.GetStringAsync("Startup/User", TestContext.Current.CancellationToken);
                _ = await host.PostJsonAsync(
                    "Startup/User",
                    JsonSerializer.Serialize(new { Name = "infra-admin", Password = password }),
                    authorization: null,
                    TestContext.Current.CancellationToken);
                _ = await host.PostJsonAsync("Startup/Complete", "{}", authorization: null, TestContext.Current.CancellationToken);
                var authentication = await host.PostJsonAsync(
                    "Users/AuthenticateByName",
                    JsonSerializer.Serialize(new { Username = "infra-admin", Pw = password }),
                    $"MediaBrowser Client=\"npgsql-infra\", Device=\"tests\", DeviceId=\"launch-{launch}\", Version=\"0.1\"",
                    TestContext.Current.CancellationToken);
                using var document = JsonDocument.Parse(authentication);
                var token = document.RootElement.GetProperty("AccessToken").GetString();
                Assert.False(string.IsNullOrWhiteSpace(token));
                redactor.Add(token);
                await host.StopGracefullyAsync(token!, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
                await publish.VerifyUnchangedAsync(TestContext.Current.CancellationToken);
            }

            succeeded = true;
        }
        finally
        {
            if (succeeded)
            {
                Directory.Delete(caseRoot, recursive: true);
                if (configuredArtifactRoot is null && !Directory.EnumerateFileSystemEntries(artifactRoot).Any())
                {
                    Directory.Delete(artifactRoot);
                }
            }
        }
    }
}
