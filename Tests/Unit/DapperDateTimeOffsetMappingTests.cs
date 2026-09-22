using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Threading.Tasks;
using APIBack.Infrastructure;
using Dapper;
using Xunit;

namespace APIBack.Tests.Unit
{
    /// <summary>
    /// Exercita o Dapper de ponta a ponta com um provider falso que se comporta como o
    /// Npgsql 9: timestamptz chega como DateTime(Kind=Utc). Cobre os caminhos que
    /// quebravam em producao (ExecuteScalar e mapeamento de linha) e a escrita de
    /// parametros, que precisa sair como timestamptz com offset zero.
    /// </summary>
    public sealed class DapperDateTimeOffsetMappingTests
    {
        private static readonly DateTime UtcSample = new(2026, 9, 22, 11, 53, 14, DateTimeKind.Utc);

        public DapperDateTimeOffsetMappingTests()
        {
            DapperConfiguration.Configure();
        }

        [Fact]
        public async Task ExecuteScalarAsync_DateTimeFromProvider_MapsToDateTimeOffset()
        {
            // Caminho de "SELECT NOW()" em GetSnapshotAsync e GetServerNowAsync.
            await using var connection = new FakeConnection(scalar: UtcSample);

            var result = await connection.ExecuteScalarAsync<DateTimeOffset>("SELECT NOW();");

            Assert.Equal(TimeSpan.Zero, result.Offset);
            Assert.Equal(UtcSample, result.UtcDateTime);
        }

        [Fact]
        public async Task QueryAsync_RowWithDateTimeOffsetProperties_MapsRequiredAndNullable()
        {
            var table = new DataTable();
            table.Columns.Add("expires_at_utc", typeof(DateTime));
            table.Columns.Add("received_at_utc", typeof(DateTime));
            table.Rows.Add(UtcSample, DBNull.Value);
            table.Rows.Add(UtcSample, UtcSample);
            await using var connection = new FakeConnection(table: table);

            var rows = (await connection.QueryAsync<SampleRow>("SELECT ...")).ToList();

            Assert.Equal(UtcSample, rows[0].ExpiresAtUtc.UtcDateTime);
            Assert.Null(rows[0].ReceivedAtUtc);
            Assert.Equal(UtcSample, rows[1].ReceivedAtUtc!.Value.UtcDateTime);
        }

        [Fact]
        public async Task QuerySingleAsync_SingleDateTimeOffsetColumn_MapsValue()
        {
            var table = new DataTable();
            table.Columns.Add("now", typeof(DateTime));
            table.Rows.Add(UtcSample);
            await using var connection = new FakeConnection(table: table);

            var result = await connection.QuerySingleAsync<DateTimeOffset>("SELECT NOW();");

            Assert.Equal(UtcSample, result.UtcDateTime);
        }

        [Fact]
        public async Task ExecuteAsync_DateTimeOffsetParameter_UsesDapperNativeMappingNotTheHandler()
        {
            // Documenta o comportamento real: para PARAMETROS o typeMap nativo do Dapper
            // vence o type handler, entao o valor sai como veio, com DbType.DateTimeOffset
            // (timestamptz no Npgsql). Como o Npgsql so aceita offset zero em timestamptz,
            // quem escreve no banco precisa passar UTC (DateTimeOffset.UtcNow,
            // .ToUniversalTime()) -- que e o que o codigo de delivery ja faz.
            await using var connection = new FakeConnection();
            var brasilia = new DateTimeOffset(2026, 9, 22, 8, 53, 14, TimeSpan.FromHours(-3));

            await connection.ExecuteAsync("UPDATE x SET at = @At", new { At = brasilia });

            var parameter = Assert.Single(connection.LastCommand!.Parameters.Cast<DbParameter>());
            Assert.Equal(DbType.DateTimeOffset, parameter.DbType);
            Assert.Equal(brasilia, Assert.IsType<DateTimeOffset>(parameter.Value));
        }

        private sealed class SampleRow
        {
            public DateTimeOffset ExpiresAtUtc { get; set; }
            public DateTimeOffset? ReceivedAtUtc { get; set; }
        }

        // ---- provider ADO.NET minimo -------------------------------------------------

