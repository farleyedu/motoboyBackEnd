using APIBack.Mesas.Nucleo;
using Dapper;
using Npgsql;
using R = APIBack.Mesas.Sessao.SessaoRegras;

namespace APIBack.Mesas.Sessao;

public interface ISessaoService
{
    Task<MesaAbertaDto> AbrirMesaAsync(MesasContexto ctx, AbrirMesaRequest req, CancellationToken ct);
    Task<PessoaIncluidaDto> IncluirPessoaAsync(MesasContexto ctx, IncluirPessoaRequest req, CancellationToken ct);
    Task<ComandoDto> SentarAsync(MesasContexto ctx, SentarRequest req, CancellationToken ct);
    Task<ComandoDto> TrocarLugaresAsync(MesasContexto ctx, TrocarLugaresRequest req, CancellationToken ct);
    Task<ComandoDto> MarcarAlergiaAsync(MesasContexto ctx, MarcarAlergiaRequest req, CancellationToken ct);
    Task<AnotacaoDto> AnotarMesaAsync(MesasContexto ctx, AnotarMesaRequest req, CancellationToken ct);
    /// <summary><paramref name="atender"/>: o garçom assume a mesa (atenderMesa); senão o gestor escolhe (atribuirGarcom).</summary>
    Task<ComandoDto> TrocarGarcomAsync(MesasContexto ctx, GarcomMesaRequest req, bool atender, CancellationToken ct);
    Task<MesaTransferidaDto> TransferirMesaAsync(MesasContexto ctx, TransferirMesaRequest req, CancellationToken ct);
    Task<MesasJuntadasDto> JuntarMesasAsync(MesasContexto ctx, JuntarMesasRequest req, CancellationToken ct);
    Task<ComandoDto> SepararMesasAsync(MesasContexto ctx, MesaRequest req, CancellationToken ct);
    Task<ComandoDto> PedirContaAsync(MesasContexto ctx, MesaRequest req, CancellationToken ct);
    Task<ComandoDto> EncerrarAsync(MesasContexto ctx, EncerrarRequest req, CancellationToken ct);
    Task<ComandoDto> ReabrirAsync(MesasContexto ctx, ReabrirRequest req, CancellationToken ct);
    Task<ComandoDto> LiberarLimpezaAsync(MesasContexto ctx, MesaRequest req, CancellationToken ct);
    Task<ChamadoCriadoDto> ChamarGarcomAsync(MesasContexto ctx, ChamarGarcomRequest req, CancellationToken ct);
    Task<ComandoDto> CaminhoChamadoAsync(MesasContexto ctx, ChamadoRequest req, CancellationToken ct);
    Task<ComandoDto> PassarChamadoAsync(MesasContexto ctx, ChamadoRequest req, CancellationToken ct);
    Task<ChamadosAtendidosDto> AtenderChamadoAsync(MesasContexto ctx, AtenderChamadoRequest req, CancellationToken ct);
}

/// <summary>
/// Sessão da mesa (P07): abrir, pessoas, cadeiras, garçom, transferir, juntar, separar, conta pedida, encerrar, reabrir,
/// liberar e chamados. Toda mudança trava <c>mesa_estado</c>, confere a versão onde o contrato pede, sobe a versão de todas as mesas
/// tocadas e grava uma linha em <c>mesa_evento</c> com quem fez. Dinheiro (saldo ao encerrar) é do P14, pelos ganchos.
/// </summary>
public sealed class SessaoService(MesasComandos comandos, IEnumerable<IMesasSessaoGancho> ganchos) : ISessaoService
{
    private sealed class SessaoRow
    {
        public Guid Id { get; set; }
        public Guid MesaId { get; set; }
        public string Estado { get; set; } = "";
        public DateTime AbertaEm { get; set; }
        public string AbertaPor { get; set; } = "";
        public Guid? GarcomId { get; set; }
        public string? GarcomNome { get; set; }
        public string TipoComanda { get; set; } = "mesa";
        public short OcupantesAnonimos { get; set; }
        public DateTime? ContaPedidaEm { get; set; }
        public DateTime? EncerradaEm { get; set; }
    }

    private sealed class PessoaRow
    {
        public Guid Id { get; set; }
        public Guid SessaoId { get; set; }
        public string? Nome { get; set; }
        public string? Celular { get; set; }
        public string? CadeiraId { get; set; }
        public DateTime? SaiuEm { get; set; }
        public string Rotulo => string.IsNullOrWhiteSpace(Nome) ? "Cliente" : Nome!;
    }

    private sealed class ChamadoRow
    {
        public Guid Id { get; set; }
        public Guid SessaoId { get; set; }
        public Guid? PessoaId { get; set; }
        public string Estado { get; set; } = "";
        public string? Motivo { get; set; }
        public Guid? GarcomId { get; set; }
        public DateTime CriadoEm { get; set; }
        public DateTime? ACaminhoEm { get; set; }
        public string? ACaminhoPor { get; set; }
        public DateTime? AtendidoEm { get; set; }
        public string? AtendidoPor { get; set; }
    }

    private const string SessaoSelect = """
        SELECT s.id, s.mesa_id AS mesaId, s.estado, s.aberta_em AS abertaEm, s.aberta_por AS abertaPor, s.garcom_id AS garcomId,
               g.nome::text AS garcomNome, s.tipo_comanda AS tipoComanda, s.ocupantes_anonimos AS ocupantesAnonimos,
               s.conta_pedida_em AS contaPedidaEm, s.encerrada_em AS encerradaEm
          FROM mesa_sessao s LEFT JOIN gestao_pessoas g ON g.id = s.garcom_id
        """;

    private const string PessoaSelect = """
        SELECT id, sessao_id AS sessaoId, nome, celular, cadeira_id AS cadeiraId, saiu_em AS saiuEm FROM mesa_pessoa
        """;

    // ---------- abrir e pessoas ----------

    public Task<MesaAbertaDto> AbrirMesaAsync(MesasContexto ctx, AbrirMesaRequest req, CancellationToken ct) =>
        comandos.Executar(ctx, "abrirMesa", req.Chave, req, async op =>
        {
            var mesaId = req.MesaId ?? throw MesasException.Validacao("Escolha a mesa.");
            var pessoas = req.Pessoas ?? 0;
            if (pessoas is < 0 or > 100) throw MesasException.Validacao("Quantidade de pessoas fora do limite.");
            var nome = Texto(req.Nome, 80, "Nome");
            var celular = Celular(req.Celular);
            var eu = await op.ExigirPontoSeGarcom();

            var m = await op.Travar(mesaId);
            if (R.Aberta(m.Status))
            {
                var s = await Sessao(op, m.SessaoId!.Value);
                throw JaFeito($"A {m.Rotulo} já foi aberta por {R.PrimeiroNome(s.AbertaPor)} às {Hora(s.AbertaEm)}.", s.AbertaPor, s.AbertaEm,
                    new { sessaoId = s.Id });
            }
            await op.ConferirVersao(m, req.VersaoMesa);
            if (!R.PodeAbrir(m.Status)) throw EstadoInvalido($"A {m.Rotulo} está {R.Rotulo(m.Status)}.");

            var tipo = req.TipoComanda?.Trim() ?? await op.ConfigTexto("abertura.tipoComandaPadrao") ?? "mesa";
            if (tipo is not ("mesa" or "pessoa")) throw MesasException.Validacao("A conta é da mesa ou por pessoa.");
            var garcomId = req.GarcomId ?? (ctx.Papel == MesasPermissoes.PapelGarcom ? eu : null);
            var garcomNome = garcomId is Guid gid ? await op.GarcomComPonto(gid) : null;

            var antes = m.Status;
            var aberta = await AbrirSessao(op, m, garcomId, tipo, pessoas, req.ReservaId);
            Guid? pessoaId = null;
            if (nome is not null || celular is not null)
                pessoaId = (await IncluirInterno(op, aberta.SessaoId, nome, celular, null, req.ReservaId is null ? "garcom" : "reserva",
                    tipo == "pessoa", titular: true)).PessoaId;
            await op.Mudar(m, R.Ocupada, aberta.SessaoId);

            var depois = string.Join(" · ", new[] { R.Pessoas(pessoas), garcomNome is null ? null : "atende " + R.PrimeiroNome(garcomNome) }
                .Where(x => x is not null));
            var eventoId = await op.Evento("abrirMesa", "mesa", m.MesaId, m.Rotulo, Capital(R.Rotulo(antes)),
                depois.Length == 0 ? "Aberta" : depois, mesaId: m.MesaId, sessaoId: aberta.SessaoId);
            Pendencias(op, garcomId);
            return new MesaAbertaDto(eventoId, op.Versoes, aberta.SessaoId, aberta.ColetivaId, tipo, pessoaId, aberta.QrToken);
        }, ct);

