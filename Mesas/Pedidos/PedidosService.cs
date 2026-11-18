using System.Text.Json;
using System.Text.Json.Nodes;
using APIBack.Mesas.Nucleo;
using APIBack.Mesas.Sessao;
using Dapper;
using Npgsql;
using R = APIBack.Mesas.Pedidos.PedidosRegras;

namespace APIBack.Mesas.Pedidos;

/// <summary>
/// Pedidos, itens, preparo e entrega da mesa (P08, CONTRATO-API §3.5). Todo comando passa pelo <see cref="MesasComandos"/> (idempotência,
/// <c>mesa_evento</c>, avisos depois do commit), trava as mesas da sessão antes dos itens (ordem fixa: mesas por id, depois itens por id),
/// grava cada passo do item em <c>mesa_item_evento</c> e sobe a versão das mesas tocadas. Autonomia do garçom pelo
/// <see cref="MesasOperacao.Autonomia"/> (P07): "pede o gerente" grava a solicitação e devolve <c>AGUARDANDO_GERENTE</c> sem travar nada.
/// </summary>
public sealed class PedidosService(NpgsqlDataSource source, MesasComandos comandos, ICozinhaTempoReal cozinhaRt) : IMesasAcaoExecutor
{
    // ---------------------------------------------------------------- linhas

    internal sealed class ItemRow
    {
        public Guid Id { get; set; }
        public Guid PedidoId { get; set; }
        public Guid SessaoId { get; set; }
        public Guid MesaId { get; set; }
        public Guid ProdutoId { get; set; }
        public string Nome { get; set; } = "";
        public string TipoPrato { get; set; } = "outro";
        public decimal Qtd { get; set; }
        public decimal Preco { get; set; }
        public string EstacaoId { get; set; } = R.ProducaoGeral;
        public string EtapasJson { get; set; } = "[]";
        public short EtapaAtual { get; set; }
        public string Estado { get; set; } = "";
        public string? Obs { get; set; }
        public string? QuemLeva { get; set; }
        public bool QuerBuscar { get; set; }
        public Guid? LevaGarcomId { get; set; }
        public DateTime? AvisadoEm { get; set; }
        public DateTime? PegouEm { get; set; }
        public string? PegouPor { get; set; }
        public string? PegouPorNome { get; set; }
        public Guid? PreparoPor { get; set; }
        public string? RetiradaEstacao { get; set; }
        public DateTime? RetiradaDesde { get; set; }
        public DateTime? RetiradoEm { get; set; }
        public string? RetiradoPor { get; set; }
        public string? CortesiaJson { get; set; }
        public short Refazer { get; set; }
        public bool EntregaPresumida { get; set; }
        public bool EntregaDireta { get; set; }
        public string? AprovadoPor { get; set; }
        public string? Motivo { get; set; }
        public Guid? MovidoDe { get; set; }
        public DateTime? TFila { get; set; }
        public DateTime? TPreparo { get; set; }
        public DateTime? TPronto { get; set; }
        public DateTime? TEntregue { get; set; }
        public DateTime? AtrasoAvisadoEm { get; set; }
        public string SessaoEstado { get; set; } = "";
        public Guid? SessaoGarcomId { get; set; }

        public string Rotulo => R.Qtd(Qtd) + "× " + Nome;

        public ItemPasso Passo() => new()
        {
            Estado = Estado, EtapaAtual = EtapaAtual, EstacaoId = EstacaoId, QuemLeva = QuemLeva, RetiradaEstacao = RetiradaEstacao,
            Etapas = JsonSerializer.Deserialize<List<Etapa>>(EtapasJson, MesasComandos.Json) ?? new()
        };

        public void Aplicar(ItemPasso p)
        {
            Estado = p.Estado; EtapaAtual = p.EtapaAtual; EstacaoId = p.EstacaoId; QuemLeva = p.QuemLeva; RetiradaEstacao = p.RetiradaEstacao;
            EtapasJson = JsonSerializer.Serialize(p.Etapas, MesasComandos.Json);
        }
    }

    private const string ItemSelect = """
        SELECT i.id, i.pedido_id, i.sessao_id, i.mesa_id, i.produto_id, i.nome, i.tipo_prato, i.qtd, i.preco, i.estacao_id,
               i.etapas::text AS etapas_json, i.etapa_atual, i.estado, i.obs, i.quem_leva, i.quer_buscar, i.leva_garcom_id, i.avisado_em,
               i.pegou_em, i.pegou_por, i.pegou_por_nome, i.preparo_por, i.retirada_estacao, i.retirada_desde, i.retirado_em, i.retirado_por,
               i.cortesia::text AS cortesia_json, i.refazer, i.entrega_presumida, i.entrega_direta, i.aprovado_por, i.motivo, i.movido_de,
               i.t_fila, i.t_preparo, i.t_pronto, i.t_entregue, i.atraso_avisado_em, s.estado AS sessao_estado, s.garcom_id AS sessao_garcom_id
          FROM mesa_item i JOIN mesa_sessao s ON s.id = i.sessao_id
        """;

    private sealed class PedidoRow
    {
        public Guid Id { get; set; }
        public Guid SessaoId { get; set; }
        public Guid MesaId { get; set; }
        public string Estado { get; set; } = "";
        public bool SegurarPrincipal { get; set; }
        public DateTime? SoltoEm { get; set; }
        public string? SoltoPor { get; set; }
        public DateTime? VistoEm { get; set; }
        public string? VistoPor { get; set; }
        public Guid? EscaladoPara { get; set; }
        public Guid? GarcomId { get; set; }
    }

    private sealed class ProdutoRow
    {
        public Guid Id { get; set; }
        public string Nome { get; set; } = "";
        public decimal PrecoBase { get; set; }
        public string? ImagemUrl { get; set; }
        public Guid CategoriaId { get; set; }
        public string? CategoriaSlug { get; set; }
        public bool Ativo { get; set; }
        public bool Disponivel { get; set; }
        public DateTime? DeletedAt { get; set; }
    }

    private sealed class ComandaRow { public Guid Id { get; set; } public string Tipo { get; set; } = ""; public bool Aberta { get; set; } }
    private sealed class QuemRow { public string? Nome { get; set; } public DateTime Quando { get; set; } }

    /// <summary>Itens travados de um comando, com as mesas da(s) sessão(ões) já travadas.</summary>
    private sealed record Lote(IReadOnlyList<MesaTravada> Mesas, List<ItemRow> Itens);

    // ---------------------------------------------------------------- lançar

    public Task<PedidoLancadoDto> LancarAsync(MesasContexto ctx, LancarPedidoRequest req, CancellationToken ct, string? origemForcada = null) =>
        comandos.Executar(ctx, "lancarPedido", req.Chave, req, async op =>
        {
            if (origemForcada is null) R.Exigir(ctx.Papel, "WGD");
            var origem = origemForcada ?? Origem(ctx.Papel, req.Origem);
            var mesaId = req.MesaId ?? throw MesasException.Validacao("Escolha a mesa.");
            await op.ExigirPontoSeGarcom();
            var r = await LancarCore(op, mesaId, req.Itens, req.Rascunho == true, req.SegurarPrincipal == true, origem, req.PessoaId, null);
            return new PedidoLancadoDto(r.EventoId, op.Versoes, r.PedidoId, r.Pendentes, r.ItemIds);
        }, ct);

    public Task<RodadaDto> RepetirRodadaAsync(MesasContexto ctx, RepetirRodadaRequest req, CancellationToken ct) =>
        comandos.Executar(ctx, "repetirRodada", req.Chave, req, async op =>
        {
            R.Exigir(ctx.Papel, "WGD");
            var mesaId = req.MesaId ?? throw MesasException.Validacao("Escolha a mesa.");
            await op.ExigirPontoSeGarcom();
            var sessaoId = await SessaoDaMesa(op, mesaId) ?? throw EstadoInvalido("A mesa não está aberta.");
            var itens = req.Itens;
            Guid? anterior = null;
            if (itens is null || itens.Count == 0)
            {
                anterior = await op.C.QueryFirstOrDefaultAsync<Guid?>(op.Sql("""
                    SELECT p.id FROM mesa_pedido p
                     WHERE p.estabelecimento_id = @loja AND p.sessao_id = @sessaoId AND p.estado IN ('enviado', 'aguardando_aprovacao')
                       AND EXISTS (SELECT 1 FROM mesa_item i
                                    WHERE i.pedido_id = p.id AND i.estado <> 'cancelado'
                                      AND (CAST(@pessoaId AS uuid) IS NULL OR EXISTS (
                                          SELECT 1 FROM mesa_item_parte ip JOIN mesa_comanda c ON c.id = ip.comanda_id
                                           WHERE ip.item_id = i.id AND c.pessoa_id = @pessoaId)))
                     ORDER BY p.criado_em DESC LIMIT 1
                    """, new { loja = ctx.Loja, sessaoId, pessoaId = req.PessoaId }))
                    ?? throw EstadoInvalido("Ainda não há pedido para repetir.");
                var linhas = await op.C.QueryAsync<(Guid ProdutoId, decimal Qtd, string? Obs, Guid ItemId)>(op.Sql("""
                    SELECT i.produto_id, i.qtd, i.obs, i.id FROM mesa_item i
                     WHERE i.pedido_id = @anterior AND i.estado <> 'cancelado' AND i.refazer = 0
                       AND (CAST(@pessoaId AS uuid) IS NULL OR EXISTS (
                           SELECT 1 FROM mesa_item_parte ip JOIN mesa_comanda c ON c.id = ip.comanda_id WHERE ip.item_id = i.id AND c.pessoa_id = @pessoaId))
                     ORDER BY i.criado_em, i.id
                    """, new { anterior, pessoaId = req.PessoaId }));
                var novos = new List<ItemNovo>();
                foreach (var l in linhas)
                {
                    var cmds = (await op.C.QueryAsync<Guid>(op.Sql("""
                        SELECT ip.comanda_id FROM mesa_item_parte ip JOIN mesa_comanda c ON c.id = ip.comanda_id WHERE ip.item_id = @id AND c.aberta
                        """, new { id = l.ItemId }))).ToList();
                    novos.Add(new ItemNovo(l.ProdutoId, l.Qtd, l.Obs?.StartsWith("↻") == true ? null : l.Obs, null, null, cmds.Count > 0 ? cmds : null, null, null));
                }
                itens = novos;
            }
            var origem = Origem(ctx.Papel, null);
            var r = await LancarCore(op, mesaId, itens, false, false, origem, req.PessoaId, anterior);
            return new RodadaDto(r.EventoId, op.Versoes, r.PedidoId, r.ItemIds.Count, r.ItemIds);
        }, ct);

    private sealed record Lancado(Guid EventoId, Guid PedidoId, int Pendentes, List<Guid> ItemIds);

