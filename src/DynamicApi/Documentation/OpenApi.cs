namespace DynamicApi;

// Generates a concrete OpenAPI contract from the same snapshot used by the API engine.
internal static class OpenApi
{
    private static Dictionary<string, object?> Obj(params (string Key, object? Value)[] values) => values.ToDictionary(x => x.Key, x => x.Value);
    private static object Ref(string name) => Obj(("$ref", "#/components/schemas/" + name));
    private static object Scalar(string type, string? format = null) => format is null ? Obj(("type", type)) : Obj(("type", type), ("format", format));
    private static object ArrayOf(object items) => Obj(("type", "array"), ("items", items));
    private static object JsonContent(object schema) => Obj(("application/json", Obj(("schema", schema))));
    private static object Response(string description, object? schema = null) => schema is null ? Obj(("description", description)) : Obj(("description", description), ("content", JsonContent(schema)));
    private static object Body(object schema) => Obj(("required", true), ("content", JsonContent(schema)));
    private static object Parameter(string name, string where, object schema, bool required = false, string? description = null) => Obj(("name", name), ("in", where), ("required", required), ("schema", schema), ("description", description ?? ""));
    private static object Security(bool required) => required ? new[] { Obj(("Bearer", System.Array.Empty<string>())) } : System.Array.Empty<object>();
    private static object FieldSchema(Field f, bool multipart = false)
    {
        var schema = f.Type switch
        {
            "integer" => Obj(("type", "integer"), ("format", "int64")),
            "number" => Obj(("type", "number")),
            "boolean" => Obj(("type", "boolean")),
            "date" => Obj(("type", "string"), ("format", "date")),
            "datetime" => Obj(("type", "string"), ("format", "date-time")),
            "object" when multipart => Obj(("type", "string"), ("description", "JSON object as text"), ("example", "{\"key\":\"value\"}")),
            "array" when multipart => Obj(("type", "string"), ("description", "JSON array as text"), ("example", "[1,2]")),
            "object" => Obj(("type", "object"), ("additionalProperties", true)),
            "array" => Obj(("type", "array"), ("items", Obj())),
            "file" when multipart => Obj(("type", "string"), ("format", "binary")),
            "files" when multipart => Obj(("type", "array"), ("items", Scalar("string", "binary"))),
            "file" => Obj(("type", "string"), ("description", "Relative file URL")),
            "files" => Obj(("type", "array"), ("items", Scalar("string")), ("description", "Relative file URLs")),
            _ => Obj(("type", "string"))
        };
        if (!f.Required && !multipart) schema["nullable"] = true;
        return schema;
    }
    private static object RecordSchema(Resource r, bool response = false, bool patch = false, bool multipart = false)
    {
        var fields = new Dictionary<string, object?>(); var required = new List<string>();
        if (response) { fields.Add("id", Scalar("integer", "int64")); required.Add("id"); }
        foreach (var f in r.Fields.Values)
        {
            fields.Add(f.Name, FieldSchema(f, multipart));
            // Older records can predate newly required fields after hot reload.
            if (f.Required && !patch && !response) required.Add(f.Name);
        }
        var result = Obj(("type", "object"), ("properties", fields), ("additionalProperties", false));
        if (required.Count > 0) result["required"] = required;
        return result;
    }
    private static object ResourceBody(Resource r, bool patch, bool create)
    {
        var content = Obj();
        if (r.HasFiles) content["multipart/form-data"] = Obj(("schema", RecordSchema(r, patch: patch, multipart: true)));
        if (!(create && r.Fields.Values.Any(x => x.Required && (x.Type is "file" or "files"))))
            content["application/json"] = Obj(("schema", Ref(r.Name + (patch ? ".Patch" : ".Write"))));
        return Obj(("required", true), ("content", content), ("description", r.HasFiles ? "Upload files as multipart. PATCH preserves omitted fields; PUT replaces current fields. JSON can retain existing file URLs or clear optional files." : "JSON request."));
    }
    private static Dictionary<string, object?> Op(string tag, string id, string summary, bool auth, string status, object success, object? parameters = null, object? body = null, bool conflict = false)
    {
        var responses = Obj((status, success), ("400", Response("Invalid request", Ref("Error"))), ("404", Response("Not found", Ref("Error"))), ("500", Response("Server error", Ref("Error"))));
        if (auth || id == "login") responses["401"] = Response("Missing or invalid credentials", Ref("Error"));
        if (body is not null) { responses["413"] = Response("Payload too large", Ref("Error")); responses["415"] = Response("Unsupported media type", Ref("Error")); }
        if (conflict) responses["409"] = Response("Username already exists", Ref("Error"));
        var op = Obj(("tags", new[] { tag }), ("operationId", id), ("summary", summary), ("security", Security(auth)), ("responses", responses));
        if (parameters is not null) op["parameters"] = parameters;
        if (body is not null) op["requestBody"] = body;
        return op;
    }
    public static object Build(Schema schema)
    {
        var paths = Obj(); var schemas = Obj();
        schemas["Error"] = Obj(("type", "object"), ("properties", Obj(("message", Scalar("string")))), ("required", new[] { "message" }));
        schemas["Credentials"] = Obj(("type", "object"), ("required", new[] { "username", "password" }), ("properties", Obj(("username", Obj(("type", "string"), ("example", "demo"))), ("password", Obj(("type", "string"), ("format", "password"), ("example", "123456"))))));
        schemas["User"] = Obj(("type", "object"), ("properties", Obj(("id", Scalar("integer", "int64")), ("username", Scalar("string")))));
        schemas["LoginResult"] = Obj(("type", "object"), ("properties", Obj(("accessToken", Scalar("string")), ("tokenType", Scalar("string")), ("expiresIn", Scalar("integer")), ("expiresAt", Scalar("string", "date-time")), ("user", Ref("User")))));
        paths["/api/auth/register"] = Obj(("post", Op("Auth", "register", "Register a user", false, "201", Response("Created", Ref("User")), body: Body(Ref("Credentials")), conflict: true)));
        paths["/api/auth/login"] = Obj(("post", Op("Auth", "login", "Login and get a Bearer token", false, "200", Response("OK", Ref("LoginResult")), body: Body(Ref("Credentials")))));
        paths["/api/auth/me"] = Obj(("get", Op("Auth", "me", "Get current user", true, "200", Response("OK", Ref("User")))));
        var fileResponse = Obj(("description", "File"), ("content", Obj(("application/octet-stream", Obj(("schema", Scalar("string", "binary")))))));
        paths["/api/files/{filename}"] = Obj(("get", Op("Files", "download", "Download an uploaded file", schema.Auth, "200", fileResponse, new[] { Parameter("filename", "path", Scalar("string"), true) })));
        var idParams = new[] { Parameter("id", "path", Scalar("integer", "int64"), true) };
        foreach (var r in schema.Resources.Values)
        {
            var name = r.Name; var tag = name; var path = "/api/" + name;
            schemas[name + ".Record"] = RecordSchema(r, response: true);
            schemas[name + ".Write"] = RecordSchema(r);
            schemas[name + ".Patch"] = RecordSchema(r, patch: true);
            schemas[name + ".Page"] = Obj(("type", "object"), ("properties", Obj(("items", ArrayOf(Ref(name + ".Record"))), ("page", Scalar("integer")), ("pageSize", Scalar("integer")), ("totalCount", Scalar("integer")), ("totalPages", Scalar("integer")))));
            var scalarFields = r.Fields.Values.Where(f => f.Type is not ("file" or "files" or "object" or "array")).ToArray();
            var filters = new List<object>
            {
                Parameter("page", "query", Obj(("type", "integer"), ("minimum", 1), ("default", 1))),
                Parameter("pageSize", "query", Obj(("type", "integer"), ("minimum", 1), ("maximum", 1000), ("default", 20))),
                Parameter("search", "query", Scalar("string"), description: "Case-insensitive search over string fields"),
                Parameter("sort", "query", Obj(("type", "string"), ("enum", new[] { "id" }.Concat(scalarFields.Select(x => x.Name)).ToArray()), ("default", "id"))),
                Parameter("order", "query", Obj(("type", "string"), ("enum", new[] { "asc", "desc" }), ("default", "asc"))),
                Parameter("filter.id", "query", Scalar("integer", "int64"))
            };
            filters.AddRange(scalarFields.Select(f => Parameter("filter." + f.Name, "query", FieldSchema(f with { Required = true }), description: "Exact match")));
            var item = Ref(name + ".Record");
            paths[path] = Obj(
                ("get", Op(tag, name + "_list", "List records", schema.Auth, "200", Response("OK", Ref(name + ".Page")), filters)),
                ("post", Op(tag, name + "_create", "Create record", schema.Auth, "201", Response("Created", item), body: ResourceBody(r, false, true))));
            paths[path + "/{id}"] = Obj(
                ("get", Op(tag, name + "_get", "Get record", schema.Auth, "200", Response("OK", item), idParams)),
                ("put", Op(tag, name + "_update", "Replace record fields", schema.Auth, "200", Response("OK", item), idParams, ResourceBody(r, false, false))),
                ("patch", Op(tag, name + "_patch", "Update selected fields", schema.Auth, "200", Response("OK", item), idParams, ResourceBody(r, true, false))),
                ("delete", Op(tag, name + "_delete", "Delete record", schema.Auth, "204", Response("Deleted"), idParams)));
        }
        return Obj(("openapi", "3.0.3"), ("info", Obj(("title", "DynamicApi"), ("version", "1.0"), ("description", "Edit schema.json and refresh Swagger. No backend coding needed."))), ("paths", paths), ("components", Obj(("schemas", schemas), ("securitySchemes", Obj(("Bearer", Obj(("type", "http"), ("scheme", "bearer"), ("bearerFormat", "JWT"))))))));
    }
}
