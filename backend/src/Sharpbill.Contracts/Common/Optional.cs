using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sharpbill.Contracts.Common;

/// <summary>Preserves the distinction between an omitted JSON property and an explicit null.</summary>
[JsonConverter(typeof(PatchFieldJsonConverterFactory))]
public readonly record struct PatchField<T>
{
    public PatchField(T value)
    {
        Value = value;
        HasValue = true;
    }

    public bool HasValue { get; }
    public T? Value { get; }

    public T? GetValueOrDefault() => Value;

    public static implicit operator PatchField<T>(T value) => new(value);
}

public sealed class PatchFieldJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType &&
        typeToConvert.GetGenericTypeDefinition() == typeof(PatchField<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(typeToConvert);
        ArgumentNullException.ThrowIfNull(options);
        var valueType = typeToConvert.GetGenericArguments()[0];
        var converterType = typeof(PatchFieldJsonConverter<>).MakeGenericType(valueType);
        return (JsonConverter)(Activator.CreateInstance(converterType) ??
            throw new InvalidOperationException("Unable to create a patch-field JSON converter"));
    }

    private sealed class PatchFieldJsonConverter<T> : JsonConverter<PatchField<T>>
    {
        public override bool HandleNull => true;

        public override PatchField<T> Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            var value = JsonSerializer.Deserialize<T>(ref reader, options);
            return new PatchField<T>(value!);
        }

        public override void Write(
            Utf8JsonWriter writer,
            PatchField<T> value,
            JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value.Value, options);
    }
}
