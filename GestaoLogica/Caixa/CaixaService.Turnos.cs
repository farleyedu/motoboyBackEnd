using System.Text.Json;
using APIBack.Acontecimentos;
using APIBack.Security;
using Dapper;
using Npgsql;

namespace APIBack.GestaoLogica.Caixa;

/// <summary>
/// Abrir de novo depois de fechar (migração 20261125_01). Quem opera escolhe:
/// <list type="bullet">
/// <item><b>Novo turno</b>: o fechamento vale (quebra/sobra e recolhimento ficam no livro) e o dia abre outro turno com um fundo
/// novo; a abertura acerta a gaveta como a primeira (o que falta vem do cofre, o que sobra vai para ele).</item>
/// <item><b>Reabrir</b>: o fechamento é desfeito — estorno da quebra/sobra, dos ajustes e do recolhimento (o dinheiro volta do cofre
/// para a gaveta) — e o caixa continua aberto de onde parou. Fechamento com diferença ou aprovado pelo gerente só o gerente desfaz.</item>
/// </list>
/// Em ambos o fechamento que sai do dia vai inteiro para <c>gestao_caixa_turnos</c>; nada se apaga.
/// </summary>
public sealed partial class CaixaService
{
    public const string DestinoNovoTurno = "novo_turno", DestinoReaberto = "reaberto";

    public async Task<CaixaDoDia> NovoTurno(Guid loja, int usuario, GestaoPapeis.Acesso acesso, NovoTurnoRequest req, CancellationToken ct)
    {
        if (req.Fundo < 0 || decimal.Round(req.Fundo, 2) != req.Fundo) throw Invalido("Fundo de troco: zero ou mais, até 2 casas.");
        var hoje = await SoHoje(loja, req.Dia, ct);
        await using (var c = await dataSource.OpenConnectionAsync(ct))
        {
            var row = await Fechado(c, loja, hoje);
            await SemPendencia(c, loja);
            var contas = await ContasDoCaixa(c, loja, null, ct);
            var gaveta = await Saldo(loja, contas.Gaveta, ct);
            var acerto = CaixaRegras.R2(req.Fundo - gaveta);
            var turno = row.Turno + 1;
            var agora = _relogio.GetUtcNow();
            var pedido = new PublicarAcontecimentoRequest
            {
                ChaveIdempotencia = $"caixa:abertura:{hoje:yyyy-MM-dd}:v{row.Versao + 1}", Tipo = TipoAbertura, Modulo = ModuloGestao.Caixa,
                DataFato = agora, Descricao = $"Abertura do {turno}º turno · fundo R$ {req.Fundo:N2}",
                // A origem leva a versão: o livro não aceita duas aberturas com a mesma origem (a do 1º turno é só o dia).
                Origem = new OrigemRef { Tipo = "caixa", Id = $"{hoje:yyyy-MM-dd}:v{row.Versao + 1}" },
                Dados = JsonSerializer.SerializeToElement(new { fundo = req.Fundo, gavetaAntes = gaveta, turno }, Json),
                Lancamentos = acerto == 0 ? new() : Transferencia(acerto > 0 ? contas.Cofre : contas.Gaveta,
                    acerto > 0 ? contas.Gaveta : contas.Cofre, Math.Abs(acerto), hoje, "Acerto do fundo de troco"),
            };
            GestaoPapeis.ValidarPublicacao(acesso, pedido, hoje, hoje);
            await using var tx = await c.BeginTransactionAsync(ct);
            var r = await livro.Publicar(c, tx, loja, usuario, pedido, ct);
            await Guardar(c, tx, loja, hoje, row.Versao, DestinoNovoTurno, usuario, agora, null, Array.Empty<Guid>());
            var n = await c.ExecuteAsync(@"
UPDATE gestao_caixa_dias SET fundo = @Fundo, aberto_por = @usuario, aberto_em = @agora, abertura_id = @abertura, turno = turno + 1,
       versao = versao + 1, ciclo_desde = @agora" + Limpar + @"
 WHERE estabelecimento_id = @loja AND dia = @dia AND versao = @versao AND fechado_em IS NOT NULL",
                new { loja, dia = hoje.ToDateTime(TimeOnly.MinValue), req.Fundo, usuario, agora, abertura = r.Id, versao = row.Versao }, tx);
            if (n == 0) throw JaAbriu();
            await GestaoTempoReal.AvisarNoCommit(c, tx, loja, Guid.NewGuid(), GestaoTempoReal.Caixa);
            await tx.CommitAsync(ct);
        }
        return (await CarregarDia(loja, hoje, ct))!;
    }

