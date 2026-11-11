using System.Reflection;
using System.Text;
using System.Text.Json;
using Bermuda.IpcContract;
using Google.Protobuf;
using Google.Protobuf.Reflection;

// Usage: Bermuda.Fdc3.Grpc.Generator <descriptor set (.pb)> <output directory>
//
// fdc3.proto is the source of truth. This tool reads protoc's descriptor set of it, checks that the C#
// contract (IFdc3Commands and the records it uses) still matches it, and writes the glue that turns each
// gRPC call into a call on Fdc3Connection. Any mismatch fails the build with the list of differences.
// The rules it enforces are documented at the top of Protos/fdc3.proto.
if (args.Length != 2) throw new ArgumentException("Usage: <descriptor set> <output directory>");
var descriptorPath = args[0];
var outDir = args[1];
Directory.CreateDirectory(outDir);

var set = FileDescriptorSet.Parser.ParseFrom(File.ReadAllBytes(descriptorPath));
var file = set.File.Single(f => f.Name.EndsWith("fdc3.proto", StringComparison.Ordinal));
var model = new ContractModel(typeof(IFdc3Commands));
var errors = ProtoCheck.Run(model, file);
if (errors.Count > 0)
{
    Console.Error.WriteLine("fdc3.proto and the C# contract (Fdc3Contract.cs) disagree:");
    foreach (var error in errors) Console.Error.WriteLine("  - " + error);
    return 1;
}

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

static class Expect
{
    public static string EventField(Message e) => Naming.Snake(e.Name.EndsWith("Event", StringComparison.Ordinal) ? e.Name[..^5] : e.Name);

    public static string RequestType(Method m) => m.Param is null ? "google.protobuf.Empty" : ContractModel.MessageName(m.Param);

    public static string ResponseType(Method m) =>
        m.IsVoid ? "google.protobuf.Empty" : m.NeedsWrapper ? m.WrapperName : ContractModel.MessageName(m.Result!);
}

/// <summary>Checks the contract against the .proto: every difference is an error.</summary>
static class ProtoCheck
{
    public static List<string> Run(ContractModel model, FileDescriptorProto file)
    {
        var errors = new List<string>();
        var pkg = file.Package;
        string Full(string name) => name.Contains('.') ? "." + name : "." + pkg + "." + name;
        string Qualified(string name) => name == "google.protobuf.Empty" ? ".google.protobuf.Empty" : Full(name);

        var messages = file.MessageType.ToDictionary(m => m.Name);
        var used = new HashSet<string> { "SubscribeRequest", "Connected", "AgentEvent" };

        // Service
        var service = file.Service.SingleOrDefault(s => s.Name == "Fdc3Agent");
        if (service is null) { errors.Add("service Fdc3Agent is missing."); return errors; }
        var rpcs = service.Method.Where(m => m.Name != "Subscribe").ToDictionary(m => m.Name);
        if (!service.Method.Any(m => m.Name == "Subscribe" && m.ServerStreaming && m.InputType == Full("SubscribeRequest") && m.OutputType == Full("AgentEvent")))
            errors.Add("rpc Subscribe(SubscribeRequest) returns (stream AgentEvent) is required.");

        foreach (var method in model.Methods)
        {
            if (!rpcs.Remove(method.RpcName, out var rpc))
            {
                errors.Add($"IFdc3Commands.{method.Info.Name} has no rpc {method.RpcName} in the proto.");
                continue;
            }
            if (rpc.ClientStreaming || rpc.ServerStreaming) errors.Add($"rpc {rpc.Name} must be unary.");
            if (rpc.InputType != Qualified(Expect.RequestType(method)))
                errors.Add($"rpc {rpc.Name} takes {rpc.InputType.TrimStart('.')}; the contract expects {Expect.RequestType(method)}.");
            if (rpc.OutputType != Qualified(Expect.ResponseType(method)))
                errors.Add($"rpc {rpc.Name} returns {rpc.OutputType.TrimStart('.')}; the contract expects {Expect.ResponseType(method)}.");

            if (method.NeedsWrapper)
            {
                used.Add(method.WrapperName);
                if (!messages.TryGetValue(method.WrapperName, out var wrapper)) { errors.Add($"message {method.WrapperName} is missing."); continue; }
                var elementType = method.Result == typeof(JsonElement) ? null : Full(ContractModel.MessageName(method.Result!));
                if (method.ResultIsArray)
                    ExpectFields(errors, wrapper, ("items", FieldDescriptorProto.Types.Type.Message, elementType, true, false));
                else if (method.Result == typeof(JsonElement))
                    ExpectFields(errors, wrapper, ("result_json", FieldDescriptorProto.Types.Type.String, null, false, true));
                else
                    ExpectFields(errors, wrapper, ("result", FieldDescriptorProto.Types.Type.Message, elementType, false, false));
            }
        }
        foreach (var extra in rpcs.Keys) errors.Add($"rpc {extra} has no matching method on IFdc3Commands.");

        // Messages
        foreach (var message in model.Messages)
        {
            used.Add(message.Name);
            if (!messages.TryGetValue(message.Name, out var proto)) { errors.Add($"message {message.Name} is missing (contract record {message.Type.Name})."); continue; }
            var expected = message.Fields.Select(f => (
                f.ProtoName,
                f.Kind switch
                {
                    Kind.Bool => FieldDescriptorProto.Types.Type.Bool,
                    Kind.Message => FieldDescriptorProto.Types.Type.Message,
                    _ => FieldDescriptorProto.Types.Type.String,
                },
                f.Kind == Kind.Message ? Full(ContractModel.MessageName(f.MessageType!)) : null,
                f.IsArray,
                f.IsScalarOptional)).ToArray();
            ExpectFields(errors, proto, expected);
        }

        // Events
        if (!messages.TryGetValue("AgentEvent", out var agentEvent)) errors.Add("message AgentEvent is missing.");
        else
        {
            var expected = new List<(string, FieldDescriptorProto.Types.Type, string?, bool, bool)>
            {
                ("connected", FieldDescriptorProto.Types.Type.Message, Full("Connected"), false, false),
            };
            expected.AddRange(model.Events.Select(e => (Expect.EventField(e), FieldDescriptorProto.Types.Type.Message, (string?)Full(e.Name), false, false)));
            ExpectFields(errors, agentEvent, expected.ToArray());
            if (agentEvent.OneofDecl.Count != 1 || agentEvent.Field.Any(f => !f.HasOneofIndex))
                errors.Add("message AgentEvent: all fields must be alternatives of a single oneof.");
        }

        foreach (var name in messages.Keys.Where(n => !used.Contains(n)))
            errors.Add($"message {name} is not used by any rpc, record or event; remove it or add it to the contract.");
        return errors;
    }

