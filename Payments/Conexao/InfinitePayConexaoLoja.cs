using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Payments.MercadoPago;
using APIBack.Payments.Provedores;
using Dapper;
using Npgsql;

namespace APIBack.Payments.Conexao
{
    public sealed class InfinitePayConfiguracaoRequest
    {
        public string InfiniteTag { get; set; } = string.Empty;
        /// <summary>Opcional. Ausente mantem a chave atual; presente cria nova versao cifrada.</summary>
        public string? ApiKey { get; set; }
        public bool Habilitada { get; set; }
    }

    public sealed class InfinitePayConfiguracaoDto
    {
        public string Status { get; set; } = StatusConfiguracaoProvedor.NaoConfigurado;
        public string? InfiniteTag { get; set; }
        public bool Habilitada { get; set; }
        public bool TemApiKey { get; set; }
        public int Versao { get; set; }
        public DateTime? RecebimentoValidadoEm { get; set; }
        public string? UltimoErro { get; set; }
        public string Validacao { get; set; } = "nao_validada";
    }

    public static class InfinitePayConfiguracaoRegras
    {
        public static string NormalizarHandle(string? handle)
        {
            var valor = handle?.Trim() ?? string.Empty;
            if (valor.Length is < 1 or > 80 || valor.Contains('$') || valor.Any(char.IsWhiteSpace) || valor.Any(char.IsControl))
                throw PagamentoException.Invalida("InfiniteTag invalida. Informe sem o simbolo $ e sem espacos.", "infiniteTag");
            return valor.ToLowerInvariant();
        }

        public static string? ValidarApiKey(string? apiKey)
        {
            if (apiKey == null) return null;
            var valor = apiKey.Trim();
            if (valor.Length is < 12 or > 512 || !valor.StartsWith("inchk_", StringComparison.Ordinal) || valor.Any(char.IsWhiteSpace))
                throw PagamentoException.Invalida("API Key InfinitePay invalida.", "apiKey");
            return valor;
        }
    }

    public interface IInfinitePayConexaoLojaService
    {
        Task<InfinitePayConfiguracaoDto> EstadoAsync(Guid loja, CancellationToken ct = default);
        Task<InfinitePayConfiguracaoDto> ConfigurarAsync(Guid loja, int usuarioId, InfinitePayConfiguracaoRequest request, CancellationToken ct = default);
    }

    public sealed class InfinitePayConexaoLojaService : IInfinitePayConexaoLojaService
    {
        private readonly NpgsqlDataSource _db;
        private readonly IPagamentoSegredoProtector _segredos;

        public InfinitePayConexaoLojaService(NpgsqlDataSource db, IPagamentoSegredoProtector segredos)
        {
            _db = db;
            _segredos = segredos;
        }

        public async Task<InfinitePayConfiguracaoDto> EstadoAsync(Guid loja, CancellationToken ct = default)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            return await EstadoAsync(cx, null, loja, ct);
        }

