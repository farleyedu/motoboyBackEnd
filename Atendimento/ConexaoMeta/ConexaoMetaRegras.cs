using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using APIBack.Atendimento.Templates;

namespace APIBack.Atendimento.ConexaoMeta
{
    /// <summary>O que fazer com o registro do numero na Cloud API (POST /{phone}/register).</summary>
    public enum DecisaoRegistro
    {
        /// <summary>Coexistencia: o numero ja esta no app WhatsApp Business e NUNCA e registrado/desregistrado pela API.</summary>
        Dispensado,
        JaRegistrado,
        /// <summary>Registrado, mas a Meta limitou o numero (FLAGGED, RESTRICTED...): funciona com restricao.</summary>
        RegistradoComRestricao,
        Bloqueado,
        NaoVerificado,
        Registrar
    }

    /// <summary>Regras puras do fluxo (sem banco, rede ou relogio), para serem testadas sozinhas.</summary>
    public static class ConexaoMetaRegras
    {
        public const int RegistrosPorJanela = 3;
        public static readonly TimeSpan JanelaRegistro = TimeSpan.FromHours(72);
        /// <summary>Uma troca de codigo ou ativacao parada ha mais que isto foi interrompida (queda do servidor): pode ser retomada.</summary>
        public static readonly TimeSpan OperacaoTravada = TimeSpan.FromMinutes(2);

        // ---- state (CSRF) -------------------------------------------------------------------------------------------