    public Task<PessoaIncluidaDto> IncluirPessoaAsync(MesasContexto ctx, IncluirPessoaRequest req, CancellationToken ct) =>
        comandos.Executar(ctx, "incluirPessoa", req.Chave, req, async op =>
        {
            var mesaId = req.MesaId ?? throw MesasException.Validacao("Escolha a mesa.");
            var nome = Texto(req.Nome, 80, "Nome");
            var celular = Celular(req.Celular);
            var cadeira = Texto(req.CadeiraId, 120, "Cadeira");
            var alergia = Texto(req.Alergia, 200, "Alergia");
            var origem = req.Origem?.Trim() ?? "garcom";
            if (origem is not ("garcom" or "qr" or "reserva")) throw MesasException.Validacao("Origem inválida.");
            var eu = await op.ExigirPontoSeGarcom();

            var (m, mesas) = await TravarComSessao(op, mesaId);
            if (R.PodeAbrir(m.Status))
            {
                // Como no protótipo: incluir alguém numa mesa livre já abre a mesa.
                var garcomId = ctx.Papel == MesasPermissoes.PapelGarcom ? eu : null;
                if (garcomId is Guid g) await op.GarcomComPonto(g);
                var tipo = await op.ConfigTexto("abertura.tipoComandaPadrao") ?? "mesa";
                var aberta = await AbrirSessao(op, m, garcomId, tipo, 0, null);
                await op.Mudar(m, R.Ocupada, aberta.SessaoId);
                await op.Evento("abrirMesa", "mesa", m.MesaId, m.Rotulo, Capital(R.Rotulo(R.Livre)), "Aberta", mesaId: m.MesaId, sessaoId: aberta.SessaoId);
                mesas = new[] { m };
            }
            else if (!R.Aberta(m.Status)) throw EstadoInvalido($"A {m.Rotulo} não está aberta.");
            var sessaoId = m.SessaoId!.Value;

            if (celular is not null)
            {
                var ja = await op.C.QueryFirstOrDefaultAsync<PessoaRow>(op.Sql(PessoaSelect +
                    " WHERE sessao_id = @sessaoId AND celular = @celular AND saiu_em IS NULL LIMIT 1", new { sessaoId, celular }));
                if (ja is not null)
                {
                    var cmd = await ComandaAberta(op, ja.Id);
                    return new PessoaIncluidaDto(Guid.Empty, op.Versoes, sessaoId, ja.Id, cmd, true);
                }
            }
            if (cadeira is not null)
            {
                ConferirCadeiraDaSessao(cadeira, mesas);
                await CadeiraLivre(op, sessaoId, cadeira, null);
            }

            var r = await IncluirInterno(op, sessaoId, nome, celular, cadeira, origem, req.Individual ?? true, titular: false, alergia);
            await op.TocarTodas(mesas);
            var eventoId = await op.Evento("incluirPessoa", "pessoa", r.PessoaId, r.Nome, depois: origem == "qr" ? "Entrou pelo QR" : cadeira,
                mesaId: m.MesaId, sessaoId: sessaoId);
            return new PessoaIncluidaDto(eventoId, op.Versoes, sessaoId, r.PessoaId, r.ComandaId, false);
        }, ct);

    public Task<ComandoDto> SentarAsync(MesasContexto ctx, SentarRequest req, CancellationToken ct) =>
        comandos.Executar(ctx, "sentar", req.Chave, req, async op =>
        {
            var pessoaId = req.PessoaId ?? throw MesasException.Validacao("Escolha o cliente.");
            var cadeira = Texto(req.CadeiraId, 120, "Cadeira") ?? throw MesasException.Validacao("Escolha a cadeira.");
            await op.ExigirPontoSeGarcom();
            var (p, mesas) = await TravarPessoa(op, pessoaId);
            await op.Autonomia("cadeira", null, mesas[0].MesaId, p.SessaoId, $"Trocar {p.Rotulo} de cadeira", req);
            if (p.CadeiraId == cadeira) return new ComandoDto(Guid.Empty, op.Versoes); // sem efeito
            ConferirCadeiraDaSessao(cadeira, mesas);
            await CadeiraLivre(op, p.SessaoId, cadeira, p.Id);

            await op.C.ExecuteAsync(op.Sql("UPDATE mesa_pessoa SET cadeira_id = @cadeira WHERE id = @id", new { cadeira, id = p.Id }));
            await op.TocarTodas(mesas);
            var eventoId = await op.Evento(p.CadeiraId is null ? "sentar" : "trocarAssento", "pessoa", p.Id, p.Rotulo, p.CadeiraId, cadeira,
                mesaId: MesaDaCadeira(cadeira, mesas), sessaoId: p.SessaoId);
            return new ComandoDto(eventoId, op.Versoes);
        }, ct);

    public Task<ComandoDto> TrocarLugaresAsync(MesasContexto ctx, TrocarLugaresRequest req, CancellationToken ct) =>
        comandos.Executar(ctx, "trocarLugares", req.Chave, req, async op =>
        {
            if (req.PessoaIdA is not Guid a || req.PessoaIdB is not Guid b || a == b) throw MesasException.Validacao("Escolha duas pessoas.");
            await op.ExigirPontoSeGarcom();
            var (pa, mesas) = await TravarPessoa(op, a);
            var pb = await op.C.QueryFirstOrDefaultAsync<PessoaRow>(op.Sql(PessoaSelect + " WHERE id = @b AND estabelecimento_id = @loja",
                new { b, loja = ctx.Loja })) ?? throw MesasException.NaoEncontrado("Cliente não encontrado.");
            if (pb.SessaoId != pa.SessaoId || pb.SaiuEm is not null) throw MesasException.Validacao("As duas pessoas precisam estar na mesma mesa.");
            await op.Autonomia("cadeira", null, mesas[0].MesaId, pa.SessaoId, $"Trocar {pa.Rotulo} e {pb.Rotulo} de lugar", req);
            if (pa.CadeiraId == pb.CadeiraId) return new ComandoDto(Guid.Empty, op.Versoes);

            // Cadeira é única na sessão: solta a de A antes de trocar.
            await op.C.ExecuteAsync(op.Sql("UPDATE mesa_pessoa SET cadeira_id = NULL WHERE id = @a", new { a }));
            await op.C.ExecuteAsync(op.Sql("UPDATE mesa_pessoa SET cadeira_id = @c WHERE id = @b", new { c = pa.CadeiraId, b }));
            await op.C.ExecuteAsync(op.Sql("UPDATE mesa_pessoa SET cadeira_id = @c WHERE id = @a", new { c = pb.CadeiraId, a }));
            await op.TocarTodas(mesas);
            var eventoId = await op.Evento("trocarLugares", "pessoa", pa.Id, $"{pa.Rotulo} ↔ {pb.Rotulo}", pa.CadeiraId, pb.CadeiraId,
                mesaId: mesas[0].MesaId, sessaoId: pa.SessaoId);
            return new ComandoDto(eventoId, op.Versoes);
        }, ct);

    public Task<ComandoDto> MarcarAlergiaAsync(MesasContexto ctx, MarcarAlergiaRequest req, CancellationToken ct) =>
        comandos.Executar(ctx, "marcarAlergia", req.Chave, req, async op =>
        {
            var pessoaId = req.PessoaId ?? throw MesasException.Validacao("Escolha o cliente.");
            var texto = Texto(req.Texto, 200, "Alergia");
            await op.ExigirPontoSeGarcom();
            var (p, mesas) = await TravarPessoa(op, pessoaId);
            await op.C.ExecuteAsync(op.Sql("UPDATE mesa_pessoa SET alergia = @texto WHERE id = @id", new { texto, id = p.Id }));
            await op.TocarTodas(mesas);
            var eventoId = await op.Evento("marcarAlergia", "pessoa", p.Id, p.Rotulo, depois: texto ?? "Sem alergia", mesaId: mesas[0].MesaId, sessaoId: p.SessaoId);
            return new ComandoDto(eventoId, op.Versoes);
        }, ct);

