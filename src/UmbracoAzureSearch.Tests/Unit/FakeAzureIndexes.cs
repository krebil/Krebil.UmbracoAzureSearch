using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UmbracoAzureSearch.Tests.Unit;

/// <summary>
/// A search service that keeps the schema of each index and the documents written to it. A write with a field the index
/// does not have fails with a 400, as in Azure. Set <see cref="FakeSearchService.Respond"/> to <see cref="Respond"/>.
/// </summary>
internal sealed class FakeAzureIndexes
{
    private readonly object _lock = new();
    private int _schemaCallsInFlight;

    /// <summary>The indexes and their fields, by index name.</summary>
    public Dictionary<string, JsonObject> Indexes { get; } = [];

    /// <summary>The aliases and the index each points to.</summary>
    public Dictionary<string, string> Aliases { get; } = [];

    /// <summary>The document ids written to each index, in request order.</summary>
    public Dictionary<string, List<string>> Documents { get; } = [];

    /// <summary>The number of documents in each upload request, per index.</summary>
    public Dictionary<string, List<int>> Uploads { get; } = [];

    /// <summary>The schema reads (GET of one index), per index.</summary>
    public Dictionary<string, int> SchemaReads { get; } = [];

    /// <summary>The schema updates (PUT of one index) that Azure accepted, per index.</summary>
    public Dictionary<string, int> SchemaUpdates { get; } = [];

    /// <summary>The uploads Azure rejected because a document has a field the index does not have.</summary>
    public int RejectedUploads { get; private set; }

    /// <summary>The searches, per index or alias name.</summary>
    public List<string> Searches { get; } = [];

    /// <summary>The highest number of schema reads and updates in flight at once.</summary>
    public int MaxSchemaCallsInFlight { get; private set; }