        public static string GerarState() =>
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        public static string HashState(string state) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state))).ToLowerInvariant();

        public static bool StateConfere(string hashGuardado, string? state)
        {
            if (string.IsNullOrWhiteSpace(state) || state.Length > 200 || string.IsNullOrWhiteSpace(hashGuardado)) return false;
            return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(HashState(state)), Encoding.ASCII.GetBytes(hashGuardado));
        }

        public static string GerarPin() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

        public static bool PinValido(string? pin) => pin is { Length: 6 } && pin.All(char.IsAsciiDigit);

        /// <summary>Texto vindo do navegador (etapa, mensagem de erro do SDK): curto e sem quebra de linha, so para auditoria e tela.</summary>
        public static string? Curto(string? texto, int max = 200)
        {
            var t = texto?.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (string.IsNullOrEmpty(t)) return null;
            return t.Length > max ? t[..max] : t;
        }

        // ---- autorizacao ---------------------------------------------------------------------------------------------

        public static IReadOnlyList<string> PermissoesFaltando(IEnumerable<string> concedidas) =>
            ConfigEmbeddedSignup.PermissoesExigidas.Except(concedidas, StringComparer.Ordinal).ToList();

        /// <summary>
        /// Qual WABA vale. A fonte e o token (granular_scopes); o ID do navegador so escolhe entre as autorizadas. Sem granular_scopes
        /// (a Meta nem sempre devolve), o ID do navegador e aceito como candidato e o servico confirma consultando a WABA com o token.
        /// </summary>
        /// <returns>A WABA, se confirmada pelo token; <c>Confirmar</c> = ainda precisa ser conferida na Meta.</returns>
        public static (string? Waba, bool Confirmar, WhatsappErro? Erro) EscolherWaba(IReadOnlyList<string> autorizadas, string? doNavegador)
        {
            var dica = Digitos(doNavegador) ? doNavegador!.Trim() : null;
            if (autorizadas.Count > 0)
            {
                if (dica != null) return autorizadas.Contains(dica) ? (dica, false, null) : (null, false, ConexaoMetaErros.WabaNaoAutorizada());
                return autorizadas.Count == 1 ? (autorizadas[0], false, null) : (null, false, ConexaoMetaErros.WabaNaoAutorizada());
            }

            return dica != null
                ? (dica, true, null)
                : (null, false, ConexaoMetaErros.AutorizacaoIncompleta("conta do WhatsApp Business"));
        }

        // ---- numeros -------------------------------------------------------------------------------------------------

        /// <summary>Um numero da WABA visto por esta loja. Numero de outra loja aparece, mas nunca e selecionavel (nem mostra de quem e).</summary>
        public static NumeroAutorizadoView Classificar(NumeroMeta numero, CanalWhatsapp? dono, Guid loja)
        {
            var situacao = dono == null ? SituacaoNumero.Livre : dono.IdEstabelecimento == loja ? SituacaoNumero.DestaLoja : SituacaoNumero.OutraLoja;
            string? motivo = situacao == SituacaoNumero.OutraLoja ? "Este numero ja esta conectado a outra loja." : null;
            var bloqueado = numero.Estado is "BANNED";
            if (bloqueado) motivo = "A Meta bloqueou este numero.";
            return new NumeroAutorizadoView(numero.Id, numero.Telefone, numero.Nome, numero.Estado, numero.VerificacaoCodigo, situacao,
                situacao != SituacaoNumero.OutraLoja && !bloqueado, numero.NoAppBusiness, motivo);
        }

        /// <summary>O numero a ativar sem perguntar: o do canal reconectado, o indicado pelo navegador (se selecionavel) ou o unico selecionavel.</summary>
        public static string? EscolherNumeroAutomatico(IReadOnlyList<NumeroAutorizadoView> numeros, string? doNavegador, string? doCanalReconectado)
        {
            var selecionaveis = numeros.Where(n => n.Selecionavel).ToList();
            if (doCanalReconectado != null) return selecionaveis.Any(n => n.PhoneNumberId == doCanalReconectado) ? doCanalReconectado : null;
            if (doNavegador != null && selecionaveis.Any(n => n.PhoneNumberId == doNavegador)) return doNavegador;
            return selecionaveis.Count == 1 ? selecionaveis[0].PhoneNumberId : null;
        }

        public static DecisaoRegistro DecidirRegistro(NumeroMeta numero, bool coexistencia)
        {
            if (coexistencia || numero.NoAppBusiness == true) return DecisaoRegistro.Dispensado;
            switch (numero.Estado?.ToUpperInvariant())
            {
                case "CONNECTED": return DecisaoRegistro.JaRegistrado;
                case "FLAGGED" or "RESTRICTED" or "RATE_LIMITED" or "MIGRATED": return DecisaoRegistro.RegistradoComRestricao;
                case "BANNED": return DecisaoRegistro.Bloqueado;
            }

            // Verificacao do codigo so importa enquanto o numero nao esta registrado.
            if (numero.VerificacaoCodigo is { } v && !string.Equals(v, "VERIFIED", StringComparison.OrdinalIgnoreCase)) return DecisaoRegistro.NaoVerificado;
            return DecisaoRegistro.Registrar;
        }

        /// <summary>A Meta limita registros por numero: no maximo <see cref="RegistrosPorJanela"/> tentativas do Zippy em 72 horas.</summary>
        public static bool PodeTentarRegistro(int tentativas, DateTime? ultimaTentativa, DateTime agora) =>
            ultimaTentativa == null || agora - ultimaTentativa.Value > JanelaRegistro || tentativas < RegistrosPorJanela;

        public static int ContarTentativa(int tentativas, DateTime? ultimaTentativa, DateTime agora) =>
            ultimaTentativa == null || agora - ultimaTentativa.Value > JanelaRegistro ? 1 : tentativas + 1;

        // ---- estado geral --------------------------------------------------------------------------------------------

        /// <summary>
        /// Um estado so, das etapas reais: revogado (sem autorizacao), erro, atencao, configurando (algo pendente) ou conectado (tudo ok).
        /// Resultado parcial nunca vira "conectado".
        /// </summary>
        public static string EstadoGeral(IReadOnlyList<EtapaConexaoView> etapas, bool revogado = false, bool desconectado = false)
        {
            if (desconectado) return EstadoConexao.Desconectado;
            if (revogado) return EstadoConexao.Revogado;
            if (etapas.Any(e => e.Estado == EstadoEtapa.Erro)) return EstadoConexao.Erro;
            if (etapas.Any(e => e.Estado == EstadoEtapa.Atencao)) return EstadoConexao.AtencaoNecessaria;
            if (etapas.Any(e => e.Estado == EstadoEtapa.Pendente)) return EstadoConexao.Configurando;
            return etapas.Count == 0 ? EstadoConexao.AguardandoAutorizacao : EstadoConexao.Conectado;
        }

        /// <summary>O numero ja envia e recebe pela Cloud API: autorizacao, inscricao do app e registro (ou coexistencia) estao ok.</summary>
        public static bool Operacional(IReadOnlyList<EtapaConexaoView> etapas) =>
            new[] { EtapaConexao.Autorizacao, EtapaConexao.Inscricao, EtapaConexao.Registro }
                .All(c => etapas.FirstOrDefault(e => e.Codigo == c)?.Estado is EstadoEtapa.Ok or EstadoEtapa.NaoAplicavel);

        public static IReadOnlyList<string> Acoes(IReadOnlyList<EtapaConexaoView> etapas) =>
            etapas.Select(e => e.Acao).Where(a => a != null).Select(a => a!).Distinct().ToList();

        // ---- erros da Meta -------------------------------------------------------------------------------------------

        /// <summary>Codigos da Graph API que dizem "o acesso acabou" (token invalido, expirado ou removido pelo cliente).</summary>
        public static bool TokenPerdido(MetaFalha? f) => f is { SemResposta: false, Codigo: 190 };

        public static EtapaConexaoView EtapaDaFalha(string etapa, MetaFalha falha)
        {
            if (falha.SemResposta)
            {
                var indisponivel = WhatsappErros.MetaIndisponivel();
                return new EtapaConexaoView(etapa, EstadoEtapa.Atencao, indisponivel.Mensagem, indisponivel.ComoResolver, ResponsavelPendencia.Meta,
                    "tentar_de_novo", null, indisponivel.Codigo);
            }

            var erro = (falha.Codigo, falha.Subcodigo) switch
            {
                (190, _) => ConexaoMetaErros.TokenRevogado(),
                (10, _) or (200, _) or (294, _) => ConexaoMetaErros.AutorizacaoIncompleta("permissao para esta conta"),
                (133005, _) => ConexaoMetaErros.PinNecessario(),
                (133016, _) or (4, _) or (80007, _) => ConexaoMetaErros.RegistroLimite(),
                (133006, _) or (136025, _) => ConexaoMetaErros.NumeroNaoVerificado(),
                (100, _) when etapa == EtapaConexao.Numero || etapa == EtapaConexao.Registro => ConexaoMetaErros.NumeroNaoAutorizado(),
                (100, _) => WhatsappErros.WabaNaoEncontrada(),
                _ => WhatsappErros.Generico(falha.Http, falha.MensagemUsuario ?? falha.Mensagem)
            };

            var (estado, responsavel, acao) = erro.Codigo switch
            {
                ConexaoMetaCodigos.TokenRevogado => (EstadoEtapa.Erro, ResponsavelPendencia.Loja, "reconectar"),
                ConexaoMetaCodigos.AutorizacaoIncompleta => (EstadoEtapa.Erro, ResponsavelPendencia.Loja, "reconectar"),
                ConexaoMetaCodigos.PinNecessario => (EstadoEtapa.Pendente, ResponsavelPendencia.Loja, "informar_pin"),
                ConexaoMetaCodigos.RegistroLimite => (EstadoEtapa.Atencao, ResponsavelPendencia.Meta, "aguardar"),
                ConexaoMetaCodigos.NumeroNaoVerificado => (EstadoEtapa.Pendente, ResponsavelPendencia.Loja, "tentar_de_novo"),
                _ => (EstadoEtapa.Erro, ResponsavelPendencia.Zippy, "tentar_de_novo")
            };

            return new EtapaConexaoView(etapa, estado, erro.Mensagem, erro.ComoResolver, responsavel, acao, erro.Link, erro.Codigo);
        }

        public static EtapaConexaoView Ok(string etapa, string mensagem) => new(etapa, EstadoEtapa.Ok, mensagem);

        public static EtapaConexaoView DoErro(string etapa, string estado, WhatsappErro erro, string responsavel, string? acao) =>
            new(etapa, estado, erro.Mensagem, erro.ComoResolver, responsavel, acao, erro.Link, erro.Codigo);

        private static bool Digitos(string? v) => !string.IsNullOrWhiteSpace(v) && v.Trim().Length <= 32 && v.Trim().All(char.IsAsciiDigit);
    }
}
