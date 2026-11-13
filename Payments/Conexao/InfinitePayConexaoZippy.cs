using System;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Payments.Provedores;
using APIBack.Payments.MercadoPago;
using Dapper;
using Npgsql;

namespace APIBack.Payments.Conexao
{
    public sealed class InfinitePayZippyConfiguracaoDto
    {
        public bool Configurada { get; set; }
        public string? InfiniteTag { get; set; }
        public bool Habilitada { get; set; }
        public bool TemApiKey { get; set; }
        public int Versao { get; set; }
        public DateTime? AtualizadoEm { get; set; }
    }

    public interface IInfinitePayConexaoZippyService
    {
        Task<InfinitePayZippyConfiguracaoDto> EstadoAsync(CancellationToken ct = default);
        Task<InfinitePayZippyConfiguracaoDto> ConfigurarAsync(int usuarioId, InfinitePayConfiguracaoRequest request, CancellationToken ct = default);
    }

    /// <summary>
    /// Conta InfinitePay da PROPRIA Zippy (recebe mensalidades e compras extras). So super admin chama (controller). A chave e opcional,
    /// fica cifrada e nunca volta na resposta. Cada troca de InfiniteTag/chave cria versao nova; cobrancas ja emitidas ficam na antiga.
    /// </summary>
    public sealed class InfinitePayConexaoZippyService : IInfinitePayConexaoZippyService
    {
        private readonly NpgsqlDataSource _db;
        private readonly IPagamentoSegredoProtector _segredos;

        public InfinitePayConexaoZippyService(NpgsqlDataSource db, IPagamentoSegredoProtector segredos)
        {
            _db = db;
            _segredos = segredos;
        }

        public async Task<InfinitePayZippyConfiguracaoDto> EstadoAsync(CancellationToken ct = default)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            return await LerAsync(cx, null, ct);
        }

