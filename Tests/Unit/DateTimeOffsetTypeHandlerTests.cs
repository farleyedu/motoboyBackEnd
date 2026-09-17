using System;
using System.Data;
using APIBack.Infrastructure;
using Xunit;

namespace APIBack.Tests.Unit
{
    public sealed class DateTimeOffsetTypeHandlerTests
    {
        private readonly DateTimeOffsetTypeHandler _handler = new();

        [Fact]
        public void Parse_UtcDateTime_FromTimestamptz_KeepsInstantAndZeroOffset()
        {
            // Caso que derrubava o snapshot: Npgsql devolve timestamptz como
            // DateTime(Kind=Utc) e o Dapper nao convertia para DateTimeOffset.
            var value = new DateTime(2026, 9, 17, 18, 48, 47, DateTimeKind.Utc);

            var result = _handler.Parse(value);

            Assert.Equal(TimeSpan.Zero, result.Offset);
            Assert.Equal(value, result.UtcDateTime);
        }

        [Fact]
        public void Parse_UnspecifiedDateTime_IsTreatedAsUtcNotServerLocalTime()
        {
            var value = new DateTime(2026, 9, 17, 18, 48, 47, DateTimeKind.Unspecified);

            var result = _handler.Parse(value);

            Assert.Equal(TimeSpan.Zero, result.Offset);
            Assert.Equal(18, result.UtcDateTime.Hour);
        }

        [Fact]
        public void Parse_AlreadyDateTimeOffset_IsReturnedUnchanged()
        {
            var value = new DateTimeOffset(2026, 9, 17, 15, 48, 47, TimeSpan.FromHours(-3));

            var result = _handler.Parse(value);

            Assert.Equal(value, result);
        }

        [Fact]
        public void Parse_UnsupportedType_ThrowsDataException()
        {
            Assert.Throws<DataException>(() => _handler.Parse(42));
        }

        [Fact]
        public void SetValue_WritesUtcDateTime_SoNonZeroOffsetsAreAccepted()
        {
            var parameter = new StubParameter();

            _handler.SetValue(parameter, new DateTimeOffset(2026, 9, 17, 15, 0, 0, TimeSpan.FromHours(-3)));

            var written = Assert.IsType<DateTime>(parameter.Value);
            Assert.Equal(DateTimeKind.Utc, written.Kind);
            Assert.Equal(new DateTime(2026, 9, 17, 18, 0, 0, DateTimeKind.Utc), written);
        }

        private sealed class StubParameter : IDbDataParameter
        {
            public byte Precision { get; set; }
            public byte Scale { get; set; }
            public int Size { get; set; }
            public DbType DbType { get; set; }
            public ParameterDirection Direction { get; set; }
            public bool IsNullable => true;
            public string ParameterName { get; set; } = string.Empty;
            public string SourceColumn { get; set; } = string.Empty;
            public DataRowVersion SourceVersion { get; set; }
            public object? Value { get; set; }
        }
    }
}
