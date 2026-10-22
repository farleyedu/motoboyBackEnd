using System;
using System.Collections.Generic;

namespace APIBack.Atendimento
{
    /// <summary>Servico do catalogo (o que o cliente final pode fazer). Os modulos exigidos sao valores de modulo_enum.</summary>
    public sealed record ServicoCatalogoItem(
        string Codigo, string Nome, string? Descricao, IReadOnlyList<string> ModulosExigidos, int Ordem, bool Ativo);

    /// <summary>Servico que um tipo de estabelecimento permite e se ele nasce ligado.</summary>
    public sealed record ServicoDoTipo(string Codigo, bool Padrao);

    /// <summary>Servico ligado (ou desligado) numa loja.</summary>
    public sealed class ServicoDaLoja
    {
        public string Codigo { get; set; } = string.Empty;
        public bool Ativo { get; set; }
        public string ConfigJson { get; set; } = "{}";
    }

    public static class ModoAtendimento
    {
        public const string Bot = "bot";
        public const string Humano = "humano";
        public const string Hibrido = "hibrido";

        public static readonly IReadOnlyList<string> Todos = new[] { Bot, Humano, Hibrido };

        public static bool Valido(string? modo) => modo != null && ((IList<string>)Todos).Contains(modo);
    }

    public static class StatusCanal
    {
        public const string Configurando = "configurando";
        public const string Ativo = "ativo";
        public const string Erro = "erro";
        public const string Inativo = "inativo";
    }

    /// <summary>De quem e a conta Meta do numero: define quem guarda o token e por onde chegam as mensagens.</summary>
    public static class OrigemCanal
    {
        /// <summary>Numero criado na conta Meta da Zippy: usa o token global e o webhook global (/wa/webhook).</summary>
        public const string Zippy = "zippy";
        /// <summary>O cliente usa o Meta dele: token, app e webhook proprios (/wa/webhook/{chave}).</summary>
        public const string Proprio = "proprio";
        /// <summary>Numero compartilhado de testes (so a Gestao mexe).</summary>
        public const string Central = "central";

        public static bool Valida(string? origem) => origem is Zippy or Proprio or Central;
    }

    /// <summary>
    /// Situacao do webhook do numero proprio. nao_aplicavel (zippy e central) -> pendente (aguardando registro na Meta) ->
    /// registrado (a Meta aceitou a URL) -> recebendo (chegou uma mensagem de verdade). erro: tentou registrar de novo e falhou.
    /// </summary>
    public static class WebhookEstado
    {
        public const string NaoAplicavel = "nao_aplicavel";
        public const string Pendente = "pendente";
        public const string Registrado = "registrado";
        public const string Recebendo = "recebendo";
        public const string Erro = "erro";

        /// <summary>Um evento de verdade chegou: a conexao funciona, qualquer que tenha sido o caminho do registro.</summary>
        public static string AoReceber(string origem, string estado) =>
            origem == OrigemCanal.Proprio && estado != NaoAplicavel ? Recebendo : estado;

        /// <summary>A Meta aceitou a URL (registro automatico ou a validacao do registro manual). Nunca desfaz "recebendo".</summary>
        public static string AoRegistrar(string estado) => estado is Pendente or Erro ? Registrado : estado;

        /// <summary>O registro automatico falhou: na conexao fica "pendente" (registro manual); ao tentar de novo, "erro".</summary>
        public static string AoFalharRegistro(string estado, bool primeiraTentativa) =>
            estado == Recebendo ? estado : primeiraTentativa ? Pendente : Erro;
    }

    /// <summary>Numero de WhatsApp de uma loja. O token nunca sai deste objeto em texto puro: so se sabe se existe.</summary>
    public sealed class CanalWhatsapp
    {
        public Guid Id { get; set; }
        public Guid IdEstabelecimento { get; set; }
        /// <summary>ID do numero na Meta (WhatsApp Business Manager), usado para enviar. Nao e o telefone.</summary>
        public string PhoneNumberId { get; set; } = string.Empty;
        /// <summary>Telefone que o cliente enxerga, normalizado: +DDI DDD numero.</summary>
        public string NumeroE164 { get; set; } = string.Empty;
        public string? Nome { get; set; }
        public string? TokenCifrado { get; set; }
        /// <summary>Numero central de testes: todas as lojas de teste usam este numero; recebe quem esta vinculado a ele agora.</summary>
        public bool Central { get; set; }
        public bool TemToken => !string.IsNullOrWhiteSpace(TokenCifrado);
        public string Status { get; set; } = StatusCanal.Configurando;
        public string ModoAtendimento { get; set; } = Atendimento.ModoAtendimento.Hibrido;
        public string ConfigJson { get; set; } = "{}";
        public DateTime? VerificadoEm { get; set; }
        public DateTime? UltimoRecebimentoEm { get; set; }
        public DateTime? UltimoEnvioOkEm { get; set; }
        public string? UltimoErro { get; set; }
        public DateTime? UltimoErroEm { get; set; }
        public List<string> Servicos { get; set; } = new();
        public string Origem { get; set; } = OrigemCanal.Zippy;
        /// <summary>Conta do WhatsApp Business (WABA) na Meta. So no numero proprio.</summary>
        public string? WabaId { get; set; }
        public string? AppId { get; set; }
        /// <summary>App secret do cliente, cifrado: assina os webhooks do numero proprio. Nunca sai em resposta.</summary>
        public string? AppSecretCifrado { get; set; }
        /// <summary>Parte secreta da URL do webhook deste canal (/wa/webhook/{chave}).</summary>
        public string? WebhookChave { get; set; }
        public string? VerifyTokenCifrado { get; set; }
        public string WebhookEstado { get; set; } = Atendimento.WebhookEstado.NaoAplicavel;
        public string? WebhookErro { get; set; }
        public DateTime? WebhookRegistradoEm { get; set; }
    }

    /// <summary>Quem esta pedindo a operacao. "Gestao" e o super admin: so ele cria canais e liga servicos.</summary>
    public sealed record AtendimentoAtor(int UsuarioId, bool Gestao, Guid? EstabelecimentoId, Func<string, string, bool> TemPermissao);

    /// <summary>Violacao de unicidade: o numero (ou o ID da Meta) ja pertence a outra loja.</summary>
    public sealed class CanalConflitoException : Exception
    {
        public CanalConflitoException(string campo, string message) : base(message) => Campo = campo;
        public string Campo { get; }
    }
}
