using System.Text.Json.Nodes;
using APIBack.Mesas.Auditoria;
using APIBack.Mesas.Nucleo;
using Dapper;
using Npgsql;
using R = APIBack.Mesas.Sessao.SessaoRegras;

namespace APIBack.Mesas.Sessao;

public interface ISalaoService
{
    Task<IReadOnlyList<MesaDoSalaoDto>> SalaoAsync(Guid loja, string? pisoId, CancellationToken ct);
    Task<ResumoMesaDto> MesaAsync(Guid loja, Guid mesaId, CancellationToken ct);
    Task<RelatorioSessaoDto> RelatorioAsync(Guid loja, Guid sessaoId, CancellationToken ct);
}

/// <summary>Leituras do salão e da mesa (CONTRATO-API §4.1). Alertas calculados aqui, com os limites de <c>mesas_config</c>.</summary>
public sealed class SalaoService(NpgsqlDataSource source, IEnumerable<IMesasAlertaFonte> fontes) : ISalaoService
{
    private sealed class MesaRow
    {
        public Guid MesaId { get; set; }
        public string Numero { get; set; } = "";
        public string PisoId { get; set; } = "";
        public string? SetorId { get; set; }
        public short Capacidade { get; set; }
        public string Status { get; set; } = "";
        public int Versao { get; set; }
        public string? MotivoBloqueio { get; set; }
        public Guid? SessaoId { get; set; }
        public Guid? PrincipalId { get; set; }
        public DateTime? AbertaEm { get; set; }
        public DateTime? ContaPedidaEm { get; set; }
        public DateTime? UltimoPedidoEm { get; set; }
        public Guid? GarcomId { get; set; }
        public string? GarcomNome { get; set; }
        public int Anonimos { get; set; }
        public int Pessoas { get; set; }
        public bool Chamado { get; set; }
        public bool Autorizacao { get; set; }
    }

    private sealed class SessaoRow
    {
        public Guid Id { get; set; }
        public Guid MesaId { get; set; }
        public string Estado { get; set; } = "";
        public DateTime AbertaEm { get; set; }
        public string AbertaPor { get; set; } = "";
        public string? AbertaPorId { get; set; }
        public Guid? GarcomId { get; set; }
        public string? GarcomNome { get; set; }
        public string TipoComanda { get; set; } = "";
        public int OcupantesAnonimos { get; set; }
        public DateTime? ContaPedidaEm { get; set; }
        public DateTime? UltimoPedidoEm { get; set; }
        public DateTime? EncerradaEm { get; set; }
        public string? EncerradaPor { get; set; }
        public int ReabertaVezes { get; set; }
        public Guid? ReservaId { get; set; }
        public string QrToken { get; set; } = "";
        public string MesaNumero { get; set; } = "";
    }

    private const string MesaSelect = """
        SELECT m.id AS mesaId, m.numero, m.piso_id AS pisoId, m.setor_id AS setorId, m.capacidade, e.status, e.versao,
               e.motivo_bloqueio AS motivoBloqueio, e.sessao_id AS sessaoId, s.mesa_id AS principalId, s.aberta_em AS abertaEm,
               s.conta_pedida_em AS contaPedidaEm, s.ultimo_pedido_em AS ultimoPedidoEm, s.garcom_id AS garcomId, g.nome::text AS garcomNome,
               COALESCE(s.ocupantes_anonimos, 0)::int AS anonimos,
               (SELECT count(*) FROM mesa_pessoa p WHERE p.sessao_id = s.id AND p.saiu_em IS NULL)::int AS pessoas,
               EXISTS (SELECT 1 FROM mesa_chamado c WHERE c.sessao_id = s.id AND c.estado IN ('aberto', 'a_caminho')) AS chamado,
               EXISTS (SELECT 1 FROM mesa_solicitacao so WHERE so.estado = 'pendente' AND (so.sessao_id = s.id OR so.mesa_id = m.id)) AS autorizacao
          FROM mesa m
          JOIN mesa_estado e ON e.mesa_id = m.id
          LEFT JOIN mesa_sessao s ON s.id = e.sessao_id
          LEFT JOIN gestao_pessoas g ON g.id = s.garcom_id
        """;

    private const string SessaoSelect = """
        SELECT s.id, s.mesa_id AS mesaId, s.estado, s.aberta_em AS abertaEm, s.aberta_por AS abertaPor, s.aberta_por_id AS abertaPorId,
               s.garcom_id AS garcomId, g.nome::text AS garcomNome, s.tipo_comanda AS tipoComanda, s.ocupantes_anonimos::int AS ocupantesAnonimos,
               s.conta_pedida_em AS contaPedidaEm, s.ultimo_pedido_em AS ultimoPedidoEm, s.encerrada_em AS encerradaEm, s.encerrada_por AS encerradaPor,
               s.reaberta_vezes::int AS reabertaVezes, s.reserva_id AS reservaId, s.qr_token AS qrToken, m.numero AS mesaNumero
          FROM mesa_sessao s
          JOIN mesa m ON m.id = s.mesa_id
          LEFT JOIN gestao_pessoas g ON g.id = s.garcom_id
        """;