    private async Task<Lancado> LancarCore(MesasOperacao op, Guid mesaId, IReadOnlyList<ItemNovo>? itens, bool rascunho, bool segurar, string origem,
        Guid? pessoaId, Guid? rodadaDe)
    {
        if (itens is null || itens.Count == 0) throw MesasException.Validacao("O pedido está vazio.");
        if (itens.Count > 60) throw MesasException.Validacao("Pedido grande demais. Divida em partes.");
        var loja = op.Ctx.Loja;
        var sessaoId = await SessaoDaMesa(op, mesaId) ?? throw EstadoInvalido("Abra a mesa antes de lançar.");
        var mesas = await op.TravarSessao(sessaoId);
        var m = mesas.FirstOrDefault(x => x.MesaId == mesaId) ?? throw EstadoInvalido("A mesa mudou. Confira de novo.");
        if (m.SessaoId != sessaoId || m.Status is "livre" or "aguardando_limpeza" or "indisponivel" or "reservada")
            throw EstadoInvalido($"A {m.Rotulo} não está aberta.");
        var cfg = await op.Config();
        if (origem == "cliente" && MesasConfigRegras.Ler(cfg, "pedidoPeloCelular") is JsonValue pc && pc.TryGetValue<bool>(out var celular) && !celular)
            throw MesasException.Validacao("Esta casa não aceita pedido pelo celular.");

        var ids = itens.Select(i => i.ProdutoId ?? throw MesasException.Validacao("Escolha o produto.")).Distinct().ToArray();
        var produtos = (await op.C.QueryAsync<ProdutoRow>(op.Sql("""
            SELECT p.id, p.nome, p.preco_base, p.imagem_url, p.categoria_id, c.slug AS categoria_slug, p.ativo, p.disponivel, p.deleted_at
              FROM cardapio_produto p JOIN cardapio_categoria c ON c.id = p.categoria_id
             WHERE p.id_estabelecimento = @loja AND p.id = ANY(@ids)
            """, new { loja, ids }))).ToDictionary(p => p.Id);
        var esgotados = (MesasConfigRegras.Ler(cfg, "esgotados") as JsonArray)?.Select(R.Texto).Where(x => x is not null).ToHashSet() ?? new();
        foreach (var id in ids)
        {
            if (!produtos.TryGetValue(id, out var p)) throw MesasException.Validacao("Produto não encontrado no cardápio.");
            if (!p.Ativo || !p.Disponivel || p.DeletedAt is not null || esgotados.Contains(id.ToString()))
                throw MesasException.Validacao($"{p.Nome} está esgotado.");
        }

        var comandas = (await op.C.QueryAsync<ComandaRow>(op.Sql(
            "SELECT id, tipo, aberta FROM mesa_comanda WHERE sessao_id = @sessaoId", new { sessaoId }))).ToList();
        var coletiva = comandas.FirstOrDefault(c => c.Tipo == "coletiva" && c.Aberta)?.Id
            ?? comandas.FirstOrDefault(c => c.Tipo == "coletiva")?.Id ?? throw EstadoInvalido("A mesa ainda não tem conta.");

        var pedidoId = Guid.NewGuid();
        await op.C.ExecuteAsync(op.Sql("""
            INSERT INTO mesa_pedido (id, estabelecimento_id, sessao_id, mesa_id, origem, ator_id, ator_nome, pessoa_id, estado, segurar_principal,
                                     rodada_de, criado_em, atualizado_em)
            VALUES (@pedidoId, @loja, @sessaoId, @mesaId, @origem, @ator, @nome, @pessoaId, 'rascunho', @segurar, @rodadaDe, @agora, @agora)
            """, new { pedidoId, loja, sessaoId, mesaId, origem, ator = op.Ctx.Usuario.ToString(), nome = op.AtorNome, pessoaId, segurar, rodadaDe,
            agora = op.Agora }));

        var itemIds = new List<Guid>();
        var estacoes = new HashSet<string>();
        var pendentes = 0;
        foreach (var x in itens)
        {
            var p = produtos[x.ProdutoId!.Value];
            var qtd = x.Qtd ?? 1;
            if (qtd <= 0 || qtd > 999) throw MesasException.Validacao("Quantidade inválida.");
            var obs = Texto(x.Obs, 200, "Observação");
            var alergia = Texto(x.Alergia, 200, "Alergia");
            var cids = (x.ComandaIds is { Count: > 0 } l ? l : x.ComandaId is Guid one ? new[] { one } : new[] { coletiva }).Distinct().ToList();
            foreach (var cid in cids)
            {
                var c = comandas.FirstOrDefault(k => k.Id == cid) ?? throw MesasException.Validacao("Essa conta não é desta mesa.");
                if (!c.Aberta) throw MesasException.Validacao("Essa conta já foi fechada.");
            }
            var etapas = R.Rotear(cfg, p.Id, p.CategoriaId);
            var estacao = etapas[0].EstacaoId;
            var aprov = R.RegraAprovacao(cfg, origem, estacao, p.CategoriaId, p.Id, p.PrecoBase);
            var estado = rascunho ? "rascunho" : aprov.Exige ? "aguardando_aprovacao" : "em_fila";
            if (estado == "aguardando_aprovacao") pendentes++;
            if (estado == "em_fila") estacoes.Add(estacao);
            var querBuscar = x.Buscar == true;
            var quemLeva = estado == "em_fila" ? R.QuemLeva(cfg, p.Id, etapas[^1].EstacaoId, querBuscar) : null;
            var itemId = Guid.NewGuid();
            await op.C.ExecuteAsync(op.Sql("""
                INSERT INTO mesa_item (id, estabelecimento_id, pedido_id, sessao_id, mesa_id, produto_id, nome, foto_url, tipo_prato, qtd, preco,
                                       estacao_id, etapas, etapa_atual, estado, origem, obs, alergia, regra_aprovacao, quem_leva, quer_buscar, extra,
                                       criado_em, t_fila)
                VALUES (@itemId, @loja, @pedidoId, @sessaoId, @mesaId, @produtoId, @nome, @foto, @tipo, @qtd, @preco, @estacao,
                        CAST(@etapas AS jsonb), 0, @estado, @origem, @obs, @alergia, @regra, @quemLeva, @querBuscar, @extra, @agora,
                        CASE WHEN @estado = 'em_fila' THEN CAST(@agora AS timestamptz) END)
                """, new
            {
                itemId, loja, pedidoId, sessaoId, mesaId, produtoId = p.Id, nome = Cortar(p.Nome, 120), foto = p.ImagemUrl, tipo = R.TipoPrato(p.CategoriaSlug),
                qtd, preco = p.PrecoBase, estacao, etapas = JsonSerializer.Serialize(etapas, MesasComandos.Json), estado, origem, obs, alergia,
                regra = aprov.Exige ? aprov.Rotulo : null, quemLeva, querBuscar, extra = x.Extra == true, agora = op.Agora
            }));
            await GravarPartes(op, itemId, p.PrecoBase * qtd, cids);
            await ItemEvento(op, itemId, "lancarPedido", null, estado, null, 0);
            itemIds.Add(itemId);
        }

        await AtualizarPedidos(op, new[] { pedidoId }, "cancelado");
        await op.C.ExecuteAsync(op.Sql("""
            UPDATE mesa_sessao SET ultimo_pedido_em = @agora, conta_pedida_em = CASE WHEN @reabre THEN NULL ELSE conta_pedida_em END WHERE id = @sessaoId
            """, new { agora = op.Agora, sessaoId, reabre = !rascunho }));
        if (!rascunho && m.Status is "ocupada" or "aguardando_pagamento")
            foreach (var x in mesas) await op.Mudar(x, "em_atendimento", sessaoId);
        else
            await op.TocarTodas(mesas);

        var depois = $"{itemIds.Count} {(itemIds.Count == 1 ? "item" : "itens")}" + (pendentes > 0 ? $", {pendentes} para aprovar" : "") + (rascunho ? " (rascunho)" : "");
        var eventoId = await op.Evento(rodadaDe is null ? "lancarPedido" : "repetirRodada", "pedido", pedidoId, "Pedido da " + m.Rotulo,
            depois: depois, mesaId: mesaId, sessaoId: sessaoId);
        var garcom = await GarcomDaSessao(op, sessaoId);
        if (estacoes.Count > 0) AvisarCozinha(op, estacoes, pedidoId);
        if (pendentes > 0)
        {
            op.Avisar(t => t.Aviso(loja, "pedido", pedidoId, mesaId, garcom));
            op.Avisar(t => t.PendenciasMudou(loja, "aprovacao", garcom));
        }
        return new Lancado(eventoId, pedidoId, pendentes, itemIds);
    }

    // ---------------------------------------------------------------- produção e aprovação

    public Task<ItensDto> EnviarProducaoAsync(MesasContexto ctx, ItensRequest req, CancellationToken ct) =>
        comandos.Executar(ctx, "enviarProducao", req.Chave, req, async op =>
        {
            R.Exigir(ctx.Papel, "WGD");
            await op.ExigirPontoSeGarcom();
            var lote = await Travar(op, await Escolher(op, req));
            ExigirAberta(lote);
            var its = lote.Itens.Where(i => i.Estado == "rascunho").ToList();
            if (its.Count == 0) throw EstadoInvalido("Nada para enviar.");
            var cfg = await op.Config();
            var est = new HashSet<string>();
            var pend = 0;
            foreach (var it in its)
            {
                var cat = await CategoriaDo(op, it.ProdutoId);
                var aprov = R.RegraAprovacao(cfg, await OrigemDoItem(op, it.Id), it.EstacaoId, cat, it.ProdutoId, it.Preco);
                var de = it.Estado;
                if (aprov.Exige) { it.Estado = "aguardando_aprovacao"; pend++; }
                else { EntrarNaFila(it, cfg, op.Agora); est.Add(it.EstacaoId); }
                await Salvar(op, it);
                await ItemEvento(op, it.Id, "enviarProducao", de, it.Estado, it.EtapaAtual, it.EtapaAtual);
            }
            await AtualizarPedidos(op, its.Select(i => i.PedidoId).Distinct(), "cancelado");
            await MudarMesaSeOcupada(op, lote.Mesas);
            var ev = await op.Evento("enviarProducao", "pedido", its[0].PedidoId, $"{its.Count} {(its.Count == 1 ? "item" : "itens")}",
                depois: pend > 0 ? $"{pend} para aprovar" : "Na produção", mesaId: its[0].MesaId, sessaoId: its[0].SessaoId);
            AvisarCozinha(op, est, its[0].PedidoId);
            if (pend > 0) { var loja = ctx.Loja; var g = its[0].SessaoGarcomId; op.Avisar(t => t.PendenciasMudou(loja, "aprovacao", g)); }
            return new ItensDto(ev, op.Versoes, its.Count, its.Select(i => i.Id).ToList());
        }, ct);

    public Task<ItensDto> AprovarAsync(MesasContexto ctx, ItensRequest req, CancellationToken ct) =>
        comandos.Executar(ctx, "aprovarPedido", req.Chave, req, async op =>
        {
            R.Exigir(ctx.Papel, "WGD");
            await op.ExigirPontoSeGarcom();
            var lote = await Travar(op, await Escolher(op, req));
            var its = lote.Itens.Where(i => i.Estado == "aguardando_aprovacao").ToList();
            if (its.Count == 0) throw await JaResolvido(op, lote.Itens);
            var cfg = await op.Config();
            foreach (var it in its)
            {
                it.AprovadoPor = op.AtorNome;
                EntrarNaFila(it, cfg, op.Agora);
                await Salvar(op, it);
                await ItemEvento(op, it.Id, "aprovarPedido", "aguardando_aprovacao", "em_fila", it.EtapaAtual, it.EtapaAtual);
            }
            var pedidos = its.Select(i => i.PedidoId).Distinct().ToArray();
            await AtualizarPedidos(op, pedidos, "cancelado", visto: true);
            await MudarMesaSeOcupada(op, lote.Mesas);
            var ev = await op.Evento("aprovarPedido", "pedido", pedidos[0], "Pedido da " + Rotulo(lote, its[0].MesaId),
                depois: $"{its.Count} {(its.Count == 1 ? "item" : "itens")} para a produção", mesaId: its[0].MesaId, sessaoId: its[0].SessaoId);
            AvisarCozinha(op, its.Select(i => i.EstacaoId), pedidos[0]);
            var loja = ctx.Loja; var g = its[0].SessaoGarcomId;
            op.Avisar(t => t.PendenciasMudou(loja, "aprovacao", g));
            return new ItensDto(ev, op.Versoes, its.Count, its.Select(i => i.Id).ToList());
        }, ct);

    public Task<ItensDto> RecusarAsync(MesasContexto ctx, ItensRequest req, CancellationToken ct) =>
        comandos.Executar(ctx, "recusarPedido", req.Chave, req, async op =>
        {
            R.Exigir(ctx.Papel, "WGD");
            var motivo = Motivo(req.Motivo);
            await op.ExigirPontoSeGarcom();
            var lote = await Travar(op, await Escolher(op, req));
            var its = lote.Itens.Where(i => i.Estado == "aguardando_aprovacao").ToList();
            if (its.Count == 0) throw await JaResolvido(op, lote.Itens);
            foreach (var it in its)
            {
                it.Estado = "cancelado"; it.Motivo = motivo;
                await Salvar(op, it);
                await ItemEvento(op, it.Id, "recusarPedido", "aguardando_aprovacao", "cancelado", it.EtapaAtual, it.EtapaAtual);
            }
            var pedidos = its.Select(i => i.PedidoId).Distinct().ToArray();
            await AtualizarPedidos(op, pedidos, "recusado", visto: true);
            await op.TocarTodas(lote.Mesas);
            var ev = await op.Evento("recusarPedido", "pedido", pedidos[0], "Pedido da " + Rotulo(lote, its[0].MesaId),
                depois: $"{its.Count} {(its.Count == 1 ? "item recusado" : "itens recusados")}", motivo: motivo, mesaId: its[0].MesaId, sessaoId: its[0].SessaoId);
            var loja = ctx.Loja; var g = its[0].SessaoGarcomId;
            op.Avisar(t => t.PendenciasMudou(loja, "aprovacao", g));
            return new ItensDto(ev, op.Versoes, its.Count, its.Select(i => i.Id).ToList());
        }, ct);

    public Task<PedidoComandoDto> EscalarAsync(MesasContexto ctx, PedidoRequest req, CancellationToken ct) =>
        comandos.Executar(ctx, "escalarAprovacao", req.Chave, req, async op =>
        {
            R.Exigir(ctx.Papel, "WG");
            var (mesas, ped) = await TravarPedido(op, req.PedidoId);
            if (ped.Estado != "aguardando_aprovacao") throw EstadoInvalido("Nada para escalar.");
            var para = req.ParaId ?? await QuemAprova(op);
            await op.C.ExecuteAsync(op.Sql("UPDATE mesa_pedido SET escalado_para = @para, escalado_em = @agora, atualizado_em = @agora WHERE id = @id",
                new { para, agora = op.Agora, id = ped.Id }));
            await op.TocarTodas(mesas);
            var nome = para is Guid p ? await NomePessoa(op, p) : null;
            var ev = await op.Evento("escalarAprovacao", "pedido", ped.Id, "Pedido da " + RotuloMesa(mesas, ped.MesaId),
                depois: "Encaminhado para " + (nome ?? "o gerente"), mesaId: ped.MesaId, sessaoId: ped.SessaoId);
            var loja = ctx.Loja;
            op.Avisar(t => t.Aviso(loja, "pedido", ped.Id, ped.MesaId, para));
            op.Avisar(t => t.PendenciasMudou(loja, "aprovacao", para));
            return new PedidoComandoDto(ev, op.Versoes, ped.Id);
        }, ct);

