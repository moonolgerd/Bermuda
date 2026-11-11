using System.Text;
using Google.Protobuf;
using Google.Protobuf.Reflection;

// Usage: Bermuda.Fdc3.Grpc.Generator <descriptor set (.pb)> <contract .cs> <glue .cs>
//
// Protos/fdc3.proto is the source of truth for Bermuda's FDC3 contract (the conventions are documented at the
// top of that file). This tool reads protoc's descriptor set of it (with source info, for the doc comments) and
// writes:
//   * the C# contract the in-process agent and the web bridge use: Fdc3* records and IFdc3Commands;
//   * the glue that serves it over gRPC: proto <-> record mapping and one service override per rpc.
// A proto that breaks the conventions fails the build with the list of problems.
if (args.Length != 3) throw new ArgumentException("Usage: <descriptor set> <contract .cs> <glue .cs>");

// The `async` method option (field 50001 of google.protobuf.MethodOptions) is defined in fdc3.proto itself.
var asyncOption = new Extension<MethodOptions, bool>(50001, FieldCodec.ForBool(50001u << 3));
var set = FileDescriptorSet.Parser
    .WithExtensionRegistry(new ExtensionRegistry { asyncOption })
    .ParseFrom(File.ReadAllBytes(args[0]));
var file = set.File.Single(f => f.Name.EndsWith("fdc3.proto", StringComparison.Ordinal));

var (model, errors) = ProtoModel.Build(file, asyncOption);
if (errors.Count > 0)
{
    Console.Error.WriteLine("fdc3.proto breaks the generator's conventions:");
    foreach (var error in errors) Console.Error.WriteLine("  - " + error);
    return 1;
}

Write(args[1], new ContractWriter(model).Generate());
Write(args[2], new GlueWriter(model).Generate());
return 0;

static void Write(string path, string content)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    content = content.Replace("\r\n", "\n");
    // Don't touch an unchanged file, so incremental builds stay incremental.
    if (!File.Exists(path) || File.ReadAllText(path).Replace("\r\n", "\n") != content) File.WriteAllText(path, content);
}

enum Kind { String, Bool, Json, Message }

/// <param name="ProtoCs">Property name on the protoc-generated class.</param>
/// <param name="Prop">Property name on the contract record (and so the web JSON name).</param>
sealed record Field(string ProtoName, string ProtoCs, string Prop, Kind Kind, bool Repeated, bool Optional, string? Message)
{
    public bool Nullable => Optional && !Repeated;
}

sealed record Msg(string Name, string Record, List<Field> Fields, string? Doc);

enum Shape { Void, Direct, Array, Nullable, NullableJson }

/// <param name="Request">Request message, null for google.protobuf.Empty.</param>
/// <param name="Result">Result message (null for void / JSON); <see cref="Shape"/> says how the contract returns it.</param>
sealed record Rpc(string Name, string? Request, string? Result, Shape Shape, bool Async, string? Wrapper, string? Doc);

/// <param name="Field">AgentEvent oneof field, which is also the web message type suffix ("fdc3.&lt;field&gt;").</param>
sealed record Ev(Msg Message, string Field, string ProtoCs);

sealed class ProtoModel
{
    private static readonly HashSet<string> Transport = ["SubscribeRequest", "Connected", "AgentEvent"];

    public List<Msg> Messages { get; } = [];
    public List<Rpc> Rpcs { get; } = [];
    public List<Ev> Events { get; } = [];

