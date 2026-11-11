using System.Reflection;
using System.Text;
using System.Text.Json;
using Bermuda.IpcContract;

// Usage: Bermuda.Fdc3.Grpc.Generator <output directory>
//
// Contract -> gRPC rules
//   record Fdc3Foo(...)            -> message Foo (fields numbered in constructor order, so only ever append)
//   string / bool                  -> string / bool; a nullable string becomes `optional`
//   JsonElement                    -> string <name>_json (JSON text); nullable -> `optional`
//   object? (event details)        -> optional string <name>_json (serialised with the web options)
//   record                         -> message reference;  Foo[] -> repeated
//   method M(Req) -> Resp          -> rpc M(Req) returns (Resp); no parameter / void -> google.protobuf.Empty;
//                                     array / nullable results are wrapped in <M>Response { items | result }
//   [IpcType] Fdc3*Event records   -> alternatives of AgentEvent's oneof
var outDir = args.Length > 0 ? args[0] : throw new ArgumentException("Pass the output directory.");
Directory.CreateDirectory(outDir);

var model = new ContractModel(typeof(IFdc3Commands));
Write("fdc3.proto", new ProtoWriter(model).Generate());
Write("Fdc3Generated.g.cs", new CSharpWriter(model).Generate());
return 0;

void Write(string name, string content)
{
    var path = Path.Combine(outDir, name);
    content = content.Replace("\r\n", "\n");
    // Don't touch an unchanged file, so incremental builds stay incremental.
    if (!File.Exists(path) || File.ReadAllText(path) != content) File.WriteAllText(path, content);
}

enum Kind { String, Bool, Json, Object, Message }

sealed record Field(string ClrName, string ProtoName, string CsName, Kind Kind, bool IsArray, bool Optional, Type? MessageType)
{
    public bool IsScalarOptional => Optional && Kind is Kind.String or Kind.Json or Kind.Object;
}

sealed record Message(Type Type, string Name, List<Field> Fields, ConstructorInfo Constructor);

sealed record Method(MethodInfo Info, string RpcName, Type? Param, Type? Result, bool ResultIsArray, bool ResultNullable, bool IsAsync, bool IsVoid)
{
    public bool NeedsWrapper => !IsVoid && (ResultIsArray || ResultNullable);
    public string WrapperName => RpcName + "Response";
}

sealed class ContractModel
{
    private readonly NullabilityInfoContext _nullability = new();

    public List<Message> Messages { get; } = [];
    public List<Method> Methods { get; } = [];
    public List<Message> Events { get; } = [];

    public ContractModel(Type commands)
    {
        foreach (var info in commands.GetMethods())
        {
            var parameters = info.GetParameters();
            if (parameters.Length > 1) throw new NotSupportedException($"{info.Name}: at most one parameter.");

            var returnType = info.ReturnType;
            var isAsync = false;
            if (returnType == typeof(Task)) { returnType = typeof(void); isAsync = true; }
            else if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                returnType = returnType.GetGenericArguments()[0];
                isAsync = true;
            }

            var isVoid = returnType == typeof(void);
            var isArray = returnType.IsArray;
            var nullable = !isVoid && (Nullable.GetUnderlyingType(returnType) is not null
                || (!isAsync && !returnType.IsValueType && _nullability.Create(info.ReturnParameter).ReadState == NullabilityState.Nullable));
            var element = isVoid ? null : isArray ? returnType.GetElementType()! : Nullable.GetUnderlyingType(returnType) ?? returnType;

            if (parameters.Length == 1) Visit(parameters[0].ParameterType);
            if (element is not null && element != typeof(JsonElement)) Visit(element);

            var rpcName = info.Name.EndsWith("Async", StringComparison.Ordinal) ? info.Name[..^5] : info.Name;
            Methods.Add(new Method(info, rpcName, parameters.Length == 1 ? parameters[0].ParameterType : null,
                element, isArray, nullable, isAsync, isVoid));
        }