    public Task<ItensDto> ReconhecerAsync(MesasContexto ctx, ReconhecerRequest req, CancellationToken ct) =>
        comandos.Executar(ctx, "reconhecerPedido", req.Chave, req, async op =>
        {
            R.Exigir(ctx.Papel, "WGDX");
            var ids = (req.PedidoIds ?? Array.Empty<Guid>()).Concat(req.PedidoId is Guid p ? new[] { p } : Array.Empty<Guid>()).Distinct().ToArray();
            if (ids.Length == 0) throw MesasException.Validacao("Escolha o pedido.");
            var vistos = 0; Guid? eventoMesa = null; Guid primeiro = ids[0];
            var tocadas = new List<MesaTravada>();
            foreach (var id in ids)
            {
                var (mesas, ped) = await TravarPedido(op, id);
                if (ped.VistoEm is not null) continue;
                await op.C.ExecuteAsync(op.Sql("UPDATE mesa_pedido SET visto_em = @agora, visto_por = @nome WHERE id = @id",
                    new { agora = op.Agora, nome = op.AtorNome, id }));
                vistos++; eventoMesa ??= ped.MesaId; tocadas.AddRange(mesas);
            }
            if (vistos == 0)
            {
                var q = await op.C.QueryFirstAsync<QuemRow>(op.Sql("SELECT visto_por AS nome, visto_em AS quando FROM mesa_pedido WHERE id = @id", new { id = primeiro }));
                throw JaFeito($"Já visto por {Primeiro(q.Nome)} às {Hora(q.Quando)}.", q.Nome, q.Quando);
            }
            await op.TocarTodas(tocadas.DistinctBy(m => m.MesaId));
            var ev = await op.Evento("reconhecerPedido", "pedido", primeiro, vistos == 1 ? "Pedido" : $"{vistos} pedidos", depois: "Ciente", mesaId: eventoMesa);
            return new ItensDto(ev, op.Versoes, vistos, ids);
        }, ct);

    // ---------------------------------------------------------------- cozinha

    public Task<ItemEstadoDto> AvancarEtapaAsync(MesasContexto ctx, ItemRequest req, CancellationToken ct) =>
        PassoCozinha(ctx, req, "avancarEtapa", (it, cfg) =>
        {
            if (it.Estado == "em_fila") { it.Estado = "em_preparo"; return "Começou"; }
            return PassoPronto(it, cfg);
        }, ct);

    public Task<ItemEstadoDto> MarcarItemProntoAsync(MesasContexto ctx, ItemRequest req, CancellationToken ct) =>
        PassoCozinha(ctx, req, "marcarItemPronto", (it, cfg) => PassoPronto(it, cfg), ct);

    public Task<ItemEstadoDto> MarcarProntoAsync(MesasContexto ctx, ItemRequest req, CancellationToken ct) =>
        PassoCozinha(ctx, req, "marcarPronto", (it, cfg) =>
        {
            var p = it.Passo();
            R.TudoPronto(p, it.QuemLeva ?? R.QuemLeva(cfg, it.ProdutoId, p.EstacaoFinal, it.QuerBuscar));
            it.Aplicar(p);
            return it.Estado == "pronto" ? "Pronto" : "Esperando o cliente buscar";
        }, ct);

    private Task<ItemEstadoDto> PassoCozinha(MesasContexto ctx, ItemRequest req, string comando, Func<ItemRow, JsonObject, string> passo,
        CancellationToken ct = default) =>
        comandos.Executar(ctx, comando, req.Chave, req, async op =>
        {
            R.Exigir(ctx.Papel, "KGD");
            var lote = await Travar(op, new[] { req.ItemId ?? throw MesasException.Validacao("Escolha o item.") });
            var it = lote.Itens[0];
            if (it.Estado is "pronto" or "aguardando_retirada" or "entregue")
                throw await JaFeitoItem(op, it, "Já estava pronto");
            if (it.Estado is not ("em_fila" or "em_preparo")) throw EstadoInvalido($"Esse item está {Rot(it.Estado)}.");
            var cfg = await op.Config();
            var (de, etapaDe, estacaoDe) = (it.Estado, it.EtapaAtual, it.EstacaoId);
            var texto = passo(it, cfg);
            Carimbar(it, de, etapaDe, op.Agora);
            if (it.Estado == "em_preparo" && it.PreparoPor is null) it.PreparoPor = await op.MinhaPessoa();
            await Salvar(op, it);
            await ItemEvento(op, it.Id, comando, de, it.Estado, etapaDe, it.EtapaAtual);
            await op.TocarTodas(lote.Mesas);
            var ev = await op.Evento(comando, "item", it.Id, it.Rotulo, Rot(de), texto, mesaId: it.MesaId, sessaoId: it.SessaoId);
            AvisarCozinha(op, new[] { estacaoDe, it.EstacaoId }, it.PedidoId);
            AvisarPronto(op, it);
            return new ItemEstadoDto(ev, op.Versoes, it.Id, it.Estado);
        }, ct);

    private static string PassoPronto(ItemRow it, JsonObject cfg)
    {
        var p = it.Passo();
        if (p.Estado == "em_fila") p.Estado = "em_preparo";
        var texto = R.PassoPronto(p, it.QuemLeva ?? R.QuemLeva(cfg, it.ProdutoId, p.EstacaoFinal, it.QuerBuscar));
        it.Aplicar(p);
        return texto;
    }

    public Task<ItemEstadoDto> DesfazerPassoAsync(MesasContexto ctx, ItemRequest req, CancellationToken ct) =>
        comandos.Executar(ctx, "desfazerPasso", req.Chave, req, async op =>
        {
            R.Exigir(ctx.Papel, "KWGD");
            var lote = await Travar(op, new[] { req.ItemId ?? throw MesasException.Validacao("Escolha o item.") });
            var it = lote.Itens[0];
            if (it.Estado == "cancelado") throw EstadoInvalido("Esse item foi cancelado.");
            var eventos = await op.C.QueryAsync<(Guid Id, string Comando, string? De, string Para, short? EtapaDe, Guid? DesfazId)>(op.Sql("""
                SELECT id, comando, de, para, etapa_de, desfaz_id FROM mesa_item_evento WHERE item_id = @id ORDER BY quando DESC, id DESC
                """, new { id = it.Id }));
            var desfeitos = new HashSet<Guid>();
            (Guid Id, string Comando, string? De, string Para, short? EtapaDe, Guid? DesfazId)? alvo = null;
            foreach (var e in eventos)
            {
                if (e.DesfazId is Guid d) { desfeitos.Add(d); continue; }
                if (desfeitos.Contains(e.Id) || e.Comando == "pegarPrato") continue;
                alvo = e; break;
            }
            if (alvo is not { } a || !R.Desfazíveis.Contains(a.Comando) || a.De is null || a.Para != it.Estado)
                throw EstadoInvalido("Não há passo para voltar.");
            var (de, etapaDe, estacaoDe) = (it.Estado, it.EtapaAtual, it.EstacaoId);
            var p = it.Passo();
            R.Reverter(p, a.De, a.EtapaDe);
            it.Aplicar(p);
            switch (it.Estado)
            {
                case "em_fila": it.TPreparo = null; it.TPronto = null; it.TEntregue = null; it.AvisadoEm = null; break;
                case "em_preparo": it.TPronto = null; it.TEntregue = null; it.AvisadoEm = null; it.PegouEm = null; it.PegouPor = null; it.PegouPorNome = null; break;
                case "pronto": it.TEntregue = null; it.EntregaDireta = false; it.EntregaPresumida = false; break;
                case "aguardando_retirada": it.TEntregue = null; it.RetiradoEm = null; it.RetiradoPor = null; break;
            }
            if (it.Estado != "aguardando_retirada") { it.RetiradaDesde = null; it.RetiradoEm = null; it.RetiradoPor = null; }
            if (it.Estado is "em_fila" or "em_preparo" && de == "entregue") { it.EntregaDireta = false; it.EntregaPresumida = false; }
            it.AtrasoAvisadoEm = null;
            await Salvar(op, it);
            await ItemEvento(op, it.Id, "desfazerPasso", de, it.Estado, etapaDe, it.EtapaAtual, desfazId: a.Id);
            await op.TocarTodas(lote.Mesas);
            var ev = await op.Evento("desfazerPasso", "item", it.Id, it.Rotulo, Rot(de), Rot(it.Estado), mesaId: it.MesaId, sessaoId: it.SessaoId);
            AvisarCozinha(op, new[] { estacaoDe, it.EstacaoId }, it.PedidoId);
            return new ItemEstadoDto(ev, op.Versoes, it.Id, it.Estado);
        }, ct);

    public Task<ItemEstadoDto> AtribuirPreparoAsync(MesasContexto ctx, AtribuirPreparoRequest req, CancellationToken ct) =>
        comandos.Executar(ctx, "atribuirPreparo", req.Chave, req, async op =>
        {
            R.Exigir(ctx.Papel, "KGD");
            var pessoa = req.PessoaId ?? throw MesasException.Validacao("Escolha quem faz.");
            var nome = await NomePessoa(op, pessoa) ?? throw MesasException.NaoEncontrado("Pessoa não encontrada.");
            var lote = await Travar(op, new[] { req.ItemId ?? throw MesasException.Validacao("Escolha o item.") });
            var it = lote.Itens[0];
            if (it.Estado is not ("em_fila" or "em_preparo")) throw EstadoInvalido($"Esse item está {Rot(it.Estado)}.");
            it.PreparoPor = pessoa;
            await Salvar(op, it);
            await op.TocarTodas(lote.Mesas);
            var ev = await op.Evento("atribuirPreparo", "item", it.Id, it.Rotulo, depois: "Quem faz: " + Primeiro(nome), mesaId: it.MesaId, sessaoId: it.SessaoId);
            AvisarCozinha(op, new[] { it.EstacaoId }, it.PedidoId);
            return new ItemEstadoDto(ev, op.Versoes, it.Id, it.Estado);
        }, ct);

    public Task<ItemEstadoDto> TrocarEstacaoAsync(MesasContexto ctx, TrocarEstacaoRequest req, CancellationToken ct) =>
        comandos.Executar(ctx, "trocarEstacao", req.Chave, req, async op =>
        {
            R.Exigir(ctx.Papel, "KGD");
            var destino = req.EstacaoId?.Trim();
            var cfg = await op.Config();
            if (string.IsNullOrEmpty(destino) || MesasConfigRegras.Ler(cfg, "estacoes." + destino) is null) throw MesasException.Validacao("Escolha a estação.");
            var lote = await Travar(op, new[] { req.ItemId ?? throw MesasException.Validacao("Escolha o item.") });
            var it = lote.Itens[0];
            if (it.Estado != "em_fila") throw EstadoInvalido("Só dá para trocar antes de começar.");
            if (it.EstacaoId == destino) throw JaFeito("O item já está nessa estação.", null, null);
            var antes = it.EstacaoId;
            var p = it.Passo();
            if (p.Etapas.Count > 0) p.Etapas[p.EtapaAtual].EstacaoId = destino;
            p.EstacaoId = destino;
            it.Aplicar(p);
            await Salvar(op, it);
            await op.TocarTodas(lote.Mesas);
            var ev = await op.Evento("trocarEstacao", "item", it.Id, it.Rotulo, antes, destino, mesaId: it.MesaId, sessaoId: it.SessaoId);
            AvisarCozinha(op, new[] { antes, destino }, it.PedidoId);
            return new ItemEstadoDto(ev, op.Versoes, it.Id, it.Estado);
        }, ct);

    // ---------------------------------------------------------------- pedido: principal, cobrança, recados

    public Task<PedidoComandoDto> SoltarEtapaAsync(MesasContexto ctx, PedidoRequest req, CancellationToken ct) =>
        comandos.Executar(ctx, "soltarEtapa", req.Chave, req, async op =>
        {
            R.Exigir(ctx.Papel, "WKGD");
            await op.ExigirPontoSeGarcom();
            var (mesas, ped) = await TravarPedido(op, req.PedidoId);
            if (!ped.SegurarPrincipal) throw EstadoInvalido("Esse pedido não está segurando o principal.");
            if (ped.SoltoEm is DateTime q) throw JaFeito($"Já liberado por {Primeiro(ped.SoltoPor)} às {Hora(q)}.", ped.SoltoPor, q);
            await op.Autonomia("soltarPrincipal", null, ped.MesaId, ped.SessaoId, "Liberar o principal", req);
            await op.C.ExecuteAsync(op.Sql("UPDATE mesa_pedido SET solto_em = @agora, solto_por = @nome, atualizado_em = @agora WHERE id = @id",
                new { agora = op.Agora, nome = op.AtorNome, id = ped.Id }));
            await op.TocarTodas(mesas);
            var ev = await op.Evento("soltarEtapa", "pedido", ped.Id, "Pedido da " + RotuloMesa(mesas, ped.MesaId), depois: "Principal liberado",
                mesaId: ped.MesaId, sessaoId: ped.SessaoId);
            AvisarCozinha(op, await EstacoesDoPedido(op, ped.Id), ped.Id);
            return new PedidoComandoDto(ev, op.Versoes, ped.Id);
        }, ct);

