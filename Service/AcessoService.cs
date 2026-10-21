using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Security;
using Dapper;
using Npgsql;

namespace APIBack.Service
{
    /// <summary>tipo: convite (primeiro acesso) | redefinicao (link gerado pela Gestao).</summary>
    public sealed record AcessoLinkDto(int UsuarioId, string Nome, string Email, string Tipo, string Token, DateTimeOffset ExpiraEm);

    public sealed record AcessoUsuarioDto(
        int UsuarioId, string Nome, string Email, string TipoAcesso, bool Responsavel, bool SenhaDefinida,
        DateTimeOffset? UltimoAcesso, DateTimeOffset? LinkValidoAte, DateTimeOffset? RedefinicaoSolicitadaEm);

    public sealed record ConviteInfoDto(string Nome, string Email, string Tipo, DateTimeOffset ExpiraEm);

    public sealed class AcessoException : Exception
    {
        public AcessoException(int status, string code, string message) : base(message)
        {
            Status = status;
            Code = code;
        }

        public int Status { get; }
        public string Code { get; }
    }

    public interface IAcessoService
    {
        Task<IReadOnlyList<AcessoUsuarioDto>> ListarAsync(Guid estabelecimentoId, CancellationToken ct = default);
        /// <summary>Gera o link de primeiro acesso (senha ainda pendente) ou de redefinicao. Invalida os links anteriores do usuario.</summary>
        Task<AcessoLinkDto> GerarLinkAsync(Guid estabelecimentoId, int usuarioId, int? actorUserId, CancellationToken ct = default);
        Task<ConviteInfoDto> ConsultarAsync(string token, CancellationToken ct = default);
        /// <summary>Grava a senha nova, consome o link e encerra as sessoes antigas. Devolve o e-mail para o login automatico.</summary>
        Task<string> DefinirSenhaAsync(string token, string senha, CancellationToken ct = default);
        /// <summary>"Esqueci minha senha": so registra o pedido para a Gestao ver (nao ha envio de e-mail). Nunca revela se o e-mail existe.</summary>
        Task SolicitarRedefinicaoAsync(string email, CancellationToken ct = default);
    }

    public sealed class AcessoService : IAcessoService
    {
        public static readonly TimeSpan ValidadeConvite = TimeSpan.FromDays(7);
        public static readonly TimeSpan ValidadeRedefinicao = TimeSpan.FromDays(2);
        public const int SenhaMinima = 6;

        private readonly NpgsqlDataSource _dataSource;

        public AcessoService(NpgsqlDataSource dataSource) => _dataSource = dataSource;

