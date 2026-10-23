using Dapper;
using Npgsql;

namespace APIBack.Service
{
    public sealed class ChamadoDto
    {
        public long Id { get; set; }
        public Guid EstabelecimentoId { get; set; }
        public string Loja { get; set; } = string.Empty;
        public string Categoria { get; set; } = string.Empty;
        public string Titulo { get; set; } = string.Empty;
        public string Status { get; set; } = "aberto";
        public DateTimeOffset PrazoEm { get; set; }
        public DateTimeOffset? PrimeiraRespostaEm { get; set; }
        public DateTimeOffset? ResolvidoEm { get; set; }
        public short? Nota { get; set; }
        public DateTimeOffset CriadoEm { get; set; }
        public string? AbertoPor { get; set; }
        public string? UltimaMensagem { get; set; }
        public int Mensagens { get; set; }
    }

    public sealed class ChamadoMensagemDto
    {
        public long Id { get; set; }
        public bool DaZippy { get; set; }
        public string Texto { get; set; } = string.Empty;
        public string? Autor { get; set; }
        public DateTimeOffset CriadoEm { get; set; }
    }

    public sealed record AbrirChamadoRequest(string? Categoria, string? Texto);
    public sealed record ResponderChamadoRequest(string? Texto);
    public sealed record StatusChamadoRequest(string? Status);
    public sealed record NotaChamadoRequest(short Nota);

    public sealed class ChamadoException(int status, string message) : Exception(message)
    {
        public int Status { get; } = status;
    }

    public interface IChamadosService
    {
        Task<ChamadoDto> AbrirAsync(Guid loja, int usuarioId, AbrirChamadoRequest request, CancellationToken ct = default);
        Task<IReadOnlyList<ChamadoDto>> ListarAsync(Guid? loja, CancellationToken ct = default);
        Task<(ChamadoDto Chamado, IReadOnlyList<ChamadoMensagemDto> Mensagens)> ObterAsync(long id, Guid? loja, CancellationToken ct = default);
        Task ResponderAsync(long id, Guid? loja, int usuarioId, bool daZippy, string texto, CancellationToken ct = default);
        Task MudarStatusAsync(long id, string status, CancellationToken ct = default);
        Task AvaliarAsync(long id, Guid loja, short nota, CancellationToken ct = default);
    }

    /// <summary>Chamados entre a loja e a Zippy. A loja so enxerga os dela (loja informada); a Gestao enxerga todos (loja nula).</summary>
    public sealed class ChamadosService(NpgsqlDataSource dataSource) : IChamadosService
    {
        private static readonly string[] Categorias = ["parou", "duvida", "pagamento", "ideia"];
        private static readonly string[] Status = ["aberto", "andamento", "loja", "resolvido"];

        private const string Select = @"
SELECT c.id, c.estabelecimento_id AS EstabelecimentoId, e.nome_fantasia AS Loja, c.categoria, c.titulo, c.status, c.prazo_em AS PrazoEm,
       c.primeira_resposta_em AS PrimeiraRespostaEm, c.resolvido_em AS ResolvidoEm, c.nota, c.criado_em AS CriadoEm, u.nome AS AbertoPor,
       (SELECT m.texto FROM chamado_mensagem m WHERE m.chamado_id = c.id ORDER BY m.criado_em DESC LIMIT 1) AS UltimaMensagem,
       (SELECT COUNT(*) FROM chamado_mensagem m WHERE m.chamado_id = c.id)::int AS Mensagens
  FROM chamado c
  JOIN estabelecimentos e ON e.id = c.estabelecimento_id
  LEFT JOIN usuario u ON u.id = c.aberto_por";