        public async Task<InfinitePayZippyConfiguracaoDto> ConfigurarAsync(int usuarioId, InfinitePayConfiguracaoRequest request, CancellationToken ct = default)
        {
            if (request == null) throw PagamentoException.Invalida("Configuracao obrigatoria.");
            var handle = InfinitePayConfiguracaoRegras.NormalizarHandle(request.InfiniteTag);
            var apiKey = InfinitePayConfiguracaoRegras.ValidarApiKey(request.ApiKey);
            if (apiKey != null && !_segredos.Configurado) throw PagamentoException.NaoConfigurado("Chave mestra de pagamentos nao configurada.");

            await using var cx = await _db.OpenConnectionAsync(ct);
            await using var tx = await cx.BeginTransactionAsync(ct);
            await cx.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(hashtext('infinitepay_conta_zippy'))", transaction: tx, cancellationToken: ct));

            var conta = await cx.QueryFirstOrDefaultAsync<ContaRow>(new CommandDefinition(
                "SELECT id, infinite_tag_normalizada, habilitada, versao FROM infinitepay_conta_zippy FOR UPDATE", transaction: tx, cancellationToken: ct));
            var anterior = conta == null ? null : new { conta.InfiniteTagNormalizada, conta.Habilitada, conta.Versao };
            if (conta == null)
            {
                conta = new ContaRow { Id = Guid.NewGuid(), InfiniteTagNormalizada = handle, Habilitada = request.Habilitada, Versao = 1 };
                await cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO infinitepay_conta_zippy (id, infinite_tag_normalizada, habilitada, versao, atualizado_por) VALUES (@Id, @Handle, @Habilitada, 1, @U)",
                    new { conta.Id, Handle = handle, conta.Habilitada, U = usuarioId }, tx, cancellationToken: ct));
            }

            var ativa = await cx.QueryFirstOrDefaultAsync<VersaoRow>(new CommandDefinition(
                "SELECT id, infinite_tag_snapshot, api_key_cifrada FROM infinitepay_conta_zippy_versao WHERE id_conta = @C AND ativa FOR UPDATE",
                new { C = conta.Id }, tx, cancellationToken: ct));
            var novaVersao = ativa == null || !string.Equals(ativa.InfiniteTagSnapshot, handle, StringComparison.Ordinal) || apiKey != null;
            var numero = conta.Versao;
            if (novaVersao)
            {
                string? segredo = apiKey;
                if (segredo == null && ativa?.ApiKeyCifrada != null)
                {
                    segredo = _segredos.Revelar(ativa.ApiKeyCifrada, SegredoContexto.InfinitePayZippyApiKey(ativa.Id));
                    if (segredo == null) throw PagamentoException.NaoConfigurado("Nao foi possivel abrir a API Key InfinitePay atual.");
                }
                if (ativa != null)
                {
                    await cx.ExecuteAsync(new CommandDefinition("UPDATE infinitepay_conta_zippy_versao SET ativa = FALSE, rotacionada_em = NOW() WHERE id = @Id",
                        new { ativa.Id }, tx, cancellationToken: ct));
                    numero = conta.Versao + 1;
                }
                var versaoId = Guid.NewGuid();
                var cifrada = segredo == null ? null : _segredos.Proteger(segredo, SegredoContexto.InfinitePayZippyApiKey(versaoId));
                await cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO infinitepay_conta_zippy_versao (id, id_conta, versao, infinite_tag_snapshot, api_key_cifrada, criada_por)
VALUES (@Id, @C, @V, @Handle, @Key, @U)", new { Id = versaoId, C = conta.Id, V = numero, Handle = handle, Key = cifrada, U = usuarioId }, tx, cancellationToken: ct));
            }

            await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE infinitepay_conta_zippy SET infinite_tag_normalizada = @Handle, habilitada = @Habilitada, versao = @V, atualizado_por = @U, updated_at = NOW() WHERE id = @Id",
                new { Handle = handle, request.Habilitada, V = numero, U = usuarioId, conta.Id }, tx, cancellationToken: ct));
            await PagamentoAuditoria.RegistrarAsync(cx, tx, null, "infinitepay_conta_zippy", conta.Id.ToString(), "configuracao_alterada", "usuario", usuarioId,
                new { antes = anterior, depois = new { infiniteTag = handle, request.Habilitada, versao = numero, temApiKey = apiKey != null || ativa?.ApiKeyCifrada != null } }, ct);
            await tx.CommitAsync(ct);
            return await LerAsync(cx, null, ct);
        }

        private static async Task<InfinitePayZippyConfiguracaoDto> LerAsync(NpgsqlConnection cx, NpgsqlTransaction? tx, CancellationToken ct)
        {
            var row = await cx.QueryFirstOrDefaultAsync<EstadoRow>(new CommandDefinition(@"
SELECT c.infinite_tag_normalizada, c.habilitada, c.versao, c.updated_at, (v.api_key_cifrada IS NOT NULL) AS tem_api_key
  FROM infinitepay_conta_zippy c LEFT JOIN infinitepay_conta_zippy_versao v ON v.id_conta = c.id AND v.ativa", transaction: tx, cancellationToken: ct));
            return row == null
                ? new InfinitePayZippyConfiguracaoDto()
                : new InfinitePayZippyConfiguracaoDto
                {
                    Configurada = true, InfiniteTag = row.InfiniteTagNormalizada, Habilitada = row.Habilitada, TemApiKey = row.TemApiKey, Versao = row.Versao,
                    AtualizadoEm = row.UpdatedAt
                };
        }

        private sealed class ContaRow
        {
            public Guid Id { get; set; }
            public string InfiniteTagNormalizada { get; set; } = string.Empty;
            public bool Habilitada { get; set; }
            public int Versao { get; set; }
        }

        private sealed class VersaoRow
        {
            public Guid Id { get; set; }
            public string InfiniteTagSnapshot { get; set; } = string.Empty;
            public string? ApiKeyCifrada { get; set; }
        }

        private sealed class EstadoRow
        {
            public string InfiniteTagNormalizada { get; set; } = string.Empty;
            public bool Habilitada { get; set; }
            public int Versao { get; set; }
            public DateTime? UpdatedAt { get; set; }
            public bool TemApiKey { get; set; }
        }
    }
}
