using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Npgsql;

namespace APIBack.Payments.Assinaturas
{
    /// <summary>A assinatura de uma loja como o painel, a lista da Gestão e a carteira a enxergam.</summary>
    public sealed record AssinaturaDaLoja
    {
        public Guid Loja { get; init; }
        public string Nome { get; init; } = string.Empty;
        public FatosAssinatura Fatos { get; init; } = new();
        public SituacaoAssinatura Situacao { get; init; } = null!;
        /// <summary>A viva ou a última encerrada. Nulo = nunca assinou.</summary>
        public AssinaturaRow? Assinatura { get; init; }
        public DividaDto? Divida => Fatos.Divida;
        /// <summary>Próxima mensalidade ainda no prazo.</summary>
        public DateTimeOffset? ProximoVencimento { get; init; }
        /// <summary>Quando a cancelada deixa de valer: o fim do último período pago.</summary>
        public DateTimeOffset? AcessoAte { get; init; }
        public DateTimeOffset? TrocaEm { get; init; }
        public BloqueioDto? Bloqueio { get; init; }
        public IReadOnlyList<CobrancaRow> Abertas { get; init; } = Array.Empty<CobrancaRow>();
    }

    /// <summary>Uma leitura só, para uma loja ou para todas: o mesmo estado em qualquer tela.</summary>
    public static class AssinaturaConsulta
    {
        private sealed class LojaRow
        {
            public Guid Id { get; set; }
            public string? Nome { get; set; }
            public string? Situacao { get; set; }
            public DateTimeOffset? TesteTerminaEm { get; set; }
            public DateTimeOffset? BloqueadoEm { get; set; }
            public string? BloqueadoMotivo { get; set; }
            public int? BloqueadoPor { get; set; }
            public string? BloqueadoPorNome { get; set; }
        }

        private sealed class CicloDaLoja
        {
            public Guid Id { get; set; }
            public Guid IdAssinatura { get; set; }
            public int Numero { get; set; }
            public string PlanoChave { get; set; } = string.Empty;
            public string Periodicidade { get; set; } = Periodicidades.Mensal;
            public decimal Preco { get; set; }
            public DateTimeOffset Fim { get; set; }
            public string Status { get; set; } = string.Empty;
            public bool TemPagamento { get; set; }
            public bool Dispensado { get; set; }
        }

        public static async Task<AssinaturaDaLoja> UmaAsync(NpgsqlConnection cx, Guid loja, DateTimeOffset agora, CancellationToken ct) =>
            (await CarregarAsync(cx, new[] { loja }, agora, ct)).GetValueOrDefault(loja)
            ?? throw MercadoPago.PagamentoException.NaoEncontrado("Estabelecimento não encontrado.");