        public static string NovoToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        public static string HashToken(string token) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim())));

        public async Task<IReadOnlyList<AcessoUsuarioDto>> ListarAsync(Guid estabelecimentoId, CancellationToken ct = default)
        {
            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            var rows = await connection.QueryAsync<AcessoRow>(new CommandDefinition(@"
SELECT u.id AS UsuarioId, u.nome AS Nome, u.email AS Email,
       ue.tipo_acesso::text AS TipoAcesso, uemp.tipo_acesso::text AS TipoAcessoEmpresa,
       u.senha_pendente AS SenhaPendente,
       (SELECT MAX(t.created_at) FROM usuario_refresh_tokens t WHERE t.id_usuario = u.id) AS UltimoAcesso,
       (SELECT MAX(c.expira_em) FROM usuario_convites c
         WHERE c.id_usuario = u.id AND c.tipo IN ('convite', 'redefinicao') AND c.usado_em IS NULL AND c.expira_em > NOW()) AS LinkValidoAte,
       (SELECT MAX(c.criado_em) FROM usuario_convites c
         WHERE c.id_usuario = u.id AND c.tipo = 'solicitacao' AND c.usado_em IS NULL) AS RedefinicaoSolicitadaEm
  FROM usuario_estabelecimentos ue
  JOIN estabelecimentos e ON e.id = ue.id_estabelecimento
  JOIN usuario u ON u.id = ue.id_usuario AND u.deleted_at IS NULL
  LEFT JOIN usuario_empresas uemp ON uemp.id_empresa = e.id_empresa AND uemp.id_usuario = u.id
                                 AND COALESCE(uemp.ativo, TRUE) = TRUE AND COALESCE(uemp.status, 'ativo') = 'ativo'
 WHERE ue.id_estabelecimento = @Id
   AND COALESCE(ue.ativo, TRUE) = TRUE AND COALESCE(ue.status, 'ativo') = 'ativo'
   AND COALESCE(u.is_super_admin, FALSE) = FALSE
   AND ue.tipo_acesso::text <> 'motoboy'
 ORDER BY CASE WHEN uemp.tipo_acesso::text IN ('dono', 'gerente_empresa') THEN 0
               WHEN ue.tipo_acesso::text = 'gerente_estabelecimento' THEN 1 ELSE 2 END, u.nome
 LIMIT 50;", new { Id = estabelecimentoId }, cancellationToken: ct));

            return rows.Select(row => new AcessoUsuarioDto(
                row.UsuarioId, row.Nome, row.Email,
                string.IsNullOrWhiteSpace(row.TipoAcessoEmpresa) || row.TipoAcessoEmpresa == "colaborador" ? row.TipoAcesso : row.TipoAcessoEmpresa!,
                row.TipoAcessoEmpresa is "dono" or "gerente_empresa" || row.TipoAcesso == "gerente_estabelecimento",
                !row.SenhaPendente,
                Utc(row.UltimoAcesso), Utc(row.LinkValidoAte), Utc(row.RedefinicaoSolicitadaEm))).ToList();
        }

        public async Task<AcessoLinkDto> GerarLinkAsync(Guid estabelecimentoId, int usuarioId, int? actorUserId, CancellationToken ct = default)
        {
            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            await using var tx = await connection.BeginTransactionAsync(ct);

            var usuario = await connection.QuerySingleOrDefaultAsync<UsuarioLinkRow>(new CommandDefinition(@"
SELECT u.nome AS Nome, u.email AS Email, u.senha_pendente AS SenhaPendente
  FROM usuario u
  JOIN usuario_estabelecimentos ue ON ue.id_usuario = u.id AND ue.id_estabelecimento = @Estabelecimento
                                  AND COALESCE(ue.ativo, TRUE) = TRUE AND COALESCE(ue.status, 'ativo') = 'ativo'
 WHERE u.id = @Usuario AND u.deleted_at IS NULL AND COALESCE(u.is_super_admin, FALSE) = FALSE
 FOR UPDATE OF u;", new { Estabelecimento = estabelecimentoId, Usuario = usuarioId }, tx, cancellationToken: ct));
            if (usuario is null)
                throw new AcessoException(404, "USUARIO_NAO_ENCONTRADO", "Esse usuário não tem acesso ativo a esta loja.");

            var tipo = usuario.SenhaPendente ? "convite" : "redefinicao";
            var token = NovoToken();
            var expira = DateTimeOffset.UtcNow + (tipo == "convite" ? ValidadeConvite : ValidadeRedefinicao);

            // Um link por vez: o anterior (e o pedido de redefinicao que ele atende) deixa de valer.
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE usuario_convites SET usado_em = NOW() WHERE id_usuario = @Usuario AND usado_em IS NULL;",
                new { Usuario = usuarioId }, tx, cancellationToken: ct));
            await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO usuario_convites (id_usuario, tipo, token_hash, expira_em, criado_por)
VALUES (@Usuario, @Tipo, @Hash, @Expira, @Actor);",
                new { Usuario = usuarioId, Tipo = tipo, Hash = HashToken(token), Expira = expira, Actor = actorUserId }, tx, cancellationToken: ct));
            await tx.CommitAsync(ct);

            return new AcessoLinkDto(usuarioId, usuario.Nome, usuario.Email, tipo, token, expira);
        }

        public async Task<ConviteInfoDto> ConsultarAsync(string token, CancellationToken ct = default)
        {
            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            var row = await ObterConviteAsync(connection, null, token, false, ct);
            return new ConviteInfoDto(row.Nome, row.Email, row.Tipo, Utc(row.ExpiraEm)!.Value);
        }

        public async Task<string> DefinirSenhaAsync(string token, string senha, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(senha) || senha.Trim().Length < SenhaMinima)
                throw new AcessoException(422, "SENHA_CURTA", $"A senha deve ter ao menos {SenhaMinima} caracteres.");

            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            await using var tx = await connection.BeginTransactionAsync(ct);
            var row = await ObterConviteAsync(connection, tx, token, true, ct);

            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE usuario SET senha = @Senha, senha_pendente = FALSE, updated_at = NOW() WHERE id = @Usuario;",
                new { Senha = PasswordSecurity.Hash(senha.Trim()), Usuario = row.UsuarioId }, tx, cancellationToken: ct));
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE usuario_convites SET usado_em = NOW() WHERE id_usuario = @Usuario AND usado_em IS NULL;",
                new { Usuario = row.UsuarioId }, tx, cancellationToken: ct));
            // A senha mudou: sessoes abertas com a senha antiga (redefinicao) deixam de renovar.
            await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE usuario_refresh_tokens SET revoked_at = NOW(), reason_revoked = 'password_reset'
 WHERE id_usuario = @Usuario AND revoked_at IS NULL;", new { Usuario = row.UsuarioId }, tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
            return row.Email;
        }

        public async Task SolicitarRedefinicaoAsync(string email, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(email)) return;
            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            var usuarioId = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
                "SELECT id FROM usuario WHERE LOWER(email) = LOWER(@Email) AND deleted_at IS NULL LIMIT 1;",
                new { Email = email.Trim() }, cancellationToken: ct));
            if (usuarioId is null) return;

            // Um pedido aberto basta; repetir o clique nao enche a fila da Gestao.
            await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO usuario_convites (id_usuario, tipo, token_hash, expira_em)