    public Task<AnotacaoDto> AnotarMesaAsync(MesasContexto ctx, AnotarMesaRequest req, CancellationToken ct) =>
        comandos.Executar(ctx, "anotarMesa", req.Chave, req, async op =>
        {
            var mesaId = req.MesaId ?? throw MesasException.Validacao("Escolha a mesa.");
            var tipo = req.Tipo?.Trim();
            if (tipo is not ("demora" or "comida" or "atendimento" or "elogio")) throw MesasException.Validacao("Escolha o tipo da anotação.");
            var texto = Texto(req.Texto, 200, "Anotação");
            await op.ExigirPontoSeGarcom();
            var (m, mesas) = await TravarComSessao(op, mesaId);
            if (!R.Aberta(m.Status)) throw EstadoInvalido($"A {m.Rotulo} não está aberta.");
            var id = Guid.NewGuid();
            await op.C.ExecuteAsync(op.Sql("""
                INSERT INTO mesa_anotacao (id, estabelecimento_id, sessao_id, tipo, por, texto, criado_em) VALUES (@id, @loja, @s, @tipo, @por, @texto, @agora)
                """, new { id, loja = ctx.Loja, s = m.SessaoId, tipo, por = op.AtorNome, texto, agora = op.Agora }));
            await op.TocarTodas(mesas);
            var eventoId = await op.Evento("anotarMesa", "mesa", m.MesaId, m.Rotulo, depois: texto is null ? tipo : tipo + ": " + texto,
                mesaId: m.MesaId, sessaoId: m.SessaoId);
            return new AnotacaoDto(eventoId, op.Versoes, id);
        }, ct);

    // ---------- garçom, transferir, juntar, separar ----------

    public Task<ComandoDto> TrocarGarcomAsync(MesasContexto ctx, GarcomMesaRequest req, bool atender, CancellationToken ct) =>
        comandos.Executar(ctx, atender ? "atenderMesa" : "atribuirGarcom", req.Chave, req, async op =>
        {
            var mesaId = req.MesaId ?? throw MesasException.Validacao("Escolha a mesa.");
            var eu = await op.ExigirPontoSeGarcom();
            var garcomId = req.GarcomId ?? (atender ? eu : null) ?? throw MesasException.Validacao("Escolha o garçom.");
            if (atender && ctx.Papel == MesasPermissoes.PapelGarcom && garcomId != eu)
                throw MesasException.SemPermissao("Para passar a mesa a um colega, use Transferir.");

            var (m, mesas) = await TravarComSessao(op, mesaId);
            if (!R.Aberta(m.Status)) throw EstadoInvalido($"A {m.Rotulo} não está aberta.");
            var s = await Sessao(op, m.SessaoId!.Value);
            if (s.GarcomId == garcomId)
                throw JaFeito($"{R.PrimeiroNome(s.GarcomNome)} já atende a {m.Rotulo}.", null, null, new { garcomId });
            await op.ConferirVersao(m, req.VersaoMesa);
            var nome = await op.GarcomComPonto(garcomId);

            await op.C.ExecuteAsync(op.Sql("UPDATE mesa_sessao SET garcom_id = @garcomId WHERE id = @id", new { garcomId, id = s.Id }));
            await op.TocarTodas(mesas);
            var eventoId = await op.Evento(atender ? "atenderMesa" : "atribuirGarcom", "mesa", m.MesaId, m.Rotulo, s.GarcomNome ?? "Sem garçom", nome,
                mesaId: m.MesaId, sessaoId: s.Id);
            Pendencias(op, garcomId);
            Pendencias(op, s.GarcomId);
            return new ComandoDto(eventoId, op.Versoes);
        }, ct);

    public Task<MesaTransferidaDto> TransferirMesaAsync(MesasContexto ctx, TransferirMesaRequest req, CancellationToken ct) =>
        comandos.Executar(ctx, "transferirMesa", req.Chave, req, async op =>
        {
            var mesaId = req.MesaId ?? throw MesasException.Validacao("Escolha a mesa.");
            if (req.ParaMesaId is null && req.ParaGarcomId is null) throw MesasException.Validacao("Escolha a mesa ou o garçom de destino.");
            if (req.ParaMesaId == mesaId) throw MesasException.Validacao("Escolha outra mesa.");
            var motivo = Texto(req.Motivo, 280, "Motivo") ?? throw new MesasException("EXIGE_MOTIVO", "Diga por que vai transferir.", 422);
            await op.ExigirPontoSeGarcom();

            var sessaoAntes = await SessaoDaMesa(op, mesaId);
            var travar = new List<Guid> { mesaId };
            if (req.ParaMesaId is Guid pm) travar.Add(pm);
            if (sessaoAntes is Guid sa) travar.AddRange(await MesasDaSessao(op, sa));
            var todas = await op.Travar(travar);
            var m = todas.First(x => x.MesaId == mesaId);
            if (m.SessaoId != sessaoAntes) throw Mudou(m);
            if (!R.Aberta(m.Status)) throw EstadoInvalido($"A {m.Rotulo} não está aberta.");
            await op.ConferirVersao(m, req.VersaoMesa);
            var s = await Sessao(op, m.SessaoId!.Value);
            var daSessao = todas.Where(x => x.SessaoId == s.Id).ToList();
            await op.Autonomia(req.ParaMesaId is null ? "passarMesa" : "trocarMesa", motivo, m.MesaId, s.Id, $"Transferir a {m.Rotulo}", req);

            var garcomNome = s.GarcomNome;
            if (req.ParaGarcomId is Guid pg && pg != s.GarcomId)
            {
                garcomNome = await op.GarcomComPonto(pg);
                await op.C.ExecuteAsync(op.Sql("UPDATE mesa_sessao SET garcom_id = @pg WHERE id = @id", new { pg, id = s.Id }));
                Pendencias(op, pg);
                Pendencias(op, s.GarcomId);
            }

            var final = m;
            if (req.ParaMesaId is Guid paraId)
            {
                var d = todas.First(x => x.MesaId == paraId);
                if (!R.PodeAbrir(d.Status) || d.SessaoId is not null) throw EstadoInvalido($"A {d.Rotulo} não está livre.");
                // Mesa juntada: só esta mesa muda de lugar; as outras continuam juntas. Se era a principal, a nova vira a principal.
                if (s.MesaId == m.MesaId)
                {
                    await op.C.ExecuteAsync(op.Sql("UPDATE mesa_sessao SET mesa_id = @d WHERE id = @s", new { d = d.MesaId, s = s.Id }));
                    await op.C.ExecuteAsync(op.Sql("UPDATE mesa_comanda SET nome = @nome WHERE sessao_id = @s AND tipo = 'coletiva'",
                        new { nome = d.Rotulo, s = s.Id }));
                }
                await op.C.ExecuteAsync(op.Sql("UPDATE mesa_sessao_mesa SET ate = @agora WHERE sessao_id = @s AND mesa_id = @m AND ate IS NULL",
                    new { agora = op.Agora, s = s.Id, m = m.MesaId }));
                await InserirMesaNaSessao(op, s.Id, d.MesaId);
                var pessoas = await op.C.QueryAsync<PessoaRow>(op.Sql(PessoaSelect + " WHERE sessao_id = @s AND saiu_em IS NULL AND cadeira_id LIKE @prefixo",
                    new { s = s.Id, prefixo = m.Chave.Replace("%", "\\%").Replace("_", "\\_") + ":c%" }));
                foreach (var p in pessoas)
                    await op.C.ExecuteAsync(op.Sql("UPDATE mesa_pessoa SET cadeira_id = @c WHERE id = @id",
                        new { c = R.CadeiraNaMesa(p.CadeiraId, d.Chave, d.Capacidade), id = p.Id }));
                var status = m.Status;
                await op.Mudar(m, R.AguardandoLimpeza, null);
                await op.Mudar(d, status, s.Id);
                foreach (var outra in daSessao.Where(x => x.MesaId != m.MesaId)) await op.Tocar(outra);
                final = d;
            }
            else await op.TocarTodas(daSessao);

            var antes = req.ParaMesaId is null ? s.GarcomNome ?? "Sem garçom" : m.Rotulo;
            var depois = req.ParaMesaId is null ? garcomNome : final.Rotulo + (req.ParaGarcomId is null ? "" : " · " + garcomNome);
            var eventoId = await op.Evento("transferirMesa", "mesa", m.MesaId, m.Rotulo, antes, depois, motivo, final.MesaId, s.Id);
            return new MesaTransferidaDto(eventoId, op.Versoes, final.MesaId);
        }, ct);