        public async Task<ChamadoDto> AbrirAsync(Guid loja, int usuarioId, AbrirChamadoRequest request, CancellationToken ct = default)
        {
            var categoria = request.Categoria?.Trim().ToLowerInvariant() ?? string.Empty;
            var texto = request.Texto?.Trim();
            if (!Categorias.Contains(categoria)) throw new ChamadoException(422, "Escolha sobre o que é o chamado.");
            if (string.IsNullOrEmpty(texto) || texto.Length < 5) throw new ChamadoException(422, "Conte o que aconteceu.");
            // O titulo e a primeira linha, curta: e o que a Gestao le na fila.
            var titulo = texto.Split('\n')[0];
            if (titulo.Length > 90) titulo = titulo[..87] + "…";
            var horas = categoria == "parou" ? 4 : 24;
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            await using var tx = await connection.BeginTransactionAsync(ct);
            var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(@"
INSERT INTO chamado (estabelecimento_id, aberto_por, categoria, titulo, prazo_em)
VALUES (@Loja, @Usuario, @Categoria, @Titulo, NOW() + make_interval(hours => @Horas)) RETURNING id;",
                new { Loja = loja, Usuario = usuarioId, Categoria = categoria, Titulo = titulo, Horas = horas }, tx, cancellationToken: ct));
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO chamado_mensagem (chamado_id, autor_id, da_zippy, texto) VALUES (@Id, @Usuario, FALSE, @Texto);",
                new { Id = id, Usuario = usuarioId, Texto = texto }, tx, cancellationToken: ct));
            var dto = await connection.QuerySingleAsync<ChamadoDto>(new CommandDefinition(Select + " WHERE c.id = @Id;", new { Id = id }, tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
            return dto;
        }

        public async Task<IReadOnlyList<ChamadoDto>> ListarAsync(Guid? loja, CancellationToken ct = default)
        {
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            return (await connection.QueryAsync<ChamadoDto>(new CommandDefinition(Select + @"
 WHERE (@Loja::uuid IS NULL OR c.estabelecimento_id = @Loja)
   AND (c.status <> 'resolvido' OR c.resolvido_em > NOW() - INTERVAL '30 days')
 ORDER BY (c.status = 'resolvido'), c.prazo_em LIMIT 300;", new { Loja = loja }, cancellationToken: ct))).ToList();
        }

        public async Task<(ChamadoDto, IReadOnlyList<ChamadoMensagemDto>)> ObterAsync(long id, Guid? loja, CancellationToken ct = default)
        {
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            var dto = await connection.QuerySingleOrDefaultAsync<ChamadoDto>(new CommandDefinition(Select + " WHERE c.id = @Id AND (@Loja::uuid IS NULL OR c.estabelecimento_id = @Loja);",
                new { Id = id, Loja = loja }, cancellationToken: ct)) ?? throw new ChamadoException(404, "Chamado não encontrado.");
            var msgs = (await connection.QueryAsync<ChamadoMensagemDto>(new CommandDefinition(@"
SELECT m.id, m.da_zippy AS DaZippy, m.texto, u.nome AS Autor, m.criado_em AS CriadoEm
  FROM chamado_mensagem m LEFT JOIN usuario u ON u.id = m.autor_id WHERE m.chamado_id = @Id ORDER BY m.criado_em;",
                new { Id = id }, cancellationToken: ct))).ToList();
            return (dto, msgs);
        }

        public async Task ResponderAsync(long id, Guid? loja, int usuarioId, bool daZippy, string texto, CancellationToken ct = default)
        {
            texto = texto?.Trim() ?? string.Empty;
            if (texto.Length == 0) throw new ChamadoException(422, "Escreva a resposta.");
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            await using var tx = await connection.BeginTransactionAsync(ct);
            // Resposta da Zippy passa para "andamento"; nova mensagem da loja reabre um chamado que esperava por ela.
            var linhas = await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE chamado SET atualizado_em = NOW(),
       primeira_resposta_em = CASE WHEN @DaZippy THEN COALESCE(primeira_resposta_em, NOW()) ELSE primeira_resposta_em END,
       status = CASE WHEN @DaZippy AND status = 'aberto' THEN 'andamento' WHEN NOT @DaZippy AND status IN ('loja', 'resolvido') THEN 'aberto' ELSE status END,
       resolvido_em = CASE WHEN NOT @DaZippy THEN NULL ELSE resolvido_em END
 WHERE id = @Id AND (@Loja::uuid IS NULL OR estabelecimento_id = @Loja);", new { Id = id, Loja = loja, DaZippy = daZippy }, tx, cancellationToken: ct));
            if (linhas == 0) throw new ChamadoException(404, "Chamado não encontrado.");
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO chamado_mensagem (chamado_id, autor_id, da_zippy, texto) VALUES (@Id, @Usuario, @DaZippy, @Texto);",
                new { Id = id, Usuario = usuarioId, DaZippy = daZippy, Texto = texto }, tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
        }

        public async Task MudarStatusAsync(long id, string status, CancellationToken ct = default)
        {
            status = status?.Trim().ToLowerInvariant() ?? string.Empty;
            if (!Status.Contains(status)) throw new ChamadoException(422, "Situação inválida.");
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            var linhas = await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE chamado SET status = @Status, atualizado_em = NOW(), resolvido_em = CASE WHEN @Status = 'resolvido' THEN NOW() ELSE NULL END WHERE id = @Id;",
                new { Id = id, Status = status }, cancellationToken: ct));
            if (linhas == 0) throw new ChamadoException(404, "Chamado não encontrado.");
        }

        public async Task AvaliarAsync(long id, Guid loja, short nota, CancellationToken ct = default)
        {
            if (nota is < 1 or > 5) throw new ChamadoException(422, "Dê uma nota de 1 a 5.");
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            var linhas = await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE chamado SET nota = @Nota, atualizado_em = NOW() WHERE id = @Id AND estabelecimento_id = @Loja AND status = 'resolvido';",
                new { Id = id, Loja = loja, Nota = nota }, cancellationToken: ct));
            if (linhas == 0) throw new ChamadoException(404, "Só dá para avaliar um chamado resolvido.");
        }
    }
}
