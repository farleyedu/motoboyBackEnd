using System.Text.Json;
using APIBack.Acontecimentos;
using APIBack.GestaoLogica.Documentos;
using Dapper;
using Npgsql;

namespace APIBack.GestaoLogica.Despesas;

/// <summary>
/// Contas fixas e finalidades da loja. Valor fixo vira <c>despesa_fixa</c> a pagar ao gerar o mês; valor variável fica
/// "aguardando valor" até a pessoa informar. Um lançamento por conta e mês (origem <c>recorrencia:{id}:{AAAA-MM}</c>; depois de
/// um estorno, <c>~n</c>). Lembretes: o que vence nos próximos dias e o que já venceu sem pagar.
/// </summary>
public interface IRecorrenciasService
{
    Task<IReadOnlyList<ContaFixaDto>> Listar(Guid loja, bool incluirArquivadas, CancellationToken ct);
    Task<ContaFixaDto> Salvar(Guid loja, int usuario, Guid? id, ContaFixaRequest req, CancellationToken ct);
    Task<ContaFixaDto> Arquivar(Guid loja, int usuario, Guid id, CancellationToken ct);
    Task<IReadOnlyList<OcorrenciaDto>> Ocorrencias(Guid loja, DateOnly mes, CancellationToken ct);
    Task<GeracaoDto> Gerar(Guid loja, int usuario, DateOnly mes, CancellationToken ct);
    Task<OcorrenciaDto> Lancar(Guid loja, int usuario, Guid id, LancarOcorrenciaRequest req, CancellationToken ct);
    Task<IReadOnlyList<LembreteDto>> Lembretes(Guid loja, int dias, CancellationToken ct);
    Task<IReadOnlyList<string>> Finalidades(Guid loja, CancellationToken ct);
    Task<string> CriarFinalidade(Guid loja, int usuario, string? nome, CancellationToken ct);
}

