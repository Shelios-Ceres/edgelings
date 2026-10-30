using System.Text.Json.Serialization;
using AgentIsland.Cost;

namespace AgentIsland.Core;

/// Source-generated JSON for the types Core writes and reads itself. Under
/// NativeAOT (the macOS bridge) reflection-based serialisation is off, so these
/// calls go through a context. Preferences has its own (PreferencesJson).
[JsonSerializable(typeof(Dictionary<string, LogParseCache.Entry>), TypeInfoPropertyName = "ParseCache")]
[JsonSerializable(typeof(Dictionary<string, Dictionary<string, string>>), TypeInfoPropertyName = "ModelMaps")]
[JsonSerializable(typeof(Dictionary<string, string>), TypeInfoPropertyName = "StringMap")]
[JsonSerializable(typeof(string))]
internal sealed partial class CoreJson : JsonSerializerContext;