SELECT @Usuario, 'solicitacao', @Hash, NOW() + INTERVAL '30 days'
 WHERE NOT EXISTS (SELECT 1 FROM usuario_convites
                    WHERE id_usuario = @Usuario AND tipo = 'solicitacao' AND usado_em IS NULL);",
                new { Usuario = usuarioId, Hash = HashToken(NovoToken()) }, cancellationToken: ct));
        }

        private static async Task<ConviteRow> ObterConviteAsync(
            NpgsqlConnection connection, NpgsqlTransaction? tx, string token, bool forUpdate, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(token))
                throw new AcessoException(404, "LINK_INVALIDO", "Link inválido. Peça um novo à Zippy.");
            var row = await connection.QuerySingleOrDefaultAsync<ConviteRow>(new CommandDefinition(@"
SELECT c.id_usuario AS UsuarioId, u.nome AS Nome, u.email AS Email, c.tipo AS Tipo, c.expira_em AS ExpiraEm, c.usado_em AS UsadoEm
  FROM usuario_convites c
  JOIN usuario u ON u.id = c.id_usuario AND u.deleted_at IS NULL
 WHERE c.token_hash = @Hash AND c.tipo IN ('convite', 'redefinicao')" + (forUpdate ? " FOR UPDATE OF c;" : ";"),
                new { Hash = HashToken(token) }, tx, cancellationToken: ct));
            if (row is null)
                throw new AcessoException(404, "LINK_INVALIDO", "Link inválido. Peça um novo à Zippy.");
            if (row.UsadoEm is not null)
                throw new AcessoException(410, "LINK_USADO", "Este link já foi usado ou substituído por um mais novo. Peça um novo à Zippy.");
            if (Utc(row.ExpiraEm)!.Value <= DateTimeOffset.UtcNow)
                throw new AcessoException(410, "LINK_EXPIRADO", "Este link expirou. Peça um novo à Zippy.");
            return row;
        }

        private sealed class AcessoRow
        {
            public int UsuarioId { get; set; }
            public string Nome { get; set; } = string.Empty;
            public string Email { get; set; } = string.Empty;
            public string TipoAcesso { get; set; } = string.Empty;
            public string? TipoAcessoEmpresa { get; set; }
            public bool SenhaPendente { get; set; }
            public DateTime? UltimoAcesso { get; set; }
            public DateTime? LinkValidoAte { get; set; }
            public DateTime? RedefinicaoSolicitadaEm { get; set; }
        }

        private sealed class UsuarioLinkRow
        {
            public string Nome { get; set; } = string.Empty;
            public string Email { get; set; } = string.Empty;
            public bool SenhaPendente { get; set; }
        }

        private sealed class ConviteRow
        {
            public int UsuarioId { get; set; }
            public string Nome { get; set; } = string.Empty;
            public string Email { get; set; } = string.Empty;
            public string Tipo { get; set; } = string.Empty;
            public DateTime ExpiraEm { get; set; }
            public DateTime? UsadoEm { get; set; }
        }

        private static DateTimeOffset? Utc(DateTime? value) =>
            value is null ? null : new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));
    }
}
