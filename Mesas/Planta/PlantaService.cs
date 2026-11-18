using System.Text;
using System.Text.Json.Nodes;
using APIBack.Mesas.Nucleo;
using Dapper;
using Npgsql;

namespace APIBack.Mesas.Planta;

public interface IPlantaService
{
    Task<PlantaDto?> LerAsync(Guid loja, string estado, int? versao, CancellationToken ct);
    Task<IReadOnlyList<PlantaVersaoDto>> VersoesAsync(Guid loja, CancellationToken ct);
    Task<PlantaAppDto?> AppAsync(Guid loja, CancellationToken ct);
    Task<RascunhoSalvoDto> SalvarRascunhoAsync(MesasContexto ctx, SalvarRascunhoPlantaRequest req, CancellationToken ct);
    Task<PlantaPublicadaDto> PublicarAsync(MesasContexto ctx, PublicarPlantaRequest req, CancellationToken ct);
    Task<RascunhoDescartadoDto> DescartarAsync(MesasContexto ctx, DescartarRascunhoPlantaRequest req, CancellationToken ct);
    Task<MesaComandoDto> BloquearAsync(MesasContexto ctx, MesaBloqueioRequest req, bool bloquear, CancellationToken ct);
}

/// <summary>
/// Planta e salão (P05). Um rascunho e uma publicada por loja. Publicar valida tudo antes de mexer: com erro, nada muda e a
/// publicada continua a mesma. O cadastro de mesas sai da planta pela chave estável (id da mesa no documento).
/// </summary>
public sealed class PlantaService(NpgsqlDataSource source, IPlantaTempoReal tempoReal) : IPlantaService
{
    private sealed class PlantaRow
    {
        public Guid Id { get; set; }
        public int Versao { get; set; }
        public int Revisao { get; set; }
        public string Estado { get; set; } = "";
        public string Documento { get; set; } = "{}";
        public string? SalvoPorNome { get; set; }
        public DateTime AtualizadoEm { get; set; }
        public DateTime? PublicadoEm { get; set; }
        public string? PublicadoPorNome { get; set; }
    }

    private sealed class MesaRow
    {
        public Guid Id { get; set; }
        public string Chave { get; set; } = "";
        public string Numero { get; set; } = "";
        public bool Ativa { get; set; }
        public bool EmUso { get; set; }
    }

    private sealed class EstadoRow
    {
        public string Numero { get; set; } = "";
        public string Status { get; set; } = "";
        public int Versao { get; set; }
    }

    private sealed class AppMesaRow
    {
        public Guid MesaId { get; set; }
        public string Chave { get; set; } = "";
        public string Numero { get; set; } = "";
        public string PisoId { get; set; } = "";
        public string? SetorId { get; set; }
        public short Capacidade { get; set; }
        public string Forma { get; set; } = "";
        public bool Reservavel { get; set; }
        public string Status { get; set; } = "";
        public int Versao { get; set; }
        public string? MotivoBloqueio { get; set; }
    }

    private const string PlantaSelect = """
        SELECT p.id, p.versao, p.revisao, p.estado, p.documento::text AS documento, s.nome::text AS salvoPorNome, p.atualizado_em AS atualizadoEm,
               p.publicado_em AS publicadoEm, u.nome::text AS publicadoPorNome
          FROM mesa_planta p
          LEFT JOIN usuario s ON s.id = p.salvo_por
          LEFT JOIN usuario u ON u.id = p.publicado_por
        """;

    public async Task<PlantaDto?> LerAsync(Guid loja, string estado, int? versao, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        var row = await c.QueryFirstOrDefaultAsync<PlantaRow>(new CommandDefinition(PlantaSelect + (versao is null
            ? " WHERE p.estabelecimento_id = @loja AND p.estado = @estado"
            : " WHERE p.estabelecimento_id = @loja AND p.versao = @versao"), new { loja, estado, versao }, cancellationToken: ct));
        return row is null ? null : Dto(row);
    }