    public Task<PedidoComandoDto> CobrarCozinhaAsync(MesasContexto ctx, PedidoRequest req, CancellationToken ct) =>
        Recado(ctx, req, "cobrarCozinha", "WGD", "cobranca", null, null, ct);

    public Task<PedidoComandoDto> RecadoCozinhaAsync(MesasContexto ctx, PedidoRequest req, CancellationToken ct) =>
        Recado(ctx, req, "recadoCozinha", "WGD", "recado", Texto(req.Texto, 140, "Recado") ?? throw MesasException.Validacao("Escolha o recado."), null, ct);

    public Task<PedidoComandoDto> AvisarAtrasoAsync(MesasContexto ctx, PedidoRequest req, CancellationToken ct) =>
        Recado(ctx, req, "avisarAtraso", "KGD", "atraso", null, req.Para == "cliente" ? "cliente" : "garcom", ct);

    private Task<PedidoComandoDto> Recado(MesasContexto ctx, PedidoRequest req, string comando, string papeis, string tipo, string? texto, string? para,
        CancellationToken ct) =>
        comandos.Executar(ctx, comando, req.Chave, req, async op =>
        {
            R.Exigir(ctx.Papel, papeis);
            await op.ExigirPontoSeGarcom();
            var (mesas, ped) = await TravarPedido(op, req.PedidoId);
            if (tipo is "cobranca" or "recado")
            {
                // Anti-repetição de 2 minutos (cobrança: qualquer uma; recado: o mesmo texto).
                var ultimo = await op.C.QueryFirstOrDefaultAsync<QuemRow>(op.Sql("""
                    SELECT por_nome AS nome, quando FROM mesa_recado
                     WHERE pedido_id = @id AND tipo = @tipo AND quando > @agora - INTERVAL '2 minutes' AND (@tipo = 'cobranca' OR texto = @texto)
                     ORDER BY quando DESC LIMIT 1
                    """, new { id = ped.Id, tipo, texto, agora = op.Agora }));
                if (ultimo is not null)
                    throw JaFeito(tipo == "cobranca"
                        ? $"A cozinha já foi cobrada por {Primeiro(ultimo.Nome)} às {Hora(ultimo.Quando)}."
                        : "Esse recado já foi para a cozinha há pouco.", ultimo.Nome, ultimo.Quando);
            }
            await op.C.ExecuteAsync(op.Sql("""
                INSERT INTO mesa_recado (id, estabelecimento_id, pedido_id, tipo, texto, para, por, por_nome, quando)
                VALUES (@id, @loja, @pedido, @tipo, @texto, @para, @por, @nome, @agora)
                """, new { id = Guid.NewGuid(), loja = ctx.Loja, pedido = ped.Id, tipo, texto, para, por = ctx.Usuario.ToString(), nome = op.AtorNome, agora = op.Agora }));
            await op.TocarTodas(mesas);
            var depois = tipo switch
            {
                "cobranca" => "Cobrou a cozinha",
                "recado" => "Recado: " + texto,
                _ => para == "cliente" ? "Avisou a mesa" : "Avisou o garçom"
            };
            var ev = await op.Evento(comando, "pedido", ped.Id, "Pedido da " + RotuloMesa(mesas, ped.MesaId), depois: depois, mesaId: ped.MesaId, sessaoId: ped.SessaoId);
            AvisarCozinha(op, await EstacoesDoPedido(op, ped.Id), ped.Id);
            if (tipo == "atraso") { var loja = ctx.Loja; var g = ped.GarcomId; op.Avisar(t => t.PendenciasMudou(loja, "atraso", g)); }
            return new PedidoComandoDto(ev, op.Versoes, ped.Id);
        }, ct);

    // ---------------------------------------------------------------- pronto, quem leva, Peguei, entrega

    public Task<ItensDto> AvisarProntoAsync(MesasContexto ctx, ItensRequest req, CancellationToken ct) =>
        comandos.Executar(ctx, "avisarPronto", req.Chave, req, async op =>
        {
            R.Exigir(ctx.Papel, "KGD");
            var lote = await Travar(op, await Escolher(op, req));
            var its = lote.Itens.Where(i => i.Estado is "pronto" or "aguardando_retirada").ToList();
            if (its.Count == 0) throw EstadoInvalido("Nada pronto para avisar.");
            foreach (var it in its) { it.AvisadoEm = op.Agora; await Salvar(op, it); AvisarPronto(op, it, forcar: true); }
            foreach (var pid in its.Select(i => i.PedidoId).Distinct())
                await op.C.ExecuteAsync(op.Sql("""
                    INSERT INTO mesa_recado (id, estabelecimento_id, pedido_id, tipo, por, por_nome, quando) VALUES (@id, @loja, @pid, 'aviso_pronto', @por, @nome, @agora)
                    """, new { id = Guid.NewGuid(), loja = ctx.Loja, pid, por = ctx.Usuario.ToString(), nome = op.AtorNome, agora = op.Agora }));
            await op.TocarTodas(lote.Mesas);
            var ev = await op.Evento("avisarPronto", "pedido", its[0].PedidoId, string.Join(", ", its.Select(i => i.Rotulo)),
                depois: its[0].Estado == "pronto" ? "Chamou o garçom" : "Avisou o cliente de novo", mesaId: its[0].MesaId, sessaoId: its[0].SessaoId);
            AvisarCozinha(op, its.Select(i => i.EstacaoId), its[0].PedidoId);
            return new ItensDto(ev, op.Versoes, its.Count, its.Select(i => i.Id).ToList());
        }, ct);

    public Task<ItemEstadoDto> TrocarQuemLevaAsync(MesasContexto ctx, TrocarQuemLevaRequest req, CancellationToken ct) =>
        comandos.Executar(ctx, "trocarQuemLeva", req.Chave, req, async op =>
        {
            R.Exigir(ctx.Papel, "GD");
            var q = req.QuemLeva is "cliente" or "balcao" ? "cliente" : req.QuemLeva == "garcom" ? "garcom" : throw MesasException.Validacao("Escolha quem leva.");
            var lote = await Travar(op, new[] { req.ItemId ?? throw MesasException.Validacao("Escolha o item.") });
            var it = lote.Itens[0];
            if (it.Estado is "entregue" or "cancelado") throw EstadoInvalido($"Esse item está {Rot(it.Estado)}.");
            var cfg = await op.Config();
            var antes = it.QuemLeva ?? R.QuemLeva(cfg, it.ProdutoId, it.Passo().EstacaoFinal, it.QuerBuscar);
            if (antes == q) throw JaFeito("Já está assim.", null, null);
            var de = it.Estado;
            it.QuemLeva = q;
            if (it.Estado == "pronto" && q == "cliente")
            {
                it.Estado = "aguardando_retirada"; it.RetiradaEstacao = it.Passo().EstacaoFinal; it.RetiradaDesde = op.Agora; it.AvisadoEm = op.Agora;
            }
            else if (it.Estado == "aguardando_retirada" && q == "garcom")
            {
                it.Estado = "pronto"; it.RetiradaEstacao = null; it.RetiradaDesde = null; it.AvisadoEm = op.Agora;
            }
            await Salvar(op, it);
            if (de != it.Estado) await ItemEvento(op, it.Id, "trocarQuemLeva", de, it.Estado, it.EtapaAtual, it.EtapaAtual);
            await op.TocarTodas(lote.Mesas);
            string Txt(string x) => x == "cliente" ? "Cliente busca" : "Garçom leva";
            var ev = await op.Evento("trocarQuemLeva", "item", it.Id, it.Rotulo, Txt(antes), Txt(q), mesaId: it.MesaId, sessaoId: it.SessaoId);
            AvisarCozinha(op, new[] { it.EstacaoId }, it.PedidoId);
            if (de != it.Estado) AvisarPronto(op, it, forcar: true);
            return new ItemEstadoDto(ev, op.Versoes, it.Id, it.Estado);
        }, ct);

    /// <summary>"Peguei": o prato pronto fica a caminho com o nome de quem pegou. Dois garçons no mesmo prato: o segundo recebe <c>JA_FEITO</c>.</summary>
    public Task<ItensDto> PegarPratoAsync(MesasContexto ctx, ItensRequest req, CancellationToken ct) =>
        comandos.Executar(ctx, "pegarPrato", req.Chave, req, async op =>
        {
            R.Exigir(ctx.Papel, "WGD");
            await op.ExigirPontoSeGarcom();
            var lote = await Travar(op, await Escolher(op, req));
            var eu = ctx.Usuario.ToString();
            var outro = lote.Itens.FirstOrDefault(i => i.Estado == "pronto" && i.PegouPor is not null && i.PegouPor != eu);
            if (outro is not null)
                throw JaFeito($"{Primeiro(outro.PegouPorNome)} já pegou esse prato.", outro.PegouPorNome, outro.PegouEm);
            var its = lote.Itens.Where(i => i.Estado == "pronto" && i.QuemLeva != "cliente" && i.PegouPor is null).ToList();
            if (its.Count == 0)
            {
                if (lote.Itens.Any(i => i.Estado == "pronto" && i.PegouPor == eu)) throw JaFeito("Você já pegou esse prato.", op.AtorNome, null);
                var e = lote.Itens.FirstOrDefault(i => i.Estado == "entregue");
                if (e is not null) throw await JaFeitoItem(op, e, "Esse prato já foi levado");
                throw EstadoInvalido("Esse prato ainda não está pronto.");
            }
            var minha = await op.MinhaPessoa();
            foreach (var it in its)
            {
                it.PegouEm = op.Agora; it.PegouPor = eu; it.PegouPorNome = op.AtorNome; it.LevaGarcomId ??= minha;
                await Salvar(op, it);
                await ItemEvento(op, it.Id, "pegarPrato", "pronto", "pronto", it.EtapaAtual, it.EtapaAtual);
            }
            await op.TocarTodas(lote.Mesas);
            var ev = await op.Evento("pegarPrato", "item", its[0].Id, string.Join(", ", its.Select(i => i.Rotulo)), depois: "A caminho da mesa",
                mesaId: its[0].MesaId, sessaoId: its[0].SessaoId);
            AvisarCozinha(op, its.Select(i => i.EstacaoId), its[0].PedidoId);
            return new ItensDto(ev, op.Versoes, its.Count, its.Select(i => i.Id).ToList());
        }, ct);

    public Task<ItensDto> PassarEntregaAsync(MesasContexto ctx, PassarEntregaRequest req, CancellationToken ct) =>
        comandos.Executar(ctx, "passarEntrega", req.Chave, req, async op =>
        {
            R.Exigir(ctx.Papel, "WGD");
            await op.ExigirPontoSeGarcom();
            var garcom = req.GarcomId ?? throw MesasException.Validacao("Escolha o garçom.");
            var nome = await op.GarcomComPonto(garcom);
            var ids = (req.ItemIds ?? Array.Empty<Guid>()).Concat(req.ItemId is Guid i ? new[] { i } : Array.Empty<Guid>()).Distinct().ToArray();
            var lote = await Travar(op, ids);
            var its = lote.Itens.Where(x => x.Estado == "pronto").ToList();
            if (its.Count == 0) throw EstadoInvalido("Nada pronto para levar.");
            foreach (var it in its)
            {
                it.LevaGarcomId = garcom; it.AvisadoEm = op.Agora; it.PegouEm = null; it.PegouPor = null; it.PegouPorNome = null;
                await Salvar(op, it);
            }
            await op.TocarTodas(lote.Mesas);
            var ev = await op.Evento("passarEntrega", "pedido", its[0].PedidoId, string.Join(", ", its.Select(x => x.Rotulo)),
                depois: "Quem leva: " + Primeiro(nome), mesaId: its[0].MesaId, sessaoId: its[0].SessaoId);
            var loja = ctx.Loja;
            op.Avisar(t => t.Aviso(loja, "pronto", its[0].Id, its[0].MesaId, garcom));
            op.Avisar(t => t.PendenciasMudou(loja, "pronto", garcom));
            AvisarCozinha(op, its.Select(x => x.EstacaoId), its[0].PedidoId);
            return new ItensDto(ev, op.Versoes, its.Count, its.Select(x => x.Id).ToList());
        }, ct);

    /// <summary>
    /// "Entreguei". Com o item escolhido (<c>itemId|itemIds</c>) vale mesmo sem "pronto" (cozinha sem painel ou que esqueceu de marcar):
    /// grava <c>entrega_direta</c>. Pela mesa ou pelo pedido, só o que está pronto.
    /// </summary>
    public Task<ItensDto> EntregarAsync(MesasContexto ctx, ItensRequest req, CancellationToken ct) =>
        comandos.Executar(ctx, "entregar", req.Chave, req, async op =>
        {
            R.Exigir(ctx.Papel, "WGD");
            await op.ExigirPontoSeGarcom();
            var explicito = req.ItemId is not null || req.ItemIds is { Count: > 0 };
            var lote = await Travar(op, await Escolher(op, req));
            var its = lote.Itens.Where(i => explicito ? i.Estado is "pronto" or "em_fila" or "em_preparo" : i.Estado == "pronto").ToList();
            if (its.Count == 0)
            {
                var e = lote.Itens.FirstOrDefault(i => i.Estado == "entregue");
                if (e is not null) throw await JaFeitoItem(op, e, "Já entregue");
                throw EstadoInvalido("Nada para entregar.");
            }
            foreach (var it in its) await Entregar(op, it, presumida: false, "entregar");
            await op.TocarTodas(lote.Mesas);
            var ev = await op.Evento("entregar", "item", its[0].Id, string.Join(", ", its.Select(i => i.Rotulo)),
                depois: its.Any(i => i.EntregaDireta) ? "Entregue direto" : "Entregue", mesaId: its[0].MesaId, sessaoId: its[0].SessaoId);
            AvisarCozinha(op, its.Select(i => i.EstacaoId), its[0].PedidoId);
            var loja = ctx.Loja; var g = its[0].SessaoGarcomId;
            op.Avisar(t => t.PendenciasMudou(loja, "pronto", g));
            return new ItensDto(ev, op.Versoes, its.Count, its.Select(i => i.Id).ToList());
        }, ct);

