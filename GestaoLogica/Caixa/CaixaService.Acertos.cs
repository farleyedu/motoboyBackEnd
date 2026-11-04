using System.Globalization;
using System.Text.Json;
using APIBack.Acontecimentos;
using APIBack.GestaoLogica.Vendas;
using APIBack.Security;
using Dapper;
using Npgsql;

namespace APIBack.GestaoLogica.Caixa;

/// <summary>
/// 6C5: aba Motoboys do Caixa e recebimento do que ficou a receber. O dinheiro da entrega com motoboy fica a receber na conta
/// "Dinheiro com motoboys" (<see cref="VendaDoPedido"/>); o acerto liquida essas linhas para a gaveta numa transação só, lança a
/// diferença (quebra ou sobra) e grava o lote (gestao_caixa_acertos). Pix e iFood só aparecem; cartão exige a conferência do caixa.
/// </summary>
public sealed partial class CaixaService
{
    public const string TipoDiferencaAcerto = "diferenca_acerto";
    /// <summary>Até quantos dias para trás uma entrega ainda aparece pendente com o motoboy.</summary>
    private const int DiasDePendencia = 30;

    private sealed class LinhaVenda
    {
        public Guid VendaId { get; set; }
        public string PedidoTexto { get; set; } = "";
        public DateTime Em { get; set; }
        public string Descricao { get; set; } = "";
        public int? MotoboyId { get; set; }
        public string? Forma { get; set; }
        public Guid LancamentoId { get; set; }
        public decimal Liquido { get; set; }
        public string Status { get; set; } = "";
        public string? ContaTipo { get; set; }
        public string? ContaSubtipo { get; set; }
    }

    private sealed record Parte(string Tipo, decimal Valor, string Status, Guid Lancamento);

    private sealed record EntregaPendente(int PedidoId, Guid VendaId, DateTimeOffset Em, int MotoboyId, IReadOnlyList<Parte> Partes)
    {
        public decimal Soma(string tipo) => CaixaRegras.R2(Partes.Where(p => p.Tipo == tipo).Sum(p => p.Valor));
    }

    private sealed class LoteRow
    {
        public Guid Id { get; set; }
        public int Numero { get; set; }
        public DateTime Dia { get; set; }
        public int MotoboyId { get; set; }
        public string Tipo { get; set; } = "";
        public Guid? CorrigeId { get; set; }
        public decimal Esperado { get; set; }
        public decimal Entregue { get; set; }
        public decimal Diferenca { get; set; }
        public string? Motivo { get; set; }
        public string Situacao { get; set; } = "";
        public int PendentesDepois { get; set; }
        public int Operador { get; set; }
        public string? OperadorNome { get; set; }
        public DateTime Em { get; set; }
        public Guid? CorrigidoPor { get; set; }
    }

    private sealed class ItemRow
    {
        public Guid AcertoId { get; set; }
        public int PedidoId { get; set; }
        public decimal Dinheiro { get; set; }
        public decimal DigitalIntegracao { get; set; }
        public decimal CartaoManual { get; set; }
        public decimal Ifood { get; set; }
        public bool ConferidoManual { get; set; }
    }

    private sealed class RotaRow
    {
        public int MotoboyId { get; set; }
        public int PedidoId { get; set; }
        public string? Valor { get; set; }
        public string? Forma { get; set; }
        public DateTime Em { get; set; }
    }

    // ------------------------------------------------------------------ leitura

    public async Task<IReadOnlyList<MotoboyDoDia>> MotoboysDoDia(Guid loja, DateOnly dia, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        var pendentes = await Pendentes(c, null, loja, null);
        var rota = await EmRota(c, loja);
        var lotes = await Lotes(c, null, loja, dia, null, null);
        var ids = pendentes.Select(p => p.MotoboyId).Concat(rota.Select(r => r.MotoboyId)).Concat(lotes.Select(l => l.MotoboyId)).Distinct().ToArray();
        var nomes = await NomesMotoboys(c, null, ids);
        var clientes = await Clientes(c, loja, pendentes.Select(p => p.PedidoId).Concat(rota.Select(r => r.PedidoId)).Distinct().ToArray());
        var presencas = chegadas is null ? new Dictionary<int, ChegadaDoMotoboy>() : await chegadas.Ler(c, loja, ids, ct);
        return ids.Select(id =>
        {
            var presenca = presencas.GetValueOrDefault(id);
            var meus = pendentes.Where(p => p.MotoboyId == id).ToList();
            var entregas = meus.Select(p => new EntregaDoMotoboy(p.PedidoId, $"#{p.PedidoId}", clientes.GetValueOrDefault(p.PedidoId), p.Em,
                "entregue", CaixaRegras.R2(p.Partes.Sum(x => x.Valor)), p.Partes.Select(x => new ParteDaEntrega(x.Tipo, x.Valor, x.Status)).ToList())).ToList();
            var naRua = rota.Where(r => r.MotoboyId == id).Select(r =>
            {
                var valor = Valor(r.Valor);
                return new EntregaDoMotoboy(r.PedidoId, $"#{r.PedidoId}", clientes.GetValueOrDefault(r.PedidoId), Utc(r.Em), "em_rota", valor,
                    new[] { new ParteDaEntrega(PartePrevista(r.Forma), valor, StatusLancamento.Previsto) });
            }).ToList();
            return new MotoboyDoDia(id, nomes.GetValueOrDefault(id) ?? $"Motoboy {id}", presenca?.Estado, presenca?.Desde,
                CaixaRegras.R2(meus.Sum(p => p.Soma(ParteAcerto.Dinheiro))), CaixaRegras.R2(meus.Sum(p => p.Soma(ParteAcerto.CartaoManual))),
                CaixaRegras.R2(meus.Sum(p => p.Soma(ParteAcerto.Integracao))), CaixaRegras.R2(meus.Sum(p => p.Soma(ParteAcerto.Ifood))),
                entregas, naRua, lotes.Where(l => l.MotoboyId == id).ToList(), presenca?.Sinal, presenca?.PosicaoEm, presenca?.Origem);
        }).OrderByDescending(m => m.DinheiroCom).ThenBy(m => m.Nome, StringComparer.CurrentCulture).ToList();
    }