    /// <summary>Holds each schema read and update this long, so overlapping calls show.</summary>
    public TimeSpan SchemaCallDuration { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Answers a schema update of an index instead of Azure, e.g. with a 409. Null applies the update.
    /// Runs before the update, so it can also change the index as another process would.
    /// </summary>
    public Func<string, HttpResponseMessage?>? OnSchemaUpdate { get; set; }

    /// <summary>Answers an upload instead of Azure, e.g. with a 413 or 503. Gets the index and the document ids.</summary>
    public Func<string, string[], HttpResponseMessage?>? OnUpload { get; set; }

    /// <summary>The documents of an upload that fail one by one, with Azure's message.</summary>
    public Dictionary<string, string> FailingDocuments { get; } = [];

    /// <summary>Adds an index with the base fields of the package (suggestions on, the default) and any extra fields.</summary>
    public void AddIndex(string name, params string[] extraFields)
    {
        var fields = new JsonArray(
            Field("id", "Edm.String", key: true), Field("key", "Edm.String"), Field("objectType", "Edm.String"),
            Field("culture", "Edm.String"), Field("segment", "Edm.String"), Field("accessKeys", "Collection(Edm.String)"),
            Field("allTexts", "Collection(Edm.String)"), Field("allTextsR1", "Collection(Edm.String)"),
            Field("allTextsR2", "Collection(Edm.String)"), Field("allTextsR3", "Collection(Edm.String)"), Field("suggestion", "Edm.String"));
        foreach (var extraField in extraFields)
        {
            fields.Add(Field(extraField, "Collection(Edm.String)"));
        }

        lock (_lock)
        {
            Indexes[name] = new JsonObject { ["name"] = name, ["fields"] = fields };
        }
    }

    public string[] FieldNames(string index)
    {
        lock (_lock)
        {
            return Indexes[index]["fields"]!.AsArray().Select(f => f!["name"]!.GetValue<string>()).ToArray();
        }
    }

    public int Count(Dictionary<string, int> counts, string index)
    {
        lock (_lock)
        {
            return counts.GetValueOrDefault(index);
        }
    }

    public static HttpResponseMessage Error(HttpStatusCode status, string code, string message)
        => FakeSearchService.Json(new JsonObject { ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }.ToJsonString(), status);

    public static HttpResponseMessage Conflict(string index)
        => Error(HttpStatusCode.Conflict, "OperationNotAllowed", $"Index '{index}' cannot be updated due to a concurrent operation.");

    public HttpResponseMessage? Respond(HttpRequestMessage request)
    {
        var path = request.RequestUri!.AbsolutePath;
        var method = request.Method;
        var body = request.Content?.ReadAsStringAsync().Result;
        var index = Name("/indexes('", path);
        var alias = Name("/aliases('", path);

        if (alias is not null)
            return RespondAlias(method, alias, body);

        if (index is null)
            return RespondIndexes(method, path, body);

        string physical;
        lock (_lock)
        {
            physical = Aliases.GetValueOrDefault(index, index);
        }

        if (path.Contains("/docs/"))
            return RespondDocuments(index, physical, path, body);

        return RespondSchema(method, index, body);
    }

    private HttpResponseMessage RespondAlias(HttpMethod method, string alias, string? body)
    {
        lock (_lock)
        {
            if (method == HttpMethod.Get)
                return Aliases.TryGetValue(alias, out var target)
                    ? FakeSearchService.Json($$"""{"name":"{{alias}}","indexes":["{{target}}"]}""")
                    : NotFound();
            if (method == HttpMethod.Put)
            {
                Aliases[alias] = JsonDocument.Parse(body!).RootElement.GetProperty("indexes")[0].GetString()!;
                return FakeSearchService.Json(body!);
            }

            return Aliases.Remove(alias) ? new HttpResponseMessage(HttpStatusCode.NoContent) : NotFound();
        }
    }

    private HttpResponseMessage? RespondIndexes(HttpMethod method, string path, string? body)
    {
        lock (_lock)
        {
            if (method == HttpMethod.Get && path.EndsWith("/indexes"))
                return FakeSearchService.Json($$"""{"value":[{{string.Join(",", Indexes.Values.Select(i => i.ToJsonString()))}}]}""");
            if (method == HttpMethod.Post && path.EndsWith("/indexes"))
            {
                var created = JsonNode.Parse(body!)!.AsObject();
                var name = created["name"]!.GetValue<string>();
                if (Indexes.ContainsKey(name))
                    return Error(HttpStatusCode.Conflict, "ResourceNameAlreadyInUse", $"Index '{name}' already exists.");
                Indexes[name] = created;
                return FakeSearchService.Json(body!, HttpStatusCode.Created);
            }
        }

        return null;
    }

    private HttpResponseMessage RespondSchema(HttpMethod method, string index, string? body)
    {
        lock (_lock)
        {
            _schemaCallsInFlight++;
            MaxSchemaCallsInFlight = Math.Max(MaxSchemaCallsInFlight, _schemaCallsInFlight);
        }

        try
        {
            if (SchemaCallDuration > TimeSpan.Zero)
                Thread.Sleep(SchemaCallDuration);

            if (method == HttpMethod.Put && OnSchemaUpdate?.Invoke(index) is { } answer)
                return answer;

            lock (_lock)
            {
                if (method == HttpMethod.Delete)
                {
                    Documents.Remove(index);
                    return Indexes.Remove(index) ? new HttpResponseMessage(HttpStatusCode.NoContent) : NotFound();
                }

                if (method == HttpMethod.Get)
                {
                    SchemaReads[index] = SchemaReads.GetValueOrDefault(index) + 1;
                    return Indexes.TryGetValue(index, out var existing) ? FakeSearchService.Json(existing.ToJsonString()) : NotFound();
                }

                // An update must keep every field: Azure cannot delete a field.
                var updated = JsonNode.Parse(body!)!.AsObject();
                if (Indexes.TryGetValue(index, out var current))
                {
                    var names = updated["fields"]!.AsArray().Select(f => f!["name"]!.GetValue<string>()).ToHashSet();
                    var dropped = current["fields"]!.AsArray().Select(f => f!["name"]!.GetValue<string>()).Where(n => !names.Contains(n)).ToArray();
                    if (dropped.Length > 0)
                        return Error(HttpStatusCode.BadRequest, "OperationNotAllowed", $"Existing field(s) '{string.Join(", ", dropped)}' cannot be deleted.");
                }

                SchemaUpdates[index] = SchemaUpdates.GetValueOrDefault(index) + 1;
                Indexes[index] = updated;
                return FakeSearchService.Json(updated.ToJsonString());
            }
        }
        finally
        {
            lock (_lock)
            {
                _schemaCallsInFlight--;
            }
        }
    }

    private HttpResponseMessage RespondDocuments(string index, string physical, string path, string? body)
    {
        lock (_lock)
        {
            if (path.Contains("search.post.search"))
            {
                Searches.Add(index);
                var count = Documents.GetValueOrDefault(physical)?.Distinct().Count() ?? 0;
                return FakeSearchService.Json($$"""{"@odata.count":{{count}},"value":[]}""");
            }

            if (!path.Contains("search.index"))
                return FakeSearchService.Json("""{"value":[]}""");

            if (!Indexes.TryGetValue(physical, out var schema))
                return NotFound();

            var documents = JsonDocument.Parse(body!).RootElement.GetProperty("value").EnumerateArray().ToArray();
            var ids = documents.Select(d => d.GetProperty("id").GetString()!).ToArray();
            if (OnUpload?.Invoke(physical, ids) is { } answer)
                return answer;

            var fields = schema["fields"]!.AsArray().Select(f => f!["name"]!.GetValue<string>()).ToHashSet();
            var unknown = documents.SelectMany(d => d.EnumerateObject().Select(p => p.Name))
                .FirstOrDefault(name => !name.StartsWith('@') && !fields.Contains(name));
            if (unknown is not null)
                RejectedUploads++;
            if (unknown is not null)
                return Error(HttpStatusCode.BadRequest, "", $"The request is invalid. Details: The property '{unknown}' does not exist on type 'search.documentFields'.");

            Uploads.TryAdd(physical, []);
            Uploads[physical].Add(documents.Length);
            Documents.TryAdd(physical, []);
            var results = new List<string>();
            foreach (var document in documents)
            {
                var id = document.GetProperty("id").GetString()!;
                var action = document.GetProperty("@search.action").GetString();
                if (FailingDocuments.TryGetValue(id, out var message))
                {
                    results.Add($$"""{"key":"{{id}}","status":false,"errorMessage":"{{message}}","statusCode":400}""");
                    continue;
                }

                if (action == "delete")
                    Documents[physical].RemoveAll(d => d == id);
                else
                    Documents[physical].Add(id);
                results.Add($$"""{"key":"{{id}}","status":true,"errorMessage":null,"statusCode":201}""");
            }

            var status = results.Any(r => r.Contains("\"status\":false")) ? (HttpStatusCode)207 : HttpStatusCode.OK;
            return FakeSearchService.Json($$"""{"value":[{{string.Join(",", results)}}]}""", status);
        }
    }

    private static JsonObject Field(string name, string type, bool key = false)
        => new() { ["name"] = name, ["type"] = type, ["key"] = key, ["searchable"] = name.StartsWith("allTexts") || name == "suggestion" };

    private static string? Name(string prefix, string path)
    {
        var start = path.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0)
            return null;
        start += prefix.Length;
        return path[start..path.IndexOf("')", start, StringComparison.Ordinal)];
    }

    private static HttpResponseMessage NotFound()
        => FakeSearchService.Json("""{"error":{"code":"","message":"Not found"}}""", HttpStatusCode.NotFound);
}