        foreach (var type in commands.Assembly.GetTypes()
                     .Where(t => t.GetCustomAttribute<IpcTypeAttribute>() is not null && t.Name.StartsWith("Fdc3", StringComparison.Ordinal) && t.Name.EndsWith("Event", StringComparison.Ordinal))
                     .OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            Visit(type);
            Events.Add(Messages.Single(m => m.Type == type));
        }
    }

    public static string MessageName(Type type) => type.Name.StartsWith("Fdc3", StringComparison.Ordinal) ? type.Name[4..] : type.Name;

    public Message Of(Type type) => Messages.Single(m => m.Type == type);

    private void Visit(Type type)
    {
        if (Messages.Any(m => m.Type == type)) return;
        if (type.Assembly != typeof(IFdc3Commands).Assembly || type.IsPrimitive || type == typeof(string))
            throw new NotSupportedException($"{type}: only contract records can be messages.");

        var ctor = type.GetConstructors().Single();
        var message = new Message(type, MessageName(type), [], ctor);
        Messages.Add(message);

        foreach (var p in ctor.GetParameters())
        {
            var isArray = p.ParameterType.IsArray;
            var t = isArray ? p.ParameterType.GetElementType()! : p.ParameterType;
            var underlying = Nullable.GetUnderlyingType(t);
            var optional = underlying is not null || (!t.IsValueType && _nullability.Create(p).ReadState == NullabilityState.Nullable);
            t = underlying ?? t;

            var snake = Naming.Snake(p.Name!);
            if (t == typeof(string)) message.Fields.Add(new(p.Name!, snake, Naming.Pascal(snake), Kind.String, isArray, optional, null));
            else if (t == typeof(bool)) message.Fields.Add(new(p.Name!, snake, Naming.Pascal(snake), Kind.Bool, isArray, false, null));
            else if (t == typeof(JsonElement)) message.Fields.Add(new(p.Name!, snake + "_json", Naming.Pascal(snake + "_json"), Kind.Json, isArray, optional, null));
            else if (t == typeof(object)) message.Fields.Add(new(p.Name!, snake + "_json", Naming.Pascal(snake + "_json"), Kind.Object, isArray, optional, null));
            else
            {
                Visit(t);
                message.Fields.Add(new(p.Name!, snake, Naming.Pascal(snake), Kind.Message, isArray, optional, t));
            }
        }
    }
}

static class Naming
{
    /// <summary>PascalCase -> snake_case the way protoc round-trips it ("APIs" -> "ap_is").</summary>
    public static string Snake(string name)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                var prevLowerOrDigit = i > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]));
                var acronymEnd = i > 0 && char.IsUpper(name[i - 1]) && i + 1 < name.Length && char.IsLower(name[i + 1]);
                if (prevLowerOrDigit || acronymEnd) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>protoc's C# property name for a proto field.</summary>
    public static string Pascal(string snake) =>
        string.Concat(snake.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(s => char.ToUpperInvariant(s[0]) + s[1..]));
}

sealed class ProtoWriter(ContractModel model)
{
    private readonly StringBuilder _sb = new();

    public string Generate()
    {
        Line("// <auto-generated>");
        Line("// Generated by Bermuda.Fdc3.Grpc.Generator from IFdc3Commands (src/Bermuda.IpcContract/Fdc3Contract.cs).");
        Line("// Do not edit: change the contract and rebuild Bermuda.Fdc3.Grpc.");
        Line("// </auto-generated>");
        Line("syntax = \"proto3\";");
        Line();
        Line("package bermuda.fdc3.v1;");
        Line();
        Line("import \"google/protobuf/empty.proto\";");
        Line();
        Line("option csharp_namespace = \"Bermuda.Fdc3.Grpc.Proto\";");
        Line();
        Line("// Connection model: one pipe client opens Subscribe(app_id). The first message on that stream is");
        Line("// `connected` (carrying the new instance_id); the agent's deliveries follow. Every other RPC must carry");
        Line("// the instance_id in the `x-fdc3-instance` request header and use the same pipe connection. The instance");
        Line("// leaves the agent when the Subscribe stream ends. Contexts are JSON text (*_json fields); failures are");
        Line("// gRPC errors whose status detail is the FDC3 error string (Fdc3Errors).");
        Line("service Fdc3Agent {");
        Line("  rpc Subscribe(SubscribeRequest) returns (stream AgentEvent);");
        Line();
        foreach (var m in model.Methods)
            Line($"  rpc {m.RpcName}({RequestType(m)}) returns ({ResponseType(m)});");
        Line("}");
        Line();

        foreach (var message in model.Messages) WriteMessage(message);

        foreach (var m in model.Methods.Where(m => m.NeedsWrapper))
        {
            Line($"message {m.WrapperName} {{");
            if (m.ResultIsArray) Line($"  repeated {ElementType(m)} items = 1;");
            else if (m.Result == typeof(JsonElement)) Line("  optional string result_json = 1;");
            else Line($"  {ElementType(m)} result = 1;");
            Line("}");
            Line();
        }

        Line("message SubscribeRequest { string app_id = 1; }");
        Line();
        Line("// First message of every Subscribe stream.");
        Line("message Connected {");
        Line("  string instance_id = 1;");
        Line("  string app_id = 2;");
        Line("}");
        Line();
        Line("message AgentEvent {");
        Line("  oneof event {");
        Line("    Connected connected = 1;");
        var number = 2;
        foreach (var e in model.Events)
            Line($"    {e.Name} {EventField(e)} = {number++};");
        Line("  }");
        Line("}");
        return _sb.ToString();
    }

