using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Npgsql;

namespace APIBack.IdentidadeWhatsapp
{
    public interface IIdentidadeWhatsappRepository
    {
        Task<UsuarioIdentidade?> ObterUsuarioAsync(int usuarioId);
        /// <summary>Usuarios ativos cujo telefone atual e este.</summary>
        Task<IReadOnlyList<UsuarioIdentidade>> ListarUsuariosPorTelefoneAsync(string telefoneE164);
        Task<VinculoWhatsapp?> ObterVinculoAsync(int usuarioId);
        Task<IReadOnlyList<VinculoWhatsapp>> ListarVinculosAsync(IReadOnlyCollection<int> usuarioIds);
        /// <summary>Vinculo pendente ou verificado deste telefone (no maximo um: indice unico).</summary>
        Task<VinculoWhatsapp?> ObterVinculoAtivoPorTelefoneAsync(string telefoneE164);
        /// <summary>Telefone de oportunidade do CRM (lead ou cliente comercial). Comparacao por digitos, nunca por nome.</summary>
        Task<bool> ExisteContatoCrmAsync(string telefoneE164);
        /// <summary>
        /// Grava se a versao no banco ainda e <paramref name="versaoEsperada"/> (0 = inserir). Devolve false se outra operacao gravou antes.
        /// Telefone ja pendente/verificado em outro usuario vira IdentidadeWhatsappException 409.
        /// </summary>
        Task<bool> SalvarAsync(VinculoWhatsapp vinculo, long versaoEsperada);
        Task AuditarAsync(AuditoriaWhatsapp auditoria);
    }

    public sealed class IdentidadeWhatsappRepository : IIdentidadeWhatsappRepository
    {
        private const string ColunasVinculo = @"
id AS Id, usuario_id AS UsuarioId, telefone_e164 AS TelefoneE164, estado AS Estado, codigo_hash AS CodigoHash,
codigo_expira_em AS CodigoExpiraEm, tentativas AS Tentativas, envios_janela AS EnviosJanela, janela_envio_inicio AS JanelaEnvioInicio,
ultimo_envio_em AS UltimoEnvioEm, bloqueado_ate AS BloqueadoAte, verificado_em AS VerificadoEm, revogado_em AS RevogadoEm,
motivo AS Motivo, versao AS Versao";

        private readonly NpgsqlDataSource _db;

        public IdentidadeWhatsappRepository(NpgsqlDataSource db) => _db = db;

        public async Task<UsuarioIdentidade?> ObterUsuarioAsync(int usuarioId)
        {
            await using var cx = await _db.OpenConnectionAsync();
            return await cx.QueryFirstOrDefaultAsync<UsuarioIdentidade>(
                "SELECT id AS Id, telefone_e164 AS TelefoneE164, (deleted_at IS NULL) AS Ativo FROM usuario WHERE id = @Id",
                new { Id = usuarioId });
        }

        public async Task<IReadOnlyList<UsuarioIdentidade>> ListarUsuariosPorTelefoneAsync(string telefoneE164)
        {
            await using var cx = await _db.OpenConnectionAsync();
            var rows = await cx.QueryAsync<UsuarioIdentidade>(
                "SELECT id AS Id, telefone_e164 AS TelefoneE164, TRUE AS Ativo FROM usuario WHERE telefone_e164 = @Tel AND deleted_at IS NULL",
                new { Tel = telefoneE164 });
            return rows.ToList();
        }

        public async Task<VinculoWhatsapp?> ObterVinculoAsync(int usuarioId)
        {
            await using var cx = await _db.OpenConnectionAsync();
            return await cx.QueryFirstOrDefaultAsync<VinculoWhatsapp>(
                $"SELECT {ColunasVinculo} FROM usuario_whatsapp_vinculo WHERE usuario_id = @Id", new { Id = usuarioId });
        }

        public async Task<IReadOnlyList<VinculoWhatsapp>> ListarVinculosAsync(IReadOnlyCollection<int> usuarioIds)
        {
            if (usuarioIds.Count == 0) return Array.Empty<VinculoWhatsapp>();
            await using var cx = await _db.OpenConnectionAsync();
            var rows = await cx.QueryAsync<VinculoWhatsapp>(
                $"SELECT {ColunasVinculo} FROM usuario_whatsapp_vinculo WHERE usuario_id = ANY(@Ids)", new { Ids = usuarioIds.ToArray() });
            return rows.ToList();
        }