    public Task<MesasJuntadasDto> JuntarMesasAsync(MesasContexto ctx, JuntarMesasRequest req, CancellationToken ct) =>
        comandos.Executar(ctx, "juntarMesas", req.Chave, req, async op =>
        {
            var ids = (req.MesaIds ?? Array.Empty<Guid>()).Distinct().ToList();
            if (ids.Count < 2) throw MesasException.Validacao("Escolha pelo menos duas mesas.");
            if (ids.Count > 12) throw MesasException.Validacao("Mesas demais para juntar.");
            var eu = await op.ExigirPontoSeGarcom();

            var sessoesAntes = new Dictionary<Guid, Guid?>();
            var travar = new List<Guid>(ids);
            foreach (var id in ids)
            {
                sessoesAntes[id] = await SessaoDaMesa(op, id);
                if (sessoesAntes[id] is Guid s) travar.AddRange(await MesasDaSessao(op, s));
            }
            var todas = await op.Travar(travar);
            var escolhidas = ids.Select(id => todas.First(x => x.MesaId == id)).ToList();
            foreach (var m in escolhidas) if (m.SessaoId != sessoesAntes[m.MesaId]) throw Mudou(m);

            var sessoes = escolhidas.Where(m => m.SessaoId is not null).Select(m => m.SessaoId!.Value).Distinct().ToList();
            if (sessoes.Count == 1 && escolhidas.All(m => m.SessaoId == sessoes[0]))
                throw JaFeito("Essas mesas já estão juntas.", null, null, new { sessaoId = sessoes[0] });
            foreach (var m in escolhidas)
            {
                if (m.Status == R.Indisponivel) throw EstadoInvalido($"A {m.Rotulo} está fora de uso.");
                if (m.Status == R.AguardandoLimpeza) throw EstadoInvalido($"A {m.Rotulo} está esperando limpeza.");
                if (m.SessaoId is Guid s && todas.Count(x => x.SessaoId == s) > 1) throw EstadoInvalido($"A {m.Rotulo} já está junta com outra.");
            }
            foreach (var m in escolhidas)
            {
                var v = req.Versoes is not null && req.Versoes.TryGetValue(m.MesaId, out var vv) ? vv : req.VersaoMesa;
                await op.ConferirVersao(m, v);
            }
            var principal = escolhidas.FirstOrDefault(m => R.Aberta(m.Status)) ?? escolhidas[0];
            await op.Autonomia("juntarMesas", null, principal.MesaId, principal.SessaoId, "Juntar " + string.Join(" + ", escolhidas.Select(x => x.Rotulo)), req);

            Guid sessaoId, coletiva;
            if (principal.SessaoId is Guid ps)
            {
                sessaoId = ps;
                coletiva = await Coletiva(op, ps);
            }
            else
            {
                var garcomId = ctx.Papel == MesasPermissoes.PapelGarcom ? eu : null;
                if (garcomId is Guid g) await op.GarcomComPonto(g);
                var aberta = await AbrirSessao(op, principal, garcomId, await op.ConfigTexto("abertura.tipoComandaPadrao") ?? "mesa", 0, null);
                await op.Mudar(principal, R.Ocupada, aberta.SessaoId);
                sessaoId = aberta.SessaoId; coletiva = aberta.ColetivaId;
            }

            foreach (var m in escolhidas.Where(x => x.MesaId != principal.MesaId))
            {
                if (m.SessaoId is Guid outra)
                {
                    var colOutra = await Coletiva(op, outra);
                    foreach (var tabela in new[] { "mesa_pessoa", "mesa_chamado", "mesa_anotacao" })
                        await op.C.ExecuteAsync(op.Sql($"UPDATE {tabela} SET sessao_id = @para WHERE sessao_id = @de", new { para = sessaoId, de = outra }));
                    await op.C.ExecuteAsync(op.Sql("UPDATE mesa_comanda SET sessao_id = @para WHERE sessao_id = @de AND tipo = 'individual'",
                        new { para = sessaoId, de = outra }));
                    await op.C.ExecuteAsync(op.Sql("""
                        UPDATE mesa_comanda SET aberta = FALSE, fechada_em = @agora, fechada_por = @por WHERE id = @id AND aberta
                        """, new { agora = op.Agora, por = op.AtorNome, id = colOutra }));
                    foreach (var g in ganchos) await g.AoJuntar(op.C, op.Tx, ctx.Loja, outra, sessaoId, colOutra, coletiva, op.Ct);
                    await op.C.ExecuteAsync(op.Sql("""
                        UPDATE mesa_sessao
                           SET ocupantes_anonimos = LEAST(200, ocupantes_anonimos + (SELECT ocupantes_anonimos FROM mesa_sessao WHERE id = @de))
                         WHERE id = @para
                        """, new { para = sessaoId, de = outra }));
                    await op.C.ExecuteAsync(op.Sql("""
                        UPDATE mesa_sessao SET estado = 'encerrada', encerrada_em = @agora, encerrada_por = @por, unida_a = @para WHERE id = @de
                        """, new { agora = op.Agora, por = op.AtorNome, para = sessaoId, de = outra }));
                    await op.C.ExecuteAsync(op.Sql("UPDATE mesa_sessao_mesa SET ate = @agora WHERE sessao_id = @de AND ate IS NULL",
                        new { agora = op.Agora, de = outra }));
                }
                await InserirMesaNaSessao(op, sessaoId, m.MesaId);
                await op.Mudar(m, principal.Status, sessaoId);
            }
            await op.Tocar(principal);
            var eventoId = await op.Evento("juntarMesas", "mesa", principal.MesaId, string.Join(" + ", escolhidas.Select(x => x.Rotulo)),
                mesaId: principal.MesaId, sessaoId: sessaoId);
            return new MesasJuntadasDto(eventoId, op.Versoes, sessaoId, principal.MesaId);
        }, ct);

    public Task<ComandoDto> SepararMesasAsync(MesasContexto ctx, MesaRequest req, CancellationToken ct) =>
        comandos.Executar(ctx, "separarMesas", req.Chave, req, async op =>
        {
            var mesaId = req.MesaId ?? throw MesasException.Validacao("Escolha a mesa.");
            await op.ExigirPontoSeGarcom();
            var (m, mesas) = await TravarComSessao(op, mesaId);
            if (m.SessaoId is null || mesas.Count < 2) throw JaFeito($"A {m.Rotulo} não está junta.", null, null, null);
            await op.ConferirVersao(m, req.VersaoMesa);
            var s = await Sessao(op, m.SessaoId.Value);
            await op.Autonomia("juntarMesas", null, m.MesaId, s.Id, "Separar a " + m.Rotulo, req);

            var principal = mesas.First(x => x.MesaId == s.MesaId);
            var outras = mesas.Where(x => x.MesaId != s.MesaId).ToList();
            foreach (var o in outras)
            {
                await op.C.ExecuteAsync(op.Sql("UPDATE mesa_sessao_mesa SET ate = @agora WHERE sessao_id = @s AND mesa_id = @m AND ate IS NULL",
                    new { agora = op.Agora, s = s.Id, m = o.MesaId }));
                await op.Mudar(o, R.AguardandoLimpeza, null);
            }
            await op.Tocar(principal);
            var eventoId = await op.Evento("separarMesas", "mesa", principal.MesaId, string.Join(" / ", new[] { principal }.Concat(outras).Select(x => x.Rotulo)),
                mesaId: principal.MesaId, sessaoId: s.Id);
            return new ComandoDto(eventoId, op.Versoes);
        }, ct);

    // ---------- conta pedida, encerrar, reabrir, liberar ----------