    public async Task<CaixaDoDia> Reabrir(Guid loja, int usuario, GestaoPapeis.Acesso acesso, ReabrirCaixaRequest req, CancellationToken ct)
    {
        var motivo = req.Motivo?.Trim();
        if (motivo is null || motivo.Length < 3) throw Invalido("Diga por que o fechamento vai ser desfeito (mínimo de 3 letras).");
        if (!acesso.PodeLancar) throw new GestaoException("SEM_PERMISSAO", "Você não pode mexer no caixa.", 403);
        var hoje = await SoHoje(loja, req.Dia, ct);
        await using (var c = await dataSource.OpenConnectionAsync(ct))
        {
            var row = await Fechado(c, loja, hoje);
            await SemPendencia(c, loja, row.Dia);
            var comDiferenca = row.Contado != row.Esperado || (await AjustesDoCiclo(c, loja, row)).Count > 0;
            if (acesso.Papel != GestaoPapeis.Gerente && (comDiferenca || row.AprovadoPor is not null))
                throw new GestaoException("SEM_PERMISSAO", "Este fechamento teve diferença ou foi aprovado pelo gerente: só o gerente desfaz.", 403);

            var agora = _relogio.GetUtcNow();
            var chave = $"caixa:reabertura:{hoje:yyyy-MM-dd}:v{row.Versao}";
            var texto = Cortar($"Caixa reaberto: {motivo}", 300);
            await using var tx = await c.BeginTransactionAsync(ct);
            // Desfaz no livro o que o fechamento fez: a quebra/sobra (só existe se não bateu), as correções dele e o recolhimento.
            var alvos = new List<(string Nome, Guid Id)>();
            if (row.Contado != row.Esperado) alvos.Add(("fechamento", row.FechamentoId!.Value));
            alvos.AddRange((await AjustesDoCiclo(c, loja, row, tx)).Select(a => ("ajuste:" + a, a)));
            if (row.RecolhimentoId is { } recolhimento) alvos.Add(("recolhimento", recolhimento));
            var estornos = new List<Guid>();
            foreach (var (nome, id) in alvos)
                estornos.Add((await livro.Estornar(c, tx, loja, usuario, id, new EstornoRequest
                {
                    ChaveIdempotencia = $"{chave}:{nome}", Motivo = texto, ConfirmacaoHumana = true, DataFato = agora,
                }, ct)).Id);
            await Guardar(c, tx, loja, hoje, row.Versao, DestinoReaberto, usuario, agora, Cortar(motivo, 300), estornos);
            var n = await c.ExecuteAsync(@"
UPDATE gestao_caixa_dias SET versao = versao + 1, ciclo_desde = @agora" + Limpar + @"
 WHERE estabelecimento_id = @loja AND dia = @dia AND versao = @versao AND fechado_em IS NOT NULL",
                new { loja, dia = hoje.ToDateTime(TimeOnly.MinValue), agora, versao = row.Versao }, tx);
            if (n == 0) throw JaAbriu();
            await GestaoTempoReal.AvisarNoCommit(c, tx, loja, Guid.NewGuid(), GestaoTempoReal.Caixa);
            await tx.CommitAsync(ct);
        }
        return (await CarregarDia(loja, hoje, ct))!;
    }

    // ---------------------------------------------------------------- apoio

    /// <summary>Tudo o que o fechamento e o envio ao gerente gravaram no dia volta a vazio (o que era fica em gestao_caixa_turnos).</summary>
    private const string Limpar = @",
       fechado_por = NULL, fechado_em = NULL, fechamento_id = NULL, recolhimento_id = NULL, esperado = NULL, contado = NULL,
       maquininha = NULL, maquininha_esperado = NULL, justificativa = NULL, fica = NULL, cofre = NULL, aprovado_por = NULL, aprovado_em = NULL,
       envio_em = NULL, envio_por = NULL, envio = NULL, devolvido_em = NULL, devolvido_por = NULL, devolucao_motivo = NULL";

