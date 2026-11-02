using System;
using System.Collections.Generic;
using System.Linq;

namespace APIBack.IdentidadeWhatsapp
{
    /// <summary>Estados do vinculo entre o WhatsApp de uma pessoa da equipe e o usuario interno dela.</summary>
    public static class EstadoVinculoWhatsapp
    {
        public const string NaoVerificado = "nao_verificado";
        /// <summary>Codigo enviado ao numero, aguardando a pessoa digitar no painel.</summary>
        public const string Pendente = "pendente";
        /// <summary>Posse do numero provada. Unico estado que libera comando interno.</summary>
        public const string Verificado = "verificado";
        /// <summary>Tentativas esgotadas: bloqueado ate <see cref="VinculoWhatsapp.BloqueadoAte"/>.</summary>
        public const string Suspenso = "suspenso";
        /// <summary>Revogado (manual, telefone alterado, conta inativa ou acesso removido). Recomeca do zero.</summary>
        public const string Revogado = "revogado";
    }

    /// <summary>Motivos gravados na revogacao e na auditoria.</summary>
    public static class MotivoRevogacaoWhatsapp
    {
        public const string Manual = "manual";
        public const string TelefoneAlterado = "telefone_alterado";
        public const string ContaInativa = "conta_inativa";
        public const string AcessoRemovido = "acesso_removido";
        /// <summary>O numero passou a ser de outra pessoa da equipe.</summary>
        public const string TelefoneReaproveitado = "telefone_reaproveitado";
    }

    /// <summary>Linha de usuario_whatsapp_vinculo. O codigo nunca fica em claro: so o HMAC, apagado ao confirmar.</summary>
    public sealed class VinculoWhatsapp
    {
        public Guid Id { get; set; }
        public int UsuarioId { get; set; }
        /// <summary>Telefone (E.164) que recebeu o codigo. Diferente do telefone atual do usuario = vinculo morto.</summary>
        public string? TelefoneE164 { get; set; }
        public string Estado { get; set; } = EstadoVinculoWhatsapp.NaoVerificado;
        public byte[]? CodigoHash { get; set; }
        public DateTime? CodigoExpiraEm { get; set; }
        public int Tentativas { get; set; }
        public int EnviosJanela { get; set; }
        public DateTime? JanelaEnvioInicio { get; set; }
        public DateTime? UltimoEnvioEm { get; set; }
        public DateTime? BloqueadoAte { get; set; }
        public DateTime? VerificadoEm { get; set; }
        public DateTime? RevogadoEm { get; set; }
        public string? Motivo { get; set; }
        /// <summary>Concorrencia otimista: toda gravacao exige a versao lida. 0 = ainda nao existe no banco.</summary>
        public long Versao { get; set; }
    }

    /// <summary>O minimo do usuario que a identidade precisa: telefone atual e se a conta esta ativa.</summary>
    public sealed record UsuarioIdentidade(int Id, string? TelefoneE164, bool Ativo);

    /// <summary>Saida das rotas /whatsapp. Nunca leva codigo nem hash.</summary>
    public sealed record VinculoWhatsappView(
        int UsuarioId,
        string? Telefone,
        string Estado,
        DateTime? VerificadoEm,
        DateTime? CodigoExpiraEm,
        int? TentativasRestantes,
        DateTime? PodeReenviarEm,
        DateTime? RevogadoEm,
        string? Motivo);

    /// <summary>Linha de usuario_whatsapp_auditoria. So os 4 ultimos digitos do telefone; nunca codigo, hash ou token.</summary>
    public sealed record AuditoriaWhatsapp(int UsuarioId, Guid? VinculoId, string Evento, int? AtorUsuarioId, string? TelefoneFinal, string? Detalhe);

    public static class EventoAuditoriaWhatsapp
    {
        public const string CodigoEnviado = "codigo_enviado";
        public const string EnvioFalhou = "envio_falhou";
        public const string EnvioNegado = "envio_negado";
        public const string Confirmado = "confirmado";
        public const string ConfirmacaoFalhou = "confirmacao_falhou";
        public const string Suspenso = "suspenso";
        public const string Revogado = "revogado";
    }

    /// <summary>Para onde vai uma mensagem recebida, segundo quem a mandou.</summary>
    public static class DestinoRemetente
    {
        /// <summary>Funcionario com vinculo verificado: assistente interno, limitado as permissoes reais dele.</summary>
        public const string AssistenteInterno = "assistente_interno";
        /// <summary>Verificado e tambem contato comercial: perguntar o papel antes; nada interno ate a escolha.</summary>
        public const string ConfirmarPapel = "confirmar_papel";
        /// <summary>Contato/cliente do CRM, ou funcionario no canal de simulacao: atendimento externo, sem dado interno.</summary>
        public const string AtendimentoPublico = "atendimento_publico";
        /// <summary>Numero desconhecido: qualificacao, pode virar lead.</summary>
        public const string Qualificacao = "qualificacao";
    }

    /// <param name="UsuarioId">So preenchido em AssistenteInterno e ConfirmarPapel. Na simulacao fica nulo de proposito.</param>
    /// <param name="Simulacao">Funcionario verificado pediu o canal simulador: tratado como externo, sem identidade interna.</param>
    /// <param name="InternoNaoVerificado">O telefone e de alguem da equipe que ainda nao provou a posse. Nao concede nada.</param>
    public sealed record RemetenteResolvido(string Destino, int? UsuarioId, bool Simulacao, bool ContatoCrm, bool InternoNaoVerificado);

    /// <summary>Um vinculo real do usuario (empresa ou loja) com as permissoes efetivas dele. Super admin nunca aparece aqui.</summary>
    public sealed record EscopoAutorizado(Guid EmpresaId, Guid? EstabelecimentoId, string TipoAcesso, IReadOnlyDictionary<string, List<string>> Permissoes);

    /// <summary>
    /// Quem mandou a mensagem, provado pelo vinculo verificado. Vale para esta mensagem: e recalculada a cada uma, entao revogar,
    /// trocar o telefone ou inativar a conta corta na hora. <paramref name="Versao"/> serve para sessoes que guardem a autorizacao
    /// conferirem se o vinculo ainda e o mesmo.
    /// </summary>
    public sealed record AutorizacaoWhatsapp(int UsuarioId, Guid VinculoId, long Versao, IReadOnlyList<EscopoAutorizado> Escopos)
    {
        /// <summary>Permissao real na loja (vinculo da loja) ou na empresa dela (vinculo de empresa).</summary>
        public bool TemPermissao(Guid empresaId, Guid? estabelecimentoId, string modulo, string acao) =>
            Escopos.Any(e =>
                e.EmpresaId == empresaId
                && (e.EstabelecimentoId == null || e.EstabelecimentoId == estabelecimentoId)
                && e.Permissoes.TryGetValue(modulo, out var acoes)
                && acoes.Any(a => string.Equals(a, acao, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Erro de dominio com status HTTP e codigo estavel para o painel.</summary>
    public sealed class IdentidadeWhatsappException : Exception
    {
        public IdentidadeWhatsappException(int status, string codigo, string message, DateTime? tenteNovamenteEm = null) : base(message)
        {
            Status = status;
            Codigo = codigo;
            TenteNovamenteEm = tenteNovamenteEm;
        }

        public int Status { get; }
        public string Codigo { get; }
        public DateTime? TenteNovamenteEm { get; }
    }
}
