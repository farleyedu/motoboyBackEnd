using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Acompanhamento.Fases;
using APIBack.Payments.Assinaturas;
using APIBack.Service;
using Dapper;
using Npgsql;
using Fx = APIBack.Acompanhamento.Fases.FaixasAcompanhamento;

namespace APIBack.Acompanhamento.Saude
{
    public sealed record CartaoDto(Guid? AcompanhamentoId, Guid? LojaId, Guid? OportunidadeId, string Nome, string Fase, DateTimeOffset? FaseDesde, SaudeAvaliacao Saude);

    public sealed record ResumoQuadroDto(int Total, IReadOnlyDictionary<string, int> PorFaixa, IReadOnlyDictionary<string, int> PorFase, int Neutras, int AEscalar);

    public sealed record MovimentoDto(long Id, Guid AcompanhamentoId, string Nome, string Tipo, string? De, string Para, DateTimeOffset Em, string AutorTipo, string? Motivo);

    public sealed record IncidentesDto(bool Disponivel, string Motivo, IReadOnlyList<object> Itens);

    public sealed record QuadroDto(DateTimeOffset GeradoEm, int Calculo, int Limites, ResumoQuadroDto Resumo, IReadOnlyList<CartaoDto> Cartoes,
        IReadOnlyList<MovimentoDto> Historico, IncidentesDto Incidentes);

    public sealed record RegraValorDto(string Chave, string Descricao, string Unidade, int Minimo, int Maximo, bool Anulavel, int? Padrao, int? Valor);

    public sealed record RegrasDto(int Versao, int Calculo, IReadOnlyList<RegraValorDto> Regras, IReadOnlyList<SaudeSql.VersaoRegraDto> Historico);

    public sealed record LojaAfetadaDto(Guid? AcompanhamentoId, Guid? LojaId, string Nome, string Fase, string FaixaAtual, string FaixaNova, string? MotivoAtual, string? MotivoNovo);

    public sealed record PreviaDto(int LimitesAtuais, IReadOnlyDictionary<string, int?> Mudancas, int Avaliadas, int MudamDeFaixa,
        IReadOnlyDictionary<string, int> PorFaixaAtual, IReadOnlyDictionary<string, int> PorFaixaNova, IReadOnlyList<LojaAfetadaDto> Afetadas);

    public sealed record UsoDoTesteDto(int Pedidos, int DiasComPedido, int DiasDeTeste, IReadOnlyList<string> ModulosAtivos);

    public sealed record SugestaoDto(string Regra, string Acao, string Motivo);

    /// <summary>Falha registrada para a loja que tocou o periodo do teste. Executor "zippy" pesa a favor da loja na decisao.</summary>
    public sealed record FalhaNoTesteDto(Guid Id, string Categoria, DateTimeOffset Inicio, DateTimeOffset? Fim, string CausaProvavel, string Executor, string Estado);

    public sealed record PedidosDoDiaDto(DateOnly Dia, int Pedidos);

    public sealed record EscalonamentoDto(bool Ligado, int? Dias, bool Atingido);

    public sealed record FimDeTesteDto(
        Guid LojaId, string Nome, string Situacao, DateTimeOffset? TesteTerminaEm, int? DiasRestantes, bool Terminou, int? DiasOperandoSemPagamento,
        string? PlanoContratado, bool PagamentoConfirmado, string AtivacaoAoTerminar, bool AcessoCortadoPorPrazo, UsoDoTesteDto Uso,
        SugestaoDto? Sugestao, EscalonamentoDto Escalonamento, SaudeAvaliacao Saude, IReadOnlyList<SaudeSql.AvisoTesteDto> Avisos, IReadOnlyList<string> Indisponiveis,
        IReadOnlyList<FalhaNoTesteDto>? FalhasNoTeste = null, IReadOnlyList<PedidosDoDiaDto>? PedidosPorDia = null);

    public interface ISaudeService
    {
        Task<QuadroDto> QuadroAsync(CancellationToken ct = default);
        Task<RegrasDto> RegrasAsync(CancellationToken ct = default);
        Task<RegrasDto> SalvarRegrasAsync(IReadOnlyDictionary<string, int?> valores, int? versaoEsperada, string? motivo, int usuarioId, CancellationToken ct = default);
        Task<PreviaDto> PreviaAsync(IReadOnlyDictionary<string, int?> mudancas, CancellationToken ct = default);
        Task<FimDeTesteDto> FimDeTesteAsync(Guid lojaId, CancellationToken ct = default);
    }