public sealed class RecorrenciasService(NpgsqlDataSource dataSource, IAcontecimentosService livro, TimeProvider? relogio = null) : IRecorrenciasService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider _relogio = relogio ?? TimeProvider.System;

    public const string TipoDespesaFixa = "despesa_fixa", CategoriaFixas = "Despesas fixas";

    private const string Colunas = "id, nome, finalidade, valor_previsto, dia_vencimento, valor_varia, arquivada, conta_id, versao, criado_em";

    private sealed class FixaRow
    {
        public Guid Id { get; set; }
        public string Nome { get; set; } = "";
        public string? Finalidade { get; set; }
        public decimal ValorPrevisto { get; set; }
        public short DiaVencimento { get; set; }
        public bool ValorVaria { get; set; }
        public bool Arquivada { get; set; }
        public Guid? ContaId { get; set; }
        public int Versao { get; set; }
        public DateTime CriadoEm { get; set; }
        public ContaFixaDto Dto() => new(Id, Nome, Finalidade, ValorPrevisto, DiaVencimento, ValorVaria, Arquivada, ContaId, Versao);
    }

    private sealed record Lancada(Guid FixaId, Guid AcontecimentoId, Guid LancamentoId, decimal Valor, string Status, DateOnly? Vencimento);

    public async Task<IReadOnlyList<ContaFixaDto>> Listar(Guid loja, bool incluirArquivadas, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<FixaRow>($"SELECT {Colunas} FROM gestao_contas_fixas WHERE estabelecimento_id = @loja AND (@todas OR NOT arquivada) ORDER BY nome",
            new { loja, todas = incluirArquivadas })).Select(f => f.Dto()).ToList();
    }

    public async Task<ContaFixaDto> Salvar(Guid loja, int usuario, Guid? id, ContaFixaRequest req, CancellationToken ct)
    {
        var r = RecorrenciaRegras.Validar(req);
        await using var c = await dataSource.OpenConnectionAsync(ct);
        var p = new
        {
            loja, id = id ?? Guid.NewGuid(), nome = Cortar(r.Nome!, 120), finalidade = r.Finalidade is null ? null : Cortar(r.Finalidade, 60),
            valor = r.ValorPrevisto, dia = (short)r.DiaVencimento!.Value, varia = r.ValorVaria, r.ContaId, usuario, agora = _relogio.GetUtcNow(), r.Versao,
        };
        if (id is null)
            await c.ExecuteAsync(@"
INSERT INTO gestao_contas_fixas (id, estabelecimento_id, nome, finalidade, valor_previsto, dia_vencimento, valor_varia, conta_id, criado_por,
    criado_em, atualizado_por, atualizado_em)
VALUES (@id, @loja, @nome, @finalidade, @valor, @dia, @varia, @ContaId, @usuario, @agora, @usuario, @agora)", p);
        // Mudar o valor vale do próximo lançamento em diante; o que já está no livro não muda.
        else if (await c.ExecuteAsync(@"
UPDATE gestao_contas_fixas SET nome = @nome, finalidade = @finalidade, valor_previsto = @valor, dia_vencimento = @dia, valor_varia = @varia,
       conta_id = @ContaId, versao = versao + 1, atualizado_por = @usuario, atualizado_em = @agora
 WHERE estabelecimento_id = @loja AND id = @id AND (@Versao::int IS NULL OR versao = @Versao)", p) == 0)
            throw await Obter(c, loja, id.Value) is null
                ? new GestaoException("NAO_ENCONTRADO", "Conta fixa não encontrada.", 404)
                : new GestaoException("IDEMPOTENCIA_CONFLITO", "Outra pessoa alterou esta conta. Atualize a tela.", 409);
        return (await Obter(c, loja, p.id))!;
    }

    public async Task<ContaFixaDto> Arquivar(Guid loja, int usuario, Guid id, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(@"UPDATE gestao_contas_fixas SET arquivada = true, versao = versao + 1, atualizado_por = @usuario, atualizado_em = @agora
 WHERE estabelecimento_id = @loja AND id = @id AND NOT arquivada", new { loja, id, usuario, agora = _relogio.GetUtcNow() });
        return await Obter(c, loja, id) ?? throw new GestaoException("NAO_ENCONTRADO", "Conta fixa não encontrada.", 404);
    }

    public async Task<IReadOnlyList<OcorrenciaDto>> Ocorrencias(Guid loja, DateOnly mes, CancellationToken ct)
    {
        mes = RecorrenciaRegras.Mes(mes);
        await using var c = await dataSource.OpenConnectionAsync(ct);
        return await OcorrenciasDoMes(c, loja, mes);
    }

    public async Task<GeracaoDto> Gerar(Guid loja, int usuario, DateOnly mes, CancellationToken ct)
    {
        mes = RecorrenciaRegras.Mes(mes);
        RecorrenciaRegras.ValidarMesDeGeracao(mes, await Hoje(loja, ct));
        List<OcorrenciaDto> pendentes;
        await using (var c = await dataSource.OpenConnectionAsync(ct))
            pendentes = (await OcorrenciasDoMes(c, loja, mes)).Where(o => o.Estado == EstadoOcorrencia.Prevista).ToList();
        var geradas = new List<OcorrenciaDto>();
        foreach (var o in pendentes)
            geradas.Add(await Publicar(loja, usuario, o.ContaFixaId, mes, o.Valor, o.Vencimento, null, ct));
        await using var c2 = await dataSource.OpenConnectionAsync(ct);
        return new GeracaoDto(mes, geradas, (await OcorrenciasDoMes(c2, loja, mes)).Where(o => o.Estado == EstadoOcorrencia.AguardandoValor).ToList());
    }

    public async Task<OcorrenciaDto> Lancar(Guid loja, int usuario, Guid id, LancarOcorrenciaRequest req, CancellationToken ct)
    {
        if (req.Valor <= 0 || decimal.Round(req.Valor, 2) != req.Valor) throw Invalido("Informe o valor do mês (até 2 casas).");
        var mes = RecorrenciaRegras.Mes(req.Mes);
        RecorrenciaRegras.ValidarMesDeGeracao(mes, await Hoje(loja, ct));
        OcorrenciaDto? atual;
        await using (var c = await dataSource.OpenConnectionAsync(ct))
        {
            if (await Obter(c, loja, id) is null) throw new GestaoException("NAO_ENCONTRADO", "Conta fixa não encontrada.", 404);
            atual = (await OcorrenciasDoMes(c, loja, mes, incluirArquivadas: true)).FirstOrDefault(o => o.ContaFixaId == id);
        }
        if (atual?.Estado == EstadoOcorrencia.Lancada)
            throw new GestaoException("DUPLICADO", "Esta conta já foi lançada neste mês. Para mudar o valor, faça um ajuste.", 409);
        var fixa = await Listar(loja, true, ct);
        var dia = fixa.First(f => f.Id == id).DiaVencimento;
        return await Publicar(loja, usuario, id, mes, req.Valor, req.Vencimento ?? RecorrenciaRegras.Vencimento(mes, dia), req.ChaveIdempotencia, ct);
    }

    public async Task<IReadOnlyList<LembreteDto>> Lembretes(Guid loja, int dias, CancellationToken ct)
    {
        var hoje = await Hoje(loja, ct);
        var mes = RecorrenciaRegras.Mes(hoje);
        await using var c = await dataSource.OpenConnectionAsync(ct);
        var ocorrencias = new List<OcorrenciaDto>();
        // Mês passado (o que ficou sem pagar), o atual e o que vem (vencimento no começo do mês seguinte).
        foreach (var m in new[] { mes.AddMonths(-1), mes, mes.AddMonths(1) })
            ocorrencias.AddRange(await OcorrenciasDoMes(c, loja, m));
        var lancadas = await Lancadas(c, loja, null);
        return RecorrenciaRegras.Lembretes(ocorrencias, lancadas.ToDictionary(l => l.AcontecimentoId, l => l.LancamentoId), hoje, dias);
    }

    public async Task<IReadOnlyList<string>> Finalidades(Guid loja, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<string>("SELECT nome FROM gestao_finalidades WHERE estabelecimento_id = @loja ORDER BY nome", new { loja })).ToList();
    }

    /// <summary>Finalidade é da loja (regra 7); a mesma escrita de outro jeito ("Gás", "gas") é a mesma finalidade.</summary>
    public async Task<string> CriarFinalidade(Guid loja, int usuario, string? nome, CancellationToken ct)
    {
        var limpo = nome?.Trim() ?? "";
        if (limpo.Length is < 2 or > 60) throw Invalido("Finalidade com 2 a 60 letras.");
        var chave = DocumentoRegras.NomeChave(limpo);
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(@"INSERT INTO gestao_finalidades (estabelecimento_id, nome, chave, criado_por) VALUES (@loja, @limpo, @chave, @usuario)
ON CONFLICT DO NOTHING", new { loja, limpo, chave, usuario });
        return await c.ExecuteScalarAsync<string>("SELECT nome FROM gestao_finalidades WHERE estabelecimento_id = @loja AND chave = @chave", new { loja, chave }) ?? limpo;
    }

    // ---------------------------------------------------------------- apoio

    private async Task<OcorrenciaDto> Publicar(Guid loja, int usuario, Guid fixaId, DateOnly mes, decimal valor, DateOnly vencimento, string? chave,
        CancellationToken ct)
    {
        ContaFixaDto fixa;
        int revisao;
        await using (var c = await dataSource.OpenConnectionAsync(ct))
        {
            fixa = await Obter(c, loja, fixaId) ?? throw new GestaoException("NAO_ENCONTRADO", "Conta fixa não encontrada.", 404);
            // Depois de um estorno, a origem do mês não se repete no livro: a nova publicação leva ~n.
            revisao = await c.ExecuteScalarAsync<int>(@"SELECT count(*) FROM gestao_acontecimentos
 WHERE estabelecimento_id = @loja AND tipo = 'despesa_fixa' AND origem_tipo = 'recorrencia' AND corrige_id IS NULL
   AND (origem_id = @base OR origem_id LIKE @baseRev)", new { loja, @base = Base(fixaId, mes), baseRev = Base(fixaId, mes) + "~%" });
        }
        var origem = revisao == 0 ? Base(fixaId, mes) : $"{Base(fixaId, mes)}~{revisao}";
        var r = await livro.Publicar(loja, usuario, new PublicarAcontecimentoRequest
        {
            ChaveIdempotencia = string.IsNullOrWhiteSpace(chave) ? $"despesas:recorrencia:{origem}" : chave.Trim(),
            Tipo = TipoDespesaFixa, Modulo = ModuloGestao.Despesas, DataFato = _relogio.GetUtcNow(),
            Descricao = Cortar($"{fixa.Nome} · {mes:MM/yyyy}", 300),
            Origem = new OrigemRef { Tipo = "recorrencia", Id = origem },
            Dados = JsonSerializer.SerializeToElement(new { contaFixaId = fixa.Id, recorrencia = true, mes }, Json),
            Lancamentos = new()
            {
                new LancamentoRequest
                {
                    Natureza = Natureza.Despesa, Valor = valor, Status = StatusLancamento.APagar, MesReferencia = mes, DataVencimento = vencimento,
                    Categoria = CategoriaFixas, Finalidade = fixa.Finalidade, Descricao = Cortar($"{fixa.Nome} · {mes:MM/yyyy}", 300),
                },
            },
        }, ct);
        var l = r.Acontecimento.Lancamentos[0];
        return new OcorrenciaDto(fixa.Id, fixa.Nome, mes, vencimento, valor, EstadoOcorrencia.Lancada, r.Id, l.Status);
    }

    /// <summary>
    /// Cada conta fixa ativa no mês, mais as arquivadas que já tinham lançamento nele. Conta como lançada qualquer
    /// <c>despesa_fixa</c> com <c>dados.contaFixaId</c> e competência no mês, que não foi estornada (vale também o que o Painel lançou).
    /// </summary>
    private static async Task<List<OcorrenciaDto>> OcorrenciasDoMes(NpgsqlConnection c, Guid loja, DateOnly mes, bool incluirArquivadas = false)
    {
        var fixas = (await c.QueryAsync<FixaRow>($"SELECT {Colunas} FROM gestao_contas_fixas WHERE estabelecimento_id = @loja ORDER BY nome", new { loja })).ToList();
        var lancadas = (await Lancadas(c, loja, mes)).GroupBy(l => l.FixaId).ToDictionary(g => g.Key, g => g.First());
        var r = new List<OcorrenciaDto>();
        foreach (var f in fixas)
        {
            var venc = RecorrenciaRegras.Vencimento(mes, f.DiaVencimento);
            if (lancadas.TryGetValue(f.Id, out var l))
                r.Add(new OcorrenciaDto(f.Id, f.Nome, mes, l.Vencimento ?? venc, l.Valor, EstadoOcorrencia.Lancada, l.AcontecimentoId, l.Status));
            // Antes do mês em que a conta foi cadastrada não há o que lembrar.
            else if ((!f.Arquivada || incluirArquivadas) && mes >= RecorrenciaRegras.Mes(DateOnly.FromDateTime(f.CriadoEm.AddHours(-3))))
                r.Add(new OcorrenciaDto(f.Id, f.Nome, mes, venc, f.ValorPrevisto, RecorrenciaRegras.EstadoSemLancamento(f.ValorVaria), null, null));
        }
        return r;
    }

    private static async Task<List<Lancada>> Lancadas(NpgsqlConnection c, Guid loja, DateOnly? mes) =>
        (await c.QueryAsync<(string FixaId, Guid AcontecimentoId, Guid LancamentoId, decimal Valor, string Status, DateTime? Vencimento)>(@"
SELECT a.dados->>'contaFixaId', a.id, s.id,
       s.valor + coalesce((SELECT sum(x.valor) FROM gestao_lancamentos x WHERE x.estabelecimento_id = s.estabelecimento_id AND x.corrige_lancamento_id = s.id), 0),
       s.status, s.data_vencimento
  FROM gestao_acontecimentos a
  JOIN gestao_lancamentos_situacao s ON s.estabelecimento_id = a.estabelecimento_id AND s.acontecimento_id = a.id AND s.corrige_lancamento_id IS NULL
 WHERE a.estabelecimento_id = @loja AND a.tipo = 'despesa_fixa' AND a.corrige_id IS NULL AND a.dados->>'contaFixaId' IS NOT NULL
   AND (@mes::date IS NULL OR s.mes_referencia = @mes)
   AND NOT EXISTS (SELECT 1 FROM gestao_acontecimentos e WHERE e.estabelecimento_id = a.estabelecimento_id AND e.corrige_id = a.id AND e.correcao = 'estorno')
 ORDER BY a.registrado_em", new { loja, mes = mes?.ToDateTime(TimeOnly.MinValue) }))
        .Where(x => Guid.TryParse(x.FixaId, out _)) // id de conta fixa que não é deste backend (ex.: "fixa-..." do Painel simulado)
        .Select(x => new Lancada(Guid.Parse(x.FixaId), x.AcontecimentoId, x.LancamentoId, x.Valor, x.Status,
            x.Vencimento is { } v ? DateOnly.FromDateTime(v) : null)).ToList();

    private static async Task<ContaFixaDto?> Obter(NpgsqlConnection c, Guid loja, Guid id) =>
        (await c.QuerySingleOrDefaultAsync<FixaRow>($"SELECT {Colunas} FROM gestao_contas_fixas WHERE estabelecimento_id = @loja AND id = @id",
            new { loja, id }))?.Dto();

    private async Task<DateOnly> Hoje(Guid loja, CancellationToken ct) =>
        AcontecimentoRegras.DiaLocal(_relogio.GetUtcNow(), await livro.Fuso(loja, ct));

    private static string Base(Guid id, DateOnly mes) => $"{id}:{mes:yyyy-MM}";

    private static string Cortar(string s, int max) => s.Length <= max ? s : s[..max];

    private static GestaoException Invalido(string m) => new("CAMPO_INVALIDO", m, 422);
}
