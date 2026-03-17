using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace APIBack.Infrastructure
{
    /// <summary>
    /// Conversor JSON tolerante para Guid?. Strings que nao sao GUIDs validos
    /// (ex: "emp-1773790043148", "tipo-garagem") sao convertidas para null
    /// em vez de causar erro 400 no model binding.
    /// </summary>
    public class LenientGuidConverter : JsonConverter<Guid?>
    {
        public override Guid? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
                return null;

            if (reader.TokenType == JsonTokenType.String)
            {
                var str = reader.GetString();
                if (string.IsNullOrWhiteSpace(str))
                    return null;

                return Guid.TryParse(str, out var guid) ? guid : null;
            }

            return null;
        }

        public override void Write(Utf8JsonWriter writer, Guid? value, JsonSerializerOptions options)
        {
            if (value.HasValue)
                writer.WriteStringValue(value.Value);
            else
                writer.WriteNullValue();
        }
    }
}
