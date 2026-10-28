using System.Text.Json;
using APIBack.Acontecimentos;
using Dapper;
using Npgsql;

namespace APIBack.GestaoLogica.Equipe;

/// <summary>
/// Equipe: pessoas, escala, turnos e remuneração. O turno muda de estado na tabela (com histórico imutável); o dinheiro vai
/// para o livro: realizado publica <c>turno</c> (despesa a pagar), mudança de valor ajusta, falta/cancelamento estorna.
/// Adiantamento é despesa paga, compensada por ajuste no próximo pagamento; pagamento parcial ajusta o item e publica
/// <c>saldo_remuneracao</c> com o restante (decisão 4 da Lógica).
/// </summary>
public interface IEquipeService
{
    Task<IReadOnlyList<PessoaDto>> Pessoas(Guid loja, bool incluirInativas, CancellationToken ct);
    Task<PessoaDto> SalvarPessoa(Guid loja, int usuario, Guid? id, PessoaRequest req, CancellationToken ct);
    Task<SemanaDto> Semana(Guid loja, DateOnly inicio, CancellationToken ct);
    Task<TurnoDto> MudarTurno(Guid loja, int usuario, MudarTurnoRequest req, CancellationToken ct);
    Task<IReadOnlyList<ResumoPessoa>> Resumo(Guid loja, DateOnly mes, CancellationToken ct);
    Task<AcontecimentoDto> Adiantar(Guid loja, int usuario, AdiantamentoRequest req, CancellationToken ct);
    Task<AcontecimentoDto> Descontar(Guid loja, int usuario, DescontoRequest req, CancellationToken ct);
    Task<PagamentoFeito> Pagar(Guid loja, int usuario, PagamentoRequest req, CancellationToken ct);
}