    public async Task<IReadOnlyList<AcertoDto>> Acertos(Guid loja, DateOnly dia, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        return await Lotes(c, null, loja, dia, null, null);
    }

    public async Task<SituacaoAcertoMotoboy> SituacaoDoMotoboy(Guid loja, int motoboyId, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        var pendentes = await Pendentes(c, null, loja, motoboyId);
        var ultimo = (await Lotes(c, null, loja, null, motoboyId, null)).LastOrDefault();
        return new SituacaoAcertoMotoboy(motoboyId, pendentes.Count, CaixaRegras.R2(pendentes.Sum(p => p.Soma(ParteAcerto.Dinheiro))),
            pendentes.Count == 0, ultimo);
    }

    public async Task<ConferenciasDoMotoboy> ConferenciasDoMotoboy(Guid loja, int motoboyId, DateTimeOffset desde, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        var pendentes = (await Pendentes(c, null, loja, motoboyId)).Where(p => p.Em >= desde);
        var lotes = await Lotes(c, null, loja, null, motoboyId, null);
        var correcoes = lotes.Where(l => l.Tipo == "correcao" && l.CorrigeId is not null).ToDictionary(l => l.CorrigeId!.Value);
        var pedidos = pendentes.Select(p => new ConferenciaDoPedido(p.PedidoId, "pendente", CaixaRegras.R2(p.Partes.Sum(x => x.Valor)),
            p.Soma(ParteAcerto.Dinheiro), null, null, null, null, null, 0m, null, null)).ToList();
        // Só lote original tem itens; a correção muda o valor entregue do lote inteiro (e a diferença), nunca quais pedidos entraram.
        foreach (var l in lotes.Where(l => l.Tipo == "acerto" && l.Em >= desde))
        {
            var correcao = correcoes.GetValueOrDefault(l.Id);
            var final = correcao ?? l;
            pedidos.AddRange(l.Itens.Select(i => new ConferenciaDoPedido(i.PedidoId, correcao is null ? "conferido" : "corrigido",
                CaixaRegras.R2(i.Dinheiro + i.DigitalIntegracao + i.CartaoManual + i.Ifood), i.Dinheiro, l.Id, l.Numero, l.Situacao, l.Em,
                PrimeiroNome(l.OperadorNome), final.Diferenca, correcao?.Em, final.Diferenca == 0 ? null : final.Motivo)));
        }
        return new ConferenciasDoMotoboy(loja, motoboyId, _relogio.GetUtcNow(), pedidos.OrderBy(p => p.PedidoId).ToList());
    }

    private static string? PrimeiroNome(string? nome) => nome?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

    public async Task<IReadOnlyList<VendaAReceber>> AReceberNoCaixa(Guid loja, CancellationToken ct)
    {
        await using var c = await dataSource.OpenConnectionAsync(ct);
        var gaveta = await c.ExecuteScalarAsync<Guid?>("SELECT conta_gaveta_id FROM gestao_caixas WHERE estabelecimento_id = @loja", new { loja });
        return (await LinhasAReceber(c, null, loja, gaveta, null)).GroupBy(l => l.VendaId).Select(g =>
        {
            var f = g.First();
            return new VendaAReceber(int.Parse(f.PedidoTexto, CultureInfo.InvariantCulture), $"#{f.PedidoTexto}", f.Descricao, Utc(f.Em),
                CaixaRegras.R2(g.Sum(l => l.Liquido)), f.Forma);
        }).ToList();
    }

    // ------------------------------------------------------------------ acerto

    public async Task<AcertoDto> Acertar(Guid loja, int usuario, GestaoPapeis.Acesso acesso, AcertoRequest req, CancellationToken ct)
    {
        if (!acesso.PodeLancar) throw new GestaoException("SEM_PERMISSAO", "Você não pode registrar no caixa.", 403);
        var chave = req.ChaveIdempotencia?.Trim();
        if (string.IsNullOrEmpty(chave) || chave.Length > 100) throw Invalido("Envie a chave de idempotência.");
        var itens = (req.Itens ?? Array.Empty<ItemAcertoRequest>()).GroupBy(i => i.PedidoId).Select(g => g.Last()).OrderBy(i => i.PedidoId).ToList();
        if (itens.Count == 0) throw Invalido("Escolha ao menos um pedido do motoboy.");
        var motivo = string.IsNullOrWhiteSpace(req.Motivo) ? null : Cortar(req.Motivo.Trim(), 300);
        var hoje = await SoHoje(loja, req.Dia, ct);
        var hash = AcontecimentoRegras.Hash(new { req.MotoboyId, itens, req.ValorEntregue, motivo });

        await using var c = await dataSource.OpenConnectionAsync(ct);
        if (await LotePorChave(c, null, loja, chave) is { } antes) return await Replay(c, loja, antes, hash);
        await DiaAberto(c, loja, hoje);
        var contas = await ContasDoCaixa(c, loja, null, ct);

        await using var tx = await c.BeginTransactionAsync(ct);
        // Um acerto por vez na loja: a numeração L-0001… e a mesma entrega não entram em dois lotes.
        await c.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended(@k, 0))", new { k = $"{loja}:caixa:acerto" }, tx);
        if (await LotePorChave(c, tx, loja, chave) is { } gravado) { await tx.CommitAsync(ct); return await Replay(c, loja, gravado, hash); }

