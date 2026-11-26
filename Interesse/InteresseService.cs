using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Turnos;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace APIBack.Interesse;

public static class TiposDeInteresse
{
    // Gravados aqui.
    public const string SemCredito = "sem_credito";
    public const string AbriuCompra = "abriu_compra";
    // Só lidos das tabelas de compra.
    public const string PixNaoPago = "pix_nao_pago";
    public const string Compra = "compra";
    public const string PedidoEquipe = "pedido_equipe";
}

public sealed record PessoaDoInteresseDto(int Id, string? Nome);

public sealed record InteresseMotoboyDto(
    Guid EstabelecimentoId, string Loja, string Tipo, DateTimeOffset Quando, PessoaDoInteresseDto? Pessoa, int? Quantidade, string Detalhe);

/// <summary>
/// Registro genérico de interesse da loja (quem, papel, tipo, quando, contexto). O histórico não se recupera depois,
/// então se grava na hora em que acontece; serve a outros assuntos além de motoboys.
/// </summary>
public interface IInteresseService
{
    /// <summary>Falso = tabela ainda não criada neste ambiente (migração 20261212_03).</summary>
    Task<bool> RegistrarAbriuCompraAsync(Guid estabelecimentoId, int usuarioId, string? papel, CancellationToken ct = default);
    /// <summary>Um motoboy não entrou. Uma vez por motoboy em cada turno. Nunca lança.</summary>
    Task RegistrarSemCreditoAsync(Guid estabelecimentoId, int usuarioId, int motoboyId, SituacaoDoTurno situacao, CancellationToken ct = default);
    Task<IReadOnlyList<InteresseMotoboyDto>> ListarMotoboysAsync(int dias, Guid? estabelecimentoId, CancellationToken ct = default);
}

public sealed class InteresseService : IInteresseService
{
    /// <summary>Aberturas repetidas da compra pela mesma pessoa dentro deste intervalo contam uma vez.</summary>
    private const int MinutosEntreAberturas = 5;

    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<InteresseService>? _logger;

    public InteresseService(NpgsqlDataSource dataSource, ILogger<InteresseService>? logger = null)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    public async Task<bool> RegistrarAbriuCompraAsync(Guid estabelecimentoId, int usuarioId, string? papel, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        if (!await ExisteAsync(connection, "loja_interesse", ct)) return false;
        await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO loja_interesse (estabelecimento_id, pessoa_id, pessoa_papel, tipo)
SELECT @Loja, @Pessoa, @Papel, @Tipo
 WHERE NOT EXISTS (SELECT 1 FROM loja_interesse
                    WHERE estabelecimento_id = @Loja AND pessoa_id = @Pessoa AND tipo = @Tipo
                      AND quando > NOW() - make_interval(mins => @Minutos));",
            new
            {
                Loja = estabelecimentoId, Pessoa = usuarioId, Papel = string.IsNullOrWhiteSpace(papel) ? null : papel,
                Tipo = TiposDeInteresse.AbriuCompra, Minutos = MinutosEntreAberturas
            }, cancellationToken: ct));
        return true;
    }

    public async Task RegistrarSemCreditoAsync(Guid estabelecimentoId, int usuarioId, int motoboyId, SituacaoDoTurno situacao, CancellationToken ct = default)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            if (!await ExisteAsync(connection, "loja_interesse", ct)) return;
            var turno = situacao.Turno?.Id.ToString(CultureInfo.InvariantCulture)
                        ?? situacao.Agora.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO loja_interesse (estabelecimento_id, pessoa_id, pessoa_papel, tipo, contexto, chave_unica)
