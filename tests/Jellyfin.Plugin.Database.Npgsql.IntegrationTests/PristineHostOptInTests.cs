using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.Database.Npgsql.TestInfrastructure;
using Xunit;

namespace Jellyfin.Plugin.Database.Npgsql.IntegrationTests;

public sealed class PristineHostOptInTests
{
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
        using var artifacts = HostTestArtifactScope.Create("pristine-host");
        var caseRoot = artifacts.CaseDirectory;
        var evidenceRedactor = new SensitiveDataRedactor();
        try
        {
            var publish = await PristineJellyfinPublish.CreateAsync(
                PristineJellyfinPublishOptions.FromEnvironment(),
                Path.Combine(caseRoot, "pristine-host"),
                TestContext.Current.CancellationToken);
            var ffmpeg = Path.Combine(repositoryRoot, "tests", "fixtures", "fake-ffmpeg.sh");
            var portCoordinationRoot = Path.Combine(artifacts.ArtifactRoot, ".port-leases");
            for (var launch = 0; launch < 2; launch++)
            {
                using var httpPort = LoopbackPortReservation.Acquire(portCoordinationRoot);
                using var httpsPort = LoopbackPortReservation.Acquire(portCoordinationRoot);
                var layout = JellyfinHostLayout.Create(Path.Combine(caseRoot, "runs"), publish, "launch-" + launch);
                layout.WriteNetworkConfiguration(httpPort.Port, httpsPort.Port);
                var password = "test-only-" + Guid.NewGuid().ToString("N");
                evidenceRedactor.Add(password);
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
                evidenceRedactor.Add(token);
                await host.StopGracefullyAsync(token!, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
                await publish.VerifyUnchangedAsync(TestContext.Current.CancellationToken);
            }
        }
        catch (Exception exception)
        {
            artifacts.RetainFailureEvidence(exception, evidenceRedactor);
            throw new InvalidOperationException(
                $"The pristine-host proof failed. Bounded sanitized diagnostics are in '{artifacts.RetainedEvidenceDirectory}' "
                + $"({artifacts.RetainedEvidenceBytes} bytes); its copied host and case scratch directory were removed.",
                exception);
        }
    }
}