    public static (ProtoModel Model, List<string> Errors) Build(FileDescriptorProto file, Extension<MethodOptions, bool> asyncOption)
    {
        var model = new ProtoModel();
        var errors = new List<string>();
        var prefix = "." + file.Package + ".";
        string? Local(string typeName) => typeName.StartsWith(prefix, StringComparison.Ordinal) ? typeName[prefix.Length..] : null;

        var docs = file.SourceCodeInfo?.Location
            .Where(l => l.LeadingComments.Length > 0)
            .ToDictionary(l => string.Join(',', l.Path), l => Clean(l.LeadingComments)) ?? [];
        string? DocOf(params int[] path) => docs.GetValueOrDefault(string.Join(',', path));

        var service = file.Service.SingleOrDefault(s => s.Name == "Fdc3Agent");
        if (service is null) { errors.Add("service Fdc3Agent is missing."); return (model, errors); }
        var serviceIndex = file.Service.IndexOf(service);

        // Wrapper messages are named after the rpc whose result they carry.
        var wrapperNames = service.Method.Select(m => m.Name + "Response").ToHashSet();
        var byName = file.MessageType.ToDictionary(m => m.Name);

        if (!service.Method.Any(m => m.Name == "Subscribe" && m.ServerStreaming && m.InputType == prefix + "SubscribeRequest" && m.OutputType == prefix + "AgentEvent"))
            errors.Add("rpc Subscribe(SubscribeRequest) returns (stream AgentEvent) is required.");

        // Events: AgentEvent's oneof alternatives other than `connected`.
        var events = new List<(string Field, string Message)>();
        if (!byName.TryGetValue("AgentEvent", out var agentEvent)) errors.Add("message AgentEvent is missing.");
        else
        {
            if (agentEvent.OneofDecl.Count != 1 || agentEvent.Field.Any(f => !f.HasOneofIndex))
                errors.Add("message AgentEvent must contain exactly one oneof holding all its fields.");
            foreach (var f in agentEvent.Field.Where(f => f.Name != "connected"))
            {
                var type = f.Type == FieldDescriptorProto.Types.Type.Message ? Local(f.TypeName) : null;
                if (type is null || !type.EndsWith("Event", StringComparison.Ordinal))
                    errors.Add($"AgentEvent.{f.Name} must be a message whose name ends in Event.");
                else events.Add((f.Name, type));
            }
        }

        // Messages
        for (var i = 0; i < file.MessageType.Count; i++)
        {
            var m = file.MessageType[i];
            if (Transport.Contains(m.Name) || wrapperNames.Contains(m.Name)) continue;
            var fields = new List<Field>();
            foreach (var f in m.Field)
            {
                var repeated = f.Label == FieldDescriptorProto.Types.Label.Repeated;
                var optional = f.Proto3Optional;
                string? message = null;
                Kind kind;
                switch (f.Type)
                {
                    case FieldDescriptorProto.Types.Type.String when f.Name.EndsWith("_json", StringComparison.Ordinal):
                        kind = Kind.Json;
                        break;
                    case FieldDescriptorProto.Types.Type.String:
                        kind = Kind.String;
                        break;
                    case FieldDescriptorProto.Types.Type.Bool:
                        kind = Kind.Bool;
                        break;
                    case FieldDescriptorProto.Types.Type.Message when Local(f.TypeName) is { } local:
                        kind = Kind.Message;
                        message = local;
                        break;
                    default:
                        errors.Add($"{m.Name}.{f.Name}: unsupported type {f.Type} (use string, bool, a message of this package, or repeated message).");
                        continue;
                }
                if (repeated && kind != Kind.Message) errors.Add($"{m.Name}.{f.Name}: only message fields can be repeated.");
                if (repeated && optional) errors.Add($"{m.Name}.{f.Name}: a repeated field cannot be optional.");
                if (kind is Kind.Bool && optional) errors.Add($"{m.Name}.{f.Name}: optional bool is not supported.");

                var json = string.IsNullOrEmpty(f.JsonName) ? Naming.Camel(f.Name) : f.JsonName;
                if (kind == Kind.Json && json.EndsWith("Json", StringComparison.Ordinal)) json = json[..^4];
                fields.Add(new Field(f.Name, Naming.Pascal(f.Name), char.ToUpperInvariant(json[0]) + json[1..], kind, repeated, optional, message));
            }
            model.Messages.Add(new Msg(m.Name, "Fdc3" + m.Name, fields, DocOf(4, i)));
        }

        var messages = model.Messages.ToDictionary(m => m.Name);
        foreach (var (field, message) in events)
        {
            if (!messages.TryGetValue(message, out var msg)) { errors.Add($"event message {message} is missing."); continue; }
            model.Events.Add(new Ev(msg, field, Naming.Pascal(field)));
        }

        // Rpcs
        for (var i = 0; i < service.Method.Count; i++)
        {
            var rpc = service.Method[i];
            if (rpc.Name == "Subscribe") continue;
            if (rpc.ClientStreaming || rpc.ServerStreaming) errors.Add($"rpc {rpc.Name} must be unary.");

            var request = rpc.InputType == ".google.protobuf.Empty" ? null : Local(rpc.InputType);
            if (rpc.InputType != ".google.protobuf.Empty" && (request is null || !messages.ContainsKey(request)))
                errors.Add($"rpc {rpc.Name}: request type {rpc.InputType} must be google.protobuf.Empty or a message of this package.");

            var isAsync = rpc.Options is not null && rpc.Options.HasExtension(asyncOption) && rpc.Options.GetExtension(asyncOption);
            string? result = null;
            string? wrapper = null;
            Shape shape;
            if (rpc.OutputType == ".google.protobuf.Empty") shape = Shape.Void;
            else if (Local(rpc.OutputType) is { } output && output == rpc.Name + "Response" && byName.TryGetValue(output, out var w))
            {
                wrapper = output;
                var f = w.Field.Count == 1 ? w.Field[0] : null;
                if (f is { Name: "items", Type: FieldDescriptorProto.Types.Type.Message } && f.Label == FieldDescriptorProto.Types.Label.Repeated && Local(f.TypeName) is { } item)
                    (shape, result) = (Shape.Array, item);
                else if (f is { Name: "result", Type: FieldDescriptorProto.Types.Type.Message, Proto3Optional: true } && Local(f.TypeName) is { } single)
                    (shape, result) = (Shape.Nullable, single);
                else if (f is { Name: "result_json", Type: FieldDescriptorProto.Types.Type.String, Proto3Optional: true })
                    shape = Shape.NullableJson;
                else
                {
                    errors.Add($"{output} must have exactly one field: `repeated Message items`, `optional Message result` or `optional string result_json`.");
                    shape = Shape.Direct;
                }
            }
            else if (Local(rpc.OutputType) is { } direct && messages.ContainsKey(direct)) (shape, result) = (Shape.Direct, direct);
            else
            {
                errors.Add($"rpc {rpc.Name}: response type {rpc.OutputType} must be google.protobuf.Empty or a message of this package.");
                shape = Shape.Void;
            }
            model.Rpcs.Add(new Rpc(rpc.Name, request, result, shape, isAsync, wrapper, DocOf(6, serviceIndex, 2, i)));
        }

        // Every contract message must be reachable from an rpc or an event; otherwise it is dead weight.
        var used = new HashSet<string>();
        void Use(string? name)
        {
            if (name is null || !used.Add(name) || !messages.TryGetValue(name, out var msg)) return;
            foreach (var f in msg.Fields) Use(f.Message);
        }
        foreach (var r in model.Rpcs) { Use(r.Request); Use(r.Result); }
        foreach (var e in model.Events) Use(e.Message.Name);
        foreach (var m in model.Messages.Where(m => !used.Contains(m.Name)))
            errors.Add($"message {m.Name} is not used by any rpc or event; remove it.");
        foreach (var w in wrapperNames.Where(byName.ContainsKey).Where(n => model.Rpcs.All(r => r.Wrapper != n)))
            errors.Add($"message {w} looks like a wrapper but rpc {w[..^8]} does not return it.");

        return (model, errors);
    }