    /// <summary>Quadro, regras e fim do teste. Usa o mesmo <see cref="SaudeCalculador"/> da fila Hoje (que le a saude pela carteira).</summary>
    public sealed class SaudeService : ISaudeService
    {
        private readonly NpgsqlDataSource _db;
        private readonly TimeProvider _clock;

        public SaudeService(NpgsqlDataSource db, TimeProvider? clock = null)
        {
            _db = db;
            _clock = clock ?? TimeProvider.System;
        }

        /// <summary>Avalia todas as lojas pelos limites atuais (ou pelos informados). Quem chama escolhe o que fazer com o resultado.</summary>
        public static async Task<IReadOnlyList<(SaudeEntrada Entrada, SaudeAvaliacao Saude)>> AvaliarLojasAsync(
            NpgsqlConnection cx, DateTimeOffset agora, LimitesSaude? limites, bool comOportunidades, CancellationToken ct)
        {
            limites ??= await SaudeSql.LimitesAsync(cx, ct);
            var entradas = (await SaudeSql.EntradasDasLojasAsync(cx, agora, null, ct)).ToList();
            if (comOportunidades) entradas.AddRange(await SaudeSql.EntradasDasOportunidadesAsync(cx, ct));
            return entradas.Select(e => (e, SaudeCalculador.Avaliar(e, limites, agora))).ToList();
        }