    public async Task<IReadOnlyList<MesaDoSalaoDto>> SalaoAsync(Guid loja, string? pisoId, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        var rows = (await c.QueryAsync<MesaRow>(new CommandDefinition(MesaSelect + """
             WHERE m.estabelecimento_id = @loja AND m.ativa AND (CAST(@pisoId AS text) IS NULL OR m.piso_id = @pisoId)
             ORDER BY length(m.numero), m.numero
            """, new { loja, pisoId = string.IsNullOrWhiteSpace(pisoId) ? null : pisoId }, cancellationToken: ct))).ToList();
        var (agora, limites, extras) = await Contexto(c, loja, rows.Where(r => r.SessaoId is not null).Select(r => r.SessaoId!.Value).Distinct().ToList(), ct);
        return rows.Select(r =>
        {
            var alertas = Alertas(r, agora, limites, extras);
            return new MesaDoSalaoDto(r.MesaId, r.Numero, r.PisoId, r.SetorId, r.Capacidade, r.Status, r.Versao, r.MotivoBloqueio, r.SessaoId,
                r.PrincipalId is Guid p && p != r.MesaId ? p : null, alertas, R.Icone(alertas), r.GarcomId, r.GarcomNome,
                r.Pessoas + r.Anonimos, R.Minutos(r.AbertaEm, agora), r.AbertaEm, null);
        }).ToList();
    }

    public async Task<ResumoMesaDto> MesaAsync(Guid loja, Guid mesaId, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        var r = await c.QueryFirstOrDefaultAsync<MesaRow>(new CommandDefinition(MesaSelect + " WHERE m.estabelecimento_id = @loja AND m.id = @mesaId",
            new { loja, mesaId }, cancellationToken: ct)) ?? throw MesasException.NaoEncontrado("Mesa não encontrada.");
        var sessaoIds = r.SessaoId is Guid sid ? new List<Guid> { sid } : new List<Guid>();
        var (agora, limites, extras) = await Contexto(c, loja, sessaoIds, ct);
        var alertas = Alertas(r, agora, limites, extras);

        SessaoResumoDto? sessao = null;
        IReadOnlyList<MesaDaSessaoDto> mesas = Array.Empty<MesaDaSessaoDto>();
        IReadOnlyList<PessoaDto> pessoas = Array.Empty<PessoaDto>();
        IReadOnlyList<ComandaDto> comandas = Array.Empty<ComandaDto>();
        IReadOnlyList<ChamadoDto> chamados = Array.Empty<ChamadoDto>();
        IReadOnlyList<AnotacaoResumoDto> anotacoes = Array.Empty<AnotacaoResumoDto>();
        if (r.SessaoId is Guid s)
        {
            var row = await c.QuerySingleAsync<SessaoRow>(new CommandDefinition(SessaoSelect + " WHERE s.id = @s", new { s }, cancellationToken: ct));
            sessao = Sessao(row);
            mesas = (await c.QueryAsync<MesaDaSessaoDto>(new CommandDefinition("""
                SELECT sm.mesa_id AS "MesaId", m.numero AS "Numero", e.versao AS "Versao", (sm.mesa_id = ms.mesa_id) AS "Principal"
                  FROM mesa_sessao_mesa sm
                  JOIN mesa m ON m.id = sm.mesa_id
                  JOIN mesa_estado e ON e.mesa_id = sm.mesa_id
                  JOIN mesa_sessao ms ON ms.id = sm.sessao_id
                 WHERE sm.sessao_id = @s AND sm.ate IS NULL
                 ORDER BY 4 DESC, length(m.numero), m.numero
                """, new { s }, cancellationToken: ct))).ToList();
            pessoas = await Pessoas(c, s, true, ct);
            comandas = await Comandas(c, s, true, ct);
            chamados = (await c.QueryAsync<ChamadoDto>(new CommandDefinition("""
                SELECT id AS "Id", pessoa_id AS "PessoaId", tipo AS "Tipo", motivo AS "Motivo", estado AS "Estado", garcom_id AS "GarcomId",
                       criado_em AS "CriadoEm", a_caminho_em AS "ACaminhoEm"
                  FROM mesa_chamado WHERE sessao_id = @s AND estado IN ('aberto', 'a_caminho') ORDER BY criado_em
                """, new { s }, cancellationToken: ct))).ToList();
            anotacoes = (await c.QueryAsync<AnotacaoResumoDto>(new CommandDefinition("""
                SELECT id AS "Id", tipo AS "Tipo", por AS "Por", texto AS "Texto", criado_em AS "CriadoEm"
                  FROM mesa_anotacao WHERE sessao_id = @s ORDER BY criado_em
                """, new { s }, cancellationToken: ct))).ToList();
        }
        return new ResumoMesaDto(r.MesaId, r.Numero, r.Capacidade, r.PisoId, r.SetorId, r.Status, r.Versao, r.MotivoBloqueio, r.SessaoId, sessao, mesas,
            r.PrincipalId is Guid p && p != r.MesaId ? p : null, alertas, R.Icone(alertas), R.Minutos(r.AbertaEm, agora), r.Pessoas + r.Anonimos,
            pessoas, comandas, chamados, anotacoes);
    }

