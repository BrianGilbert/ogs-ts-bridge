#nullable enable
using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace OgsTsBridge.Sources
{
/// <summary>A JSON key with a standard channel: OGS name, unit, and the
/// factor from the game's unit (value × Scale + Bias).</summary>
public readonly record struct JsonKey(string Path, string Channel, string Unit, string Description,
                                      double Scale = 1, double Bias = 0);

/// <summary>
/// Maps every value of a JSON document to channels, learning the key set
/// from the stream — for games whose telemetry is a JSON object (UDP
/// datagrams or local HTTP replies). Nested objects and arrays flatten to
/// dotted paths ("Rotation.Yaw", "Wheels.0.Speed"). Numbers and numeric
/// strings become number channels, true/false boolean channels, other
/// strings session text. Keys listed in the known table get their
/// standard name, unit and scale; every other key is published raw as
/// "&lt;prefix&gt;.&lt;path&gt;".
///
/// <see cref="Read"/> parses with Utf8JsonReader and a path hash, so a
/// document whose keys are all known costs no allocation. A key not seen
/// before makes Read return true: the source then redeclares (cold path,
/// once per new key) and reads the document again. Sources hand-map pose
/// and dynamics from <see cref="Watch"/> slots.
/// </summary>
public sealed class JsonChannels
{
    enum Kind : byte { Number, Boolean, Text }

    sealed class Key
    {
        public required byte[] Path;
        public Kind Kind;
        public OgsChannel Channel;
        public double Scale = 1, Bias;
        public bool Ignored, Declared;
    }

    readonly string _prefix;
    readonly Dictionary<string, JsonKey> _known = new(StringComparer.Ordinal);
    readonly Dictionary<ulong, Key> _byHash = new();
    readonly List<Key> _keys = new();             // declaration order
    readonly List<Key> _pending = new();
    readonly HashSet<string> _ignored = new(StringComparer.Ordinal);
    readonly List<byte[]> _watchPaths = new();
    double[] _watch = Array.Empty<double>();
    readonly byte[] _path = new byte[512];
    readonly int[] _segStart = new int[32];
    readonly int[] _arrayIndex = new int[32];

    readonly Func<string, JsonKey?>? _resolve;

    /// <param name="resolve">Maps a key outside the known table (e.g. one
    /// carrying its unit in its name) to a channel, or null to publish it
    /// raw. Called once per new key, cold.</param>
    public JsonChannels(string prefix, IEnumerable<JsonKey>? known = null, Func<string, JsonKey?>? resolve = null)
    {
        _prefix = prefix;
        _resolve = resolve;
        if (known is not null)
            foreach (var k in known) _known[k.Path] = k;
    }

    /// <summary>Keys left out of the channel set (read by the source only,
    /// or meaningless — e.g. a validity flag).</summary>
    public void Ignore(string path) => _ignored.Add(path);

    /// <summary>A slot whose value <see cref="Read"/> updates (NaN until
    /// the key arrives) — for the pose and dynamics a source maps by hand.
    /// Call before the first Read.</summary>
    public int Watch(string path)
    {
        _watchPaths.Add(Encoding.UTF8.GetBytes(path));
        Array.Resize(ref _watch, _watchPaths.Count);
        _watch[^1] = double.NaN;
        return _watchPaths.Count - 1;
    }

    public double this[int watch] => _watch[watch];

    /// <summary>Number of keys learned so far.</summary>
    public int Count => _keys.Count;

    /// <summary>Declares a channel for every key learned so far, in the
    /// order they first arrived. Call from the source's Declare.</summary>
    public void Declare(OgsSession session)
    {
        foreach (var key in _keys)
        {
            key.Declared = true;
            var path = Encoding.UTF8.GetString(key.Path);
            JsonKey? match = _known.TryGetValue(path, out var known) ? known : _resolve?.Invoke(path);
            if (match is { } k)
            {
                key.Scale = k.Scale;
                key.Bias = k.Bias;
                key.Channel = key.Kind == Kind.Boolean
                    ? session.AddBooleanChannel(k.Channel, k.Description)
                    : key.Kind == Kind.Text
                        ? session.AddSessionText(k.Channel, k.Description)
                        : session.AddChannel(k.Channel, k.Unit, k.Description);
                continue;
            }
            var name = _prefix + "." + Sanitize(path);
            var description = $"'{path}' (raw)";
            key.Channel = key.Kind switch
            {
                Kind.Boolean => session.AddBooleanChannel(name, description),
                Kind.Text => session.AddSessionText(name, description),
                _ => session.AddChannel(name, "", description),
            };
        }
    }

    /// <summary>Reads one document into the session's channels and the
    /// watch slots. Returns true when it carried keys not seen before —
    /// the caller then redeclares and calls Read again. Malformed JSON
    /// stops the read; values read up to that point stand.</summary>
    public bool Read(ReadOnlySpan<byte> json, OgsSession session)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });
        // containers open: _segStart[i] is where container i's child
        // segments start in _path (after its '.'), _arrayIndex[i] its next
        // element index (−1 for objects)
        int depth = 0;
        int pathLength = 0;

        try
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.PropertyName:
                        pathLength = Segment(depth, ref reader);
                        continue;

                    case JsonTokenType.StartObject:
                    case JsonTokenType.StartArray:
                        if (depth == _segStart.Length) return Finish();
                        int childStart = 0;
                        if (depth > 0)
                        {
                            if (_arrayIndex[depth - 1] >= 0) pathLength = Index(depth);
                            childStart = Math.Min(pathLength + 1, _path.Length - 1);
                        }
                        _segStart[depth] = childStart;
                        _arrayIndex[depth] = reader.TokenType == JsonTokenType.StartArray ? 0 : -1;
                        depth++;
                        continue;

                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        if (depth > 0) depth--;
                        continue;
                }

                // a value: inside an array it is named by its index
                if (depth == 0) continue;
                if (_arrayIndex[depth - 1] >= 0) pathLength = Index(depth);
                Value(ref reader, _path.AsSpan(0, pathLength), session);
            }
        }
        catch (JsonException) { /* malformed / truncated — keep what was read */ }
        return Finish();
    }

    bool Finish()
    {
        if (_pending.Count == 0) return false;
        _keys.AddRange(_pending);
        _pending.Clear();
        return true;
    }

    /// <summary>Writes a property name as the innermost container's child
    /// segment; returns the path length.</summary>
    int Segment(int depth, ref Utf8JsonReader reader)
    {
        int at = depth == 0 ? 0 : _segStart[depth - 1];
        if (at > 0) _path[at - 1] = (byte)'.';
        if (reader.ValueIsEscaped)
        {
            // rare: unescape (allocates)
            var bytes = Encoding.UTF8.GetBytes(reader.GetString() ?? "");
            int n = Math.Min(bytes.Length, _path.Length - at);
            bytes.AsSpan(0, n).CopyTo(_path.AsSpan(at));
            return at + n;
        }
        var name = reader.ValueSpan;
        int len = Math.Min(name.Length, _path.Length - at);
        name[..len].CopyTo(_path.AsSpan(at));
        return at + len;
    }

    /// <summary>Writes the innermost array's next index as the child
    /// segment; returns the path length.</summary>
    int Index(int depth)
    {
        int at = _segStart[depth - 1];
        if (at > 0) _path[at - 1] = (byte)'.';
        Utf8Formatter.TryFormat(_arrayIndex[depth - 1]++, _path.AsSpan(at), out int written);
        return at + written;
    }

    void Value(ref Utf8JsonReader reader, ReadOnlySpan<byte> path, OgsSession session)
    {
        double number = double.NaN;
        Kind kind;
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                if (!reader.TryGetDouble(out number)) return;
                kind = Kind.Number;
                break;
            case JsonTokenType.True:
            case JsonTokenType.False:
                number = reader.TokenType == JsonTokenType.True ? 1 : 0;
                kind = Kind.Boolean;
                break;
            case JsonTokenType.String:
                if (!reader.ValueIsEscaped && Utf8Parser.TryParse(reader.ValueSpan, out number, out int used)
                    && used == reader.ValueSpan.Length)
                    kind = Kind.Number;
                else kind = Kind.Text;
                break;
            default:
                return; // null
        }

        for (int i = 0; i < _watchPaths.Count; i++)
            if (path.SequenceEqual(_watchPaths[i])) _watch[i] = number;

        ulong hash = Hash(path);
        if (!_byHash.TryGetValue(hash, out var key))
        {
            var p = path.ToArray();
            key = new Key { Path = p, Kind = kind, Ignored = _ignored.Contains(Encoding.UTF8.GetString(p)) };
            _byHash[hash] = key;
            if (!key.Ignored) _pending.Add(key);
            return;
        }
        if (key.Ignored || !key.Declared) return;

        switch (key.Kind)
        {
            case Kind.Number when !double.IsNaN(number):
                session.SetChannel(key.Channel, number * key.Scale + key.Bias);
                break;
            case Kind.Boolean when !double.IsNaN(number):
                session.SetChannel(key.Channel, number != 0);
                break;
            case Kind.Text when reader.TokenType == JsonTokenType.String && !reader.ValueIsEscaped:
                session.SetSessionText(key.Channel, reader.ValueSpan);
                break;
            case Kind.Text when reader.TokenType == JsonTokenType.String:
                session.SetSessionText(key.Channel, reader.GetString() ?? "");
                break;
        }
    }

    static ulong Hash(ReadOnlySpan<byte> s)
    {
        ulong h = 14695981039346656037UL;           // FNV-1a 64
        foreach (byte b in s) { h ^= b; h *= 1099511628211UL; }
        return h;
    }

    /// <summary>Channel-name form of a raw key: letters, digits and dots
    /// kept, every other run becomes one '_' ("TAS, km/h" → "TAS_km_h").</summary>
    internal static string Sanitize(string path)
    {
        var sb = new StringBuilder(path.Length);
        bool gap = false;
        foreach (char c in path)
        {
            if (char.IsAsciiLetterOrDigit(c) || c == '.')
            {
                if (gap && sb.Length > 0 && sb[^1] != '.') sb.Append('_');
                gap = false;
                sb.Append(c);
            }
            else gap = true;
        }
        return sb.ToString();
    }
}
}