    public async Task<IReadOnlyList<PlantaVersaoDto>> VersoesAsync(Guid loja, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        var rows = await c.QueryAsync<PlantaVersaoDto>(new CommandDefinition("""
            SELECT p.versao AS "Versao", p.estado AS "Estado", p.revisao AS "Revisao",
                   COALESCE(jsonb_array_length(CASE WHEN jsonb_typeof(p.documento->'pisos') = 'array' THEN p.documento->'pisos' END), 0) AS "Andares",
                   COALESCE(jsonb_array_length(CASE WHEN jsonb_typeof(p.documento->'mesas') = 'array' THEN p.documento->'mesas' END), 0) AS "Mesas",
                   s.nome::text AS "SalvoPorNome", p.atualizado_em AS "AtualizadoEm", p.publicado_em AS "PublicadoEm", u.nome::text AS "PublicadoPorNome"
              FROM mesa_planta p
              LEFT JOIN usuario s ON s.id = p.salvo_por
              LEFT JOIN usuario u ON u.id = p.publicado_por
             WHERE p.estabelecimento_id = @loja
             ORDER BY p.versao DESC
             LIMIT 100
            """, new { loja }, cancellationToken: ct));
        return rows.ToList();
    }

    public async Task<PlantaAppDto?> AppAsync(Guid loja, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        var row = await c.QueryFirstOrDefaultAsync<PlantaRow>(new CommandDefinition(
            PlantaSelect + " WHERE p.estabelecimento_id = @loja AND p.estado = 'publicada'", new { loja }, cancellationToken: ct));
        if (row is null) return null;
        var mesas = await c.QueryAsync<AppMesaRow>(new CommandDefinition("""
            SELECT m.id AS mesaId, m.chave_planta AS chave, m.numero, m.piso_id AS pisoId, m.setor_id AS setorId, m.capacidade, m.forma,
                   m.reservavel, e.status, e.versao, e.motivo_bloqueio AS motivoBloqueio
              FROM mesa m JOIN mesa_estado e ON e.mesa_id = m.id
             WHERE m.estabelecimento_id = @loja AND m.ativa
             ORDER BY length(m.numero), m.numero
            """, new { loja }, cancellationToken: ct));

        var doc = Documento(row.Documento);
        var desenhos = (doc["mesas"] as JsonArray ?? new JsonArray()).OfType<JsonObject>()
            .Where(m => m["id"] is JsonValue).GroupBy(m => m["id"]!.ToString()).ToDictionary(g => g.Key, g => g.First());
        var pisos = new JsonArray();
        foreach (var p in (doc["pisos"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
        {
            var leve = (JsonObject)p.DeepClone();
            leve.Remove("fundo"); // foto de fundo é do editor; pesa e o app não usa
            pisos.Add(leve);
        }
        return new PlantaAppDto(row.Versao, row.PublicadoEm ?? row.AtualizadoEm, pisos, mesas.Select(m => new PlantaAppMesaDto(
            m.MesaId, m.Chave, m.Numero, m.PisoId, m.SetorId, m.Capacidade, m.Forma, m.Reservavel, m.Status, m.Versao, m.MotivoBloqueio,
            desenhos.TryGetValue(m.Chave, out var d) ? (JsonObject)d.DeepClone() : null)).ToList());
    }

    public async Task<RascunhoSalvoDto> SalvarRascunhoAsync(MesasContexto ctx, SalvarRascunhoPlantaRequest req, CancellationToken ct)
    {
        var texto = req.Documento?.ToJsonString() ?? "";
        if (PlantaRegras.ValidarFormato(req.Documento, Encoding.UTF8.GetByteCount(texto)) is { } erro) throw MesasException.Validacao(erro);

        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var atual = await Rascunho(c, tx, ctx.Loja, ct);
        int versao, revisao;
        if (atual is null)
        {
            if (req.VersaoRascunho is not null)
                throw VersaoVelha("O rascunho que você abriu não existe mais: alguém publicou ou descartou. Abra a planta de novo.", null);
            versao = await c.ExecuteScalarAsync<int>(new CommandDefinition(
                "SELECT COALESCE(MAX(versao), 0) + 1 FROM mesa_planta WHERE estabelecimento_id = @loja", new { loja = ctx.Loja }, tx, cancellationToken: ct));
            revisao = 1;
            try
            {
                await c.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO mesa_planta (id, estabelecimento_id, versao, revisao, estado, documento, salvo_por)
                    VALUES (@id, @loja, @versao, 1, 'rascunho', @texto::jsonb, @usuario)
                    """, new { id = Guid.NewGuid(), loja = ctx.Loja, versao, texto, usuario = ctx.Usuario }, tx, cancellationToken: ct));
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                throw VersaoVelha("Outra pessoa começou um rascunho agora. Abra a planta de novo.", null);
            }
        }
        else
        {
            if (req.VersaoRascunho != atual.Revisao)
                throw VersaoVelha(req.VersaoRascunho is null
                    ? "Já existe um rascunho da planta. Abra o rascunho antes de salvar."
                    : $"{atual.SalvoPorNome ?? "Outra pessoa"} salvou a planta enquanto você editava. Abra de novo para não perder a mudança de ninguém.", atual);
            versao = atual.Versao;
            revisao = atual.Revisao + 1;
            await c.ExecuteAsync(new CommandDefinition("""
                UPDATE mesa_planta SET documento = @texto::jsonb, revisao = @revisao, salvo_por = @usuario, atualizado_em = NOW()
                 WHERE id = @id
                """, new { atual.Id, texto, revisao, usuario = ctx.Usuario }, tx, cancellationToken: ct));
        }
        var ator = await Ator(c, tx, ctx, ct);
        var eventoId = await Evento(c, tx, ctx, ator, "salvarRascunhoPlanta", "planta", null, "Planta", null, $"Rascunho {revisao}", null, null, req.Chave, ct);
        var salvoEm = await c.ExecuteScalarAsync<DateTime>(new CommandDefinition("SELECT NOW()", transaction: tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return new RascunhoSalvoDto(eventoId, revisao, versao, salvoEm);
    }

    public async Task<PlantaPublicadaDto> PublicarAsync(MesasContexto ctx, PublicarPlantaRequest req, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var rascunho = await Rascunho(c, tx, ctx.Loja, ct);
        if (rascunho is null)
        {
            var publicada = await c.QueryFirstOrDefaultAsync<PlantaRow>(new CommandDefinition(
                PlantaSelect + " WHERE p.estabelecimento_id = @loja AND p.estado = 'publicada'", new { loja = ctx.Loja }, tx, cancellationToken: ct));
            if (publicada is null) throw MesasException.Validacao("Nada para publicar. Salve um rascunho antes.");
            throw new MesasException("JA_FEITO", "A planta já está publicada. Não há rascunho novo.", 409)
            {
                Detalhes = new { conflito = true, versaoPlanta = publicada.Versao, porQuem = publicada.PublicadoPorNome, quando = publicada.PublicadoEm }
            };
        }
        if (req.VersaoRascunho is null) throw MesasException.Validacao("Envie a versão do rascunho.");
        if (req.VersaoRascunho != rascunho.Revisao)
            throw VersaoVelha($"{rascunho.SalvoPorNome ?? "Outra pessoa"} mudou o rascunho. Confira antes de publicar.", rascunho);

        var (lida, erros) = PlantaRegras.ValidarParaPublicar(Documento(rascunho.Documento));
        if (lida is null)
            throw new MesasException("VALIDACAO", erros.Count == 1 ? erros[0].Mensagem : $"Resolva {erros.Count} pontos antes de publicar.", 400)
            {
                Detalhes = new { erros }
            };

        // Trava cadastro e estado juntos: P07 trava mesa_estado para abrir sessão, então ninguém abre uma mesa que está saindo.
        var atuais = (await c.QueryAsync<MesaRow>(new CommandDefinition("""
            SELECT m.id, m.chave_planta AS chave, m.numero, m.ativa, (e.sessao_id IS NOT NULL) AS emUso
              FROM mesa m JOIN mesa_estado e ON e.mesa_id = m.id
             WHERE m.estabelecimento_id = @loja
             ORDER BY m.id
               FOR UPDATE
            """, new { loja = ctx.Loja }, tx, cancellationToken: ct)))
            .Select(m => new PlantaMesaAtual(m.Id, m.Chave, m.Numero, m.Ativa, m.EmUso)).ToList();
        var dif = PlantaRegras.Comparar(atuais, lida.Mesas);
        if (dif.Presas.Count > 0)
            throw new MesasException("ESTADO_INVALIDO", dif.Presas.Count == 1 ? dif.Presas[0].Mensagem
                : $"Não dá para tirar {dif.Presas.Count} mesas: elas estão em uso.", 409) { Detalhes = new { erros = dif.Presas } };

        var v = rascunho.Versao;
        var loja = ctx.Loja;
        await c.ExecuteAsync(new CommandDefinition(
            "UPDATE mesa SET ativa = FALSE, planta_versao = @v, atualizado_em = NOW() WHERE id = ANY(@ids)",
            new { v, ids = dif.Desativar.Select(d => d.Id).ToArray() }, tx, cancellationToken: ct));
        // Número provisório antes do definitivo: trocar 1 por 2 numa publicação não esbarra no número único.
        await c.ExecuteAsync(new CommandDefinition(
            "UPDATE mesa SET numero = '~' || substr(replace(id::text, '-', ''), 1, 16) WHERE id = ANY(@ids)",
            new { ids = dif.Atualizar.Select(a => a.Id).ToArray() }, tx, cancellationToken: ct));
        foreach (var a in dif.Atualizar)
            await c.ExecuteAsync(new CommandDefinition("""
                UPDATE mesa SET numero = @Numero, piso_id = @PisoId, setor_id = @SetorId, capacidade = @Capacidade, forma = @Forma,
                       reservavel = @Reservavel, ativa = TRUE, planta_versao = @v, atualizado_em = NOW()
                 WHERE id = @id
                """, new { a.Nova.Numero, a.Nova.PisoId, a.Nova.SetorId, a.Nova.Capacidade, a.Nova.Forma, a.Nova.Reservavel, v, id = a.Id },
                tx, cancellationToken: ct));
        var criadas = new List<(Guid Id, PlantaMesaDoc Doc)>();
        foreach (var n in dif.Criar)
        {
            var id = Guid.NewGuid();
            await c.ExecuteAsync(new CommandDefinition("""
                INSERT INTO mesa (id, estabelecimento_id, numero, piso_id, setor_id, capacidade, forma, reservavel, planta_versao, chave_planta)
                VALUES (@id, @loja, @Numero, @PisoId, @SetorId, @Capacidade, @Forma, @Reservavel, @v, @Chave);
                INSERT INTO mesa_estado (mesa_id, estabelecimento_id) VALUES (@id, @loja);
                """, new { id, loja, n.Numero, n.PisoId, n.SetorId, n.Capacidade, n.Forma, n.Reservavel, v, n.Chave }, tx, cancellationToken: ct));
            criadas.Add((id, n));
        }

        foreach (var a in lida.Andares)
            await c.ExecuteAsync(new CommandDefinition("""
                INSERT INTO mesa_andar (id, estabelecimento_id, chave_planta, nome, nivel, planta_versao)
                VALUES (@id, @loja, @Chave, @Nome, @Nivel, @v)
                ON CONFLICT (estabelecimento_id, chave_planta)
                DO UPDATE SET nome = EXCLUDED.nome, nivel = EXCLUDED.nivel, ativa = TRUE, planta_versao = EXCLUDED.planta_versao, atualizado_em = NOW()
                """, new { id = Guid.NewGuid(), loja, a.Chave, a.Nome, a.Nivel, v }, tx, cancellationToken: ct));
        foreach (var a in lida.Areas)
            await c.ExecuteAsync(new CommandDefinition("""
                INSERT INTO mesa_area (id, estabelecimento_id, chave_planta, piso_id, nome, cor, planta_versao)
                VALUES (@id, @loja, @Chave, @PisoId, @Nome, @Cor, @v)
                ON CONFLICT (estabelecimento_id, chave_planta)
                DO UPDATE SET piso_id = EXCLUDED.piso_id, nome = EXCLUDED.nome, cor = EXCLUDED.cor, ativa = TRUE,
                              planta_versao = EXCLUDED.planta_versao, atualizado_em = NOW()
                """, new { id = Guid.NewGuid(), loja, a.Chave, a.PisoId, a.Nome, a.Cor, v }, tx, cancellationToken: ct));
        await c.ExecuteAsync(new CommandDefinition("""
            UPDATE mesa_andar SET ativa = FALSE, atualizado_em = NOW() WHERE estabelecimento_id = @loja AND ativa AND planta_versao <> @v;
            UPDATE mesa_area SET ativa = FALSE, atualizado_em = NOW() WHERE estabelecimento_id = @loja AND ativa AND planta_versao <> @v;
            UPDATE mesa_planta SET estado = 'arquivada' WHERE estabelecimento_id = @loja AND estado = 'publicada';
            UPDATE mesa_planta SET estado = 'publicada', publicado_em = NOW(), publicado_por = @usuario WHERE id = @id;
            """, new { loja, v, usuario = ctx.Usuario, rascunho.Id }, tx, cancellationToken: ct));

        var ator = await Ator(c, tx, ctx, ct);
        var eventoId = await Evento(c, tx, ctx, ator, "publicarPlanta", "planta", rascunho.Id,
            $"{lida.Andares.Count} {(lida.Andares.Count == 1 ? "andar" : "andares")}, {lida.Mesas.Count} mesas", null, $"Versão {v}", null, null, req.Chave, ct);
        // Histórico de cada mesa no livro do salão: a mesa é a mesma, o número muda.
        foreach (var (id, n) in criadas)
            await Evento(c, tx, ctx, ator, "criarMesa", "mesa", id, $"Mesa {n.Numero}", null, n.Numero, null, id, req.Chave, ct);
        foreach (var r in dif.Renumeradas)
            await Evento(c, tx, ctx, ator, "renumerarMesa", "mesa", r.Id, $"Mesa {r.Nova.Numero}", r.NumeroAntes, r.Nova.Numero, null, r.Id, req.Chave, ct);
        foreach (var r in dif.Atualizar.Where(a => a.Reativar))
            await Evento(c, tx, ctx, ator, "reativarMesa", "mesa", r.Id, $"Mesa {r.Nova.Numero}", null, r.Nova.Numero, null, r.Id, req.Chave, ct);
        foreach (var d in dif.Desativar)
            await Evento(c, tx, ctx, ator, "desativarMesa", "mesa", d.Id, $"Mesa {d.Numero}", d.Numero, null, null, d.Id, req.Chave, ct);

        await tx.CommitAsync(ct);
        await tempoReal.SalaoMudou(loja, v);
        return new PlantaPublicadaDto(eventoId, v, criadas.Count, dif.Desativar.Count, dif.Renumeradas.Count());
    }

    public async Task<RascunhoDescartadoDto> DescartarAsync(MesasContexto ctx, DescartarRascunhoPlantaRequest req, CancellationToken ct)
    {
        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var rascunho = await Rascunho(c, tx, ctx.Loja, ct)
            ?? throw new MesasException("JA_FEITO", "Não há rascunho para descartar.", 409) { Detalhes = new { conflito = true } };
        if (req.VersaoRascunho is not null && req.VersaoRascunho != rascunho.Revisao)
            throw VersaoVelha($"{rascunho.SalvoPorNome ?? "Outra pessoa"} mudou o rascunho. Confira antes de descartar.", rascunho);
        await c.ExecuteAsync(new CommandDefinition(
            "UPDATE mesa_planta SET estado = 'descartada', atualizado_em = NOW() WHERE id = @Id", new { rascunho.Id }, tx, cancellationToken: ct));
        var ator = await Ator(c, tx, ctx, ct);
        var eventoId = await Evento(c, tx, ctx, ator, "descartarRascunhoPlanta", "planta", rascunho.Id, "Planta", $"Rascunho {rascunho.Revisao}", null, null, null, req.Chave, ct);
        await tx.CommitAsync(ct);
        return new RascunhoDescartadoDto(eventoId, rascunho.Versao);
    }

    public async Task<MesaComandoDto> BloquearAsync(MesasContexto ctx, MesaBloqueioRequest req, bool bloquear, CancellationToken ct)
    {
        if (req.MesaId is not Guid mesaId) throw MesasException.Validacao("Escolha a mesa.");
        if (req.VersaoMesa is null) throw MesasException.Validacao("Envie a versão da mesa.");
        var motivo = string.IsNullOrWhiteSpace(req.Motivo) ? null : req.Motivo.Trim();
        if (bloquear && motivo is null) throw new MesasException("EXIGE_MOTIVO", "Diga por que a mesa vai ficar fora de uso.", 422);
        if (motivo is { Length: > 280 }) throw MesasException.Validacao("Motivo longo demais.");

        await using var c = await source.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        var e = await c.QueryFirstOrDefaultAsync<EstadoRow>(new CommandDefinition("""
            SELECT m.numero, e.status, e.versao
              FROM mesa_estado e JOIN mesa m ON m.id = e.mesa_id
             WHERE e.mesa_id = @mesaId AND e.estabelecimento_id = @loja AND m.ativa
               FOR UPDATE OF e
            """, new { mesaId, loja = ctx.Loja }, tx, cancellationToken: ct)) ?? throw MesasException.NaoEncontrado("Mesa não encontrada.");
        if (e.Versao != req.VersaoMesa)
            throw new MesasException("VERSAO_VELHA", $"A Mesa {e.Numero} mudou. Confira de novo.", 409) { Detalhes = new { versaoAtual = e.Versao } };
        if (bloquear && e.Status == "indisponivel" || !bloquear && e.Status == "livre")
            throw new MesasException("JA_FEITO", bloquear ? $"A Mesa {e.Numero} já está fora de uso." : $"A Mesa {e.Numero} já está liberada.", 409)
            {
                Detalhes = new { conflito = true }
            };
        if (bloquear && e.Status != "livre")
            throw new MesasException("ESTADO_INVALIDO", $"A Mesa {e.Numero} está aberta. Feche antes de tirar de uso.", 409);
        if (!bloquear && e.Status != "indisponivel")
            throw new MesasException("ESTADO_INVALIDO", $"A Mesa {e.Numero} não está fora de uso.", 409);

        var versao = await c.ExecuteScalarAsync<int>(new CommandDefinition("""
            UPDATE mesa_estado SET status = @status, motivo_bloqueio = @motivo, versao = versao + 1, atualizado_em = NOW()
             WHERE mesa_id = @mesaId RETURNING versao
            """, new { mesaId, status = bloquear ? "indisponivel" : "livre", motivo = bloquear ? motivo : null }, tx, cancellationToken: ct));
        var ator = await Ator(c, tx, ctx, ct);
        var eventoId = await Evento(c, tx, ctx, ator, bloquear ? "bloquearMesa" : "desbloquearMesa", "mesa", mesaId, $"Mesa {e.Numero}",
            bloquear ? "Livre" : "Fora de uso", bloquear ? "Fora de uso" : "Livre", motivo, mesaId, req.Chave, ct);
        await tx.CommitAsync(ct);
        await tempoReal.MesaMudou(ctx.Loja, mesaId, versao);
        return new MesaComandoDto(eventoId, new Dictionary<Guid, int> { [mesaId] = versao });
    }

    private static Task<PlantaRow?> Rascunho(NpgsqlConnection c, NpgsqlTransaction tx, Guid loja, CancellationToken ct) =>
        c.QueryFirstOrDefaultAsync<PlantaRow>(new CommandDefinition(
            PlantaSelect + " WHERE p.estabelecimento_id = @loja AND p.estado = 'rascunho' FOR UPDATE OF p", new { loja }, tx, cancellationToken: ct));

    private static MesasException VersaoVelha(string mensagem, PlantaRow? atual) => new("VERSAO_VELHA", mensagem, 409)
    {
        Detalhes = new { conflito = true, versaoAtual = atual?.Revisao, porQuem = atual?.SalvoPorNome, quando = atual?.AtualizadoEm }
    };

    private static async Task<string> Ator(NpgsqlConnection c, NpgsqlTransaction tx, MesasContexto ctx, CancellationToken ct)
    {
        var nome = await c.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT nome::text FROM usuario WHERE id = @id", new { id = ctx.Usuario }, tx, cancellationToken: ct));
        nome = string.IsNullOrWhiteSpace(nome) ? "Equipe" : nome.Trim();
        return nome.Length <= 80 ? nome : nome[..80];
    }

    private static async Task<Guid> Evento(NpgsqlConnection c, NpgsqlTransaction tx, MesasContexto ctx, string atorNome, string acao,
        string alvoTipo, Guid? alvoId, string rotulo, string? antes, string? depois, string? motivo, Guid? mesaId, Guid? chave, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        await c.ExecuteAsync(new CommandDefinition("""
            INSERT INTO mesa_evento (id, estabelecimento_id, ator_id, ator_nome, ator_papel, acao, alvo_tipo, alvo_id, alvo_rotulo,
                                     antes, depois, motivo, mesa_id, comando_chave)
            VALUES (@id, @loja, @atorId, @atorNome, @papel, @acao, @alvoTipo, @alvoId, @rotulo, @antes, @depois, @motivo, @mesaId, @chave)
            """, new { id, loja = ctx.Loja, atorId = ctx.Usuario.ToString(), atorNome, papel = ctx.Papel, acao, alvoTipo, alvoId, rotulo,
                antes, depois, motivo, mesaId, chave }, tx, cancellationToken: ct));
        return id;
    }

    private static JsonObject Documento(string texto) => JsonNode.Parse(texto) as JsonObject ?? new JsonObject();

    private static PlantaDto Dto(PlantaRow r) => new(r.Id, r.Versao, r.Estado == "rascunho" ? r.Revisao : null, r.Estado, Documento(r.Documento),
        r.SalvoPorNome, r.AtualizadoEm, r.PublicadoEm, r.PublicadoPorNome);
}