public sealed class EquipeService(NpgsqlDataSource dataSource, IAcontecimentosService livro, TimeProvider? relogio = null) : IEquipeService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider _relogio = relogio ?? TimeProvider.System;

    public const string TipoTurno = "turno", TipoSaldo = "saldo_remuneracao", TipoAdiantamento = "adiantamento";

    private sealed class PessoaRow
    {
        public Guid Id { get; set; }
        public string Nome { get; set; } = "";
        public string Funcao { get; set; } = "";
        public string Vinculo { get; set; } = "";
        public string Regra { get; set; } = "{}";
        public string PagaCom { get; set; } = "pix";
        public string? ChavePix { get; set; }
        public int? UsuarioId { get; set; }
        public string? Cor { get; set; }
        public string Escala { get; set; } = "[]";
        public bool Ativo { get; set; }
        public DateTime Desde { get; set; }
        public int CriadoPor { get; set; }
        public DateTime CriadoEm { get; set; }
        public int Versao { get; set; }
    }

    private sealed class TurnoRow
    {
        public Guid Id { get; set; }
        public Guid PessoaId { get; set; }
        public DateTime Data { get; set; }
        public string Entrada { get; set; } = "";
        public string Saida { get; set; } = "";
        public string Status { get; set; } = "";
        public bool Avulso { get; set; }
        public bool Justificada { get; set; }
        public int Entregas { get; set; }
        public string? PagamentoAvulso { get; set; }
        public string Adicionais { get; set; } = "[]";
        public decimal BaseComissao { get; set; }
        public decimal Valor { get; set; }
        public decimal Previsto { get; set; }
        public string Itens { get; set; } = "[]";
        public int Revisao { get; set; }
        public Guid? AcontecimentoId { get; set; }
        public Guid? LancamentoId { get; set; }
    }

    private const string ColunasPessoa = @"id, nome, funcao, vinculo, regra::text AS regra, paga_com, chave_pix, usuario_id, cor,
       escala::text AS escala, ativo, desde, criado_por, criado_em, versao";

    private const string ColunasTurno = @"id, pessoa_id, data, entrada, saida, status, avulso, justificada, entregas,
       pagamento_avulso::text AS pagamento_avulso, adicionais::text AS adicionais, base_comissao, valor, previsto, itens::text AS itens,
       revisao, acontecimento_id, lancamento_id";

    // ---------------------------------------------------------------- pessoas

    public async Task<IReadOnlyList<PessoaDto>> Pessoas(Guid loja, bool incluirInativas, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<PessoaRow>($"SELECT {ColunasPessoa} FROM gestao_pessoas WHERE estabelecimento_id = @loja AND (@todas OR ativo) ORDER BY nome",
            new { loja, todas = incluirInativas })).Select(Pessoa).ToList();
    }

    public async Task<PessoaDto> SalvarPessoa(Guid loja, int usuario, Guid? id, PessoaRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Nome)) throw Invalido("Informe o nome.");
        var vinculo = req.Vinculo ?? "";
        var regra = EquipeRegras.Normalizar(vinculo, req.Regra);
        var escala = EquipeRegras.NormalizarEscala(req.EscalaPadrao);
        var pagaCom = req.PagaCom ?? "pix";
        if (pagaCom is not ("pix" or "dinheiro")) throw Invalido("Paga com: pix ou dinheiro.");
        var agora = _relogio.GetUtcNow();
        var desde = req.Desde ?? AcontecimentoRegras.DiaLocal(agora, await livro.Fuso(loja, ct));
        var p = new
        {
            loja, nome = Cortar(req.Nome.Trim(), 120), funcao = Cortar(req.Funcao?.Trim() ?? "", 60), vinculo,
            regra = JsonSerializer.Serialize(regra, Json), pagaCom, chavePix = Cortar(req.ChavePix?.Trim(), 120), req.UsuarioId,
            cor = Cortar(req.Cor, 20), escala = JsonSerializer.Serialize(escala, Json), req.Ativo, desde = desde.ToDateTime(TimeOnly.MinValue),
            usuario, agora,
        };
        await using var c = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        Guid pessoaId;
        if (id is null)
        {
            pessoaId = Guid.NewGuid();
            await c.ExecuteAsync(@"
INSERT INTO gestao_pessoas (id, estabelecimento_id, nome, funcao, vinculo, regra, paga_com, chave_pix, usuario_id, cor, escala, ativo,
    desde, criado_por, criado_em, atualizado_por, atualizado_em)
VALUES (@pessoaId, @loja, @nome, @funcao, @vinculo, CAST(@regra AS jsonb), @pagaCom, @chavePix, @UsuarioId, @cor, CAST(@escala AS jsonb),
    @Ativo, @desde, @usuario, @agora, @usuario, @agora)", new DynamicParameters(p).Tap(d => d.Add("pessoaId", pessoaId)), tx);
        }
        else
        {
            pessoaId = id.Value;
            // Versão: quem editou em cima de uma versão velha não sobrescreve a mudança de outra pessoa.
            var n = await c.ExecuteAsync(@"
UPDATE gestao_pessoas SET nome = @nome, funcao = @funcao, vinculo = @vinculo, regra = CAST(@regra AS jsonb), paga_com = @pagaCom,
       chave_pix = @chavePix, usuario_id = @UsuarioId, cor = @cor, escala = CAST(@escala AS jsonb), ativo = @Ativo, desde = coalesce(@desdeNovo, desde),
       versao = versao + 1, atualizado_por = @usuario, atualizado_em = @agora
 WHERE estabelecimento_id = @loja AND id = @pessoaId AND (@versao::int IS NULL OR versao = @versao)",
                new DynamicParameters(p).Tap(d => { d.Add("pessoaId", pessoaId); d.Add("versao", req.Versao); d.Add("desdeNovo", req.Desde?.ToDateTime(TimeOnly.MinValue)); }), tx);
            if (n == 0)
                throw await c.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM gestao_pessoas WHERE estabelecimento_id = @loja AND id = @pessoaId)",
                    new { loja, pessoaId }, tx)
                    ? new GestaoException("IDEMPOTENCIA_CONFLITO", "Outra pessoa alterou este cadastro. Atualize a tela.", 409)
                    : new GestaoException("NAO_ENCONTRADO", "Pessoa não encontrada.", 404);
        }
        var salva = Pessoa(await c.QuerySingleAsync<PessoaRow>($"SELECT {ColunasPessoa} FROM gestao_pessoas WHERE id = @pessoaId", new { pessoaId }, tx));
        await c.ExecuteAsync(@"
INSERT INTO gestao_pessoas_historico (id, estabelecimento_id, pessoa_id, versao, retrato, por, em)
VALUES (@id, @loja, @pessoaId, @Versao, CAST(@retrato AS jsonb), @usuario, @agora)",
            new { id = Guid.NewGuid(), loja, pessoaId, salva.Versao, retrato = JsonSerializer.Serialize(salva, Json), usuario, agora }, tx);
        await tx.CommitAsync(ct);
        return salva;
    }

    // ---------------------------------------------------------------- semana e turnos

    public async Task<SemanaDto> Semana(Guid loja, DateOnly inicio, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        var dias = Enumerable.Range(0, 7).Select(inicio.AddDays).ToList();
        var (pessoas, grade) = await Grade(c, loja, dias);
        return new SemanaDto(inicio, pessoas, grade, EquipeRegras.Totais(grade.Values.SelectMany(x => x)));
    }

    public async Task<TurnoDto> MudarTurno(Guid loja, int usuario, MudarTurnoRequest req, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        var chave = EquipeRegras.Chave(req.PessoaId, req.Data);
        // Um turno por vez: duas telas confirmando o mesmo turno não publicam duas vezes.
        await c.ExecuteAsync("SELECT pg_advisory_lock(hashtext(@k))", new { k = "gestao-turno:" + loja + ":" + chave });
        try
        {
            return await MudarTurnoTravado(c, loja, usuario, req, chave, ct);
        }
        finally
        {
            await c.ExecuteAsync("SELECT pg_advisory_unlock(hashtext(@k))", new { k = "gestao-turno:" + loja + ":" + chave });
        }
    }

    private async Task<TurnoDto> MudarTurnoTravado(NpgsqlConnection c, Guid loja, int usuario, MudarTurnoRequest req, string chave,
        CancellationToken ct)
    {
        var pessoa = await ObterPessoa(c, loja, req.PessoaId);
        var gravado = await c.QuerySingleOrDefaultAsync<TurnoRow>(
            $"SELECT {ColunasTurno} FROM gestao_turnos WHERE estabelecimento_id = @loja AND pessoa_id = @PessoaId AND data = @data",
            new { loja, req.PessoaId, data = req.Data.ToDateTime(TimeOnly.MinValue) });
        var antes = gravado is null ? null : Turno(gravado, Array.Empty<EventoTurno>());
        TurnoDto baseTurno;
        if (antes is not null) baseTurno = antes;
        else if (req.Avulso || req.PagamentoAvulso is not null)
        {
            if (req.Entrada is null || req.Saida is null) throw Invalido("Jornada avulsa: informe entrada e saída.");
            baseTurno = Novo(pessoa, req.Data, req.Entrada, req.Saida, true, req.PagamentoAvulso);
        }
        else if (EquipeRegras.DaEscala(pessoa, req.Data) is { } padrao) baseTurno = Novo(pessoa, req.Data, padrao.Entrada, padrao.Saida, false, null);
        else throw Invalido("Esta pessoa não tem turno na escala deste dia: registre uma jornada avulsa.");

        var status = req.Status ?? baseTurno.Status;
        if (!StatusTurno.Todos.Contains(status)) throw Invalido("Status deve ser previsto, realizado, falta ou cancelado.");
        var entrada = req.Entrada ?? baseTurno.Entrada;
        var saida = req.Saida ?? baseTurno.Saida;
        if (EquipeRegras.Minutos(entrada) is null || EquipeRegras.Minutos(saida) is null || EquipeRegras.DuracaoMin(entrada, saida) == 0)
            throw Invalido("Horário inválido: use HH:MM, com entrada diferente da saída.");
        if (req.Entregas is < 0) throw Invalido("Entregas não pode ser negativo.");
        if (req.BaseComissao is < 0) throw Invalido("Base da comissão não pode ser negativa.");
        if (req.PagamentoAvulso is { } pa && (pa.Forma is not ("turno" or "diaria" or "hora" or "entrega") || pa.Valor <= 0))
            throw Invalido("Pagamento avulso: forma turno, diaria, hora ou entrega, com valor maior que zero.");
        var adicionais = (req.Adicionais ?? baseTurno.Adicionais).ToList();
        if (adicionais.Any(a => string.IsNullOrWhiteSpace(a.Rotulo) || a.Valor <= 0 || decimal.Round(a.Valor, 2) != a.Valor))
            throw Invalido("Adicional: diga o que é e o valor (maior que zero, até 2 casas).");
        var justificada = req.Justificada ?? (req.Status is { } s && s != StatusTurno.Falta ? false : baseTurno.Justificada);
        var jornada = new JornadaTurno(entrada, saida, status, justificada, req.Entregas ?? baseTurno.Entregas,
            req.PagamentoAvulso ?? baseTurno.PagamentoAvulso, adicionais, req.BaseComissao ?? baseTurno.BaseComissao);
        var rem = EquipeRegras.Calcular(pessoa.Regra, jornada);

        var fuso = await livro.Fuso(loja, ct);
        var inicioLocal = req.Data.ToDateTime(TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(EquipeRegras.Minutos(entrada)!.Value)));
        var inicio = new DateTimeOffset(inicioLocal, fuso.GetUtcOffset(inicioLocal));
        if (status is StatusTurno.Realizado or StatusTurno.Falta && inicio > _relogio.GetUtcNow())
            throw Invalido("O turno ainda não começou: confirme como realizado ou falta depois do horário de entrada.");

        var mudou = antes is null || antes.Status != status || antes.Entrada != entrada || antes.Saida != saida || antes.Justificada != justificada
            || antes.Entregas != jornada.Entregas || antes.BaseComissao != jornada.BaseComissao || antes.Valor != rem.Valor
            || !antes.Adicionais.SequenceEqual(adicionais) || antes.PagamentoAvulso != jornada.PagamentoAvulso;
        if (!mudou) return await TurnoComHistorico(c, loja, antes!.Id!.Value);
        var temRegistro = antes?.AcontecimentoId is not null;
        var motivo = req.Motivo?.Trim();
        if (temRegistro && (motivo is null || motivo.Length < 3))
            throw Invalido("Este turno já está no livro: diga o motivo da mudança (mínimo de 3 letras).");

        var plano = EquipeRegras.PlanoDoLivro(temRegistro, antes?.Valor ?? 0, status, justificada, rem.Valor);
        if (plano != EquipeRegras.Plano.Nada && temRegistro
            && await c.ExecuteScalarAsync<string>("SELECT status FROM gestao_lancamentos_situacao WHERE id = @id", new { id = antes!.LancamentoId }) == StatusLancamento.Pago)
            throw Invalido("Este turno já foi pago: corrija com desconto ou adiantamento no próximo pagamento.");

        var agora = _relogio.GetUtcNow();
        var turnoId = antes?.Id ?? Guid.NewGuid();
        var revisao = antes?.Revisao ?? 0;
        Guid? acontecimentoId = antes?.AcontecimentoId, lancamentoId = antes?.LancamentoId;
        var origem = revisao > 0 ? $"{chave}~{revisao}" : chave;
        var historico = await c.ExecuteScalarAsync<int>("SELECT count(*) FROM gestao_turnos_historico WHERE turno_id = @turnoId", new { turnoId });
        switch (plano)
        {
            case EquipeRegras.Plano.Ajustar:
                await livro.Ajustar(loja, usuario, new AjusteRequest
                {
                    ChaveIdempotencia = $"equipe:turno:{origem}:ajuste:{historico}", LancamentoId = lancamentoId, ValorNovo = rem.Valor,
                    Motivo = motivo, ConfirmacaoHumana = true,
                }, ct);
                break;
            case EquipeRegras.Plano.Estornar:
                await livro.Estornar(loja, usuario, acontecimentoId!.Value, new EstornoRequest
                {
                    ChaveIdempotencia = $"equipe:turno:{origem}:estorno", Motivo = motivo, ConfirmacaoHumana = true,
                }, ct);
                (acontecimentoId, lancamentoId, revisao) = (null, null, revisao + 1);
                break;
        }

        await using var tx = await c.BeginTransactionAsync(ct);
        if (plano == EquipeRegras.Plano.Publicar)
        {
            // Publica junto com a gravação do turno: ou os dois, ou nenhum.
            var r = await livro.Publicar(c, tx, loja, usuario, new PublicarAcontecimentoRequest
            {
                ChaveIdempotencia = $"equipe:turno:{origem}", Tipo = TipoTurno, Modulo = ModuloGestao.Equipe, DataFato = inicio,
                Descricao = Cortar($"Turno · {pessoa.Nome} · {req.Data:dd/MM} · {entrada}–{saida}", 300)!,
                Origem = new OrigemRef { Tipo = TipoTurno, Id = origem },
                Dados = JsonSerializer.SerializeToElement(new
                {
                    pessoaId = pessoa.Id, pessoaNome = pessoa.Nome, turnoId = chave, data = req.Data, entrada, saida, entregas = jornada.Entregas,
                    avulso = baseTurno.Avulso, status, justificada, itens = rem.Itens,
                }, Json),
                Lancamentos = new()
                {
                    new LancamentoRequest
                    {
                        Natureza = Natureza.Despesa, Valor = rem.Valor, Status = StatusLancamento.APagar,
                        MesReferencia = new DateOnly(req.Data.Year, req.Data.Month, 1), Categoria = "equipe", Finalidade = "remuneracao",
                        Descricao = Cortar($"Turno · {pessoa.Nome} · {req.Data:dd/MM}", 300),
                    },
                },
            }, ct);
            acontecimentoId = r.Id;
            lancamentoId = r.Acontecimento.Lancamentos[0].Id;
        }
        await c.ExecuteAsync(@"
INSERT INTO gestao_turnos (id, estabelecimento_id, pessoa_id, data, entrada, saida, status, avulso, justificada, entregas, pagamento_avulso,
    adicionais, base_comissao, valor, previsto, itens, revisao, acontecimento_id, lancamento_id, criado_em, atualizado_em)
VALUES (@turnoId, @loja, @pessoaId, @data, @entrada, @saida, @status, @avulso, @justificada, @entregas, CAST(@pagamentoAvulso AS jsonb),
    CAST(@adicionais AS jsonb), @baseComissao, @valor, @previsto, CAST(@itens AS jsonb), @revisao, @acontecimentoId, @lancamentoId, @agora, @agora)
ON CONFLICT (estabelecimento_id, pessoa_id, data) DO UPDATE SET entrada = EXCLUDED.entrada, saida = EXCLUDED.saida, status = EXCLUDED.status,
    justificada = EXCLUDED.justificada, entregas = EXCLUDED.entregas, pagamento_avulso = EXCLUDED.pagamento_avulso,
    adicionais = EXCLUDED.adicionais, base_comissao = EXCLUDED.base_comissao, valor = EXCLUDED.valor, previsto = EXCLUDED.previsto,
    itens = EXCLUDED.itens, revisao = EXCLUDED.revisao, acontecimento_id = EXCLUDED.acontecimento_id,
    lancamento_id = EXCLUDED.lancamento_id, atualizado_em = EXCLUDED.atualizado_em", new
        {
            turnoId, loja, pessoaId = pessoa.Id, data = req.Data.ToDateTime(TimeOnly.MinValue), entrada, saida, status, avulso = baseTurno.Avulso,
            justificada, entregas = jornada.Entregas,
            pagamentoAvulso = jornada.PagamentoAvulso is null ? null : JsonSerializer.Serialize(jornada.PagamentoAvulso, Json),
            adicionais = JsonSerializer.Serialize(adicionais, Json), baseComissao = jornada.BaseComissao, valor = rem.Valor, previsto = rem.Previsto,
            itens = JsonSerializer.Serialize(rem.Itens, Json), revisao, acontecimentoId, lancamentoId, agora,
        }, tx);
        var detalhes = new List<string>();
        if (antes is not null && (antes.Entrada != entrada || antes.Saida != saida)) detalhes.Add($"horário {antes.Entrada}–{antes.Saida} → {entrada}–{saida}");
        if (antes is not null && antes.Entregas != jornada.Entregas) detalhes.Add($"entregas {antes.Entregas} → {jornada.Entregas}");
        if (antes is not null && antes.Justificada != justificada) detalhes.Add(justificada ? "falta justificada" : "falta sem justificativa");
        if (antes is not null && !antes.Adicionais.SequenceEqual(adicionais)) detalhes.Add("adicionais: " + (adicionais.Count == 0 ? "nenhum" : string.Join(", ", adicionais.Select(a => a.Rotulo))));
        var acao = antes is null && baseTurno.Avulso ? "avulso" : antes is null || antes.Status != status ? status : "ajuste";
        await c.ExecuteAsync(@"
INSERT INTO gestao_turnos_historico (id, estabelecimento_id, turno_id, em, por, acao, valor_anterior, valor_novo, motivo, detalhe)
VALUES (@id, @loja, @turnoId, @agora, @usuario, @acao, @anterior, @novo, @motivo, @detalhe)", new
        {
            id = Guid.NewGuid(), loja, turnoId, agora, usuario, acao, anterior = antes?.Valor ?? baseTurno.Valor, novo = rem.Valor,
            motivo = Cortar(motivo, 300), detalhe = detalhes.Count == 0 ? null : Cortar(string.Join(" · ", detalhes), 300),
        }, tx);
        await tx.CommitAsync(ct);
        return await TurnoComHistorico(c, loja, turnoId);
    }

    // ---------------------------------------------------------------- dinheiro

    public async Task<IReadOnlyList<ResumoPessoa>> Resumo(Guid loja, DateOnly mes, CancellationToken ct)
    {
        mes = new DateOnly(mes.Year, mes.Month, 1);
        await using var c = await dataSource.OpenConnectionAsync(ct);
        var dias = Enumerable.Range(0, DateTime.DaysInMonth(mes.Year, mes.Month)).Select(mes.AddDays).ToList();
        var (pessoas, grade) = await Grade(c, loja, dias);
        var r = new List<ResumoPessoa>();
        foreach (var p in pessoas)
        {
            var turnos = grade[p.Id];
            var totais = EquipeRegras.Totais(turnos);
            var itens = (await Itens(c, loja, p.Id)).Where(i => i.MesReferencia == mes && i.Valor > 0).ToList();
            var adiant = await Adiantamentos(c, loja, p.Id);
            var pago = itens.Where(i => i.Status == StatusLancamento.Pago).Sum(i => i.Valor);
            var aPagar = itens.Where(i => i.Status == StatusLancamento.APagar).Sum(i => i.Valor);
            r.Add(new ResumoPessoa(p.Id, p.Nome, totais.Previsto, totais.Apurado, pago + aPagar, pago, aPagar, adiant.Sum(a => a.Aberto),
                totais.Faltas, itens, adiant.Where(a => a.Aberto > 0 || (a.Data.Year == mes.Year && a.Data.Month == mes.Month)).ToList()));
        }
        return r;
    }

    public async Task<AcontecimentoDto> Adiantar(Guid loja, int usuario, AdiantamentoRequest req, CancellationToken ct)
    {
        if (req.Valor <= 0) throw Invalido("Valor do adiantamento deve ser maior que zero.");
        PessoaDto pessoa;
        await using (var c = await dataSource.OpenConnectionAsync(ct)) pessoa = await ObterPessoa(c, loja, req.PessoaId);
        var data = req.Data ?? AcontecimentoRegras.DiaLocal(_relogio.GetUtcNow(), await livro.Fuso(loja, ct));
        return (await livro.Publicar(loja, usuario, new PublicarAcontecimentoRequest
        {
            ChaveIdempotencia = ChaveObrigatoria(req.ChaveIdempotencia), Tipo = TipoAdiantamento, Modulo = ModuloGestao.Equipe,
            DataFato = _relogio.GetUtcNow(), Descricao = Cortar($"Adiantamento · {pessoa.Nome}" + (string.IsNullOrWhiteSpace(req.Motivo) ? "" : $" · {req.Motivo.Trim()}"), 300)!,
            Dados = JsonSerializer.SerializeToElement(new { pessoaId = pessoa.Id, pessoaNome = pessoa.Nome, data }, Json),
            Lancamentos = new()
            {
                new LancamentoRequest { Natureza = Natureza.Despesa, Valor = req.Valor, ContaId = req.ContaId, DataPagamento = data,
                    Categoria = "equipe", Finalidade = "adiantamento", Descricao = Cortar($"Adiantamento · {pessoa.Nome}", 300) },
            },
        }, ct)).Acontecimento;
    }

    public async Task<AcontecimentoDto> Descontar(Guid loja, int usuario, DescontoRequest req, CancellationToken ct)
    {
        if (req.Valor <= 0) throw Invalido("Valor do desconto deve ser maior que zero.");
        if (string.IsNullOrWhiteSpace(req.Motivo) || req.Motivo.Trim().Length < 3) throw Invalido("Diga o motivo do desconto.");
        await using var c = await dataSource.OpenConnectionAsync(ct);
        var item = (await Itens(c, loja, req.PessoaId)).FirstOrDefault(i => i.LancamentoId == req.LancamentoId)
            ?? throw new GestaoException("NAO_ENCONTRADO", "Item a pagar não encontrado para esta pessoa.", 404);
        if (item.Status != StatusLancamento.APagar) throw Invalido("Desconto só em item ainda a pagar.");
        if (req.Valor > item.Valor) throw Invalido("O desconto passa do valor do item.");
        return (await livro.Ajustar(loja, usuario, new AjusteRequest
        {
            ChaveIdempotencia = ChaveObrigatoria(req.ChaveIdempotencia), LancamentoId = item.LancamentoId,
            ValorNovo = item.Valor - req.Valor, Motivo = Cortar("Desconto: " + req.Motivo.Trim(), 300), ConfirmacaoHumana = true,
        }, ct)).Acontecimento;
    }

    public async Task<PagamentoFeito> Pagar(Guid loja, int usuario, PagamentoRequest req, CancellationToken ct)
    {
        var chave = ChaveObrigatoria(req.ChaveIdempotencia);
        if (chave.Length > 100) throw Invalido("Chave de idempotência longa demais (até 100).");
        await using var c = await dataSource.OpenConnectionAsync(ct);
        var pessoa = await ObterPessoa(c, loja, req.PessoaId);
        var trava = "gestao-pagamento:" + loja + ":" + pessoa.Id;
        await c.ExecuteAsync("SELECT pg_advisory_lock(hashtext(@trava))", new { trava });
        try
        {
            var data = req.DataPagamento ?? AcontecimentoRegras.DiaLocal(_relogio.GetUtcNow(), await livro.Fuso(loja, ct));

            // 1. Adiantamentos em aberto abatem dos itens a pagar (ajuste com motivo; a ligação fica em gestao_equipe_compensacoes).
            var compensado = 0m;
            var aPagar = (await Itens(c, loja, pessoa.Id)).Where(i => i.Status == StatusLancamento.APagar && i.Valor > 0).ToList();
            foreach (var (adiantamentoId, lancamentoId, valor) in EquipeRegras.Compensar(await Adiantamentos(c, loja, pessoa.Id), aPagar))
            {
                var item = aPagar.First(i => i.LancamentoId == lancamentoId);
                var ajuste = await livro.Ajustar(loja, usuario, new AjusteRequest
                {
                    ChaveIdempotencia = $"equipe:compensa:{adiantamentoId}:{lancamentoId}", LancamentoId = lancamentoId,
                    ValorNovo = item.Valor - valor, Motivo = "Compensação de adiantamento", ConfirmacaoHumana = true,
                }, ct);
                await c.ExecuteAsync(@"
INSERT INTO gestao_equipe_compensacoes (estabelecimento_id, adiantamento_id, lancamento_id, valor, ajuste_id, criado_em)
VALUES (@loja, @adiantamentoId, @lancamentoId, @valor, @ajusteId, @agora) ON CONFLICT DO NOTHING",
                    new { loja, adiantamentoId, lancamentoId, valor, ajusteId = ajuste.Id, agora = _relogio.GetUtcNow() });
                aPagar[aPagar.IndexOf(item)] = item with { Valor = item.Valor - valor };
                compensado += valor;
            }
            aPagar = aPagar.Where(i => i.Valor > 0).ToList();

            // 2. O que esta mesma chave já pagou (nova tentativa depois de uma falha no meio) não se paga de novo.
            var jaPago = await c.ExecuteScalarAsync<decimal>(@"
SELECT coalesce(sum(q.valor), 0) FROM gestao_liquidacoes q
  JOIN gestao_acontecimentos a ON a.estabelecimento_id = q.estabelecimento_id AND a.id = q.acontecimento_id
 WHERE a.estabelecimento_id = @loja AND left(a.chave_idempotencia, @n) = @prefixo",
                new { loja, prefixo = chave + ":", n = chave.Length + 1 });
            var alvo = req.Valor ?? aPagar.Sum(i => i.Valor) + jaPago;
            var restante = alvo - jaPago;
            var liquidados = new List<Guid>();
            Guid? saldoId = null;
            if (restante > 0)
            {
                var (inteiros, parcial) = EquipeRegras.Dividir(aPagar, restante);
                foreach (var (id, _) in inteiros)
                {
                    await Liquidar(loja, usuario, chave, id, req.ContaId, data, ct);
                    liquidados.Add(id);
                }
                if (parcial is { } p)
                {
                    var item = aPagar.First(i => i.LancamentoId == p.Id);
                    await livro.Ajustar(loja, usuario, new AjusteRequest
                    {
                        ChaveIdempotencia = $"{chave}:parcial:{p.Id}", LancamentoId = p.Id, ValorNovo = p.Pago, ConfirmacaoHumana = true,
                        Motivo = $"Pagamento parcial: {p.Resto:N2} ficam como saldo a pagar",
                    }, ct);
                    var saldo = await livro.Publicar(loja, usuario, new PublicarAcontecimentoRequest
                    {
                        ChaveIdempotencia = $"{chave}:saldo:{p.Id}", Tipo = TipoSaldo, Modulo = ModuloGestao.Equipe, DataFato = _relogio.GetUtcNow(),
                        Descricao = Cortar($"Saldo a pagar · {pessoa.Nome} · {item.Descricao}", 300)!,
                        Origem = new OrigemRef { Tipo = "saldo", Id = p.Id.ToString() },
                        Dados = JsonSerializer.SerializeToElement(new { pessoaId = pessoa.Id, pessoaNome = pessoa.Nome, data = item.Data, de = p.Id }, Json),
                        Lancamentos = new()
                        {
                            new LancamentoRequest { Natureza = Natureza.Despesa, Valor = p.Resto, Status = StatusLancamento.APagar,
                                MesReferencia = item.MesReferencia, Categoria = "equipe", Finalidade = "remuneracao",
                                Descricao = Cortar($"Saldo · {pessoa.Nome}", 300) },
                        },
                    }, ct);
                    saldoId = saldo.Acontecimento.Lancamentos[0].Id;
                    await Liquidar(loja, usuario, chave, p.Id, req.ContaId, data, ct);
                    liquidados.Add(p.Id);
                }
            }
            var resumo = (await Resumo(loja, new DateOnly(data.Year, data.Month, 1), ct)).FirstOrDefault(x => x.PessoaId == pessoa.Id)
                ?? new ResumoPessoa(pessoa.Id, pessoa.Nome, 0, 0, 0, 0, 0, 0, 0, Array.Empty<ItemDevido>(), Array.Empty<AdiantamentoDto>());
            var pagoAgora = await c.ExecuteScalarAsync<decimal>(@"
SELECT coalesce(sum(q.valor), 0) FROM gestao_liquidacoes q
  JOIN gestao_acontecimentos a ON a.estabelecimento_id = q.estabelecimento_id AND a.id = q.acontecimento_id
 WHERE a.estabelecimento_id = @loja AND left(a.chave_idempotencia, @n) = @prefixo",
                new { loja, prefixo = chave + ":", n = chave.Length + 1 });
            var restam = (await Itens(c, loja, pessoa.Id)).Where(i => i.Status == StatusLancamento.APagar).Sum(i => i.Valor);
            return new PagamentoFeito(pagoAgora, compensado, restam, liquidados, saldoId, resumo);
        }
        finally
        {
            await c.ExecuteAsync("SELECT pg_advisory_unlock(hashtext(@trava))", new { trava });
        }
    }

    private Task Liquidar(Guid loja, int usuario, string chave, Guid lancamentoId, Guid contaId, DateOnly data, CancellationToken ct) =>
        livro.Liquidar(loja, usuario, new LiquidarRequest
        {
            ChaveIdempotencia = $"{chave}:{lancamentoId}", LancamentoId = lancamentoId, ContaId = contaId, DataPagamento = data,
        }, ct);

    // ---------------------------------------------------------------- leitura

    /// <summary>Itens de turno e de saldo da pessoa no livro, com o valor líquido (original + ajustes + estorno) e o status atual.</summary>
    private static async Task<List<ItemDevido>> Itens(NpgsqlConnection c, Guid loja, Guid pessoaId) =>
        (await c.QueryAsync<(Guid LancamentoId, Guid AcontecimentoId, string Tipo, DateTime Data, string Descricao, decimal Valor, string Status,
            DateTime Mes)>(@"
SELECT s.id, a.id, a.tipo, coalesce((a.dados->>'data')::date, a.data_fato::date), a.descricao,
       s.valor + coalesce((SELECT sum(x.valor) FROM gestao_lancamentos x
                            WHERE x.estabelecimento_id = s.estabelecimento_id AND x.corrige_lancamento_id = s.id), 0),
       s.status, s.mes_referencia
  FROM gestao_acontecimentos a
  JOIN gestao_lancamentos_situacao s ON s.estabelecimento_id = a.estabelecimento_id AND s.acontecimento_id = a.id AND s.corrige_lancamento_id IS NULL
 WHERE a.estabelecimento_id = @loja AND a.tipo IN ('turno', 'saldo_remuneracao') AND a.corrige_id IS NULL
   AND a.dados->>'pessoaId' = @pessoa
 ORDER BY 4, 1", new { loja, pessoa = pessoaId.ToString() }))
        .Select(x => new ItemDevido(x.LancamentoId, x.AcontecimentoId, x.Tipo, DateOnly.FromDateTime(x.Data), x.Descricao, x.Valor, x.Status,
            DateOnly.FromDateTime(x.Mes))).ToList();

    private static async Task<List<AdiantamentoDto>> Adiantamentos(NpgsqlConnection c, Guid loja, Guid pessoaId) =>
        (await c.QueryAsync<(Guid Id, DateTime Data, decimal Valor, decimal Compensado)>(@"
SELECT a.id, coalesce((a.dados->>'data')::date, a.data_fato::date),
       sum(l.valor) + coalesce((SELECT sum(x.valor) FROM gestao_lancamentos x JOIN gestao_lancamentos o ON o.id = x.corrige_lancamento_id
                                 WHERE o.acontecimento_id = a.id AND x.estabelecimento_id = a.estabelecimento_id), 0),
       coalesce((SELECT sum(k.valor) FROM gestao_equipe_compensacoes k WHERE k.estabelecimento_id = a.estabelecimento_id AND k.adiantamento_id = a.id), 0)
  FROM gestao_acontecimentos a
  JOIN gestao_lancamentos l ON l.estabelecimento_id = a.estabelecimento_id AND l.acontecimento_id = a.id AND l.corrige_lancamento_id IS NULL
 WHERE a.estabelecimento_id = @loja AND a.tipo = 'adiantamento' AND a.corrige_id IS NULL AND a.dados->>'pessoaId' = @pessoa
 GROUP BY a.id ORDER BY 2, 1", new { loja, pessoa = pessoaId.ToString() }))
        .Select(x => new AdiantamentoDto(x.Id, DateOnly.FromDateTime(x.Data), x.Valor, x.Compensado, Math.Max(0, x.Valor - x.Compensado))).ToList();

    /// <summary>Gravados valem por cima da escala; previsto acompanha a regra atual, confirmado guarda o valor do dia.</summary>
    private async Task<(IReadOnlyList<PessoaDto>, Dictionary<Guid, IReadOnlyList<TurnoDto?>>)> Grade(NpgsqlConnection c, Guid loja,
        IReadOnlyList<DateOnly> dias)
    {
        var gravados = (await c.QueryAsync<TurnoRow>(
            $"SELECT {ColunasTurno} FROM gestao_turnos WHERE estabelecimento_id = @loja AND data BETWEEN @de AND @ate",
            new { loja, de = dias[0].ToDateTime(TimeOnly.MinValue), ate = dias[^1].ToDateTime(TimeOnly.MinValue) }))
            .Select(t => Turno(t, Array.Empty<EventoTurno>())).ToList();
        var comTurno = gravados.Select(t => t.PessoaId).ToHashSet();
        // Quem saiu da equipe ainda aparece nos dias em que trabalhou.
        var pessoas = (await Pessoas(loja, true, CancellationToken.None)).Where(p => p.Ativo || comTurno.Contains(p.Id)).ToList();
        var porChave = gravados.ToDictionary(t => t.Chave);
        var grade = new Dictionary<Guid, IReadOnlyList<TurnoDto?>>();
        foreach (var p in pessoas)
            grade[p.Id] = dias.Select(d =>
            {
                if (porChave.TryGetValue(EquipeRegras.Chave(p.Id, d), out var g))
                {
                    if (g.Status != StatusTurno.Previsto) return g;
                    var rem = EquipeRegras.Calcular(p.Regra, Jornada(g));
                    return g with { Valor = rem.Valor, Previsto = rem.Previsto, Itens = rem.Itens };
                }
                return EquipeRegras.DaEscala(p, d) is { } padrao ? Novo(p, d, padrao.Entrada, padrao.Saida, false, null) : null;
            }).ToList();
        return (pessoas, grade);
    }

    private static TurnoDto Novo(PessoaDto p, DateOnly data, string entrada, string saida, bool avulso, PagamentoAvulso? pagamentoAvulso)
    {
        var jornada = new JornadaTurno(entrada, saida, StatusTurno.Previsto, false, 0, pagamentoAvulso, Array.Empty<AdicionalTurno>(), 0);
        var rem = EquipeRegras.Calcular(p.Regra, jornada);
        return new TurnoDto(null, EquipeRegras.Chave(p.Id, data), p.Id, data, entrada, saida, StatusTurno.Previsto, avulso, false, 0,
            pagamentoAvulso, Array.Empty<AdicionalTurno>(), 0, rem.Valor, rem.Previsto, rem.Itens, 0, null, null, Array.Empty<EventoTurno>());
    }

    private static JornadaTurno Jornada(TurnoDto t) =>
        new(t.Entrada, t.Saida, t.Status, t.Justificada, t.Entregas, t.PagamentoAvulso, t.Adicionais, t.BaseComissao);

    private static async Task<TurnoDto> TurnoComHistorico(NpgsqlConnection c, Guid loja, Guid id)
    {
        var row = await c.QuerySingleAsync<TurnoRow>($"SELECT {ColunasTurno} FROM gestao_turnos WHERE estabelecimento_id = @loja AND id = @id", new { loja, id });
        var historico = (await c.QueryAsync<(DateTime Em, int? Por, string Acao, decimal? Anterior, decimal? Novo, string? Motivo, string? Detalhe)>(@"
SELECT em, por, acao, valor_anterior, valor_novo, motivo, detalhe FROM gestao_turnos_historico
 WHERE estabelecimento_id = @loja AND turno_id = @id ORDER BY em, id", new { loja, id }))
            .Select(h => new EventoTurno(new DateTimeOffset(DateTime.SpecifyKind(h.Em, DateTimeKind.Utc)), h.Por, h.Acao, h.Anterior, h.Novo,
                h.Motivo, h.Detalhe)).ToList();
        return Turno(row, historico);
    }

    private static TurnoDto Turno(TurnoRow t, IReadOnlyList<EventoTurno> historico)
    {
        var data = DateOnly.FromDateTime(t.Data);
        return new TurnoDto(t.Id, EquipeRegras.Chave(t.PessoaId, data), t.PessoaId, data, t.Entrada, t.Saida, t.Status, t.Avulso, t.Justificada,
            t.Entregas, t.PagamentoAvulso is null ? null : JsonSerializer.Deserialize<PagamentoAvulso>(t.PagamentoAvulso, Json),
            JsonSerializer.Deserialize<List<AdicionalTurno>>(t.Adicionais, Json)!, t.BaseComissao, t.Valor, t.Previsto,
            JsonSerializer.Deserialize<List<ItemRemuneracao>>(t.Itens, Json)!, t.Revisao, t.AcontecimentoId, t.LancamentoId, historico);
    }

    private static async Task<PessoaDto> ObterPessoa(NpgsqlConnection c, Guid loja, Guid id) =>
        Pessoa(await c.QuerySingleOrDefaultAsync<PessoaRow>($"SELECT {ColunasPessoa} FROM gestao_pessoas WHERE estabelecimento_id = @loja AND id = @id",
            new { loja, id }) ?? throw new GestaoException("NAO_ENCONTRADO", "Pessoa não encontrada.", 404));

    private static PessoaDto Pessoa(PessoaRow r) => new(r.Id, r.Nome, r.Funcao, r.Vinculo, JsonSerializer.Deserialize<RegraPagamento>(r.Regra, Json)!,
        r.PagaCom, r.ChavePix, r.UsuarioId, r.Cor, JsonSerializer.Deserialize<List<DiaPadrao>>(r.Escala, Json)!, r.Ativo,
        DateOnly.FromDateTime(r.Desde), r.CriadoPor, new DateTimeOffset(DateTime.SpecifyKind(r.CriadoEm, DateTimeKind.Utc)), r.Versao);

    private static string ChaveObrigatoria(string? chave) => string.IsNullOrWhiteSpace(chave)
        ? throw Invalido("Envie a chave de idempotência (gerada uma vez por envio).")
        : chave.Trim();

    private static string? Cortar(string? s, int max) => s is null || s.Length <= max ? s : s[..max];

    private static GestaoException Invalido(string m) => new("CAMPO_INVALIDO", m, 422);
}

internal static class DynamicParametersExtensions
{
    public static DynamicParameters Tap(this DynamicParameters d, Action<DynamicParameters> acao) { acao(d); return d; }
}