    public static string EventField(Message e) => Naming.Snake(e.Name.EndsWith("Event", StringComparison.Ordinal) ? e.Name[..^5] : e.Name);

    public static string RequestType(Method m) => m.Param is null ? "google.protobuf.Empty" : ContractModel.MessageName(m.Param);

    public static string ResponseType(Method m) =>
        m.IsVoid ? "google.protobuf.Empty" : m.NeedsWrapper ? m.WrapperName : ContractModel.MessageName(m.Result!);

    private static string ElementType(Method m) => m.Result == typeof(JsonElement) ? "string" : ContractModel.MessageName(m.Result!);

    private void WriteMessage(Message message)
    {
        Line($"message {message.Name} {{");
        var number = 1;
        foreach (var f in message.Fields)
        {
            var type = f.Kind switch
            {
                Kind.String or Kind.Json or Kind.Object => "string",
                Kind.Bool => "bool",
                _ => ContractModel.MessageName(f.MessageType!),
            };
            var prefix = f.IsArray ? "repeated " : f.IsScalarOptional ? "optional " : "";
            Line($"  {prefix}{type} {f.ProtoName} = {number++};");
        }
        Line("}");
        Line();
    }

    private void Line(string text = "") => _sb.Append(text).Append('\n');
}

sealed class CSharpWriter(ContractModel model)
{
    private readonly StringBuilder _sb = new();
    private int _indent;

    public string Generate()
    {
        L("// <auto-generated>");
        L("// Generated by Bermuda.Fdc3.Grpc.Generator from IFdc3Commands. Do not edit.");
        L("// </auto-generated>");
        L("#nullable enable");
        L("using System.Text.Json;");
        L("using Bermuda.IpcContract;");
        L("using Google.Protobuf.WellKnownTypes;");
        L("using Grpc.Core;");
        L("using Proto = Bermuda.Fdc3.Grpc.Proto;");
        L();
        L("namespace Bermuda.Fdc3.Grpc;");
        L();
        L("internal static class GeneratedMapping");
        Open();
        foreach (var message in model.Messages)
        {
            if (!model.Events.Contains(message)) WriteToContract(message);
            WriteToProto(message);
        }

        L("/// <summary>Maps what the agent posts to a sink; null for a message type this transport doesn't know.</summary>");
        L("public static Proto.AgentEvent? ToProtoEvent(object message) => message switch");
        Open();
        foreach (var e in model.Events)
            L($"{e.Type.Name} e => new Proto.AgentEvent {{ {Naming.Pascal(ProtoWriter.EventField(e))} = ToProto(e) }},");
        L("_ => null,");
        Close(";");
        Close();
        L();

        L("internal sealed partial class Fdc3AgentService");
        Open();
        foreach (var m in model.Methods) WriteRpc(m);
        Close();
        return _sb.ToString();
    }