    public Task<ComandoDto> PedirContaAsync(MesasContexto ctx, MesaRequest req, CancellationToken ct) =>
        comandos.Executar(ctx, "pedirConta", req.Chave, req, async op =>
        {
            var mesaId = req.MesaId ?? throw MesasException.Validacao("Escolha a mesa.");
            await op.ExigirPontoSeGarcom();
            var (m, mesas) = await TravarComSessao(op, mesaId);
            if (!R.Aberta(m.Status)) throw EstadoInvalido($"A {m.Rotulo} não está aberta.");
            if (m.Status == R.AguardandoPagamento) return new ComandoDto(Guid.Empty, op.Versoes); // sem efeito
            var s = await Sessao(op, m.SessaoId!.Value);
            await op.C.ExecuteAsync(op.Sql("UPDATE mesa_sessao SET conta_pedida_em = @agora WHERE id = @id", new { agora = op.Agora, id = s.Id }));
            foreach (var x in mesas) await op.Mudar(x, R.AguardandoPagamento, s.Id);
            var eventoId = await op.Evento("pedirConta", "mesa", s.MesaId, m.Rotulo, mesaId: s.MesaId, sessaoId: s.Id);
            var loja = ctx.Loja;
            op.Avisar(t => t.Aviso(loja, "conta", s.Id, s.MesaId, s.GarcomId));
            Pendencias(op, s.GarcomId);
            return new ComandoDto(eventoId, op.Versoes);
        }, ct);

    public Task<ComandoDto> EncerrarAsync(MesasContexto ctx, EncerrarRequest req, CancellationToken ct) =>
        comandos.Executar(ctx, "encerrar", req.Chave, req, async op =>
        {
            var mesaId = req.MesaId ?? throw MesasException.Validacao("Escolha a mesa.");
            var motivo = Texto(req.Motivo, 280, "Motivo");
            var forcar = req.Forcar == true;
            if (forcar && ctx.Papel is not (MesasPermissoes.PapelDono or MesasPermissoes.PapelGerente))
                throw MesasException.SemPermissao("Encerrar com saldo em aberto precisa do gerente.");
            if (forcar && motivo is null) throw new MesasException("EXIGE_MOTIVO", "Diga por que vai encerrar com saldo.", 422);
            await op.ExigirPontoSeGarcom();

            var (m, mesas) = await TravarComSessao(op, mesaId);
            if (m.Status == R.AguardandoLimpeza) throw await JaFeitoPorEvento(op, m, "encerrar", $"A {m.Rotulo} já foi encerrada");
            if (!R.Aberta(m.Status)) throw EstadoInvalido($"A {m.Rotulo} não está aberta.");
            await op.ConferirVersao(m, req.VersaoMesa);
            var s = await Sessao(op, m.SessaoId!.Value);
            foreach (var g in ganchos) await g.AntesDeEncerrar(op.C, op.Tx, ctx, s.Id, forcar, motivo, op.Ct);

            var a = new { agora = op.Agora, por = op.AtorNome, s = s.Id };
            await op.C.ExecuteAsync(op.Sql("UPDATE mesa_comanda SET aberta = FALSE, fechada_em = @agora, fechada_por = @por WHERE sessao_id = @s AND aberta", a));
            await op.C.ExecuteAsync(op.Sql("UPDATE mesa_pessoa SET saiu_em = @agora WHERE sessao_id = @s AND saiu_em IS NULL", a));
            await op.C.ExecuteAsync(op.Sql("""
                UPDATE mesa_chamado SET estado = 'atendido', atendido_em = @agora, atendido_por = @por WHERE sessao_id = @s AND estado IN ('aberto', 'a_caminho')
                """, a));
            await op.C.ExecuteAsync(op.Sql("UPDATE mesa_sessao SET estado = 'encerrada', encerrada_em = @agora, encerrada_por = @por WHERE id = @s", a));
            await op.C.ExecuteAsync(op.Sql("UPDATE mesa_sessao_mesa SET ate = @agora WHERE sessao_id = @s AND ate IS NULL", a));
            foreach (var x in mesas) await op.Mudar(x, R.AguardandoLimpeza, null);
            var eventoId = await op.Evento("encerrar", "mesa", s.MesaId, string.Join(" + ", mesas.Select(x => x.Rotulo)), Capital(R.Rotulo(m.Status)),
                "Esperando limpeza", motivo, s.MesaId, s.Id, ocorrencia: forcar);
            Pendencias(op, s.GarcomId);
            return new ComandoDto(eventoId, op.Versoes);
        }, ct);

    public Task<ComandoDto> ReabrirAsync(MesasContexto ctx, ReabrirRequest req, CancellationToken ct) =>
        comandos.Executar(ctx, "reabrir", req.Chave, req, async op =>
        {
            var mesaId = req.MesaId ?? throw MesasException.Validacao("Escolha a mesa.");
            var motivo = Texto(req.Motivo, 280, "Motivo") ?? throw new MesasException("EXIGE_MOTIVO", "Diga por que vai reabrir.", 422);

            // A última sessão que esta mesa deixou ao ser encerrada (não por transferência, separação ou junção).
            var s = await op.C.QueryFirstOrDefaultAsync<SessaoRow>(op.Sql(SessaoSelect + """
                 JOIN mesa_sessao_mesa sm ON sm.sessao_id = s.id AND sm.mesa_id = @mesaId
                WHERE s.estabelecimento_id = @loja AND s.estado = 'encerrada' AND s.unida_a IS NULL AND sm.ate = s.encerrada_em
                  AND NOT EXISTS (SELECT 1 FROM mesa_sessao_mesa x WHERE x.mesa_id = @mesaId AND x.desde > sm.desde)
                ORDER BY sm.ate DESC LIMIT 1
                """, new { mesaId, loja = ctx.Loja }));
            var ids = new List<Guid> { mesaId };
            if (s is not null)
                ids.AddRange(await op.C.QueryAsync<Guid>(op.Sql("SELECT mesa_id FROM mesa_sessao_mesa WHERE sessao_id = @s AND ate = @fim",
                    new { s = s.Id, fim = s.EncerradaEm })));
            var mesas = await op.Travar(ids);
            var m = mesas.First(x => x.MesaId == mesaId);
            if (R.Aberta(m.Status) && s is not null && m.SessaoId == s.Id) throw JaFeito($"A {m.Rotulo} já foi reaberta.", null, null, null);
            await op.ConferirVersao(m, req.VersaoMesa);
            if (m.Status != R.AguardandoLimpeza || s is null || !mesas.Any(x => x.MesaId == s.MesaId))
                throw EstadoInvalido("Só dá para reabrir uma mesa que acabou de ser encerrada.");
            var usada = mesas.FirstOrDefault(x => x.Status != R.AguardandoLimpeza);
            if (usada is not null) throw EstadoInvalido($"A {usada.Rotulo} já foi usada de novo.");
            foreach (var g in ganchos) await g.AoReabrir(op.C, op.Tx, ctx, s.Id, op.Ct);

            var a = new { fim = s.EncerradaEm, s = s.Id };
            await op.C.ExecuteAsync(op.Sql("""
                UPDATE mesa_sessao SET estado = 'aberta', encerrada_em = NULL, encerrada_por = NULL, reaberta_vezes = reaberta_vezes + 1 WHERE id = @s
                """, a));
            await op.C.ExecuteAsync(op.Sql("""
                UPDATE mesa_comanda SET aberta = TRUE, fechada_em = NULL, fechada_por = NULL WHERE sessao_id = @s AND fechada_em = @fim AND fechamento IS NULL
                """, a));
            await op.C.ExecuteAsync(op.Sql("UPDATE mesa_pessoa SET saiu_em = NULL WHERE sessao_id = @s AND saiu_em = @fim", a));
            await op.C.ExecuteAsync(op.Sql("UPDATE mesa_sessao_mesa SET ate = NULL WHERE sessao_id = @s AND ate = @fim", a));
            var status = s.ContaPedidaEm is null ? R.Ocupada : R.AguardandoPagamento;
            foreach (var x in mesas) await op.Mudar(x, status, s.Id);
            var eventoId = await op.Evento("reabrir", "mesa", s.MesaId, string.Join(" + ", mesas.Select(x => x.Rotulo)), "Esperando limpeza",
                Capital(R.Rotulo(status)), motivo, s.MesaId, s.Id, ocorrencia: true);
            Pendencias(op, s.GarcomId);
            return new ComandoDto(eventoId, op.Versoes);
        }, ct);