    public Task<ItemEstadoDto> MarcarRetiradoAsync(MesasContexto ctx, ItemRequest req, CancellationToken ct) =>
        comandos.Executar(ctx, "marcarRetirado", req.Chave, req, async op =>
        {
            R.Exigir(ctx.Papel, "KWGD");
            await op.ExigirPontoSeGarcom();
            var lote = await Travar(op, new[] { req.ItemId ?? throw MesasException.Validacao("Escolha o item.") });
            var it = lote.Itens[0];
            if (it.Estado == "entregue") throw await JaFeitoItem(op, it, "Já retirado");
            if (it.Estado != "aguardando_retirada") throw EstadoInvalido("Esse item não está esperando retirada.");
            it.RetiradoEm = op.Agora; it.RetiradoPor = op.AtorNome;
            it.Estado = "entregue"; it.TEntregue = op.Agora;
            await Salvar(op, it);
            await ItemEvento(op, it.Id, "marcarRetirado", "aguardando_retirada", "entregue", it.EtapaAtual, it.EtapaAtual);
            await op.TocarTodas(lote.Mesas);
            var ev = await op.Evento("marcarRetirado", "item", it.Id, it.Rotulo, depois: "Retirado no balcão", mesaId: it.MesaId, sessaoId: it.SessaoId);
            AvisarCozinha(op, new[] { it.EstacaoId }, it.PedidoId);
            return new ItemEstadoDto(ev, op.Versoes, it.Id, it.Estado);
        }, ct);

    // ---------------------------------------------------------------- mexer no item (autonomia)

    public Task<ItensDto> PratoVoltouAsync(MesasContexto ctx, ItensRequest req, CancellationToken ct) =>
        comandos.Executar(ctx, "pratoVoltou", req.Chave, req, async op =>
        {
            R.Exigir(ctx.Papel, "WGD");
            var motivo = Motivo(req.Motivo);
            await op.ExigirPontoSeGarcom();
            var lote = await Travar(op, await Escolher(op, req));
            var its = lote.Itens.Where(i => i.Estado is "pronto" or "entregue" or "aguardando_retirada").ToList();
            if (its.Count == 0) throw EstadoInvalido("Esse prato não está pronto nem entregue.");
            await AutonomiaItem(op, "pratoVoltou", motivo, its[0], "Prato voltou: " + string.Join(", ", its.Select(i => i.Rotulo)), req);
            var cfg = await op.Config();
            foreach (var it in its)
            {
                var (de, etapaDe) = (it.Estado, it.EtapaAtual);
                var p = it.Passo();
                foreach (var e in p.Etapas) e.Feita = false;
                p.EtapaAtual = 0; p.EstacaoId = p.Etapas.Count > 0 ? p.Etapas[0].EstacaoId : p.EstacaoId; p.Estado = "em_fila"; p.QuemLeva = null; p.RetiradaEstacao = null;
                it.Aplicar(p);
                it.Obs = Cortar("↻ Refazer: " + motivo + (string.IsNullOrWhiteSpace(it.Obs) || it.Obs.StartsWith("↻") ? "" : " · " + it.Obs), 200);
                it.Refazer++;
                it.TFila = op.Agora; it.TPreparo = null; it.TPronto = null; it.TEntregue = null;
                it.PegouEm = null; it.PegouPor = null; it.PegouPorNome = null; it.AvisadoEm = null; it.RetiradaDesde = null; it.RetiradoEm = null;
                it.RetiradoPor = null; it.EntregaDireta = false; it.EntregaPresumida = false; it.AtrasoAvisadoEm = null; it.PreparoPor = null;
                it.QuemLeva = R.QuemLeva(cfg, it.ProdutoId, it.Passo().EstacaoFinal, it.QuerBuscar);
                await Salvar(op, it);
                await ItemEvento(op, it.Id, "pratoVoltou", de, "em_fila", etapaDe, 0);
            }
            await op.TocarTodas(lote.Mesas);
            var ev = await op.Evento("pratoVoltou", "item", its[0].Id, string.Join(", ", its.Select(i => i.Rotulo)), depois: "Voltou para a cozinha",
                motivo: motivo, mesaId: its[0].MesaId, sessaoId: its[0].SessaoId, ocorrencia: true);
            AvisarCozinha(op, its.Select(i => i.EstacaoId), its[0].PedidoId);
            return new ItensDto(ev, op.Versoes, its.Count, its.Select(i => i.Id).ToList());
        }, ct);

    public Task<ItemEstadoDto> CancelarItemAsync(MesasContexto ctx, ItemRequest req, CancellationToken ct) =>
        comandos.Executar(ctx, "cancelarItem", req.Chave, req, async op =>
        {
            R.Exigir(ctx.Papel, "WGD");
            var motivo = Motivo(req.Motivo);
            await op.ExigirPontoSeGarcom();
            var lote = await Travar(op, new[] { req.ItemId ?? throw MesasException.Validacao("Escolha o item.") });
            var it = lote.Itens[0];
            if (it.Estado == "cancelado") throw await JaFeitoItem(op, it, "Já cancelado");
            await AutonomiaItem(op, R.ChaveCancelar(it.Estado), motivo, it, "Cancelar " + it.Rotulo, req);
            var (de, estacao) = (it.Estado, it.EstacaoId);
            it.Estado = "cancelado"; it.Motivo = motivo;
            await Salvar(op, it);
            await ItemEvento(op, it.Id, "cancelarItem", de, "cancelado", it.EtapaAtual, it.EtapaAtual);
            await AtualizarPedidos(op, new[] { it.PedidoId }, "cancelado");
            await op.TocarTodas(lote.Mesas);
            var ev = await op.Evento("cancelarItem", "item", it.Id, it.Rotulo, Rot(de), "Cancelado", motivo, it.MesaId, it.SessaoId,
                ocorrencia: !R.AntesDaCozinha.Contains(de));
            AvisarCozinha(op, new[] { estacao }, it.PedidoId);
            var loja = ctx.Loja; var g = it.SessaoGarcomId;
            op.Avisar(t => t.PendenciasMudou(loja, "aprovacao", g));
            return new ItemEstadoDto(ev, op.Versoes, it.Id, it.Estado);
        }, ct);

    public Task<ItemEstadoDto> EditarItemAsync(MesasContexto ctx, EditarItemRequest req, CancellationToken ct) =>
        comandos.Executar(ctx, "editarItem", req.Chave, req, async op =>
        {
            R.Exigir(ctx.Papel, "WGD");
            await op.ExigirPontoSeGarcom();
            if (req.Qtd is decimal q && (q <= 0 || q > 999)) throw MesasException.Validacao("Quantidade inválida.");
            var obs = req.Obs is null ? null : Texto(req.Obs, 200, "Observação") ?? "";
            var lote = await Travar(op, new[] { req.ItemId ?? throw MesasException.Validacao("Escolha o item.") });
            var it = lote.Itens[0];
            ExigirAberta(lote);
            if (it.Estado is "cancelado" or "entregue") throw EstadoInvalido($"Esse item está {Rot(it.Estado)}.");
            await ExigirComandasAbertas(op, it.Id);
            await AutonomiaItem(op, R.ChaveEditar(it.Estado), req.Motivo, it, "Mudar " + it.Rotulo, req);
            var antes = it.Rotulo + (string.IsNullOrEmpty(it.Obs) ? "" : " · " + it.Obs);
            if (req.Qtd is decimal nq) it.Qtd = nq;
            if (obs is not null) it.Obs = obs.Length == 0 ? null : obs;
            await Salvar(op, it);
            if (req.ComandaIds is { Count: > 0 } cids) await TrocarPartes(op, it, cids);
            else if (req.Qtd is not null) await RecalcularPartes(op, it);
            await op.TocarTodas(lote.Mesas);
            var ev = await op.Evento("editarItem", "item", it.Id, it.Nome, antes, it.Rotulo + (string.IsNullOrEmpty(it.Obs) ? "" : " · " + it.Obs),
                req.Motivo, it.MesaId, it.SessaoId);
            AvisarCozinha(op, new[] { it.EstacaoId }, it.PedidoId);
            return new ItemEstadoDto(ev, op.Versoes, it.Id, it.Estado);
        }, ct);

    public Task<ItemEstadoDto> MoverItemAsync(MesasContexto ctx, MoverItemRequest req, CancellationToken ct) =>
        comandos.Executar(ctx, "moverItem", req.Chave, req, async op =>
        {
            R.Exigir(ctx.Papel, "WGD");
            await op.ExigirPontoSeGarcom();
            var cids = req.ComandaIds is { Count: > 0 } l ? l : throw MesasException.Validacao("Escolha de quem é o item.");
            var lote = await Travar(op, new[] { req.ItemId ?? throw MesasException.Validacao("Escolha o item.") });
            var it = lote.Itens[0];
            ExigirAberta(lote);
            if (it.Estado == "cancelado") throw EstadoInvalido("Esse item foi cancelado.");
            await ExigirComandasAbertas(op, it.Id);
            var tipos = (await op.C.QueryAsync<string>(op.Sql("SELECT tipo FROM mesa_comanda WHERE id = ANY(@ids)", new { ids = cids.ToArray() }))).ToList();
            var regra = tipos.Count > 0 && tipos.All(t => t == "coletiva") ? "itemMesa" : "itemPessoa";
            await AutonomiaItem(op, regra, req.Motivo, it, "Passar " + it.Rotulo, req);
            var antes = await NomesDasComandas(op, it.Id);
            await TrocarPartes(op, it, cids);
            await op.TocarTodas(lote.Mesas);
            var ev = await op.Evento("moverItem", "item", it.Id, it.Rotulo, antes, await NomesDasComandas(op, it.Id), req.Motivo, it.MesaId, it.SessaoId);
            return new ItemEstadoDto(ev, op.Versoes, it.Id, it.Estado);
        }, ct);

