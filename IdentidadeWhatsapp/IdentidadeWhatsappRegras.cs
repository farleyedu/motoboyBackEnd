using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace APIBack.IdentidadeWhatsapp
{
    public enum ResultadoConfirmacao
    {
        Confirmado,
        /// <summary>Codigo errado: conta tentativa.</summary>
        Invalido,
        /// <summary>Nada a confirmar: sem codigo pendente, expirado, telefone mudou ou conta inativa. Mesma resposta que Invalido para fora.</summary>
        SemCodigo,
        Bloqueado
    }

    /// <summary>Regras puras da identidade por WhatsApp (sem banco, sem relogio proprio): testadas direto em IdentidadeWhatsappTests.</summary>
    public static class IdentidadeWhatsappRegras
    {
        public const int DigitosCodigo = 6;
        public const int MaxTentativas = 5;
        public const int MaxEnviosPorHora = 5;
        public static readonly TimeSpan ValidadeCodigo = TimeSpan.FromMinutes(5);
        public static readonly TimeSpan IntervaloReenvio = TimeSpan.FromSeconds(60);
        public static readonly TimeSpan JanelaEnvios = TimeSpan.FromHours(1);
        public static readonly TimeSpan Bloqueio = TimeSpan.FromMinutes(15);

        public static string GerarCodigo() =>
            RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D" + DigitosCodigo);

        /// <summary>Chave do HMAC derivada do segredo do servidor; o segredo em si nao e usado direto.</summary>
        public static byte[] DerivarChave(string segredo) =>
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(segredo), Encoding.UTF8.GetBytes("zippy.identidade-whatsapp.v1"));

        /// <summary>HMAC preso ao vinculo e ao telefone: o mesmo codigo nao vale para outro usuario nem outro numero.</summary>
        public static byte[] HashCodigo(byte[] chave, Guid vinculoId, string telefoneE164, string codigo) =>
            HMACSHA256.HashData(chave, Encoding.UTF8.GetBytes($"{vinculoId:N}|{telefoneE164}|{codigo}"));

        public static string? NormalizarCodigo(string? codigo)
        {
            var digitos = new string((codigo ?? string.Empty).Where(char.IsDigit).ToArray());
            return digitos.Length == DigitosCodigo ? digitos : null;
        }

        /// <summary>
        /// Estado que vale agora, nao o gravado: verificado/pendente com telefone diferente do atual ou conta inativa ja e revogado,
        /// codigo vencido volta a nao verificado e suspensao vencida tambem.
        /// </summary>
        public static string EstadoEfetivo(VinculoWhatsapp? v, UsuarioIdentidade usuario, DateTime agora)
        {
            if (v == null) return EstadoVinculoWhatsapp.NaoVerificado;
            if (v.Estado == EstadoVinculoWhatsapp.Revogado) return EstadoVinculoWhatsapp.Revogado;
            if (v.Estado == EstadoVinculoWhatsapp.NaoVerificado) return EstadoVinculoWhatsapp.NaoVerificado;
            if (!usuario.Ativo) return EstadoVinculoWhatsapp.Revogado;
            if (!MesmoTelefone(v.TelefoneE164, usuario.TelefoneE164)) return EstadoVinculoWhatsapp.Revogado;
            if (v.Estado == EstadoVinculoWhatsapp.Pendente && (v.CodigoExpiraEm == null || v.CodigoExpiraEm <= agora)) return EstadoVinculoWhatsapp.NaoVerificado;
            if (v.Estado == EstadoVinculoWhatsapp.Suspenso && (v.BloqueadoAte == null || v.BloqueadoAte <= agora)) return EstadoVinculoWhatsapp.NaoVerificado;
            return v.Estado;
        }

        /// <summary>Motivo da revogacao implicita (telefone ou conta), ou nulo se o gravado ainda vale.</summary>
        public static string? MotivoRevogacaoImplicita(VinculoWhatsapp v, UsuarioIdentidade usuario)
        {
            if (v.Estado is EstadoVinculoWhatsapp.Revogado or EstadoVinculoWhatsapp.NaoVerificado) return null;
            if (!usuario.Ativo) return MotivoRevogacaoWhatsapp.ContaInativa;
            return MesmoTelefone(v.TelefoneE164, usuario.TelefoneE164) ? null : MotivoRevogacaoWhatsapp.TelefoneAlterado;
        }

        /// <summary>Unico ponto que libera comando interno: vinculo verificado, conta ativa e mensagem vinda do telefone atual do usuario.</summary>
        public static bool Autoriza(VinculoWhatsapp? v, UsuarioIdentidade? usuario, string? telefoneRemetenteE164, DateTime agora) =>
            v != null && usuario != null && v.UsuarioId == usuario.Id
            && EstadoEfetivo(v, usuario, agora) == EstadoVinculoWhatsapp.Verificado
            && MesmoTelefone(telefoneRemetenteE164, usuario.TelefoneE164);

        /// <summary>Por que nao pode mandar outro codigo agora, e quando pode. Nulo = pode.</summary>
        public static (string Codigo, DateTime TenteEm)? BloqueioEnvio(VinculoWhatsapp? v, DateTime agora)
        {
            if (v == null) return null;
            if (v.Estado == EstadoVinculoWhatsapp.Suspenso && v.BloqueadoAte > agora) return ("bloqueado", v.BloqueadoAte.Value);
            if (v.UltimoEnvioEm.HasValue && v.UltimoEnvioEm.Value + IntervaloReenvio > agora) return ("aguarde_reenvio", v.UltimoEnvioEm.Value + IntervaloReenvio);
            if (v.JanelaEnvioInicio.HasValue && v.JanelaEnvioInicio.Value + JanelaEnvios > agora && v.EnviosJanela >= MaxEnviosPorHora)
                return ("limite_envios", v.JanelaEnvioInicio.Value + JanelaEnvios);
            return null;
        }

        /// <summary>Conta o envio na janela de 1 h (abre janela nova quando a anterior venceu).</summary>
        public static void ContarEnvio(VinculoWhatsapp v, DateTime agora)
        {
            if (!v.JanelaEnvioInicio.HasValue || v.JanelaEnvioInicio.Value + JanelaEnvios <= agora)
            {
                v.JanelaEnvioInicio = agora;
                v.EnviosJanela = 0;
            }
            v.EnviosJanela++;
            v.UltimoEnvioEm = agora;
        }

        public static ResultadoConfirmacao AvaliarConfirmacao(VinculoWhatsapp? v, UsuarioIdentidade usuario, string? codigo, byte[] chave, DateTime agora)
        {
            if (v == null) return ResultadoConfirmacao.SemCodigo;
            if (v.Estado == EstadoVinculoWhatsapp.Suspenso && v.BloqueadoAte > agora) return ResultadoConfirmacao.Bloqueado;
            if (EstadoEfetivo(v, usuario, agora) != EstadoVinculoWhatsapp.Pendente || v.CodigoHash == null || v.TelefoneE164 == null)
                return ResultadoConfirmacao.SemCodigo;

            var normalizado = NormalizarCodigo(codigo);
            if (normalizado == null) return ResultadoConfirmacao.Invalido;
            var esperado = HashCodigo(chave, v.Id, v.TelefoneE164, normalizado);
            return CryptographicOperations.FixedTimeEquals(esperado, v.CodigoHash) ? ResultadoConfirmacao.Confirmado : ResultadoConfirmacao.Invalido;
        }

        /// <summary>
        /// Precedencia do remetente. So o vinculo verificado da acesso interno; se o mesmo numero tambem e contato comercial, pergunta o papel
        /// (nunca decide em silencio). No canal simulador a identidade interna some: o funcionario e atendido como externo.
        /// </summary>
        public static RemetenteResolvido Resolver(int? usuarioVerificadoId, bool internoNaoVerificado, bool contatoCrm, bool simulacaoExterna)
        {
            if (usuarioVerificadoId.HasValue && simulacaoExterna)
                return new RemetenteResolvido(contatoCrm ? DestinoRemetente.AtendimentoPublico : DestinoRemetente.Qualificacao, null, true, contatoCrm, false);
            if (usuarioVerificadoId.HasValue)
                return new RemetenteResolvido(contatoCrm ? DestinoRemetente.ConfirmarPapel : DestinoRemetente.AssistenteInterno, usuarioVerificadoId, false, contatoCrm, false);
            return new RemetenteResolvido(contatoCrm ? DestinoRemetente.AtendimentoPublico : DestinoRemetente.Qualificacao, null, false, contatoCrm, internoNaoVerificado);
        }

        public static VinculoWhatsappView Ver(int usuarioId, VinculoWhatsapp? v, UsuarioIdentidade usuario, DateTime agora)
        {
            var estado = EstadoEfetivo(v, usuario, agora);
            var pendente = estado == EstadoVinculoWhatsapp.Pendente;
            var bloqueio = BloqueioEnvio(v, agora);
            var revogado = estado == EstadoVinculoWhatsapp.Revogado;
            return new VinculoWhatsappView(
                usuarioId,
                usuario.TelefoneE164,
                estado,
                estado == EstadoVinculoWhatsapp.Verificado ? v!.VerificadoEm : null,
                pendente ? v!.CodigoExpiraEm : null,
                pendente ? Math.Max(0, MaxTentativas - v!.Tentativas) : null,
                bloqueio?.TenteEm,
                revogado ? v!.RevogadoEm : null,
                revogado ? (v!.Estado == EstadoVinculoWhatsapp.Revogado ? v.Motivo : MotivoRevogacaoImplicita(v, usuario)) : null);
        }

        /// <summary>Para a auditoria: so os 4 ultimos digitos.</summary>
        public static string? FinalTelefone(string? telefoneE164) =>
            string.IsNullOrEmpty(telefoneE164) || telefoneE164.Length < 4 ? null : telefoneE164[^4..];

        private static bool MesmoTelefone(string? a, string? b) =>
            !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.Ordinal);
    }
}