    public Task<ComandoDto> LiberarLimpezaAsync(MesasContexto ctx, MesaRequest req, CancellationToken ct) =>
        comandos.Executar(ctx, "liberarLimpeza", req.Chave, req, async op =>
        {
            var mesaId = req.MesaId ?? throw MesasException.Validacao("Escolha a mesa.");
            await op.ExigirPontoSeGarcom();
            var m = await op.Travar(mesaId);
            // Livre depois de uma limpeza = alguém já liberou (JA_FEITO); livre sem limpeza = não havia o que liberar.
            if (m.Status == R.Livre && await op.C.ExecuteScalarAsync<bool>(op.Sql("""
                    SELECT EXISTS (SELECT 1 FROM mesa_evento WHERE estabelecimento_id = @loja AND mesa_id = @m AND acao = 'liberarLimpeza')
                    """, new { loja = ctx.Loja, m = m.MesaId })))
                throw await JaFeitoPorEvento(op, m, "liberarLimpeza", $"A {m.Rotulo} já foi liberada");
            if (m.Status != R.AguardandoLimpeza) throw EstadoInvalido($"A {m.Rotulo} não está esperando limpeza.");
            await op.ConferirVersao(m, req.VersaoMesa);
            await op.Mudar(m, R.Livre, null);
            var eventoId = await op.Evento("liberarLimpeza", "mesa", m.MesaId, m.Rotulo, "Esperando limpeza", "Livre", mesaId: m.MesaId);
            return new ComandoDto(eventoId, op.Versoes);
        }, ct);

    // ---------- chamados ----------

    public Task<ChamadoCriadoDto> ChamarGarcomAsync(MesasContexto ctx, ChamarGarcomRequest req, CancellationToken ct) =>
        comandos.Executar(ctx, "chamarGarcom", req.Chave, req, async op =>
        {
            var mesaId = req.MesaId ?? throw MesasException.Validacao("Escolha a mesa.");
            var tipo = req.Tipo?.Trim() ?? "garcom";
            if (tipo is not ("garcom" or "conta" or "contestacao" or "outro")) throw MesasException.Validacao("Tipo de chamado inválido.");
            var motivo = Texto(req.Motivo, 140, "Recado");
            var (m, mesas) = await TravarComSessao(op, mesaId);
            if (!R.Aberta(m.Status)) throw EstadoInvalido($"A {m.Rotulo} não está aberta.");
            var s = await Sessao(op, m.SessaoId!.Value);
            if (req.PessoaId is Guid pid && !await op.C.ExecuteScalarAsync<bool>(op.Sql(
                    "SELECT EXISTS (SELECT 1 FROM mesa_pessoa WHERE id = @pid AND sessao_id = @s AND saiu_em IS NULL)", new { pid, s = s.Id })))
                throw MesasException.NaoEncontrado("Cliente não encontrado.");
            var aberto = await op.C.QueryFirstOrDefaultAsync<ChamadoRow>(op.Sql(ChamadoSelect + """
                 WHERE sessao_id = @s AND estado IN ('aberto', 'a_caminho') AND pessoa_id IS NOT DISTINCT FROM @pid LIMIT 1
                """, new { s = s.Id, pid = req.PessoaId }));
            if (aberto is not null)
                throw JaFeito($"O garçom já foi chamado às {Hora(aberto.CriadoEm)}.", null, aberto.CriadoEm, new { chamadoId = aberto.Id });

            var id = Guid.NewGuid();
            await op.C.ExecuteAsync(op.Sql("""
                INSERT INTO mesa_chamado (id, estabelecimento_id, sessao_id, pessoa_id, tipo, motivo, garcom_id, criado_por, criado_em)
                VALUES (@id, @loja, @s, @pid, @tipo, @motivo, @g, @por, @agora)
                """, new { id, loja = ctx.Loja, s = s.Id, pid = req.PessoaId, tipo, motivo, g = s.GarcomId, por = op.AtorNome, agora = op.Agora }));
            await op.TocarTodas(mesas);
            var eventoId = await op.Evento("chamarGarcom", "chamado", id, m.Rotulo, depois: motivo ?? "Chamar garçom", mesaId: s.MesaId, sessaoId: s.Id);
            var loja = ctx.Loja;
            op.Avisar(t => t.Aviso(loja, "chamado", id, s.MesaId, s.GarcomId));
            Pendencias(op, s.GarcomId);
            return new ChamadoCriadoDto(eventoId, op.Versoes, id);
        }, ct);

    public Task<ComandoDto> CaminhoChamadoAsync(MesasContexto ctx, ChamadoRequest req, CancellationToken ct) =>
        comandos.Executar(ctx, "caminhoChamado", req.Chave, req, async op =>
        {
            var eu = await op.ExigirPontoSeGarcom();
            var (ch, mesas) = await TravarChamado(op, req.ChamadoId);
            if (ch.Estado == "a_caminho") throw JaFeito($"{R.PrimeiroNome(ch.ACaminhoPor)} já está indo, desde as {Hora(ch.ACaminhoEm!.Value)}.", ch.ACaminhoPor, ch.ACaminhoEm, null);
            if (ch.Estado == "atendido") throw JaFeito($"{R.PrimeiroNome(ch.AtendidoPor)} já atendeu às {Hora(ch.AtendidoEm!.Value)}.", ch.AtendidoPor, ch.AtendidoEm, null);
            if (ch.Estado != "aberto") throw EstadoInvalido("Esse chamado foi cancelado.");
            await op.C.ExecuteAsync(op.Sql("""
                UPDATE mesa_chamado SET estado = 'a_caminho', a_caminho_em = @agora, a_caminho_por = @por, garcom_id = COALESCE(@eu, garcom_id) WHERE id = @id
                """, new { agora = op.Agora, por = op.AtorNome, eu, id = ch.Id }));
            await op.TocarTodas(mesas);
            var eventoId = await op.Evento("caminhoChamado", "chamado", ch.Id, mesas[0].Rotulo, depois: "A caminho", mesaId: mesas[0].MesaId, sessaoId: ch.SessaoId);
            Pendencias(op, eu ?? ch.GarcomId);
            return new ComandoDto(eventoId, op.Versoes);
        }, ct);

    public Task<ComandoDto> PassarChamadoAsync(MesasContexto ctx, ChamadoRequest req, CancellationToken ct) =>
        comandos.Executar(ctx, "passarChamado", req.Chave, req, async op =>
        {
            var garcomId = req.GarcomId ?? throw MesasException.Validacao("Escolha o garçom.");
            await op.ExigirPontoSeGarcom();
            var (ch, mesas) = await TravarChamado(op, req.ChamadoId);
            if (ch.Estado == "atendido") throw JaFeito($"{R.PrimeiroNome(ch.AtendidoPor)} já atendeu às {Hora(ch.AtendidoEm!.Value)}.", ch.AtendidoPor, ch.AtendidoEm, null);
            if (ch.Estado == "cancelado") throw EstadoInvalido("Esse chamado foi cancelado.");
            if (ch.GarcomId == garcomId) throw JaFeito("Esse chamado já é desse garçom.", null, null, null);
            var nome = await op.GarcomComPonto(garcomId);
            await op.C.ExecuteAsync(op.Sql("UPDATE mesa_chamado SET garcom_id = @garcomId, estado = 'aberto', a_caminho_em = NULL, a_caminho_por = NULL WHERE id = @id",
                new { garcomId, id = ch.Id }));
            await op.TocarTodas(mesas);
            var eventoId = await op.Evento("passarChamado", "chamado", ch.Id, mesas[0].Rotulo, depois: nome, mesaId: mesas[0].MesaId, sessaoId: ch.SessaoId);
            var loja = ctx.Loja;
            op.Avisar(t => t.Aviso(loja, "chamado", ch.Id, mesas[0].MesaId, garcomId));
            Pendencias(op, garcomId);
            Pendencias(op, ch.GarcomId);
            return new ComandoDto(eventoId, op.Versoes);
        }, ct);