    private static GestaoException JaAbriu() =>
        new("FECHADO", "O caixa acabou de ser aberto de novo em outro aparelho. Atualize a tela.", 409);

    /// <summary>Só se abre de novo o caixa de hoje que está fechado (não aberto, nem esperando o gerente).</summary>
    private static async Task<DiaRow> Fechado(NpgsqlConnection c, Guid loja, DateOnly dia)
    {
        var row = await Dia(c, loja, dia) ?? throw new GestaoException("SEM_CAIXA", "O caixa de hoje ainda não foi aberto.", 409);
        if (row.EnvioEm is not null && row.FechadoEm is null)
            throw new GestaoException("AGUARDANDO_APROVACAO", "O fechamento espera a aprovação do gerente: ele decide antes.", 409);
        if (row.FechadoEm is null) throw new GestaoException("CAMPO_INVALIDO", "O caixa de hoje já está aberto.", 409);
        return row;
    }

    /// <summary>
    /// Nenhum caixa novo abre (outro turno ou o dia seguinte) enquanto houver fechamento esperando o gerente: enviado para decidir ou
    /// fechado pelo operador sem aprovação. <paramref name="exceto"/>: o próprio dia que está sendo desfeito (reabrir não abre outro).
    /// </summary>
    private static async Task SemPendencia(NpgsqlConnection c, Guid loja, DateTime? exceto = null)
    {
        var p = await c.QueryFirstOrDefaultAsync<DiaRow>($@"SELECT {ColunasDia} FROM gestao_caixa_dias
 WHERE estabelecimento_id = @loja AND (CAST(@exceto AS date) IS NULL OR dia <> @exceto)
   AND ((envio_em IS NOT NULL AND fechado_em IS NULL) OR (fechado_em IS NOT NULL AND aprovado_em IS NULL))
 ORDER BY dia LIMIT 1", new { loja, exceto });
        if (p is null) return;
        if (p.FechadoEm is null)
            throw new GestaoException("AGUARDANDO_APROVACAO",
                $"O caixa de {p.Dia:dd/MM} espera a aprovação do gerente. Ele precisa ser fechado antes de abrir outro.", 409);
        throw new GestaoException("SEM_APROVACAO",
            $"O caixa de {p.Dia:dd/MM}{(p.FechadoPorNome is null ? "" : $", fechado por {p.FechadoPorNome},")} espera o gerente aprovar. Só depois abre outro caixa.", 409);
    }

    /// <summary>Correções do fechamento atual (as de um fechamento já desfeito ficaram no ciclo anterior).</summary>
    private static async Task<IReadOnlyList<Guid>> AjustesDoCiclo(NpgsqlConnection c, Guid loja, DiaRow row, NpgsqlTransaction? tx = null) =>
        (await c.QueryAsync<Guid>(@"SELECT acontecimento_id FROM gestao_caixa_ajustes
 WHERE estabelecimento_id = @loja AND dia = @dia AND acontecimento_id IS NOT NULL AND (CAST(@desde AS timestamptz) IS NULL OR em >= @desde)
 ORDER BY em, id", new { loja, dia = row.Dia, desde = row.CicloDesde }, tx)).ToList();

    private static Task Guardar(NpgsqlConnection c, NpgsqlTransaction tx, Guid loja, DateOnly dia, int versao, string destino, int por,
        DateTimeOffset em, string? motivo, IReadOnlyList<Guid> estornos) =>
        c.ExecuteAsync(@"
INSERT INTO gestao_caixa_turnos (id, estabelecimento_id, dia, versao, turno, destino, por, em, motivo, estornos, fundo, aberto_por, aberto_em,
       ciclo_desde, fechado_por, fechado_em, fechamento_id, recolhimento_id, esperado, contado, maquininha, maquininha_esperado, justificativa,
       fica, cofre, aprovado_por, aprovado_em)
SELECT @id, estabelecimento_id, dia, versao, turno, @destino, @por, @em, @motivo, CAST(@estornos AS jsonb), fundo, aberto_por, aberto_em,
       ciclo_desde, fechado_por, fechado_em, fechamento_id, recolhimento_id, esperado, contado, maquininha, maquininha_esperado, justificativa,
       fica, cofre, aprovado_por, aprovado_em
  FROM gestao_caixa_dias WHERE estabelecimento_id = @loja AND dia = @dia AND versao = @versao AND fechado_em IS NOT NULL",
            new { id = Guid.NewGuid(), loja, dia = dia.ToDateTime(TimeOnly.MinValue), versao, destino, por, em, motivo,
                estornos = JsonSerializer.Serialize(estornos) }, tx);

    private static string SufixoDaVersao(DiaRow? row) => row is { Versao: > 0 } ? $":v{row.Versao}" : "";

    private sealed class TurnoRow
    {
        public string Destino { get; set; } = "";
        public int Turno { get; set; }
        public DateTime Dia { get; set; }
        public DateTime Em { get; set; }
        public string? PorNome { get; set; }
        public string? Motivo { get; set; }
        public DateTime AbertoEm { get; set; }
        public DateTime FechadoEm { get; set; }
        public int FechadoPor { get; set; }
        public string? FechadoPorNome { get; set; }
        public decimal Esperado { get; set; }
        public decimal Contado { get; set; }
        public decimal? Cofre { get; set; }
        public decimal? Fica { get; set; }
        public string? Maquininha { get; set; }
        public string? MaquininhaEsperado { get; set; }
        public string? Justificativa { get; set; }
    }

    private const string ColunasTurno = @"t.destino, t.turno, t.dia, t.em, (SELECT u.nome::text FROM usuario u WHERE u.id = t.por) AS por_nome, t.motivo,
       t.aberto_em, t.fechado_em, t.fechado_por, (SELECT u.nome::text FROM usuario u WHERE u.id = t.fechado_por) AS fechado_por_nome, t.esperado,
       t.contado, t.cofre, t.fica, t.maquininha::text AS maquininha, t.maquininha_esperado::text AS maquininha_esperado, t.justificativa";

    private static async Task<IReadOnlyList<FechamentoAnterior>> Anteriores(NpgsqlConnection c, Guid loja, DateOnly dia) =>
        (await c.QueryAsync<TurnoRow>($"SELECT {ColunasTurno} FROM gestao_caixa_turnos t WHERE t.estabelecimento_id = @loja AND t.dia = @dia ORDER BY t.versao",
            new { loja, dia = dia.ToDateTime(TimeOnly.MinValue) }))
        .Select(t => new FechamentoAnterior(t.Destino, t.Turno, Utc(t.Em), t.PorNome, t.Motivo, Utc(t.AbertoEm), Utc(t.FechadoEm), t.FechadoPorNome,
            t.Esperado, t.Contado, t.Cofre ?? 0, t.Fica ?? 0)).ToList();

    /// <summary>Turnos encerrados do mês (o dia abriu outro turno depois): fechamentos de verdade, só sem ajuste.</summary>
    private static async Task<IReadOnlyList<FechamentoDto>> TurnosEncerrados(NpgsqlConnection c, Guid loja, DateOnly mes)
    {
        var vazio = new Dictionary<string, decimal>();
        return (await c.QueryAsync<TurnoRow>($@"SELECT {ColunasTurno} FROM gestao_caixa_turnos t
 WHERE t.estabelecimento_id = @loja AND t.destino = @destino AND t.dia >= @de AND t.dia < @ate ORDER BY t.dia DESC, t.versao DESC",
            new { loja, destino = DestinoNovoTurno, de = mes.ToDateTime(TimeOnly.MinValue), ate = mes.AddMonths(1).ToDateTime(TimeOnly.MinValue) }))
            .Select(t =>
            {
                var dia = DateOnly.FromDateTime(t.Dia);
                return new FechamentoDto($"{dia:yyyy-MM-dd}:t{t.Turno}", dia, t.FechadoPor, Utc(t.AbertoEm), Utc(t.FechadoEm), t.Esperado, t.Contado,
                    t.Maquininha is null ? vazio : JsonSerializer.Deserialize<Dictionary<string, decimal>>(t.Maquininha)!,
                    t.MaquininhaEsperado is null ? vazio : JsonSerializer.Deserialize<Dictionary<string, decimal>>(t.MaquininhaEsperado)!,
                    t.Justificativa, t.Fica ?? 0, t.Cofre ?? 0, Array.Empty<AjusteFechamento>(), t.FechadoPorNome, t.Turno, Ajustavel: false);
            }).ToList();
    }
}