    private static void ExpectFields(
        List<string> errors,
        DescriptorProto message,
        params (string Name, FieldDescriptorProto.Types.Type Type, string? TypeName, bool Repeated, bool Optional)[] expected)
    {
        var actual = message.Field.ToDictionary(f => f.Name);
        foreach (var e in expected)
        {
            if (!actual.Remove(e.Name, out var f))
            {
                errors.Add($"message {message.Name}: field {e.Name} is in the contract but not in the proto.");
                continue;
            }
            if (f.Type != e.Type || (e.TypeName is not null && f.TypeName != e.TypeName))
                errors.Add($"message {message.Name}.{e.Name}: proto has {Describe(f)}; the contract expects {(e.TypeName?.TrimStart('.') ?? e.Type.ToString().ToLowerInvariant())}.");
            if ((f.Label == FieldDescriptorProto.Types.Label.Repeated) != e.Repeated)
                errors.Add($"message {message.Name}.{e.Name}: {(e.Repeated ? "must be" : "must not be")} repeated (the contract {(e.Repeated ? "has an array" : "has a single value")}).");
            if (e.Type != FieldDescriptorProto.Types.Type.Message && !e.Repeated && f.Proto3Optional != e.Optional)
                errors.Add($"message {message.Name}.{e.Name}: {(e.Optional ? "must be" : "must not be")} `optional` (the contract value {(e.Optional ? "is" : "is not")} nullable).");
        }
        foreach (var extra in actual.Keys)
            errors.Add($"message {message.Name}: field {extra} is in the proto but not in the contract record.");
    }

    private static string Describe(FieldDescriptorProto f) =>
        f.Type == FieldDescriptorProto.Types.Type.Message ? f.TypeName.TrimStart('.') : f.Type.ToString().ToLowerInvariant();
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
            L($"{e.Type.Name} e => new Proto.AgentEvent {{ {Naming.Pascal(Expect.EventField(e))} = ToProto(e) }},");
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
        var req = Expect.RequestType(m).Replace("google.protobuf.Empty", "Empty");
        var resp = Expect.ResponseType(m).Replace("google.protobuf.Empty", "Empty");
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
