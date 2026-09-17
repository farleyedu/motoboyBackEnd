using System;
using System.Data;
using Dapper;

namespace APIBack.Infrastructure
{
    /// <summary>
    /// Converte <c>timestamptz</c> do Postgres para <see cref="DateTimeOffset"/>.
    ///
    /// A partir do Npgsql 6 (aqui roda a 9), <c>timestamptz</c> e materializado como
    /// <see cref="DateTime"/> com <c>Kind = Utc</c>. O Dapper, ao encontrar uma
    /// propriedade/escalar declarado como <see cref="DateTimeOffset"/>, cai no
    /// <c>Convert.DefaultToType</c> -- que nao sabe converter <c>DateTime</c> em
    /// <c>DateTimeOffset</c> -- e lanca
    /// <c>InvalidCastException: Invalid cast from 'System.DateTime' to 'System.DateTimeOffset'</c>.
    ///
    /// Isso afeta todo o modelo de tracking/fila do delivery (sessoes operacionais,
    /// localizacoes, paradas de rota), que usa <see cref="DateTimeOffset"/> em ~12
    /// propriedades e em <c>SELECT NOW()</c>. Registrar o handler resolve todos os
    /// pontos de uma vez, em vez de converter em cada call site.
    ///
    /// O Dapper registra automaticamente o equivalente <c>Nullable&lt;DateTimeOffset&gt;</c>
    /// ao receber um handler de tipo-valor, entao colunas anulaveis tambem passam por aqui.
    /// </summary>
    public sealed class DateTimeOffsetTypeHandler : SqlMapper.TypeHandler<DateTimeOffset>
    {
        public override DateTimeOffset Parse(object value) => value switch
        {
            DateTimeOffset offset => offset,

            // timestamptz chega como Kind=Utc; timestamp (sem timezone) chega como
            // Unspecified. Em ambos os casos o banco guarda UTC neste schema, entao
            // normalizamos para UTC antes de montar o offset em vez de deixar o
            // construtor assumir o fuso local do servidor.
            DateTime dateTime => new DateTimeOffset(
                DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),

            string text => DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture),

            _ => throw new DataException(
                $"Nao foi possivel converter '{value?.GetType().FullName ?? "null"}' em DateTimeOffset.")
        };

        public override void SetValue(IDbDataParameter parameter, DateTimeOffset value)
        {
            // UtcDateTime carrega Kind=Utc, que o Npgsql mapeia para timestamptz.
            // Enviar o DateTimeOffset cru falha quando o offset nao e zero.
            parameter.DbType = DbType.DateTime;
            parameter.Value = value.UtcDateTime;
        }
    }
}