    private static string Clean(string comment) =>
        string.Join("\n", comment.TrimEnd().Split('\n').Select(l => l.StartsWith(' ') ? l[1..] : l));
}

static class Naming
{
    /// <summary>protoc's C# property name for a proto field: "app_id" -> "AppId".</summary>
    public static string Pascal(string snake) =>
        string.Concat(snake.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(s => char.ToUpperInvariant(s[0]) + s[1..]));

    /// <summary>protoc's default json_name: "app_id" -> "appId".</summary>
    public static string Camel(string snake)
    {
        var pascal = Pascal(snake);
        return char.ToLowerInvariant(pascal[0]) + pascal[1..];
    }
}

abstract class CodeWriter
{
    private readonly StringBuilder _sb = new();
    private int _indent;

    protected void L(string text = "") => _sb.Append(text.Length == 0 ? "" : new string(' ', _indent * 4) + text).Append('\n');
    protected void Open() { L("{"); _indent++; }
    protected void Close(string suffix = "") { _indent--; L("}" + suffix); }
    protected string Result => _sb.ToString();

    protected void Doc(string? comment)
    {
        if (string.IsNullOrWhiteSpace(comment)) return;
        L("/// <summary>");
        foreach (var line in comment.Split('\n'))
            L(("/// " + line.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")).TrimEnd());
        L("/// </summary>");
    }
}

/// <summary>Fdc3* records and IFdc3Commands: what DesktopAgent, Fdc3Connection and the web bridge are written against.</summary>
sealed class ContractWriter(ProtoModel model) : CodeWriter
{
    public string Generate()
    {
        L("// <auto-generated>");
        L("// Generated by Bermuda.Fdc3.Grpc.Generator from src/Bermuda.Fdc3.Proto/Protos/fdc3.proto. Do not edit:");
        L("// change the proto and rebuild Bermuda.Fdc3.Proto.");
        L("// </auto-generated>");
        L("#nullable enable");
        L("using System.Text.Json;");
        L();
        L("namespace Bermuda.IpcContract;");
        L();

        foreach (var m in model.Messages)
        {
            var ev = model.Events.FirstOrDefault(e => e.Message == m);
            Doc(m.Doc);
            if (ev is not null) L("[IpcType]");
            L($"public sealed record {m.Record}({Parameters(m)}){(ev is null ? ";" : "")}");
            if (ev is not null)
            {
                Open();
                L($"public string Type => \"fdc3.{ev.Field}\";");
                Close();
            }
            L();
        }

        L("/// <summary>");
        L("/// The fdc3.* commands: one per FDC3 DesktopAgent / Channel / PrivateChannel / IntentResolution call,");
        L("/// plus SendIntentResult, which reports an intent handler's outcome back. Implemented per app instance");
        L("/// by <c>Bermuda.Fdc3.Fdc3Connection</c>. Methods throw <c>Fdc3Exception</c>, whose message is the");
        L("/// standard FDC3 error string.");
        L("/// </summary>");
        L("[IpcCommands(\"fdc3\")]");
        L("public interface IFdc3Commands");
        Open();
        foreach (var rpc in model.Rpcs)
        {
            Doc(rpc.Doc);
            var returns = rpc.Shape switch
            {
                Shape.Void => rpc.Async ? "Task" : "void",
                _ => rpc.Async ? $"Task<{ReturnType(rpc)}>" : ReturnType(rpc),
            };
            var parameter = rpc.Request is null ? "" : $"Fdc3{rpc.Request} request";
            L($"{returns} {rpc.Name}{(rpc.Async ? "Async" : "")}({parameter});");
        }
        Close();
        return Result;
    }

    private static string ReturnType(Rpc rpc) => rpc.Shape switch
    {
        Shape.Array => $"Fdc3{rpc.Result}[]",
        Shape.Nullable => $"Fdc3{rpc.Result}?",
        Shape.NullableJson => "JsonElement?",
        _ => $"Fdc3{rpc.Result}",
    };

    private static string Parameters(Msg m)
    {
        var parts = m.Fields.Select(f => (Type: TypeOf(f), f.Prop, f.Nullable)).ToList();
        var firstDefault = parts.Count;
        while (firstDefault > 0 && parts[firstDefault - 1].Nullable) firstDefault--;
        return string.Join(", ", parts.Select((p, i) => $"{p.Type} {p.Prop}{(i >= firstDefault ? " = null" : "")}"));
    }

    private static string TypeOf(Field f) => f.Kind switch
    {
        Kind.String => f.Nullable ? "string?" : "string",
        Kind.Bool => "bool",
        Kind.Json => f.Nullable ? "JsonElement?" : "JsonElement",
        _ => "Fdc3" + f.Message + (f.Repeated ? "[]" : f.Nullable ? "?" : ""),
    };
}

/// <summary>The gRPC side: proto message <-> record mapping, and the service overrides that call Fdc3Connection.</summary>
sealed class GlueWriter(ProtoModel model) : CodeWriter
{
    public string Generate()
    {
        L("// <auto-generated>");
        L("// Generated by Bermuda.Fdc3.Grpc.Generator from src/Bermuda.Fdc3.Proto/Protos/fdc3.proto. Do not edit.");
        L("// </auto-generated>");
        L("#nullable enable");
        L("using Bermuda.IpcContract;");
        L("using Google.Protobuf.WellKnownTypes;");
        L("using Grpc.Core;");
        L("using Proto = Bermuda.Fdc3.Grpc.Proto;");
        L();
        L("namespace Bermuda.Fdc3.Grpc;");
        L();
        L("internal static class GeneratedMapping");
        Open();
        foreach (var m in model.Messages)
        {
            if (model.Events.All(e => e.Message != m)) WriteToContract(m);
            WriteToProto(m);
        }

        L("/// <summary>Maps what the agent posts to a sink; null for a message type this transport doesn't know.</summary>");
        L("public static Proto.AgentEvent? ToProtoEvent(object message) => message switch");
        Open();
        foreach (var e in model.Events)
            L($"{e.Message.Record} e => new Proto.AgentEvent {{ {e.ProtoCs} = ToProto(e) }},");
        L("_ => null,");
        Close(";");
        Close();
        L();

        L("internal sealed partial class Fdc3AgentService");
        Open();
        foreach (var rpc in model.Rpcs) WriteRpc(rpc);
        Close();
        return Result;
    }

    private void WriteToContract(Msg m)
    {
        L($"public static {m.Record} ToContract(Proto.{m.Name}? p)");
        Open();
        L("if (p is null) throw new Fdc3Exception(Fdc3Errors.InvalidArguments);");
        L($"return new {m.Record}(");
        for (var i = 0; i < m.Fields.Count; i++)
            L("    " + ToContractArg(m.Fields[i]) + (i < m.Fields.Count - 1 ? "," : ");"));
        Close();
        L();
    }

    private static string ToContractArg(Field f)
    {
        var p = "p." + f.ProtoCs;
        var has = "p.Has" + f.ProtoCs;
        if (f.Repeated) return $"{p}.Select(ToContract).ToArray()";
        return f.Kind switch
        {
            Kind.String => f.Optional ? $"{has} ? {p} : null" : p,
            Kind.Bool => p,
            Kind.Json => f.Optional ? $"{has} ? Mapping.ParseContext({p}) : null" : $"Mapping.ParseContext({p})",
            _ => f.Optional ? $"{p} is null ? null : ToContract({p})" : $"ToContract({p})",
        };
    }

    private void WriteToProto(Msg m)
    {
        L($"public static Proto.{m.Name} ToProto({m.Record} r)");
        Open();
        L($"var m = new Proto.{m.Name}();");
        foreach (var f in m.Fields)
        {
            var r = "r." + f.Prop;
            var target = "m." + f.ProtoCs;
            if (f.Repeated) { L($"{target}.AddRange({r}.Select(ToProto));"); continue; }
            switch (f.Kind)
            {
                case Kind.Json when f.Optional: L($"if ({r} is {{ }} {f.ProtoCs.ToLowerInvariant()}) {target} = {f.ProtoCs.ToLowerInvariant()}.GetRawText();"); break;
                case Kind.Json: L($"{target} = {r}.GetRawText();"); break;
                case Kind.Message when f.Optional: L($"if ({r} is not null) {target} = ToProto({r});"); break;
                case Kind.Message: L($"{target} = ToProto({r});"); break;
                default: L(f.Optional ? $"if ({r} is not null) {target} = {r};" : $"{target} = {r};"); break;
            }
        }
        L("return m;");
        Close();
        L();
    }

    private void WriteRpc(Rpc rpc)
    {
        var requestType = rpc.Request is null ? "Empty" : "Proto." + rpc.Request;
        var responseType = rpc.Shape == Shape.Void ? "Empty" : "Proto." + (rpc.Wrapper ?? rpc.Result);
        var call = $"Caller(context).{rpc.Name}{(rpc.Async ? "Async" : "")}({(rpc.Request is null ? "" : "GeneratedMapping.ToContract(request)")})";

        L($"public override {(rpc.Async ? "async " : "")}Task<{responseType}> {rpc.Name}({requestType} request, ServerCallContext context)");
        Open();
        if (rpc.Shape == Shape.Void)
        {
            L(rpc.Async ? $"await {call};" : $"{call};");
            L(rpc.Async ? "return new Empty();" : "return Task.FromResult(new Empty());");
            Close();
            L();
            return;
        }

        L($"var result = {(rpc.Async ? "await " : "")}{call};");
        string response;
        switch (rpc.Shape)
        {
            case Shape.Direct:
                response = "GeneratedMapping.ToProto(result)";
                break;
            case Shape.Array:
                L($"var response = new Proto.{rpc.Wrapper}();");
                L("response.Items.AddRange(result.Select(GeneratedMapping.ToProto));");
                response = "response";
                break;
            case Shape.Nullable:
                L($"var response = new Proto.{rpc.Wrapper}();");
                L("if (result is not null) response.Result = GeneratedMapping.ToProto(result);");
                response = "response";
                break;
            default:
                L($"var response = new Proto.{rpc.Wrapper}();");
                L("if (result is { } value) response.ResultJson = value.GetRawText();");
                response = "response";
                break;
        }
        L(rpc.Async ? $"return {response};" : $"return Task.FromResult({response});");
        Close();
        L();
    }
}
