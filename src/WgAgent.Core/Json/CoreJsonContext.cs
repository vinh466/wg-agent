using System.Text.Json.Serialization;
using WgAgent.Core.Model;

namespace WgAgent.Core.Json;

/// <summary>
/// Source-generated serialisation, so nothing reflects at run time under NativeAOT. Nulls are
/// written: a status field that is unknown says so (REQ-RES-023, REQ-RES-036).
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(InterfaceResource))]
[JsonSerializable(typeof(PeerResource))]
[JsonSerializable(typeof(IReadOnlyList<InterfaceResource>))]
[JsonSerializable(typeof(IReadOnlyList<PeerResource>))]
[JsonSerializable(typeof(InterfaceSpec))]
[JsonSerializable(typeof(PeerSpec))]
public sealed partial class CoreJsonContext : JsonSerializerContext;