    private void WriteToContract(Message message)
    {
        var type = message.Type.Name;
        L($"public static {type} ToContract(Proto.{message.Name}? p)");
        Open();
        L("if (p is null) throw new Fdc3Exception(Fdc3Errors.InvalidArguments);");
        L($"return new {type}(");
        _indent++;
        var args = message.Constructor.GetParameters().Select((_, i) => ToContractArg(message.Fields[i])).ToList();
        for (var i = 0; i < args.Count; i++) L(args[i] + (i < args.Count - 1 ? "," : ");"));
        _indent--;
        Close();
        L();
    }

    private static string ToContractArg(Field f)
    {
        var p = "p." + f.CsName;
        var has = "p.Has" + f.CsName;
        if (f.IsArray) return $"{p}.Select(ToContract).ToArray()";
        return f.Kind switch
        {
            Kind.String => f.Optional ? $"{has} ? {p} : null" : p,
            Kind.Bool => p,
            Kind.Json => f.Optional ? $"{has} ? Mapping.ParseContext({p}) : null" : $"Mapping.ParseContext({p})",
            Kind.Object => throw new NotSupportedException("object fields only flow agent -> app."),
            _ => f.Optional ? $"{p} is null ? null : ToContract({p})" : $"ToContract({p})",
        };
    }

    private void WriteToProto(Message message)
    {
        L($"public static Proto.{message.Name} ToProto({message.Type.Name} r)");
        Open();
        L($"var m = new Proto.{message.Name}();");
        foreach (var f in message.Fields)
        {
            var r = "r." + f.ClrName[..1].ToUpperInvariant() + f.ClrName[1..];
            var m = "m." + f.CsName;
            if (f.IsArray) { L($"{m}.AddRange({r}.Select(ToProto));"); continue; }
            var value = f.Kind switch
            {
                Kind.String or Kind.Bool => r,
                Kind.Json => $"{r}{(f.Optional ? ".Value" : "")}.GetRawText()",
                Kind.Object => $"JsonSerializer.Serialize({r}, JsonSerializerOptions.Web)",
                _ => $"ToProto({r}{(f.MessageType!.IsValueType ? "" : "")})",
            };
            L(f.Optional ? $"if ({r} is not null) {m} = {value};" : $"{m} = {value};");
        }
        L("return m;");
        Close();
        L();
    }

    private void WriteRpc(Method m)
    {
        var req = ProtoWriter.RequestType(m).Replace("google.protobuf.Empty", "Empty");
        var resp = ProtoWriter.ResponseType(m).Replace("google.protobuf.Empty", "Empty");
        var respType = resp == "Empty" ? "Empty" : "Proto." + resp;
        var reqType = req == "Empty" ? "Empty" : "Proto." + req;
        var call = $"Caller(context).{m.Info.Name}({(m.Param is null ? "" : "GeneratedMapping.ToContract(request)")})";

        L($"public override {(m.IsAsync ? "async " : "")}Task<{respType}> {m.RpcName}({reqType} request, ServerCallContext context)");
        Open();
        if (m.IsVoid)
        {
            L(m.IsAsync ? $"await {call};" : $"{call};");
            L(m.IsAsync ? "return new Empty();" : "return Task.FromResult(new Empty());");
            Close();
            L();
            return;
        }

        L($"var result = {(m.IsAsync ? "await " : "")}{call};");
        var ret = new StringBuilder();
        if (!m.NeedsWrapper) ret.Append("GeneratedMapping.ToProto(result)");
        if (m.NeedsWrapper)
        {
            L($"var response = new Proto.{m.WrapperName}();");
            if (m.ResultIsArray) L("response.Items.AddRange(result.Select(GeneratedMapping.ToProto));");
            else if (m.Result == typeof(JsonElement)) L("if (result is { } value) response.ResultJson = value.GetRawText();");
            else L("if (result is not null) response.Result = GeneratedMapping.ToProto(result);");
            ret.Append("response");
        }
        L(m.IsAsync ? $"return {ret};" : $"return Task.FromResult({ret});");
        Close();
        L();
    }

    private void Open() { L("{"); _indent++; }
    private void Close(string suffix = "") { _indent--; L("}" + suffix); }
    private void L(string text = "") => _sb.Append(text.Length == 0 ? "" : new string(' ', _indent * 4) + text).Append('\n');
}
