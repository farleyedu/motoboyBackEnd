using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Acompanhamento.Fases;
using APIBack.Payments.Assinaturas;
using Dapper;
using Npgsql;

namespace APIBack.Acompanhamento.Saude
{
    /// <summary>Leitura dos fatos de cada loja e dos limites; gravacao dos limites (auditada) e da faixa (so o worker). Nada aqui toca nos pagamentos.</summary>
    public static class SaudeSql
    {
        private const long TravaRegras = 75442341;
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        public static Task<bool> ExisteAsync(NpgsqlConnection cx, string tabela, CancellationToken ct) =>
            cx.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT to_regclass(@T) IS NOT NULL;", new { T = tabela }, cancellationToken: ct));

        // ---- limites

        public static async Task<LimitesSaude> LimitesAsync(NpgsqlConnection cx, CancellationToken ct, NpgsqlTransaction? tx = null)
        {
            if (!await ExisteAsync(cx, "acompanhamento_saude_regra", ct)) return new LimitesSaude();
            var linhas = await cx.QueryAsync<(string Chave, int? Valor)>(new CommandDefinition("SELECT chave, valor FROM acompanhamento_saude_regra;", transaction: tx, cancellationToken: ct));
            var versao = await cx.ExecuteScalarAsync<long>(new CommandDefinition("SELECT COALESCE(MAX(id), 0) FROM acompanhamento_saude_regra_versao;", transaction: tx, cancellationToken: ct));
            var valores = linhas.ToDictionary(l => l.Chave, l => l.Valor);
            // Valor invalido gravado a mao no banco nao derruba o quadro: a regra volta ao padrao.
            var (limites, erros) = new LimitesSaude { Versao = (int)versao }.Aplicar(valores);
            return erros.Count == 0 ? limites : new LimitesSaude { Versao = (int)versao };
        }

        public sealed record AlteracaoRegra(string Chave, int? Antes, int? Depois);