    public Task<ChamadosAtendidosDto> AtenderChamadoAsync(MesasContexto ctx, AtenderChamadoRequest req, CancellationToken ct) =>
        comandos.Executar(ctx, "atenderChamado", req.Chave, req, async op =>
        {
            await op.ExigirPontoSeGarcom();
            IReadOnlyList<MesaTravada> mesas;
            Guid sessaoId;
            if (req.ChamadoId is not null)
            {
                var (ch, ms) = await TravarChamado(op, req.ChamadoId);
                mesas = ms; sessaoId = ch.SessaoId;
            }
            else
            {
                var mesaId = req.MesaId ?? throw MesasException.Validacao("Escolha o chamado ou a mesa.");
                var (m, ms) = await TravarComSessao(op, mesaId);
                if (m.SessaoId is null) throw EstadoInvalido("Nenhum chamado aberto.");
                mesas = ms; sessaoId = m.SessaoId.Value;
            }
            var abertos = (await op.C.QueryAsync<ChamadoRow>(op.Sql(ChamadoSelect + " WHERE sessao_id = @s AND estado IN ('aberto', 'a_caminho')" +
                (req.ChamadoId is null ? "" : " AND id = @id"), new { s = sessaoId, id = req.ChamadoId }))).ToList();
            if (abertos.Count == 0)
            {
                var ultimo = await op.C.QueryFirstOrDefaultAsync<ChamadoRow>(op.Sql(ChamadoSelect + " WHERE sessao_id = @s AND estado = 'atendido'" +
                    (req.ChamadoId is null ? "" : " AND id = @id") + " ORDER BY atendido_em DESC LIMIT 1", new { s = sessaoId, id = req.ChamadoId }));
                if (ultimo is not null)
                    throw JaFeito($"{R.PrimeiroNome(ultimo.AtendidoPor)} já atendeu às {Hora(ultimo.AtendidoEm!.Value)}.", ultimo.AtendidoPor, ultimo.AtendidoEm, null);
                throw EstadoInvalido("Nenhum chamado aberto.");
            }
            await op.C.ExecuteAsync(op.Sql("UPDATE mesa_chamado SET estado = 'atendido', atendido_em = @agora, atendido_por = @por WHERE id = ANY(@ids)",
                new { agora = op.Agora, por = op.AtorNome, ids = abertos.Select(x => x.Id).ToArray() }));
            await op.TocarTodas(mesas);
            var eventoId = await op.Evento("atenderChamado", "chamado", abertos[0].Id, mesas[0].Rotulo,
                depois: string.Join(", ", abertos.Select(x => x.Motivo ?? "Chamar garçom")), mesaId: mesas[0].MesaId, sessaoId: sessaoId);
            foreach (var g in abertos.Select(x => x.GarcomId).Distinct()) Pendencias(op, g);
            return new ChamadosAtendidosDto(eventoId, op.Versoes, abertos.Count);
        }, ct);

    private const string ChamadoSelect = """
        SELECT id, sessao_id AS sessaoId, pessoa_id AS pessoaId, estado, motivo, garcom_id AS garcomId, criado_em AS criadoEm,
               a_caminho_em AS aCaminhoEm, a_caminho_por AS aCaminhoPor, atendido_em AS atendidoEm, atendido_por AS atendidoPor
          FROM mesa_chamado
        """;

    // ---------- apoio ----------

    private sealed record SessaoAberta(Guid SessaoId, Guid ColetivaId, string QrToken);
    private sealed record Incluida(Guid PessoaId, Guid? ComandaId, string Nome);