    public Task<ItemMovidoDto> MoverItemMesaAsync(MesasContexto ctx, MoverItemMesaRequest req, CancellationToken ct) =>
        comandos.Executar(ctx, "moverItemMesa", req.Chave, req, async op =>
        {
            R.Exigir(ctx.Papel, "WGD");
            await op.ExigirPontoSeGarcom();
            var itemId = req.ItemId ?? throw MesasException.Validacao("Escolha o item.");
            var paraMesa = req.ParaMesaId ?? throw MesasException.Validacao("Escolha a mesa.");
            var origemSessao = await op.C.QueryFirstOrDefaultAsync<Guid?>(op.Sql(
                "SELECT sessao_id FROM mesa_item WHERE id = @itemId AND estabelecimento_id = @loja", new { itemId, loja = ctx.Loja }))
                ?? throw MesasException.NaoEncontrado("Item não encontrado.");
            var destSessao = await SessaoDaMesa(op, paraMesa) ?? throw EstadoInvalido("A outra mesa não está aberta.");
            if (destSessao == origemSessao) throw JaFeito("O item já está nessa mesa.", null, null);
            var ids = await op.C.QueryAsync<Guid>(op.Sql("SELECT mesa_id FROM mesa_sessao_mesa WHERE sessao_id = ANY(@s) AND ate IS NULL",
                new { s = new[] { origemSessao, destSessao } }));
            var mesas = await op.Travar(ids);
            var it = (await ItensTravados(op, new[] { itemId }))[0];
            if (it.SessaoId != origemSessao) throw EstadoInvalido("O item mudou. Confira de novo.");
            if (it.Estado == "cancelado") throw EstadoInvalido("Esse item foi cancelado.");
            if (it.SessaoEstado != "aberta") throw EstadoInvalido("A mesa do item já foi encerrada.");
            var mOrigem = mesas.FirstOrDefault(x => x.MesaId == it.MesaId) ?? mesas.First(x => x.SessaoId == origemSessao);
            var mDestino = mesas.First(x => x.MesaId == paraMesa);
            await op.ConferirVersao(mOrigem, req.VersaoMesa);
            await op.ConferirVersao(mDestino, req.VersaoParaMesa ?? mDestino.Versao);
            await ExigirComandasAbertas(op, it.Id);
            var comandas = (await op.C.QueryAsync<ComandaRow>(op.Sql("SELECT id, tipo, aberta FROM mesa_comanda WHERE sessao_id = @destSessao",
                new { destSessao }))).ToList();
            var alvo = req.ComandaId is Guid cid
                ? comandas.FirstOrDefault(c => c.Id == cid && c.Aberta) ?? throw MesasException.Validacao("Essa conta não é da outra mesa ou já foi fechada.")
                : comandas.FirstOrDefault(c => c.Tipo == "coletiva" && c.Aberta) ?? throw EstadoInvalido("A outra mesa ainda não tem conta.");
            await AutonomiaItem(op, "itemOutraMesa", req.Motivo, it, $"Mandar {it.Rotulo} para a {mDestino.Rotulo}", req);

            var outros = await op.C.ExecuteScalarAsync<int>(op.Sql("SELECT count(*) FROM mesa_item WHERE pedido_id = @p AND id <> @id",
                new { p = it.PedidoId, id = it.Id }));
            var pedidoNovo = it.PedidoId;
            if (outros == 0)
                await op.C.ExecuteAsync(op.Sql("UPDATE mesa_pedido SET sessao_id = @s, mesa_id = @m, atualizado_em = @agora WHERE id = @p",
                    new { s = destSessao, m = paraMesa, agora = op.Agora, p = it.PedidoId }));
            else
            {
                pedidoNovo = Guid.NewGuid();
                await op.C.ExecuteAsync(op.Sql("""
                    INSERT INTO mesa_pedido (id, estabelecimento_id, sessao_id, mesa_id, origem, ator_id, ator_nome, pessoa_id, estado, segurar_principal,
                                             visto_em, visto_por, criado_em, atualizado_em)
                    SELECT @novo, estabelecimento_id, @s, @m, origem, ator_id, ator_nome, NULL, estado, false, visto_em, visto_por, criado_em, @agora
                      FROM mesa_pedido WHERE id = @p
                    """, new { novo = pedidoNovo, s = destSessao, m = paraMesa, agora = op.Agora, p = it.PedidoId }));
            }
            var mesaAntes = it.MesaId;
            it.MovidoDe = mesaAntes; it.SessaoId = destSessao; it.MesaId = paraMesa; it.PedidoId = pedidoNovo;
            await Salvar(op, it);
            await TrocarPartes(op, it, new[] { alvo.Id });
            await AtualizarPedidos(op, new[] { pedidoNovo }, "cancelado");
            await op.TocarTodas(mesas);
            var dono = await GarcomDaSessao(op, destSessao);
            var minha = await op.MinhaPessoa();
            var avisar = dono is Guid d && d != minha ? dono : null;
            var ev = await op.Evento("moverItemMesa", "item", it.Id, it.Rotulo, mOrigem.Rotulo, mDestino.Rotulo, req.Motivo, paraMesa, destSessao);
            var loja = ctx.Loja;
            if (avisar is Guid a) op.Avisar(t => t.Aviso(loja, "pedido", it.Id, paraMesa, a));
            AvisarCozinha(op, new[] { it.EstacaoId }, pedidoNovo);
            return new ItemMovidoDto(ev, op.Versoes, it.Id, paraMesa, pedidoNovo, avisar);
        }, ct);

    // ---------------------------------------------------------------- cortesia, perda, avisos ao gestor

    public Task<AvisoCriadoDto> CortesiaAsync(MesasContexto ctx, CortesiaRequest req, CancellationToken ct) =>
        comandos.Executar(ctx, "cortesia", req.Chave, req, async op =>
        {
            R.Exigir(ctx.Papel, "WGD");
            var motivo = Motivo(req.Motivo);
            await op.ExigirPontoSeGarcom();
            var lote = await Travar(op, new[] { req.ItemId ?? throw MesasException.Validacao("Escolha o item.") });
            var it = lote.Itens[0];
            var m = lote.Mesas.FirstOrDefault(x => x.MesaId == it.MesaId) ?? lote.Mesas.FirstOrDefault();
            if (m is not null) await op.ConferirVersao(m, req.VersaoMesa);
            if (it.Estado == "cancelado") throw EstadoInvalido("Esse item foi cancelado.");
            if (it.CortesiaJson is not null) throw await JaFeitoEvento(op, it.Id, "cortesia", "Já é cortesia");
            await ExigirComandasAbertas(op, it.Id);
            var valor = Math.Round(it.Preco * it.Qtd, 2, MidpointRounding.AwayFromZero);
            var cfg = await op.Config();
            if (ctx.Papel == MesasPermissoes.PapelGarcom && !R.CortesiaSozinho(cfg, valor))
                await AutonomiaItem(op, "cortesia", motivo, it, $"Cortesia: {it.Rotulo}", req);
            it.CortesiaJson = JsonSerializer.Serialize(new { motivo, por = ctx.Usuario.ToString(), porNome = op.AtorNome, quando = op.Agora, valor }, MesasComandos.Json);
            await Salvar(op, it);
            var avisoId = Guid.NewGuid();
            await op.C.ExecuteAsync(op.Sql("""
                INSERT INTO mesa_aviso_gestor (id, estabelecimento_id, tipo, mesa_id, item_id, valor, motivo, por, por_nome, texto, criado_em)
                VALUES (@avisoId, @loja, 'cortesia', @mesa, @item, @valor, @motivo, @por, @nome, @texto, @agora)
                """, new { avisoId, loja = ctx.Loja, mesa = it.MesaId, item = it.Id, valor, motivo, por = ctx.Usuario.ToString(), nome = op.AtorNome,
                texto = $"Cortesia de {it.Rotulo} na {m?.Rotulo ?? "mesa"}", agora = op.Agora }));
            await op.TocarTodas(lote.Mesas);
            var ev = await op.Evento("cortesia", "item", it.Id, it.Rotulo, depois: "Cortesia de " + Dinheiro(valor), motivo: motivo, mesaId: it.MesaId,
                sessaoId: it.SessaoId, ocorrencia: true);
            var loja = ctx.Loja;
            op.Avisar(t => t.PendenciasMudou(loja, "aviso", null));
            return new AvisoCriadoDto(ev, op.Versoes, avisoId);
        }, ct);

    public Task<PerdaDto> RegistrarPerdaAsync(MesasContexto ctx, PerdaRequest req, CancellationToken ct) =>
        comandos.Executar(ctx, "registrarPerda", req.Chave, req, async op =>
        {
            R.Exigir(ctx.Papel, "WKGD");
            var motivo = Motivo(req.Motivo);
            await op.ExigirPontoSeGarcom();
            Guid produto; string nome; Guid? sessao = null; Guid? mesa = req.MesaId; decimal qtd;
            if (req.ItemId is Guid itemId)
            {
                var i = await op.C.QueryFirstOrDefaultAsync<(Guid ProdutoId, string Nome, Guid SessaoId, Guid MesaId, decimal Qtd)>(op.Sql(
                    "SELECT produto_id, nome, sessao_id, mesa_id, qtd FROM mesa_item WHERE id = @itemId AND estabelecimento_id = @loja",
                    new { itemId, loja = ctx.Loja }));
                if (i.Nome is null) throw MesasException.NaoEncontrado("Item não encontrado.");
                (produto, nome, sessao, mesa, qtd) = (i.ProdutoId, i.Nome, i.SessaoId, i.MesaId, req.Qtd ?? i.Qtd);
            }
            else
            {
                produto = req.ProdutoId ?? throw MesasException.Validacao("Escolha o produto.");
                nome = await op.C.QueryFirstOrDefaultAsync<string?>(op.Sql(
                    "SELECT nome FROM cardapio_produto WHERE id = @produto AND id_estabelecimento = @loja", new { produto, loja = ctx.Loja }))
                    ?? throw MesasException.NaoEncontrado("Produto não encontrado.");
                qtd = req.Qtd ?? 1;
            }
            if (qtd <= 0 || qtd > 999) throw MesasException.Validacao("Quantidade inválida.");
            var perdaId = Guid.NewGuid(); var avisoId = Guid.NewGuid();
            await op.C.ExecuteAsync(op.Sql("""
                INSERT INTO mesa_perda (id, estabelecimento_id, produto_id, nome, item_id, sessao_id, qtd, motivo, custo, por, por_nome, criado_em)
                VALUES (@perdaId, @loja, @produto, @nome, @item, @sessao, @qtd, @motivo, 0, @por, @porNome, @agora);
                INSERT INTO mesa_aviso_gestor (id, estabelecimento_id, tipo, mesa_id, item_id, valor, motivo, por, por_nome, texto, criado_em)
                VALUES (@avisoId, @loja, 'perda', @mesa, @item, 0, @motivo, @por, @porNome, @texto, @agora);
                """, new { perdaId, avisoId, loja = ctx.Loja, produto, nome, item = req.ItemId, sessao, mesa, qtd, motivo, por = ctx.Usuario.ToString(),
                porNome = op.AtorNome, texto = $"Perda: {R.Qtd(qtd)}× {nome}", agora = op.Agora }));
            var ev = await op.Evento("registrarPerda", "produto", produto, $"{R.Qtd(qtd)}× {nome}", depois: "Perda registrada", motivo: motivo,
                mesaId: mesa, sessaoId: sessao, ocorrencia: true);
            var loja = ctx.Loja;
            op.Avisar(t => t.PendenciasMudou(loja, "aviso", null));
            return new PerdaDto(ev, op.Versoes, perdaId, avisoId);
        }, ct);

    public Task<AvisoCriadoDto> MarcarAvisoLidoAsync(MesasContexto ctx, AvisoRequest req, CancellationToken ct) =>
        comandos.Executar(ctx, "marcarAvisoLido", req.Chave, req, async op =>
        {
            R.Exigir(ctx.Papel, "GD");
            var id = req.AvisoId ?? throw MesasException.Validacao("Escolha o aviso.");
            var a = await op.C.QueryFirstOrDefaultAsync<(bool Lido, string? LidoPor, DateTime? LidoEm, string? Texto, Guid? MesaId)>(op.Sql(
                "SELECT lido, lido_por, lido_em, texto, mesa_id FROM mesa_aviso_gestor WHERE id = @id AND estabelecimento_id = @loja FOR UPDATE",
                new { id, loja = ctx.Loja }));
            if (a == default) throw MesasException.NaoEncontrado("Aviso não encontrado.");
            if (a.Lido) throw JaFeito($"Já visto por {Primeiro(a.LidoPor)} às {Hora(a.LidoEm ?? op.Agora)}.", a.LidoPor, a.LidoEm);
            await op.C.ExecuteAsync(op.Sql("UPDATE mesa_aviso_gestor SET lido = true, lido_por = @nome, lido_em = @agora WHERE id = @id",
                new { nome = op.AtorNome, agora = op.Agora, id }));
            var ev = await op.Evento("marcarAvisoLido", "aviso", id, a.Texto, depois: "Visto", mesaId: a.MesaId);
            var loja = ctx.Loja;
            op.Avisar(t => t.PendenciasMudou(loja, "aviso", null));
            return new AvisoCriadoDto(ev, op.Versoes, id);
        }, ct);

    // ---------------------------------------------------------------- autorização (P07 chama ao aprovar)

    private static readonly Dictionary<string, Type> Executaveis = new()
    {
        ["editarItem"] = typeof(EditarItemRequest), ["moverItem"] = typeof(MoverItemRequest), ["moverItemMesa"] = typeof(MoverItemMesaRequest),
        ["pratoVoltou"] = typeof(ItensRequest), ["cancelarItem"] = typeof(ItemRequest), ["cortesia"] = typeof(CortesiaRequest),
        ["soltarEtapa"] = typeof(PedidoRequest)
    };

    public bool Atende(string comando) => Executaveis.ContainsKey(comando);

    public async Task<object> Executar(MesasContexto ctx, string comando, JsonElement corpo, Guid chave, CancellationToken ct)
    {
        T Ler<T>() => corpo.Deserialize<T>(MesasComandos.Json) ?? throw MesasException.Validacao("Pedido de autorização inválido.");
        return comando switch
        {
            "editarItem" => await EditarItemAsync(ctx, Ler<EditarItemRequest>() with { Chave = chave }, ct),
            "moverItem" => await MoverItemAsync(ctx, Ler<MoverItemRequest>() with { Chave = chave }, ct),
            // As versões podem ter subido enquanto o gerente pensava: a aprovação usa as atuais.
            "moverItemMesa" => await MoverItemMesaAtual(ctx, Ler<MoverItemMesaRequest>() with { Chave = chave }, ct),
            "pratoVoltou" => await PratoVoltouAsync(ctx, Ler<ItensRequest>() with { Chave = chave }, ct),
            "cancelarItem" => await CancelarItemAsync(ctx, Ler<ItemRequest>() with { Chave = chave }, ct),
            "cortesia" => await CortesiaAtual(ctx, Ler<CortesiaRequest>() with { Chave = chave }, ct),
            "soltarEtapa" => await SoltarEtapaAsync(ctx, Ler<PedidoRequest>() with { Chave = chave }, ct),
            _ => throw MesasException.Validacao("Comando desconhecido.")
        };
    }

    private async Task<ItemMovidoDto> MoverItemMesaAtual(MesasContexto ctx, MoverItemMesaRequest req, CancellationToken ct)
    {
        var (a, b) = await VersoesAtuais(ctx.Loja, req.ItemId, req.ParaMesaId, ct);
        return await MoverItemMesaAsync(ctx, req with { VersaoMesa = a, VersaoParaMesa = b }, ct);
    }