        private sealed class FakeConnection : DbConnection
        {
            private readonly object? _scalar;
            private readonly DataTable? _table;

            public FakeConnection(object? scalar = null, DataTable? table = null)
            {
                _scalar = scalar;
                _table = table;
            }

            public FakeCommand? LastCommand { get; private set; }

            [System.Diagnostics.CodeAnalysis.AllowNull]
            public override string ConnectionString { get; set; } = string.Empty;
            public override string Database => "fake";
            public override string DataSource => "fake";
            public override string ServerVersion => "16.0";
            public override ConnectionState State => ConnectionState.Open;
            public override void ChangeDatabase(string databaseName) { }
            public override void Close() { }
            public override void Open() { }
            protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
                throw new NotSupportedException();
            protected override DbCommand CreateDbCommand() => LastCommand = new FakeCommand(_scalar, _table);
        }

        private sealed class FakeCommand : DbCommand
        {
            private readonly object? _scalar;
            private readonly DataTable? _table;
            private readonly FakeParameterCollection _parameters = new();

            public FakeCommand(object? scalar, DataTable? table)
            {
                _scalar = scalar;
                _table = table;
            }

            [System.Diagnostics.CodeAnalysis.AllowNull]
            public override string CommandText { get; set; } = string.Empty;
            public override int CommandTimeout { get; set; }
            public override CommandType CommandType { get; set; }
            public override bool DesignTimeVisible { get; set; }
            public override UpdateRowSource UpdatedRowSource { get; set; }
            protected override DbConnection? DbConnection { get; set; }
            protected override DbParameterCollection DbParameterCollection => _parameters;
            protected override DbTransaction? DbTransaction { get; set; }
            public override void Cancel() { }
            public override int ExecuteNonQuery() => 1;
            public override object? ExecuteScalar() => _scalar;
            public override void Prepare() { }
            protected override DbParameter CreateDbParameter() => new FakeParameter();
            protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
                (_table ?? new DataTable()).CreateDataReader();
        }

        private sealed class FakeParameter : DbParameter
        {
            public override DbType DbType { get; set; }
            public override ParameterDirection Direction { get; set; } = ParameterDirection.Input;
            public override bool IsNullable { get; set; }
            [System.Diagnostics.CodeAnalysis.AllowNull]
            public override string ParameterName { get; set; } = string.Empty;
            public override int Size { get; set; }
            [System.Diagnostics.CodeAnalysis.AllowNull]
            public override string SourceColumn { get; set; } = string.Empty;
            public override bool SourceColumnNullMapping { get; set; }
            public override object? Value { get; set; }
            public override void ResetDbType() => DbType = DbType.Object;
        }

        private sealed class FakeParameterCollection : DbParameterCollection
        {
            private readonly List<DbParameter> _items = new();

            public override int Count => _items.Count;
            public override object SyncRoot => _items;
            public override int Add(object value) { _items.Add((DbParameter)value); return _items.Count - 1; }
            public override void AddRange(Array values) { foreach (var value in values) Add(value!); }
            public override void Clear() => _items.Clear();
            public override bool Contains(object value) => _items.Contains((DbParameter)value);
            public override bool Contains(string value) => IndexOf(value) >= 0;
            public override void CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);
            public override IEnumerator GetEnumerator() => _items.GetEnumerator();
            public override int IndexOf(object value) => _items.IndexOf((DbParameter)value);
            public override int IndexOf(string parameterName) =>
                _items.FindIndex(p => string.Equals(p.ParameterName, parameterName, StringComparison.Ordinal));
            public override void Insert(int index, object value) => _items.Insert(index, (DbParameter)value);
            public override void Remove(object value) => _items.Remove((DbParameter)value);
            public override void RemoveAt(int index) => _items.RemoveAt(index);
            public override void RemoveAt(string parameterName) => _items.RemoveAt(IndexOf(parameterName));
            protected override DbParameter GetParameter(int index) => _items[index];
            protected override DbParameter GetParameter(string parameterName) => _items[IndexOf(parameterName)];
            protected override void SetParameter(int index, DbParameter value) => _items[index] = value;
            protected override void SetParameter(string parameterName, DbParameter value) =>
                _items[IndexOf(parameterName)] = value;
        }
    }
}