        public async Task<QuadroDto> QuadroAsync(CancellationToken ct = default)
        {
            var agora = _clock.GetUtcNow();
            await using var cx = await _db.OpenConnectionAsync(ct);
            var limites = await SaudeSql.LimitesAsync(cx, ct);
            var avaliadas = await AvaliarLojasAsync(cx, agora, limites, true, ct);
            var cartoes = avaliadas
                .Where(x => FasesAcompanhamento.NoQuadro.Contains(x.Entrada.FaseEfetiva) && x.Entrada.Situacao != SituacoesComerciais.Bloqueado)
                .Select(x => new CartaoDto(x.Entrada.AcompanhamentoId, x.Entrada.LojaId, x.Entrada.OportunidadeId, x.Entrada.Nome, x.Entrada.FaseEfetiva, x.Entrada.FaseDesde, x.Saude))
                .OrderByDescending(c => SaudeCalculador.Peso(c.Saude.Faixa)).ThenBy(c => c.Nome, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            var porFaixa = new Dictionary<string, int> { [Fx.Bem] = 0, [Fx.Atencao] = 0, [Fx.PrecisaDeAjuda] = 0 };
            foreach (var c in cartoes) porFaixa[c.Saude.Faixa]++;
            var porFase = FasesAcompanhamento.NoQuadro.ToDictionary(f => f, f => cartoes.Count(c => c.Fase == f));
            var resumo = new ResumoQuadroDto(cartoes.Count, porFaixa, porFase, cartoes.Count(c => c.Saude.Neutra), cartoes.Count(c => c.Saude.EscalarAoGestor));

            return new QuadroDto(agora, SaudeCalculador.Versao, limites.Versao, resumo, cartoes, await MovimentosDaSemanaAsync(cx, agora, ct),
                new IncidentesDto(false, "O módulo de incidentes ainda não existe neste servidor; nada é simulado.", Array.Empty<object>()));
        }

        private static async Task<IReadOnlyList<MovimentoDto>> MovimentosDaSemanaAsync(NpgsqlConnection cx, DateTimeOffset agora, CancellationToken ct)
        {
            var linhas = await cx.QueryAsync<MovimentoDto>(new CommandDefinition(@"
SELECT h.id AS Id, h.acompanhamento_id AS AcompanhamentoId, COALESCE(e.nome_fantasia, '') AS Nome, h.tipo AS Tipo, h.de AS De, h.para AS Para,
       h.em AS Em, h.autor_tipo AS AutorTipo, h.motivo AS Motivo
  FROM acompanhamento_historico h
  JOIN acompanhamento ac ON ac.id = h.acompanhamento_id
  LEFT JOIN estabelecimentos e ON e.id = ac.estabelecimento_id
 WHERE h.em >= @Desde ORDER BY h.em DESC, h.id DESC LIMIT 200;", new { Desde = agora.AddDays(-7) }, cancellationToken: ct));
            return linhas.ToList();
        }

        public async Task<RegrasDto> RegrasAsync(CancellationToken ct = default)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            return await MontarRegrasAsync(cx, await SaudeSql.LimitesAsync(cx, ct), ct);
        }

        private static async Task<RegrasDto> MontarRegrasAsync(NpgsqlConnection cx, LimitesSaude limites, CancellationToken ct)
        {
            var valores = limites.ComoDicionario();
            var regras = LimitesSaude.Definicoes.Select(d => new RegraValorDto(d.Chave, d.Descricao, d.Unidade, d.Minimo, d.Maximo, d.Anulavel, d.Padrao, valores[d.Chave])).ToList();
            return new RegrasDto(limites.Versao, SaudeCalculador.Versao, regras, await SaudeSql.HistoricoLimitesAsync(cx, 20, ct));
        }

        public async Task<RegrasDto> SalvarRegrasAsync(IReadOnlyDictionary<string, int?> valores, int? versaoEsperada, string? motivo, int usuarioId, CancellationToken ct = default)
        {
            if (valores.Count == 0) throw new SaudeException(422, "REGRAS_INVALIDAS", "Informe ao menos uma regra para mudar.");
            await using var cx = await _db.OpenConnectionAsync(ct);
            if (!await SaudeSql.ExisteAsync(cx, "acompanhamento_saude_regra", ct))
                throw new SaudeException(422, "MIGRACAO_PENDENTE", "A migração 20261127_02_acompanhamento_saude ainda não foi aplicada.");
            var (limites, _) = await SaudeSql.SalvarLimitesAsync(cx, valores, versaoEsperada, usuarioId, motivo, ct);
            return await MontarRegrasAsync(cx, limites, ct);
        }

        public async Task<PreviaDto> PreviaAsync(IReadOnlyDictionary<string, int?> mudancas, CancellationToken ct = default)
        {
            var agora = _clock.GetUtcNow();
            await using var cx = await _db.OpenConnectionAsync(ct);
            var atuais = await SaudeSql.LimitesAsync(cx, ct);
            var (novos, erros) = atuais.Aplicar(mudancas);
            if (erros.Count > 0) throw new SaudeException(422, "REGRAS_INVALIDAS", "Há regras inválidas na prévia.", erros);

            var entradas = (await SaudeSql.EntradasDasLojasAsync(cx, agora, null, ct)).ToList();
            entradas.AddRange(await SaudeSql.EntradasDasOportunidadesAsync(cx, ct));
            entradas = entradas.Where(e => FasesAcompanhamento.NoQuadro.Contains(e.FaseEfetiva) && e.Situacao != SituacoesComerciais.Bloqueado).ToList();

            var afetadas = new List<LojaAfetadaDto>();
            var antes = new Dictionary<string, int> { [Fx.Bem] = 0, [Fx.Atencao] = 0, [Fx.PrecisaDeAjuda] = 0 };
            var depois = new Dictionary<string, int>(antes);
            foreach (var e in entradas)
            {
                var a = SaudeCalculador.Avaliar(e, atuais, agora);
                var n = SaudeCalculador.Avaliar(e, novos, agora);
                antes[a.Faixa]++;
                depois[n.Faixa]++;
                if (a.Faixa != n.Faixa)
                    afetadas.Add(new LojaAfetadaDto(e.AcompanhamentoId, e.LojaId, e.Nome, e.FaseEfetiva, a.Faixa, n.Faixa, a.Principal?.Texto, n.Principal?.Texto));
            }
            return new PreviaDto(atuais.Versao, mudancas, entradas.Count, afetadas.Count, antes, depois,
                afetadas.OrderByDescending(x => SaudeCalculador.Peso(x.FaixaNova)).ThenBy(x => x.Nome, StringComparer.CurrentCultureIgnoreCase).ToList());
        }

        public async Task<FimDeTesteDto> FimDeTesteAsync(Guid lojaId, CancellationToken ct = default)
        {
            var agora = _clock.GetUtcNow();
            await using var cx = await _db.OpenConnectionAsync(ct);
            var limites = await SaudeSql.LimitesAsync(cx, ct);
            var e = (await SaudeSql.EntradasDasLojasAsync(cx, agora, lojaId, ct)).FirstOrDefault()
                ?? throw new SaudeException(404, "LOJA_NAO_ENCONTRADA", "Loja não encontrada.");
            var saude = SaudeCalculador.Avaliar(e, limites, agora);

            var emTeste = e.Situacao == SituacoesComerciais.Teste;
            var hoje = FusoComercial.Data(agora);
            int? restantes = e.TesteTerminaEm is { } fim ? FusoComercial.Data(fim).DayNumber - hoje.DayNumber : null;
            var terminou = restantes is < 0;
            var pagou = e.PlanoContratado != null && e.PagamentoConfirmado;
            var inicio = e.OperacaoIniciadaEm ?? e.CriadaEm;
            var diasDeTeste = inicio is { } i ? Math.Max(0, hoje.DayNumber - FusoComercial.Data(i).DayNumber) : 0;

            var falhas = await FalhasNoPeriodoAsync(cx, lojaId, inicio, e.TesteTerminaEm, agora, ct);
            var porDia = await PedidosPorDiaAsync(cx, lojaId, hoje, ct);
            var falhasNossas = falhas.Where(f => f.Executor == "zippy").ToList();

            var sugestao = SugerirFimDeTeste(emTeste, terminou, pagou, e.Pedidos28d, limites.TesteUsoMinimoPedidos, falhasNossas);

            var sem = terminou && !pagou ? -restantes : null;
            // "modulos_usados" continua indisponivel: so ha os modulos ligados (e.Modulos), nao o uso real de cada um. Falhas agora vem do banco.
            var indisponiveis = saude.Indisponiveis.Concat(new[] { "modulos_usados" }).Distinct().ToList();
            return new FimDeTesteDto(e.LojaId!.Value, e.Nome, e.Situacao ?? SituacoesComerciais.Ativo, e.TesteTerminaEm, restantes, terminou, sem,
                e.PlanoContratado, e.PagamentoConfirmado,
                !emTeste ? "nao_se_aplica" : pagou ? "automatica" : "aguarda_decisao_humana", AcessoCortadoPorPrazo: false,
                new UsoDoTesteDto(e.Pedidos28d, e.DiasComPedido28d, diasDeTeste, e.Modulos), sugestao,
                new EscalonamentoDto(limites.EscalonarSemPagamentoDias != null, limites.EscalonarSemPagamentoDias, saude.EscalarAoGestor),
                saude, await SaudeSql.AvisosDoTesteAsync(cx, lojaId, e.TesteTerminaEm, ct), indisponiveis, falhas, porDia);
        }

        /// <summary>
        /// Regra pura da sugestao. A falha nossa pesa a favor da loja: pouco uso com falha da Zippy no periodo pede mais tempo
        /// (prorrogar, ou dar dias sem cobrar se o teste ja acabou), nunca pressao para decidir.
        /// </summary>
        public static SugestaoDto? SugerirFimDeTeste(bool emTeste, bool terminou, bool pagou, int pedidos, int usoMinimo, IReadOnlyList<FalhaNoTesteDto> falhasNossas)
        {
            if (!emTeste && !(terminou && !pagou)) return null;
            if (pagou) return new SugestaoDto("plano_pago", "ativar_ao_terminar", "Plano escolhido e pago: a loja é ativada sozinha quando o ciclo começar.");
            if (falhasNossas.Count > 0 && pedidos < usoMinimo)
                return new SugestaoDto("falha_nossa_pouco_uso", terminou ? "dar_dias_sem_cobrar" : "prorrogar_teste",
                    $"{pedidos} pedidos, mas houve {(falhasNossas.Count == 1 ? "1 falha nossa" : falhasNossas.Count + " falhas nossas")} no período ({falhasNossas[0].CausaProvavel}). Ligue, explique e ofereça mais 7 dias antes de falar de plano.");
            if (pedidos >= usoMinimo) return new SugestaoDto("uso_suficiente", "propor_plano", $"{pedidos} pedidos no teste (mínimo {usoMinimo}): vale propor o plano.");
            if (pedidos > 0) return new SugestaoDto("uso_baixo", "conversar_sobre_uso", $"{pedidos} pedidos no teste (mínimo {usoMinimo}): entender o que travou antes de falar de plano.");
            return new SugestaoDto("sem_uso", "investigar_motivo", "Nenhum pedido no teste: descubra se a loja chegou a operar antes de decidir.");
        }

        /// <summary>Falhas da loja que tocaram o periodo do teste (do inicio ate o fim ou hoje). Sem a tabela de falhas, volta vazio: nada e inventado.</summary>
        private static async Task<IReadOnlyList<FalhaNoTesteDto>> FalhasNoPeriodoAsync(NpgsqlConnection cx, Guid lojaId, DateTimeOffset? inicio, DateTimeOffset? fimTeste, DateTimeOffset agora, CancellationToken ct)
        {
            if (!await SaudeSql.ExisteAsync(cx, "acompanhamento_falha", ct)) return Array.Empty<FalhaNoTesteDto>();
            var de = inicio ?? agora.AddDays(-15);
            var ate = fimTeste is { } f && f > agora ? agora : (fimTeste ?? agora);
            var linhas = await cx.QueryAsync<(Guid Id, string Categoria, DateTime Inicio, DateTime? Fim, string Causa, string Executor, string Estado)>(new CommandDefinition(@"
SELECT id, categoria, inicio, fim, causa_provavel, executor, estado
  FROM acompanhamento_falha
 WHERE loja_id = @Loja AND inicio <= @Ate AND (fim IS NULL OR fim >= @De)
 ORDER BY inicio;", new { Loja = lojaId, De = de.UtcDateTime, Ate = ate.UtcDateTime }, cancellationToken: ct));
            static DateTimeOffset U(DateTime d) => new(DateTime.SpecifyKind(d, DateTimeKind.Utc));
            return linhas.Select(l => new FalhaNoTesteDto(l.Id, l.Categoria, U(l.Inicio), l.Fim == null ? null : U(l.Fim.Value), l.Causa, l.Executor, l.Estado)).ToList();
        }

        /// <summary>Pedidos por dia dos ultimos 28 dias (mesma conta da carteira: sem simulador nem cancelados). So dias com pedido; o resto e zero.</summary>
        private static async Task<IReadOnlyList<PedidosDoDiaDto>> PedidosPorDiaAsync(NpgsqlConnection cx, Guid lojaId, DateOnly hoje, CancellationToken ct)
        {
            var linhas = await cx.QueryAsync<(string Dia, int Pedidos)>(new CommandDefinition(@"
SELECT LEFT(p.data_pedido::text, 10) AS dia, COUNT(*)::int
  FROM pedido p
 WHERE p.id_estabelecimento = @Loja AND LEFT(p.data_pedido::text, 10) >= @Desde
   AND COALESCE(p.origem, '') <> 'simulador' AND COALESCE(p.status_pedido, 1) <> 6
 GROUP BY 1 ORDER BY 1;", new { Loja = lojaId, Desde = hoje.AddDays(-28).ToString("yyyy-MM-dd") }, cancellationToken: ct));
            return linhas.Where(l => DateOnly.TryParse(l.Dia, out _)).Select(l => new PedidosDoDiaDto(DateOnly.Parse(l.Dia), l.Pedidos)).ToList();
        }

        /// <summary>Corpo de ?previa= (JSON: {"dias_sem_pedido": 10, "escalonar_sem_pagamento_dias": null}). Invalido = 422.</summary>
        public static IReadOnlyDictionary<string, int?> LerMudancas(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
                var mudancas = new Dictionary<string, int?>();
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    if (p.Value.ValueKind == JsonValueKind.Null) mudancas[p.Name] = null;
                    else if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var n)) mudancas[p.Name] = n;
                    else throw new SaudeException(422, "REGRAS_INVALIDAS", $"{p.Name} deve ser um número inteiro (ou vazio, quando a regra permite).");
                }
                return mudancas;
            }
            catch (JsonException)
            {
                throw new SaudeException(422, "PREVIA_INVALIDA", "A prévia deve ser um JSON com as regras a mudar, por exemplo {\"dias_sem_pedido\":10}.");
            }
        }
    }
}
