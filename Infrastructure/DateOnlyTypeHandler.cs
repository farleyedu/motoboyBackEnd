using System;
using System.Data;
using Dapper;

namespace APIBack.Infrastructure
{
    /// <summary>
    /// Converte <c>date</c> do Postgres para <see cref="DateOnly"/>.
    ///
    /// O Npgsql materializa uma coluna <c>date</c> como <see cref="DateTime"/> ao ler a linha
    /// "crua" (como o Dapper faz para preencher uma tupla ou uma propriedade declarada como
    /// <see cref="DateOnly"/>). O Dapper, sem um <see cref="SqlMapper.TypeHandler{T}"/> registrado
    /// para o tipo, cai no <c>Convert.DefaultToType</c> -- que nao sabe converter
    /// <c>DateTime</c> em <c>DateOnly</c> -- e lanca
    /// <c>InvalidCastException: Invalid cast from 'System.DateTime' to 'System.DateOnly'</c>.
    /// Mesma causa do <see cref="DateTimeOffsetTypeHandler"/>, tipo diferente; ver o comentario
    /// de la para o historico completo.
    ///
    /// Registrar o handler resolve todos os pontos de uma vez (ex.: HorarioOperacaoRepository,
    /// que le e grava "data" em estabelecimento_horario_especial), em vez de converter em cada
    /// call site.
    /// </summary>
    public sealed class DateOnlyTypeHandler : SqlMapper.TypeHandler<DateOnly>
    {
        public override DateOnly Parse(object value) => value switch
        {
            DateOnly dateOnly => dateOnly,
            DateTime dateTime => DateOnly.FromDateTime(dateTime),
            string text => DateOnly.Parse(text, System.Globalization.CultureInfo.InvariantCulture),

            _ => throw new DataException(
                $"Nao foi possivel converter '{value?.GetType().FullName ?? "null"}' em DateOnly.")
        };

        public override void SetValue(IDbDataParameter parameter, DateOnly value)
        {
            parameter.DbType = DbType.Date;
            parameter.Value = value.ToDateTime(TimeOnly.MinValue);
        }
    }
}
