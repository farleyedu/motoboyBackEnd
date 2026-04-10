using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace APIBack.Infrastructure
{
    public sealed class FlexibleDateOnlyJsonConverter : JsonConverter<DateOnly>
    {
        public override DateOnly Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return FlexibleDateOnlyParser.ParseRequired(ref reader);
        }

        public override void Write(Utf8JsonWriter writer, DateOnly value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }
    }

    public sealed class FlexibleNullableDateOnlyJsonConverter : JsonConverter<DateOnly?>
    {
        public override DateOnly? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return FlexibleDateOnlyParser.ParseNullable(ref reader);
        }

        public override void Write(Utf8JsonWriter writer, DateOnly? value, JsonSerializerOptions options)
        {
            if (!value.HasValue)
            {
                writer.WriteNullValue();
                return;
            }

            writer.WriteStringValue(value.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }
    }

    internal static class FlexibleDateOnlyParser
    {
        public static DateOnly ParseRequired(ref Utf8JsonReader reader)
        {
            return ParseNullable(ref reader)
                ?? throw new JsonException("Expected a non-null date value.");
        }

        public static DateOnly? ParseNullable(ref Utf8JsonReader reader)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return null;
            }

            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException("Expected a string date value.");
            }

            var raw = reader.GetString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            if (DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateOnly))
            {
                return dateOnly;
            }

            if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dateTimeOffset))
            {
                return DateOnly.FromDateTime(dateTimeOffset.DateTime);
            }

            if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dateTime))
            {
                return DateOnly.FromDateTime(dateTime);
            }

            throw new JsonException($"Invalid date value '{raw}'.");
        }
    }
}