    private async Task<AvisoCriadoDto> CortesiaAtual(MesasContexto ctx, CortesiaRequest req, CancellationToken ct)
    {
        var (a, _) = await VersoesAtuais(ctx.Loja, req.ItemId, null, ct);
        return await CortesiaAsync(ctx, req with { VersaoMesa = a }, ct);
    }

    private async Task<(int?, int?)> VersoesAtuais(Guid loja, Guid? itemId, Guid? paraMesa, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        var a = await c.QueryFirstOrDefaultAsync<int?>(new CommandDefinition("""
            SELECT e.versao FROM mesa_item i JOIN mesa_estado e ON e.mesa_id = i.mesa_id WHERE i.id = @itemId AND i.estabelecimento_id = @loja
            """, new { itemId, loja }, cancellationToken: ct));
        var b = paraMesa is null ? null : await c.QueryFirstOrDefaultAsync<int?>(new CommandDefinition(
            "SELECT versao FROM mesa_estado WHERE mesa_id = @paraMesa AND estabelecimento_id = @loja", new { paraMesa, loja }, cancellationToken: ct));
        return (a, b);
    }

    // ---------------------------------------------------------------- sistema (worker e encerrar)

    /// <summary>
    /// Fecha como entrega presumida: ao encerrar (<paramref name="sessaoId"/>, tudo que está na produção ou pronto) ou depois do "Peguei"
    /// (<paramref name="itemIds"/>). Só se <c>entrega.fechaSozinho</c>. Devolve as estações tocadas.
    /// </summary>
    internal static async Task<IReadOnlyCollection<string>> FecharPresumidas(NpgsqlConnection c, NpgsqlTransaction tx, Guid loja, Guid? sessaoId,
        IReadOnlyCollection<Guid>? itemIds, string comando, CancellationToken ct, JsonObject cfg)
    {
        if (MesasConfigRegras.Ler(cfg, "entrega.fechaSozinho") is JsonValue v && v.TryGetValue<bool>(out var liga) && !liga) return Array.Empty<string>();
        var rows = (await c.QueryAsync<(Guid Id, string Estado, string EstacaoId, short EtapaAtual)>(new CommandDefinition("""
            SELECT id, estado, estacao_id, etapa_atual FROM mesa_item
             WHERE estabelecimento_id = @loja AND estado IN ('em_fila', 'em_preparo', 'pronto')
               AND (CAST(@sessaoId AS uuid) IS NULL OR sessao_id = @sessaoId)
               AND (CAST(@ids AS uuid[]) IS NULL OR id = ANY(@ids))
             ORDER BY id FOR UPDATE
            """, new { loja, sessaoId, ids = itemIds?.ToArray() }, tx, cancellationToken: ct))).ToList();
        foreach (var r in rows)
        {
            await c.ExecuteAsync(new CommandDefinition("""
                UPDATE mesa_item SET estado = 'entregue', entrega_presumida = true, entrega_direta = (estado <> 'pronto'),
                       t_pronto = COALESCE(t_pronto, NOW()), t_entregue = NOW() WHERE id = @id;
                INSERT INTO mesa_item_evento (id, estabelecimento_id, item_id, comando, de, para, etapa_de, etapa_para, por, por_nome, quando, presumido)
                VALUES (@ev, @loja, @id, @comando, @de, 'entregue', @etapa, @etapa, 'sistema', 'Sistema', NOW(), true);
                """, new { id = r.Id, ev = Guid.NewGuid(), loja, comando, de = r.Estado, etapa = r.EtapaAtual }, tx, cancellationToken: ct));
        }
        return rows.Select(r => r.EstacaoId).Distinct().ToList();
    }

    // ---------------------------------------------------------------- auxiliares de banco

    private async Task<IReadOnlyCollection<Guid>> Escolher(MesasOperacao op, ItensRequest req)
    {
        if (req.ItemIds is { Count: > 0 } l) return l.Distinct().ToList();
        if (req.ItemId is Guid i) return new[] { i };
        if (req.PedidoId is Guid p)
            return (await op.C.QueryAsync<Guid>(op.Sql("SELECT id FROM mesa_item WHERE pedido_id = @p AND estabelecimento_id = @loja",
                new { p, loja = op.Ctx.Loja }))).ToList() is { Count: > 0 } ids ? ids : throw MesasException.NaoEncontrado("Pedido não encontrado.");
        if (req.MesaId is Guid m)
        {
            var s = await SessaoDaMesa(op, m) ?? throw EstadoInvalido("A mesa não está aberta.");
            var ids = (await op.C.QueryAsync<Guid>(op.Sql("SELECT id FROM mesa_item WHERE sessao_id = @s", new { s }))).ToList();
            return ids.Count > 0 ? ids : throw EstadoInvalido("Essa mesa ainda não tem pedido.");
        }
        throw MesasException.Validacao("Escolha o item, o pedido ou a mesa.");
    }

    /// <summary>Trava as mesas das sessões dos itens (ordem por id) e depois os itens (ordem por id).</summary>
    private async Task<Lote> Travar(MesasOperacao op, IReadOnlyCollection<Guid> itemIds)
    {
        if (itemIds.Count == 0) throw MesasException.Validacao("Escolha o item.");
        var ids = itemIds.Distinct().ToArray();
        var sessoes = (await op.C.QueryAsync<Guid>(op.Sql(
            "SELECT DISTINCT sessao_id FROM mesa_item WHERE estabelecimento_id = @loja AND id = ANY(@ids)", new { loja = op.Ctx.Loja, ids }))).ToArray();
        if (sessoes.Length == 0) throw MesasException.NaoEncontrado("Item não encontrado.");
        var mesaIds = await op.C.QueryAsync<Guid>(op.Sql("SELECT mesa_id FROM mesa_sessao_mesa WHERE sessao_id = ANY(@sessoes) AND ate IS NULL",
            new { sessoes }));
        var mesas = await op.Travar(mesaIds);
        var itens = await ItensTravados(op, ids);
        if (itens.Any(i => !sessoes.Contains(i.SessaoId))) throw EstadoInvalido("O item mudou de mesa. Confira de novo.");
        return new Lote(mesas, itens);
    }

    private static async Task<List<ItemRow>> ItensTravados(MesasOperacao op, Guid[] ids)
    {
        var itens = (await op.C.QueryAsync<ItemRow>(op.Sql(ItemSelect + """
             WHERE i.estabelecimento_id = @loja AND i.id = ANY(@ids) ORDER BY i.id FOR UPDATE OF i
            """, new { loja = op.Ctx.Loja, ids }))).ToList();
        if (itens.Count != ids.Length) throw MesasException.NaoEncontrado("Item não encontrado.");
        return itens;
    }

    private static async Task<(IReadOnlyList<MesaTravada>, PedidoRow)> TravarPedido(MesasOperacao op, Guid? pedidoId)
    {
        var id = pedidoId ?? throw MesasException.Validacao("Escolha o pedido.");
        var sessao = await op.C.QueryFirstOrDefaultAsync<Guid?>(op.Sql("SELECT sessao_id FROM mesa_pedido WHERE id = @id AND estabelecimento_id = @loja",
            new { id, loja = op.Ctx.Loja })) ?? throw MesasException.NaoEncontrado("Pedido não encontrado.");
        var mesas = await op.TravarSessao(sessao);
        var ped = await op.C.QueryFirstAsync<PedidoRow>(op.Sql("""
            SELECT p.id, p.sessao_id, p.mesa_id, p.estado, p.segurar_principal, p.solto_em, p.solto_por, p.visto_em, p.visto_por, p.escalado_para,
                   s.garcom_id
              FROM mesa_pedido p JOIN mesa_sessao s ON s.id = p.sessao_id WHERE p.id = @id FOR UPDATE OF p
            """, new { id }));
        return (mesas, ped);
    }

    private static Task<Guid?> SessaoDaMesa(MesasOperacao op, Guid mesaId) => op.C.QueryFirstOrDefaultAsync<Guid?>(op.Sql("""
        SELECT sm.sessao_id FROM mesa_sessao_mesa sm JOIN mesa_sessao s ON s.id = sm.sessao_id
         WHERE sm.mesa_id = @mesaId AND sm.ate IS NULL AND s.estado = 'aberta' AND s.estabelecimento_id = @loja
         LIMIT 1
        """, new { mesaId, loja = op.Ctx.Loja }));

    private static Task<Guid?> GarcomDaSessao(MesasOperacao op, Guid sessaoId) =>
        op.C.QueryFirstOrDefaultAsync<Guid?>(op.Sql("SELECT garcom_id FROM mesa_sessao WHERE id = @sessaoId", new { sessaoId }));

    private static Task<Guid?> CategoriaDo(MesasOperacao op, Guid produtoId) =>
        op.C.QueryFirstOrDefaultAsync<Guid?>(op.Sql("SELECT categoria_id FROM cardapio_produto WHERE id = @produtoId", new { produtoId }));

    private static async Task<string> OrigemDoItem(MesasOperacao op, Guid itemId) =>
        await op.C.QueryFirstOrDefaultAsync<string?>(op.Sql("SELECT origem FROM mesa_item WHERE id = @itemId", new { itemId })) ?? "garcom";

    private static Task<string?> NomePessoa(MesasOperacao op, Guid pessoaId) => op.C.QueryFirstOrDefaultAsync<string?>(op.Sql(
        "SELECT nome::text FROM gestao_pessoas WHERE id = @pessoaId AND estabelecimento_id = @loja", new { pessoaId, loja = op.Ctx.Loja }));

    /// <summary>Quem aprova quando escala: <c>aprovacao.escalonarPara</c> ou o gerente de salão com ponto aberto.</summary>
    internal static async Task<Guid?> QuemAprova(MesasOperacao op)
    {
        if (Guid.TryParse(R.Texto(MesasConfigRegras.Ler(await op.Config(), "aprovacao.escalonarPara")), out var fixo)) return fixo;
        return await op.C.QueryFirstOrDefaultAsync<Guid?>(op.Sql("""
            SELECT p.id FROM gestao_pessoas p
             WHERE p.estabelecimento_id = @loja AND p.ativo AND p.funcao_codigo = 'gerente_salao'
               AND EXISTS (SELECT 1 FROM gestao_ponto t WHERE t.pessoa_id = p.id AND t.estabelecimento_id = @loja AND t.saida IS NULL)
             ORDER BY p.nome LIMIT 1
            """, new { loja = op.Ctx.Loja }));
    }

    private static async Task<IReadOnlyCollection<string>> EstacoesDoPedido(MesasOperacao op, Guid pedidoId) =>
        (await op.C.QueryAsync<string>(op.Sql("SELECT DISTINCT estacao_id FROM mesa_item WHERE pedido_id = @pedidoId", new { pedidoId }))).ToList();

    private static Task Salvar(MesasOperacao op, ItemRow it) => op.C.ExecuteAsync(op.Sql("""
        UPDATE mesa_item SET estado = @Estado, etapa_atual = @EtapaAtual, etapas = CAST(@EtapasJson AS jsonb), estacao_id = @EstacaoId,
               quem_leva = @QuemLeva, leva_garcom_id = @LevaGarcomId, avisado_em = @AvisadoEm, pegou_em = @PegouEm, pegou_por = @PegouPor,
               pegou_por_nome = @PegouPorNome, preparo_por = @PreparoPor, retirada_estacao = @RetiradaEstacao, retirada_desde = @RetiradaDesde,
               retirado_em = @RetiradoEm, retirado_por = @RetiradoPor, cortesia = CAST(@CortesiaJson AS jsonb), refazer = @Refazer,
               entrega_presumida = @EntregaPresumida, entrega_direta = @EntregaDireta, aprovado_por = @AprovadoPor, motivo = @Motivo, obs = @Obs,
               qtd = @Qtd, t_fila = @TFila, t_preparo = @TPreparo, t_pronto = @TPronto, t_entregue = @TEntregue, atraso_avisado_em = @AtrasoAvisadoEm,
               sessao_id = @SessaoId, mesa_id = @MesaId, pedido_id = @PedidoId, movido_de = @MovidoDe
         WHERE id = @Id
        """, it));

    private static Task ItemEvento(MesasOperacao op, Guid itemId, string comando, string? de, string para, short? etapaDe, short? etapaPara,
        bool presumido = false, Guid? desfazId = null) => op.C.ExecuteAsync(op.Sql("""
        INSERT INTO mesa_item_evento (id, estabelecimento_id, item_id, comando, de, para, etapa_de, etapa_para, por, por_nome, quando, presumido, desfaz_id)
        VALUES (@id, @loja, @itemId, @comando, @de, @para, @etapaDe, @etapaPara, @por, @nome, @agora, @presumido, @desfazId)
        """, new { id = Guid.NewGuid(), loja = op.Ctx.Loja, itemId, comando, de, para, etapaDe, etapaPara, por = op.Ctx.Usuario.ToString(),
        nome = op.AtorNome, agora = op.Agora, presumido, desfazId }));