    public async Task<RelatorioSessaoDto> RelatorioAsync(Guid loja, Guid sessaoId, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        var row = await c.QueryFirstOrDefaultAsync<SessaoRow>(new CommandDefinition(SessaoSelect + " WHERE s.id = @sessaoId AND s.estabelecimento_id = @loja",
            new { sessaoId, loja }, cancellationToken: ct)) ?? throw MesasException.NaoEncontrado("Sessão não encontrada.");
        var eventos = await AuditoriaService.Eventos(c, loja, "e.sessao_id = @sessaoId", new { loja, sessaoId }, "e.quando, e.id", 500, ct);
        return new RelatorioSessaoDto(Sessao(row), row.MesaNumero, await Pessoas(c, sessaoId, false, ct), await Comandas(c, sessaoId, false, ct), eventos);
    }

    private async Task<(DateTime Agora, R.LimitesAlerta Limites, IReadOnlyDictionary<Guid, List<string>> Extras)> Contexto(
        NpgsqlConnection c, Guid loja, IReadOnlyCollection<Guid> sessaoIds, CancellationToken ct)
    {
        var agora = await c.ExecuteScalarAsync<DateTime>(new CommandDefinition("SELECT NOW()", cancellationToken: ct));
        var dados = await c.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT dados::text FROM mesas_config WHERE estabelecimento_id = @loja", new { loja }, cancellationToken: ct));
        var config = MesasConfigRegras.Efetiva(dados is null ? null : JsonNode.Parse(dados) as JsonObject);
        var limites = new R.LimitesAlerta(Int(config, "alertas.tempoMesaMin", 120), Int(config, "alertaSemPedidoMin", 25));
        var extras = new Dictionary<Guid, List<string>>();
        if (sessaoIds.Count > 0)
            foreach (var f in fontes)
                foreach (var (sessao, lista) in await f.AlertasAsync(c, loja, sessaoIds, config, ct))
                {
                    if (!extras.TryGetValue(sessao, out var l)) extras[sessao] = l = new List<string>();
                    l.AddRange(lista);
                }
        return (agora, limites, extras);
    }

    private static IReadOnlyList<string> Alertas(MesaRow r, DateTime agora, R.LimitesAlerta limites, IReadOnlyDictionary<Guid, List<string>> extras) =>
        R.Alertas(new R.SituacaoMesa(r.Status, r.AbertaEm, r.ContaPedidaEm, r.UltimoPedidoEm, r.Chamado, r.Autorizacao), agora, limites,
            r.SessaoId is Guid s && extras.TryGetValue(s, out var l) ? l : null);

    private static int Int(JsonObject config, string chave, int padrao) =>
        MesasConfigRegras.Ler(config, chave) is JsonValue v && v.TryGetValue<double>(out var d) ? (int)d : padrao;

    private static SessaoResumoDto Sessao(SessaoRow s) => new(s.Id, s.Estado, s.AbertaEm, s.AbertaPor, s.AbertaPorId, s.GarcomId, s.GarcomNome,
        s.TipoComanda, s.OcupantesAnonimos, s.ContaPedidaEm, s.UltimoPedidoEm, s.EncerradaEm, s.EncerradaPor, s.ReabertaVezes, s.ReservaId, s.QrToken);

    private static async Task<IReadOnlyList<PessoaDto>> Pessoas(NpgsqlConnection c, Guid s, bool soNaMesa, CancellationToken ct) =>
        (await c.QueryAsync<PessoaDto>(new CommandDefinition($"""
            SELECT id AS "Id", nome AS "Nome", celular AS "Celular", cadeira_id AS "CadeiraId", origem AS "Origem", entrou_em AS "EntrouEm",
                   cor AS "Cor", alergia AS "Alergia", titular AS "Titular"
              FROM mesa_pessoa WHERE sessao_id = @s {(soNaMesa ? "AND saiu_em IS NULL" : "")} ORDER BY entrou_em, id
            """, new { s }, cancellationToken: ct))).ToList();

    private static async Task<IReadOnlyList<ComandaDto>> Comandas(NpgsqlConnection c, Guid s, bool soAbertas, CancellationToken ct) =>
        (await c.QueryAsync<ComandaDto>(new CommandDefinition($"""
            SELECT id AS "Id", tipo AS "Tipo", pessoa_id AS "PessoaId", nome AS "Nome", ordem::int AS "Ordem", aberta AS "Aberta", fechada_em AS "FechadaEm"
              FROM mesa_comanda WHERE sessao_id = @s {(soAbertas ? "AND aberta" : "")} ORDER BY (tipo = 'coletiva') DESC, criado_em, id
            """, new { s }, cancellationToken: ct))).ToList();
}