        public async Task<InfinitePayConfiguracaoDto> ConfigurarAsync(Guid loja, int usuarioId, InfinitePayConfiguracaoRequest request, CancellationToken ct = default)
        {
            if (request == null) throw PagamentoException.Invalida("Configuracao obrigatoria.");
            var handle = InfinitePayConfiguracaoRegras.NormalizarHandle(request.InfiniteTag);
            var apiKey = InfinitePayConfiguracaoRegras.ValidarApiKey(request.ApiKey);
            if (apiKey != null && !_segredos.Configurado)
                throw PagamentoException.NaoConfigurado("Chave mestra de pagamentos nao configurada.");

            await using var cx = await _db.OpenConnectionAsync(ct);
            await using var tx = await cx.BeginTransactionAsync(ct);
            await cx.ExecuteAsync(new CommandDefinition(
                "SELECT pg_advisory_xact_lock(hashtext('infinitepay_conta_loja:' || @Loja::text))",
                new { Loja = loja }, tx, cancellationToken: ct));

            var conta = await cx.QueryFirstOrDefaultAsync<ContaRow>(new CommandDefinition(@"
SELECT id, id_estabelecimento, infinite_tag_normalizada, habilitada, status, versao, ultimo_erro, recebimento_validado_em
  FROM infinitepay_conta_loja WHERE id_estabelecimento = @Loja FOR UPDATE", new { Loja = loja }, tx, cancellationToken: ct));
            var anterior = conta == null ? null : new { conta.InfiniteTagNormalizada, conta.Habilitada, conta.Status, conta.Versao };

            if (conta == null)
            {
                conta = new ContaRow
                {
                    Id = Guid.NewGuid(), IdEstabelecimento = loja, InfiniteTagNormalizada = handle,
                    Habilitada = request.Habilitada, Status = StatusConfiguracaoProvedor.Configurado, Versao = 1
                };
                await cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO infinitepay_conta_loja (id, id_estabelecimento, infinite_tag_normalizada, habilitada, status, versao, atualizado_por)
VALUES (@Id, @IdEstabelecimento, @InfiniteTagNormalizada, @Habilitada, @Status, @Versao, @Usuario)",
                    new { conta.Id, conta.IdEstabelecimento, conta.InfiniteTagNormalizada, conta.Habilitada, conta.Status, conta.Versao, Usuario = usuarioId }, tx, cancellationToken: ct));
            }

            var ativa = await cx.QueryFirstOrDefaultAsync<VersaoRow>(new CommandDefinition(@"
SELECT id, id_conta, versao, infinite_tag_snapshot, api_key_cifrada, ativa
  FROM infinitepay_conta_versao WHERE id_conta = @Conta AND ativa FOR UPDATE", new { Conta = conta.Id }, tx, cancellationToken: ct));
            var novaVersao = ativa == null || !string.Equals(ativa.InfiniteTagSnapshot, handle, StringComparison.Ordinal) || apiKey != null;

            if (novaVersao)
            {
                string? segredo = apiKey;
                if (segredo == null && ativa?.ApiKeyCifrada != null)
                {
                    segredo = _segredos.Revelar(ativa.ApiKeyCifrada, SegredoContexto.InfinitePayApiKey(ativa.Id));
                    if (segredo == null) throw PagamentoException.NaoConfigurado("Nao foi possivel abrir a API Key InfinitePay atual.");
                }

                if (ativa != null)
                    await cx.ExecuteAsync(new CommandDefinition(
                        "UPDATE infinitepay_conta_versao SET ativa = FALSE, rotacionada_em = NOW() WHERE id = @Id",
                        new { ativa.Id }, tx, cancellationToken: ct));

                var versaoId = Guid.NewGuid();
                var numero = ativa == null ? conta.Versao : conta.Versao + 1;
                var cifrada = segredo == null ? null : _segredos.Proteger(segredo, SegredoContexto.InfinitePayApiKey(versaoId));
                await cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO infinitepay_conta_versao (id, id_conta, id_estabelecimento, versao, infinite_tag_snapshot, api_key_cifrada, criada_por)
VALUES (@Id, @Conta, @Loja, @Versao, @Handle, @ApiKey, @Usuario)",
                    new { Id = versaoId, Conta = conta.Id, Loja = loja, Versao = numero, Handle = handle, ApiKey = cifrada, Usuario = usuarioId }, tx, cancellationToken: ct));
                conta.Versao = numero;
            }

            var configuracaoMudou = novaVersao || !string.Equals(conta.InfiniteTagNormalizada, handle, StringComparison.Ordinal);
            conta.InfiniteTagNormalizada = handle;
            conta.Habilitada = request.Habilitada;
            if (configuracaoMudou || conta.Status == StatusConfiguracaoProvedor.Erro)
                conta.Status = StatusConfiguracaoProvedor.Configurado;
            await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE infinitepay_conta_loja
   SET infinite_tag_normalizada = @Handle, habilitada = @Habilitada, status = @Status, versao = @Versao,
       ultimo_erro = NULL,
       recebimento_validado_em = CASE WHEN @ConfiguracaoMudou THEN NULL ELSE recebimento_validado_em END,
       atualizado_por = @Usuario, updated_at = NOW()
 WHERE id = @Id", new { Handle = handle, conta.Habilitada, conta.Status, conta.Versao, ConfiguracaoMudou = configuracaoMudou, Usuario = usuarioId, conta.Id }, tx, cancellationToken: ct));

            await cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO pagamento_provedor_loja (id, id_estabelecimento, provedor, habilitado, status, versao, atualizado_por)
VALUES (@Id, @Loja, 'infinitepay', @Habilitada, @Status, @Versao, @Usuario)
ON CONFLICT (id_estabelecimento, provedor) DO UPDATE
 SET habilitado = EXCLUDED.habilitado, status = EXCLUDED.status, versao = EXCLUDED.versao,
     ultimo_erro = NULL,
     validado_em = CASE WHEN @ConfiguracaoMudou THEN NULL ELSE pagamento_provedor_loja.validado_em END,
     atualizado_por = EXCLUDED.atualizado_por, updated_at = NOW()",
                new { Id = Guid.NewGuid(), Loja = loja, conta.Habilitada, conta.Status, conta.Versao, ConfiguracaoMudou = configuracaoMudou, Usuario = usuarioId }, tx, cancellationToken: ct));

            await PagamentoAuditoria.RegistrarAsync(cx, tx, loja, "infinitepay_conta_loja", conta.Id.ToString(), "configuracao_alterada", "usuario", usuarioId,
                new { antes = anterior, depois = new { infiniteTag = handle, conta.Habilitada, conta.Status, conta.Versao, temApiKey = apiKey != null || ativa?.ApiKeyCifrada != null } }, ct);
            await tx.CommitAsync(ct);
            return await EstadoAsync(cx, null, loja, ct);
        }

        private static async Task<InfinitePayConfiguracaoDto> EstadoAsync(NpgsqlConnection cx, NpgsqlTransaction? tx, Guid loja, CancellationToken ct)
        {
            var row = await cx.QueryFirstOrDefaultAsync<EstadoRow>(new CommandDefinition(@"
SELECT c.infinite_tag_normalizada, c.habilitada, c.status, c.versao, c.ultimo_erro, c.recebimento_validado_em,
       (v.api_key_cifrada IS NOT NULL) AS tem_api_key
  FROM infinitepay_conta_loja c
  LEFT JOIN infinitepay_conta_versao v ON v.id_conta = c.id AND v.ativa
 WHERE c.id_estabelecimento = @Loja", new { Loja = loja }, tx, cancellationToken: ct));
            if (row == null) return new InfinitePayConfiguracaoDto();
            return new InfinitePayConfiguracaoDto
            {
                Status = row.Status, InfiniteTag = row.InfiniteTagNormalizada, Habilitada = row.Habilitada, TemApiKey = row.TemApiKey,
                Versao = row.Versao, RecebimentoValidadoEm = row.RecebimentoValidadoEm, UltimoErro = row.UltimoErro,
                Validacao = row.Status == StatusConfiguracaoProvedor.RecebimentoValidado ? "recebimento_validado" : "configurado_nao_validado"
            };
        }

        private sealed class ContaRow
        {
            public Guid Id { get; set; }
            public Guid IdEstabelecimento { get; set; }
            public string InfiniteTagNormalizada { get; set; } = string.Empty;
            public bool Habilitada { get; set; }
            public string Status { get; set; } = string.Empty;
            public int Versao { get; set; }
            public string? UltimoErro { get; set; }
            public DateTime? RecebimentoValidadoEm { get; set; }
        }

        private sealed class VersaoRow
        {
            public Guid Id { get; set; }
            public Guid IdConta { get; set; }
            public int Versao { get; set; }
            public string InfiniteTagSnapshot { get; set; } = string.Empty;
            public string? ApiKeyCifrada { get; set; }
            public bool Ativa { get; set; }
        }

        private sealed class EstadoRow
        {
            public string InfiniteTagNormalizada { get; set; } = string.Empty;
            public bool Habilitada { get; set; }
            public string Status { get; set; } = string.Empty;
            public int Versao { get; set; }
            public string? UltimoErro { get; set; }
            public DateTime? RecebimentoValidadoEm { get; set; }
            public bool TemApiKey { get; set; }
        }
    }

    public sealed class PreferenciasProvedorRequest
    {
        public string CheckoutOnline { get; set; } = ProvedoresPagamento.MercadoPago;
        /// <summary>Null desabilita o fluxo Pix-only; InfinitePay nunca e aceito aqui.</summary>
        public string? PixExclusivo { get; set; } = ProvedoresPagamento.MercadoPago;
    }

    public sealed class ProvedorLojaDto
    {
        public string Provedor { get; set; } = string.Empty;
        public bool Habilitado { get; set; }
        public string Status { get; set; } = StatusConfiguracaoProvedor.NaoConfigurado;
        public ProvedorCapacidades Capacidades { get; set; } = new(false, false, false, false, false, false);
    }

    public sealed class PreferenciasProvedorDto
    {
        public int VersaoContrato { get; set; } = 1;
        public string CheckoutOnline { get; set; } = ProvedoresPagamento.MercadoPago;
        public string? PixExclusivo { get; set; } = ProvedoresPagamento.MercadoPago;
        public IReadOnlyList<ProvedorLojaDto> Provedores { get; set; } = Array.Empty<ProvedorLojaDto>();
    }

    public interface IPagamentoProvedorLojaService
    {
        Task<PreferenciasProvedorDto> ObterAsync(Guid loja, CancellationToken ct = default);
        Task<PreferenciasProvedorDto> SalvarAsync(Guid loja, int usuarioId, PreferenciasProvedorRequest request, CancellationToken ct = default);
    }

    public sealed class PagamentoProvedorLojaService : IPagamentoProvedorLojaService, IPagamentoProvedorPreferenciaResolver
    {
        private readonly NpgsqlDataSource _db;

        public PagamentoProvedorLojaService(NpgsqlDataSource db) => _db = db;

        public async Task<PreferenciaProvedorResolvida> ResolverAsync(Guid loja, string fluxo, CancellationToken ct = default)
        {
            ValidarFluxo(fluxo);
            await using var cx = await _db.OpenConnectionAsync(ct);
            var row = await cx.QueryFirstOrDefaultAsync<PreferenciaRow>(new CommandDefinition(@"
SELECT fluxo, habilitado, provedor FROM pagamento_preferencia_provedor
 WHERE id_estabelecimento = @Loja AND fluxo = @Fluxo", new { Loja = loja, Fluxo = fluxo }, cancellationToken: ct));
            return row == null
                ? new PreferenciaProvedorResolvida(fluxo, true, ProvedoresPagamento.MercadoPago, true)
                : new PreferenciaProvedorResolvida(row.Fluxo, row.Habilitado, row.Provedor, false);
        }

        public async Task<PreferenciasProvedorDto> ObterAsync(Guid loja, CancellationToken ct = default)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            return await ObterAsync(cx, null, loja, ct);
        }

        public async Task<PreferenciasProvedorDto> SalvarAsync(Guid loja, int usuarioId, PreferenciasProvedorRequest request, CancellationToken ct = default)
        {
            if (request == null || !ProvedoresPagamento.Valido(request.CheckoutOnline))
                throw PagamentoException.Invalida("Provedor do checkout online invalido.", "checkoutOnline");
            if (request.PixExclusivo != null && request.PixExclusivo != ProvedoresPagamento.MercadoPago)
                throw PagamentoException.Regra(ProvedorPagamentoCodigos.NaoSuportado,
                    "InfinitePay nao oferece checkout Pix-only. Use Mercado Pago ou desabilite essa modalidade.");

            await using var cx = await _db.OpenConnectionAsync(ct);
            await using var tx = await cx.BeginTransactionAsync(ct);
            if (request.CheckoutOnline == ProvedoresPagamento.InfinitePay)
            {
                var pronta = await cx.ExecuteScalarAsync<bool>(new CommandDefinition(@"
SELECT EXISTS (
    SELECT 1 FROM infinitepay_conta_loja c
    JOIN infinitepay_conta_versao v ON v.id_conta = c.id AND v.ativa
    WHERE c.id_estabelecimento = @Loja AND c.habilitada AND c.status IN ('configurado', 'recebimento_validado')
)", new { Loja = loja }, tx, cancellationToken: ct));
                if (!pronta)
                    throw PagamentoException.Regra(ProvedorPagamentoCodigos.ProvedorNaoConfigurado,
                        "Configure e habilite a InfinitePay antes de seleciona-la.");
            }

            await GravarPreferenciaAsync(cx, tx, loja, FluxosPagamento.CheckoutOnline, true, request.CheckoutOnline, usuarioId, ct);
            await GravarPreferenciaAsync(cx, tx, loja, FluxosPagamento.PixExclusivo, request.PixExclusivo != null, request.PixExclusivo, usuarioId, ct);
            await PagamentoAuditoria.RegistrarAsync(cx, tx, loja, "pagamento_preferencia_provedor", loja.ToString(), "preferencia_alterada", "usuario", usuarioId,
                new { request.CheckoutOnline, request.PixExclusivo }, ct);
            await tx.CommitAsync(ct);
            return await ObterAsync(cx, null, loja, ct);
        }

        private static Task GravarPreferenciaAsync(NpgsqlConnection cx, NpgsqlTransaction tx, Guid loja, string fluxo, bool habilitado,
            string? provedor, int usuarioId, CancellationToken ct) => cx.ExecuteAsync(new CommandDefinition(@"
INSERT INTO pagamento_preferencia_provedor (id_estabelecimento, fluxo, habilitado, provedor, atualizado_por)
VALUES (@Loja, @Fluxo, @Habilitado, @Provedor, @Usuario)
ON CONFLICT (id_estabelecimento, fluxo) DO UPDATE
 SET habilitado = EXCLUDED.habilitado, provedor = EXCLUDED.provedor, atualizado_por = EXCLUDED.atualizado_por, updated_at = NOW()",
            new { Loja = loja, Fluxo = fluxo, Habilitado = habilitado, Provedor = provedor, Usuario = usuarioId }, tx, cancellationToken: ct));

        private static async Task<PreferenciasProvedorDto> ObterAsync(NpgsqlConnection cx, NpgsqlTransaction? tx, Guid loja, CancellationToken ct)
        {
            var preferencias = (await cx.QueryAsync<PreferenciaRow>(new CommandDefinition(@"
SELECT fluxo, habilitado, provedor FROM pagamento_preferencia_provedor WHERE id_estabelecimento = @Loja",
                new { Loja = loja }, tx, cancellationToken: ct))).ToDictionary(x => x.Fluxo, StringComparer.Ordinal);
            var estados = (await cx.QueryAsync<ProvedorEstadoRow>(new CommandDefinition(@"
SELECT provedor, habilitado, status FROM pagamento_provedor_loja WHERE id_estabelecimento = @Loja",
                new { Loja = loja }, tx, cancellationToken: ct))).ToDictionary(x => x.Provedor, StringComparer.Ordinal);

            var checkout = Escolha(preferencias, FluxosPagamento.CheckoutOnline, ProvedoresPagamento.MercadoPago);
            var pix = Escolha(preferencias, FluxosPagamento.PixExclusivo, ProvedoresPagamento.MercadoPago);
            estados.TryGetValue(ProvedoresPagamento.InfinitePay, out var ip);
            return new PreferenciasProvedorDto
            {
                CheckoutOnline = checkout ?? ProvedoresPagamento.MercadoPago,
                PixExclusivo = pix,
                Provedores = new[]
                {
                    new ProvedorLojaDto
                    {
                        Provedor = ProvedoresPagamento.MercadoPago, Habilitado = true,
                        Status = StatusConfiguracaoProvedor.Configurado,
                        Capacidades = ProvedorCapacidadesCatalogo.Obter(ProvedoresPagamento.MercadoPago, FluxosPagamento.CheckoutOnline)
                    },
                    new ProvedorLojaDto
                    {
                        Provedor = ProvedoresPagamento.InfinitePay, Habilitado = ip?.Habilitado == true,
                        Status = ip?.Status ?? StatusConfiguracaoProvedor.NaoConfigurado,
                        Capacidades = ProvedorCapacidadesCatalogo.Obter(ProvedoresPagamento.InfinitePay, FluxosPagamento.CheckoutOnline)
                    }
                }
            };
        }

        private static string? Escolha(IReadOnlyDictionary<string, PreferenciaRow> preferencias, string fluxo, string padrao)
        {
            if (!preferencias.TryGetValue(fluxo, out var p)) return padrao;
            return p.Habilitado ? p.Provedor : null;
        }

        private static void ValidarFluxo(string fluxo)
        {
            if (fluxo is not (FluxosPagamento.CheckoutOnline or FluxosPagamento.PixExclusivo))
                throw new ArgumentOutOfRangeException(nameof(fluxo));
        }

        private sealed class PreferenciaRow
        {
            public string Fluxo { get; set; } = string.Empty;
            public bool Habilitado { get; set; }
            public string? Provedor { get; set; }
        }

        private sealed class ProvedorEstadoRow
        {
            public string Provedor { get; set; } = string.Empty;
            public bool Habilitado { get; set; }
            public string Status { get; set; } = string.Empty;
        }
    }
}