    /// <summary>Estado do pedido a partir dos itens (§2.4). <paramref name="todosCancelados"/>: recusado (recusarPedido) ou cancelado.</summary>
    private static Task AtualizarPedidos(MesasOperacao op, IEnumerable<Guid> pedidoIds, string todosCancelados, bool visto = false) =>
        op.C.ExecuteAsync(op.Sql("""
            UPDATE mesa_pedido p SET estado = CASE
                    WHEN NOT EXISTS (SELECT 1 FROM mesa_item i WHERE i.pedido_id = p.id AND i.estado <> 'cancelado') THEN @todos
                    WHEN EXISTS (SELECT 1 FROM mesa_item i WHERE i.pedido_id = p.id AND i.estado = 'aguardando_aprovacao') THEN 'aguardando_aprovacao'
                    WHEN NOT EXISTS (SELECT 1 FROM mesa_item i WHERE i.pedido_id = p.id AND i.estado NOT IN ('rascunho', 'cancelado')) THEN 'rascunho'
                    ELSE 'enviado' END,
                   visto_em = CASE WHEN @visto THEN COALESCE(p.visto_em, @agora) ELSE p.visto_em END,
                   visto_por = CASE WHEN @visto THEN COALESCE(p.visto_por, @nome) ELSE p.visto_por END,
                   atualizado_em = @agora
             WHERE p.id = ANY(@ids)
            """, new { ids = pedidoIds.Distinct().ToArray(), todos = todosCancelados, visto, agora = op.Agora, nome = op.AtorNome }));

    private static async Task GravarPartes(MesasOperacao op, Guid itemId, decimal total, IReadOnlyList<Guid> comandaIds)
    {
        var partes = R.Repartir(total, comandaIds.Count);
        for (var i = 0; i < comandaIds.Count; i++)
            await op.C.ExecuteAsync(op.Sql("""
                INSERT INTO mesa_item_parte (id, estabelecimento_id, item_id, comanda_id, percentual, valor) VALUES (@id, @loja, @itemId, @comanda, @pct, @valor)
                """, new { id = Guid.NewGuid(), loja = op.Ctx.Loja, itemId, comanda = comandaIds[i], pct = partes[i].Percentual, valor = partes[i].Valor }));
    }

    private static async Task TrocarPartes(MesasOperacao op, ItemRow it, IReadOnlyList<Guid> comandaIds)
    {
        var ids = comandaIds.Distinct().ToList();
        var validas = (await op.C.QueryAsync<Guid>(op.Sql("SELECT id FROM mesa_comanda WHERE sessao_id = @s AND aberta AND id = ANY(@ids)",
            new { s = it.SessaoId, ids = ids.ToArray() }))).ToHashSet();
        if (ids.Any(c => !validas.Contains(c))) throw EstadoInvalido("Essa conta já foi fechada ou não é desta mesa.");
        await op.C.ExecuteAsync(op.Sql("DELETE FROM mesa_item_parte WHERE item_id = @id", new { id = it.Id }));
        await GravarPartes(op, it.Id, it.Preco * it.Qtd, ids);
    }

    private static async Task RecalcularPartes(MesasOperacao op, ItemRow it)
    {
        var ids = (await op.C.QueryAsync<Guid>(op.Sql("SELECT comanda_id FROM mesa_item_parte WHERE item_id = @id ORDER BY percentual DESC, comanda_id",
            new { id = it.Id }))).ToList();
        if (ids.Count == 0) return;
        await op.C.ExecuteAsync(op.Sql("DELETE FROM mesa_item_parte WHERE item_id = @id", new { id = it.Id }));
        await GravarPartes(op, it.Id, it.Preco * it.Qtd, ids);
    }

    private static async Task ExigirComandasAbertas(MesasOperacao op, Guid itemId)
    {
        var fechada = await op.C.ExecuteScalarAsync<bool>(op.Sql("""
            SELECT EXISTS (SELECT 1 FROM mesa_item_parte ip JOIN mesa_comanda c ON c.id = ip.comanda_id WHERE ip.item_id = @itemId AND NOT c.aberta)
            """, new { itemId }));
        if (fechada) throw EstadoInvalido("A conta desse item já foi fechada.");
    }

    private static async Task<string> NomesDasComandas(MesasOperacao op, Guid itemId) =>
        string.Join(", ", await op.C.QueryAsync<string>(op.Sql("""
            SELECT c.nome FROM mesa_item_parte ip JOIN mesa_comanda c ON c.id = ip.comanda_id WHERE ip.item_id = @itemId ORDER BY c.ordem, c.nome
            """, new { itemId })));

    /// <summary>Autonomia do garçom; se pedir o gerente, a solicitação fica ligada ao item (selo "esperando o gerente").</summary>
    private static async Task AutonomiaItem(MesasOperacao op, string regra, string? motivo, ItemRow it, string detalhe, object corpo)
    {
        try
        {
            await op.Autonomia(regra, motivo, it.MesaId, it.SessaoId, Cortar(detalhe, 280), corpo);
        }
        catch (PedeGerenteException pg)
        {
            await op.C.ExecuteAsync(op.Sql("UPDATE mesa_solicitacao SET item_id = @item WHERE id = @id AND item_id IS NULL", new { item = it.Id, id = pg.SolicitacaoId }));
            throw;
        }
    }

    private static async Task MudarMesaSeOcupada(MesasOperacao op, IReadOnlyList<MesaTravada> mesas)
    {
        if (mesas.Count > 0 && mesas.Any(m => m.Status == "ocupada"))
            foreach (var m in mesas) await op.Mudar(m, "em_atendimento", m.SessaoId);
        else await op.TocarTodas(mesas);
    }

    private static async Task Entregar(MesasOperacao op, ItemRow it, bool presumida, string comando)
    {
        var (de, etapa) = (it.Estado, it.EtapaAtual);
        if (it.Estado != "pronto") { it.EntregaDireta = true; it.TPronto ??= op.Agora; }
        it.Estado = "entregue"; it.TEntregue = op.Agora; it.EntregaPresumida = presumida;
        await Salvar(op, it);
        await ItemEvento(op, it.Id, comando, de, "entregue", etapa, etapa, presumida);
    }

    private static void EntrarNaFila(ItemRow it, JsonObject cfg, DateTime agora)
    {
        it.Estado = "em_fila"; it.TFila = agora;
        it.QuemLeva ??= R.QuemLeva(cfg, it.ProdutoId, it.Passo().EstacaoFinal, it.QuerBuscar);
    }

    /// <summary>Tempos do passo: nova estação = <c>t_fila</c>; começar = <c>t_preparo</c>; pronto/retirada = <c>t_pronto</c> e aviso.</summary>
    private static void Carimbar(ItemRow it, string de, short etapaDe, DateTime agora)
    {
        it.AtrasoAvisadoEm = null;
        switch (it.Estado)
        {
            case "em_fila" when it.EtapaAtual != etapaDe: it.TFila = agora; it.TPreparo = null; break;
            case "em_preparo": it.TPreparo ??= agora; break;
            case "pronto": it.TPreparo ??= agora; it.TPronto = agora; it.AvisadoEm = agora; break;
            case "aguardando_retirada": it.TPreparo ??= agora; it.TPronto = agora; it.AvisadoEm = agora; it.RetiradaDesde = agora; break;
        }
    }

    private void AvisarCozinha(MesasOperacao op, IEnumerable<string> estacoes, Guid? pedidoId)
    {
        var lista = estacoes.Where(e => !string.IsNullOrEmpty(e)).Distinct().ToArray();
        if (lista.Length == 0) return;
        var loja = op.Ctx.Loja;
        op.Avisar(_ => cozinhaRt.CozinhaMudou(loja, lista, pedidoId));
    }

    private static void AvisarPronto(MesasOperacao op, ItemRow it, bool forcar = false)
    {
        if (!forcar && it.Estado is not ("pronto" or "aguardando_retirada")) return;
        var loja = op.Ctx.Loja;
        var garcom = it.Estado == "pronto" ? it.LevaGarcomId ?? it.SessaoGarcomId : null;
        op.Avisar(t => t.Aviso(loja, "pronto", it.Id, it.MesaId, garcom));
        op.Avisar(t => t.PendenciasMudou(loja, "pronto", garcom));
    }

    private static void ExigirAberta(Lote lote)
    {
        if (lote.Itens.Any(i => i.SessaoEstado != "aberta")) throw EstadoInvalido("Essa mesa já foi encerrada.");
    }

    private static async Task<MesasException> JaResolvido(MesasOperacao op, List<ItemRow> itens)
    {
        var feito = itens.FirstOrDefault(i => i.AprovadoPor is not null) ?? itens.FirstOrDefault(i => i.Estado == "cancelado");
        if (feito is null) return EstadoInvalido("Nada para aprovar.");
        return feito.Estado == "cancelado" && feito.AprovadoPor is null
            ? await JaFeitoItem(op, feito, "Já recusado")
            : await JaFeitoEvento(op, feito.Id, "aprovarPedido", "Já aprovado");
    }

    /// <summary>"Já … por Fulano às HH:MM", pelo último passo do item.</summary>
    private static async Task<MesasException> JaFeitoItem(MesasOperacao op, ItemRow it, string frase)
    {
        var q = await op.C.QueryFirstOrDefaultAsync<QuemRow>(op.Sql("""
            SELECT por_nome AS nome, quando FROM mesa_item_evento WHERE item_id = @id AND para = @estado ORDER BY quando DESC LIMIT 1
            """, new { id = it.Id, estado = it.Estado }));
        return q is null ? JaFeito(frase + ".", null, null) : JaFeito($"{frase} por {Primeiro(q.Nome)} às {Hora(q.Quando)}.", q.Nome, q.Quando);
    }

    private static async Task<MesasException> JaFeitoEvento(MesasOperacao op, Guid itemId, string comando, string frase)
    {
        var q = await op.C.QueryFirstOrDefaultAsync<QuemRow>(op.Sql("""
            SELECT por_nome AS nome, quando FROM mesa_item_evento WHERE item_id = @itemId AND comando = @comando ORDER BY quando DESC LIMIT 1
            """, new { itemId, comando }))
            ?? await op.C.QueryFirstOrDefaultAsync<QuemRow>(op.Sql("""
            SELECT ator_nome AS nome, quando FROM mesa_evento WHERE estabelecimento_id = @loja AND alvo_id = @itemId AND acao = @comando ORDER BY quando DESC LIMIT 1
            """, new { loja = op.Ctx.Loja, itemId, comando }));
        return q is null ? JaFeito(frase + ".", null, null) : JaFeito($"{frase} por {Primeiro(q.Nome)} às {Hora(q.Quando)}.", q.Nome, q.Quando);
    }

    // ---------------------------------------------------------------- auxiliares puros

    private static string Origem(string papel, string? pedida) => papel switch
    {
        MesasPermissoes.PapelGarcom => "garcom",
        MesasPermissoes.PapelDono or MesasPermissoes.PapelGerente => pedida is "cliente" or "balcao" ? pedida : "gestao",
        _ => "gestao"
    };

    private static string Rotulo(Lote lote, Guid mesaId) => RotuloMesa(lote.Mesas, mesaId);
    private static string RotuloMesa(IReadOnlyList<MesaTravada> mesas, Guid mesaId) => mesas.FirstOrDefault(m => m.MesaId == mesaId)?.Rotulo ?? "mesa";

    internal static MesasException EstadoInvalido(string mensagem) => new("ESTADO_INVALIDO", mensagem, 409);

    internal static MesasException JaFeito(string mensagem, string? porQuem, DateTime? quando) =>
        new("JA_FEITO", mensagem, 409) { Detalhes = new { conflito = true, porQuem, quando } };

    private static string Motivo(string? motivo)
    {
        var m = motivo?.Trim();
        if (string.IsNullOrEmpty(m)) throw new MesasException("EXIGE_MOTIVO", "Diga o motivo.", 422);
        if (m.Length > 280) throw MesasException.Validacao("Motivo muito longo (até 280 letras).");
        return m;
    }

    private static string? Texto(string? texto, int limite, string campo)
    {
        var t = texto?.Trim();
        if (string.IsNullOrEmpty(t)) return null;
        if (t.Length > limite) throw MesasException.Validacao($"{campo} muito longa (até {limite} letras).");
        return t;
    }

    private static string Cortar(string s, int max) => s.Length <= max ? s : s[..max];

    internal static string Primeiro(string? nome) => string.IsNullOrWhiteSpace(nome) ? "outra pessoa" : nome.Trim().Split(' ')[0];

    private static readonly TimeZoneInfo Fuso = AcharFuso();
    private static TimeZoneInfo AcharFuso()
    {
        foreach (var id in new[] { "America/Sao_Paulo", "E. South America Standard Time" })
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { /* tenta o próximo */ }
        return TimeZoneInfo.CreateCustomTimeZone("BRT", TimeSpan.FromHours(-3), "BRT", "BRT");
    }

    internal static string Hora(DateTime quando) =>
        TimeZoneInfo.ConvertTimeFromUtc(quando.Kind == DateTimeKind.Utc ? quando : quando.ToUniversalTime(), Fuso).ToString("HH:mm");

    private static string Dinheiro(decimal v) => "R$ " + v.ToString("N2", System.Globalization.CultureInfo.GetCultureInfo("pt-BR"));

    private static string Rot(string estado) => estado switch
    {
        "rascunho" => "rascunho",
        "aguardando_aprovacao" => "esperando aprovação",
        "em_fila" => "na fila",
        "em_preparo" => "em preparo",
        "pronto" => "pronto",
        "aguardando_retirada" => "esperando retirada",
        "entregue" => "entregue",
        "cancelado" => "cancelado",
        _ => estado
    };
}