        /// <param name="lojas">Nulo = todas.</param>
        public static async Task<Dictionary<Guid, AssinaturaDaLoja>> CarregarAsync(NpgsqlConnection cx, Guid[]? lojas, DateTimeOffset agora, CancellationToken ct)
        {
            // O bloqueio é lido por to_jsonb para a consulta não depender das colunas existirem.
            var estabelecimentos = (await cx.QueryAsync<LojaRow>(new CommandDefinition(@"
SELECT e.id, e.nome_fantasia AS nome, e.situacao_comercial AS situacao, e.teste_termina_em,
       (to_jsonb(e) ->> 'bloqueado_em')::timestamptz AS bloqueado_em, to_jsonb(e) ->> 'bloqueado_motivo' AS bloqueado_motivo,
       (to_jsonb(e) ->> 'bloqueado_por')::int AS bloqueado_por, u.nome AS bloqueado_por_nome
  FROM estabelecimentos e LEFT JOIN usuario u ON u.id = (to_jsonb(e) ->> 'bloqueado_por')::int
 WHERE @Lojas::uuid[] IS NULL OR e.id = ANY(@Lojas)
 ORDER BY e.nome_fantasia", new { Lojas = lojas }, cancellationToken: ct))).ToList();

            var assinaturas = (await cx.QueryAsync<AssinaturaRow>(new CommandDefinition($@"
SELECT DISTINCT ON (id_estabelecimento) {AssinaturaSql.ColunasAssinatura} FROM assinatura
 WHERE @Lojas::uuid[] IS NULL OR id_estabelecimento = ANY(@Lojas)
 ORDER BY id_estabelecimento, (status IN ('pendente', 'ativa')) DESC, created_at DESC", new { Lojas = lojas }, cancellationToken: ct)))
                .ToDictionary(a => a.IdEstabelecimento);

            var abertas = (await cx.QueryAsync<CobrancaRow>(new CommandDefinition($@"
SELECT {AssinaturaSql.ColunasCobranca} FROM comercial_cobranca
 WHERE status IN ('aberta', 'processando') AND (@Lojas::uuid[] IS NULL OR id_estabelecimento = ANY(@Lojas))
 ORDER BY vencimento, created_at", new { Lojas = lojas }, cancellationToken: ct))).ToLookup(c => c.IdEstabelecimento);

            var ids = assinaturas.Values.Select(a => a.Id).ToArray();
            var ciclos = (await cx.QueryAsync<CicloDaLoja>(new CommandDefinition(@"
SELECT ci.id, ci.id_assinatura, ci.numero, ci.plano_chave, ci.periodicidade, ci.preco, ci.fim, ci.status,
       EXISTS (SELECT 1 FROM comercial_cobranca c WHERE c.id_ciclo = ci.id AND c.tipo = 'ciclo' AND c.status = 'paga') AS tem_pagamento,
       EXISTS (SELECT 1 FROM comercial_cobranca c WHERE c.id_ciclo = ci.id AND c.tipo = 'ciclo' AND c.status = 'cancelada'
                AND c.cancelamento_motivo = 'gestao') AS dispensado
  FROM assinatura_ciclo ci JOIN assinatura a ON a.id = ci.id_assinatura
 WHERE ci.id_assinatura = ANY(@Ids) AND ci.status <> 'cancelado'
   AND ci.numero >= COALESCE((SELECT x.numero FROM assinatura_ciclo x WHERE x.id = a.id_ciclo_atual), 0)
 ORDER BY ci.numero", new { Ids = ids }, cancellationToken: ct))).ToLookup(c => c.IdAssinatura);

            var resultado = new Dictionary<Guid, AssinaturaDaLoja>();
            foreach (var loja in estabelecimentos)
            {
                var a = assinaturas.GetValueOrDefault(loja.Id);
                var doLoja = abertas[loja.Id].ToList();
                var divida = Divida.De(doLoja.Where(c => c.Tipo == TiposCobranca.Ciclo).Select(c => c.Mensalidade()), agora);
                var meus = a == null ? new List<CicloDaLoja>() : ciclos[a.Id].ToList();
                var atual = a?.IdCicloAtual == null ? null : meus.FirstOrDefault(c => c.Id == a.IdCicloAtual);
                var seguintes = meus.Where(c => atual != null && c.Numero > atual.Numero).ToList();
                var proximo = seguintes.FirstOrDefault();
                var trocaNaProxima = a?.MudancaPlano != null && proximo != null && proximo.PlanoChave == a.MudancaPlano
                    && proximo.Periodicidade == (a.MudancaPeriodicidade ?? a.Periodicidade);
                var acessoAte = seguintes.Where(c => c.Status == "pago").Select(c => (DateTimeOffset?)c.Fim).LastOrDefault() ?? a?.PeriodoFim;
                // A troca vale no vencimento do ciclo atual; com a próxima fatura já paga no plano antigo, só depois dela.
                var trocaEm = a?.MudancaPlano == null ? null : proximo != null && !trocaNaProxima ? proximo.Fim : a.PeriodoFim;

                string? periodo = null;
                if (atual != null)
                {
                    // Só a mensalidade conta: a diferença de um upgrade pendurada no ciclo não deixa o mês "em aberto".
                    if (doLoja.Any(c => c.IdCiclo == atual.Id && c.Tipo == TiposCobranca.Ciclo)) periodo = RegrasDeAcesso.StatusPeriodoAberto;
                    else if (atual.Status == "pago" && atual.Preco > 0 && !atual.Dispensado) periodo = RegrasDeAcesso.StatusPeriodoPago;
                }

                var fatos = new FatosAssinatura
                {
                    SituacaoLoja = loja.Situacao,
                    TesteTerminaEm = loja.TesteTerminaEm,
                    Status = a?.Status,
                    Periodicidade = a?.Periodicidade ?? Periodicidades.Mensal,
                    EncerramentoMotivo = a?.EncerramentoMotivo,
                    Cancelada = a is { Viva: true, CancelamentoSolicitadoEm: not null },
                    TrocaMarcada = a is { Viva: true, MudancaPlano: not null },
                    ProximaPagaComATroca = trocaNaProxima && proximo!.Status == "pago" && proximo.Preco > 0,
                    AnualPagoAFrente = seguintes.Any(c => c.Periodicidade == Periodicidades.Anual && c.TemPagamento),
                    Divida = divida,
                    TemCobrancaAPagar = doLoja.Count > 0,
                    CobrancaDoPeriodo = periodo
                };
                resultado[loja.Id] = new AssinaturaDaLoja
                {
                    Loja = loja.Id,
                    Nome = loja.Nome ?? "Loja",
                    Fatos = fatos,
                    Situacao = RegrasDeAcesso.Avaliar(fatos, agora),
                    Assinatura = a,
                    ProximoVencimento = doLoja.Where(c => c.Tipo == TiposCobranca.Ciclo && c.Vencimento >= FusoComercial.InicioDeHoje(agora) && c.DividaValor == 0)
                        .Select(c => (DateTimeOffset?)c.Vencimento).FirstOrDefault(),
                    AcessoAte = acessoAte,
                    TrocaEm = trocaEm,
                    Bloqueio = loja.Situacao == Service.SituacoesComerciais.Bloqueado && loja.BloqueadoEm.HasValue
                        ? new BloqueioDto(loja.BloqueadoEm.Value, loja.BloqueadoMotivo,
                            loja.BloqueadoPor.HasValue ? new PessoaDto(loja.BloqueadoPor.Value, loja.BloqueadoPorNome) : null)
                        : null,
                    Abertas = doLoja
                };
            }
            return resultado;
        }
    }
}