VALUES (@Loja, @Pessoa, 'motoboy', @Tipo, CAST(@Contexto AS jsonb), @Chave)
ON CONFLICT (estabelecimento_id, chave_unica) WHERE chave_unica IS NOT NULL DO NOTHING;",
                new
                {
                    Loja = estabelecimentoId, Pessoa = usuarioId, Tipo = TiposDeInteresse.SemCredito,
                    Chave = $"{TiposDeInteresse.SemCredito}:{turno}:{motoboyId}",
                    Contexto = JsonSerializer.Serialize(new
                    {
                        motoboyId, turnoId = situacao.Turno?.Id, total = situacao.Total, gastos = situacao.Gastos.Count,
                        detalhe = $"Motoboy não entrou: {situacao.Gastos.Count} de {situacao.Total} créditos usados."
                    })
                }, cancellationToken: ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A recusa ao motoboy não pode virar erro por causa do registro.
            _logger?.LogError(ex, "Não foi possível registrar o interesse sem crédito da loja {Loja}.", estabelecimentoId);
        }
    }

    private sealed class Linha
    {
        public Guid EstabelecimentoId { get; set; }
        public string? Loja { get; set; }
        public string Tipo { get; set; } = string.Empty;
        public DateTime Quando { get; set; }
        public int? PessoaId { get; set; }
        public string? PessoaNome { get; set; }
        public int? Quantidade { get; set; }
        public string? Detalhe { get; set; }
        public string? Produto { get; set; }
        public decimal? Valor { get; set; }
        public string? Situacao { get; set; }
    }

    public async Task<IReadOnlyList<InteresseMotoboyDto>> ListarMotoboysAsync(int dias, Guid? estabelecimentoId, CancellationToken ct = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(ct);
        var partes = new List<string>();
        if (await ExisteAsync(connection, "loja_interesse", ct))
            partes.Add(@"
SELECT i.estabelecimento_id AS EstabelecimentoId, i.tipo AS Tipo, i.quando AS Quando, i.pessoa_id AS PessoaId,
       (i.contexto->>'quantidade')::int AS Quantidade, i.contexto->>'detalhe' AS Detalhe,
       NULL::text AS Produto, NULL::numeric AS Valor, NULL::text AS Situacao
  FROM loja_interesse i
 WHERE i.tipo IN ('sem_credito', 'abriu_compra')");

        // Só extras com motoboys, e nunca a cortesia da Gestão (não é interesse da loja).
        if (await ExisteAsync(connection, "compra_extra", ct) && await ExisteAsync(connection, "comercial_cobranca", ct))
            partes.Add(@"
SELECT ce.id_estabelecimento AS EstabelecimentoId, 'compra'::text AS Tipo, ce.ativada_em AS Quando, ce.criado_por AS PessoaId,
       ce.motoboys * ce.quantidade AS Quantidade, NULL::text AS Detalhe, ce.nome AS Produto, ce.valor_total AS Valor, ce.status AS Situacao
  FROM compra_extra ce
 WHERE ce.motoboys > 0 AND ce.ativada_em IS NOT NULL AND COALESCE(to_jsonb(ce)->>'cortesia', 'false') <> 'true'
UNION ALL
SELECT ce.id_estabelecimento, 'pix_nao_pago', ce.created_at, ce.criado_por, ce.motoboys * ce.quantidade, NULL::text,
       ce.nome, ce.valor_total, ce.status
  FROM compra_extra ce
  LEFT JOIN comercial_cobranca c ON c.id_compra = ce.id
 WHERE ce.motoboys > 0 AND ce.ativada_em IS NULL AND COALESCE(to_jsonb(ce)->>'cortesia', 'false') <> 'true'
   AND (ce.status = 'cancelada'
     OR (ce.status = 'aguardando_pagamento'
         AND COALESCE((to_jsonb(c)->>'expira_em')::timestamptz, ce.created_at + INTERVAL '1 hour') <= NOW()))");

        if (await ExisteAsync(connection, "solicitacao_compra", ct) && await ExisteAsync(connection, "produtos_avulsos", ct))
            partes.Add(@"
SELECT s.id_estabelecimento AS EstabelecimentoId, 'pedido_equipe'::text AS Tipo, s.created_at AS Quando, s.solicitante_id AS PessoaId,
       p.motoboys * s.quantidade AS Quantidade, NULL::text AS Detalhe, p.nome AS Produto, NULL::numeric AS Valor, s.status AS Situacao
  FROM solicitacao_compra s
  JOIN produtos_avulsos p ON p.id = s.produto_avulso_id
 WHERE p.motoboys > 0");

        if (partes.Count == 0) return Array.Empty<InteresseMotoboyDto>();

        var temUsuario = await ExisteAsync(connection, "usuario", ct);
        var sql = $@"
SELECT x.EstabelecimentoId, e.nome_fantasia AS Loja, x.Tipo, x.Quando, x.PessoaId,
       {(temUsuario ? "u.nome" : "NULL::text")} AS PessoaNome, x.Quantidade, x.Detalhe, x.Produto, x.Valor, x.Situacao
  FROM ({string.Join(" UNION ALL ", partes)}) x
  JOIN estabelecimentos e ON e.id = x.EstabelecimentoId
  {(temUsuario ? "LEFT JOIN usuario u ON u.id = x.PessoaId" : string.Empty)}
 WHERE x.Quando >= NOW() - make_interval(days => @Dias) AND (@Loja::uuid IS NULL OR x.EstabelecimentoId = @Loja::uuid)
 ORDER BY x.Quando DESC
 LIMIT 1000;";
        var linhas = await connection.QueryAsync<Linha>(new CommandDefinition(sql,
            new { Dias = Math.Clamp(dias, 1, 365), Loja = estabelecimentoId }, cancellationToken: ct));
        return linhas.Select(l => new InteresseMotoboyDto(
            l.EstabelecimentoId, l.Loja ?? "Loja sem nome", l.Tipo,
            new DateTimeOffset(DateTime.SpecifyKind(l.Quando, DateTimeKind.Utc)),
            l.PessoaId.HasValue ? new PessoaDoInteresseDto(l.PessoaId.Value, l.PessoaNome) : null,
            l.Quantidade, Detalhar(l))).ToList();
    }

    private static string Detalhar(Linha l)
    {
        var valor = l.Valor is > 0 ? $" ({l.Valor.Value.ToString("C", CultureInfo.GetCultureInfo("pt-BR"))})" : string.Empty;
        return l.Tipo switch
        {
            TiposDeInteresse.Compra => $"Comprou {l.Produto}{valor}.",
            TiposDeInteresse.PixNaoPago => $"Não pagou {l.Produto}{valor}.",
            TiposDeInteresse.PedidoEquipe => $"A equipe pediu {l.Produto}: " + (l.Situacao switch
            {
                "aprovada" => "aprovado.", "recusada" => "recusado.", "cancelada" => "cancelado.", _ => "esperando resposta."
            }),
            TiposDeInteresse.AbriuCompra => "Abriu a compra de motoboys.",
            _ => l.Detalhe ?? "Motoboy não entrou por falta de crédito."
        };
    }

    private static Task<bool> ExisteAsync(NpgsqlConnection connection, string tabela, CancellationToken ct) =>
        connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT to_regclass(@Tabela) IS NOT NULL;", new { Tabela = tabela }, cancellationToken: ct));
}