        public async Task<VinculoWhatsapp?> ObterVinculoAtivoPorTelefoneAsync(string telefoneE164)
        {
            await using var cx = await _db.OpenConnectionAsync();
            return await cx.QueryFirstOrDefaultAsync<VinculoWhatsapp>(
                $"SELECT {ColunasVinculo} FROM usuario_whatsapp_vinculo WHERE telefone_e164 = @Tel AND estado IN ('pendente', 'verificado') LIMIT 1",
                new { Tel = telefoneE164 });
        }

        public async Task<bool> ExisteContatoCrmAsync(string telefoneE164)
        {
            var comPais = telefoneE164.TrimStart('+');
            var semPais = comPais.StartsWith("55") ? comPais[2..] : comPais;
            // Celular antigo gravado sem o nono digito ainda e o mesmo numero.
            var semNono = semPais.Length == 11 && semPais[2] == '9' ? semPais[..2] + semPais[3..] : semPais;
            var variantes = new[] { comPais, semPais, "55" + semNono, semNono }.Distinct().ToArray();

            await using var cx = await _db.OpenConnectionAsync();
            if (!await cx.ExecuteScalarAsync<bool>("SELECT to_regclass('crm_oportunidade') IS NOT NULL")) return false;
            return await cx.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM crm_oportunidade WHERE regexp_replace(COALESCE(telefone, ''), '[^0-9]', '', 'g') = ANY(@Variantes))",
                new { Variantes = variantes });
        }

        public async Task<bool> SalvarAsync(VinculoWhatsapp v, long versaoEsperada)
        {
            const string inserir = @"
INSERT INTO usuario_whatsapp_vinculo
    (id, usuario_id, telefone_e164, estado, codigo_hash, codigo_expira_em, tentativas, envios_janela, janela_envio_inicio,
     ultimo_envio_em, bloqueado_ate, verificado_em, revogado_em, motivo, versao, criado_em, atualizado_em)
VALUES
    (@Id, @UsuarioId, @TelefoneE164, @Estado, @CodigoHash, @CodigoExpiraEm, @Tentativas, @EnviosJanela, @JanelaEnvioInicio,
     @UltimoEnvioEm, @BloqueadoAte, @VerificadoEm, @RevogadoEm, @Motivo, 1, NOW(), NOW())
ON CONFLICT (usuario_id) DO NOTHING";
            const string atualizar = @"
UPDATE usuario_whatsapp_vinculo
   SET telefone_e164 = @TelefoneE164, estado = @Estado, codigo_hash = @CodigoHash, codigo_expira_em = @CodigoExpiraEm,
       tentativas = @Tentativas, envios_janela = @EnviosJanela, janela_envio_inicio = @JanelaEnvioInicio, ultimo_envio_em = @UltimoEnvioEm,
       bloqueado_ate = @BloqueadoAte, verificado_em = @VerificadoEm, revogado_em = @RevogadoEm, motivo = @Motivo,
       versao = versao + 1, atualizado_em = NOW()
 WHERE id = @Id AND versao = @VersaoEsperada";

            await using var cx = await _db.OpenConnectionAsync();
            try
            {
                var linhas = await cx.ExecuteAsync(versaoEsperada == 0 ? inserir : atualizar, new
                {
                    v.Id, v.UsuarioId, v.TelefoneE164, v.Estado, v.CodigoHash, v.CodigoExpiraEm, v.Tentativas, v.EnviosJanela,
                    v.JanelaEnvioInicio, v.UltimoEnvioEm, v.BloqueadoAte, v.VerificadoEm, v.RevogadoEm, v.Motivo, VersaoEsperada = versaoEsperada
                });
                if (linhas != 1) return false;
                v.Versao = versaoEsperada + 1;
                return true;
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                throw IdentidadeWhatsappService.TelefoneIndisponivel();
            }
        }

        public async Task AuditarAsync(AuditoriaWhatsapp a)
        {
            await using var cx = await _db.OpenConnectionAsync();
            await cx.ExecuteAsync(@"
INSERT INTO usuario_whatsapp_auditoria (usuario_id, vinculo_id, evento, ator_usuario_id, telefone_final, detalhe, criado_em)
VALUES (@UsuarioId, @VinculoId, @Evento, @AtorUsuarioId, @TelefoneFinal, @Detalhe, NOW())", a);
        }
    }
}
