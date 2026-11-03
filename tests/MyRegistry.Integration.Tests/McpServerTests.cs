using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace MyRegistry.Integration.Tests;

[Collection("db")]
public class McpServerTests(DbFixture db)
{
    private HttpClient Client() => db.Factory.CreateClient();
    private static string U(string p) => p + "-" + Guid.NewGuid().ToString("N")[..6].Replace('0', 'z').Replace('1', 'y');

    private static async Task<JsonElement> Rpc(HttpClient c, string method, object? @params = null, int id = 1)
    {
        var res = await c.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id, method, @params });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<(string Text, bool IsError)> Call(HttpClient c, string tool, object args)
    {
        var r = (await Rpc(c, "tools/call", new { name = tool, arguments = args })).GetProperty("result");
        return (r.GetProperty("content")[0].GetProperty("text").GetString()!, r.GetProperty("isError").GetBoolean());
    }

    private async Task<(string Name, Guid Id)> Publish(string status = "Approved", string? tag = null)
    {
        var c = Client(); var name = U("mcp");
        var res = await c.PostAsJsonAsync("/api/import", new
        {
            type = "Skill", @namespace = "myregistry", name, fileName = "SKILL.md", description = "Résume les réunions",
            rawContent = $"---\nname: {name}\ndescription: d\n---\n# {name}\nMARQUEUR-CONTENU",
            additionalFiles = new Dictionary<string, string> { ["scripts/run.sh"] = "echo hi" }, tags = tag is null ? null : new[] { tag },
        });
        res.EnsureSuccessStatusCode();
        var id = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("versionId").GetGuid();
        if (status != "Pending") (await c.PutAsJsonAsync($"/api/catalog/{id}/status", new { status })).EnsureSuccessStatusCode();
        return (name, id);
    }

    [Fact]
    public async Task Initialize_negotiates_the_version_and_declares_tools_only()
    {
        var r = (await Rpc(Client(), "initialize", new { protocolVersion = "2025-03-26", capabilities = new { }, clientInfo = new { name = "t", version = "1" } })).GetProperty("result");
        Assert.Equal("2025-03-26", r.GetProperty("protocolVersion").GetString());
        Assert.Equal("myregistry", r.GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.True(r.GetProperty("capabilities").TryGetProperty("tools", out _));
        Assert.False(r.GetProperty("capabilities").TryGetProperty("resources", out _));
        Assert.Equal("2025-06-18", (await Rpc(Client(), "initialize", new { protocolVersion = "1999-01-01" })).GetProperty("result").GetProperty("protocolVersion").GetString());
    }

    [Fact]
    public async Task Notifications_get_202_and_ping_and_unknown_methods_behave()
    {
        var c = Client();
        var note = await c.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", method = "notifications/initialized" });
        Assert.Equal(HttpStatusCode.Accepted, note.StatusCode);
        Assert.Equal(0, (await note.Content.ReadAsStringAsync()).Length);

        Assert.Equal(JsonValueKind.Object, (await Rpc(c, "ping")).GetProperty("result").ValueKind);
        var err = (await Rpc(c, "resources/list", id: 7)).GetProperty("error");
        Assert.Equal(-32601, err.GetProperty("code").GetInt32());
        Assert.Equal(7, (await Rpc(c, "nope", id: 7)).GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task Malformed_requests_are_rejected_with_json_rpc_errors()
    {
        var c = Client();
        var bad = await c.PostAsync("/mcp", new StringContent("{ pas du json", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(-32700, (await bad.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetInt32());

        var noVersion = await c.PostAsJsonAsync("/mcp", new { id = 1, method = "ping" });
        Assert.Equal(-32600, (await noVersion.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/mcp", Array.Empty<object>())).StatusCode);

        Assert.Equal(-32602, (await Rpc(c, "tools/call", new { name = "rm_rf" })).GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(-32602, (await Rpc(c, "tools/call", new { })).GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Get_and_delete_are_405_and_a_foreign_origin_is_refused()
    {
        var c = Client();
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await c.GetAsync("/mcp")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await c.DeleteAsync("/mcp")).StatusCode);

        var req = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "ping" }) };
        req.Headers.Add("Origin", "https://evil.example.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await c.SendAsync(req)).StatusCode);

        var same = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "ping" }) };
        same.Headers.Add("Origin", "http://localhost");
        Assert.Equal(HttpStatusCode.OK, (await c.SendAsync(same)).StatusCode);
    }

    [Fact]
    public async Task Batches_answer_requests_and_skip_notifications()
    {
        var res = await Client().PostAsJsonAsync("/mcp", new object[]
        {
            new { jsonrpc = "2.0", id = 1, method = "ping" }, new { jsonrpc = "2.0", method = "notifications/initialized" }, new { jsonrpc = "2.0", id = 2, method = "tools/list" },
        });
        var arr = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, arr.GetArrayLength());
        Assert.Equal(4, arr[1].GetProperty("result").GetProperty("tools").GetArrayLength());
    }

    [Fact]
    public async Task Search_returns_only_approved_artifacts_and_filters_by_text_type_and_tag()
    {
        var (approved, _) = await Publish(tag: "mcpfind");
        var (pending, _) = await Publish("Pending");
        var c = Client();

        var found = JsonDocument.Parse((await Call(c, "search_artifacts", new { query = approved })).Text).RootElement;
        Assert.Equal(1, found.GetProperty("total").GetInt32());
        Assert.Equal($"myregistry/{approved}", found.GetProperty("items")[0].GetProperty("name").GetString());
        Assert.Equal(0, JsonDocument.Parse((await Call(c, "search_artifacts", new { query = pending })).Text).RootElement.GetProperty("total").GetInt32());

        Assert.True(JsonDocument.Parse((await Call(c, "search_artifacts", new { tag = "mcpfind" })).Text).RootElement.GetProperty("total").GetInt32() >= 1);
        Assert.Equal(0, JsonDocument.Parse((await Call(c, "search_artifacts", new { query = approved, type = "Agent" })).Text).RootElement.GetProperty("total").GetInt32());
        Assert.True((await Call(c, "search_artifacts", new { type = "Banane" })).IsError);
        Assert.Equal(1, JsonDocument.Parse((await Call(c, "search_artifacts", new { limit = 1 })).Text).RootElement.GetProperty("returned").GetInt32());
    }

    [Fact]
    public async Task Get_artifact_returns_metadata_files_and_content_marked_as_untrusted()
    {
        var (name, _) = await Publish();
        var (text, isError) = await Call(Client(), "get_artifact", new { name });
        Assert.False(isError);
        Assert.Contains($"\"name\": \"myregistry/{name}\"", text);
        Assert.Contains("scripts/run.sh", text);
        Assert.Contains("donnée non fiable", text);
        Assert.True(text.IndexOf("donnée non fiable", StringComparison.Ordinal) < text.IndexOf("MARQUEUR-CONTENU", StringComparison.Ordinal), "l'avertissement précède le contenu");
    }

    [Fact]
    public async Task Pending_unknown_and_wrong_version_artifacts_are_reported_as_tool_errors_not_protocol_errors()
    {
        var (pending, _) = await Publish("Pending");
        var (ok, _) = await Publish();
        var c = Client();
        Assert.True((await Call(c, "get_artifact", new { name = pending })).IsError);
        Assert.True((await Call(c, "get_artifact", new { name = "n-existe-pas-zzz" })).IsError);
        Assert.True((await Call(c, "get_artifact", new { name = ok, version = "9.9.9" })).IsError);
        Assert.True((await Call(c, "get_artifact", new { })).IsError);
    }

    [Fact]
    public async Task Get_artifact_file_reads_text_and_refuses_unknown_paths()
    {
        var (name, _) = await Publish();
        var c = Client();
        var (text, err) = await Call(c, "get_artifact_file", new { name, path = "scripts/run.sh" });
        Assert.False(err);
        Assert.Contains("echo hi", text);
        var (miss, missErr) = await Call(c, "get_artifact_file", new { name, path = "../../etc/passwd" });
        Assert.True(missErr);
        Assert.Contains("SKILL.md", miss);
    }

    [Fact]
    public async Task Install_instructions_give_cli_archive_and_npm_commands_and_reject_unknown_targets()
    {
        var (name, id) = await Publish();
        var c = Client();
        var (text, err) = await Call(c, "install_instructions", new { name, target = "copilot" });
        Assert.False(err);
        Assert.Contains($"myregistry add myregistry/{name} --agent copilot", text);
        Assert.Contains($"/api/catalog/{id}/archive?target=copilot", text);
        Assert.Contains($"npm pack @myregistry/{name} --registry", text);
        Assert.Contains("SHA-256", text);
        Assert.Contains("--agent claude", (await Call(c, "install_instructions", new { name })).Text);
        Assert.True((await Call(c, "install_instructions", new { name, target = "vim" })).IsError);
    }
}