        var pendentes = (await Pendentes(c, tx, loja, req.MotoboyId)).ToDictionary(p => p.PedidoId);
        // Outra tela (ou a reconexão) pode ter acertado antes: nada é confirmado com dado velho.
        var sumiram = itens.Where(i => !pendentes.ContainsKey(i.PedidoId)).Select(i => $"#{i.PedidoId}").ToList();
        if (sumiram.Count > 0)
            throw new GestaoException("ACERTO_DESATUALIZADO", $"{string.Join(", ", sumiram)} já foi acertado ou não está com este motoboy. Atualize a tela.", 409);
        var semConferir = itens.Where(i => !i.ConferidoManual && pendentes[i.PedidoId].Soma(ParteAcerto.CartaoManual) > 0).Select(i => $"#{i.PedidoId}").ToList();
        if (semConferir.Count > 0) throw Invalido($"Confira na maquininha o cartão de {string.Join(", ", semConferir)} antes de pôr no lote.");

        var escolhidos = itens.Select(i => pendentes[i.PedidoId]).ToList();
        var esperado = CaixaRegras.R2(escolhidos.Sum(p => p.Soma(ParteAcerto.Dinheiro)));
        var diferenca = AcertoRegras.Diferenca(esperado, req.ValorEntregue, motivo);
        var numero = await c.ExecuteScalarAsync<int>("SELECT coalesce(max(numero), 0) + 1 FROM gestao_caixa_acertos WHERE estabelecimento_id = @loja",
            new { loja }, tx);
        var rotulo = AcertoRegras.Numero(numero);
        var nome = (await NomesMotoboys(c, tx, new[] { req.MotoboyId })).GetValueOrDefault(req.MotoboyId) ?? $"motoboy {req.MotoboyId}";
        var id = Guid.NewGuid();

        var liquidacoes = new Dictionary<int, Guid[]>();
        foreach (var p in escolhidos)
        {
            var feitas = new List<Guid>();
            foreach (var parte in p.Partes.Where(x => x.Tipo == ParteAcerto.Dinheiro && x.Valor > 0))
                feitas.Add((await livro.Liquidar(c, tx, loja, usuario, new LiquidarRequest
                {
                    ChaveIdempotencia = $"acerto:{chave}:{parte.Lancamento}", LancamentoId = parte.Lancamento, ContaId = contas.Gaveta,
                    DataPagamento = hoje, Descricao = Cortar($"Acerto {rotulo} · {nome} · pedido #{p.PedidoId}", 300),
                }, ct)).Id);
            liquidacoes[p.PedidoId] = feitas.ToArray();
        }
        var diferencaId = diferenca == 0 ? (Guid?)null : await PublicarDiferenca(c, tx, loja, usuario, acesso, contas.Gaveta, hoje,
            $"acerto:{chave}:diferenca", id, rotulo, nome, req.MotoboyId, diferenca, motivo!, "Acerto");

