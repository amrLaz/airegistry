using System.Net;
using System.Text;
using Azure.Storage.Blobs;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using MyRegistry.Application;
using MyRegistry.Infrastructure;

namespace MyRegistry.Integration.Tests;

/// <summary>Émulateur Azure Blob (Azurite) : MYREGISTRY_TEST_AZURITE (chaîne de connexion) s'il est défini, sinon un conteneur éphémère.</summary>
public sealed class AzuriteFixture : IAsyncLifetime
{
    private IContainer? _container;
    public string ConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        var env = Environment.GetEnvironmentVariable("MYREGISTRY_TEST_AZURITE");
        if (!string.IsNullOrWhiteSpace(env)) { ConnectionString = env; return; }
        _container = new ContainerBuilder().WithImage("mcr.microsoft.com/azure-storage/azurite:latest")
            .WithCommand("azurite-blob", "--blobHost", "0.0.0.0", "--skipApiVersionCheck", "--loose")
            .WithPortBinding(10000, true).WithWaitStrategy(Wait.ForUnixContainer().UntilExternalTcpPortIsAvailable(10000)).Build();
        await _container.StartAsync();
        // Clé publique et documentée de l'émulateur : ce n'est pas un secret.
        ConnectionString = "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;"
            + $"BlobEndpoint=http://{_container.Hostname}:{_container.GetMappedPublicPort(10000)}/devstoreaccount1;";
    }

    public async Task DisposeAsync() { if (_container is not null) await _container.DisposeAsync(); }
}

public class BlobStoreTests(AzuriteFixture azurite) : IClassFixture<AzuriteFixture>, IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "myregistry-blobs-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    public static IEnumerable<object[]> Kinds => [["local"], ["azure"]];
    private IBlobStore Store(string kind) => kind == "local" ? new LocalBlobStore(_dir)
        : new AzureBlobStore(new BlobContainerClient(azurite.ConnectionString, "t" + Guid.NewGuid().ToString("N")[..12]));

    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    [Theory, MemberData(nameof(Kinds))]
    public async Task Put_get_overwrite_exists_delete(string kind)
    {
        var s = Store(kind);
        Assert.Null(await s.GetAsync("a/b.txt"));
        Assert.False(await s.ExistsAsync("a/b.txt"));
        await s.PutAsync("a/b.txt", B("un"), "text/plain");
        Assert.Equal("un", Encoding.UTF8.GetString((await s.GetAsync("a/b.txt"))!));
        await s.PutAsync("a/b.txt", B("deux"));
        Assert.Equal("deux", Encoding.UTF8.GetString((await s.GetAsync("a/b.txt"))!));
        Assert.True(await s.ExistsAsync("a/b.txt"));
        Assert.True(await s.DeleteAsync("a/b.txt"));
        Assert.False(await s.DeleteAsync("a/b.txt"));
        Assert.Null(await s.GetAsync("a/b.txt"));
    }

    [Theory, MemberData(nameof(Kinds))]
    public async Task Binary_content_round_trips_unchanged(string kind)
    {
        var s = Store(kind);
        var data = Enumerable.Range(0, 256).Select(i => (byte)i).Concat(Enumerable.Repeat((byte)0, 1000)).ToArray();
        await s.PutAsync("bin/all.bytes", data);
        Assert.Equal(data, await s.GetAsync("bin/all.bytes"));
    }

    [Theory, MemberData(nameof(Kinds))]
    public async Task List_returns_sorted_keys_under_a_prefix_only(string kind)
    {
        var s = Store(kind);
        foreach (var k in new[] { "r/2.csv", "r/1.csv", "other/x.csv", "r/sub/3.csv" }) await s.PutAsync(k, B(k));
        Assert.Equal(["r/1.csv", "r/2.csv", "r/sub/3.csv"], await s.ListAsync("r/"));
        Assert.Equal(4, (await s.ListAsync("")).Count);
        Assert.Empty(await s.ListAsync("nothing/"));
    }

    [Theory, MemberData(nameof(Kinds))]
    public async Task Unsafe_keys_and_prefixes_are_refused_before_touching_storage(string kind)
    {
        var s = Store(kind);
        foreach (var bad in new[] { "../escape", "a/../../escape", "/abs", "a\\b", "", "a//b" })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => s.PutAsync(bad, B("x")));
            await Assert.ThrowsAsync<ArgumentException>(() => s.GetAsync(bad));
            await Assert.ThrowsAsync<ArgumentException>(() => s.DeleteAsync(bad));
        }
        await Assert.ThrowsAsync<ArgumentException>(() => s.ListAsync("../"));
    }

    [Fact]
    public async Task Local_store_never_writes_outside_its_root_and_leaves_no_temp_files()
    {
        var s = new LocalBlobStore(_dir);
        await s.PutAsync("ok/file.txt", B("x"));
        await Assert.ThrowsAsync<ArgumentException>(() => s.PutAsync("../outside.txt", B("x")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_dir)!, "outside.txt")));
        Assert.DoesNotContain(Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories), f => f.EndsWith(".tmp"));
    }

    [Fact]
    public void Factory_picks_the_backend_from_configuration_and_rejects_bad_values()
    {
        Assert.IsType<LocalBlobStore>(BlobStoreFactory.Create(null, null, null, _dir));
        Assert.IsType<AzureBlobStore>(BlobStoreFactory.Create(azurite.ConnectionString, null, "mycontainer", null));
        Assert.IsType<AzureBlobStore>(BlobStoreFactory.Create(null, "https://acct.blob.core.windows.net/", null, null));
        Assert.Throws<InvalidOperationException>(() => BlobStoreFactory.Create(null, "http://acct.blob.core.windows.net", null, null));
        Assert.Throws<InvalidOperationException>(() => BlobStoreFactory.Create(null, null, "Bad_Container", _dir));
    }
}
