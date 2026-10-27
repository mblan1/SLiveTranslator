using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace SLiveTranslator.Models;

/// <summary>
/// Lets a settings file from a newer version load here instead of failing as a whole: an enum name this version does
/// not know reads as the property's default, and unknown keys of an enum-keyed dictionary or unknown items of an enum
/// set are skipped. <see cref="SettingsStore"/> writes the skipped values back on save.
/// </summary>
static class TolerantEnums
{
    /// <summary>A <see cref="DefaultJsonTypeInfoResolver"/> modifier that installs the tolerant converters.</summary>
    public static void Apply(JsonTypeInfo type)
    {
        if (type.Kind != JsonTypeInfoKind.Object) return;
        object? sample = null;
        foreach (var property in type.Properties)
        {
            var t = property.PropertyType;
            if (t.IsEnum)
            {
                // The default is whatever a new instance holds, e.g. LogLevel.Info rather than the first member.
                sample ??= type.CreateObject?.Invoke();
                object fallback = sample is not null && property.Get is not null ? property.Get(sample)! : Activator.CreateInstance(t)!;
                property.CustomConverter = Create(typeof(EnumConverter<>), [t], fallback);
            }
            else if (IsGeneric(t, typeof(Dictionary<,>)) && t.GetGenericArguments()[0].IsEnum)
                property.CustomConverter = Create(typeof(EnumKeyDictionaryConverter<,>), t.GetGenericArguments());
            else if (IsGeneric(t, typeof(HashSet<>)) && t.GetGenericArguments()[0].IsEnum)
                property.CustomConverter = Create(typeof(EnumSetConverter<>), t.GetGenericArguments());
        }
    }

    static bool IsGeneric(Type t, Type definition) => t.IsGenericType && t.GetGenericTypeDefinition() == definition;

    static JsonConverter Create(Type definition, Type[] arguments, params object[] args) =>
        (JsonConverter)Activator.CreateInstance(definition.MakeGenericType(arguments), args)!;

    /// <summary>
    /// Exact member names only: a looser match ("clear" for Clear) would make the save write both spellings,
    /// because the file's spelling is kept as an unknown entry.
    /// </summary>
    static bool TryParseName<T>(string? name, out T value) where T : struct, Enum
    {
        value = default;
        return name is not null && Enum.IsDefined(typeof(T), name) && Enum.TryParse(name, out value);
    }

    /// <summary>Reads a name (any case) or a number like the stock converter; anything unknown gives the fallback.</summary>
    sealed class EnumConverter<T>(T fallback) : JsonConverter<T> where T : struct, Enum
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String
                && Enum.TryParse(reader.GetString(), ignoreCase: true, out T named) && Enum.IsDefined(named)) return named;
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out int number)
                && (T)Enum.ToObject(typeof(T), number) is var numbered && Enum.IsDefined(numbered)) return numbered;
            reader.Skip();
            return fallback;
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }

    sealed class EnumKeyDictionaryConverter<TKey, TValue> : JsonConverter<Dictionary<TKey, TValue>> where TKey : struct, Enum
    {
        public override Dictionary<TKey, TValue> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException($"Expected an object for {typeof(TKey).Name} entries.");
            var result = new Dictionary<TKey, TValue>();
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                string? name = reader.GetString();
                reader.Read();
                if (TryParseName(name, out TKey key)) result[key] = JsonSerializer.Deserialize<TValue>(ref reader, options)!;
                else reader.Skip();
            }
            return result;
        }

        public override void Write(Utf8JsonWriter writer, Dictionary<TKey, TValue> value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            foreach (var (key, item) in value)
            {
                writer.WritePropertyName(key.ToString());
                JsonSerializer.Serialize(writer, item, options);
            }
            writer.WriteEndObject();
        }
    }

    sealed class EnumSetConverter<T> : JsonConverter<HashSet<T>> where T : struct, Enum
    {
        public override HashSet<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException($"Expected an array of {typeof(T).Name} names.");
            var result = new HashSet<T>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType == JsonTokenType.String && TryParseName(reader.GetString(), out T item)) result.Add(item);
                else reader.Skip();
            }
            return result;
        }

        public override void Write(Utf8JsonWriter writer, HashSet<T> value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            foreach (var item in value) writer.WriteStringValue(item.ToString());
            writer.WriteEndArray();
        }
    }
}