        /// <summary>Grava as regras em uma transacao com trava e guarda o antes/depois. Versao esperada diferente = 409 (alguem mudou antes).</summary>
        public static async Task<(LimitesSaude Limites, IReadOnlyList<AlteracaoRegra> Alteracoes)> SalvarLimitesAsync(
            NpgsqlConnection cx, IReadOnlyDictionary<string, int?> novos, int? versaoEsperada, int usuarioId, string? motivo, CancellationToken ct)
        {
            await using var tx = await cx.BeginTransactionAsync(ct);
            await cx.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(@L);", new { L = TravaRegras }, tx, cancellationToken: ct));
            var atuais = await LimitesAsync(cx, ct, tx);
            if (versaoEsperada.HasValue && versaoEsperada.Value != atuais.Versao)
                throw new SaudeException(409, "VERSAO_DAS_REGRAS", "As regras mudaram desde que você abriu a tela. Atualize e tente de novo.");
            var (limites, erros) = atuais.Aplicar(novos);
            if (erros.Count > 0) throw new SaudeException(422, "REGRAS_INVALIDAS", "Há regras inválidas.", erros);

            var antes = atuais.ComoDicionario();
            var depois = limites.ComoDicionario();
            var alteracoes = novos.Keys.Where(k => antes[k] != depois[k]).Select(k => new AlteracaoRegra(k, antes[k], depois[k])).ToList();
            if (alteracoes.Count == 0) { await tx.CommitAsync(ct); return (atuais, alteracoes); }

            foreach (var a in alteracoes)
                await cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO acompanhamento_saude_regra (chave, valor, atualizado_em, atualizado_por) VALUES (@Chave, @Valor, NOW(), @U)
ON CONFLICT (chave) DO UPDATE SET valor = EXCLUDED.valor, atualizado_em = NOW(), atualizado_por = EXCLUDED.atualizado_por;",
                    new { a.Chave, Valor = a.Depois, U = usuarioId }, tx, cancellationToken: ct));
            var versao = await cx.ExecuteScalarAsync<int>(new CommandDefinition(@"
INSERT INTO acompanhamento_saude_regra_versao (usuario_id, motivo, antes, depois)
VALUES (@U, @Motivo, CAST(@Antes AS jsonb), CAST(@Depois AS jsonb)) RETURNING id;",
                new
                {
                    U = usuarioId, Motivo = string.IsNullOrWhiteSpace(motivo) ? null : motivo.Trim(),
                    Antes = JsonSerializer.Serialize(alteracoes.ToDictionary(a => a.Chave, a => a.Antes), Json),
                    Depois = JsonSerializer.Serialize(alteracoes.ToDictionary(a => a.Chave, a => a.Depois), Json),
                }, tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
            return (limites with { Versao = versao }, alteracoes);
        }

        public sealed record VersaoRegraDto(int Versao, DateTimeOffset Em, int? UsuarioId, string? Motivo, JsonElement Antes, JsonElement Depois);

        public static async Task<IReadOnlyList<VersaoRegraDto>> HistoricoLimitesAsync(NpgsqlConnection cx, int quantas, CancellationToken ct)
        {
            if (!await ExisteAsync(cx, "acompanhamento_saude_regra_versao", ct)) return Array.Empty<VersaoRegraDto>();
            var linhas = await cx.QueryAsync<(int Id, DateTimeOffset Em, int? Usuario, string? Motivo, string Antes, string Depois)>(new CommandDefinition(@"
SELECT id, em, usuario_id, motivo, antes::text, depois::text FROM acompanhamento_saude_regra_versao ORDER BY id DESC LIMIT @N;", new { N = quantas }, cancellationToken: ct));
            return linhas.Select(l => new VersaoRegraDto(l.Id, l.Em, l.Usuario, l.Motivo, JsonDocument.Parse(l.Antes).RootElement.Clone(), JsonDocument.Parse(l.Depois).RootElement.Clone())).ToList();
        }

        // ---- fatos

        private sealed class Linha
        {
            public Guid? LojaId { get; set; }
            public Guid? AcompanhamentoId { get; set; }
            public Guid? OportunidadeId { get; set; }
            public string Nome { get; set; } = string.Empty;
            public string? Fase { get; set; }
            public DateTimeOffset? FaseDesde { get; set; }
            public string? Situacao { get; set; }
            public DateTimeOffset? TesteTerminaEm { get; set; }
            public DateTimeOffset? CriadaEm { get; set; }
            public DateTimeOffset? OperacaoIniciadaEm { get; set; }
            public DateTime? PausaAte { get; set; }
            public int Pedidos7d { get; set; }
            public int Pedidos28d { get; set; }
            public int DiasComPedido28d { get; set; }
            public DateTimeOffset? UltimoPedidoEm { get; set; }
            public string? PlanoContratado { get; set; }
            public bool PagamentoConfirmado { get; set; }
            public decimal ValorAtrasado { get; set; }
            public DateTime? AtrasadoDesde { get; set; }
            public DateTime? ProximoVencimento { get; set; }
            public DateTimeOffset? ChamadoPendenteDesde { get; set; }
            public bool ChamadoPrazoVencido { get; set; }
            public DateTimeOffset? UltimoPassoEm { get; set; }
            public DateTime? PropostaValidadeAte { get; set; }
            public string[]? Modulos { get; set; }
            public string? FaixaGravada { get; set; }

            public SaudeEntrada ParaEntrada() => new()
            {
                LojaId = LojaId, AcompanhamentoId = AcompanhamentoId, OportunidadeId = OportunidadeId, Nome = Nome, Fase = Fase, FaseDesde = FaseDesde,
                Situacao = Situacao, TesteTerminaEm = TesteTerminaEm, CriadaEm = CriadaEm, OperacaoIniciadaEm = OperacaoIniciadaEm,
                PausaAte = Dia(PausaAte), Pedidos7d = Pedidos7d, Pedidos28d = Pedidos28d, DiasComPedido28d = DiasComPedido28d, UltimoPedidoEm = UltimoPedidoEm,
                PlanoContratado = PlanoContratado, PagamentoConfirmado = PagamentoConfirmado, ValorAtrasado = ValorAtrasado, AtrasadoDesde = Dia(AtrasadoDesde),
                ProximoVencimento = Dia(ProximoVencimento), ChamadoPendenteDesde = ChamadoPendenteDesde, ChamadoPrazoVencido = ChamadoPrazoVencido,
                UltimoPassoEm = UltimoPassoEm, PropostaValidadeAte = Dia(PropostaValidadeAte), Modulos = Modulos ?? Array.Empty<string>(), FaixaGravada = FaixaGravada,
            };

            private static DateOnly? Dia(DateTime? valor) => valor.HasValue ? DateOnly.FromDateTime(valor.Value) : null;
        }

        private const string UltimoPasso = @"
GREATEST(
  (SELECT MAX(p.criada_em) FROM acompanhamento_passagem p WHERE p.acompanhamento_id = ac.id AND p.autor_tipo = 'usuario'),
  (SELECT MAX(h.em) FROM acompanhamento_historico h WHERE h.acompanhamento_id = ac.id AND h.autor_tipo = 'usuario'),
  (SELECT MAX(pp.realizado_em) FROM acompanhamento_proximo_passo pp WHERE pp.acompanhamento_id = ac.id),
  {NOTA}
  {CHAMADO})";

        /// <summary>Fatos de todas as lojas (ou de uma). Pedidos: ultimos 28 dias, sem simulador nem cancelados (mesma conta da carteira).</summary>
        public static async Task<IReadOnlyList<SaudeEntrada>> EntradasDasLojasAsync(NpgsqlConnection cx, DateTimeOffset agora, Guid? lojaId, CancellationToken ct)
        {
            var hoje = FusoComercial.Data(agora).ToDateTime(TimeOnly.MinValue);
            var passo = UltimoPasso
                .Replace("{NOTA}", "(SELECT MAX(n.criado_em) FROM gestao_nota n WHERE n.estabelecimento_id = e.id),")
                .Replace("{CHAMADO}", "(SELECT MAX(m.criado_em) FROM chamado_mensagem m JOIN chamado c ON c.id = m.chamado_id WHERE c.estabelecimento_id = e.id AND m.da_zippy)");
            var sql = @"
SELECT e.id AS LojaId, ac.id AS AcompanhamentoId, ac.oportunidade_id AS OportunidadeId, COALESCE(e.nome_fantasia, '') AS Nome,
       ac.fase AS Fase, ac.fase_desde AS FaseDesde, COALESCE(e.situacao_comercial, 'ativo') AS Situacao,
       e.teste_termina_em AS TesteTerminaEm, e.data_criacao AS CriadaEm, e.operacao_iniciada_em AS OperacaoIniciadaEm,
       ac.pausa_ate::timestamp AS PausaAte, ac.faixa AS FaixaGravada,
       COALESCE(e.modulos_ativos::text[], ARRAY[]::text[]) AS Modulos,
       COALESCE(ped.p7, 0)::int AS Pedidos7d, COALESCE(ped.p28, 0)::int AS Pedidos28d, COALESCE(ped.dias, 0)::int AS DiasComPedido28d, ped.ultimo AS UltimoPedidoEm,
       ass.plano_chave AS PlanoContratado,
       COALESCE(ass.id IS NOT NULL AND EXISTS (SELECT 1 FROM assinatura_ciclo ci WHERE ci.id_assinatura = ass.id AND ci.status = 'pago'), FALSE) AS PagamentoConfirmado,
       COALESCE(fin.atrasado, 0) AS ValorAtrasado, fin.desde::timestamp AS AtrasadoDesde, fin.proximo::timestamp AS ProximoVencimento,
       ch.desde AS ChamadoPendenteDesde, COALESCE(ch.vencido, FALSE) AS ChamadoPrazoVencido,
       " + passo + @" AS UltimoPassoEm
  FROM estabelecimentos e
  LEFT JOIN acompanhamento ac ON ac.estabelecimento_id = e.id
  LEFT JOIN LATERAL (
       SELECT COUNT(*) FILTER (WHERE LEFT(p.data_pedido::text, 10) >= (CURRENT_DATE - 7)::text) AS p7, COUNT(*) AS p28,
              COUNT(DISTINCT LEFT(p.data_pedido::text, 10)) AS dias, MAX(p.data_pedido) AS ultimo
         FROM pedido p
        WHERE p.id_estabelecimento = e.id AND LEFT(p.data_pedido::text, 10) >= (CURRENT_DATE - 28)::text
          AND COALESCE(p.origem, '') <> 'simulador' AND COALESCE(p.status_pedido, 1) <> 6) ped ON TRUE
  LEFT JOIN LATERAL (
       SELECT a.id, a.plano_chave FROM assinatura a
        WHERE a.id_estabelecimento = e.id AND a.status IN ('pendente', 'ativa')
        ORDER BY (a.status = 'ativa') DESC, a.updated_at DESC LIMIT 1) ass ON TRUE
  LEFT JOIN LATERAL (
       -- Cartao recorrente antes do vencimento: o Mercado Pago cobra, nao ha o que pedir a loja (mesma regra dos avisos da assinatura).
       SELECT SUM(cb.valor) FILTER (WHERE cb.status IN ('aberta', 'processando') AND cb.vencimento < @Hoje) AS atrasado,
              MIN(cb.vencimento) FILTER (WHERE cb.status IN ('aberta', 'processando') AND cb.vencimento < @Hoje) AS desde,
              MIN(cb.vencimento) FILTER (WHERE cb.status IN ('aberta', 'processando') AND cb.vencimento >= @Hoje
                                           AND COALESCE(cb.meio, '') <> 'cartao_recorrente') AS proximo
         FROM comercial_cobranca cb WHERE cb.id_estabelecimento = e.id) fin ON TRUE
  LEFT JOIN LATERAL (
       SELECT MIN(c.criado_em) AS desde, BOOL_OR(c.prazo_em < NOW()) AS vencido FROM chamado c
        WHERE c.estabelecimento_id = e.id AND c.status = 'aberto') ch ON TRUE
 WHERE (@Loja::uuid IS NULL OR e.id = @Loja)
 ORDER BY e.nome_fantasia;";
            var linhas = await cx.QueryAsync<Linha>(new CommandDefinition(sql, new { Hoje = hoje, Loja = lojaId }, cancellationToken: ct));
            return linhas.Select(l => l.ParaEntrada()).ToList();
        }

        /// <summary>Oportunidades ainda sem loja (conversa e proposta). Sem as tabelas do CRM, volta vazio.</summary>
        public static async Task<IReadOnlyList<SaudeEntrada>> EntradasDasOportunidadesAsync(NpgsqlConnection cx, CancellationToken ct)
        {
            var crm = await ExisteAsync(cx, "crm_oportunidade", ct);
            var passo = UltimoPasso.Replace("{NOTA}", "").Replace("{CHAMADO}", "NULL");
            var sql = @"
SELECT NULL::uuid AS LojaId, ac.id AS AcompanhamentoId, ac.oportunidade_id AS OportunidadeId, " + (crm ? "COALESCE(o.nome_empresa, '')" : "''") + @" AS Nome,
       ac.fase AS Fase, ac.fase_desde AS FaseDesde, NULL AS Situacao, ac.criado_em AS CriadaEm,
       ac.pausa_ate::timestamp AS PausaAte, ac.faixa AS FaixaGravada,
       (SELECT v.validade_ate::timestamp FROM acompanhamento_proposta_versao v WHERE v.oportunidade_id = ac.oportunidade_id AND v.status = 'enviada'
         ORDER BY v.versao DESC LIMIT 1) AS PropostaValidadeAte,
       " + passo + @" AS UltimoPassoEm
  FROM acompanhamento ac " + (crm ? "LEFT JOIN crm_oportunidade o ON o.id = ac.oportunidade_id" : "") + @"
 WHERE ac.estabelecimento_id IS NULL AND ac.fase IN ('conversa', 'proposta')
 ORDER BY ac.fase_desde;";
            var linhas = await cx.QueryAsync<Linha>(new CommandDefinition(sql, cancellationToken: ct));
            return linhas.Select(l => l.ParaEntrada()).ToList();
        }

        // ---- gravacao pelo worker (so faixa e avisos; pagamento fica intocado)

        /// <summary>Grava a faixa nova com historico (autor sistema) quando mudou. Trava otimista pela faixa antiga: duas instancias nao duplicam.</summary>
        public static async Task<int> PersistirFaixasAsync(NpgsqlConnection cx, IEnumerable<(SaudeEntrada Entrada, SaudeAvaliacao Avaliacao)> itens, CancellationToken ct)
        {
            var mudou = 0;
            foreach (var (e, a) in itens)
            {
                if (e.AcompanhamentoId == null || e.FaixaGravada == a.Faixa || !FaixasAcompanhamento.Existe(a.Faixa)) continue;
                await using var tx = await cx.BeginTransactionAsync(ct);
                var linhas = await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE acompanhamento SET faixa = @Nova, faixa_desde = NOW(), faixa_motivos = CAST(@Motivos AS jsonb), faixa_calculo = @Calculo, faixa_limites = @Limites
 WHERE id = @Id AND faixa = @Antes;",
                    new { Nova = a.Faixa, Motivos = JsonSerializer.Serialize(a.Motivos, Json), Calculo = a.Calculo, Limites = a.Limites, Id = e.AcompanhamentoId, Antes = e.FaixaGravada }, tx, cancellationToken: ct));
                if (linhas == 1)
                {
                    await cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO acompanhamento_historico (acompanhamento_id, tipo, de, para, autor_tipo, motivo) VALUES (@Id, 'faixa', @De, @Para, 'sistema', @Motivo);",
                        new { Id = e.AcompanhamentoId, De = e.FaixaGravada, Para = a.Faixa, Motivo = a.Resumo }, tx, cancellationToken: ct));
                    mudou++;
                }
                await tx.CommitAsync(ct);
            }
            return mudou;
        }

        /// <summary>Marcos do fim do teste (4, 3, 2, 1, 0 dias, terminou, escalonar): um registro por marco e por teste, sem repetir.</summary>
        public static async Task RegistrarAvisosDoTesteAsync(NpgsqlConnection cx, SaudeEntrada e, SaudeAvaliacao a, CancellationToken ct)
        {
            if (e.LojaId == null || e.TesteTerminaEm == null) return;
            var fim = FusoComercial.Data(e.TesteTerminaEm.Value).ToDateTime(TimeOnly.MinValue);
            foreach (var m in a.Motivos)
            {
                var marco = m.Codigo switch
                {
                    "teste_fim_proximo" => $"faltam_{m.Contador}",
                    "teste_sem_pagamento" => "terminou",
                    "escalonar_sem_pagamento" => "escalonar",
                    _ => null
                };
                if (marco == null) continue;
                await cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO acompanhamento_teste_aviso (estabelecimento_id, teste_termina_em, marco, faixa, texto) VALUES (@Loja, @Fim, @Marco, @Faixa, @Texto)
ON CONFLICT (estabelecimento_id, teste_termina_em, marco) DO NOTHING;",
                    new { Loja = e.LojaId, Fim = fim, Marco = marco, Faixa = m.Faixa, Texto = m.Texto }, cancellationToken: ct));
            }
        }

        public sealed record AvisoTesteDto(string Marco, string Faixa, string Texto, DateTimeOffset CriadoEm);

        public static async Task<IReadOnlyList<AvisoTesteDto>> AvisosDoTesteAsync(NpgsqlConnection cx, Guid loja, DateTimeOffset? terminaEm, CancellationToken ct)
        {
            if (terminaEm == null || !await ExisteAsync(cx, "acompanhamento_teste_aviso", ct)) return Array.Empty<AvisoTesteDto>();
            var linhas = await cx.QueryAsync<AvisoTesteDto>(new CommandDefinition(@"
SELECT marco AS Marco, faixa AS Faixa, texto AS Texto, criado_em AS CriadoEm FROM acompanhamento_teste_aviso
 WHERE estabelecimento_id = @Loja AND teste_termina_em = @Fim ORDER BY criado_em;",
                new { Loja = loja, Fim = FusoComercial.Data(terminaEm.Value).ToDateTime(TimeOnly.MinValue) }, cancellationToken: ct));
            return linhas.ToList();
        }
    }

    public sealed class SaudeException : Exception
    {
        public SaudeException(int status, string code, string message, object? details = null) : base(message)
        {
            Status = status;
            Code = code;
            Details = details;
        }

        public int Status { get; }
        public string Code { get; }
        public object? Details { get; }
    }
}
