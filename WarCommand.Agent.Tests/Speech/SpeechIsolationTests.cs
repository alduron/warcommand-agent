using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using WarCommand.Agent.Speech;

namespace WarCommand.Agent.Tests.Speech;

/// <summary>
/// Audio never touches disk and never crosses the network, and there is no debug flag that changes
/// that, because a debug flag that writes audio will eventually be on in a shipped build.
/// </summary>
/// <remarks>
/// This is the structural form of that promise. It reads the compiled assembly's type and member
/// references rather than its source, so a helper three calls deep is caught as readily as a
/// <c>File.WriteAllBytes</c> in the buffer itself. A comment is not enforcement.
/// </remarks>
public class SpeechIsolationTests
{
    /// <summary>Types that can put bytes on a disk or a socket. None may be referenced at all.</summary>
    private static readonly (string Namespace, string Name)[] ForbiddenTypes =
    [
        ("System.IO", "File"),
        ("System.IO", "FileStream"),
        ("System.IO", "FileInfo"),
        ("System.IO", "StreamWriter"),
        ("System.IO", "BinaryWriter"),
        ("System.IO", "TextWriter"),
        ("System.IO", "Stream"),
        ("System.IO", "MemoryStream"),
        ("System.IO.Compression", "GZipStream"),
    ];

    /// <summary>Namespaces that are entirely off limits.</summary>
    private static readonly string[] ForbiddenNamespaces =
    [
        "System.Net",
        "System.IO.Pipes",
        "System.IO.MemoryMappedFiles",
        "System.IO.IsolatedStorage",
    ];

    /// <summary>
    /// The only file-system members the assembly may touch, and both are reads. The Vosk model
    /// directory has to be checked before the native loader is handed it, because a bad path there
    /// returns a null handle and faults the process on the next call.
    /// </summary>
    private static readonly (string Type, string Member)[] AllowedIoMembers =
    [
        ("Directory", "Exists"),
        ("Path", "Combine"),
    ];

    [Fact]
    public void The_speech_assembly_references_no_file_write_and_no_network_api()
    {
        var violations = WithMetadata(reader =>
        {
            var found = new List<string>();
            foreach (var handle in reader.TypeReferences)
            {
                var reference = reader.GetTypeReference(handle);
                var space = reader.GetString(reference.Namespace);
                var name = reader.GetString(reference.Name);

                if (ForbiddenNamespaces.Any(banned =>
                        space.Equals(banned, StringComparison.Ordinal)
                        || space.StartsWith(banned + ".", StringComparison.Ordinal)))
                {
                    found.Add($"{space}.{name}");
                }

                if (ForbiddenTypes.Any(t =>
                        t.Namespace.Equals(space, StringComparison.Ordinal)
                        && t.Name.Equals(name, StringComparison.Ordinal)))
                {
                    found.Add($"{space}.{name}");
                }
            }

            return found;
        });

        Assert.True(
            violations.Count == 0,
            "Audio never touches disk and never crosses the network.\n"
            + string.Join("\n", violations.Distinct(StringComparer.Ordinal)));
    }

    [Fact]
    public void The_only_file_system_members_it_touches_are_reads()
    {
        var violations = WithMetadata(reader =>
        {
            var found = new List<string>();
            foreach (var handle in reader.MemberReferences)
            {
                var member = reader.GetMemberReference(handle);
                if (member.Parent.Kind != HandleKind.TypeReference)
                {
                    continue;
                }

                var parent = reader.GetTypeReference((TypeReferenceHandle)member.Parent);
                var space = reader.GetString(parent.Namespace);
                if (!space.Equals("System.IO", StringComparison.Ordinal))
                {
                    continue;
                }

                var type = reader.GetString(parent.Name);
                var name = reader.GetString(member.Name);
                if (!AllowedIoMembers.Any(a =>
                        a.Type.Equals(type, StringComparison.Ordinal)
                        && a.Member.Equals(name, StringComparison.Ordinal)))
                {
                    found.Add($"System.IO.{type}.{name}");
                }
            }

            return found;
        });

        Assert.True(
            violations.Count == 0,
            "Only a model-directory existence check is allowed to reach the file system here.\n"
            + string.Join("\n", violations.Distinct(StringComparer.Ordinal)));
    }

    [Fact]
    public void The_audio_buffer_has_no_path_no_stream_and_no_serializer()
    {
        var offenders = new List<string>();

        foreach (var member in typeof(AudioBuffer).GetMembers(
                     BindingFlags.Public | BindingFlags.NonPublic
                     | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            if (member.Name.Contains("save", StringComparison.OrdinalIgnoreCase)
                || member.Name.Contains("write", StringComparison.OrdinalIgnoreCase)
                || member.Name.Contains("file", StringComparison.OrdinalIgnoreCase)
                || member.Name.Contains("path", StringComparison.OrdinalIgnoreCase)
                || member.Name.Contains("serial", StringComparison.OrdinalIgnoreCase)
                || member.Name.Contains("stream", StringComparison.OrdinalIgnoreCase))
            {
                offenders.Add(member.Name);
            }

            foreach (var type in TypesOf(member))
            {
                if (typeof(System.IO.Stream).IsAssignableFrom(type) || type == typeof(Uri))
                {
                    offenders.Add($"{member.Name}: {type.Name}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "AudioBuffer must carry no member through which audio could leave the process.\n"
            + string.Join("\n", offenders));
    }

    [Fact]
    public void The_engine_contract_is_the_one_method_the_spec_writes()
    {
        var methods = typeof(ISpeechEngine).GetMethods();
        var recognize = Assert.Single(methods);

        Assert.Equal("RecognizeAsync", recognize.Name);
        Assert.Equal(typeof(Task<WarCommand.Agent.Core.Grammar.Utterance>), recognize.ReturnType);
        Assert.Equal(
            new[]
            {
                typeof(AudioBuffer),
                typeof(WarCommand.Agent.Core.Grammar.Grammar),
                typeof(CancellationToken),
            },
            recognize.GetParameters().Select(p => p.ParameterType));
    }

    private static List<string> WithMetadata(Func<MetadataReader, List<string>> read)
    {
        var bytes = System.IO.File.ReadAllBytes(typeof(AudioBuffer).Assembly.Location);
        using var stream = new System.IO.MemoryStream(bytes);
        using var portable = new PEReader(stream);
        return read(portable.GetMetadataReader());
    }

    private static IEnumerable<Type> TypesOf(MemberInfo member) => member switch
    {
        PropertyInfo property => [property.PropertyType],
        FieldInfo field => [field.FieldType],
        MethodInfo method => method.GetParameters().Select(p => p.ParameterType).Append(method.ReturnType),
        ConstructorInfo constructor => constructor.GetParameters().Select(p => p.ParameterType),
        _ => [],
    };
}