        var pendentesDepois = pendentes.Count - escolhidos.Count;
        await InserirLote(c, tx, id, loja, numero, chave, hash, hoje, req.MotoboyId, "acerto", null, esperado, req.ValorEntregue, diferenca, motivo,
            AcertoRegras.Situacao(pendentesDepois), pendentesDepois, diferencaId, usuario);
        try
        {
            foreach (var (p, i) in escolhidos.Zip(itens))
                await c.ExecuteAsync(@"
INSERT INTO gestao_caixa_acerto_itens (acerto_id, estabelecimento_id, pedido_id, venda_id, dinheiro, digital_integracao, cartao_manual, ifood,
       conferido_manual, liquidacoes)
VALUES (@id, @loja, @pedido, @venda, @dinheiro, @integracao, @cartao, @ifood, @conferido, @liquidacoes)", new
                {
                    id, loja, pedido = p.PedidoId, venda = p.VendaId, dinheiro = p.Soma(ParteAcerto.Dinheiro), integracao = p.Soma(ParteAcerto.Integracao),
                    cartao = p.Soma(ParteAcerto.CartaoManual), ifood = p.Soma(ParteAcerto.Ifood), conferido = i.ConferidoManual,
                    liquidacoes = liquidacoes[p.PedidoId],
                }, tx);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new GestaoException("ACERTO_DESATUALIZADO", "Algum pedido deste lote já foi acertado. Atualize a tela.", 409);
        }
        await AvisarMotoboy(c, tx, loja, req.MotoboyId, id, escolhidos.Select(p => p.PedidoId).ToArray());
        await tx.CommitAsync(ct);
        return (await Lotes(c, null, loja, null, null, id)).Single();
    }

    /// <summary>Gerente corrige o valor entregue de um lote: lote novo (tipo correcao) aponta o original, que não muda.</summary>
    public async Task<AcertoDto> CorrigirAcerto(Guid loja, int usuario, GestaoPapeis.Acesso acesso, Guid acertoId, CorrigirAcertoRequest req,
        CancellationToken ct)
    {
        GestaoPapeis.ExigirCorrecao(acesso);
        var chave = req.ChaveIdempotencia?.Trim();
        if (string.IsNullOrEmpty(chave) || chave.Length > 100) throw Invalido("Envie a chave de idempotência.");
        var motivo = string.IsNullOrWhiteSpace(req.Motivo) ? null : Cortar(req.Motivo.Trim(), 300);
        if ((motivo?.Length ?? 0) < 3) throw Invalido("Diga o motivo da correção.");
        if (req.EntregueNovo < 0 || decimal.Round(req.EntregueNovo, 2) != req.EntregueNovo) throw Invalido("Valor entregue: zero ou mais, até 2 casas.");
        var hoje = await Hoje(loja, ct);
        var hash = AcontecimentoRegras.Hash(new { acertoId, req.EntregueNovo, motivo });

        await using var c = await dataSource.OpenConnectionAsync(ct);
        if (await LotePorChave(c, null, loja, chave) is { } antes) return await Replay(c, loja, antes, hash);
        await DiaAberto(c, loja, hoje);
        var contas = await ContasDoCaixa(c, loja, null, ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await c.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtextextended(@k, 0))", new { k = $"{loja}:caixa:acerto" }, tx);
        if (await LotePorChave(c, tx, loja, chave) is { } gravado) { await tx.CommitAsync(ct); return await Replay(c, loja, gravado, hash); }

        var original = (await Lotes(c, tx, loja, null, null, acertoId)).SingleOrDefault()
            ?? throw new GestaoException("NAO_ENCONTRADO", "Lote não encontrado.", 404);
        if (original.Tipo != "acerto") throw Invalido("Corrija o lote original, não a correção.");
        if (original.CorrigidoPor is not null) throw new GestaoException("JA_CORRIGIDO", "Este lote já foi corrigido.", 409);
        var delta = CaixaRegras.R2(req.EntregueNovo - original.Entregue);
        if (delta == 0) throw Invalido("O valor entregue é o mesmo do lote.");

        var numero = await c.ExecuteScalarAsync<int>("SELECT coalesce(max(numero), 0) + 1 FROM gestao_caixa_acertos WHERE estabelecimento_id = @loja",
            new { loja }, tx);
        var id = Guid.NewGuid();
        var nome = original.MotoboyNome ?? $"motoboy {original.MotoboyId}";
        var diferencaId = await PublicarDiferenca(c, tx, loja, usuario, acesso, contas.Gaveta, hoje, $"acerto:{chave}:correcao", id,
            original.Numero, nome, original.MotoboyId, delta, motivo!, "Correção do acerto");
        await InserirLote(c, tx, id, loja, numero, chave, hash, hoje, original.MotoboyId, "correcao", original.Id, original.Esperado, req.EntregueNovo,
            CaixaRegras.R2(req.EntregueNovo - original.Esperado), motivo, original.Situacao, original.PendentesDepois, diferencaId, usuario);
        await AvisarMotoboy(c, tx, loja, original.MotoboyId, id, original.Itens.Select(i => i.PedidoId).ToArray());
        await tx.CommitAsync(ct);
        return (await Lotes(c, null, loja, null, null, id)).Single();
    }

    // ------------------------------------------------------------------ recebimento

    public async Task<CaixaDoDia> Receber(Guid loja, int usuario, GestaoPapeis.Acesso acesso, RecebimentoRequest req, CancellationToken ct)
    {
        if (!acesso.PodeLancar) throw new GestaoException("SEM_PERMISSAO", "Você não pode registrar no caixa.", 403);
        var chave = req.ChaveIdempotencia?.Trim();
        if (string.IsNullOrEmpty(chave) || chave.Length > 100) throw Invalido("Envie a chave de idempotência.");
        // Uma forma só (como sempre) ou o pedido dividido em várias partes.
        var partes = (req.Pagamentos is { Count: > 0 } lista
            ? lista.Select(p => (Forma: p.Forma?.Trim() ?? "", p.Valor, p.ValorRecebido))
            : new[] { (Forma: req.Forma?.Trim() ?? "", Valor: 0m, ValorRecebido: (decimal?)req.ValorRecebido) }).ToList();
        var dividido = req.Pagamentos is { Count: > 1 };
        foreach (var p in partes)
            if (p.Forma is not (FormaCaixa.Dinheiro or FormaCaixa.Pix or FormaCaixa.Debito or FormaCaixa.Credito))
                throw Invalido("Forma: din, pix, deb ou cre. iFood é recebido pelo repasse.");
        if (dividido)
        {
            if (partes.Count > 4 || partes.Select(p => p.Forma).Distinct().Count() != partes.Count)
                throw Invalido("Uma parte por forma de pagamento (no máximo 4).");
            if (partes.Any(p => p.Valor <= 0 || decimal.Round(p.Valor, 2) != p.Valor)) throw Invalido("Cada parte: valor maior que zero, até 2 casas.");
        }
        var forma = partes[0].Forma;
        var hoje = await SoHoje(loja, req.Dia, ct);

        await using var c = await dataSource.OpenConnectionAsync(ct);
        await DiaAberto(c, loja, hoje);
        var contas = await ContasDoCaixa(c, loja, null, ct);
        Guid? maquininha = null;
        if (partes.Any(p => p.Forma != FormaCaixa.Dinheiro))
            maquininha = await c.ExecuteScalarAsync<Guid?>(@"SELECT id FROM gestao_contas
 WHERE estabelecimento_id = @loja AND tipo = 'recebivel' AND subtipo = 'maquininha' AND ativo ORDER BY criado_em, id LIMIT 1", new { loja })
                ?? throw new GestaoException("SEM_CONTA", "Cadastre a maquininha no Financeiro antes de receber em Pix ou cartão.", 409);
        Guid DestinoDe(string f) => f == FormaCaixa.Dinheiro ? contas.Gaveta : maquininha!.Value;

        await using var tx = await c.BeginTransactionAsync(ct);
        var pedido = req.PedidoId.ToString(CultureInfo.InvariantCulture);
        var linhas = await LinhasAReceber(c, tx, loja, contas.Gaveta, pedido);
        if (linhas.Count == 0)
        {
            // Reenvio do mesmo recebimento: as linhas já foram quitadas por esta chave.
            var feito = await c.ExecuteScalarAsync<bool>(@"SELECT EXISTS (SELECT 1 FROM gestao_acontecimentos
 WHERE estabelecimento_id = @loja AND chave_idempotencia LIKE @prefixo)", new { loja, prefixo = $"recebimento:{chave}:%" }, tx);
            if (!feito) throw new GestaoException("SEM_A_RECEBER", $"O pedido #{pedido} não tem nada a receber no caixa.", 404);
            await tx.CommitAsync(ct);
            return (await CarregarDia(loja, hoje, ct))!;
        }
        var total = CaixaRegras.R2(linhas.Sum(l => l.Liquido));
        if (!dividido)
        {
            if (forma == FormaCaixa.Dinheiro && (partes[0].ValorRecebido ?? req.ValorRecebido) < total)
                throw Invalido($"Valor menor que o que falta receber (R$ {total:N2}).");
            foreach (var l in linhas)
                await livro.Liquidar(c, tx, loja, usuario, new LiquidarRequest
                {
                    ChaveIdempotencia = $"recebimento:{chave}:{l.LancamentoId}", LancamentoId = l.LancamentoId, ContaId = DestinoDe(forma), DataPagamento = hoje,
                    // A forma vai no fim da descrição: a fita e a conferência da maquininha leem dali (a liquidação não tem dados próprios).
                    Descricao = $"Pedido #{pedido} recebido no caixa · {forma}",
                }, ct);
            await tx.CommitAsync(ct);
            return (await CarregarDia(loja, hoje, ct))!;
        }

        // Dividido: as partes têm de cobrir o pedido inteiro (a sobra pela metade não existe) e cada uma vira o seu recebimento,
        // com a forma na descrição. As partes enchem as linhas do pedido em ordem; o dinheiro com troco só vale na sua parte.
        var soma = CaixaRegras.R2(partes.Sum(p => p.Valor));
        if (soma != total) throw Invalido($"As partes somam R$ {soma:N2}, mas o pedido tem R$ {total:N2} a receber.");
        foreach (var p in partes.Where(p => p.Forma == FormaCaixa.Dinheiro))
            if ((p.ValorRecebido ?? p.Valor) < p.Valor) throw Invalido($"Dinheiro: recebeu menos que a parte de R$ {p.Valor:N2}.");
        var falta = linhas.Select(l => (Linha: l, Resta: CaixaRegras.R2(l.Liquido))).ToList();
        for (var i = 0; i < partes.Count; i++)
        {
            var restaDaParte = partes[i].Valor;
            for (var j = 0; j < falta.Count && restaDaParte > 0; j++)
            {
                if (falta[j].Resta <= 0) continue;
                var parte = CaixaRegras.R2(Math.Min(restaDaParte, falta[j].Resta));
                await livro.Liquidar(c, tx, loja, usuario, new LiquidarParcialRequest
                {
                    ChaveIdempotencia = $"recebimento:{chave}:{falta[j].Linha.LancamentoId}:{partes[i].Forma}", LancamentoId = falta[j].Linha.LancamentoId,
                    ContaId = DestinoDe(partes[i].Forma), DataPagamento = hoje, Valor = parte,
                    Descricao = $"Pedido #{pedido} recebido no caixa · {partes[i].Forma}",
                }, ct);
                falta[j] = (falta[j].Linha, CaixaRegras.R2(falta[j].Resta - parte));
                restaDaParte = CaixaRegras.R2(restaDaParte - parte);
            }
        }
        await tx.CommitAsync(ct);
        return (await CarregarDia(loja, hoje, ct))!;
    }

    // ------------------------------------------------------------------ apoio

    /// <summary>Conta recebível onde o dinheiro das entregas espera o acerto (idempotente pela chave).</summary>
    private async Task GarantirContaMotoboys(NpgsqlConnection c, Guid loja, int usuario, CancellationToken ct)
    {
        // Sem a migração da 6C5 o subtipo não existe no banco: segue a regra antiga (dinheiro da entrega na gaveta).
        if (!await Existe(c, null, "gestao_caixa_acertos")) return;
        var existe = await c.ExecuteScalarAsync<bool>(@"SELECT EXISTS (SELECT 1 FROM gestao_contas WHERE estabelecimento_id = @loja
 AND tipo = 'recebivel' AND subtipo = @subtipo AND ativo)", new { loja, subtipo = VendaDoPedido.SubtipoMotoboys });
        if (!existe)
            await livro.CriarConta(loja, usuario, new ContaRequest
            {
                ChaveIdempotencia = "caixa:motoboys", Tipo = TipoConta.Recebivel, Subtipo = VendaDoPedido.SubtipoMotoboys, Nome = "Dinheiro com motoboys",
            }, ct);
    }

    private async Task<List<EntregaPendente>> Pendentes(NpgsqlConnection c, NpgsqlTransaction? tx, Guid loja, int? motoboy)
    {
        if (!await Existe(c, tx, "gestao_caixa_acerto_itens")) return new();
        var linhas = await c.QueryAsync<LinhaVenda>(SqlVendas + @"
   AND a.dados->>'motoboyId' ~ '^[0-9]+$' AND (CAST(@motoboy AS integer) IS NULL OR (a.dados->>'motoboyId')::int = @motoboy)
   AND NOT EXISTS (SELECT 1 FROM gestao_caixa_acerto_itens i WHERE i.estabelecimento_id = a.estabelecimento_id AND i.pedido_id = a.origem_id::int)
 ORDER BY a.data_fato, s.linha", new { loja, desde = _relogio.GetUtcNow().AddDays(-DiasDePendencia), motoboy }, tx);
        return linhas.GroupBy(l => l.VendaId).Select(g =>
        {
            var f = g.First();
            return new EntregaPendente(int.Parse(f.PedidoTexto, CultureInfo.InvariantCulture), f.VendaId, Utc(f.Em), f.MotoboyId!.Value,
                g.Where(l => l.Liquido != 0).Select(l => new Parte(AcertoRegras.Classificar(l.ContaTipo, l.ContaSubtipo, l.Status),
                    CaixaRegras.R2(l.Liquido), l.Status, l.LancamentoId)).ToList());
        }).ToList();
    }

    /// <summary>Linhas a receber de venda de pedido sem motoboy, sem conta ou na gaveta (forma desconhecida, caixa fechado na hora).</summary>
    private async Task<List<LinhaVenda>> LinhasAReceber(NpgsqlConnection c, NpgsqlTransaction? tx, Guid loja, Guid? gaveta, string? pedido) =>
        (await c.QueryAsync<LinhaVenda>(SqlVendas + @"
   AND a.dados->>'motoboyId' IS NULL AND s.status = 'a_receber' AND (s.conta_id IS NULL OR s.conta_id = @gaveta)
   AND NOT coalesce(a.dados->'pendencias' @> '[""pago_antes_sem_caixa""]'::jsonb, false)
   AND (CAST(@pedido AS text) IS NULL OR a.origem_id = @pedido)
 ORDER BY a.data_fato, s.linha", new { loja, desde = _relogio.GetUtcNow().AddDays(-DiasDePendencia), gaveta, pedido }, tx))
            .Where(l => l.Liquido > 0).ToList();

    /// <summary>Linhas de receita das vendas de pedido não estornadas dos últimos dias, com a conta do livro.</summary>
    private const string SqlVendas = @"
SELECT a.id AS VendaId, a.origem_id AS PedidoTexto, a.data_fato AS Em, a.descricao AS Descricao,
       CASE WHEN a.dados->>'motoboyId' ~ '^[0-9]+$' THEN (a.dados->>'motoboyId')::int END AS MotoboyId, a.dados->>'forma' AS Forma,
       s.id AS LancamentoId,
       s.valor + coalesce((SELECT sum(k.valor) FROM gestao_lancamentos k WHERE k.estabelecimento_id = s.estabelecimento_id
                           AND k.corrige_lancamento_id = s.id), 0) AS Liquido,
       s.status AS Status, g.tipo AS ContaTipo, g.subtipo AS ContaSubtipo
  FROM gestao_acontecimentos a
  JOIN gestao_lancamentos_situacao s ON s.estabelecimento_id = a.estabelecimento_id AND s.acontecimento_id = a.id
       AND s.corrige_lancamento_id IS NULL AND s.natureza = 'receita'
  LEFT JOIN gestao_contas g ON g.estabelecimento_id = s.estabelecimento_id AND g.id = s.conta_id
 WHERE a.estabelecimento_id = @loja AND a.tipo = 'venda' AND a.origem_tipo = 'pedido' AND a.corrige_id IS NULL
   AND a.origem_id ~ '^[0-9]+$' AND a.data_fato >= @desde
   AND NOT EXISTS (SELECT 1 FROM gestao_acontecimentos e WHERE e.estabelecimento_id = a.estabelecimento_id AND e.corrige_id = a.id
                   AND e.correcao = 'estorno')";

    private async Task<List<RotaRow>> EmRota(NpgsqlConnection c, Guid loja)
    {
        if (!await Existe(c, null, "delivery_route_stops")) return new();
        return (await c.QueryAsync<RotaRow>(@"
SELECT rs.motoboy_id AS MotoboyId, rs.pedido_id AS PedidoId, p.value::text AS Valor, p.tipo_pagamento::text AS Forma, rs.assigned_at_utc AS Em
  FROM delivery_route_stops rs
  JOIN pedido p ON p.id = rs.pedido_id AND p.id_estabelecimento = rs.estabelecimento_id
 WHERE rs.estabelecimento_id = @loja AND rs.stop_status IN ('assigned', 'en_route')
 ORDER BY rs.motoboy_id, rs.position", new { loja })).ToList();
    }

    private async Task<List<AcertoDto>> Lotes(NpgsqlConnection c, NpgsqlTransaction? tx, Guid loja, DateOnly? dia, int? motoboy, Guid? id)
    {
        if (!await Existe(c, tx, "gestao_caixa_acertos")) return new();
        var lotes = (await c.QueryAsync<LoteRow>(@"
SELECT l.id, l.numero, l.dia, l.motoboy_id, l.tipo, l.corrige_id, l.esperado, l.entregue, l.diferenca, l.motivo, l.situacao,
       l.pendentes_depois, l.operador, u.nome::text AS operador_nome, l.em,
       (SELECT x.id FROM gestao_caixa_acertos x WHERE x.estabelecimento_id = l.estabelecimento_id AND x.corrige_id = l.id LIMIT 1) AS corrigido_por
  FROM gestao_caixa_acertos l
  LEFT JOIN usuario u ON u.id = l.operador
 WHERE l.estabelecimento_id = @loja AND (CAST(@dia AS date) IS NULL OR l.dia = @dia)
   AND (CAST(@motoboy AS integer) IS NULL OR l.motoboy_id = @motoboy) AND (CAST(@id AS uuid) IS NULL OR l.id = @id)
 ORDER BY l.em, l.numero", new { loja, dia = dia?.ToDateTime(TimeOnly.MinValue), motoboy, id }, tx)).ToList();
        if (lotes.Count == 0) return new();
        var itens = (await c.QueryAsync<ItemRow>(@"
SELECT acerto_id, pedido_id, dinheiro, digital_integracao, cartao_manual, ifood, conferido_manual
  FROM gestao_caixa_acerto_itens WHERE estabelecimento_id = @loja AND acerto_id = ANY(@ids) ORDER BY pedido_id",
            new { loja, ids = lotes.Select(l => l.Id).ToArray() }, tx)).ToLookup(i => i.AcertoId);
        var nomes = await NomesMotoboys(c, tx, lotes.Select(l => l.MotoboyId).Distinct().ToArray());
        return lotes.Select(l => new AcertoDto(l.Id, AcertoRegras.Numero(l.Numero), DateOnly.FromDateTime(l.Dia), l.MotoboyId,
            nomes.GetValueOrDefault(l.MotoboyId), l.Tipo, l.CorrigeId, l.Esperado, l.Entregue, l.Diferenca, l.Motivo, l.Situacao, l.PendentesDepois,
            l.Operador, l.OperadorNome, Utc(l.Em),
            itens[l.Id].Select(i => new ItemAcertoDto(i.PedidoId, i.Dinheiro, i.DigitalIntegracao, i.CartaoManual, i.Ifood, i.ConferidoManual)).ToList(),
            l.CorrigidoPor)).ToList();
    }

    private static async Task<(Guid Id, string Hash)?> LotePorChave(NpgsqlConnection c, NpgsqlTransaction? tx, Guid loja, string chave) =>
        await c.QuerySingleOrDefaultAsync<(Guid Id, string Hash)?>(
            "SELECT id, hash_conteudo FROM gestao_caixa_acertos WHERE estabelecimento_id = @loja AND chave = @chave", new { loja, chave }, tx);

    /// <summary>Mesma chave e mesmo conteúdo devolve o lote gravado; outro conteúdo é recusado.</summary>
    private async Task<AcertoDto> Replay(NpgsqlConnection c, Guid loja, (Guid Id, string Hash) gravado, string hash)
    {
        if (gravado.Hash != hash) throw new GestaoException("IDEMPOTENCIA_CONFLITO", "Esta chave já foi usada com outro acerto.", 409);
        return (await Lotes(c, null, loja, null, null, gravado.Id)).Single();
    }

    /// <summary>Falta vira despesa (quebra de caixa) paga na gaveta; sobra vira receita (sobra de caixa) na gaveta — como no fechamento.</summary>
    private async Task<Guid> PublicarDiferenca(NpgsqlConnection c, NpgsqlTransaction tx, Guid loja, int usuario, GestaoPapeis.Acesso acesso,
        Guid gaveta, DateOnly hoje, string chave, Guid acerto, string rotulo, string nome, int motoboyId, decimal diferenca, string motivo, string titulo)
    {
        var pedido = new PublicarAcontecimentoRequest
        {
            ChaveIdempotencia = chave, Tipo = TipoDiferencaAcerto, Modulo = ModuloGestao.Caixa, DataFato = _relogio.GetUtcNow(),
            Descricao = Cortar($"{titulo} {rotulo} · {nome} · {(diferenca < 0 ? "faltou" : "sobrou")} R$ {Math.Abs(diferenca):N2} · {motivo}", 300),
            Origem = new OrigemRef { Tipo = "acerto_motoboy", Id = acerto.ToString() },
            Dados = JsonSerializer.SerializeToElement(new { acerto, numero = rotulo, motoboyId, diferenca, motivo }, Json),
            Lancamentos = diferenca < 0
                ? new() { Saida(Natureza.Despesa, -diferenca, gaveta, hoje, "quebra_de_caixa", "Falta no acerto do motoboy") }
                : new() { new LancamentoRequest { Natureza = Natureza.Receita, Valor = diferenca, ContaId = gaveta, DataPagamento = hoje,
                    Categoria = "sobra_de_caixa", Descricao = "Sobra no acerto do motoboy" } },
        };
        GestaoPapeis.ValidarPublicacao(acesso, pedido, hoje, hoje);
        return (await livro.Publicar(c, tx, loja, usuario, pedido, ct: default)).Id;
    }

    private Task InserirLote(NpgsqlConnection c, NpgsqlTransaction tx, Guid id, Guid loja, int numero, string chave, string hash, DateOnly dia,
        int motoboy, string tipo, Guid? corrige, decimal esperado, decimal entregue, decimal diferenca, string? motivo, string situacao,
        int pendentesDepois, Guid? diferencaId, int usuario) =>
        c.ExecuteAsync(@"
INSERT INTO gestao_caixa_acertos (id, estabelecimento_id, numero, chave, hash_conteudo, dia, motoboy_id, tipo, corrige_id, esperado, entregue,
       diferenca, motivo, situacao, pendentes_depois, diferenca_id, operador, em)
VALUES (@id, @loja, @numero, @chave, @hash, @dia, @motoboy, @tipo, @corrige, @esperado, @entregue, @diferenca, @motivo, @situacao,
        @pendentesDepois, @diferencaId, @usuario, @agora)", new
        {
            id, loja, numero, chave, hash, dia = dia.ToDateTime(TimeOnly.MinValue), motoboy, tipo, corrige, esperado, entregue, diferenca, motivo,
            situacao, pendentesDepois, diferencaId, usuario, agora = _relogio.GetUtcNow(),
        }, tx);

    public const string EventoConferencia = "rider.conference.updated";

    /// <summary>
    /// 6C6: avisa o app do motoboy (sessões abertas dele nesta loja) pelo outbox do tempo real de entregas: só invalidação, sem valores,
    /// e só no commit do lote. O app relê a conferência pela rota autorizada; sem conexão, relê ao abrir o histórico.
    /// </summary>
    private static async Task AvisarMotoboy(NpgsqlConnection c, NpgsqlTransaction tx, Guid loja, int motoboyId, Guid lote, int[] pedidos)
    {
        if (!await Existe(c, tx, "delivery_realtime_outbox") || !await Existe(c, tx, "motoboy_active_sessions")) return;
        var payload = JsonSerializer.Serialize(new { schemaVersion = 1, estabelecimentoId = loja, motoboyId, loteId = lote, pedidoIds = pedidos }, Json);
        await c.ExecuteAsync(@"
INSERT INTO delivery_realtime_outbox (event_id, event_name, target_group, estabelecimento_id, motoboy_id, aggregate_version, payload, occurred_at_utc)
SELECT gen_random_uuid(), @evento, 'delivery-session:' || replace(s.session_id::text, '-', ''), @loja, @motoboyId, 0, CAST(@payload AS jsonb), now()
  FROM motoboy_active_sessions s
 WHERE s.motoboy_id = @motoboyId AND s.id_estabelecimento = @loja AND s.ended_at_utc IS NULL AND s.revoked_at IS NULL",
            new { evento = EventoConferencia, loja, motoboyId, payload }, tx);
    }

    private static async Task<Dictionary<int, string>> NomesMotoboys(NpgsqlConnection c, NpgsqlTransaction? tx, int[] ids)
    {
        if (ids.Length == 0 || !await Existe(c, tx, "motoboy")) return new();
        return (await c.QueryAsync<(int Id, string? Nome)>("SELECT id, nome::text FROM motoboy WHERE id = ANY(@ids)", new { ids }, tx))
            .Where(x => !string.IsNullOrWhiteSpace(x.Nome)).ToDictionary(x => x.Id, x => x.Nome!.Trim());
    }

    private static async Task<Dictionary<int, string>> Clientes(NpgsqlConnection c, Guid loja, int[] pedidos)
    {
        if (pedidos.Length == 0) return new();
        return (await c.QueryAsync<(int Id, string? Nome)>(@"SELECT p.id, nullif(btrim(to_jsonb(p)->>'nome_cliente'), '')
  FROM pedido p WHERE p.id_estabelecimento = @loja AND p.id = ANY(@pedidos)", new { loja, pedidos }))
            .Where(x => x.Nome is not null).ToDictionary(x => x.Id, x => x.Nome!);
    }

    private static Task<bool> Existe(NpgsqlConnection c, NpgsqlTransaction? tx, string tabela) =>
        c.ExecuteScalarAsync<bool>("SELECT to_regclass(@tabela) IS NOT NULL", new { tabela }, tx);

    /// <summary>Forma do pedido ainda em rota, pelo texto gravado (mesma regra da venda).</summary>
    private static string PartePrevista(string? texto) => VendaRegras.Forma(texto).Forma switch
    {
        VendaRegras.Din => ParteAcerto.Dinheiro,
        VendaRegras.Pix => ParteAcerto.Integracao,
        VendaRegras.Deb or VendaRegras.Cre => ParteAcerto.CartaoManual,
        _ => ParteAcerto.Outro,
    };

    private static decimal Valor(string? texto) =>
        decimal.TryParse((texto ?? "").Trim().Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? CaixaRegras.R2(v) : 0m;
}