    private static async Task<SessaoAberta> AbrirSessao(MesasOperacao op, MesaTravada m, Guid? garcomId, string tipo, int anonimos, Guid? reservaId)
    {
        var id = Guid.NewGuid();
        var qr = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(18)).Replace('+', '-').Replace('/', '_');
        await op.C.ExecuteAsync(op.Sql("""
            INSERT INTO mesa_sessao (id, estabelecimento_id, mesa_id, aberta_em, aberta_por, aberta_por_id, garcom_id, tipo_comanda, ocupantes_anonimos,
                                     reserva_id, qr_token)
            VALUES (@id, @loja, @mesa, @agora, @por, @porId, @garcomId, @tipo, @anonimos, @reservaId, @qr)
            """, new { id, loja = op.Ctx.Loja, mesa = m.MesaId, agora = op.Agora, por = op.AtorNome, porId = op.Ctx.Usuario.ToString(), garcomId, tipo,
            anonimos = (short)anonimos, reservaId, qr }));
        await InserirMesaNaSessao(op, id, m.MesaId);
        var coletiva = Guid.NewGuid();
        await op.C.ExecuteAsync(op.Sql("""
            INSERT INTO mesa_comanda (id, estabelecimento_id, sessao_id, tipo, nome, criado_em) VALUES (@coletiva, @loja, @id, 'coletiva', @nome, @agora)
            """, new { coletiva, loja = op.Ctx.Loja, id, nome = m.Rotulo, agora = op.Agora }));
        return new SessaoAberta(id, coletiva, qr);
    }

    private static Task InserirMesaNaSessao(MesasOperacao op, Guid sessaoId, Guid mesaId) => op.C.ExecuteAsync(op.Sql("""
        INSERT INTO mesa_sessao_mesa (id, estabelecimento_id, sessao_id, mesa_id, desde) VALUES (@id, @loja, @sessaoId, @mesaId, @agora)
        """, new { id = Guid.NewGuid(), loja = op.Ctx.Loja, sessaoId, mesaId, agora = op.Agora }));

    private static async Task<Incluida> IncluirInterno(MesasOperacao op, Guid sessaoId, string? nome, string? celular, string? cadeira, string origem,
        bool individual, bool titular, string? alergia = null)
    {
        var total = await op.C.ExecuteScalarAsync<int>(op.Sql("SELECT count(*) FROM mesa_pessoa WHERE sessao_id = @sessaoId", new { sessaoId }));
        var final = nome ?? "Cliente " + (total + 1);
        var id = Guid.NewGuid();
        await op.C.ExecuteAsync(op.Sql("""
            INSERT INTO mesa_pessoa (id, estabelecimento_id, sessao_id, nome, celular, cadeira_id, origem, entrou_em, cor, alergia, titular)
            VALUES (@id, @loja, @sessaoId, @final, @celular, @cadeira, @origem, @agora, @cor, @alergia, @titular)
            """, new { id, loja = op.Ctx.Loja, sessaoId, final, celular, cadeira, origem, agora = op.Agora, cor = R.Cor(total), alergia, titular }));
        // Quem tinha entrado só na contagem ("− 4 +") ganha nome: não soma de novo.
        await op.C.ExecuteAsync(op.Sql("UPDATE mesa_sessao SET ocupantes_anonimos = ocupantes_anonimos - 1 WHERE id = @sessaoId AND ocupantes_anonimos > 0",
            new { sessaoId }));
        Guid? comanda = null;
        if (individual && (nome is not null || celular is not null))
        {
            comanda = Guid.NewGuid();
            await op.C.ExecuteAsync(op.Sql("""
                INSERT INTO mesa_comanda (id, estabelecimento_id, sessao_id, tipo, pessoa_id, nome, criado_em)
                VALUES (@comanda, @loja, @sessaoId, 'individual', @id, @nome, @agora)
                """, new { comanda, loja = op.Ctx.Loja, sessaoId, id, nome = R.PrimeiroNome(final), agora = op.Agora }));
        }
        return new Incluida(id, comanda, final);
    }

    private static Task<Guid?> ComandaAberta(MesasOperacao op, Guid pessoaId) => op.C.QueryFirstOrDefaultAsync<Guid?>(op.Sql(
        "SELECT id FROM mesa_comanda WHERE pessoa_id = @pessoaId AND aberta LIMIT 1", new { pessoaId }));

    private static Task<Guid> Coletiva(MesasOperacao op, Guid sessaoId) => op.C.ExecuteScalarAsync<Guid>(op.Sql(
        "SELECT id FROM mesa_comanda WHERE sessao_id = @sessaoId AND tipo = 'coletiva'", new { sessaoId }));

    private static async Task<SessaoRow> Sessao(MesasOperacao op, Guid id) =>
        await op.C.QueryFirstOrDefaultAsync<SessaoRow>(op.Sql(SessaoSelect + " WHERE s.id = @id AND s.estabelecimento_id = @loja", new { id, loja = op.Ctx.Loja }))
        ?? throw MesasException.NaoEncontrado("Sessão não encontrada.");

    private static Task<Guid?> SessaoDaMesa(MesasOperacao op, Guid mesaId) => op.C.QueryFirstOrDefaultAsync<Guid?>(op.Sql(
        "SELECT sessao_id FROM mesa_estado WHERE mesa_id = @mesaId AND estabelecimento_id = @loja", new { mesaId, loja = op.Ctx.Loja }));

    private static Task<IEnumerable<Guid>> MesasDaSessao(MesasOperacao op, Guid sessaoId) => op.C.QueryAsync<Guid>(op.Sql(
        "SELECT mesa_id FROM mesa_sessao_mesa WHERE sessao_id = @sessaoId AND ate IS NULL", new { sessaoId }));

    /// <summary>Trava a mesa e todas as da sessão dela, em ordem de id. Se a sessão mudou entre ler e travar, <c>VERSAO_VELHA</c>.</summary>
    private static async Task<(MesaTravada Mesa, IReadOnlyList<MesaTravada> DaSessao)> TravarComSessao(MesasOperacao op, Guid mesaId)
    {
        var antes = await SessaoDaMesa(op, mesaId);
        var ids = new List<Guid> { mesaId };
        if (antes is Guid s) ids.AddRange(await MesasDaSessao(op, s));
        var todas = await op.Travar(ids);
        var m = todas.First(x => x.MesaId == mesaId);
        if (m.SessaoId != antes) throw Mudou(m);
        var daSessao = m.SessaoId is null ? new[] { m } : todas.Where(x => x.SessaoId == m.SessaoId).ToArray();
        // A principal primeiro (é ela que dá nome ao evento).
        if (m.SessaoId is Guid sid && daSessao.Length > 1)
        {
            var principal = await op.C.ExecuteScalarAsync<Guid>(op.Sql("SELECT mesa_id FROM mesa_sessao WHERE id = @sid", new { sid }));
            daSessao = daSessao.OrderBy(x => x.MesaId == principal ? 0 : 1).ToArray();
        }
        return (m, daSessao);
    }

    private static async Task<(PessoaRow Pessoa, IReadOnlyList<MesaTravada> Mesas)> TravarPessoa(MesasOperacao op, Guid pessoaId)
    {
        var p = await LerPessoa(op, pessoaId);
        var mesas = await op.TravarSessao(p.SessaoId);
        p = await LerPessoa(op, pessoaId); // pode ter mudado de sessão (junção) antes da trava
        if (mesas.Count == 0 || mesas.Any(x => x.SessaoId != p.SessaoId)) throw EstadoInvalido("A mesa desse cliente não está aberta.");
        if (p.SaiuEm is not null) throw EstadoInvalido($"{p.Rotulo} já saiu da mesa.");
        return (p, mesas);
    }

    private static async Task<PessoaRow> LerPessoa(MesasOperacao op, Guid pessoaId) =>
        await op.C.QueryFirstOrDefaultAsync<PessoaRow>(op.Sql(PessoaSelect + " WHERE id = @pessoaId AND estabelecimento_id = @loja",
            new { pessoaId, loja = op.Ctx.Loja })) ?? throw MesasException.NaoEncontrado("Cliente não encontrado.");

    private static async Task<(ChamadoRow Chamado, IReadOnlyList<MesaTravada> Mesas)> TravarChamado(MesasOperacao op, Guid? chamadoId)
    {
        var id = chamadoId ?? throw MesasException.Validacao("Escolha o chamado.");
        var ch = await op.C.QueryFirstOrDefaultAsync<ChamadoRow>(op.Sql(ChamadoSelect + " WHERE id = @id AND estabelecimento_id = @loja",
            new { id, loja = op.Ctx.Loja })) ?? throw MesasException.NaoEncontrado("Chamado não encontrado.");
        var mesas = await op.TravarSessao(ch.SessaoId);
        if (mesas.Count == 0) throw EstadoInvalido("A mesa desse chamado já foi encerrada.");
        ch = await op.C.QuerySingleAsync<ChamadoRow>(op.Sql(ChamadoSelect + " WHERE id = @id", new { id }));
        return (ch, mesas);
    }

    private static async Task CadeiraLivre(MesasOperacao op, Guid sessaoId, string cadeira, Guid? exceto)
    {
        var dona = await op.C.QueryFirstOrDefaultAsync<string?>(op.Sql("""
            SELECT COALESCE(nome, 'Cliente') FROM mesa_pessoa
             WHERE sessao_id = @sessaoId AND cadeira_id = @cadeira AND saiu_em IS NULL AND id IS DISTINCT FROM @exceto LIMIT 1
            """, new { sessaoId, cadeira, exceto }));
        if (dona is not null) throw JaFeito($"Essa cadeira já é de {R.PrimeiroNome(dona)}.", null, null, null);
    }

    private static void ConferirCadeiraDaSessao(string cadeira, IReadOnlyList<MesaTravada> mesas)
    {
        if (MesaDaCadeira(cadeira, mesas) is null) throw MesasException.Validacao("Essa cadeira não é desta mesa.");
    }

    private static Guid? MesaDaCadeira(string cadeira, IReadOnlyList<MesaTravada> mesas)
    {
        var i = cadeira.LastIndexOf(":c", StringComparison.Ordinal);
        if (i <= 0) return null;
        var chave = cadeira[..i];
        return mesas.FirstOrDefault(x => x.Chave == chave)?.MesaId;
    }

    private static void Pendencias(MesasOperacao op, Guid? garcomId)
    {
        var loja = op.Ctx.Loja;
        op.Avisar(t => t.PendenciasMudou(loja, "mesa", garcomId));
    }

    private static async Task<MesasException> JaFeitoPorEvento(MesasOperacao op, MesaTravada m, string acao, string frase)
    {
        var ultimo = await op.C.QueryFirstOrDefaultAsync<EventoRow>(op.Sql("""
            SELECT ator_nome AS nome, quando FROM mesa_evento WHERE estabelecimento_id = @loja AND mesa_id = @mesaId AND acao = @acao
             ORDER BY quando DESC LIMIT 1
            """, new { loja = op.Ctx.Loja, mesaId = m.MesaId, acao }));
        return ultimo is null
            ? JaFeito(frase + ".", null, null, null)
            : JaFeito($"{frase} por {R.PrimeiroNome(ultimo.Nome)} às {Hora(ultimo.Quando)}.", ultimo.Nome, ultimo.Quando, null);
    }

    private sealed class EventoRow { public string Nome { get; set; } = ""; public DateTime Quando { get; set; } }

    private static MesasException JaFeito(string mensagem, string? porQuem, DateTime? quando, object? extra) =>
        new("JA_FEITO", mensagem, 409) { Detalhes = new { conflito = true, porQuem, quando, extra } };

    private static MesasException EstadoInvalido(string mensagem) => new("ESTADO_INVALIDO", mensagem, 409);

    private static MesasException Mudou(MesaTravada m) =>
        new("VERSAO_VELHA", $"A {m.Rotulo} mudou. Confira de novo.", 409)
        {
            Detalhes = new { conflito = true, mesaId = m.MesaId, versaoAtual = m.Versao, status = m.Status, sessaoId = m.SessaoId }
        };

    private static string? Texto(string? texto, int limite, string campo)
    {
        var t = R.Texto(texto, limite, campo, out var erro);
        if (erro is not null) throw MesasException.Validacao(erro);
        return t;
    }

    private static string? Celular(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return null;
        return R.Celular(texto) ?? throw MesasException.Validacao("Celular inválido.");
    }

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static readonly TimeZoneInfo Fuso = AcharFuso();

    private static TimeZoneInfo AcharFuso()
    {
        foreach (var id in new[] { "America/Sao_Paulo", "E. South America Standard Time" })
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch (TimeZoneNotFoundException) { } catch (InvalidTimeZoneException) { }
        return TimeZoneInfo.Utc;
    }

    /// <summary>HH:MM no horário de Brasília (só para a frase; o dado vai em <c>quando</c>).</summary>
    public static string Hora(DateTime quando) =>
        TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(quando, quando.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : quando.Kind), Fuso).ToString("HH:mm");
}
